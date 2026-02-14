using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class InteractionTools
{
    [McpServerTool(Name = "focus_window"), Description("Bring a window to the foreground.")]
    public static Task<FocusWindowResponse> FocusWindow(
        AutomationController automation,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Window title (exact match first, then contains)")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle is not null && !string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Provide either windowHandle or title, not both.");
        }

        return automation.FocusWindowAsync(new FocusWindowRequest(windowHandle, title), cancellationToken);
    }

    [McpServerTool(Name = "click_element"), Description("Click an element by locator.")]
    public static Task<ClickElementResponse> ClickElement(
        AutomationController automation,
        [Description("Element locator")] ElementLocator locator,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Click type: single | double | right")] string? clickType = null,
        [Description("Click mode: auto | mouseAlways | invokePreferred")] string? clickMode = null,
        CancellationToken cancellationToken = default) =>
        automation.ClickElementAsync(
            new ClickElementRequest(locator, windowHandle, ParseClickType(clickType), ParseClickMode(clickMode)),
            cancellationToken);

    [McpServerTool(Name = "invoke"), Description("Invoke an element via InvokePattern.")]
    public static Task<InvokeResponse> Invoke(
        AutomationController automation,
        [Description("Element locator")] ElementLocator locator,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        automation.InvokeAsync(new InvokeRequest(locator, windowHandle), cancellationToken);

    private static ClickType ParseClickType(string? clickType)
    {
        if (string.IsNullOrWhiteSpace(clickType))
        {
            return ClickType.Single;
        }

        var value = clickType.Trim();
        if (value.Equals("single", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("leftClick", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("left_click", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Single;
        }

        if (value.Equals("double", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("doubleClick", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("double_click", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Double;
        }

        if (value.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("rightClick", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("right_click", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("context", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("contextMenu", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("context_menu", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Right;
        }

        throw new ArgumentException($"Unknown clickType '{clickType}'. Valid values: single, double, right.");
    }

    private static ClickMode ParseClickMode(string? clickMode)
    {
        if (string.IsNullOrWhiteSpace(clickMode))
        {
            return ClickMode.Auto;
        }

        var value = clickMode.Trim();
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return ClickMode.Auto;
        }

        if (value.Equals("mouseAlways", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("mouse_always", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("mouse", StringComparison.OrdinalIgnoreCase))
        {
            return ClickMode.MouseAlways;
        }

        if (value.Equals("invokePreferred", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invoke_preferred", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invokeFirst", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invoke_first", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invoke", StringComparison.OrdinalIgnoreCase))
        {
            return ClickMode.InvokePreferred;
        }

        throw new ArgumentException($"Unknown clickMode '{clickMode}'. Valid values: auto, mouseAlways, invokePreferred.");
    }
}

