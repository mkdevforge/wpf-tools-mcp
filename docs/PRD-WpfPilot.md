# WpfPilot — WPF UI Agent MCP Server

## Problem

When using AI coding assistants to build WPF applications, the developer is the visual bottleneck. The model writes XAML and code but has zero visibility into what the running application actually looks like or how it behaves. Every visual verification requires the developer to build, launch, navigate, screenshot, paste, and describe. This kills the iterative feedback loop that makes AI-assisted development productive.

## Solution

An MCP server that gives AI models the ability to see and interact with running WPF applications using a hybrid approach: **Snoop sees, FlaUI acts.**

- **Snoop** (Ms-PL-licensed) is injected into the target process to provide deep WPF-native inspection — the real visual and logical trees, live binding status with source/path/error details, DataContext objects, dependency property values, styles, and templates. Snoop.Core already handles the hard edge cases (frozen Freezables, virtualized panels, template parts, adorner layers) that would take months to reimplement.
- **FlaUI** (UIA3) operates out-of-process to handle all interaction — clicks, typing, selection, invocation, scrolling — through Microsoft UI Automation patterns. This is what FlaUI was designed for and it does it well.

This hybrid gives the AI model the inspection depth of a developer running Snoop alongside the interaction capability of an automation framework, without requiring any modification to the target application.

### Snoop integration approach: Thin Wrapper (confirmed by feasibility spike)

A code-level analysis of the Snoop repository (commit `c1cc286`, 2025-12-21) confirmed that **Approach B (Thin Wrapper)** is the right path:

- `Snoop.Core` has **no project dependency** on the `Snoop` host/UI project and builds independently.
- However, `Snoop.Core.dll` is not a clean inspection-only library — it contains Snoop's WPF UI (windows, views, controls) compiled into the same assembly. We reference `Snoop.Core` but only call the inspection-oriented types, ignoring the UI surface.
- Tree walking (`VisualTreeService`, `LogicalTreeService`) is **clean** — pure WPF types, no Snoop UI coupling.
- Dependency property enumeration and binding inspection (`PropertyInformation`, `BindingDiagnosticHelper`) need **thin wrappers** — they are shaped for Snoop's property-grid UI (e.g., `PropertyInformation` is itself a `DependencyObject`), so the agent wraps them in serializable DTOs and marshals calls to the correct `Dispatcher`.
- Style/template inspection (`FrameworkElementHelper`, trigger model types) needs **thin wrappers** — relies on reflection into non-public WPF members (`ThemeStyle`, `TemplateInternal`), which works but is a WPF version compatibility risk.
- The injection mechanism (`Snoop.InjectorLauncher` + `Snoop.GenericInjector`) is **already generic** — it accepts arbitrary assembly/type/method arguments, so we point it at our own `WpfPilot.Agent` entry point.

The injected agent (`WpfPilot.Agent`) is a thin assembly that:

1. Is loaded into the target process via Snoop's injection mechanism
2. Starts a named pipe server on the target's `Dispatcher`
3. Receives inspection requests from the MCP server
4. Calls Snoop.Core's inspection classes, wraps results in DTOs
5. Serializes and returns results over the pipe

## Repository: `wpf-pilot`

**Organization:** mkdevforge  
**License:** MIT  
**Target framework:** .NET 8+  
**MCP SDK:** `ModelContextProtocol` (official C# SDK)  
**Interaction:** FlaUI (UIA3) — out-of-process automation  
**Inspection:** Snoop.Core + Snoop.InjectorLauncher (Ms-PL) — in-process WPF introspection  
**Communication:** Named pipe between MCP server and injected Snoop agent

---

## Architecture

### Phase 1 — FlaUI only (out-of-process)

```
┌──────────────┐  stdio   ┌───────────────────────────────┐
│ AI Assistant │◄────────►│        MCP Server              │
└──────────────┘          │                               │
                          │  FlaUI (UIA3)                  │
                          │  ├─ Inspection (automation     │
                          │  │  tree, properties,          │
                          │  │  screenshots)               │
                          │  └─ Interaction (click, type,  │
                          │     select, invoke)            │
                          │           │                    │
                          └───────────┼────────────────────┘
                                      │ UI Automation (out-of-process)
                          ┌───────────┼────────────────────┐
                          │  Target WPF App                │
                          │  (unmodified)                  │
                          └────────────────────────────────┘
```

### Phase 2 — Snoop + FlaUI (hybrid)

```
┌──────────────┐  stdio   ┌───────────────────────────────┐
│ AI Assistant │◄────────►│        MCP Server              │
└──────────────┘          │                               │
                          │  FlaUI ─────► Interaction     │
                          │               (click, type,   │
                          │                select, invoke) │
                          │                               │
                          │  Named Pipe ◄──┐              │
                          │  (deep          │              │
                          │   inspection)   │              │
                          └────────────────┼──────────────┘
                                           │
                          ┌────────────────┼──────────────┐
                          │  Target WPF App               │
                          │     ┌──────────┴────────────┐ │
                          │     │  Injected Snoop Agent  │ │
                          │     │  - Visual tree         │ │
                          │     │  - Logical tree        │ │
                          │     │  - Binding status      │ │
                          │     │  - DataContext          │ │
                          │     │  - Dependency props     │ │
                          │     │  - Styles & templates   │ │
                          │     └───────────────────────┘ │
                          └───────────────────────────────┘
```

The MCP server manages both channels in Phase 2. Inspection tools route through the named pipe to the injected Snoop agent for deep WPF data, falling back to FlaUI/UIA if the agent is not available. Interaction tools always route through FlaUI. The AI model doesn't need to know which backend serves which tool — the MCP tool surface is unified.

---

## MCP Tools

### Phase 1 — Inspection (FlaUI / UIA)

| Tool | Description | Returns |
|---|---|---|
| `list_windows` | Enumerate all windows of the target process | Window titles, handles, dimensions, process info |
| `take_screenshot` | Capture the target window or a specific element | File path + image metadata (`width`, `height`, `format`), optional Base64 payload |
| `get_visual_tree` | Return the UIA automation tree of a window or subtree | Structured JSON: element type, AutomationId, Name, ClassName, BoundingRectangle, IsEnabled, IsOffscreen. Configurable depth. |
| `get_element_properties` | Inspect a single element via UIA | All UIA automation properties, supported patterns, current values |

### Phase 1 — Interaction (FlaUI)

| Tool | Description | Parameters |
|---|---|---|
| `click_element` | Click an element | Locator strategy + optional click type (single/double/right) |
| `type_text` | Type text into a focused or specified element | Locator + text. Supports ValuePattern or keyboard input fallback. |
| `set_value` | Set value directly via ValuePattern | Locator + value |
| `select_item` | Select item in combo box, list box, tab control | Locator + item identifier (`text`, `index`, or `itemLocator`) |
| `invoke` | Invoke a button or menu item via InvokePattern | Locator |
| `scroll_to_element` | Scroll a container to bring an element into view | Locator of the target element |
| `focus_window` | Bring a window to the foreground | Window handle or title |

### Phase 1 — App Lifecycle

| Tool | Description | Parameters |
|---|---|---|
| `launch_app` | Start a WPF application | Executable path, optional arguments, working directory |
| `attach_to_app` | Attach to an already-running process | Process name or PID |
| `close_app` | Close the attached application | Graceful close with optional force kill timeout |

### Phase 2 — Upgraded inspection (Snoop, in-process)

Phase 2 enriches existing tools and adds new ones. When the Snoop agent is injected, inspection tools return deeper WPF-native data. When the agent is not available, they fall back to Phase 1 (FlaUI/UIA) behavior transparently.

**Upgraded tools:**

| Tool | Phase 2 enhancement |
|---|---|
| `get_visual_tree` | Returns the real WPF visual tree (not UIA): actual CLR types, Visibility, DataContext type, full dependency property count. Configurable depth. Falls back to UIA tree if agent unavailable. |
| `get_element_properties` | All dependency properties with values, local vs inherited/default/style source, binding expressions, and current effective value. Falls back to UIA properties if agent unavailable. |

**New tools (Phase 2 only):**

| Tool | Description | Returns |
|---|---|---|
| `get_logical_tree` | Return the WPF logical tree | Structured JSON: lighter than visual tree, shows content structure without chrome/template internals |
| `get_binding_info` | Inspect bindings on an element | For each binding: path, source, mode, converter, current value, status (Active/Error/Detached), and error message if broken |
| `get_binding_errors` | List all broken bindings in the current visual tree | Binding path, target element, target property, error description. On .NET 6+, uses `System.Windows.Diagnostics.BindingDiagnostics` (non-invasive). On .NET Framework, reports binding status only (Active/Error/Detached) without full error messages to avoid invasive re-binding. |
| `get_data_context` | Serialize the DataContext of an element | JSON representation of the DataContext object, its type, and property values. Configurable depth to avoid serializing the entire object graph. |
| `get_styles` | Inspect applied styles and templates on an element | Style setters, triggers, template structure |

### Element Locator Strategies

Tools that target elements accept a `locator` object supporting multiple strategies, tried in order of specificity:

```json
{
  "automationId": "SaveButton",
  "name": "Save",
  "className": "Button",
  "xpath": "/Window/Grid/StackPanel[2]/Button[1]",
  "index": 0
}
```

The server resolves the most specific match. If multiple elements match, it returns an error with the count and suggests narrowing the query. AutomationId is preferred when available.

---

## Test Application

The repository includes a purpose-built WPF test application (`WpfPilot.TestApp`) designed to exercise every tool. It is not a demo — it is the project's primary testing surface.

### Test App Structure

The test app contains pages/views covering these scenarios:

- **BasicControls** — Buttons, text boxes, check boxes, radio buttons, combo boxes, sliders. Every control has a stable `AutomationId`. Used to verify `click_element`, `type_text`, `set_value`, `select_item`, `invoke`, and `get_element_properties`.
- **DataGrid** — Editable data grid with sorting and selection. Tests complex element traversal and interaction patterns.
- **Navigation** — Tab control or frame-based navigation with multiple pages. Verifies `get_visual_tree` depth handling and `scroll_to_element`.
- **DeeplyNested** — 10+ levels of nested containers with elements at various depths. Stress-tests tree traversal and locator resolution.
- **BindingErrors** — Page with intentional binding errors (misspelled property names, wrong types, missing DataContext). Verifies `get_binding_errors` and `get_binding_info` detect broken bindings via Snoop inspection.
- **DynamicContent** — Timer-driven content that adds/removes elements. Tests that the MCP server handles stale references gracefully.
- **Dialogs** — Modal and non-modal dialogs, message boxes. Tests window enumeration and multi-window interaction.
- **CustomControls** — User controls and templated controls to verify automation peer behavior with non-standard controls.

In addition, the repo contains focused, deterministic test apps to validate tricky UIA interactions without relying on the target app's control templates:

- **Tabs** (`WpfPilot.TestApp.Tabs`) — TabControl selection, including nested selectable controls to ensure `select_item` targets the intended container.
- **TreeView** (`WpfPilot.TestApp.TreeView`) — TreeView selection across hierarchical items.
- **Minimal** (`WpfPilot.TestApp.Minimal`) — No `AutomationId`s. Validates ambiguity handling and fallback locator strategies (`name`, `className`, `index`).
- **BrokenAutomation** (`WpfPilot.TestApp.BrokenAutomation`) — Includes a control that is intentionally not exposed via UIA to validate expected limitations and clean failure modes.

**UIA limits:** If a control is not exposed via UI Automation (no AutomationPeer / no useful patterns), locator-based tools cannot interact with it without breaking abstraction (e.g., image/coordinate hacks), which this project intentionally avoids.

### Test App Requirements

- The app should start in a known, deterministic state every launch
- The primary test app (`WpfPilot.TestApp`) should use unique, stable `AutomationId`s for interactive elements
- Focused apps may intentionally omit `AutomationId`s to validate fallback locators and failure modes
- Navigation between pages (if present) must be automatable (tab control or programmatic)
- No special instrumentation required — Snoop injection handles all introspection

---

## Testing Strategy: Snapshot tests with Verify

Approval testing with [Verify](https://github.com/VerifyTests/Verify) is the primary testing strategy. This is a natural fit because:

- MCP tool outputs (visual trees, element properties, binding errors) are structured data that's easy to snapshot but tedious to assert field-by-field.
- Screenshots can be snapshot-tested as image files — visual regressions become PR-visible diffs.
- The "approve once, detect drift" model matches how we want to validate automation output: we care that it's *correct and stable*, not that it matches some hand-written expected structure.

### Test Categories

**Tool output verification (snapshot tests):**
Each MCP tool gets snapshot tests that launch the test app, call the tool, and verify the output against an approved snapshot.

```
Tests/
├── Snapshots/                  # Approved snapshots (committed)
├── ListWindowsTests.cs
├── GetVisualTreeTests.cs       # Snapshot the real WPF visual tree
├── GetLogicalTreeTests.cs      # Snapshot the logical tree
├── GetElementPropertiesTests.cs# Dependency properties, local vs inherited
├── GetBindingInfoTests.cs      # Binding paths, sources, status
├── GetBindingErrorsTests.cs    # Broken bindings across the tree
├── GetDataContextTests.cs      # Serialized DataContext snapshots
├── GetStylesTests.cs           # Style setters, triggers, templates
├── TakeScreenshotTests.cs      # Snapshot as .verified.png
├── ClickElementTests.cs        # Snapshot state-after
├── TypeTextTests.cs
├── SelectItemTests.cs
├── LocatorResolutionTests.cs   # Verify locator strategies resolve correctly
└── InjectionTests.cs           # Verify Snoop agent injection and pipe comms
```

**Integration flow tests:**
Multi-step scenarios that exercise realistic workflows:

- Launch app → inject Snoop → inspect visual tree → find element → interact via FlaUI → re-inspect via Snoop → verify state changed
- Launch app → `get_binding_errors` → fix binding in code → rebuild → re-launch → verify binding resolved
- Launch app → take screenshot → compare to approved baseline
- Attach to running process → inject → inspect → close

**Error handling tests:**

- Element not found → clean error message
- Ambiguous locator → returns count and suggestions
- Process not running → appropriate error
- Stale element reference after dynamic content change → graceful recovery
- Snoop injection failure → falls back to FlaUI-only mode with degraded inspection
- Named pipe disconnection → reconnection or clear error

### Test Infrastructure

- Tests use `[OneTimeSetUp]` / `ClassInitialize` to launch the test app once per test class and share the MCP server connection.
- A test helper wraps MCP tool invocations so tests read like:

```csharp
var tree = await Mcp.CallTool<VisualTreeResult>("get_visual_tree", new
{
    depth = 3,
    root = new { automationId = "BasicControlsPage" }
});

await Verify(tree);
```

- Screenshot tests use Verify's image comparison with a configurable pixel tolerance to handle anti-aliasing differences across machines.

---

## Scope — What This Is Not

- **Not a general Windows automation tool.** WPF only. Win32/WinForms/UWP support is not a goal.
- **Not a testing framework.** The MCP server enables AI-driven interaction, not a replacement for Appium, FlaUI test suites, or Coded UI. The test infrastructure is for testing *the MCP server itself*.
- **No pre-emptive caching.** The server queries on every tool call. Latency is irrelevant compared to the developer round-trip it replaces.
- **Phase 1 is not a throwaway.** FlaUI-only mode is the permanent baseline. Phase 2 enhances inspection depth but Phase 1 remains the interaction layer and the fallback when injection isn't available.
- **Not a fork of Snoop (Phase 2).** We reference `Snoop.Core` and `Snoop.InjectorLauncher` as dependencies and wrap their inspection classes in a thin DTO layer. The Snoop UI types compiled into `Snoop.Core.dll` are unused. If a future `Snoop.Core.Inspection` package is published, we can switch to that.

## Key Dependencies and Risks

### Phase 1 risks

- **UIA automation tree is a simplified projection.** It doesn't expose bindings, DataContext, dependency property sources, styles, or the full visual tree. This limits what the AI model can diagnose — but it's still far more than manual screenshots. Phase 2 addresses this.
- **AutomationId coverage varies.** UIA-based locators depend on controls having `AutomationProperties.AutomationId` set. Many real-world apps don't set these consistently. The locator system must support fallback strategies (Name, ClassName, XPath-like paths).

### Phase 2 risks (confirmed by feasibility spike)

- **Snoop.Core contains UI code.** `Snoop.Core.dll` is not a clean inspection library — it includes Snoop's WPF windows, views, and controls. We reference the assembly but only call inspection-oriented types. This means a larger-than-necessary dependency; a future optimization could extract only the needed classes, but this is not worth doing upfront.
- **`PropertyInformation` is a DependencyObject.** Snoop's primary inspection class sets up WPF bindings to keep property values live-updated for its UI grid. The agent must wrap these in plain DTOs and avoid leaking `PropertyInformation` instances across the named pipe boundary.
- **Binding error detection is invasive on .NET Framework.** `BindingDiagnosticHelper.TrySetBindingError()` clears and re-applies bindings to force trace output. This is unacceptable for passive inspection. On .NET 6+, we use the non-invasive `BindingDiagnostics` API instead. On .NET Framework targets, we report binding status (Active/Error/Detached) but not full error messages.
- **Reflection into non-public WPF internals.** Style/template inspection uses non-public members (`ThemeStyle`, `TemplateInternal`, `Style.IsBasedOnModified`) via reflection. This works on current WPF versions but is a compatibility risk on future versions. These tools should degrade gracefully if reflection fails.
- **Dispatcher marshalling.** All Snoop.Core inspection operations must run on the owning element's `Dispatcher`. The agent must enforce this for every request. Snoop provides `RunInDispatcher()` extension methods we can reuse.
- **Multi-dispatcher applications.** WPF apps can have multiple `Dispatcher` instances. The agent must detect and handle this (Snoop has `SnoopModes.MultipleDispatcherMode` guards).
- **Snoop.Core bundles PowerShell integration.** The `Snoop.Core.csproj` includes `System.Management.Automation` references. These types won't be called by our agent but the assemblies may need to be present at load time. Needs verification during P2-M0.
- **Injection and security software.** DLL injection via `CreateRemoteThread` + `VirtualAllocEx` can trigger endpoint protection. This is a development-time tool and should be documented accordingly.
- **Injector .NET version gaps.** Snoop's `ProcessWrapper` framework detection maps to `"net462"` or `"net6.0-windows"` only; .NET 5 targets throw. Acceptable since we target .NET 8+, but worth noting for future Framework support.
- **.NET 6+ `BindingDiagnostics` requires `VisualDiagnostics` enabled.** This is on by default in debug builds but may be off in release. The agent should detect and report when this API is unavailable.

---

## Milestones

The project is split into two distinct phases. Phase 1 (FlaUI) delivers a complete, useful MCP server using only out-of-process UI Automation. Phase 2 (Snoop) layers in deep WPF-native inspection via injection. Each phase is independently shippable and testable.

---

### Phase 1 — FlaUI (out-of-process automation)

Phase 1 delivers a fully functional MCP server that can see and interact with WPF applications through Microsoft UI Automation. No injection, no in-process code, no modification to the target app. The inspection is limited to what UIA exposes (the automation tree, not the full WPF visual tree), but this is already far more than what developers have today (manually screenshotting and pasting into chat).

#### P1-M0 — Walking skeleton
- MCP server starts, registers tools, communicates via stdio
- `launch_app`, `attach_to_app`, `close_app` working
- `list_windows` returns window info
- `take_screenshot` for the target window
- Test app with BasicControls page
- One passing Verify snapshot test

#### P1-M1 — See (UIA inspection)
- `get_visual_tree` returning the UIA automation tree (element type, AutomationId, Name, ClassName, BoundingRectangle, IsEnabled, IsOffscreen). Configurable depth.
- `get_element_properties` — all UIA properties and supported patterns for a single element
- `take_screenshot` for individual elements (not just full window)
- All locator strategies working (AutomationId, Name, ClassName, XPath-like, index)
- Snapshot tests for all inspection tools

#### P1-M2 — Interact
- `click_element`, `type_text`, `set_value`, `select_item`, `invoke`
- `scroll_to_element`, `focus_window`
- Test app expanded with all pages (DataGrid, Navigation, DeeplyNested, DynamicContent, Dialogs, CustomControls)
- Integration flow tests: launch → inspect → interact → re-inspect → verify state changed
- Error handling: element not found, ambiguous locator, process not running, stale references

#### Phase 1 exit criteria
At this point, an AI model can launch a WPF app, see its automation tree and screenshots, interact with controls, and verify results. This is a complete, useful tool. Phase 2 is an enhancement, not a prerequisite.

---

### Phase 2 — Snoop (in-process WPF inspection)

Phase 2 injects a lightweight agent into the target process using Snoop's injection mechanism. This upgrades inspection from UIA's simplified automation tree to the real WPF visual/logical trees, live binding diagnostics, DataContext, dependency properties with value sources, and style/template inspection. Interaction still goes through FlaUI.

The MCP tool surface is extended — new tools are added and existing inspection tools are enriched with deeper data. The AI model doesn't need to know which backend serves which tool.

#### P2-M0 — Injection + pipe
- `WpfPilot.Agent` assembly with `Start(string pipeName)` entry point
- Injection via `Snoop.InjectorLauncher` into the test app
- Named pipe established between MCP server and injected agent
- Agent can walk the visual tree via Snoop's `VisualTreeService` and return a basic JSON response
- Verify PowerShell assembly dependency doesn't block agent loading
- One passing Verify snapshot test comparing Snoop visual tree to UIA tree

#### P2-M1 — Deep inspection (DTO wrappers)
- DTO layer wrapping `PropertyInformation` and related Snoop types into serializable models
- Dispatcher marshalling enforced on all inspection requests
- `get_visual_tree` upgraded: returns real WPF visual tree with actual CLR types, Visibility, DataContext type (falls back to UIA tree if agent not injected)
- New tool: `get_logical_tree`
- `get_element_properties` upgraded: dependency property values with local vs inherited/default/style source
- New tool: `get_binding_info` — per-element binding details (path, source, mode, converter, status, error)
- New tool: `get_binding_errors` — all broken bindings across the tree (using `BindingDiagnostics` on .NET 6+)
- Snapshot tests for all upgraded/new inspection tools

#### P2-M2 — Deep diagnostics
- New tool: `get_data_context` with configurable serialization depth and cycle detection
- New tool: `get_styles` — style setters, triggers, template structure (graceful degradation if non-public WPF member reflection fails)
- Test app BindingErrors and CustomControls pages exercising Snoop-specific capabilities
- Graceful fallback: if injection fails, all tools degrade to Phase 1 (FlaUI-only) behavior with a clear indication to the model

#### Post-Phase 2 (future considerations)
- SSE transport for remote scenarios
- Visual diff tool (screenshot comparison as MCP tool)
- Accessibility audit tool (check for missing automation properties)
- .NET Framework 4.x target support
- Live property editing (change values through Snoop agent for rapid iteration)
- Extract minimal inspection classes from Snoop.Core into standalone library (reduce dependency footprint)

---

## Appendix

- **Snoop feasibility report** — `snoop-feasibility-report.md` — full code-level analysis of Snoop repository dependency graph, capability matrix with coupling ratings, injection mechanism details, and recommended approach. Based on commit `c1cc286` (2025-12-21).
