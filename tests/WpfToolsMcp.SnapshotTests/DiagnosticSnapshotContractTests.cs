using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DiagnosticSnapshotContractTests
{
    [Test]
    public void Diagnostic_snapshot_limits_and_default_budget_are_bounded()
    {
        var budget = new DiagnosticSnapshotBudget();

        Assert.Multiple(() =>
        {
            Assert.That(DiagnosticSnapshotLimits.MaxSections, Is.EqualTo(8));
            Assert.That(Enum.GetValues<DiagnosticSection>(), Has.Length.EqualTo(DiagnosticSnapshotLimits.MaxSections));
            Assert.That(DiagnosticSnapshotLimits.MinDepth, Is.EqualTo(1));
            Assert.That(DiagnosticSnapshotLimits.MaxDepth, Is.EqualTo(6));
            Assert.That(DiagnosticSnapshotLimits.MinItems, Is.EqualTo(1));
            Assert.That(DiagnosticSnapshotLimits.MaxItems, Is.EqualTo(100));
            Assert.That(DiagnosticSnapshotLimits.MinNodes, Is.EqualTo(1));
            Assert.That(DiagnosticSnapshotLimits.MaxNodes, Is.EqualTo(1_000));
            Assert.That(DiagnosticSnapshotLimits.MinValueLength, Is.EqualTo(64));
            Assert.That(DiagnosticSnapshotLimits.MaxValueLength, Is.EqualTo(2_000));
            Assert.That(DiagnosticSnapshotLimits.MinPayloadChars, Is.EqualTo(1_000));
            Assert.That(DiagnosticSnapshotLimits.MaxPayloadChars, Is.EqualTo(100_000));
            Assert.That(DiagnosticSnapshotLimits.MaxPropertyNames, Is.EqualTo(50));
            Assert.That(DiagnosticSnapshotLimits.MaxPropertyNameLength, Is.EqualTo(256));
            Assert.That(DiagnosticSnapshotLimits.MaxFailureMessageLength, Is.EqualTo(1_000));
            Assert.That(DiagnosticSnapshotLimits.MinTimeoutMs, Is.EqualTo(100));
            Assert.That(DiagnosticSnapshotLimits.MaxTimeoutMs, Is.EqualTo(30_000));

            Assert.That(budget, Is.EqualTo(new DiagnosticSnapshotBudget(
                MaxDepth: 3,
                MaxItems: 25,
                MaxNodes: 200,
                MaxValueLength: 1_000,
                MaxPayloadChars: 40_000)));
            Assert.That(budget.MaxDepth, Is.InRange(DiagnosticSnapshotLimits.MinDepth, DiagnosticSnapshotLimits.MaxDepth));
            Assert.That(budget.MaxItems, Is.InRange(DiagnosticSnapshotLimits.MinItems, DiagnosticSnapshotLimits.MaxItems));
            Assert.That(budget.MaxNodes, Is.InRange(DiagnosticSnapshotLimits.MinNodes, DiagnosticSnapshotLimits.MaxNodes));
            Assert.That(
                budget.MaxValueLength,
                Is.InRange(DiagnosticSnapshotLimits.MinValueLength, DiagnosticSnapshotLimits.MaxValueLength));
            Assert.That(
                budget.MaxPayloadChars,
                Is.InRange(DiagnosticSnapshotLimits.MinPayloadChars, DiagnosticSnapshotLimits.MaxPayloadChars));
        });
    }

    [Test]
    public void All_section_statuses_serialize_as_strings_with_status_appropriate_payloads()
    {
        var startedAt = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);
        DiagnosticSectionResult[] sections =
        [
            new(
                DiagnosticSection.VisualTree,
                DiagnosticSectionStatus.Success,
                DiagnosticCaptureSource.WpfDispatcher,
                EvidenceSchema: "get_visual_tree/v1",
                CaptureGroup: "wpf-dispatcher-1",
                startedAt,
                startedAt.AddMilliseconds(2),
                StartedOffsetMs: 0,
                CompletedOffsetMs: 2,
                DurationMs: 2,
                Data: new JsonObject { ["nodes"] = 3 },
                PayloadChars: 11),
            new(
                DiagnosticSection.UiaProperties,
                DiagnosticSectionStatus.Unavailable,
                DiagnosticCaptureSource.Uia,
                EvidenceSchema: "get_element_properties/v1",
                CaptureGroup: "uia-1",
                startedAt.AddMilliseconds(2),
                startedAt.AddMilliseconds(3),
                StartedOffsetMs: 2,
                CompletedOffsetMs: 3,
                DurationMs: 1,
                Code: "backend_unavailable",
                Message: "UI Automation is unavailable."),
            new(
                DiagnosticSection.DataContext,
                DiagnosticSectionStatus.Truncated,
                DiagnosticCaptureSource.WpfDispatcher,
                EvidenceSchema: "get_data_context/v1",
                CaptureGroup: "wpf-dispatcher-1",
                startedAt.AddMilliseconds(3),
                startedAt.AddMilliseconds(5),
                StartedOffsetMs: 3,
                CompletedOffsetMs: 5,
                DurationMs: 2,
                Data: new JsonObject { ["returned"] = 25 },
                Code: "max_items",
                PayloadChars: 15),
            new(
                DiagnosticSection.Screenshot,
                DiagnosticSectionStatus.Failed,
                DiagnosticCaptureSource.Screenshot,
                EvidenceSchema: "take_screenshot/v1",
                CaptureGroup: "screenshot-1",
                startedAt.AddMilliseconds(5),
                startedAt.AddMilliseconds(8),
                StartedOffsetMs: 5,
                CompletedOffsetMs: 8,
                DurationMs: 3,
                Code: "capture_failed",
                Message: "Screenshot capture failed.")
        ];

        var json = JsonSerializer.Serialize(sections);
        var document = JsonNode.Parse(json)!.AsArray();
        var roundTrip = JsonSerializer.Deserialize<DiagnosticSectionResult[]>(json);

        Assert.Multiple(() =>
        {
            Assert.That(document.Select(item => item!["Status"]!.GetValue<string>()), Is.EqualTo(
                new[] { "Success", "Unavailable", "Truncated", "Failed" }));
            Assert.That(document.Select(item => item!["Section"]!.GetValue<string>()), Is.EqualTo(
                new[] { "VisualTree", "UiaProperties", "DataContext", "Screenshot" }));
            Assert.That(document.Select(item => item!["Source"]!.GetValue<string>()), Is.EqualTo(
                new[] { "WpfDispatcher", "Uia", "WpfDispatcher", "Screenshot" }));
            Assert.That(document.Select(item => item!["EvidenceSchema"]!.GetValue<string>()), Is.EqualTo(
                new[]
                {
                    "get_visual_tree/v1",
                    "get_element_properties/v1",
                    "get_data_context/v1",
                    "take_screenshot/v1"
                }));
            Assert.That(document.Select(item => item!["CaptureGroup"]!.GetValue<string>()), Is.EqualTo(
                new[] { "wpf-dispatcher-1", "uia-1", "wpf-dispatcher-1", "screenshot-1" }));

            var success = document[0]!.AsObject();
            var unavailable = document[1]!.AsObject();
            var truncated = document[2]!.AsObject();
            var failed = document[3]!.AsObject();

            Assert.That(success.ContainsKey("Data"), Is.True);
            Assert.That(success.ContainsKey("Code"), Is.False);
            Assert.That(success.ContainsKey("Message"), Is.False);

            Assert.That(unavailable.ContainsKey("Data"), Is.False);
            Assert.That(unavailable["Code"]!.GetValue<string>(), Is.EqualTo("backend_unavailable"));
            Assert.That(unavailable["Message"]!.GetValue<string>(), Is.EqualTo("UI Automation is unavailable."));

            Assert.That(truncated.ContainsKey("Data"), Is.True);
            Assert.That(truncated["Code"]!.GetValue<string>(), Is.EqualTo("max_items"));

            Assert.That(failed.ContainsKey("Data"), Is.False);
            Assert.That(failed["Code"]!.GetValue<string>(), Is.EqualTo("capture_failed"));
            Assert.That(failed["Message"]!.GetValue<string>(), Is.EqualTo("Screenshot capture failed."));

            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.Select(section => section.Status), Is.EqualTo(
                new[]
                {
                    DiagnosticSectionStatus.Success,
                    DiagnosticSectionStatus.Unavailable,
                    DiagnosticSectionStatus.Truncated,
                    DiagnosticSectionStatus.Failed
                }));
        });
    }

    [Test]
    public void Response_round_trips_shared_target_timing_and_consistency_context()
    {
        var startedAt = new DateTimeOffset(2026, 7, 29, 12, 34, 56, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(17);
        var budget = new DiagnosticSnapshotBudget();
        var response = new CaptureDiagnosticSnapshotResponse(
            CaptureId: "capture_contract",
            Target: new DiagnosticSnapshotTarget(
                SessionId: "session_contract",
                ProcessId: 4242,
                ProcessName: "Probe",
                WindowHandle: 123456,
                WindowTitle: "Probe window",
                Scope: DiagnosticTargetScope.Element,
                AnchorBackend: InspectionBackend.Wpf,
                Element: new ElementRef(
                    Type: "TextBox",
                    AutomationId: "Probe_Target",
                    Name: "Probe target",
                    XPath: "/Window/TextBox",
                    ElementId: "wpfobj_contract")),
            Budget: budget,
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            DurationMs: 17,
            Consistency: new DiagnosticSnapshotConsistency(
                SessionSerialized: true,
                WpfSectionsSingleDispatcherTurn: true,
                CrossBackendAtomic: false,
                TimingSkewMs: 4),
            Sections:
            [
                new DiagnosticSectionResult(
                    DiagnosticSection.WpfProperties,
                    DiagnosticSectionStatus.Success,
                    DiagnosticCaptureSource.WpfDispatcher,
                    EvidenceSchema: "get_computed_properties/v1",
                    CaptureGroup: "wpf-dispatcher-1",
                    startedAt.AddMilliseconds(1),
                    startedAt.AddMilliseconds(5),
                    StartedOffsetMs: 1,
                    CompletedOffsetMs: 5,
                    DurationMs: 4,
                    Data: new JsonObject { ["Text"] = "ready" },
                    PayloadChars: 16)
            ]);

        var json = JsonSerializer.Serialize(response);
        var document = JsonNode.Parse(json)!.AsObject();
        var roundTrip = JsonSerializer.Deserialize<CaptureDiagnosticSnapshotResponse>(json);

        Assert.Multiple(() =>
        {
            Assert.That(document["CaptureId"]!.GetValue<string>(), Is.EqualTo("capture_contract"));
            Assert.That(document["Target"]!["SessionId"]!.GetValue<string>(), Is.EqualTo("session_contract"));
            Assert.That(document["Target"]!["ProcessId"]!.GetValue<int>(), Is.EqualTo(4242));
            Assert.That(document["Target"]!["WindowHandle"]!.GetValue<long>(), Is.EqualTo(123456));
            Assert.That(document["Target"]!["Scope"]!.GetValue<string>(), Is.EqualTo("Element"));
            Assert.That(document["Target"]!["AnchorBackend"]!.GetValue<string>(), Is.EqualTo("Wpf"));
            Assert.That(document["Target"]!["Element"]!["elementId"]!.GetValue<string>(), Is.EqualTo("wpfobj_contract"));
            Assert.That(document["StartedAtUtc"]!.GetValue<DateTimeOffset>(), Is.EqualTo(startedAt));
            Assert.That(document["CompletedAtUtc"]!.GetValue<DateTimeOffset>(), Is.EqualTo(completedAt));
            Assert.That(document["DurationMs"]!.GetValue<long>(), Is.EqualTo(17));
            Assert.That(document["Consistency"]!["SessionSerialized"]!.GetValue<bool>(), Is.True);
            Assert.That(document["Consistency"]!["WpfSectionsSingleDispatcherTurn"]!.GetValue<bool>(), Is.True);
            Assert.That(document["Consistency"]!["CrossBackendAtomic"]!.GetValue<bool>(), Is.False);
            Assert.That(document["Consistency"]!["TimingSkewMs"]!.GetValue<long>(), Is.EqualTo(4));

            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.CaptureId, Is.EqualTo(response.CaptureId));
            Assert.That(roundTrip.Target, Is.EqualTo(response.Target));
            Assert.That(roundTrip.Budget, Is.EqualTo(budget));
            Assert.That(roundTrip.StartedAtUtc, Is.EqualTo(startedAt));
            Assert.That(roundTrip.CompletedAtUtc, Is.EqualTo(completedAt));
            Assert.That(roundTrip.DurationMs, Is.EqualTo(17));
            Assert.That(roundTrip.Consistency, Is.EqualTo(response.Consistency));
            Assert.That(roundTrip.Sections, Has.Count.EqualTo(1));
            Assert.That(roundTrip.Sections[0].EvidenceSchema, Is.EqualTo("get_computed_properties/v1"));
            Assert.That(roundTrip.Sections[0].CaptureGroup, Is.EqualTo("wpf-dispatcher-1"));
            Assert.That(roundTrip.Sections[0].StartedOffsetMs, Is.EqualTo(1));
            Assert.That(roundTrip.Sections[0].CompletedOffsetMs, Is.EqualTo(5));
            Assert.That(roundTrip.Sections[0].DurationMs, Is.EqualTo(4));
        });
    }

    [Test]
    public void Current_agent_capabilities_advertise_diagnostic_snapshot_once()
    {
        Assert.That(
            AgentProtocolCapabilities.Current.Count(capability =>
                string.Equals(capability, AgentProtocolCapabilities.CaptureDiagnosticSnapshot, StringComparison.Ordinal)),
            Is.EqualTo(1));
    }
}
