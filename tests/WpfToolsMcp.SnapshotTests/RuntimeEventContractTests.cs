using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public sealed class RuntimeEventContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Test]
    public void Subscription_event_envelope_is_additive_and_versioned()
    {
        var observedAtUtc = new DateTimeOffset(2026, 7, 30, 12, 34, 56, TimeSpan.Zero);
        var envelope = new RuntimeEventEnvelope(
            Version: 1,
            ObservedAtUtc: observedAtUtc,
            SourceKind: RuntimeEventSourceKinds.PropertyChanges,
            SessionId: "11111111111111111111111111111111",
            StreamId: "22222222222222222222222222222222",
            Sequence: 7,
            WindowHandle: 42,
            ElementId: "wpf_1234567890abcdef",
            XPath: "/Window/Grid/TextBox");
        var subscriptionEvent = new SubscriptionEvent(
            Sequence: 7,
            Kind: SubscriptionEventKinds.PropertyChanged,
            Payload: new JsonObject { ["value"] = "ready" })
        {
            Envelope = envelope
        };

        var json = JsonSerializer.Serialize(subscriptionEvent, WebJson);
        var roundTrip = JsonSerializer.Deserialize<SubscriptionEvent>(json, WebJson);
        using var document = JsonDocument.Parse(json);
        var serializedEnvelope = document.RootElement.GetProperty("envelope");

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.Sequence, Is.EqualTo(7));
            Assert.That(roundTrip.Envelope, Is.EqualTo(envelope));
            Assert.That(roundTrip.Envelope!.ObservedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(serializedEnvelope.GetProperty("version").GetInt32(), Is.EqualTo(1));
            Assert.That(serializedEnvelope.GetProperty("observedAtUtc").GetDateTimeOffset(), Is.EqualTo(observedAtUtc));
            Assert.That(serializedEnvelope.GetProperty("sourceKind").GetString(), Is.EqualTo(RuntimeEventSourceKinds.PropertyChanges));
        });
    }

    [Test]
    public void Poll_response_exposes_canonical_loss_counters_and_legacy_aliases()
    {
        var response = new PollSubscriptionResponse(
            Events: [],
            Dropped: 2,
            HasMore: false,
            DroppedTotal: 5,
            Coalesced: 3,
            CoalescedTotal: 7,
            Truncated: 4,
            TruncatedTotal: 9);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(response.DroppedSinceLastPoll, Is.EqualTo(2));
            Assert.That(response.CoalescedSinceLastPoll, Is.EqualTo(3));
            Assert.That(response.TruncatedSinceLastPoll, Is.EqualTo(4));
            Assert.That(response.Dropped, Is.EqualTo(response.DroppedSinceLastPoll));
            Assert.That(response.Coalesced, Is.EqualTo(response.CoalescedSinceLastPoll));
            Assert.That(response.Truncated, Is.EqualTo(response.TruncatedSinceLastPoll));
            Assert.That(root.GetProperty("dropped").GetInt32(), Is.EqualTo(2));
            Assert.That(root.GetProperty("droppedSinceLastPoll").GetInt32(), Is.EqualTo(2));
            Assert.That(root.GetProperty("coalesced").GetInt32(), Is.EqualTo(3));
            Assert.That(root.GetProperty("coalescedSinceLastPoll").GetInt32(), Is.EqualTo(3));
            Assert.That(root.GetProperty("truncated").GetInt32(), Is.EqualTo(4));
            Assert.That(root.GetProperty("truncatedSinceLastPoll").GetInt32(), Is.EqualTo(4));
        });
    }

    [Test]
    public void Terminal_subscription_payload_is_typed_and_machine_readable()
    {
        var completedAtUtc = new DateTimeOffset(2026, 7, 30, 12, 35, 0, TimeSpan.Zero);
        var terminal = new SubscriptionTerminalEvent(
            SubscriptionTerminalCodes.TargetExited,
            completedAtUtc);

        var node = JsonSerializer.SerializeToNode(terminal)!;
        var roundTrip = node.Deserialize<SubscriptionTerminalEvent>();

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip, Is.EqualTo(terminal));
            Assert.That(node["code"]!.GetValue<string>(), Is.EqualTo(SubscriptionTerminalCodes.TargetExited));
            Assert.That(node["completedAtUtc"]!.GetValue<DateTimeOffset>(), Is.EqualTo(completedAtUtc));
        });
    }
}
