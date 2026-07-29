using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal sealed record DiagnosticSectionEvidence(
    JsonNode? Data,
    bool Truncated = false,
    string? Code = null,
    string? Message = null);

internal sealed record DiagnosticSectionFailure(
    DiagnosticSectionStatus Status,
    string Code,
    string Message);

internal static class DiagnosticSnapshotCoordinator
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<IReadOnlyList<DiagnosticSectionResult>> CaptureAsync(
        IReadOnlyList<DiagnosticSection> sections,
        DateTimeOffset captureStartedAtUtc,
        long captureStartedTimestamp,
        Func<DiagnosticSection, DiagnosticCaptureSource> source,
        Func<DiagnosticSection, string> evidenceSchema,
        Func<DiagnosticSection, string> captureGroup,
        Func<DiagnosticSection, CancellationToken, Task<DiagnosticSectionEvidence>> capture,
        Func<Exception, DiagnosticSectionFailure> classifyFailure,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(evidenceSchema);
        ArgumentNullException.ThrowIfNull(captureGroup);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(classifyFailure);

        var clock = timeProvider ?? TimeProvider.System;
        var results = new List<DiagnosticSectionResult>(sections.Count);
        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startedTimestamp = clock.GetTimestamp();
            var startedAtUtc = captureStartedAtUtc + clock.GetElapsedTime(captureStartedTimestamp, startedTimestamp);

            try
            {
                var evidence = await capture(section, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var completedTimestamp = clock.GetTimestamp();
                var completedAtUtc = captureStartedAtUtc + clock.GetElapsedTime(captureStartedTimestamp, completedTimestamp);
                results.Add(CreateResult(
                    section,
                    evidence.Truncated ? DiagnosticSectionStatus.Truncated : DiagnosticSectionStatus.Success,
                    source(section),
                    evidenceSchema(section),
                    captureGroup(section),
                    startedAtUtc,
                    completedAtUtc,
                    captureStartedTimestamp,
                    startedTimestamp,
                    completedTimestamp,
                    evidence.Data,
                    evidence.Code,
                    evidence.Message,
                    clock));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var completedTimestamp = clock.GetTimestamp();
                var completedAtUtc = captureStartedAtUtc + clock.GetElapsedTime(captureStartedTimestamp, completedTimestamp);
                var failure = classifyFailure(ex);
                results.Add(CreateResult(
                    section,
                    failure.Status,
                    source(section),
                    evidenceSchema(section),
                    captureGroup(section),
                    startedAtUtc,
                    completedAtUtc,
                    captureStartedTimestamp,
                    startedTimestamp,
                    completedTimestamp,
                    data: null,
                    failure.Code,
                    failure.Message,
                    clock));
            }
        }

        return results;
    }

    public static JsonNode? SerializeEvidence<T>(T value) =>
        JsonSerializer.SerializeToNode(value, EvidenceJsonOptions);

    public static IReadOnlyList<DiagnosticSectionResult> ApplyPayloadBudget(
        IReadOnlyList<DiagnosticSectionResult> sections,
        int maxPayloadChars)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var remaining = maxPayloadChars;
        var bounded = new DiagnosticSectionResult[sections.Count];

        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            if (section.Data is null)
            {
                bounded[index] = section with { PayloadChars = 0 };
                continue;
            }

            var payloadChars = section.Data.ToJsonString(EvidenceJsonOptions).Length;
            if (payloadChars <= remaining)
            {
                remaining -= payloadChars;
                bounded[index] = section with { PayloadChars = payloadChars };
                continue;
            }

            bounded[index] = section with
            {
                Status = DiagnosticSectionStatus.Truncated,
                Data = null,
                Code = "maxPayloadChars",
                Message = $"Captured evidence was omitted because the remaining payload budget was {remaining} characters.",
                PayloadChars = 0
            };
        }

        return bounded;
    }

    private static DiagnosticSectionResult CreateResult(
        DiagnosticSection section,
        DiagnosticSectionStatus status,
        DiagnosticCaptureSource source,
        string evidenceSchema,
        string captureGroup,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        long captureStartedTimestamp,
        long startedTimestamp,
        long completedTimestamp,
        JsonNode? data,
        string? code,
        string? message,
        TimeProvider clock) =>
        new(
            Section: section,
            Status: status,
            Source: source,
            EvidenceSchema: evidenceSchema,
            CaptureGroup: captureGroup,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: completedAtUtc,
            StartedOffsetMs: ToMilliseconds(clock.GetElapsedTime(captureStartedTimestamp, startedTimestamp)),
            CompletedOffsetMs: ToMilliseconds(clock.GetElapsedTime(captureStartedTimestamp, completedTimestamp)),
            DurationMs: ToMilliseconds(clock.GetElapsedTime(startedTimestamp, completedTimestamp)),
            Data: data,
            Code: code,
            Message: message);

    private static long ToMilliseconds(TimeSpan value) =>
        Math.Max(0, (long)Math.Ceiling(value.TotalMilliseconds));
}
