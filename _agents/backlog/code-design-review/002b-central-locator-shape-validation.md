# Central Locator Shape Validation

- ID: 002b
- Status: Pending
- Priority: P2
- Source: split from 002
- References:
  - `src/WpfToolsMcp.Contracts/Contracts.cs:54-66`
  - `src/WpfToolsMcp.Automation/AutomationController.cs:8667-8737`
  - `src/WpfToolsMcp.Automation/AutomationController.cs:8780-8791`
  - `src/WpfToolsMcp.Agent/WpfVisualTreeInspector.cs:1628-1718`
  - `src/WpfToolsMcp.Agent/WpfVisualTreeInspector.cs:1785-1810`

## Problem

UIA and WPF resolver paths each rediscover whether an `ElementLocator` is empty, uses `xpath + index`, or has an invalid negative index.

## Consequence

Locator-shape errors are reported late and can drift between UIA and WPF paths, even though those shapes are protocol-boundary invariants.

## Desired Outcome

Locator shape is validated by one shared internal rule set before resolver-specific lookup logic runs.

## Suggested Approach

Add a central locator-shape validator that allows intentional index-only locators, rejects empty locators, rejects `xpath + index`, and rejects negative indexes. Replace duplicated shape checks in the representative UIA and WPF resolver entrypoints.

## Acceptance Criteria

- Empty locator, `xpath + index`, and negative `index` produce consistent invalid-request style errors.
- Index-only locators remain supported.
- UIA and WPF resolver behavior remains otherwise unchanged.
- Existing JSON contracts remain compatible.

## Validation

- Add or update focused tests covering empty locator, `xpath + index`, negative index, and valid index-only locator behavior.
- Run the focused locator/interaction snapshot tests affected by resolver validation.

## QA Review

- 2026-05-06: Split from 002. The original item treated locator shape and target identity as one task; this slice isolates locator-shape invariants without changing every operation target at once.
