namespace WpfPilot.Contracts;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

public sealed record ElementLocator(
    [property: JsonPropertyName("automationId")] string? AutomationId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("className")] string? ClassName = null,
    [property: JsonPropertyName("xpath")] string? XPath = null,
    [property: JsonPropertyName("index")] int? Index = null);

public sealed record VisualTreeNode(
    string ElementType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    Rect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    string XPath,
    IReadOnlyList<VisualTreeNode> Children);

public sealed record GetVisualTreeResponse(VisualTreeNode Root);

public sealed record ElementSummary(
    string ElementType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    Rect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    string XPath);

public sealed record GetElementPropertiesResponse(
    ElementSummary Element,
    IReadOnlyDictionary<string, JsonNode?> Properties,
    IReadOnlyDictionary<string, JsonNode?> Patterns);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotCaptureMode
{
    Screen,
    PrintWindow,
    Auto
}

public sealed record TakeScreenshotRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    ScreenshotCaptureMode CaptureMode = ScreenshotCaptureMode.Screen);

public sealed record TakeScreenshotResponse(string PngBase64, int Width, int Height);

public sealed record FocusWindowRequest(long? WindowHandle = null, string? Title = null);

public sealed record FocusWindowResponse(bool Focused, long Handle, string Title);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickType
{
    Single,
    Double,
    Right
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickMode
{
    Auto,
    MouseAlways,
    InvokePreferred
}

public sealed record ClickElementRequest(
    ElementLocator Locator,
    long? WindowHandle = null,
    ClickType ClickType = ClickType.Single,
    ClickMode ClickMode = ClickMode.Auto);

public sealed record ClickElementResponse(bool Clicked, string MethodUsed);

public sealed record InvokeRequest(ElementLocator Locator, long? WindowHandle = null);

public sealed record InvokeResponse(bool Invoked);

public sealed record TypeTextRequest(ElementLocator Locator, string Text, long? WindowHandle = null);

public sealed record TypeTextResponse(bool Typed, string MethodUsed);

public sealed record SetValueRequest(ElementLocator Locator, double Value, long? WindowHandle = null);

public sealed record SetValueResponse(bool Set, string MethodUsed);

public sealed record SelectItemRequest(
    ElementLocator Locator,
    string? Text = null,
    int? Index = null,
    long? WindowHandle = null,
    ElementLocator? ItemLocator = null);

public sealed record SelectItemResponse(bool Selected);

public sealed record ScrollToElementRequest(
    ElementLocator Locator,
    long? WindowHandle = null,
    ElementLocator? ContainerLocator = null);

public sealed record ScrollToElementResponse(bool Scrolled, string MethodUsed);
