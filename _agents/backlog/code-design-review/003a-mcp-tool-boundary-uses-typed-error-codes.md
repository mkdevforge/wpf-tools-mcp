# MCP Tool Boundary Uses Typed Error Codes

- ID: 003a
- Status: In Progress
- Priority: P2
- Source: split from 003
- References:
  - `src/WpfToolsMcp.McpServer/Tools/McpToolErrors.cs:22-67`
  - `src/WpfToolsMcp.AgentProtocol/AgentErrorCodes.cs`
  - `src/WpfToolsMcp.Automation/AgentCallException.cs`

## Problem

`McpToolErrors` infers known error codes by inspecting exception message text and prefixes. It does not first ask whether the exception already carries a stable code.

## Consequence

MCP error behavior can change when wording changes, even for errors that already have a typed code in lower layers.

## Desired Outcome

MCP tool error translation prefers typed error codes and treats message text as presentation detail.

## Suggested Approach

Introduce or reuse a small coded-exception interface/base type and have `McpToolErrors` translate that code directly. Keep current user-facing messages useful and retain legacy parsing only as a temporary compatibility fallback for unmigrated errors.

## Acceptance Criteria

- `McpToolErrors` recognizes stable codes from coded exceptions without splitting or prefix-parsing the message.
- Existing MCP error text remains readable and includes the tool name.
- Legacy prefix parsing is either removed for migrated cases or clearly isolated as fallback for unmigrated cases.
- No public tool schema changes are introduced.

## Validation

- Add or update focused tests for typed invalid-request, stale/not-found, and timeout/error translation at the MCP tool boundary.
- Run focused MCP tool error tests or the smallest snapshot set covering tool failures.

## QA Review

- 2026-05-06: Split from 003. Current code already has `AgentErrorCodes` and `AgentCallException`, so this slice should extend the existing model instead of replacing it.

## Notes

- 2026-05-06: Selected for implementation after `005`. First validation target is focused `McpToolErrors` coverage for coded exceptions and legacy prefix fallback.
