namespace WpfPilot.Contracts;

public sealed record LaunchAppRequest(
    string ExePath,
    IReadOnlyList<string>? Args = null,
    string? WorkingDirectory = null);

public sealed record LaunchAppResponse(int Pid, string ProcessName);

public sealed record AttachToAppRequest(int? Pid = null, string? ProcessName = null);

public sealed record AttachToAppResponse(int Pid, string ProcessName);

public sealed record CloseAppRequest(bool Force = false, int TimeoutMs = 5000);

public sealed record CloseAppResponse(bool Closed);

public sealed record ListWindowsResponse(int ProcessId, string ProcessName, IReadOnlyList<WindowInfo> Windows);

public sealed record WindowInfo(
    string Title,
    long Handle,
    Rect Bounds,
    bool IsVisible,
    bool IsEnabled);

public sealed record Rect(int X, int Y, int Width, int Height);

public sealed record TakeScreenshotRequest(long? WindowHandle = null);

public sealed record TakeScreenshotResponse(string PngBase64, int Width, int Height);
