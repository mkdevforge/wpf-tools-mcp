# Agent Responses Permit Impossible States

- ID: 005
- Status: Review
- Priority: P3
- Source: review finding
- References:
  - `src/WpfToolsMcp.AgentProtocol/ProtocolMessages.cs:7-9`
  - `src/WpfToolsMcp.Agent/AgentResponses.cs`
  - `src/WpfToolsMcp.Automation/AgentClient.cs`

## Problem

Result/Error Modeling: `AgentResponse` can be constructed as successful with an error, failed with a result, or failed without an error. Current factory usage is disciplined, but the protocol type does not enforce the invariant.

## Consequence

Future protocol changes or tests can accidentally create invalid response states, and `AgentClient` only checks `Ok` before trusting the rest of the shape.

## Desired Outcome

The protocol response model makes success and failure states explicit and prevents invalid combinations.

## Suggested Approach

Use factory-only construction, validating constructors, or separate success/failure protocol records. Keep the wire shape compatible unless a protocol-breaking change is intentionally chosen.

## Acceptance Criteria

- Code cannot construct an `Ok=true` response with an error or an `Ok=false` response without an error through normal APIs.
- Agent server response factories still serialize to the existing expected JSON shape.
- Agent client validates response invariants defensively.
- Agent protocol snapshot tests are updated or added as needed.

## Validation

- Add or update focused agent protocol tests for success, failure, and invalid response shapes.
- Run the focused agent server/protocol design tests and any affected snapshot tests.

## QA Review

- 2026-05-06: Verified. `AgentResponse` is a public positional record with nullable `Result` and `Error`, so invalid success/failure combinations remain constructible and `AgentClient` currently gates primarily on `Ok`.

## Notes

- 2026-05-06: Selected for implementation after `001`. First validation target is focused agent protocol/design coverage for valid success, valid failure, invalid construction, and client-side invariant checks.
- 2026-05-06: Implemented in `3820d55`. `AgentResponse` now has validated construction plus success/failure factories while preserving the `Id`, `Ok`, `Result`, and `Error` wire properties. `AgentClient` now funnels responses through a defensive interpreter before trusting result or error state.
- 2026-05-06: Validation so far: `dotnet test tests\WpfToolsMcp.SnapshotTests\WpfToolsMcp.SnapshotTests.csproj -c Release --no-restore --filter AgentServerDesignTests` passed with 9 tests. The command emits the same `System.Diagnostics.EventLog` version conflict warning noted during `001`.
- Prefer a low-friction change that preserves current wire compatibility.
