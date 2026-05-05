# Agent WPF Resolve Failures Use Stable Codes

- ID: 003b
- Status: Pending
- Priority: P2
- Source: split from 003
- References:
  - `src/WpfToolsMcp.Agent/AgentResponses.cs:23-59`
  - `src/WpfToolsMcp.Agent/WpfVisualTreeInspector.cs:1515-1549`
  - `src/WpfToolsMcp.Agent/WpfVisualTreeInspector.cs:1628-1688`
  - `src/WpfToolsMcp.Agent/WpfVisualTreeInspector.cs:1740-1747`
  - `src/WpfToolsMcp.AgentProtocol/AgentErrorCodes.cs`

## Problem

Agent WPF resolve failures are still raised as ordinary exceptions with messages such as `wpf_resolve:not_found:` or `wpf_resolve:ambiguous:`. `AgentResponses` then infers the protocol code from those message prefixes.

## Consequence

Changing WPF resolve exception wording can change agent protocol codes and downstream fallback behavior.

## Desired Outcome

WPF resolve not-found, ambiguous, stale-handle, and invalid-request failures carry stable `AgentErrorCodes` before response serialization.

## Suggested Approach

Raise `AgentEndpointException` or another coded exception from WPF resolve failure points where semantic codes are known. Update `AgentResponses.FromException` so these cases no longer depend on substring inference.

## Acceptance Criteria

- WPF resolve not-found and ambiguous failures serialize with `wpf_resolve_not_found` and `wpf_resolve_ambiguous` codes.
- Invalid request failures serialize with `invalid_request` without relying on message text.
- Existing message text remains useful for humans.
- Prefix inference is removed for migrated WPF resolve cases or isolated as fallback only.

## Validation

- Add or update focused agent design/protocol tests for WPF resolve not-found, ambiguous, stale-handle, and invalid-request error codes.
- Run the focused agent server design tests.

## QA Review

- 2026-05-06: Split from 003. This isolates the injected-agent side of the typed error model before automation fallback logic is tightened.
