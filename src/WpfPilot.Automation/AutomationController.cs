using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed class AutomationController : IDisposable
{
    private Application? _application;
    private UIA3Automation? _automation;

    public bool IsAttached => _application is not null && !_application.HasExited;

    public void Dispose() => Cleanup();

    public Task<LaunchAppResponse> LaunchAsync(LaunchAppRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExePath);
        EnsureNotAttached();

        if (Path.IsPathRooted(request.ExePath) && !File.Exists(request.ExePath))
        {
            throw new FileNotFoundException($"Executable not found: '{request.ExePath}'.", request.ExePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExePath,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.Args is not null)
        {
            foreach (var arg in request.Args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        _application = Application.Launch(startInfo);
        _application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(10));
        _application.WaitWhileBusy(TimeSpan.FromSeconds(10));
        _automation = new UIA3Automation();
        _ = FindMainWindow(_application, _automation);

        var response = new LaunchAppResponse(_application.ProcessId, _application.Name);
        return Task.FromResult(response);
    }

    public Task<AttachToAppResponse> AttachAsync(AttachToAppRequest request, CancellationToken cancellationToken = default)
    {
        EnsureNotAttached();

        if (request.Pid is not null && !string.IsNullOrWhiteSpace(request.ProcessName))
        {
            throw new ArgumentException("Provide either pid or processName, not both.");
        }

        if (request.Pid is int pid)
        {
            _application = Application.Attach(pid);
        }
        else if (!string.IsNullOrWhiteSpace(request.ProcessName))
        {
            _application = Application.Attach(request.ProcessName);
        }
        else
        {
            throw new ArgumentException("Either pid or processName must be provided.");
        }

        _application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(10));
        _application.WaitWhileBusy(TimeSpan.FromSeconds(10));
        _automation = new UIA3Automation();
        _ = FindMainWindow(_application, _automation);

        var response = new AttachToAppResponse(_application.ProcessId, _application.Name);
        return Task.FromResult(response);
    }

    public Task<CloseAppResponse> CloseAsync(CloseAppRequest request, CancellationToken cancellationToken = default)
    {
        var timeout = request.TimeoutMs <= 0 ? 5000 : request.TimeoutMs;
        var application = EnsureAttached();

        application.CloseTimeout = TimeSpan.FromMilliseconds(timeout);
        var closedGracefully = application.Close(killIfCloseFails: request.Force);

        if (!closedGracefully && request.Force)
        {
            try
            {
                application.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }

        var closed = application.HasExited;
        Cleanup();
        return Task.FromResult(new CloseAppResponse(closed));
    }

    public Task<ListWindowsResponse> ListWindowsAsync(CancellationToken cancellationToken = default)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(10));

        var windows = application.GetAllTopLevelWindows(automation)
            .Select(ToWindowInfo)
            .ToArray();

        var response = new ListWindowsResponse(application.ProcessId, application.Name, windows);
        return Task.FromResult(response);
    }

    public async Task<TakeScreenshotResponse> TakeScreenshotAsync(
        TakeScreenshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        window.SetForeground();
        window.Focus();
        await Task.Delay(150, cancellationToken);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = request.Locator is null ? window : ResolveElement(window, request.Locator, controlWalker, rawWalker);
        var mode = request.CaptureMode;
        var captureSettings = new CaptureSettings { OutputScale = 1 };

        using var bitmapToSave = request.Locator is null
            ? mode switch
            {
                ScreenshotCaptureMode.Screen => CaptureWindowScreen(window, captureSettings),
                ScreenshotCaptureMode.PrintWindow => CaptureWindowPrintWindow(window),
                ScreenshotCaptureMode.Auto => CaptureWindowAuto(window, captureSettings),
                _ => throw new ArgumentOutOfRangeException(nameof(request), $"Unknown capture mode '{mode}'.")
            }
            : mode switch
            {
                ScreenshotCaptureMode.Screen => CaptureElementScreen(element, captureSettings),
                ScreenshotCaptureMode.PrintWindow => CaptureElementPrintWindow(window, element),
                ScreenshotCaptureMode.Auto => CaptureElementAuto(window, element, captureSettings),
                _ => throw new ArgumentOutOfRangeException(nameof(request), $"Unknown capture mode '{mode}'.")
            };

        using var stream = new MemoryStream();
        bitmapToSave.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        return new TakeScreenshotResponse(Convert.ToBase64String(bytes), bitmapToSave.Width, bitmapToSave.Height);
    }

    private static Bitmap CaptureWindowScreen(Window window, CaptureSettings captureSettings)
    {
        using var capture = Capture.Element(window, captureSettings);
        var bitmap = capture.Bitmap;
        var croppedClientArea = TryCropToClientArea(window, bitmap);
        return croppedClientArea
            ?? bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), bitmap.PixelFormat);
    }

    private static Bitmap CaptureWindowPrintWindow(Window window) =>
        TryCaptureClientAreaWithPrintWindow(window) ?? throw new InvalidOperationException("PrintWindow capture failed.");

    private static Bitmap CaptureWindowAuto(Window window, CaptureSettings captureSettings)
    {
        var printWindowBitmap = TryCaptureClientAreaWithPrintWindow(window);
        if (printWindowBitmap is not null)
        {
            return printWindowBitmap;
        }

        return CaptureWindowScreen(window, captureSettings);
    }

    private static Bitmap CaptureElementScreen(AutomationElement element, CaptureSettings captureSettings)
    {
        using var capture = Capture.Element(element, captureSettings);
        var bitmap = capture.Bitmap;
        return bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), bitmap.PixelFormat);
    }

    private static Bitmap CaptureElementPrintWindow(Window window, AutomationElement element)
    {
        using var clientBitmap = TryCaptureClientAreaWithPrintWindow(window)
            ?? throw new InvalidOperationException("PrintWindow capture failed.");

        return TryCropElementFromClientBitmap(window, element, clientBitmap)
            ?? throw new InvalidOperationException("Failed to crop element from PrintWindow capture.");
    }

    private static Bitmap CaptureElementAuto(Window window, AutomationElement element, CaptureSettings captureSettings)
    {
        using var clientBitmap = TryCaptureClientAreaWithPrintWindow(window);
        if (clientBitmap is not null)
        {
            var cropped = TryCropElementFromClientBitmap(window, element, clientBitmap);
            if (cropped is not null)
                return cropped;
        }

        return CaptureElementScreen(element, captureSettings);
    }

    private static Bitmap? TryCropToClientArea(Window window, Bitmap bitmap)
    {
        if (!TryGetWindowClientCrop(window, out var crop))
        {
            return null;
        }

        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0)
        {
            return null;
        }

        if (crop.Right > bitmap.Width || crop.Bottom > bitmap.Height)
        {
            return null;
        }

        try
        {
            return bitmap.Clone(crop, bitmap.PixelFormat);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryCropElementFromClientBitmap(Window window, AutomationElement element, Bitmap clientBitmap)
    {
        if (!TryGetClientTopLeftScreen(window, out var clientTopLeft))
        {
            return null;
        }

        var bounds = element.BoundingRectangle;
        var x = bounds.Left - clientTopLeft.X;
        var y = bounds.Top - clientTopLeft.Y;
        var width = bounds.Width;
        var height = bounds.Height;

        var crop = Rectangle.Intersect(
            new Rectangle(0, 0, clientBitmap.Width, clientBitmap.Height),
            new Rectangle(x, y, width, height));

        if (crop.Width <= 0 || crop.Height <= 0)
        {
            return null;
        }

        try
        {
            return clientBitmap.Clone(crop, clientBitmap.PixelFormat);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryCaptureClientAreaWithPrintWindow(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!GetClientRect(hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            const uint PW_CLIENTONLY = 0x00000001;
            if (!PrintWindow(hwnd, hdc, PW_CLIENTONLY))
            {
                bitmap.Dispose();
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    private static bool TryGetWindowClientCrop(Window window, out Rectangle crop)
    {
        crop = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var clientRect))
        {
            return false;
        }

        if (!TryGetClientTopLeftScreen(hwnd, out var clientTopLeft))
        {
            return false;
        }

        var x = clientTopLeft.X - windowRect.Left;
        var y = clientTopLeft.Y - windowRect.Top;
        var width = clientRect.Width;
        var height = clientRect.Height;

        crop = new Rectangle(x, y, width, height);
        return true;
    }

    private static bool TryGetClientTopLeftScreen(Window window, out POINT clientTopLeft)
    {
        clientTopLeft = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return TryGetClientTopLeftScreen(hwnd, out clientTopLeft);
    }

    private static bool TryGetClientTopLeftScreen(IntPtr hwnd, out POINT clientTopLeft)
    {
        clientTopLeft = new POINT(0, 0);
        return ClientToScreen(hwnd, ref clientTopLeft);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public Task<GetVisualTreeResponse> GetVisualTreeAsync(
        long? windowHandle = null,
        ElementLocator? root = null,
        int depth = 4,
        CancellationToken cancellationToken = default)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        if (depth <= 0)
        {
            depth = 1;
        }

        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var rootElement = root is null ? window : ResolveElement(window, root, controlWalker, rawWalker);
        var rootXPath = ComputeXPath(window, rootElement, rawWalker);
        var rootNode = BuildVisualTreeNode(rootElement, rootXPath, depth, rawWalker);
        return Task.FromResult(new GetVisualTreeResponse(rootNode));
    }

    public Task<GetElementPropertiesResponse> GetElementPropertiesAsync(
        ElementLocator locator,
        long? windowHandle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, locator, controlWalker, rawWalker);
        var xpath = ComputeXPath(window, element, rawWalker);

        var summary = new ElementSummary(
            ElementType: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            ClassName: GetClassName(element),
            Bounds: ToRect(element.BoundingRectangle),
            IsEnabled: element.IsEnabled,
            IsOffscreen: element.IsOffscreen,
            XPath: xpath);

        var properties = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        PopulateProperties(element, properties);

        var patterns = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        PopulatePatterns(element, patterns);

        var response = new GetElementPropertiesResponse(summary, properties, patterns);
        return Task.FromResult(response);
    }

    private void EnsureNotAttached()
    {
        if (IsAttached)
        {
            throw new InvalidOperationException("An application is already attached. Call close_app first.");
        }
    }

    private Application EnsureAttached() =>
        _application is not null && !_application.HasExited
            ? _application
            : throw new InvalidOperationException("No application is attached. Call launch_app or attach_to_app first.");

    private UIA3Automation EnsureAutomation() =>
        _automation ?? throw new InvalidOperationException("Automation has not been initialized.");

    private static Window FindMainWindow(Application application, UIA3Automation automation)
    {
        var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(10));
        if (window is null)
        {
            throw new InvalidOperationException("Failed to find the main window within the timeout.");
        }

        return window;
    }

    private static Window FindWindowByHandle(Application application, UIA3Automation automation, long nativeWindowHandle)
    {
        var windows = application.GetAllTopLevelWindows(automation);
        var window = windows.FirstOrDefault(w => w.Properties.NativeWindowHandle.Value.ToInt64() == nativeWindowHandle);
        if (window is null)
        {
            throw new InvalidOperationException($"No window found with handle {nativeWindowHandle}.");
        }

        return window;
    }

    private static WindowInfo ToWindowInfo(Window window)
    {
        var bounds = window.BoundingRectangle;
        return new WindowInfo(
            Title: window.Title,
            Handle: window.Properties.NativeWindowHandle.Value.ToInt64(),
            Bounds: new Rect(
                X: bounds.Left,
                Y: bounds.Top,
                Width: bounds.Width,
                Height: bounds.Height),
            IsVisible: !window.IsOffscreen,
            IsEnabled: window.IsEnabled);
    }

    private static AutomationElement ResolveElement(Window window, ElementLocator locator, ITreeWalker controlWalker, ITreeWalker rawWalker)
    {
        if (locator is null)
        {
            throw new ArgumentNullException(nameof(locator));
        }

        var strategies = new List<Func<AutomationElement?>>()
        {
            () => TryResolveByAutomationId(window, locator, controlWalker),
            () => TryResolveByName(window, locator, controlWalker),
            () => TryResolveByClassName(window, locator, controlWalker),
            () => TryResolveByXPath(window, locator, rawWalker),
            () => TryResolveByIndexOnly(window, locator, controlWalker),
        };

        foreach (var strategy in strategies)
        {
            var resolved = strategy();
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw new InvalidOperationException("Locator did not match any element.");
    }

    private static AutomationElement? TryResolveByAutomationId(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetAutomationId(e), locator.AutomationId, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, "automationId");
    }

    private static AutomationElement? TryResolveByName(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.Name))
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetName(e), locator.Name, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, "name");
    }

    private static AutomationElement? TryResolveByClassName(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.ClassName))
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetClassName(e), locator.ClassName, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, "className");
    }

    private static AutomationElement? TryResolveByXPath(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.XPath))
        {
            return null;
        }

        var xpath = locator.XPath.Trim();
        if (xpath.Length == 0)
        {
            return null;
        }

        var segments = xpath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseXPathSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("XPath must contain at least one segment.", nameof(locator));
        }

        AutomationElement current = window;
        var rootLabel = GetXPathLabel(current);
        if (string.Equals(segments[0].TypeName, rootLabel, StringComparison.OrdinalIgnoreCase))
        {
            segments = segments.Skip(1).ToArray();
        }

        foreach (var segment in segments)
        {
            var children = GetChildren(current, walker);
            var matches = children
                .Where(c => string.Equals(GetXPathLabel(c), segment.TypeName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"XPath segment not found: '{segment.TypeName}'.");
            }

            if (segment.OneBasedIndex is int oneBased)
            {
                if (oneBased <= 0 || oneBased > matches.Length)
                {
                    throw new InvalidOperationException($"XPath index [{oneBased}] is out of range for segment '{segment.TypeName}' (found {matches.Length}).");
                }

                current = matches[oneBased - 1];
            }
            else
            {
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"XPath segment '{segment.TypeName}' is ambiguous (found {matches.Length}). Add an index like '{segment.TypeName}[n]'.");
                }

                current = matches[0];
            }
        }

        return current;
    }

    private static AutomationElement? TryResolveByIndexOnly(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (locator.Index is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(locator.AutomationId) ||
            !string.IsNullOrWhiteSpace(locator.Name) ||
            !string.IsNullOrWhiteSpace(locator.ClassName) ||
            !string.IsNullOrWhiteSpace(locator.XPath))
        {
            return null;
        }

        var index = locator.Index.Value;
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
        }

        var descendants = EnumerateSelfAndDescendantsDepthFirst(window, walker).Skip(1).ToArray();
        if (index >= descendants.Length)
        {
            throw new InvalidOperationException($"index {index} is out of range (found {descendants.Length} descendants).");
        }

        return descendants[index];
    }

    private static AutomationElement? SelectMatch(IReadOnlyList<AutomationElement> matches, ElementLocator locator, string strategyName)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (locator.Index is null)
        {
            if (matches.Count == 1)
            {
                return matches[0];
            }

            throw new InvalidOperationException($"Locator strategy '{strategyName}' is ambiguous (found {matches.Count}). Provide 'index' to disambiguate.");
        }

        var index = locator.Index.Value;
        if (index < 0 || index >= matches.Count)
        {
            throw new InvalidOperationException(
                $"Locator strategy '{strategyName}' found {matches.Count} matches but index {index} is out of range.");
        }

        return matches[index];
    }

    private static IEnumerable<AutomationElement> EnumerateSelfAndDescendantsDepthFirst(AutomationElement root, ITreeWalker walker)
    {
        yield return root;

        foreach (var child in GetChildren(root, walker))
        {
            foreach (var descendant in EnumerateSelfAndDescendantsDepthFirst(child, walker))
            {
                yield return descendant;
            }
        }
    }

    private sealed record XPathSegment(string TypeName, int? OneBasedIndex);

    private static XPathSegment ParseXPathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("XPath segment cannot be empty.");
        }

        var bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex < 0)
        {
            return new XPathSegment(segment, null);
        }

        var closingIndex = segment.IndexOf(']', bracketIndex + 1);
        if (closingIndex < 0)
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': missing closing ']'.");
        }

        if (closingIndex != segment.Length - 1)
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': unexpected characters after ']'.");
        }

        var typeName = segment[..bracketIndex];
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': missing type name.");
        }

        var indexText = segment[(bracketIndex + 1)..closingIndex];
        if (!int.TryParse(indexText, out var oneBasedIndex))
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': index is not a number.");
        }

        return new XPathSegment(typeName, oneBasedIndex);
    }

    private static string ComputeXPath(Window window, AutomationElement element, ITreeWalker walker)
    {
        if (AreSameElement(window, element))
        {
            return "/Window";
        }

        var segments = new List<string>();
        AutomationElement? current = element;

        while (current is not null && !AreSameElement(current, window))
        {
            AutomationElement? parent;
            try
            {
                parent = walker.GetParent(current);
            }
            catch
            {
                parent = null;
            }

            if (parent is null)
            {
                break;
            }

            segments.Add(ComputeXPathSegment(parent, current, walker));
            current = parent;
        }

        segments.Reverse();
        return "/Window/" + string.Join('/', segments);
    }

    private static string ComputeXPathSegment(AutomationElement parent, AutomationElement child, ITreeWalker walker)
    {
        var label = GetXPathLabel(child);
        var siblings = GetChildren(parent, walker)
            .Where(c => string.Equals(GetXPathLabel(c), label, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (siblings.Length <= 1)
        {
            return label;
        }

        var oneBasedIndex = Array.FindIndex(siblings, s => AreSameElement(s, child)) + 1;
        if (oneBasedIndex <= 0)
        {
            return label;
        }

        return $"{label}[{oneBasedIndex}]";
    }

    private static bool AreSameElement(AutomationElement first, AutomationElement second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        var firstRuntimeId = TryGetRuntimeId(first);
        var secondRuntimeId = TryGetRuntimeId(second);
        if (firstRuntimeId is not null && secondRuntimeId is not null)
        {
            return firstRuntimeId.SequenceEqual(secondRuntimeId);
        }

        return false;
    }

    private static int[]? TryGetRuntimeId(AutomationElement element)
    {
        try
        {
            return element.Properties.RuntimeId.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string GetXPathLabel(AutomationElement element)
    {
        if (element.ControlType == ControlType.Window)
        {
            return "Window";
        }

        var className = GetClassName(element);
        return !string.IsNullOrWhiteSpace(className) ? className : element.ControlType.ToString();
    }

    private static VisualTreeNode BuildVisualTreeNode(AutomationElement element, string xpath, int depth, ITreeWalker walker)
    {
        var children = depth <= 1
            ? Array.Empty<VisualTreeNode>()
            : BuildChildren(element, xpath, depth - 1, walker);

        return new VisualTreeNode(
            ElementType: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            ClassName: GetClassName(element),
            Bounds: ToRect(element.BoundingRectangle),
            IsEnabled: element.IsEnabled,
            IsOffscreen: element.IsOffscreen,
            XPath: xpath,
            Children: children);
    }

    private static IReadOnlyList<VisualTreeNode> BuildChildren(AutomationElement element, string parentXPath, int remainingDepth, ITreeWalker walker)
    {
        var rawChildren = GetChildren(element, walker).ToArray();
        if (rawChildren.Length == 0)
        {
            return Array.Empty<VisualTreeNode>();
        }

        var labels = rawChildren.Select(GetXPathLabel).ToArray();
        var countsByLabel = labels
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var nodes = new List<VisualTreeNode>(rawChildren.Length);
        for (var i = 0; i < rawChildren.Length; i++)
        {
            var child = rawChildren[i];
            var label = labels[i];

            runningIndexByLabel.TryGetValue(label, out var currentIndex);
            currentIndex++;
            runningIndexByLabel[label] = currentIndex;

            var includeIndex = countsByLabel[label] > 1;
            var segment = includeIndex ? $"{label}[{currentIndex}]" : label;
            var childXPath = $"{parentXPath}/{segment}";

            nodes.Add(BuildVisualTreeNode(child, childXPath, remainingDepth, walker));
        }

        return nodes;
    }

    private static IReadOnlyList<AutomationElement> GetChildren(AutomationElement element, ITreeWalker walker)
    {
        var children = new List<AutomationElement>();

        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch
        {
            return children;
        }

        while (child is not null)
        {
            children.Add(child);

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch
            {
                break;
            }
        }

        return children;
    }

    private static void PopulateProperties(AutomationElement element, IDictionary<string, JsonNode?> destination)
    {
        var props = element.Properties;
        var declaredType = typeof(AutomationElement).GetProperty(nameof(AutomationElement.Properties))?.PropertyType
            ?? props.GetType();

        var properties = declaredType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var property in properties)
        {
            object? wrapper;
            try
            {
                wrapper = property.GetValue(props);
            }
            catch (Exception ex)
            {
                destination[property.Name] = JsonValue.Create($"<error: {ex.Message}>");
                continue;
            }

            if (wrapper is null)
            {
                continue;
            }

            var value = TryGetWrapperValue(wrapper);
            destination[property.Name] = ToJsonNode(value);
        }
    }

    private static void PopulatePatterns(AutomationElement element, IDictionary<string, JsonNode?> destination)
    {
        var patternsObject = element.Patterns;
        var declaredType = typeof(AutomationElement).GetProperty(nameof(AutomationElement.Patterns))?.PropertyType
            ?? patternsObject.GetType();

        var patterns = declaredType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var patternProperty in patterns)
        {
            object? wrapper;
            try
            {
                wrapper = patternProperty.GetValue(patternsObject);
            }
            catch (Exception ex)
            {
                destination[patternProperty.Name] = new JsonObject
                {
                    ["isSupported"] = false,
                    ["error"] = ex.Message
                };
                continue;
            }

            if (wrapper is null)
            {
                continue;
            }

            var isSupported = TryGetBooleanProperty(wrapper, "IsSupported");
            if (isSupported is not true)
            {
                continue;
            }

            var json = new JsonObject
            {
                ["isSupported"] = true
            };

            var patternInstance = TryGetProperty(wrapper, "Pattern");
            if (patternInstance is not null)
            {
                var values = ExtractPatternValues(patternProperty.Name, patternInstance);
                if (values.Count > 0)
                {
                    json["values"] = values;
                }
            }

            destination[patternProperty.Name] = json;
        }
    }

    private static JsonObject ExtractPatternValues(string patternName, object patternInstance)
    {
        var values = new JsonObject();

        switch (patternName)
        {
            case "Value":
                AddPatternValue(values, patternInstance, "Value");
                AddPatternValue(values, patternInstance, "IsReadOnly");
                break;
            case "Toggle":
                AddPatternValue(values, patternInstance, "ToggleState");
                break;
            case "RangeValue":
                AddPatternValue(values, patternInstance, "Value");
                AddPatternValue(values, patternInstance, "Minimum");
                AddPatternValue(values, patternInstance, "Maximum");
                AddPatternValue(values, patternInstance, "IsReadOnly");
                break;
            case "Scroll":
                AddPatternValue(values, patternInstance, "HorizontallyScrollable");
                AddPatternValue(values, patternInstance, "VerticallyScrollable");
                AddPatternValue(values, patternInstance, "HorizontalScrollPercent");
                AddPatternValue(values, patternInstance, "VerticalScrollPercent");
                AddPatternValue(values, patternInstance, "HorizontalViewSize");
                AddPatternValue(values, patternInstance, "VerticalViewSize");
                break;
            case "ExpandCollapse":
                AddPatternValue(values, patternInstance, "ExpandCollapseState");
                break;
            case "SelectionItem":
                AddPatternValue(values, patternInstance, "IsSelected");
                break;
            case "Selection":
                AddPatternValue(values, patternInstance, "CanSelectMultiple");
                AddPatternValue(values, patternInstance, "IsSelectionRequired");
                break;
            case "Window":
                AddPatternValue(values, patternInstance, "IsModal");
                AddPatternValue(values, patternInstance, "IsTopmost");
                AddPatternValue(values, patternInstance, "WindowInteractionState");
                AddPatternValue(values, patternInstance, "WindowVisualState");
                break;
        }

        return values;
    }

    private static void AddPatternValue(JsonObject values, object patternInstance, string propertyName)
    {
        var property = patternInstance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
        {
            return;
        }

        object? value;
        try
        {
            value = property.GetValue(patternInstance);
        }
        catch
        {
            return;
        }

        var unwrapped = value is null ? null : TryGetWrapperValue(value) ?? value;
        values[propertyName] = ToJsonNode(unwrapped);
    }

    private static object? TryGetWrapperValue(object wrapper)
    {
        var type = wrapper.GetType();
        var valueOrDefault = type.GetProperty("ValueOrDefault", BindingFlags.Instance | BindingFlags.Public);
        if (valueOrDefault is not null && valueOrDefault.CanRead)
        {
            try
            {
                return valueOrDefault.GetValue(wrapper);
            }
            catch
            {
                return null;
            }
        }

        var value = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        if (value is not null && value.CanRead)
        {
            try
            {
                return value.GetValue(wrapper);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static object? TryGetProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
        {
            return null;
        }

        try
        {
            return property.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetBooleanProperty(object instance, string propertyName)
    {
        var value = TryGetProperty(instance, propertyName);
        return value as bool? ?? (value is bool b ? b : null);
    }

    private static string? GetAutomationId(AutomationElement element)
    {
        try
        {
            return element.Properties.AutomationId.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetName(AutomationElement element)
    {
        try
        {
            return element.Properties.Name.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetClassName(AutomationElement element)
    {
        try
        {
            return element.Properties.ClassName.Value;
        }
        catch
        {
            return null;
        }
    }

    private static Rect ToRect(Rectangle rectangle) =>
        new(X: rectangle.Left, Y: rectangle.Top, Width: rectangle.Width, Height: rectangle.Height);

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return JsonValue.Create(s);
        }

        if (value is bool b)
        {
            return JsonValue.Create(b);
        }

        if (value is int i)
        {
            return JsonValue.Create(i);
        }

        if (value is long l)
        {
            return JsonValue.Create(l);
        }

        if (value is double d)
        {
            return JsonValue.Create(d);
        }

        if (value is float f)
        {
            return JsonValue.Create(f);
        }

        if (value is decimal dec)
        {
            return JsonValue.Create(dec);
        }

        if (value is Enum e)
        {
            return JsonValue.Create(e.ToString());
        }

        if (value is IntPtr ptr)
        {
            return JsonValue.Create(ptr.ToInt64());
        }

        if (value is Guid guid)
        {
            return JsonValue.Create(guid.ToString());
        }

        if (value is Rectangle rect)
        {
            return JsonSerializer.SerializeToNode(ToRect(rect));
        }

        if (value is AutomationElement element)
        {
            return new JsonObject
            {
                ["elementType"] = element.ControlType.ToString(),
                ["automationId"] = GetAutomationId(element),
                ["name"] = GetName(element),
                ["className"] = GetClassName(element)
            };
        }

        if (value is IEnumerable<AutomationElement> elements)
        {
            var array = new JsonArray();
            foreach (var item in elements)
            {
                array.Add(ToJsonNode(item));
            }

            return array;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var array = new JsonArray();
            foreach (var item in enumerable)
            {
                array.Add(ToJsonNode(item));
            }

            return array;
        }

        try
        {
            return JsonSerializer.SerializeToNode(value);
        }
        catch
        {
            return JsonValue.Create(value.ToString());
        }
    }

    private void Cleanup()
    {
        if (_automation is not null)
        {
            _automation.Dispose();
            _automation = null;
        }

        if (_application is not null)
        {
            _application.Dispose();
            _application = null;
        }
    }
}
