using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "get_visual_tree"), Description("Return the UIA automation tree of the main window or a subtree.")]
    public static Task<GetVisualTreeResponse> GetVisualTree(
        AutomationController automation,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional root locator for subtree")] ElementLocator? root = null,
        [Description("Maximum depth (1 = root only)")] int depth = 4,
        CancellationToken cancellationToken = default) =>
        automation.GetVisualTreeAsync(windowHandle, root, depth, cancellationToken);

    [McpServerTool(Name = "get_element_properties"), Description("Return all UIA properties and supported patterns for a single element.")]
    public static Task<GetElementPropertiesResponse> GetElementProperties(
        AutomationController automation,
        [Description("Element locator")] ElementLocator locator,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        automation.GetElementPropertiesAsync(locator, windowHandle, cancellationToken);
}

