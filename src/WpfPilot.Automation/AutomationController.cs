using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.WindowsAPI;
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

    public async Task<FocusWindowResponse> FocusWindowAsync(
        FocusWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        if (request.WindowHandle is not null && !string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Provide either windowHandle or title, not both.");
        }

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : !string.IsNullOrWhiteSpace(request.Title)
                ? FindWindowByTitle(application, automation, request.Title!)
                : FindMainWindow(application, automation);

        var windowPattern = window.Patterns.Window.PatternOrDefault;
        if (windowPattern is not null && windowPattern.WindowVisualState == WindowVisualState.Minimized)
        {
            windowPattern.SetWindowVisualState(WindowVisualState.Normal);
        }

        window.SetForeground();
        window.Focus();
        await Task.Delay(100, cancellationToken);

        return new FocusWindowResponse(
            Focused: true,
            Handle: window.Properties.NativeWindowHandle.Value.ToInt64(),
            Title: window.Title);
    }

    public async Task<ClickElementResponse> ClickElementAsync(
        ClickElementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Locator);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        window.SetForeground();
        window.Focus();
        await Task.Delay(100, cancellationToken);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, request.Locator, controlWalker, rawWalker);

        TryScrollIntoView(element);

        if (request.ClickType == ClickType.Single &&
            request.ClickMode != ClickMode.MouseAlways)
        {
            var invoke = element.Patterns.Invoke.PatternOrDefault;
            if (invoke is not null)
            {
                invoke.Invoke();
                await Task.Delay(75, cancellationToken);
                return new ClickElementResponse(Clicked: true, MethodUsed: "invoke");
            }
        }

        var point = GetClickPoint(element);
        switch (request.ClickType)
        {
            case ClickType.Single:
                Mouse.LeftClick(point);
                break;
            case ClickType.Double:
                Mouse.LeftDoubleClick(point);
                break;
            case ClickType.Right:
                Mouse.RightClick(point);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), $"Unknown clickType '{request.ClickType}'.");
        }

        await Task.Delay(75, cancellationToken);
        return new ClickElementResponse(Clicked: true, MethodUsed: "mouse");
    }

    public async Task<InvokeResponse> InvokeAsync(
        InvokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Locator);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        window.SetForeground();
        window.Focus();
        await Task.Delay(100, cancellationToken);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, request.Locator, controlWalker, rawWalker);

        TryScrollIntoView(element);

        var invoke = element.Patterns.Invoke.PatternOrDefault;
        if (invoke is null)
        {
            throw new InvalidOperationException(
                $"InvokePattern not supported for element (ControlType={element.ControlType}, AutomationId={GetAutomationId(element)}, Name={GetName(element)}).");
        }

        invoke.Invoke();
        await Task.Delay(75, cancellationToken);
        return new InvokeResponse(Invoked: true);
    }

    public async Task<TypeTextResponse> TypeTextAsync(
        TypeTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Locator);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        window.SetForeground();
        window.Focus();
        await Task.Delay(100, cancellationToken);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, request.Locator, controlWalker, rawWalker);

        TryScrollIntoView(element);

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern is not null && valuePattern.IsReadOnly == false)
        {
            valuePattern.SetValue(request.Text);
            await Task.Delay(75, cancellationToken);
            return new TypeTextResponse(Typed: true, MethodUsed: "valuePattern");
        }

        element.Focus();
        await Task.Delay(50, cancellationToken);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(request.Text);

        await Task.Delay(75, cancellationToken);
        return new TypeTextResponse(Typed: true, MethodUsed: "keyboard");
    }

    public async Task<SetValueResponse> SetValueAsync(
        SetValueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Locator);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        window.SetForeground();
        window.Focus();
        await Task.Delay(100, cancellationToken);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, request.Locator, controlWalker, rawWalker);

        TryScrollIntoView(element);

        var rangeValue = element.Patterns.RangeValue.PatternOrDefault;
        if (rangeValue is not null && rangeValue.IsReadOnly == false)
        {
            rangeValue.SetValue(request.Value);
            await Task.Delay(75, cancellationToken);
            return new SetValueResponse(Set: true, MethodUsed: "rangeValue");
        }

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern is not null && valuePattern.IsReadOnly == false)
        {
            valuePattern.SetValue(request.Value.ToString(CultureInfo.InvariantCulture));
            await Task.Delay(75, cancellationToken);
            return new SetValueResponse(Set: true, MethodUsed: "valuePattern");
        }

        throw new InvalidOperationException(
            "Element supports neither writable RangeValuePattern nor writable ValuePattern.");
    }

    public async Task<SelectItemResponse> SelectItemAsync(
        SelectItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Locator);

        var hasItemLocator = request.ItemLocator is not null;
        var hasText = !string.IsNullOrWhiteSpace(request.Text);
        var hasIndex = request.Index is not null;

        if (hasItemLocator && (hasText || hasIndex))
        {
            throw new ArgumentException("Provide either itemLocator or text/index, not both.");
        }

        if (!hasItemLocator && !(hasText ^ hasIndex))
        {
            throw new ArgumentException("select_item requires exactly one of: itemLocator OR text OR index.");
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        window.SetForeground();
        window.Focus();
        await Task.Delay(100, cancellationToken);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var container = ResolveElement(window, request.Locator, controlWalker, rawWalker);

        TryScrollIntoView(container);

        if (hasItemLocator)
        {
            var itemLocator = request.ItemLocator!;
            var item = !string.IsNullOrWhiteSpace(itemLocator.XPath)
                ? ResolveElement(window, itemLocator, controlWalker, rawWalker)
                : await ResolveElementWithinRootOrScrollAsync(container, itemLocator, controlWalker, cancellationToken);

            TryScrollIntoView(item);
            SelectItemElement(item);

            await Task.Delay(75, cancellationToken);
            return new SelectItemResponse(Selected: true);
        }

        if (container.ControlType == ControlType.ComboBox)
        {
            var comboBox = container.AsComboBox();
            if (hasIndex)
            {
                comboBox.Select(request.Index!.Value);
            }
            else
            {
                comboBox.Select(request.Text!);
            }

            await Task.Delay(75, cancellationToken);
            return new SelectItemResponse(Selected: true);
        }

        var allItems = EnumerateSelectableItems(container, controlWalker).ToArray();
        if (allItems.Length == 0)
        {
            throw new InvalidOperationException(
                $"No selectable items found under locator (ControlType={container.ControlType}, AutomationId={GetAutomationId(container)}, Name={GetName(container)}).");
        }

        var preferredItems = TryFilterItemsToSelectionContainer(container, allItems);

        AutomationElement? selectedItem = null;
        if (hasIndex)
        {
            var items = preferredItems is not null && preferredItems.Length > 0 ? preferredItems : allItems;
            var index = request.Index!.Value;
            if (index < 0 || index >= items.Length)
            {
                throw new InvalidOperationException($"index {index} is out of range (found {items.Length} selectable items).");
            }

            selectedItem = items[index];
        }
        else
        {
            var text = request.Text!;
            if (preferredItems is not null && preferredItems.Length > 0)
            {
                selectedItem = FindUniqueItemByName(preferredItems, text, out var matches);
                if (matches > 1)
                {
                    throw new InvalidOperationException($"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }
            }

            if (selectedItem is null)
            {
                selectedItem = FindUniqueItemByName(allItems, text, out var matches);
                if (matches > 1)
                {
                    throw new InvalidOperationException($"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }

                if (selectedItem is null)
                {
                    selectedItem = await ScrollSearchUniqueItemByNameAsync(
                        container,
                        text,
                        controlWalker,
                        cancellationToken);
                }
            }
        }

        if (selectedItem is null)
        {
            throw new InvalidOperationException("Selected item could not be resolved.");
        }

        TryScrollIntoView(selectedItem);
        SelectItemElement(selectedItem);

        await Task.Delay(75, cancellationToken);
        return new SelectItemResponse(Selected: true);
    }

    private static AutomationElement ResolveElementWithinRoot(AutomationElement root, ElementLocator locator, ITreeWalker walker)
    {
        return TryResolveElementWithinRoot(root, locator, walker)
            ?? throw new InvalidOperationException("itemLocator did not match any element under the selection container.");
    }

    private static AutomationElement? TryResolveElementWithinRoot(AutomationElement root, ElementLocator locator, ITreeWalker walker)
    {
        if (locator is null)
        {
            throw new ArgumentNullException(nameof(locator));
        }

        var descendants = EnumerateSelfAndDescendantsDepthFirst(root, walker).Skip(1).ToArray();

        if (!string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            var matches = descendants
                .Where(e => string.Equals(GetAutomationId(e), locator.AutomationId, StringComparison.Ordinal))
                .ToArray();

            var resolved = SelectMatchForItemLocator(matches, root, locator, "automationId");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.Name))
        {
            var matches = descendants
                .Where(e => string.Equals(GetName(e), locator.Name, StringComparison.Ordinal))
                .ToArray();

            var resolved = SelectMatchForItemLocator(matches, root, locator, "name");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName))
        {
            var matches = descendants
                .Where(e => string.Equals(GetClassName(e), locator.ClassName, StringComparison.Ordinal))
                .ToArray();

            var resolved = SelectMatchForItemLocator(matches, root, locator, "className");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (locator.Index is int index &&
            string.IsNullOrWhiteSpace(locator.AutomationId) &&
            string.IsNullOrWhiteSpace(locator.Name) &&
            string.IsNullOrWhiteSpace(locator.ClassName) &&
            string.IsNullOrWhiteSpace(locator.XPath))
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            if (index >= descendants.Length)
            {
                return null;
            }

            return descendants[index];
        }

        return null;
    }

    private static AutomationElement? SelectMatchForItemLocator(
        IReadOnlyList<AutomationElement> matches,
        AutomationElement rootContainer,
        ElementLocator locator,
        string strategyName)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (locator.Index is int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            if (index >= matches.Count)
            {
                throw new InvalidOperationException(
                    $"Locator strategy '{strategyName}' index {index} is out of range (found {matches.Count}).");
            }

            return matches[index];
        }

        var selectionItemCandidates = matches.Where(HasSelectionItemPattern).ToArray();
        if (selectionItemCandidates.Length > 1)
        {
            var owned = selectionItemCandidates
                .Where(e => TryGetSelectionContainer(e, out var container) && AreSameElement(container, rootContainer))
                .ToArray();

            if (owned.Length == 1)
            {
                return owned[0];
            }

            if (owned.Length > 1)
            {
                selectionItemCandidates = owned;
            }
        }

        if (selectionItemCandidates.Length == 1)
        {
            return selectionItemCandidates[0];
        }

        throw new InvalidOperationException(
            $"Locator strategy '{strategyName}' is ambiguous (found {matches.Count}). Provide 'index' to disambiguate.");
    }

    private static async Task<AutomationElement> ResolveElementWithinRootOrScrollAsync(
        AutomationElement container,
        ElementLocator locator,
        ITreeWalker walker,
        CancellationToken cancellationToken)
    {
        var resolved = TryResolveElementWithinRoot(container, locator, walker);
        if (resolved is not null)
        {
            return resolved;
        }

        if (!TryGetScrollable(container, walker, out var scrollElement))
        {
            return ResolveElementWithinRoot(container, locator, walker);
        }

        var scroll = scrollElement.Patterns.Scroll.PatternOrDefault;
        if (scroll is null || !scroll.VerticallyScrollable)
        {
            return ResolveElementWithinRoot(container, locator, walker);
        }

        try
        {
            var horizontal = scroll.HorizontallyScrollable ? scroll.HorizontalScrollPercent : -1d;
            scroll.SetScrollPercent(horizontal, 0);
        }
        catch
        {
        }

        await Task.Delay(75, cancellationToken);

        var maxScrollSteps = 50;
        double? lastPercent = null;
        for (var step = 0; step <= maxScrollSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            resolved = TryResolveElementWithinRoot(container, locator, walker);
            if (resolved is not null)
            {
                return resolved;
            }

            var beforePercent = TryGetScrollPercent(scroll, vertical: true);
            if (beforePercent is not null && beforePercent >= 100)
            {
                break;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            try
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            }
            catch
            {
                break;
            }

            await Task.Delay(75, cancellationToken);

            var afterPercent = TryGetScrollPercent(scroll, vertical: true);
            if (afterPercent is not null && lastPercent is not null && Math.Abs(afterPercent.Value - lastPercent.Value) < 0.0001)
            {
                break;
            }

            lastPercent = afterPercent;
        }

        return ResolveElementWithinRoot(container, locator, walker);
    }

    private static AutomationElement[]? TryFilterItemsToSelectionContainer(
        AutomationElement container,
        IReadOnlyList<AutomationElement> items)
    {
        if (!SupportsSelectionPattern(container))
        {
            return null;
        }

        var owned = new List<AutomationElement>();
        foreach (var item in items)
        {
            if (!TryGetSelectionContainer(item, out var selectionContainer))
            {
                continue;
            }

            if (AreSameElement(selectionContainer, container))
            {
                owned.Add(item);
            }
        }

        return owned.Count > 0 ? owned.ToArray() : null;
    }

    private static bool SupportsSelectionPattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.Selection.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetSelectionContainer(AutomationElement item, out AutomationElement selectionContainer)
    {
        selectionContainer = null!;

        try
        {
            var selectionItem = item.Patterns.SelectionItem.PatternOrDefault;
            var container = selectionItem?.SelectionContainer.ValueOrDefault;
            if (container is null)
            {
                return false;
            }

            selectionContainer = container;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateSelectableItems(AutomationElement root, ITreeWalker walker) =>
        EnumerateSelfAndDescendantsDepthFirst(root, walker)
            .Skip(1)
            .Where(HasSelectionItemPattern);

    private static bool HasSelectionItemPattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.SelectionItem.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static void SelectItemElement(AutomationElement item)
    {
        try
        {
            var selectionItem = item.Patterns.SelectionItem.PatternOrDefault;
            if (selectionItem is not null)
            {
                selectionItem.Select();
                return;
            }
        }
        catch
        {
        }

        try
        {
            var invoke = item.Patterns.Invoke.PatternOrDefault;
            if (invoke is not null)
            {
                invoke.Invoke();
                return;
            }
        }
        catch
        {
        }

        var point = GetClickPoint(item);
        Mouse.LeftClick(point);
    }

    private static AutomationElement? FindUniqueItemByName(
        IReadOnlyList<AutomationElement> items,
        string text,
        out int matches)
    {
        matches = 0;
        AutomationElement? match = null;

        foreach (var item in items)
        {
            var name = GetName(item);
            if (!string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;
            if (matches == 1)
            {
                match = item;
            }
        }

        return match;
    }

    private static bool TryGetScrollable(AutomationElement root, ITreeWalker walker, out AutomationElement scrollElement)
    {
        scrollElement = null!;

        foreach (var element in EnumerateSelfAndDescendantsDepthFirst(root, walker))
        {
            try
            {
                var scroll = element.Patterns.Scroll.PatternOrDefault;
                if (scroll is not null && scroll.VerticallyScrollable)
                {
                    scrollElement = element;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static async Task<AutomationElement> ScrollSearchUniqueItemByNameAsync(
        AutomationElement container,
        string text,
        ITreeWalker walker,
        CancellationToken cancellationToken)
    {
        if (!TryGetScrollable(container, walker, out var scrollElement))
        {
            throw new InvalidOperationException(
                $"Item text '{text}' not found (scanned current view) and container is not scrollable. Consider itemLocator.");
        }

        var scroll = scrollElement.Patterns.Scroll.PatternOrDefault;
        if (scroll is null || !scroll.VerticallyScrollable)
        {
            throw new InvalidOperationException(
                $"Item text '{text}' not found (scanned current view) and container is not vertically scrollable. Consider itemLocator.");
        }

        try
        {
            var horizontal = scroll.HorizontallyScrollable ? scroll.HorizontalScrollPercent : -1d;
            scroll.SetScrollPercent(horizontal, 0);
        }
        catch
        {
        }

        await Task.Delay(75, cancellationToken);

        var maxScrollSteps = 50;
        var scanned = 0;

        double? lastPercent = null;
        for (var step = 0; step <= maxScrollSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allItems = EnumerateSelectableItems(container, walker).ToArray();
            scanned += allItems.Length;

            var preferredItems = TryFilterItemsToSelectionContainer(container, allItems);

            if (preferredItems is not null && preferredItems.Length > 0)
            {
                var candidate = FindUniqueItemByName(preferredItems, text, out var matches);
                if (matches == 1 && candidate is not null)
                {
                    return candidate;
                }

                if (matches > 1)
                {
                    throw new InvalidOperationException(
                        $"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }
            }

            {
                var candidate = FindUniqueItemByName(allItems, text, out var matches);
                if (matches == 1 && candidate is not null)
                {
                    return candidate;
                }

                if (matches > 1)
                {
                    throw new InvalidOperationException(
                        $"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }
            }

            var beforePercent = TryGetScrollPercent(scroll, vertical: true);
            if (beforePercent is not null && beforePercent >= 100)
            {
                break;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            try
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            }
            catch
            {
                break;
            }

            await Task.Delay(75, cancellationToken);

            var afterPercent = TryGetScrollPercent(scroll, vertical: true);
            if (afterPercent is not null && lastPercent is not null && Math.Abs(afterPercent.Value - lastPercent.Value) < 0.0001)
            {
                break;
            }

            lastPercent = afterPercent;
        }

        throw new InvalidOperationException(
            $"Item text '{text}' not found (scanned ~{scanned} items across scroll attempts). Consider itemLocator.");
    }

    private static double? TryGetScrollPercent(IScrollPattern scrollPattern, bool vertical)
    {
        try
        {
            var value = vertical ? scrollPattern.VerticalScrollPercent : scrollPattern.HorizontalScrollPercent;
            if (double.IsNaN(value))
            {
                return null;
            }

            return value;
        }
        catch
        {
            return null;
        }
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

    private static Window FindWindowByTitle(Application application, UIA3Automation automation, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var windows = application.GetAllTopLevelWindows(automation).ToArray();

        var exact = windows
            .Where(w => w is not null && string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exact.Length == 1)
        {
            return exact[0];
        }

        if (exact.Length > 1)
        {
            throw new InvalidOperationException($"Multiple windows found with title '{title}'. Provide windowHandle instead.");
        }

        var contains = windows
            .Where(w => w is not null && w.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        if (contains.Length == 1)
        {
            return contains[0];
        }

        if (contains.Length > 1)
        {
            throw new InvalidOperationException($"Multiple windows contain title '{title}'. Provide windowHandle instead.");
        }

        throw new InvalidOperationException($"No window found with title '{title}'.");
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

    private static void TryScrollIntoView(AutomationElement element)
    {
        try
        {
            if (element.IsOffscreen)
            {
                element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            }
        }
        catch
        {
        }
    }

    private static Point GetClickPoint(AutomationElement element)
    {
        if (element.TryGetClickablePoint(out var point))
        {
            return point;
        }

        var bounds = element.BoundingRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Element has no clickable point and has invalid bounds.");
        }

        return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
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
