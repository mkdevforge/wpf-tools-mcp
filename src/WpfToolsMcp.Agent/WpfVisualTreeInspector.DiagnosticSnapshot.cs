using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const string WpfDiagnosticCaptureGroup = "wpf-dispatcher-1";

    private static readonly JsonSerializerOptions DiagnosticSnapshotJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<DiagnosticSection> SupportedWpfDiagnosticSections =
    [
        DiagnosticSection.VisualTree,
        DiagnosticSection.WpfProperties,
        DiagnosticSection.Layout,
        DiagnosticSection.Bindings,
        DiagnosticSection.DataContext,
        DiagnosticSection.BindingErrors
    ];

    public static CaptureWpfDiagnosticSnapshotResponse CaptureDiagnosticSnapshot(
        string ownerId,
        CaptureWpfDiagnosticSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var budget = NormalizeDiagnosticSnapshotBudget(request.Budget);
        var requestedSections = ValidateWpfDiagnosticSections(request.Sections);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var captureStartTimestamp = Stopwatch.GetTimestamp();

        var window = ResolveWindow(request.WindowHandle);
        var resolvedWindowHandle = new WindowInteropHelper(window).Handle.ToInt64();
        if (resolvedWindowHandle == 0)
        {
            resolvedWindowHandle = request.WindowHandle.GetValueOrDefault();
        }

        if (resolvedWindowHandle == 0)
        {
            throw new InvalidOperationException("wpf_window_not_found: the target WPF window does not have a native handle.");
        }

        using var treeService = new VisualTreeService();
        var resolved = ResolveDiagnosticSnapshotTarget(
            ownerId,
            request,
            window,
            treeService,
            resolvedWindowHandle,
            budget.MaxNodes,
            cancellationToken);
        var target = BuildElementRefWpf(
            ownerId,
            resolved.Element,
            resolved.XPath,
            FindReturnFields.Standard);
        var targetAgentElementId = target.ElementIdWpf
            ?? throw new InvalidOperationException("Failed to register the diagnostic snapshot target.");

        var (propertyNames, propertyNamesTruncated) = PrepareDiagnosticPropertyNames(
            request.PropertyNames,
            budget.MaxItems);
        var (dataContextProperties, dataContextPropertiesTruncated) = PrepareDiagnosticPropertyNames(
            request.DataContextProperties,
            budget.MaxItems);

        var results = new List<DiagnosticSectionResult>(requestedSections.Count);

        foreach (var section in requestedSections)
        {
            // The server owns the one global request-order budget after UIA, WPF,
            // and screenshot phases are combined. The pipe applies the same cap
            // independently to each WPF section so it cannot pre-drop a later
            // section that would fit in the final cross-phase order.
            var remainingPayloadChars = budget.MaxPayloadChars;
            results.Add(section switch
            {
                DiagnosticSection.VisualTree => CaptureWpfDiagnosticSection(
                    section,
                    evidenceSchema: "get_visual_tree/v1",
                    startedAtUtc,
                    captureStartTimestamp,
                    budget.MaxValueLength,
                    ref remainingPayloadChars,
                    capture: () => GetVisualTree(
                        ownerId,
                        new GetWpfVisualTreeRequestV2(
                            WindowHandle: resolvedWindowHandle,
                            RootXPath: resolved.XPath,
                            Depth: budget.MaxDepth,
                            MaxNodes: budget.MaxNodes,
                            VisibleOnly: false,
                            IncludeOffViewport: true,
                            InteractiveOnly: false,
                            InteractiveMode: InteractiveMode.Heuristic,
                            Preset: TreePreset.Standard),
                        cancellationToken),
                    normalize: static response => response,
                    isTruncated: static response => response.Truncated,
                    truncatedReason: static response => response.TruncatedReason),

                DiagnosticSection.WpfProperties => CaptureWpfDiagnosticSection(
                    section,
                    evidenceSchema: "get_computed_properties/v1",
                    startedAtUtc,
                    captureStartTimestamp,
                    budget.MaxValueLength,
                    ref remainingPayloadChars,
                    capture: () =>
                    {
                        var response = GetComputedProperties(
                            ownerId,
                            new GetComputedPropertiesRequest(
                                WindowHandle: resolvedWindowHandle,
                                Locator: null,
                                ElementId: targetAgentElementId,
                                PropertyNames: propertyNames,
                                IncludeSources: true,
                                IncludeDefault: false,
                                IncludeUnset: false,
                                MaxProperties: budget.MaxItems,
                                ValueFormat: "string",
                                IncludeProvenance: false,
                                MaxProvenanceCandidates: 0),
                            cancellationToken,
                            visibleOnly: false,
                            maxNodes: budget.MaxNodes);

                        if (propertyNamesTruncated)
                        {
                            response = response with
                            {
                                Truncated = true,
                                TruncatedReason = response.TruncatedReason ?? "propertyNamesBudget",
                                Warnings = AppendDiagnosticWarning(response.Warnings, "propertyNamesBudget")
                            };
                        }

                        return response;
                    },
                    normalize: response => NormalizeDiagnosticComputedProperties(
                        response,
                        budget.MaxValueLength),
                    isTruncated: static response => response.Truncated,
                    truncatedReason: static response => response.TruncatedReason),

                DiagnosticSection.Layout => CaptureWpfDiagnosticSection(
                    section,
                    evidenceSchema: "get_layout_context/v1",
                    startedAtUtc,
                    captureStartTimestamp,
                    budget.MaxValueLength,
                    ref remainingPayloadChars,
                    capture: () => GetLayoutContext(
                        ownerId,
                        new GetLayoutContextRequest(
                            WindowHandle: resolvedWindowHandle,
                            Locator: null,
                            ElementId: targetAgentElementId,
                            MaxAncestors: Math.Min(budget.MaxDepth, budget.MaxItems),
                            MaxSiblings: budget.MaxItems,
                            MaxGridDefinitions: budget.MaxItems),
                        cancellationToken,
                        maxResolutionNodes: budget.MaxNodes),
                    normalize: static response => response with
                    {
                        Element = StripDiagnosticAgentIds(response.Element)
                    },
                    isTruncated: static response => response.Truncated,
                    truncatedReason: static response => response.TruncatedReason),

                DiagnosticSection.Bindings => CaptureWpfDiagnosticSection(
                    section,
                    evidenceSchema: "get_binding_info/v1",
                    startedAtUtc,
                    captureStartTimestamp,
                    budget.MaxValueLength,
                    ref remainingPayloadChars,
                    capture: () => GetBindingInfo(
                        ownerId,
                        new GetBindingInfoRequest(
                            WindowHandle: resolvedWindowHandle,
                            Locator: null,
                            ElementId: targetAgentElementId,
                            IncludeUnbound: false,
                            MaxProperties: budget.MaxItems,
                            ValueFormat: "string"),
                        cancellationToken,
                        visibleOnly: false,
                        maxNodes: budget.MaxNodes),
                    normalize: response => NormalizeDiagnosticBindings(
                        response,
                        budget.MaxValueLength),
                    isTruncated: static response => response.Truncated,
                    truncatedReason: static response => response.TruncatedReason),

                DiagnosticSection.DataContext => CaptureWpfDiagnosticSection(
                    section,
                    evidenceSchema: "get_data_context/v1",
                    startedAtUtc,
                    captureStartTimestamp,
                    budget.MaxValueLength,
                    ref remainingPayloadChars,
                    capture: () =>
                    {
                        var response = GetDataContext(
                            ownerId,
                            new GetDataContextRequest(
                                WindowHandle: resolvedWindowHandle,
                                Locator: null,
                                ElementId: targetAgentElementId,
                                Mode: DataContextMode.Summary,
                                MaxDepth: budget.MaxDepth,
                                MaxPropertiesPerObject: budget.MaxItems,
                                MaxStringLength: budget.MaxValueLength,
                                IncludeNulls: false,
                                IncludeFrameworkProperties: false,
                                PropertyAllowList: dataContextProperties),
                            cancellationToken,
                            visibleOnly: false,
                            maxNodes: budget.MaxNodes);

                        if (dataContextPropertiesTruncated)
                        {
                            response = response with
                            {
                                Truncated = true,
                                Warnings = AppendDiagnosticWarning(response.Warnings, "dataContextPropertiesBudget")
                            };
                        }

                        return response;
                    },
                    normalize: static response => response,
                    isTruncated: static response => response.Truncated,
                    truncatedReason: static response => response.Truncated ? "sectionBudget" : null),

                DiagnosticSection.BindingErrors => CaptureWpfDiagnosticSection(
                    section,
                    evidenceSchema: "get_binding_errors/v1",
                    startedAtUtc,
                    captureStartTimestamp,
                    budget.MaxValueLength,
                    ref remainingPayloadChars,
                    capture: () => GetBindingErrors(
                        ownerId,
                        new GetBindingErrorsRequest(
                            WindowHandle: resolvedWindowHandle,
                            RootXPath: resolved.XPath,
                            Depth: budget.MaxDepth,
                            MaxErrors: budget.MaxItems,
                            MaxNodes: budget.MaxNodes),
                        cancellationToken),
                    normalize: static response => response,
                    isTruncated: static response => response.Truncated,
                    truncatedReason: static response => response.TruncatedReason),

                _ => throw new UnreachableException()
            });
        }

        return new CaptureWpfDiagnosticSnapshotResponse(
            Target: target,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: startedAtUtc.AddMilliseconds(GetElapsedMilliseconds(captureStartTimestamp)),
            Sections: results);
    }

    private static (DependencyObject Element, string XPath) ResolveDiagnosticSnapshotTarget(
        string ownerId,
        CaptureWpfDiagnosticSnapshotRequest request,
        Window window,
        VisualTreeService treeService,
        long windowHandle,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator && hasElementId)
        {
            throw new ArgumentException("invalid_request: provide at most one of locator or elementId.");
        }

        if (!hasLocator && !hasElementId)
        {
            var rootXPath = string.IsNullOrWhiteSpace(request.RootXPath)
                ? "/Window"
                : NormalizeXPath(request.RootXPath);
            if (string.Equals(rootXPath, "/Window", StringComparison.OrdinalIgnoreCase))
            {
                return (window, "/Window");
            }

            return (
                ResolveByXPath(treeService, window, rootXPath, visibleOnly: false, cancellationToken),
                rootXPath);
        }

        return ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            request.Locator,
            request.ElementId,
            windowHandle,
            visibleOnly: false,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);
    }

    private static DiagnosticSectionResult CaptureWpfDiagnosticSection<T>(
        DiagnosticSection section,
        string evidenceSchema,
        DateTimeOffset captureStartedAtUtc,
        long captureStartTimestamp,
        int maxEvidenceValueLength,
        ref int remainingPayloadChars,
        Func<T> capture,
        Func<T, T> normalize,
        Func<T, bool> isTruncated,
        Func<T, string?> truncatedReason)
    {
        var sectionStartTimestamp = Stopwatch.GetTimestamp();
        var startedOffsetMs = GetElapsedMilliseconds(captureStartTimestamp);
        var sectionStartedAtUtc = captureStartedAtUtc.AddMilliseconds(startedOffsetMs);

        if (remainingPayloadChars <= 0)
        {
            return CreatePayloadBudgetResult(
                section,
                evidenceSchema,
                sectionStartedAtUtc,
                captureStartTimestamp,
                sectionStartTimestamp,
                startedOffsetMs);
        }

        try
        {
            var response = normalize(capture());
            var data = JsonSerializer.SerializeToNode(response, DiagnosticSnapshotJsonOptions);
            var evidenceValuesTruncated = TruncateDiagnosticEvidenceValues(
                data,
                maxEvidenceValueLength);
            if (evidenceValuesTruncated)
            {
                MarkDiagnosticEvidenceValueTruncation<T>(data);
            }

            var payloadChars = data?.ToJsonString(DiagnosticSnapshotJsonOptions).Length ?? 0;
            if (payloadChars > remainingPayloadChars)
            {
                return CreatePayloadBudgetResult(
                    section,
                    evidenceSchema,
                    sectionStartedAtUtc,
                    captureStartTimestamp,
                    sectionStartTimestamp,
                    startedOffsetMs);
            }

            remainingPayloadChars -= payloadChars;
            var completedOffsetMs = GetElapsedMilliseconds(captureStartTimestamp);
            var sectionCompletedAtUtc = captureStartedAtUtc.AddMilliseconds(completedOffsetMs);
            var responseTruncated = isTruncated(response);
            var truncated = responseTruncated || evidenceValuesTruncated;
            return new DiagnosticSectionResult(
                Section: section,
                Status: truncated ? DiagnosticSectionStatus.Truncated : DiagnosticSectionStatus.Success,
                Source: DiagnosticCaptureSource.WpfDispatcher,
                EvidenceSchema: evidenceSchema,
                CaptureGroup: WpfDiagnosticCaptureGroup,
                StartedAtUtc: sectionStartedAtUtc,
                CompletedAtUtc: sectionCompletedAtUtc,
                StartedOffsetMs: startedOffsetMs,
                CompletedOffsetMs: completedOffsetMs,
                DurationMs: GetElapsedMilliseconds(sectionStartTimestamp),
                Data: data,
                Code: truncated
                    ? responseTruncated
                        ? truncatedReason(response) ?? "sectionBudget"
                        : "maxValueLength"
                    : null,
                PayloadChars: payloadChars);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var completedOffsetMs = GetElapsedMilliseconds(captureStartTimestamp);
            var sectionCompletedAtUtc = captureStartedAtUtc.AddMilliseconds(completedOffsetMs);
            var message = GetBoundedDiagnosticFailureMessage(ex, maxEvidenceValueLength);
            return new DiagnosticSectionResult(
                Section: section,
                Status: DiagnosticSectionStatus.Failed,
                Source: DiagnosticCaptureSource.WpfDispatcher,
                EvidenceSchema: evidenceSchema,
                CaptureGroup: WpfDiagnosticCaptureGroup,
                StartedAtUtc: sectionStartedAtUtc,
                CompletedAtUtc: sectionCompletedAtUtc,
                StartedOffsetMs: startedOffsetMs,
                CompletedOffsetMs: completedOffsetMs,
                DurationMs: GetElapsedMilliseconds(sectionStartTimestamp),
                Code: GetDiagnosticFailureCode(message),
                Message: message);
        }
    }

    private static DiagnosticSectionResult CreatePayloadBudgetResult(
        DiagnosticSection section,
        string evidenceSchema,
        DateTimeOffset sectionStartedAtUtc,
        long captureStartTimestamp,
        long sectionStartTimestamp,
        long startedOffsetMs)
    {
        var completedOffsetMs = GetElapsedMilliseconds(captureStartTimestamp);
        var completedAtUtc = sectionStartedAtUtc.AddMilliseconds(
            Math.Max(0, completedOffsetMs - startedOffsetMs));
        return new DiagnosticSectionResult(
            Section: section,
            Status: DiagnosticSectionStatus.Truncated,
            Source: DiagnosticCaptureSource.WpfDispatcher,
            EvidenceSchema: evidenceSchema,
            CaptureGroup: WpfDiagnosticCaptureGroup,
            StartedAtUtc: sectionStartedAtUtc,
            CompletedAtUtc: completedAtUtc,
            StartedOffsetMs: startedOffsetMs,
            CompletedOffsetMs: completedOffsetMs,
            DurationMs: GetElapsedMilliseconds(sectionStartTimestamp),
            Code: "maxPayloadChars",
            Message: "Section evidence exceeded the remaining diagnostic payload budget and was omitted.");
    }

    private static DiagnosticSnapshotBudget NormalizeDiagnosticSnapshotBudget(DiagnosticSnapshotBudget? budget)
    {
        budget ??= new DiagnosticSnapshotBudget();
        return new DiagnosticSnapshotBudget(
            MaxDepth: Math.Clamp(budget.MaxDepth, DiagnosticSnapshotLimits.MinDepth, DiagnosticSnapshotLimits.MaxDepth),
            MaxItems: Math.Clamp(budget.MaxItems, DiagnosticSnapshotLimits.MinItems, DiagnosticSnapshotLimits.MaxItems),
            MaxNodes: Math.Clamp(budget.MaxNodes, DiagnosticSnapshotLimits.MinNodes, DiagnosticSnapshotLimits.MaxNodes),
            MaxValueLength: Math.Clamp(budget.MaxValueLength, DiagnosticSnapshotLimits.MinValueLength, DiagnosticSnapshotLimits.MaxValueLength),
            MaxPayloadChars: Math.Clamp(budget.MaxPayloadChars, DiagnosticSnapshotLimits.MinPayloadChars, DiagnosticSnapshotLimits.MaxPayloadChars));
    }

    private static IReadOnlyList<DiagnosticSection> ValidateWpfDiagnosticSections(
        IReadOnlyList<DiagnosticSection>? sections)
    {
        if (sections is null || sections.Count is < 1 || sections.Count > SupportedWpfDiagnosticSections.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sections),
                $"sections must contain between 1 and {SupportedWpfDiagnosticSections.Count} WPF entries.");
        }

        var result = sections.ToArray();
        if (result.Any(section => !Enum.IsDefined(section) || !SupportedWpfDiagnosticSections.Contains(section)))
        {
            throw new ArgumentOutOfRangeException(nameof(sections), "sections contains a non-WPF or unsupported value.");
        }

        if (result.Distinct().Count() != result.Length)
        {
            throw new ArgumentException("sections must not contain duplicates.", nameof(sections));
        }

        return result;
    }

    private static (IReadOnlyList<string>? Values, bool Truncated) PrepareDiagnosticPropertyNames(
        IReadOnlyList<string>? values,
        int maxItems)
    {
        if (values is null)
        {
            return (null, false);
        }

        var maxCount = Math.Min(maxItems, DiagnosticSnapshotLimits.MaxPropertyNames);
        var result = new List<string>(Math.Min(values.Count, maxCount));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim();
            if (normalized.Length > DiagnosticSnapshotLimits.MaxPropertyNameLength)
            {
                truncated = true;
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            if (result.Count >= maxCount)
            {
                truncated = true;
                continue;
            }

            result.Add(normalized);
        }

        return (result, truncated);
    }

    private static IReadOnlyList<string> AppendDiagnosticWarning(
        IReadOnlyList<string>? warnings,
        string warning)
    {
        var result = warnings is null ? new List<string>() : new List<string>(warnings);
        result.Add(warning);
        return result;
    }

    private static GetComputedPropertiesResponse NormalizeDiagnosticComputedProperties(
        GetComputedPropertiesResponse response,
        int maxValueLength)
    {
        var properties = new ComputedPropertyInfo[response.Properties.Count];
        var valueTruncated = false;
        for (var index = 0; index < properties.Length; index++)
        {
            var property = response.Properties[index];
            var value = TruncateDiagnosticEvidenceValue(
                property.Value,
                maxValueLength,
                out var propertyValueTruncated);
            valueTruncated |= propertyValueTruncated;
            properties[index] = property with { Value = value };
        }

        return response with
        {
            Element = StripDiagnosticAgentIds(response.Element),
            Properties = properties,
            Truncated = response.Truncated || valueTruncated,
            TruncatedReason = response.TruncatedReason ?? (valueTruncated ? "maxValueLength" : null)
        };
    }

    private static GetBindingInfoResponse NormalizeDiagnosticBindings(
        GetBindingInfoResponse response,
        int maxValueLength)
    {
        var bindings = new BindingInfo[response.Bindings.Count];
        var valueTruncated = false;
        for (var index = 0; index < bindings.Length; index++)
        {
            var binding = response.Bindings[index];
            var currentValue = TruncateDiagnosticEvidenceValue(
                binding.CurrentValue,
                maxValueLength,
                out var bindingValueTruncated);
            valueTruncated |= bindingValueTruncated;
            bindings[index] = binding with { CurrentValue = currentValue };
        }

        return response with
        {
            Element = StripDiagnosticAgentIds(response.Element),
            Bindings = bindings,
            Truncated = response.Truncated || valueTruncated,
            TruncatedReason = response.TruncatedReason ?? (valueTruncated ? "maxValueLength" : null)
        };
    }

    private static string? TruncateDiagnosticEvidenceValue(
        string? value,
        int maxValueLength,
        out bool truncated)
    {
        truncated = value is not null && value.Length > maxValueLength;
        if (!truncated)
        {
            return value;
        }

        const string suffix = "...";
        var take = Math.Max(0, maxValueLength - suffix.Length);
        if (take > 0 && char.IsHighSurrogate(value![take - 1]))
        {
            take--;
        }

        return value![..take] + suffix;
    }

    private static bool TruncateDiagnosticEvidenceValues(JsonNode? node, int maxValueLength)
    {
        var truncated = false;
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var propertyName in jsonObject.Select(property => property.Key).ToArray())
                {
                    var value = jsonObject[propertyName];
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                    {
                        jsonObject[propertyName] = TruncateDiagnosticEvidenceValue(
                            text,
                            maxValueLength,
                            out var valueTruncated);
                        truncated |= valueTruncated;
                    }
                    else
                    {
                        truncated |= TruncateDiagnosticEvidenceValues(value, maxValueLength);
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var value = jsonArray[index];
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                    {
                        jsonArray[index] = TruncateDiagnosticEvidenceValue(
                            text,
                            maxValueLength,
                            out var valueTruncated);
                        truncated |= valueTruncated;
                    }
                    else
                    {
                        truncated |= TruncateDiagnosticEvidenceValues(value, maxValueLength);
                    }
                }

                break;
        }

        return truncated;
    }

    private static void MarkDiagnosticEvidenceValueTruncation<T>(JsonNode? data)
    {
        if (data is not JsonObject jsonObject || !jsonObject.ContainsKey("truncated"))
        {
            return;
        }

        jsonObject["truncated"] = true;
        if (typeof(T).GetProperty("TruncatedReason") is not null &&
            (!jsonObject.TryGetPropertyValue("truncatedReason", out var reason) || reason is null))
        {
            jsonObject["truncatedReason"] = "maxValueLength";
        }
    }

    private static ElementRef StripDiagnosticAgentIds(ElementRef element) =>
        element with
        {
            ElementId = null,
            ElementIdUia = null,
            ElementIdWpf = null
        };

    private static string GetBoundedDiagnosticFailureMessage(Exception exception, int requestedMaxLength)
    {
        var message = exception.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = exception.GetBaseException().GetType().Name;
        }

        message = ScrubDiagnosticFailureMessage(message.Trim());
        var maxLength = Math.Clamp(
            requestedMaxLength,
            DiagnosticSnapshotLimits.MinValueLength,
            DiagnosticSnapshotLimits.MaxFailureMessageLength);
        if (message.Length <= maxLength)
        {
            return message;
        }

        var take = Math.Max(0, maxLength - 3);
        if (take > 0 && char.IsHighSurrogate(message[take - 1]))
        {
            take--;
        }

        return message[..take] + "...";
    }

    internal static string ScrubDiagnosticFailureMessage(string message)
    {
        const string privateIdPrefix = "wpfobj_";
        const int privateIdValueLength = 16;
        const string replacement = "[private-wpf-id]";

        var matchStart = message.IndexOf(privateIdPrefix, StringComparison.Ordinal);
        if (matchStart < 0)
        {
            return message;
        }

        var result = new StringBuilder(message.Length);
        var copyStart = 0;
        while (matchStart >= 0)
        {
            result.Append(message, copyStart, matchStart - copyStart);
            result.Append(replacement);

            var matchEnd = matchStart + privateIdPrefix.Length;
            var maximumMatchEnd = Math.Min(message.Length, matchEnd + privateIdValueLength);
            while (matchEnd < maximumMatchEnd && IsPrivateWpfElementIdCharacter(message[matchEnd]))
            {
                matchEnd++;
            }

            copyStart = matchEnd;
            matchStart = message.IndexOf(privateIdPrefix, copyStart, StringComparison.Ordinal);
        }

        result.Append(message, copyStart, message.Length - copyStart);
        return result.ToString();
    }

    private static bool IsPrivateWpfElementIdCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

    private static string GetDiagnosticFailureCode(string message)
    {
        var separator = message.IndexOf(':');
        if (separator is <= 0 or > 64)
        {
            return "section_failed";
        }

        var candidate = message[..separator];
        return candidate.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? candidate
            : "section_failed";
    }

    private static long GetElapsedMilliseconds(long startTimestamp) =>
        Math.Max(
            0,
            (long)Math.Round(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                MidpointRounding.AwayFromZero));
}
