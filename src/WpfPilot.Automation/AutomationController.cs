using System.Diagnostics;
using System.Drawing.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
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

        var captureSettings = new CaptureSettings { OutputScale = 1 };
        using var capture = Capture.Element(window, captureSettings);
        using var stream = new MemoryStream();
        capture.Bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        var response = new TakeScreenshotResponse(Convert.ToBase64String(bytes), capture.Bitmap.Width, capture.Bitmap.Height);
        return response;
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
