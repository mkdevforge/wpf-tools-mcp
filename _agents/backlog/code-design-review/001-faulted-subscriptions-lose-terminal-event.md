# Faulted Subscriptions Lose Terminal Event

- ID: 001
- Status: Done
- Priority: P2
- Source: review finding
- References:
  - `src/WpfToolsMcp.McpServer/Subscriptions/SubscriptionManager.cs:187-195`
  - `src/WpfToolsMcp.McpServer/Subscriptions/SubscriptionManager.cs:274-290`
  - `src/WpfToolsMcp.McpServer/Subscriptions/SubscriptionManager.cs:352-359`

## Problem

Result/Error Modeling and State Object: the subscription worker catches a fatal exception, enqueues `subscription_error`, then removes the subscription in `finally`. `PollAsync` resolves the subscription before draining, so a caller can see `Unknown subscriptionId` instead of the terminal error event.

## Consequence

Consumers lose the useful failure payload and cannot distinguish a faulted subscription from an invalid or already-unsubscribed ID. This makes subscription failures hard to diagnose and weakens the event protocol.

## Desired Outcome

Faulted subscriptions keep their terminal event available for polling until the event is drained, the subscription is explicitly unsubscribed, or a clear retention policy expires it.

## Suggested Approach

Introduce an explicit subscription lifecycle state such as `Active`, `Faulted`, and `Disposed`. On fatal worker failure, transition to `Faulted`, enqueue the terminal error, and avoid immediate dictionary removal. Remove the subscription only after the terminal event is drained or after `Unsubscribe`.

## Acceptance Criteria

- A worker failure produces a pollable `subscription_error` event instead of `Unknown subscriptionId`.
- Polling with the wrong `sessionId` still rejects access.
- Unsubscribe still cancels and removes both active and faulted subscriptions.
- Add targeted tests for terminal-event polling and cleanup behavior.

## Validation

- Add or update a focused subscription-manager test that forces a worker failure and then polls the subscription.
- Run the focused subscription snapshot/unit tests that cover `subscribe_binding_errors`, `poll_subscription`, and `unsubscribe`.

## QA Review

- 2026-05-06: Verified. Current `SubscriptionManager` enqueues `subscription_error` in the worker failure path and then removes the subscription from `_subscriptions` before `PollAsync` can reliably drain it. The consequence and priority remain supported.

## Notes

- 2026-05-06: Selected for implementation. First validation target is focused subscription-manager coverage for terminal error polling and unsubscribe cleanup.
- 2026-05-06: Implemented in `9b2c5c5`. Added a subscription registration/event-sink boundary, explicit faulted lifecycle handling, and removal after a faulted terminal event is drained.
- 2026-05-06: Validation so far: `dotnet test tests\WpfToolsMcp.SnapshotTests\WpfToolsMcp.SnapshotTests.csproj -c Release --no-restore --filter SubscriptionManagerDesignTests` passed; `dotnet test tests\WpfToolsMcp.SnapshotTests\WpfToolsMcp.SnapshotTests.csproj -c Release --no-restore --filter BindingSubscriptionSnapshots` passed. Both commands emit an existing `System.Diagnostics.EventLog` version conflict warning after referencing the MCP server assembly from tests.
- 2026-05-06: Final validation passed with 5 tests: `dotnet test tests\WpfToolsMcp.SnapshotTests\WpfToolsMcp.SnapshotTests.csproj -c Release --no-restore --filter "FullyQualifiedName~SubscriptionManagerDesignTests|FullyQualifiedName~BindingSubscriptionSnapshots|FullyQualifiedName~TraceCoverageSnapshots"`. Self-review result: happy with the implementation; state is explicit, terminal events remain pollable, session ownership is still checked before drain, and cleanup is deterministic after drain or unsubscribe.
- Keep queue bounds intact; the terminal event should not be silently dropped by lifecycle cleanup.
