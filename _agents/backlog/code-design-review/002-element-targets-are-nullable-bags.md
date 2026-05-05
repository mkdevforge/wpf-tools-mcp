# Element Targets Are Nullable Bags

- ID: 002
- Status: Split
- Priority: P2
- Source: review finding
- References:
  - `src/WpfToolsMcp.Contracts/Contracts.cs:54-66`
  - `src/WpfToolsMcp.Contracts/Contracts.cs:408-516`
  - `src/WpfToolsMcp.Automation/AutomationController.cs:1874`
  - `src/WpfToolsMcp.Agent/AgentEndpointValidation.cs:7-18`

## Problem

Primitive Obsession / Make Illegal States Unrepresentable: `ElementLocator` can represent incompatible shapes such as empty locator, index-only locator, xpath plus index, and mixed filter bag. Action request DTOs add a parallel `ElementId` target, causing repeated exactly-one validation throughout MCP tools, automation, and the injected agent.

## Consequence

Invalid target shapes cross boundaries and are rejected late in many places. Any new operation must duplicate target validation and may drift from existing behavior, increasing bugs around locator versus elementId handling.

## Desired Outcome

Element target validity is represented once at the boundary and passed through the system as a typed command concept, not as unrelated nullable fields.

## Suggested Approach

Introduce a small `ElementTarget` abstraction, for example `ElementTarget.ById` and `ElementTarget.ByLocator`, plus locator variants such as `XPath`, `Index`, and `Filter`. Migrate one operation path first, then expand to shared helpers after the shape proves useful.

## Acceptance Criteria

- At least one representative action path parses locator/elementId into a typed target before automation execution.
- Empty locators and incompatible locator combinations are rejected at the boundary with consistent errors.
- Existing JSON contracts remain compatible unless a deliberate breaking change is approved.
- Tests cover locator, elementId, and invalid mixed target cases.

## Notes

- This parent item is retained as provenance. Active work has been split into `002a`, `002b`, and `002c`.
- QA correction: index-only locators are intentionally supported by current UIA and WPF resolver paths. `xpath + index` is already rejected, but the rejection happens inside duplicated resolver logic instead of at a shared boundary.

## QA Review

- 2026-05-06: Partially Verified. The nullable `locator`/`elementId` target concern is factual and repeated across MCP, automation, and agent layers. The original scope was too broad for one item and overstated index-only locators as inherently invalid.
