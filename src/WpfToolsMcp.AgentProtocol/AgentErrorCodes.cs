namespace WpfToolsMcp.AgentProtocol;

public static class AgentErrorCodes
{
    public const string UnknownMethod = "unknown_method";
    public const string MissingParams = "missing_params";
    public const string InvalidRequest = "invalid_request";
    public const string DispatcherUnavailable = "dispatcher_unavailable";
    public const string OperationCanceled = "operation_canceled";
    public const string OperationFailed = "operation_failed";
    public const string WpfResolveNotFound = "wpf_resolve_not_found";
    public const string WpfResolveAmbiguous = "wpf_resolve_ambiguous";
    public const string WpfHandleStale = "wpf_handle_stale";
}
