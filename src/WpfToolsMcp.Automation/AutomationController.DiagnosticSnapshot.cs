using System.Diagnostics;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private static readonly HashSet<DiagnosticSection> WpfDiagnosticSections =
    [
        DiagnosticSection.WpfProperties,
        DiagnosticSection.Layout,
        DiagnosticSection.Bindings,
        DiagnosticSection.DataContext,
        DiagnosticSection.BindingErrors
    ];

    public async Task<CaptureDiagnosticSnapshotResponse> CaptureDiagnosticSnapshotAsync(
        CaptureDiagnosticSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        request = DiagnosticSnapshotRequestValidator.Validate(request);
        var budget = request.Budget!;
        var clock = TimeProvider.System;
        var captureStartedAtUtc = clock.GetUtcNow();
        var captureStartedTimestamp = clock.GetTimestamp();
        var captureId = Guid.NewGuid().ToString("N");
        var trace = BeginTraceSpan("capture_diagnostic_snapshot");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);
        var operationToken = timeoutCts.Token;

        try
        {
            var scope = request.Locator is null && string.IsNullOrWhiteSpace(request.ElementId)
                ? DiagnosticTargetScope.Window
                : DiagnosticTargetScope.Element;
            var resolved = await ResolveDiagnosticSnapshotTargetAsync(request, operationToken).ConfigureAwait(false);
            var application = EnsureAttached();
            var window = await GetWindowMetadataAsync(resolved.WindowHandleUsed, operationToken).ConfigureAwait(false);
            var target = new DiagnosticSnapshotTarget(
                SessionId: request.SessionId,
                ProcessId: application.ProcessId,
                ProcessName: application.Name,
                WindowHandle: window.Handle,
                WindowTitle: window.Title,
                Scope: scope,
                AnchorBackend: resolved.BackendUsed,
                Element: resolved.Element);

            var captured = new Dictionary<DiagnosticSection, DiagnosticSectionResult>();
            var requestedWpfSections = request.Sections
                .Where(section => WpfDiagnosticSections.Contains(section) ||
                                  (section == DiagnosticSection.VisualTree && resolved.BackendUsed == InspectionBackend.Wpf))
                .ToArray();
            var wpfSectionsSingleDispatcherTurn = false;

            if (requestedWpfSections.Length > 0)
            {
                if (resolved.BackendUsed == InspectionBackend.Wpf)
                {
                    try
                    {
                        var wpfCapture = await CaptureWpfDiagnosticSnapshotAsync(
                            target,
                            requestedWpfSections,
                            budget,
                            request.PropertyNames,
                            request.DataContextProperties,
                            operationToken).ConfigureAwait(false);
                        wpfSectionsSingleDispatcherTurn = true;
                        target = target with { Element = wpfCapture.Target };
                        foreach (var result in wpfCapture.Sections)
                        {
                            captured[result.Section] = TranslateWpfTiming(result, captureStartedAtUtc);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var failures = await CaptureFailuresAsync(
                            requestedWpfSections,
                            ex,
                            captureStartedAtUtc,
                            captureStartedTimestamp,
                            operationToken,
                            clock).ConfigureAwait(false);
                        foreach (var failure in failures)
                        {
                            captured[failure.Section] = failure;
                        }
                    }
                }
                else
                {
                    var unavailable = new InvalidOperationException(
                        "wpf_backend_unavailable: the pinned target resolved through UIA, so WPF-only evidence is unavailable.");
                    var failures = await CaptureFailuresAsync(
                        requestedWpfSections,
                        unavailable,
                        captureStartedAtUtc,
                        captureStartedTimestamp,
                        operationToken,
                        clock).ConfigureAwait(false);
                    foreach (var failure in failures)
                    {
                        captured[failure.Section] = failure;
                    }
                }
            }

            var requestedUiaSections = request.Sections
                .Where(section => section == DiagnosticSection.UiaProperties ||
                                  (section == DiagnosticSection.VisualTree && resolved.BackendUsed == InspectionBackend.Uia))
                .ToArray();
            if (requestedUiaSections.Length > 0)
            {
                var results = await DiagnosticSnapshotCoordinator.CaptureAsync(
                    requestedUiaSections,
                    captureStartedAtUtc,
                    captureStartedTimestamp,
                    _ => DiagnosticCaptureSource.Uia,
                    GetEvidenceSchema,
                    section => $"uia-{section.ToString().ToLowerInvariant()}",
                    async (section, token) => section switch
                    {
                        DiagnosticSection.UiaProperties => ToEvidence(
                            await GetElementPropertiesAsync(
                                elementId: target.Element.ElementId,
                                maxProperties: budget.MaxItems,
                                cancellationToken: token).ConfigureAwait(false)),
                        DiagnosticSection.VisualTree => ToEvidence(
                            await GetVisualTreeAsync(
                                InspectionBackend.Uia,
                                target.WindowHandle,
                                new ElementLocator(XPath: target.Element.XPath),
                                budget.MaxDepth,
                                budget.MaxNodes,
                                visibleOnly: false,
                                includeOffViewport: true,
                                interactiveOnly: false,
                                InteractiveMode.Heuristic,
                                TreePreset.Minimal,
                                fields: null,
                                token).ConfigureAwait(false)),
                        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
                    },
                    ClassifySectionFailure,
                    operationToken,
                    clock).ConfigureAwait(false);
                foreach (var result in results)
                {
                    captured[result.Section] = result;
                }
            }

            if (request.Sections.Contains(DiagnosticSection.Screenshot))
            {
                var results = await DiagnosticSnapshotCoordinator.CaptureAsync(
                    [DiagnosticSection.Screenshot],
                    captureStartedAtUtc,
                    captureStartedTimestamp,
                    _ => DiagnosticCaptureSource.Screenshot,
                    GetEvidenceSchema,
                    _ => "screenshot-1",
                    async (_, token) => ToEvidence(
                        await TakeScreenshotAsync(
                            new TakeScreenshotRequest(
                                WindowHandle: target.WindowHandle,
                                ElementId: target.Element.ElementId,
                                Backend: target.AnchorBackend,
                                Format: ScreenshotImageFormat.Png,
                                AutoScroll: false,
                                FullyVisible: false,
                                ReturnBase64: false)
                            {
                                IncludeViewport = true
                            },
                            token,
                            autoInject: false).ConfigureAwait(false)),
                    ClassifySectionFailure,
                    operationToken,
                    clock).ConfigureAwait(false);
                captured[DiagnosticSection.Screenshot] = results[0];
            }

            var ordered = request.Sections.Select(section => captured[section]).ToArray();
            var bounded = DiagnosticSnapshotCoordinator.ApplyPayloadBudget(ordered, budget.MaxPayloadChars);
            var captureCompletedTimestamp = clock.GetTimestamp();
            var captureCompletedAtUtc = captureStartedAtUtc + clock.GetElapsedTime(captureStartedTimestamp, captureCompletedTimestamp);
            var timingSkewMs = bounded.Count == 0
                ? 0
                : Math.Max(0, bounded.Max(section => section.CompletedOffsetMs) - bounded.Min(section => section.StartedOffsetMs));
            var crossBackendAtomic = bounded.Count <= 1 ||
                                     (wpfSectionsSingleDispatcherTurn &&
                                      bounded.All(section => section.Source == DiagnosticCaptureSource.WpfDispatcher));

            var response = new CaptureDiagnosticSnapshotResponse(
                CaptureId: captureId,
                Target: target,
                Budget: budget,
                StartedAtUtc: captureStartedAtUtc,
                CompletedAtUtc: captureCompletedAtUtc,
                DurationMs: ToElapsedMilliseconds(clock.GetElapsedTime(captureStartedTimestamp, captureCompletedTimestamp)),
                Consistency: new DiagnosticSnapshotConsistency(
                    SessionSerialized: true,
                    WpfSectionsSingleDispatcherTurn: wpfSectionsSingleDispatcherTurn,
                    CrossBackendAtomic: crossBackendAtomic,
                    TimingSkewMs: timingSkewMs),
                Sections: bounded);
            trace?.SetSummary(
                $"sections={bounded.Count} success={bounded.Count(section => section.Status == DiagnosticSectionStatus.Success)} " +
                $"truncated={bounded.Count(section => section.Status == DiagnosticSectionStatus.Truncated)}");
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"capture_diagnostic_snapshot timed out after {request.TimeoutMs} ms.",
                ex);
            trace?.SetError(timeout);
            throw timeout;
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    private async Task<ResolveElementResponse> ResolveDiagnosticSnapshotTargetAsync(
        CaptureDiagnosticSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ElementId))
        {
            var elementId = request.ElementId.Trim();
            var handle = RequireHandle(elementId);
            if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            _ = await GetWindowMetadataAsync(handle.WindowHandle, cancellationToken).ConfigureAwait(false);
            return new ResolveElementResponse(
                handle.Backend,
                new ElementRef(
                    Type: handle.Type ?? "Unknown",
                    AutomationId: handle.AutomationId,
                    Name: handle.Name,
                    XPath: handle.XPath,
                    ClassName: handle.ClassName,
                    Bounds: handle.Bounds,
                    ElementId: elementId),
                handle.WindowHandle);
        }

        var resolutionBackend = SelectDiagnosticSnapshotResolutionBackend(request.Sections);
        return await ResolveElementAsync(
            resolutionBackend,
            request.Locator ?? new ElementLocator(XPath: "/Window"),
            request.WindowHandle,
            timeoutMs: request.TimeoutMs,
            pollIntervalMs: 100,
            stableMs: 0,
            visibleOnly: false,
            includeOffViewport: true,
            interactiveOnly: false,
            InteractiveMode.Heuristic,
            cancellationToken,
            autoInject: resolutionBackend == InspectionBackend.Auto).ConfigureAwait(false);
    }

    private async Task<CaptureWpfDiagnosticSnapshotResponse> CaptureWpfDiagnosticSnapshotAsync(
        DiagnosticSnapshotTarget target,
        IReadOnlyList<DiagnosticSection> sections,
        DiagnosticSnapshotBudget budget,
        IReadOnlyList<string>? propertyNames,
        IReadOnlyList<string>? dataContextProperties,
        CancellationToken cancellationToken)
    {
        var publicElementId = target.Element.ElementId
            ?? throw new InvalidOperationException("capture_diagnostic_snapshot requires a resolved element handle.");
        var agentTarget = PrepareWpfAgentTarget(
            "capture_diagnostic_snapshot",
            locator: null,
            elementId: publicElementId,
            target.WindowHandle);
        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);

        var request = new CaptureWpfDiagnosticSnapshotRequest(
            WindowHandle: agentTarget.WindowHandle,
            Locator: agentTarget.Locator,
            ElementId: agentTarget.AgentElementId,
            RootXPath: target.Element.XPath,
            Sections: sections,
            Budget: budget,
            PropertyNames: propertyNames,
            DataContextProperties: dataContextProperties);
        var fallbackRequest = agentTarget.RecoveryLocator is null
            ? null
            : request with { Locator = agentTarget.RecoveryLocator, ElementId = null };
        var response = await CallCaptureDiagnosticSnapshotWhenSupportedAsync(
            GetAgentCapabilities(client),
            () => CallWpfAgentTargetAsync<CaptureWpfDiagnosticSnapshotResponse>(
                client,
                AgentProtocolCapabilities.CaptureDiagnosticSnapshot,
                request,
                fallbackRequest,
                agentTarget,
                cancellationToken)).ConfigureAwait(false);
        var normalizedTarget = await StripAgentElementIdAsync(
            client,
            response.Target,
            publicElementId).ConfigureAwait(false);
        return response with { Target = normalizedTarget with { ElementId = publicElementId } };
    }

    private static async Task<IReadOnlyList<DiagnosticSectionResult>> CaptureFailuresAsync(
        IReadOnlyList<DiagnosticSection> sections,
        Exception failure,
        DateTimeOffset captureStartedAtUtc,
        long captureStartedTimestamp,
        CancellationToken cancellationToken,
        TimeProvider clock) =>
        await DiagnosticSnapshotCoordinator.CaptureAsync(
            sections,
            captureStartedAtUtc,
            captureStartedTimestamp,
            _ => DiagnosticCaptureSource.WpfDispatcher,
            GetEvidenceSchema,
            _ => "wpf-dispatcher-1",
            (_, _) => Task.FromException<DiagnosticSectionEvidence>(failure),
            ClassifySectionFailure,
            cancellationToken,
            clock).ConfigureAwait(false);

    private static DiagnosticSectionEvidence ToEvidence(GetElementPropertiesResponse response) =>
        new(DiagnosticSnapshotCoordinator.SerializeEvidence(response), response.Truncated, response.TruncatedReason);

    private static DiagnosticSectionEvidence ToEvidence(GetVisualTreeResponse response) =>
        new(DiagnosticSnapshotCoordinator.SerializeEvidence(response), response.Truncated, response.TruncatedReason);

    private static DiagnosticSectionEvidence ToEvidence(TakeScreenshotResponse response) =>
        new(DiagnosticSnapshotCoordinator.SerializeEvidence(response));

    private static DiagnosticSectionResult TranslateWpfTiming(
        DiagnosticSectionResult result,
        DateTimeOffset captureStartedAtUtc)
    {
        var startedOffset = ToElapsedMilliseconds(result.StartedAtUtc - captureStartedAtUtc);
        var completedOffset = ToElapsedMilliseconds(result.CompletedAtUtc - captureStartedAtUtc);
        return result with
        {
            StartedOffsetMs = startedOffset,
            CompletedOffsetMs = completedOffset,
            DurationMs = Math.Max(0, completedOffset - startedOffset)
        };
    }

    private static DiagnosticSectionFailure ClassifySectionFailure(Exception exception)
    {
        var message = exception.GetBaseException().Message ?? exception.Message;
        message = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
        if (message.Length > DiagnosticSnapshotLimits.MaxFailureMessageLength)
        {
            message = message[..(DiagnosticSnapshotLimits.MaxFailureMessageLength - 3)] + "...";
        }

        var unavailable = message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("does not support", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("not a WPF handle", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("agent connection", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("inject", StringComparison.OrdinalIgnoreCase);
        return new DiagnosticSectionFailure(
            unavailable ? DiagnosticSectionStatus.Unavailable : DiagnosticSectionStatus.Failed,
            unavailable ? "evidenceUnavailable" : "sectionFailed",
            message);
    }

    private static string GetEvidenceSchema(DiagnosticSection section) => section switch
    {
        DiagnosticSection.VisualTree => "get_visual_tree/v1",
        DiagnosticSection.UiaProperties => "get_element_properties/v1",
        DiagnosticSection.WpfProperties => "get_computed_properties/v1",
        DiagnosticSection.Layout => "get_layout_context/v1",
        DiagnosticSection.Bindings => "get_binding_info/v1",
        DiagnosticSection.DataContext => "get_data_context/v1",
        DiagnosticSection.BindingErrors => "get_binding_errors/v1",
        DiagnosticSection.Screenshot => "take_screenshot/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };

    private static long ToElapsedMilliseconds(TimeSpan value) =>
        Math.Max(0, (long)Math.Ceiling(value.TotalMilliseconds));

    internal static Task<T> CallCaptureDiagnosticSnapshotWhenSupportedAsync<T>(
        AgentCapabilitiesResponse? capabilities,
        Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return capabilities?.Capabilities is not null &&
               capabilities.Capabilities.Contains(
                   AgentProtocolCapabilities.CaptureDiagnosticSnapshot,
                   StringComparer.Ordinal)
            ? call()
            : Task.FromException<T>(new InvalidOperationException(
                "agent_capability_unavailable: the injected agent does not support coherent diagnostic snapshots. " +
                "Restart the target application and attach a new session."));
    }

    internal static InspectionBackend SelectDiagnosticSnapshotResolutionBackend(
        IReadOnlyList<DiagnosticSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return sections.Any(WpfDiagnosticSections.Contains)
            ? InspectionBackend.Auto
            : InspectionBackend.Uia;
    }
}
