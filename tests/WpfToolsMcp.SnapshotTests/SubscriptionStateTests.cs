using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Subscriptions;

namespace WpfToolsMcp.SnapshotTests;

public sealed class SubscriptionStateTests
{
    private const string SessionId = "11111111111111111111111111111111";
    private const string SubscriptionId = "22222222222222222222222222222222";
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 30, 12, 34, 56, TimeSpan.Zero);

    [Test]
    public void Enqueued_event_has_bounded_common_identity_envelope()
    {
        using var state = CreateState(
            maxQueue: 4,
            maxPayloadChars: 4_096,
            windowHandle: 42,
            elementId: "wpf_1234567890abcdef",
            xpath: "/" + new string('x', 2_100));

        state.Enqueue(
            SubscriptionEventKinds.PropertyChanged,
            new JsonObject { ["value"] = "ready" },
            FixedUtc);

        var item = state.Drain(10).Events.Single();

        Assert.Multiple(() =>
        {
            Assert.That(item.Sequence, Is.EqualTo(1));
            Assert.That(item.Envelope, Is.Not.Null);
            Assert.That(item.Envelope!.Version, Is.EqualTo(RuntimeEventVersions.V1));
            Assert.That(item.Envelope.ObservedAtUtc, Is.EqualTo(FixedUtc));
            Assert.That(item.Envelope.SourceKind, Is.EqualTo(RuntimeEventSourceKinds.PropertyChanges));
            Assert.That(item.Envelope.SessionId, Is.EqualTo(SessionId));
            Assert.That(item.Envelope.StreamId, Is.EqualTo(SubscriptionId));
            Assert.That(item.Envelope.Sequence, Is.EqualTo(item.Sequence));
            Assert.That(item.Envelope.WindowHandle, Is.EqualTo(42));
            Assert.That(item.Envelope.ElementId, Is.EqualTo("wpf_1234567890abcdef"));
            Assert.That(item.Envelope.XPath, Is.Null);
            Assert.That(item.Envelope.XPathOmitted, Is.True);
            Assert.That(JsonSerializer.Serialize(item), Has.Length.LessThanOrEqualTo(4_096));
        });
    }

    [Test]
    public void Queue_loss_resets_per_poll_counters_but_preserves_totals()
    {
        using var state = CreateState(maxQueue: 2, maxPayloadChars: 4_096);

        state.Enqueue(SubscriptionEventKinds.BindingErrorAdded, new JsonObject { ["index"] = 1 }, FixedUtc);
        state.Enqueue(SubscriptionEventKinds.BindingErrorAdded, new JsonObject { ["index"] = 2 }, FixedUtc);
        state.Enqueue(SubscriptionEventKinds.BindingErrorAdded, new JsonObject { ["index"] = 3 }, FixedUtc);

        var first = state.Drain(10);
        var second = state.Drain(10);

        Assert.Multiple(() =>
        {
            Assert.That(first.Events.Select(item => item.Sequence), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(first.DroppedSinceLastPoll, Is.EqualTo(1));
            Assert.That(first.DroppedTotal, Is.EqualTo(1));
            Assert.That(second.DroppedSinceLastPoll, Is.Zero);
            Assert.That(second.DroppedTotal, Is.EqualTo(1));
        });
    }

    [Test]
    public void Whole_event_budget_compacts_payload_and_reports_truncation()
    {
        using var state = CreateState(maxQueue: 4, maxPayloadChars: 1_024);

        state.Enqueue(
            SubscriptionEventKinds.BindingErrorAdded,
            new JsonObject { ["message"] = new string('x', 5_000) },
            FixedUtc);

        var drain = state.Drain(10);
        var item = drain.Events.Single();

        Assert.Multiple(() =>
        {
            Assert.That(drain.TruncatedSinceLastPoll, Is.EqualTo(1));
            Assert.That(drain.TruncatedTotal, Is.EqualTo(1));
            Assert.That(item.Payload["truncated"]!.GetValue<bool>(), Is.True);
            Assert.That(item.Payload["reason"]!.GetValue<string>(), Is.EqualTo("subscription_event_limit"));
            Assert.That(JsonSerializer.Serialize(item), Has.Length.LessThanOrEqualTo(1_024));
        });
    }

    [Test]
    public void Completion_retains_exactly_one_typed_terminal_event()
    {
        using var state = CreateState(maxQueue: 2, maxPayloadChars: 4_096);

        state.Enqueue(SubscriptionEventKinds.PropertyInitial, new JsonObject(), FixedUtc);
        var firstCompletion = state.Complete(SubscriptionTerminalCodes.TargetExited);
        var secondCompletion = state.Complete(SubscriptionTerminalCodes.SourceError);
        var drain = state.Drain(10);
        var terminal = drain.Events.Single(item => item.Kind == SubscriptionEventKinds.Terminal);
        var terminalPayload = terminal.Payload.Deserialize<SubscriptionTerminalEvent>();
        var afterDelivery = state.Drain(10);

        Assert.Multiple(() =>
        {
            Assert.That(firstCompletion, Is.True);
            Assert.That(secondCompletion, Is.False);
            Assert.That(drain.Events.Count(item => item.Kind == SubscriptionEventKinds.Terminal), Is.EqualTo(1));
            Assert.That(terminalPayload, Is.EqualTo(
                new SubscriptionTerminalEvent(SubscriptionTerminalCodes.TargetExited, FixedUtc)));
            Assert.That(terminal.Envelope!.Sequence, Is.EqualTo(terminal.Sequence));
            Assert.That(drain.Completed, Is.True);
            Assert.That(drain.CompletionReason, Is.EqualTo(SubscriptionTerminalCodes.TargetExited));
            Assert.That(afterDelivery.Events, Is.Empty);
            Assert.That(afterDelivery.Completed, Is.True);
            Assert.That(afterDelivery.CompletionReason, Is.EqualTo(SubscriptionTerminalCodes.TargetExited));
        });
    }

    [Test]
    public async Task Binding_worker_stops_after_first_target_failure()
    {
        using var manager = new SubscriptionManager();
        using var controller = new AutomationController();
        using var state = CreateState(maxQueue: 4, maxPayloadChars: 4_096);
        var scanCount = 0;

        await manager.RunBindingSubscriptionAsync(
            state,
            _ =>
            {
                scanCount++;
                throw new ActionableFailureException(new FailureInfo(
                    "target_exited",
                    "target_shutdown",
                    "The target process exited."));
            },
            TimeSpan.Zero,
            exception => SubscriptionManager.ClassifySubscriptionFailure(exception, controller));

        var drain = state.Drain(10);
        var terminal = drain.Events.Single();
        var terminalPayload = terminal.Payload.Deserialize<SubscriptionTerminalEvent>();

        Assert.Multiple(() =>
        {
            Assert.That(scanCount, Is.EqualTo(1));
            Assert.That(drain.Completed, Is.True);
            Assert.That(drain.CompletionReason, Is.EqualTo(SubscriptionTerminalCodes.TargetExited));
            Assert.That(terminal.Kind, Is.EqualTo(SubscriptionEventKinds.Terminal));
            Assert.That(terminalPayload!.Cause, Is.Not.Null);
            Assert.That(terminalPayload.Cause!.Type, Is.EqualTo(typeof(ActionableFailureException).FullName));
            Assert.That(terminalPayload.Cause.Message, Is.EqualTo("target_exited: The target process exited."));
            Assert.That(terminalPayload.CauseTruncated, Is.Null);
        });
    }

    [Test]
    public void Terminal_failure_cause_compaction_retains_a_useful_message_prefix()
    {
        using var state = CreateState(maxQueue: 2, maxPayloadChars: 1_600);
        var cause = new DiagnosticCauseInfo(new string('t', 256))
        {
            Message = new string('m', 1_024),
            Details = new string('d', 4_096)
        };

        _ = state.Complete(SubscriptionTerminalCodes.SourceError, cause);
        var terminal = state.Drain(10).Events.Single();
        var payload = terminal.Payload.Deserialize<SubscriptionTerminalEvent>();

        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(terminal), Has.Length.LessThanOrEqualTo(1_600));
            Assert.That(payload!.Cause, Is.Not.Null);
            Assert.That(payload.Cause!.Message, Is.EqualTo(new string('m', 512)));
            Assert.That(payload.Cause.Details, Is.Null);
            Assert.That(payload.CauseTruncated, Is.True);
        });
    }

    [Test]
    public void Terminal_failure_cause_falls_back_to_type_when_compacted_text_does_not_fit()
    {
        using var state = CreateState(maxQueue: 2, maxPayloadChars: 1_024);
        var cause = new DiagnosticCauseInfo(new string('t', 256))
        {
            Message = new string('m', 1_024),
            Details = new string('d', 4_096)
        };

        _ = state.Complete(SubscriptionTerminalCodes.SourceError, cause);
        var terminal = state.Drain(10).Events.Single();
        var payload = terminal.Payload.Deserialize<SubscriptionTerminalEvent>();

        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(terminal), Has.Length.LessThanOrEqualTo(1_024));
            Assert.That(payload!.Cause, Is.Not.Null);
            Assert.That(payload.Cause!.Type, Has.Length.LessThanOrEqualTo(128));
            Assert.That(payload.Cause.Message, Is.Null);
            Assert.That(payload.Cause.Details, Is.Null);
            Assert.That(payload.CauseTruncated, Is.True);
        });
    }

    [Test]
    public async Task Property_completion_preserves_primary_failure_when_resource_release_also_fails()
    {
        using var manager = new SubscriptionManager();
        var releaseAttempts = 0;
        using var state = CreateState(
            maxQueue: 2,
            maxPayloadChars: 8_192,
            releaseResource: () =>
            {
                if (Interlocked.Increment(ref releaseAttempts) == 1)
                {
                    throw new InvalidOperationException("release failed");
                }

                return Task.CompletedTask;
            });

        await manager.CompletePropertySubscriptionAsync(
            state,
            SubscriptionTerminalCodes.SourceError,
            new AgentRemoteException(
                "wpf/observe_state_poll",
                "observation failed",
                new string('p', 4_096)));
        var drain = state.Drain(10);
        var payload = drain.Events.Single().Payload.Deserialize<SubscriptionTerminalEvent>();

        Assert.Multiple(() =>
        {
            Assert.That(drain.CompletionReason, Is.EqualTo(SubscriptionTerminalCodes.SourceError));
            Assert.That(payload!.Cause!.Message, Is.EqualTo("observation failed"));
            Assert.That(payload.Cause.Details, Does.StartWith("pppp"));
            Assert.That(payload.Cause.Details, Does.Contain(SubscriptionTerminalCodes.SourceReleaseFailed));
            Assert.That(payload.Cause.Details, Does.Contain("release failed"));
        });
    }

    [Test]
    public async Task Binding_subscription_reports_its_effective_bounds()
    {
        using var manager = new SubscriptionManager();
        using var controller = new AutomationController();

        var response = manager.SubscribeBindingErrors(
            SessionId,
            controller,
            windowHandleUsed: null,
            rootXPath: null,
            depth: 6,
            maxErrors: 200,
            maxNodes: 2_000,
            pollIntervalMs: 1,
            maxQueue: 10_000,
            maxPayloadChars: 1);

        Assert.Multiple(() =>
        {
            Assert.That(response.PollIntervalMs, Is.EqualTo(50));
            Assert.That(response.MaxQueue, Is.EqualTo(1_000));
            Assert.That(response.MaxPayloadChars, Is.EqualTo(4_096));
        });

        var unsubscribed = await manager.UnsubscribeAsync(SessionId, response.SubscriptionId);
        Assert.That(unsubscribed.Unsubscribed, Is.True);
    }

    [Test]
    public async Task Resource_and_capacity_cleanup_are_idempotent()
    {
        var resourceReleaseCount = 0;
        var capacityReleaseCount = 0;
        using var state = CreateState(
            maxQueue: 4,
            maxPayloadChars: 4_096,
            releaseResource: () =>
            {
                resourceReleaseCount++;
                return Task.CompletedTask;
            },
            releaseCapacity: () => capacityReleaseCount++);

        _ = state.Complete(SubscriptionTerminalCodes.DurationElapsed);
        await state.StopAsync();
        await state.StopAsync();
        state.ReleaseCapacity();
        state.ReleaseCapacity();

        Assert.Multiple(() =>
        {
            Assert.That(resourceReleaseCount, Is.EqualTo(1));
            Assert.That(capacityReleaseCount, Is.EqualTo(1));
        });
    }

    private static SubscriptionManager.SubscriptionState CreateState(
        int maxQueue,
        int maxPayloadChars,
        long? windowHandle = null,
        string? elementId = null,
        string? xpath = null,
        Func<Task>? releaseResource = null,
        Action? releaseCapacity = null) =>
        new(
            SubscriptionId,
            SessionId,
            SubscriptionKind.PropertyChanges,
            maxQueue,
            new CancellationTokenSource(),
            maxPayloadChars,
            releaseResource,
            releaseCapacity,
            windowHandle,
            elementId,
            xpath,
            () => FixedUtc);
}
