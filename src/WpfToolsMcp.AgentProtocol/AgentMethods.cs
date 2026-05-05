namespace WpfToolsMcp.AgentProtocol;

public static class AgentMethods
{
    public const string Ping = "ping";
    public const string GetVisualTree = "wpf/get_visual_tree";
    public const string PerformanceStart = "wpf/performance_start";
    public const string PerformanceStop = "wpf/performance_stop";
    public const string FindElements = "wpf/find_elements";
    public const string GetPath = "wpf/get_path";
    public const string ResolveElement = "wpf/resolve_element";
    public const string SetValue = "wpf/set_value";
    public const string BringIntoView = "wpf/bring_into_view";
    public const string ReleaseElement = "wpf/release_element";
    public const string HighlightElement = "wpf/highlight_element";
    public const string PickElementAtPoint = "wpf/pick_element_at_point";
    public const string GetBindingInfo = "wpf/get_binding_info";
    public const string GetBindingErrors = "wpf/get_binding_errors";
    public const string GetUiaCoverageReport = "wpf/uia_coverage_report";
    public const string GetDataContext = "wpf/get_data_context";
    public const string GetComputedProperties = "wpf/get_computed_properties";
    public const string GetStyleChain = "wpf/get_style_chain";
    public const string GetTemplateInfo = "wpf/get_template_info";
}
