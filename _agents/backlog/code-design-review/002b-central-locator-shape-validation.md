# Central Locator Shape Validation

- ID: 002b
- Status: Done
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

## Notes

- 2026-05-06: Selected for implementation after `002a`. First validation target is focused `ElementLocator` shape coverage for empty locator, `xpath + index`, negative index, and valid index-only locator behavior across the shared parser plus representative UIA/WPF resolver paths.
- 2026-05-06: Implemented in `897129e`. Added internal `ElementLocatorShape`, moved empty locator, `xpath + index`, and negative index checks into the shared parser, and reused it from representative UIA and WPF resolver entrypoints while preserving index-only resolution.
- 2026-05-06: Final validation passed with 7 tests: `dotnet test tests\WpfToolsMcp.SnapshotTests\WpfToolsMcp.SnapshotTests.csproj -c Release --no-restore --filter "FullyQualifiedName~ElementLocatorShapeDesignTests|FullyQualifiedName~LocatorStrategies_resolve_snapshot|FullyQualifiedName~ResolveElement_wpf_then_get_data_context_by_elementId_snapshot|FullyQualifiedName~ClickElement_name_with_index_updates_click_count_snapshot"`. The command emits the known `System.Diagnostics.EventLog` version conflict warning.
- 2026-05-06: Self-review result: happy with the implementation. The shared parser owns locator-shape invariants, WPF agent validation keeps typed `invalid_request` behavior, UIA rejects invalid shape before lookup, and resolver-specific not-found/ambiguous behavior is unchanged.
