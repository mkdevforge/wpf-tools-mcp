# Migrate Remaining Target-Bearing Operations

- ID: 002c
- Status: Pending
- Priority: P2
- Source: split from 002
- References:
  - `src/WpfToolsMcp.Contracts/Contracts.cs:437-527`
  - `src/WpfToolsMcp.McpServer/Tools/InteractionTools.cs`
  - `src/WpfToolsMcp.McpServer/Tools/InspectionTools.cs`
  - `src/WpfToolsMcp.McpServer/Tools/WaitTools.cs`
  - `src/WpfToolsMcp.Automation/AutomationController.Agent.cs:55-92`

## Problem

After the first target parser slice, many target-bearing operations still pass parallel nullable `locator` and `elementId` values and repeat target checks.

## Consequence

Adding or changing operations remains error-prone because each call path must remember the same target rules.

## Desired Outcome

Remaining target-bearing operations use the shared target parser or value object where it fits, with no public JSON contract break.

## Suggested Approach

Migrate operations in small groups, starting with interaction tools that share action semantics, then inspection and wait paths. Keep operation-specific rules, such as secondary targets for drag/select/scroll, explicit rather than forcing them into a one-size-fits-all abstraction.

## Acceptance Criteria

- Shared target parsing covers the primary target for invoke, type_text, set_value, wait_for, get_element_properties, get_uia_locators, and get_path_to_element where applicable.
- Drag, select, and scroll secondary targets keep clear operation-specific validation.
- Existing request JSON remains compatible.
- Duplicated exactly-one checks are materially reduced without hiding operation-specific invariants.

## Validation

- Run focused interaction, wait, and inspection snapshot tests covering locator and element-id requests.
- Add targeted invalid-target tests for at least one migrated operation outside `click_element`.

## QA Review

- 2026-05-06: Split from 002. This is intentionally after `002a` and `002b` because broad migration should happen only after the target parser and locator-shape validation are proven.
