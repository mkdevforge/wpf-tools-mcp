using System.Text.Json;
using NUnit.Framework;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class LayoutContextContractTests
{
    [Test]
    public void Current_agent_capabilities_advertise_layout_context()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.GetLayoutContext));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)AgentProtocolCapabilities.Current).Add("wpf/test-only"));
    }

    [Test]
    public void Auto_and_unbounded_lengths_serialize_without_non_finite_numbers()
    {
        var response = new GetLayoutContextResponse(
            Element: new ElementRef("Border", "LayoutProbe_Target", null, "/Window/Border"),
            Target: new LayoutElementMetrics(
                ConfiguredWidthWpfDips: new LayoutLength(LayoutLengthKind.Auto),
                MaximumWidthWpfDips: new LayoutLength(LayoutLengthKind.Unbounded)),
            Ancestors: [],
            Siblings: [],
            GridContexts: [],
            Counts: new LayoutContextCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            UnavailableEvidence: [],
            Truncated: false);

        var json = JsonSerializer.Serialize(response);
        var roundTrip = JsonSerializer.Deserialize<GetLayoutContextResponse>(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Kind\":\"Auto\""));
            Assert.That(json, Does.Contain("\"Kind\":\"Unbounded\""));
            Assert.That(json, Does.Not.Contain("NaN"));
            Assert.That(json, Does.Not.Contain("Infinity"));
            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.Target.ConfiguredWidthWpfDips!.Kind, Is.EqualTo(LayoutLengthKind.Auto));
            Assert.That(roundTrip.Target.MaximumWidthWpfDips!.Kind, Is.EqualTo(LayoutLengthKind.Unbounded));
        });
    }

    [Test]
    public void Missing_layout_capability_requires_target_and_session_restart()
    {
        var exception = AutomationController.CreateGetLayoutContextCapabilityException();

        Assert.That(
            exception.Message,
            Is.EqualTo(
                "agent_capability_unavailable: get_layout_context requires the current WPF agent. " +
                "Restart the target application, start a new MCP session, and attach again so the current agent can be injected."));
        Assert.That(exception.Message, Does.Not.Contain("retry").IgnoreCase);
        Assert.That(exception.Message, Does.Not.Contain("reinject").IgnoreCase);
    }

    [Test]
    public void Bounded_layout_text_never_splits_a_utf16_surrogate_pair()
    {
        var emoji = char.ConvertFromUtf32(0x1F642);
        var value = new string('a', 127) + emoji + "tail";

        var bounded = LayoutContextText.TruncateAtValidUtf16Boundary(value, 128, out var truncated);
        var json = JsonSerializer.Serialize(new LayoutElementSummary("Border", Name: bounded, IdentityTruncated: truncated));

        Assert.Multiple(() =>
        {
            Assert.That(truncated, Is.True);
            Assert.That(bounded, Has.Length.EqualTo(127));
            Assert.That(char.IsSurrogate(bounded[^1]), Is.False);
            Assert.That(JsonSerializer.Deserialize<LayoutElementSummary>(json)!.Name, Is.EqualTo(bounded));
        });
    }

    [Test]
    public void Empty_clip_state_serializes_without_inventing_bounds()
    {
        var clipping = new LayoutClippingInfo(
            ClipToBounds: false,
            HasExplicitClip: true,
            ExplicitClipIsEmpty: true);

        var json = JsonSerializer.Serialize(clipping);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"ExplicitClipIsEmpty\":true"));
            Assert.That(json, Does.Not.Contain("ExplicitClipBoundsLocalWpfDips"));
        });
    }
}
