using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ElementResolutionAmbiguityExceptionTests
{
    [Test]
    public void Message_keeps_exact_discovered_count_when_returned_candidates_are_truncated()
    {
        var ambiguity = new ResolveElementAmbiguity(
            Code: "ambiguous_element",
            BackendUsed: InspectionBackend.Wpf,
            WindowHandleUsed: 42,
            ReturnedCandidates: 5,
            DiscoveredCandidates: 8,
            Truncated: true,
            Candidates: [],
            TruncatedReason: "maxCandidates");

        var exception = new ElementResolutionAmbiguityException(ambiguity);

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain("found 8"));
            Assert.That(exception.Message, Does.Not.Contain("at least"));
        });
    }
}
