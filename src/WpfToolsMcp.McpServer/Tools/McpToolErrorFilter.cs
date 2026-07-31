using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static partial class McpToolErrorFilter
{
    private const int MaxCandidates = 25;
    private const int MaxTraversedExceptions = 16;
    private const int MaxCodeLength = 64;
    private const int MaxDetailLength = 512;
    private const int MaxIdentityLength = 128;
    private const int MaxCauseTypeLength = 256;
    private const int MaxCauseMessageLength = 1024;
    private const int MaxCauseDetailsLength = 4096;
    private const int MaxCandidateTextLength = 512;
    private const int MaxCandidateExecutablePathLength = 512;
    private const int MaxCandidateExecutablePathUnavailableReasonLength = 256;
    private const int MaxCandidateXPathLength = 1024;
    private const int MaxRecoveryActions = 8;
    private const int MaxTruncatedReasonLength = 64;
    private const int MaxCompatibilityTextLength = 6_144;

    private static readonly HashSet<string> StableToolCodes = new(StringComparer.Ordinal)
    {
        "client_origin_unavailable",
        "element_disabled",
        "element_enabled_unknown",
        "focus_failed_uia_target",
        "focus_failed_wpf_target",
        "focus_unsupported_wpf_target",
        "focused_element_outside_session",
        "focused_element_unavailable",
        "foreground_activation_failed",
        "invoke_unsupported_wpf_target",
        "observe_state_connection_lost",
        "observe_state_dispatcher_required",
        "observe_state_not_active",
        "observe_state_not_found",
        "observe_state_released",
        "observe_state_unsupported",
        "operation_timeout",
        "performance_already_running",
        "performance_not_running",
        "performance_run_id_mismatch",
        "performance_stop_failed",
        "pick_point_in_different_window",
        "set_value_unsupported_wpf_target",
        "screenshot_correlation_missing_viewport_context",
        "subscription_limit_exceeded",
        "uia_action_failed",
        "uia_provider_operation_failed",
        "wait_backend_unsupported",
        "wait_window_framework_probe_limit",
        "wait_window_scan_failed",
        "wait_window_scan_limit",
        "window_selector_mismatch",
        "window_state_change_failed",
        "window_uia_unavailable",
        "wpf_backend_unavailable",
        "wpf_command_info",
        "wpf_element_has_no_bounds",
        "wpf_element_has_no_bounds_after_bring_into_view",
        "wpf_enabled_check_failed",
        "wpf_focus_capability_unavailable",
        "wpf_layout_context",
        "wpf_window_dispatcher_required",
        "wpf_window_not_found",
        "xpath_resolved_but_filters_mismatch"
    };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateCallToolFilter() =>
        next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (McpProtocolException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (context.MatchedPrimitive is null)
                {
                    throw;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw CreateRequestCancellation(exception, cancellationToken);
                }

                var error = MapException(
                    exception,
                    CreateRequestContext(context.Params?.Arguments));
                return CreateResult(error);
            }
        };

    private static ToolErrorInfo MapException(Exception exception, ToolErrorContext? requestContext)
    {
        var exceptions = EnumerateExceptions(exception).ToArray();
        var messages = new Dictionary<Exception, ExceptionMessageRead>(ReferenceEqualityComparer.Instance);

        if (exceptions.OfType<ActionableFailureException>().FirstOrDefault() is { } actionable)
        {
            var mapped = FromActionableFailure(actionable.Failure, requestContext);
            return mapped.Cause is not null
                ? mapped
                : WithCause(mapped, SelectDeepestCause(actionable), messages);
        }

        if (exceptions.OfType<ProcessSelectionAmbiguityException>().FirstOrDefault() is { } processAmbiguity)
        {
            return WithCause(
                FromProcessAmbiguity(processAmbiguity.Ambiguity, requestContext),
                SelectDeepestCause(processAmbiguity),
                messages);
        }

        if (exceptions.OfType<ElementResolutionAmbiguityException>().FirstOrDefault() is { } elementAmbiguity)
        {
            return WithCause(
                FromElementAmbiguity(elementAmbiguity.Ambiguity, requestContext),
                SelectDeepestCause(elementAmbiguity),
                messages);
        }

        foreach (var candidate in exceptions)
        {
            if (candidate is AgentRemoteException remote)
            {
                if (!string.IsNullOrWhiteSpace(remote.RemoteCode) &&
                    TryMapStableCode(
                        remote.RemoteMessage,
                        requestContext,
                        out var remoteMapped,
                        remote.RemoteCode))
                {
                    return WithCause(remoteMapped, remote, messages);
                }

                continue;
            }

            if (TryGetExceptionMessage(candidate, messages, out var message) &&
                TryMapStableCode(message, requestContext, out var mapped))
            {
                return WithCause(mapped, SelectDeepestCause(candidate), messages);
            }
        }

        if (exceptions.FirstOrDefault(candidate =>
                candidate is TimeoutException or OperationCanceledException) is { } timeout)
        {
            return WithCause(
                CreateKnownError(
                    "timeout",
                    "The tool operation timed out.",
                    retryable: true,
                    ["retry"],
                    requestContext),
                timeout,
                messages);
        }

        if (exceptions.FirstOrDefault(candidate =>
                candidate is ArgumentException or JsonException or NotSupportedException or McpException) is { } invalidRequest)
        {
            return WithCause(
                CreateKnownError(
                    "invalid_request",
                    "The tool arguments are invalid.",
                    retryable: false,
                    ["correct_arguments"],
                    requestContext),
                invalidRequest,
                messages);
        }

        return WithCause(CreateUnknownError(requestContext), SelectDeepestCause(exception), messages);
    }

    private static ToolErrorInfo FromActionableFailure(
        FailureInfo failure,
        ToolErrorContext? requestContext)
    {
        var code = NormalizeCode(failure.Code);
        var actions = NormalizeActions(failure.RecoveryActions);
        return new ToolErrorInfo(code, Bound(failure.Detail, MaxDetailLength, "The tool operation failed."))
        {
            Stage = NormalizeToken(failure.Stage),
            Retryable = failure.Retryable,
            RetryAfterMs = failure.RetryAfterMs is > 0 ? failure.RetryAfterMs : null,
            RecoveryActions = actions,
            Context = requestContext,
            Cause = NormalizeCause(failure.Cause)
        };
    }

    private static ToolErrorInfo FromProcessAmbiguity(
        ProcessSelectionAmbiguity ambiguity,
        ToolErrorContext? requestContext)
    {
        var candidates = ambiguity.Candidates
            .Take(MaxCandidates)
            .Select(CreateProcessCandidate)
            .ToArray();
        var filterCapped = ambiguity.Candidates.Count > candidates.Length;
        var context = MergeContext(requestContext, new ToolErrorContext
        {
            ReturnedCandidates = candidates.Length,
            DiscoveredCandidates = Math.Max(0, ambiguity.DiscoveredCandidates),
            Truncated = ambiguity.Truncated || filterCapped,
            TruncatedReason = NormalizeTruncatedReason(ambiguity.TruncatedReason)
                ?? (filterCapped ? "maxCandidates" : null),
            Candidates = candidates
        });

        return CreateKnownError(
            "ambiguous_process",
            "Multiple live processes matched the request.",
            retryable: true,
            ["select_process_instance"],
            context);
    }

    private static ToolErrorInfo FromElementAmbiguity(
        ResolveElementAmbiguity ambiguity,
        ToolErrorContext? requestContext)
    {
        var candidates = ambiguity.Candidates
            .Take(MaxCandidates)
            .Select(CreateElementCandidate)
            .ToArray();
        var filterCapped = ambiguity.Candidates.Count > candidates.Length;
        var context = MergeContext(requestContext, new ToolErrorContext
        {
            WindowHandle = ambiguity.WindowHandleUsed > 0 ? ambiguity.WindowHandleUsed : null,
            Backend = ambiguity.BackendUsed,
            ReturnedCandidates = candidates.Length,
            DiscoveredCandidates = Math.Max(0, ambiguity.DiscoveredCandidates),
            Truncated = ambiguity.Truncated || filterCapped,
            TruncatedReason = NormalizeTruncatedReason(ambiguity.TruncatedReason)
                ?? (filterCapped ? "maxCandidates" : null),
            Candidates = candidates
        });

        return CreateKnownError(
            "ambiguous_element",
            "The locator matched multiple elements.",
            retryable: true,
            ["select_element_candidate"],
            context);
    }

    private static ToolErrorCandidate CreateProcessCandidate(ProcessCandidateInfo candidate)
    {
        var executablePath = NormalizeExactCandidateText(
            candidate.ExecutablePath,
            MaxCandidateExecutablePathLength,
            out var executablePathOmitted);
        var unavailableReason = executablePathOmitted
            ? $"executablePathOmitted:maxLength={MaxCandidateExecutablePathLength};actualLength={candidate.ExecutablePath!.Length}"
            : candidate.ExecutablePathUnavailableReason;

        return new ToolErrorCandidate(ToolErrorCandidateKind.Process, candidate.Index)
        {
            ProcessInstanceId = NormalizeProcessInstanceId(candidate.ProcessInstanceId),
            Pid = candidate.Pid > 0 ? candidate.Pid : null,
            WindowHandle = candidate.MainWindowHandle > 0 ? candidate.MainWindowHandle : null,
            ProcessName = BoundOptional(candidate.ProcessName, MaxCandidateTextLength),
            StartTimeUtc = BoundOptional(candidate.StartTimeUtc, MaxCandidateTextLength),
            MainWindowTitle = BoundOptional(candidate.MainWindowTitle, MaxCandidateTextLength),
            ExecutablePath = executablePath,
            ExecutablePathUnavailableReason = BoundOptional(
                unavailableReason,
                MaxCandidateExecutablePathUnavailableReasonLength)
        };
    }

    private static ToolErrorCandidate CreateElementCandidate(ResolveElementCandidate candidate)
    {
        var xpath = NormalizeExactCandidateText(
            candidate.Element.XPath,
            MaxCandidateXPathLength,
            out var xpathOmitted);
        return new ToolErrorCandidate(ToolErrorCandidateKind.Element, candidate.Index)
        {
            ElementId = NormalizeElementId(candidate.Element.ElementId),
            ElementType = BoundOptional(candidate.Element.Type, MaxCandidateTextLength),
            AutomationId = BoundOptional(candidate.Element.AutomationId, MaxCandidateTextLength),
            Name = BoundOptional(candidate.Element.Name, MaxCandidateTextLength),
            XPath = xpath,
            XPathOmitted = xpathOmitted ? true : null,
            Bounds = NormalizeBounds(candidate.Element.Bounds)
        };
    }

    private static bool TryMapStableCode(
        string? message,
        ToolErrorContext? context,
        out ToolErrorInfo error,
        string? trustedCode = null)
    {
        error = null!;
        if (string.Equals(trustedCode, "wpf_resolve:not_found", StringComparison.Ordinal) ||
            (trustedCode is null &&
             message?.StartsWith("wpf_resolve:not_found", StringComparison.Ordinal) is true))
        {
            error = CreateKnownError(
                "element_not_found",
                "No element matched the locator.",
                true,
                ["refine_locator"],
                context);
            return true;
        }

        if (string.Equals(trustedCode, "wpf_resolve:ambiguous", StringComparison.Ordinal))
        {
            error = CreateKnownError(
                "ambiguous_element",
                "The locator matched multiple elements.",
                true,
                ["refine_locator"],
                context);
            return true;
        }

        var code = trustedCode ?? ReadLeadingCode(message);
        if (code is null)
        {
            return false;
        }

        error = code switch
        {
            "stale_element" or "wpf_handle_stale" =>
                CreateKnownError("stale_element", "The element handle is no longer valid.", true, ["resolve_element"], context),
            "timeout" =>
                CreateKnownError("timeout", "The tool operation timed out.", true, ["retry"], context),
            "element_offscreen" =>
                CreateKnownError("element_offscreen", "The target element is offscreen.", true, ["scroll_to_element"], context),
            "element_offscreen_after_scroll" =>
                CreateKnownError("element_offscreen_after_scroll", "The target element remained offscreen after scrolling.", true, ["inspect_element"], context),
            "no_hit_at_point" =>
                CreateKnownError("no_hit_at_point", "No automation element was found at the requested point.", false, ["correct_coordinates"], context),
            "element_not_found" =>
                CreateKnownError("element_not_found", "No element matched the locator.", true, ["refine_locator"], context),
            "invalid_request" =>
                CreateKnownError("invalid_request", "The tool arguments are invalid.", false, ["correct_arguments"], context),
            "ambiguous_element" =>
                CreateKnownError("ambiguous_element", "The locator matched multiple elements.", true, ["refine_locator"], context),
            "ambiguous_process" =>
                CreateKnownError("ambiguous_process", "Multiple live processes matched the request.", true, ["select_process_instance"], context),
            "stale_process_candidate" =>
                CreateKnownError("stale_process_candidate", "The selected process candidate is no longer valid.", true, ["list_process_candidates"], context),
            "stale_session" =>
                CreateKnownError("stale_session", "The session is no longer valid.", true, ["attach_to_app"], context),
            "stale_window" =>
                CreateKnownError("stale_window", "The window handle is no longer valid.", true, ["list_windows"], context),
            "window_closed" =>
                CreateKnownError("stale_window", "The window handle is no longer valid.", true, ["list_windows"], context),
            "session_replacement_in_progress" =>
                CreateKnownError("session_replacement_in_progress", "The session is being replaced.", true, ["retry"], context, retryAfterMs: 250),
            "target_process_still_running" =>
                CreateKnownError("target_process_still_running", "The target process is still running.", false, ["terminate_app"], context),
            "process_not_found" =>
                CreateKnownError("process_not_found", "The target process was not found.", false, ["correct_process_target"], context),
            "process_identity_unavailable" =>
                CreateKnownError("process_identity_unavailable", "The target process identity could not be confirmed.", true, ["retry"], context),
            "process_state_unavailable" =>
                CreateKnownError("process_state_unavailable", "The target process state could not be confirmed.", true, ["retry"], context),
            "active_window_unavailable" =>
                CreateKnownError("active_window_unavailable", "The active window could not be resolved.", true, ["list_windows"], context),
            "window_outside_session" =>
                CreateKnownError("window_outside_session", "The window is outside the attached session.", false, ["list_windows"], context),
            "interaction_policy_blocked" =>
                CreateKnownError("interaction_policy_blocked", "The operation is blocked by the interaction policy.", false, ["update_interaction_policy"], context),
            "mouse_target_occluded" =>
                CreateKnownError("mouse_target_occluded", "The requested mouse target is occluded.", true, ["uncover_target", "use_semantic_interaction"], context),
            "performance_run_not_owned" =>
                CreateKnownError("performance_run_not_owned", "The performance run belongs to another session.", false, ["use_owning_session"], context),
            "subscription_not_found" =>
                CreateKnownError("subscription_not_found", "The subscription is no longer available.", false, ["subscribe_again"], context),
            "screenshot_viewport_unstable" =>
                CreateKnownError("screenshot_viewport_unstable", "The screenshot viewport did not stabilize.", true, ["retry"], context),
            "viewport_conditions_unstable" =>
                CreateKnownError("viewport_conditions_unstable", "The requested viewport conditions did not stabilize.", true, ["retry"], context),
            "dpi_context_unavailable" =>
                CreateKnownError("dpi_context_unavailable", "The target DPI context is unavailable.", true, ["retry"], context),
            "monitor_dpi_unavailable" =>
                CreateKnownError("monitor_dpi_unavailable", "The monitor DPI is unavailable.", true, ["retry"], context),
            "agent_capability_unavailable" =>
                CreateKnownError(
                    "agent_capability_unavailable",
                    "The connected WPF agent does not support this operation.",
                    false,
                    ["restart_and_reattach"],
                    context),
            _ => trustedCode is not null || StableToolCodes.Contains(code)
                ? CreateForwardedCodeError(code, message ?? code, context)
                : null!
        };
        return error is not null;
    }

    private static ToolErrorInfo CreateKnownError(
        string code,
        string detail,
        bool retryable,
        IReadOnlyList<string> recoveryActions,
        ToolErrorContext? context,
        int? retryAfterMs = null) =>
        new(code, detail)
        {
            Retryable = retryable,
            RetryAfterMs = retryAfterMs,
            RecoveryActions = recoveryActions,
            Context = context
        };

    private static ToolErrorInfo CreateForwardedCodeError(
        string code,
        string message,
        ToolErrorContext? context)
    {
        var detail = message.Length > code.Length &&
            message.StartsWith(code, StringComparison.Ordinal) &&
            message[code.Length] == ':'
                ? message[(code.Length + 1)..].Trim()
                : string.Equals(message, code, StringComparison.Ordinal)
                    ? string.Empty
                    : message.Trim();
        return new ToolErrorInfo(
            code,
            Bound(
                detail,
                MaxDetailLength,
                $"The tool reported '{code}'."))
        {
            Context = context
        };
    }

    private static ToolErrorInfo CreateUnknownError(ToolErrorContext? context) =>
        new("tool_failed", "The tool operation failed.")
        {
            Context = context
        };

    private static CallToolResult CreateResult(ToolErrorInfo error)
    {
        var envelope = new ToolErrorResponse(error);
        var structuredContent = JsonSerializer.SerializeToElement(
            envelope,
            McpJsonUtilities.DefaultOptions);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = FormatCompatibilityText(error) }],
            StructuredContent = structuredContent
        };
    }

    private static string FormatCompatibilityText(ToolErrorInfo error)
    {
        var text = $"{error.Code}: {error.Detail}";
        if (error.Cause is not { } cause)
        {
            return text;
        }

        var causeSummary = cause.Type;
        if (!string.IsNullOrWhiteSpace(cause.Message))
        {
            causeSummary += $": {cause.Message}";
        }
        else if (!string.IsNullOrWhiteSpace(cause.MessageUnavailableReason))
        {
            causeSummary += $" (message unavailable: {cause.MessageUnavailableReason})";
        }

        if (!string.IsNullOrWhiteSpace(cause.Details))
        {
            causeSummary += $" Details: {cause.Details}";
        }

        return Bound(
            $"{text} Cause: {causeSummary}",
            MaxCompatibilityTextLength,
            text);
    }

    private static ToolErrorContext? CreateRequestContext(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var sessionId = ReadString(arguments, "sessionId", NormalizeSessionId);
        var elementId = ReadString(arguments, "elementId", NormalizeElementId);
        var windowHandle = ReadPositiveInt64(arguments, "windowHandle");
        var backend = ReadBackend(arguments);
        return sessionId is null && elementId is null && windowHandle is null && backend is null
            ? null
            : new ToolErrorContext
            {
                SessionId = sessionId,
                ElementId = elementId,
                WindowHandle = windowHandle,
                Backend = backend
            };
    }

    private static ToolErrorContext? MergeContext(ToolErrorContext? request, ToolErrorContext observed)
    {
        var merged = new ToolErrorContext
        {
            SessionId = request?.SessionId,
            ElementId = request?.ElementId,
            WindowHandle = observed.WindowHandle ?? request?.WindowHandle,
            Backend = observed.Backend ?? request?.Backend,
            ReturnedCandidates = observed.ReturnedCandidates,
            DiscoveredCandidates = observed.DiscoveredCandidates,
            Truncated = observed.Truncated,
            TruncatedReason = observed.TruncatedReason,
            Candidates = observed.Candidates
        };
        return HasContext(merged) ? merged : null;
    }

    private static bool HasContext(ToolErrorContext context) =>
        context.SessionId is not null || context.ElementId is not null || context.WindowHandle is not null ||
        context.Backend is not null || context.ReturnedCandidates is not null ||
        context.DiscoveredCandidates is not null || context.Truncated is not null ||
        context.TruncatedReason is not null || context.Candidates is not null;

    private static string? ReadString(
        IDictionary<string, JsonElement> arguments,
        string name,
        Func<string?, string?> normalize) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? normalize(value.GetString())
            : null;

    private static long? ReadPositiveInt64(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.TryGetInt64(out var result) && result > 0
            ? result
            : null;

    private static InspectionBackend? ReadBackend(IDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue("backend", out var value) || value.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<InspectionBackend>(value.GetString(), ignoreCase: true, out var backend) ||
            !Enum.IsDefined(backend))
        {
            return null;
        }

        return backend;
    }

    private static string NormalizeCode(string? value) =>
        NormalizeToken(value) ?? "tool_failed";

    private static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxCodeLength && TokenRegex().IsMatch(trimmed)
            ? trimmed
            : null;
    }

    private static string? NormalizeTruncatedReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxTruncatedReasonLength && TruncatedReasonRegex().IsMatch(trimmed)
            ? trimmed
            : null;
    }

    private static IReadOnlyList<string>? NormalizeActions(IReadOnlyList<string>? actions)
    {
        if (actions is null)
        {
            return null;
        }

        var normalized = actions
            .Select(NormalizeToken)
            .Where(action => action is not null)
            .Select(action => action!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxRecoveryActions)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string Bound(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        return TruncateUtf16(trimmed, maxLength);
    }

    private static string? BoundOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return TruncateUtf16(trimmed, maxLength);
    }

    private static string? NormalizeExactCandidateText(
        string? value,
        int maxLength,
        out bool omitted)
    {
        omitted = value is not null && value.Length > maxLength;
        return string.IsNullOrWhiteSpace(value) || omitted ? null : value;
    }

    private static string TruncateUtf16(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (length > 0 &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }

    private static ToolErrorInfo WithCause(
        ToolErrorInfo error,
        Exception exception,
        IDictionary<Exception, ExceptionMessageRead> messages) =>
        error with { Cause = CreateCause(exception, messages) };

    private static DiagnosticCauseInfo CreateCause(
        Exception exception,
        IDictionary<Exception, ExceptionMessageRead> messages)
    {
        var hasMessage = TryGetExceptionMessage(exception, messages, out var message);
        var messageRead = messages[exception];
        var type = Bound(
            exception.GetType().FullName ?? exception.GetType().Name,
            MaxCauseTypeLength,
            nameof(Exception));
        var details = exception switch
        {
            AgentRemoteException remote => BoundOptional(remote.RemoteDetails, MaxCauseDetailsLength),
            FileNotFoundException { FileName: { } fileName } =>
                BoundOptional($"File name: '{fileName}'", MaxCauseDetailsLength),
            _ => null
        };
        return new DiagnosticCauseInfo(type)
        {
            Message = hasMessage ? message : null,
            Details = details,
            MessageUnavailableReason = messageRead.UnavailableReason
        };
    }

    private static DiagnosticCauseInfo? NormalizeCause(DiagnosticCauseInfo? cause)
    {
        var type = BoundOptional(cause?.Type, MaxCauseTypeLength);
        return type is null
            ? null
            : new DiagnosticCauseInfo(type)
            {
                Message = BoundOptional(cause!.Message, MaxCauseMessageLength),
                Details = BoundOptional(cause.Details, MaxCauseDetailsLength),
                MessageUnavailableReason = BoundOptional(
                    cause.MessageUnavailableReason,
                    MaxCauseMessageLength)
            };
    }

    private static bool TryGetExceptionMessage(
        Exception exception,
        IDictionary<Exception, ExceptionMessageRead> messages,
        out string message)
    {
        if (!messages.TryGetValue(exception, out var read))
        {
            try
            {
                read = new ExceptionMessageRead(
                    BoundOptional(exception.Message, MaxCauseMessageLength),
                    null);
            }
            catch (Exception messageException)
            {
                read = new ExceptionMessageRead(
                    null,
                    FormatMessageUnavailableReason(messageException));
            }

            messages.Add(exception, read);
        }

        message = read.Message ?? string.Empty;
        return read.Message is not null;
    }

    private static string FormatMessageUnavailableReason(Exception exception)
    {
        var type = Bound(
            exception.GetType().FullName ?? exception.GetType().Name,
            MaxCauseTypeLength,
            nameof(Exception));
        string? message;
        try
        {
            message = BoundOptional(exception.Message, MaxCauseMessageLength);
        }
        catch
        {
            message = null;
        }

        return Bound(
            message is null ? $"getter_threw: {type}" : $"getter_threw: {type}: {message}",
            MaxCauseMessageLength,
            "getter_threw");
    }

    private static Exception SelectDeepestCause(Exception root)
    {
        var traversed = EnumerateExceptions(root).ToArray();
        if (traversed.OfType<AgentRemoteException>().FirstOrDefault() is { } remote)
        {
            return remote;
        }

        var current = root;
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { current };
        for (var depth = 1; depth < MaxTraversedExceptions; depth++)
        {
            var next = current is AggregateException aggregate
                ? aggregate.InnerExceptions.FirstOrDefault()
                : current.InnerException;
            if (next is null || !visited.Add(next))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static Rect? NormalizeBounds(Rect? bounds)
    {
        if (bounds is not { } value ||
            !double.IsFinite(value.X) ||
            !double.IsFinite(value.Y) ||
            !double.IsFinite(value.Width) ||
            !double.IsFinite(value.Height))
        {
            return null;
        }

        return value;
    }

    private static string? NormalizeSessionId(string? value) =>
        value is { Length: 32 } && Guid.TryParseExact(value, "N", out _)
            ? value.ToLowerInvariant()
            : null;

    private static string? NormalizeElementId(string? value) =>
        value is not null && value.Length <= MaxIdentityLength && ElementIdRegex().IsMatch(value)
            ? value
            : null;

    private static string? NormalizeProcessInstanceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentityLength)
        {
            return null;
        }

        var parts = value.Split(':', 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 &&
               long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var started) && started > 0
            ? value
            : null;
    }

    private static string? ReadLeadingCode(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var inspectedLength = Math.Min(message.Length, MaxCodeLength + 1);
        var separator = message.AsSpan(0, inspectedLength).IndexOfAny(':', ' ');
        if (separator == 0 || (separator < 0 && message.Length > MaxCodeLength))
        {
            return null;
        }

        var code = separator < 0 ? message : message[..separator];
        return NormalizeToken(code);
    }

    private static OperationCanceledException CreateRequestCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        new(
            "The MCP tool request was canceled.",
            exception,
            cancellationToken);

    private static IEnumerable<Exception> EnumerateExceptions(Exception root)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0 && visited.Count < MaxTraversedExceptions)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;
            if (current is AggregateException aggregate)
            {
                var enqueueCount = Math.Min(
                    aggregate.InnerExceptions.Count,
                    MaxTraversedExceptions - visited.Count - pending.Count);
                for (var index = enqueueCount - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null &&
                     visited.Count + pending.Count < MaxTraversedExceptions)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    private readonly record struct ExceptionMessageRead(string? Message, string? UnavailableReason);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TruncatedReasonRegex();

    [GeneratedRegex("^(?:uia|wpf)_[A-Za-z0-9_-]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex ElementIdRegex();
}
