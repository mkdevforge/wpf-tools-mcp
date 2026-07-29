using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DiagnosticSnapshotBudgetTests
{
    [Test]
    public void Value_budget_bounds_nested_evidence_without_corrupting_screenshot_paths()
    {
        const int maxValueLength = 64;
        var longValue = new string('v', maxValueLength + 20);
        var screenshotPath = Path.Combine(
            Path.GetTempPath(),
            new string('p', maxValueLength + 20),
            "snapshot.png");
        var sections = new[]
        {
            CreateResult(
                DiagnosticSection.UiaProperties,
                new JsonObject
                {
                    ["truncated"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["Value"] = longValue,
                        ["Nested"] = new JsonArray(longValue)
                    }
                }),
            CreateResult(
                DiagnosticSection.Screenshot,
                new JsonObject { ["path"] = screenshotPath })
        };

        var bounded = DiagnosticSnapshotValueBudget.Apply(sections, maxValueLength);
        var properties = bounded[0].Data!["properties"]!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(bounded[0].Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
            Assert.That(bounded[0].Code, Is.EqualTo("maxValueLength"));
            Assert.That(bounded[0].Data!["truncated"]!.GetValue<bool>(), Is.True);
            Assert.That(bounded[0].Data!["truncatedReason"]!.GetValue<string>(), Is.EqualTo("maxValueLength"));
            Assert.That(properties["Value"]!.GetValue<string>(), Has.Length.EqualTo(maxValueLength));
            Assert.That(properties["Nested"]![0]!.GetValue<string>(), Has.Length.EqualTo(maxValueLength));
            Assert.That(bounded[1].Status, Is.EqualTo(DiagnosticSectionStatus.Success));
            Assert.That(bounded[1].Data!["path"]!.GetValue<string>(), Is.EqualTo(screenshotPath));
        });
    }

    [Test]
    public void Screenshot_cleanup_deletes_a_file_created_for_an_unreturned_capture()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(DiagnosticSnapshotScreenshotCleanup.Delete(path), Is.True);
            Assert.That(File.Exists(path), Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Uia_value_serializer_uses_snapshot_depth_item_and_string_limits()
    {
        const int maxValueLength = 64;
        var budget = new PropertyValueBudget(
            maxStringLength: maxValueLength,
            maxCollectionItems: 2,
            maxValueDepth: 1,
            maxSerializedValueCharacters: 1_000,
            maxXPathLength: maxValueLength);
        var evidence = new object[]
        {
            new string('a', maxValueLength + 10),
            new[] { "nested" },
            "omitted"
        };

        var serialized = BoundedPropertyValueSerializer.Serialize(evidence, budget)!.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(serialized, Has.Count.EqualTo(2));
            Assert.That(serialized[0]!.GetValue<string>(), Has.Length.EqualTo(maxValueLength));
            Assert.That(serialized[1]!.GetValue<string>(), Is.EqualTo("<truncated:maxValueDepth>"));
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.StringLength), Is.True);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.CollectionItems), Is.True);
            Assert.That(budget.Truncation.HasFlag(PropertyValueTruncation.ValueDepth), Is.True);
        });
    }

    private static DiagnosticSectionResult CreateResult(
        DiagnosticSection section,
        JsonNode data) =>
        new(
            Section: section,
            Status: DiagnosticSectionStatus.Success,
            Source: section == DiagnosticSection.Screenshot
                ? DiagnosticCaptureSource.Screenshot
                : DiagnosticCaptureSource.Uia,
            EvidenceSchema: "test/v1",
            CaptureGroup: "test",
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            StartedOffsetMs: 0,
            CompletedOffsetMs: 1,
            DurationMs: 1,
            Data: data);
}
