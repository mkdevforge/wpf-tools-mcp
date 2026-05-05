using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Subscriptions;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class SubscriptionManagerDesignTests
{
    [Test]
    public async Task Faulted_subscription_keeps_terminal_error_until_poll_drains_it()
    {
        using var subscriptions = new SubscriptionManager();

        var subscription = subscriptions.StartSubscription(
            sessionId: "session-a",
            kind: SubscriptionKind.BindingErrors,
            maxQueue: 10,
            runAsync: (_, _) => throw new InvalidOperationException("worker failed"));

        await subscription.Worker.WaitAsync(TimeSpan.FromSeconds(1));

        var wrongSession = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await subscriptions.PollAsync(
                sessionId: "session-b",
                subscriptionId: subscription.SubscriptionId,
                maxBatch: 10,
                timeoutMs: 0,
                cancellationToken: CancellationToken.None));

        Assert.That(wrongSession?.Message, Does.Contain("subscriptionId does not belong to sessionId"));

        var poll = await subscriptions.PollAsync(
            sessionId: "session-a",
            subscriptionId: subscription.SubscriptionId,
            maxBatch: 10,
            timeoutMs: 0,
            cancellationToken: CancellationToken.None);

        Assert.That(poll.Events, Has.Count.EqualTo(1));
        Assert.That(poll.Events[0].Kind, Is.EqualTo("subscription_error"));
        Assert.That(poll.Events[0].Payload["message"]?.GetValue<string>(), Is.EqualTo("worker failed"));

        var afterDrain = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await subscriptions.PollAsync(
                sessionId: "session-a",
                subscriptionId: subscription.SubscriptionId,
                maxBatch: 10,
                timeoutMs: 0,
                cancellationToken: CancellationToken.None));

        Assert.That(afterDrain?.Message, Does.Contain("Unknown subscriptionId"));
    }

    [Test]
    public async Task Unsubscribe_removes_active_and_faulted_subscriptions()
    {
        using var subscriptions = new SubscriptionManager();

        var active = subscriptions.StartSubscription(
            sessionId: "session-a",
            kind: SubscriptionKind.BindingErrors,
            maxQueue: 10,
            runAsync: (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        Assert.That(subscriptions.Unsubscribe("session-a", active.SubscriptionId).Unsubscribed, Is.True);

        var faulted = subscriptions.StartSubscription(
            sessionId: "session-a",
            kind: SubscriptionKind.BindingErrors,
            maxQueue: 10,
            runAsync: (_, _) => throw new InvalidOperationException("worker failed"));

        await faulted.Worker.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.That(subscriptions.Unsubscribe("session-a", faulted.SubscriptionId).Unsubscribed, Is.True);

        var afterUnsubscribe = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await subscriptions.PollAsync(
                sessionId: "session-a",
                subscriptionId: faulted.SubscriptionId,
                maxBatch: 10,
                timeoutMs: 0,
                cancellationToken: CancellationToken.None));

        Assert.That(afterUnsubscribe?.Message, Does.Contain("Unknown subscriptionId"));
    }

    [Test]
    public async Task Active_subscription_polls_queued_events()
    {
        using var subscriptions = new SubscriptionManager();

        var active = subscriptions.StartSubscription(
            sessionId: "session-a",
            kind: SubscriptionKind.BindingErrors,
            maxQueue: 10,
            runAsync: (events, _) =>
            {
                events.Enqueue("binding_error_added", new JsonObject { ["message"] = "queued" });
                return Task.CompletedTask;
            });

        await active.Worker.WaitAsync(TimeSpan.FromSeconds(1));

        var poll = await subscriptions.PollAsync(
            sessionId: "session-a",
            subscriptionId: active.SubscriptionId,
            maxBatch: 10,
            timeoutMs: 0,
            cancellationToken: CancellationToken.None);

        Assert.That(poll.Events, Has.Count.EqualTo(1));
        Assert.That(poll.Events[0].Kind, Is.EqualTo("binding_error_added"));
    }
}
