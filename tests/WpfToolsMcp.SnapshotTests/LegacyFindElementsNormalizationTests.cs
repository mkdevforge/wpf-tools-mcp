using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class LegacyFindElementsNormalizationTests
{
    [Test]
    public void Preserves_agent_truncation_reason_when_result_lookahead_also_exceeds_limit()
    {
        var legacy = CreateLegacyResponse(
            matchCount: 3,
            truncated: true,
            truncatedReason: "maxNodes");

        var normalized = AutomationController.NormalizeLegacyFindElementsResponse(
            legacy,
            requestedMaxResults: 2);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Matches, Has.Count.EqualTo(2));
            Assert.That(normalized.ReturnedMatches, Is.EqualTo(2));
            Assert.That(normalized.DiscoveredMatches, Is.EqualTo(3));
            Assert.That(normalized.Truncated, Is.True);
            Assert.That(normalized.TruncatedReason, Is.EqualTo("maxNodes"));
        });
    }

    [Test]
    public void Synthesizes_max_results_reason_when_agent_reason_is_absent()
    {
        var legacy = CreateLegacyResponse(
            matchCount: 3,
            truncated: false,
            truncatedReason: null);

        var normalized = AutomationController.NormalizeLegacyFindElementsResponse(
            legacy,
            requestedMaxResults: 2);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Matches, Has.Count.EqualTo(2));
            Assert.That(normalized.ReturnedMatches, Is.EqualTo(2));
            Assert.That(normalized.DiscoveredMatches, Is.EqualTo(3));
            Assert.That(normalized.Truncated, Is.True);
            Assert.That(normalized.TruncatedReason, Is.EqualTo("maxResults"));
        });
    }

    private static FindElementsResponse CreateLegacyResponse(
        int matchCount,
        bool truncated,
        string? truncatedReason)
    {
        var matches = Enumerable.Range(1, matchCount)
            .Select(index => new ElementRef(
                Type: "Button",
                AutomationId: $"Button{index}",
                Name: $"Button {index}",
                XPath: $"/Window/Button[{index}]"))
            .ToArray();

        return new FindElementsResponse(
            BackendUsed: InspectionBackend.Wpf,
            Matches: matches,
            ReturnedMatches: matches.Length,
            ScannedNodes: 10,
            Truncated: truncated,
            TruncatedReason: truncatedReason);
    }
}
