# Automation Fallback Logic Consumes Typed Error Codes

- ID: 003c
- Status: In Progress
- Priority: P2
- Source: split from 003
- References:
  - `src/WpfToolsMcp.Automation/AutomationController.Agent.cs:94-150`
  - `src/WpfToolsMcp.Automation/AutomationController.ElementHandles.cs:453-469`
  - `src/WpfToolsMcp.Automation/AgentCallException.cs`
  - `src/WpfToolsMcp.AgentProtocol/AgentErrorCodes.cs`

## Problem

Automation retry and fallback behavior still checks exception text for `wpf_resolve:not_found:`, `wpf_handle_stale:`, `wpf_resolve:ambiguous:`, and related prefixes.

## Consequence

Retry and stale-element behavior can regress when messages are reworded, even if the underlying semantic error is unchanged.

## Desired Outcome

Automation fallback and stale-element logic uses stable error codes for migrated failures and keeps message text only for diagnostics.

## Suggested Approach

Teach the relevant automation helper methods to consume coded exceptions, especially `AgentCallException.Code`, and remove substring checks for cases migrated by `003a` and `003b`. Keep any remaining string fallback narrow and documented only for legacy unmigrated sources.

## Acceptance Criteria

- WPF agent stale/not-found retry decisions use `AgentErrorCodes` instead of message substrings.
- WPF ambiguous and not-found helper checks use typed codes where available.
- User-facing stale-element messages still include useful context and the original diagnostic text.
- Existing fallback behavior remains compatible for unmigrated legacy errors.

## Validation

- Add or update focused tests for agent retry after stale/not-found coded errors and for ambiguous/not-found handling.
- Run the focused agent/automation snapshot tests that cover WPF handle recovery and stale element behavior.

## QA Review

- 2026-05-06: Split from 003. This depends on the protocol and MCP boundary slices because automation can only consume typed codes reliably after they are propagated.

## Notes

- 2026-05-06: Selected for implementation after `003b`. First validation target is focused automation helper coverage for coded WPF not-found, ambiguous, stale-handle, and legacy fallback behavior.
