# Tool Input Grammar Drifts By Profile

- ID: 004
- Status: In Progress
- Priority: P3
- Source: review finding
- References:
  - `src/WpfToolsMcp.McpServer/Tools/CoreTools.cs:607-632`
  - `src/WpfToolsMcp.McpServer/Tools/InteractionTools.cs:358-391`

## Problem

Stringly Typed Programming: the `click_element` tool has separate string parsers in core and diagnostics profiles. Core accepts a smaller alias set than diagnostics for the same conceptual input.

## Consequence

The same tool name can accept different user input depending on profile, creating avoidable profile-specific behavior and duplicated parsing rules.

## Desired Outcome

Tool input grammar has one owner and behaves consistently across profiles.

## Suggested Approach

Move click type parsing to a shared helper or use enum binding with a shared converter while preserving existing aliases for compatibility. Apply the same pattern to other duplicated tool option parsers if discovered during the change.

## Acceptance Criteria

- Core and diagnostics `click_element` accept the same click type aliases.
- Duplicate parser logic is removed or delegated to a shared parser.
- Existing snapshot/tool-profile tests continue to pass.
- Add a focused test if current coverage does not catch profile grammar drift.

## Validation

- Add or update a focused tool-profile test that exercises at least one alias currently accepted only by diagnostics, such as `leftClick`, against the core profile.
- Run the focused tool-profile snapshot tests.

## QA Review

- 2026-05-06: Verified. `CoreTools.ParseClickType` accepts fewer aliases than `InteractionTools.ParseClickType` for the same `click_element` concept. The item is small, independent, and remains P3.

## Notes

- This is intentionally small and independent.
- 2026-05-06: Selected after completing `002c`. First validation target is a focused profile grammar test proving a diagnostics-only alias, such as `leftClick`, works through the core-profile `click_element`.
