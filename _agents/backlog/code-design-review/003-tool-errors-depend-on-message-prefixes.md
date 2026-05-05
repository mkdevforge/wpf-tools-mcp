# Tool Errors Depend On Message Prefixes

- ID: 003
- Status: Split
- Priority: P2
- Source: review finding
- References:
  - `src/WpfToolsMcp.McpServer/Tools/McpToolErrors.cs:31-67`
  - `src/WpfToolsMcp.Automation/AutomationController.ElementHandles.cs:453-469`
  - `src/WpfToolsMcp.Automation/AutomationController.Agent.cs:94-150`
  - `src/WpfToolsMcp.Agent/AgentResponses.cs:27-52`

## Problem

Stringly Typed Error Modeling: MCP and automation error behavior is inferred from exception message prefixes and substring checks such as `wpf_resolve:not_found`, `stale_element`, and `invalid_request`.

## Consequence

Changing an exception message can change retry, fallback, stale-element handling, and MCP error presentation without compiler or test feedback at the right abstraction boundary.

## Desired Outcome

Semantic errors carry stable codes through the relevant layers, and message text is only presentation detail.

## Suggested Approach

Introduce shared domain/tool exceptions or a result/error type with a stable error code. Start by replacing the most important stale/not-found/ambiguous cases, then let `McpToolErrors` translate typed codes instead of parsing text.

## Acceptance Criteria

- Stale element, WPF resolve not-found, WPF resolve ambiguous, timeout, and invalid request errors have stable typed codes.
- Existing user-facing messages remain useful.
- Retry/fallback logic no longer depends on substring matching for migrated cases.
- Targeted tests verify code propagation through agent, automation, and MCP boundaries.

## Notes

- Coordinate with `AgentCallException` and `AgentErrorCodes`; there is already a partial model to build on.
- This parent item is retained as provenance. Active work has been split into `003a`, `003b`, and `003c`.

## QA Review

- 2026-05-06: Partially Verified. Message-prefix parsing remains in MCP, agent response mapping, and automation fallback paths, but `AgentErrorCodes`, `AgentEndpointException`, and `AgentCallException` already provide a partial typed model. The implementation should extend that model rather than introduce an unrelated error system.
