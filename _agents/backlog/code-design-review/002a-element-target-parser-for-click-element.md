# Element Target Parser For Click Element

- ID: 002a
- Status: Review
- Priority: P2
- Source: split from 002
- References:
  - `src/WpfToolsMcp.Contracts/Contracts.cs:408-417`
  - `src/WpfToolsMcp.Automation/AutomationController.cs:1861-1906`
  - `src/WpfToolsMcp.Agent/AgentEndpointValidation.cs:7-18`
  - `src/WpfToolsMcp.McpServer/Tools/InteractionTools.cs:89-111`

## Problem

The `click_element` path accepts target identity as unrelated nullable fields: `Locator`, `ElementId`, and `WindowHandle`. Each layer repeats exactly-one validation and decides independently whether `windowHandle` applies.

## Consequence

Invalid or ambiguous click targets can cross protocol boundaries before being rejected, and future click behavior can drift between MCP tools, automation, and agent endpoints.

## Desired Outcome

`click_element` parses target identity into one internal target concept before automation execution while preserving the existing JSON request shape.

## Suggested Approach

Introduce a small internal value object or parser that returns either a locator target or an element-id target. Use it first in the `click_element` path, including the boundary decision for whether a window handle is required or inherited.

## Acceptance Criteria

- `click_element` accepts the existing JSON fields without a wire contract change.
- Missing target, mixed `locator` plus `elementId`, and element-id target without a usable window handle fail at the boundary with consistent invalid-request errors.
- Valid locator and valid element-id click paths still work.
- The new target type is internal implementation detail, not a public contract change.

## Validation

- Add or update focused tests for `click_element` locator target, element-id target, missing target, and mixed target cases.
- Run the focused interaction/tool-profile tests that cover `click_element`.

## QA Review

- 2026-05-06: Split from 002. This is the first representative slice because it proves the target abstraction on a single action path before broader migration.

## Notes

- 2026-05-06: Selected for implementation after `003c`. First validation target is focused `click_element` target parsing coverage for locator, element-id, missing target, and mixed target cases while preserving the existing request JSON shape.
- 2026-05-06: Implemented in `21d82b3` and refined in `44cfc34`. Added internal `ElementTarget` as a locator-or-element-id value object, wired `click_element` MCP request construction through it, reused it in automation click resolution, and routed agent endpoint target validation through the same parser while preserving public DTOs.
- 2026-05-06: Validation so far: `dotnet test tests\WpfToolsMcp.SnapshotTests\WpfToolsMcp.SnapshotTests.csproj -c Release --no-restore --filter "FullyQualifiedName~McpToolErrorsDesignTests|FullyQualifiedName~ElementTargetDesignTests|FullyQualifiedName~ClickElement_basic_button_updates_status_snapshot|FullyQualifiedName~ResolveElement_uia_then_click_by_elementId_snapshot"` passed with 10 tests. The command emits the known `System.Diagnostics.EventLog` version conflict warning.
- 2026-05-06: Review note: `dotnet format whitespace src\WpfToolsMcp.Automation\WpfToolsMcp.Automation.csproj --include src\WpfToolsMcp.Automation\AutomationController.cs --no-restore --verify-no-changes` reports broad pre-existing whitespace drift in `AutomationController.cs`; no formatting churn was applied for this item.
