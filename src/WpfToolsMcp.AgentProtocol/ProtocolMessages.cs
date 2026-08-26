using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WpfToolsMcp.AgentProtocol;

public sealed record AgentRequest(string Id, string Method, JsonNode? Params = null);

public sealed record AgentResponse(string Id, bool Ok, JsonNode? Result = null, AgentError? Error = null);

public sealed record AgentError(
    string Message,
    string? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);

public sealed record AgentCapabilitiesResponse(
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities);

public static class AgentProtocolCapabilities
{
    public const int CurrentProtocolVersion = 1;
    public const string GetCapabilitiesMethod = "capabilities";
    public const string ResolveElementDetailed = "wpf/resolve_element_detailed";
    public const string FindElementsDiscoveryCounts = "wpf/find_elements:discovery-counts";
    public const string GetComputedPropertiesBatch = "wpf/get_computed_properties_batch:v1";
    public const string GetDataContextPropertyPaths = "wpf/get_data_context:property-paths-v1";
    public const string GetLayoutContext = "wpf/get_layout_context";
    public const string CaptureDiagnosticSnapshot = "wpf/capture_diagnostic_snapshot:v1";
    public const string GetComputedPropertyProvenance = "wpf/get_computed_properties:provenance-v1";
    public const string CorrelateScreenshotRegion = "wpf/correlate_screenshot_region:v1";
    public const string SetValueTextModes = "wpf/set_value:text-modes-v1";
    public const string FocusElement = "wpf/focus_element:v1";
    public const string ObserveState = "wpf/observe_state:v1";
    public const string GetValidationErrors = "wpf/get_validation_errors:v1";
    public const string GetCommandInfo = "wpf/get_command_info:v1";
    public const string InspectionResponseMetadata = "wpf/inspection-response-metadata:v1";
    public const string MapUiaToWpf = "wpf/map_uia_to_wpf:v1";
    public const string KeyboardNavigationStep = "wpf/keyboard_navigation_step:v1";

    public static IReadOnlyList<string> Current { get; } = Array.AsReadOnly<string>(
    [
        ResolveElementDetailed,
        FindElementsDiscoveryCounts,
        GetComputedPropertiesBatch,
        GetDataContextPropertyPaths,
        GetLayoutContext,
        CaptureDiagnosticSnapshot,
        GetComputedPropertyProvenance,
        CorrelateScreenshotRegion,
        SetValueTextModes,
        FocusElement,
        ObserveState,
        GetValidationErrors,
        GetCommandInfo,
        InspectionResponseMetadata,
        MapUiaToWpf,
        KeyboardNavigationStep
    ]);
}
