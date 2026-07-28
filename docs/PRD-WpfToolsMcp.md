# WPF Tools MCP — WPF UI Agent MCP Server

> **Document status:** This is the original product and phased-delivery design
> record. It is retained for architectural context, not as the live tool or
> roadmap contract. The current usage reference is `README.md`, and the MCP
> server's advertised schemas are authoritative for individual tool inputs.

## Problem

When using AI coding assistants to build WPF applications, the developer is the visual bottleneck. The model writes XAML and code but has zero visibility into what the running application actually looks like or how it behaves. Every visual verification requires the developer to build, launch, navigate, screenshot, paste, and describe. This kills the iterative feedback loop that makes AI-assisted development productive.

## Solution

An MCP server that gives AI models the ability to see and interact with running WPF applications using a hybrid WPF-agent and FlaUI/UIA approach.

- **Snoop** (Ms-PL-licensed) is injected into the target process to provide deep WPF-native inspection of the real visual tree, live binding status, DataContext objects, dependency property values, styles, and templates. Snoop.Core already handles hard WPF edge cases that would be costly to reimplement.
- **FlaUI** (UIA3) operates out-of-process for window management, UIA inspection, semantic actions, and physical fallbacks. The WPF agent also assists with WPF handle resolution, scrolling, and supported native semantic actions.

This hybrid gives the AI model WPF-native inspection depth alongside the interaction capability of an automation framework, without requiring source changes to the target application.

### Snoop integration approach: Thin Wrapper (confirmed by feasibility spike)

A code-level analysis of the Snoop repository (commit `c1cc286`, 2025-12-21) confirmed that **Approach B (Thin Wrapper)** is the right path:

- `Snoop.Core` has **no project dependency** on the `Snoop` host/UI project and builds independently.
- However, `Snoop.Core.dll` is not a clean inspection-only library — it contains Snoop's WPF UI (windows, views, controls) compiled into the same assembly. We reference `Snoop.Core` but only call the inspection-oriented types, ignoring the UI surface.
- Visual-tree walking through `VisualTreeService` is **clean**: pure WPF types with no Snoop UI coupling.
- The feasibility spike identified Snoop property-grid types such as `PropertyInformation` as unsuitable pipe contracts. The current agent instead returns its own serializable DTOs and uses WPF binding APIs directly.
- Style/template inspection (`FrameworkElementHelper`, trigger model types) needs **thin wrappers** — relies on reflection into non-public WPF members (`ThemeStyle`, `TemplateInternal`), which works but is a WPF version compatibility risk.
- The injection mechanism (`Snoop.InjectorLauncher` + `Snoop.GenericInjector`) is **already generic** — it accepts arbitrary assembly/type/method arguments, so we point it at our own `WpfToolsMcp.Agent` entry point.

The injected agent (`WpfToolsMcp.Agent`) is a thin assembly that:

1. Is loaded into the target process via Snoop's injection mechanism
2. Starts an asynchronous named pipe server in the target process
3. Receives inspection requests and marshals WPF operations to the target's `Dispatcher`
4. Calls Snoop.Core and WPF inspection APIs, wrapping results in DTOs
5. Serializes and returns results over the pipe

## Repository: `wpf-tools-mcp`

**Organization:** mkdevforge  
**Source license:** MIT<br>
**Packaged Snoop payload:** Ms-PL<br>
**Target framework:** .NET 8+  
**MCP SDK:** `ModelContextProtocol` (official C# SDK)  
**Automation:** FlaUI (UIA3) out-of-process, with agent-assisted WPF paths
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
                          │     │  - UIA coverage        │ │
                          │     │  - Binding status      │ │
                          │     │  - DataContext          │ │
                          │     │  - Dependency props     │ │
                          │     │  - Styles & templates   │ │
                          │     └───────────────────────┘ │
                          └───────────────────────────────┘
```

The MCP server manages both channels in Phase 2. Backend-neutral inspection tools route through the named pipe for deep WPF data and fall back to FlaUI/UIA when they have a UIA equivalent. WPF-only diagnostics require the agent. Interaction is semantic-first across WPF-native and UIA patterns, with foreground and physical-input fallbacks only where needed and permitted. The MCP tool surface remains unified.

---

## MCP Tools

The tables below describe the full `diagnostics` profile. The default `core`
profile intentionally exposes a smaller 25-tool surface with compact schemas;
see `README.md` for the current profile split and configuration.

### Phase 1 — Inspection (FlaUI / UIA)

| Tool | Description | Returns |
|---|---|---|
| `list_windows` | Enumerate all windows of the target process | Window titles, handles, dimensions, process info |
| `list_displays` | List connected displays and virtual screen bounds (multi-monitor diagnostics) | Virtual screen bounds + per-display bounds |
| `take_screenshot` | Capture the target window or a specific element (defaults: `captureMode=auto`, `autoScroll=true`, `includeOverlay=false`). Supports optional annotation (`annotate` + `annotation*`). | File path + image metadata (`width`, `height`, `format`), optional Base64 payload |
| `get_visual_tree` | Return an inspection tree (UIA or WPF) for the main window or a subtree | Structured JSON. Configurable depth. `visibleOnly=true` means **in-viewport**; use `includeOffViewport=true` to include offscreen elements. |
| `find_elements` | Find elements without dumping the full tree | Matches with element summaries and optional `elementId`s |
| `resolve_element` | Resolve one element and return an `elementId` handle for re-use | ElementRef (includes `elementId`, XPath, bounds, etc.) |
| `get_path_to_element` | Get the XPath for a resolved element | XPath string |
| `pick_element_at_point` | Pick an element at a coordinate (`coordSpace`: screen/client) | ElementRef + optional ancestor chain |
| `highlight_element` | Highlight an element on-screen. Can optionally return an annotated screenshot (`returnScreenshot=true`). | Highlight result + bounds + method used |
| `get_element_properties` | Inspect a single element via UIA | Bounded UIA properties, supported patterns, current values, and property-count/truncation metadata. The summary preset is the default; diagnostics can request the full preset with an explicit limit. Property and pattern values cap strings at 2,000 characters, collections at 50 items, and nesting at depth 2, and share a 20,000-character serialized-value budget. XPaths over 2,000 characters are explicitly omitted rather than truncated. |
| `get_uia_locators` | Export stable UIA locator recommendations for a WPF or UIA element | WPF/UIA identity, ranked locators, and FlaUI snippets |
| `get_uia_tree` | Return a bounded UIA automation tree for a window or subtree | UIA identity, paths, and children |

### Phase 1 — Interaction (WPF / UIA)

| Tool | Description | Parameters |
|---|---|---|
| `click_element` | Click semantic-first, with mouse fallback when allowed | Locator strategy + optional click type (single/double/right) and click mode |
| `mouse_click` | Send physical mouse input at a coordinate (Playwright-style) | `x`, `y`, `coordSpace` (screen/client), button, clickType |
| `type_text` | Set text semantically, with keyboard input fallback when allowed | Locator + text |
| `set_value` | Set a value through WPF-native or UIA value semantics | Locator + value |
| `select_item` | Select semantically, with mouse fallback when allowed | Locator + item identifier (`text`, `index`, or `itemLocator`) |
| `invoke` | Invoke through WPF-native or UIA semantic patterns | Locator |
| `scroll_to_element` | Scroll a container to bring an element into view | Locator of the target element |
| `drag` | Send physical pointer input from an element to another element or screen coordinates | Source locator/elementId + target locator/elementId or `toX/toY` |
| `wait_for` | Wait for an element to satisfy a state | Locator/elementId + state + timeout |
| `get_active_window` | Get the active window for this session | `sessionId` |
| `set_active_window` | Bring a window to the foreground and set it as the session’s active window | `sessionId` + window handle or title |
| `set_window_bounds` | Move/resize a window by setting its bounds (outer window rectangle) | `sessionId` + optional `windowHandle` + `x/y/width/height` |
| `set_window_state` | Set a window state (normal/minimized/maximized) | `sessionId` + optional `windowHandle` + state |

### Phase 1 — App Lifecycle

| Tool | Description | Parameters |
|---|---|---|
| `launch_app` | Start a WPF application and create a session | Executable path, optional arguments, working directory, interaction policy |
| `attach_to_app` | Attach to an already-running process without activating it | Process name or PID, interaction policy |
| `close_session` | Close a session (and close the attached application) | `sessionId` + graceful close with optional force kill timeout |
| `list_sessions` | List active sessions, effective interaction policies, and confirmed backend capability state | None |

### Desktop Interaction Policy

Read-only inspection, attachment, and screenshots do not activate the target or
send physical input. A session receives an `interactionPolicy` at launch or
attach. The compatibility default permits both foreground activation and
physical input. A nullable policy supplied to an individual interaction or
window operation overrides only its non-null fields; omitted fields inherit the
session values.

The strict background policy is
`{ "allowForegroundActivation": false, "allowPhysicalInput": false }`.
Semantic WPF/UIA actions remain available under that policy. If an operation
would need forbidden foreground activation or physical input, it fails before
that fallback with `interaction_policy_blocked`. Callers can use a
narrow per-operation override, `set_active_window` for intentional activation,
or an explicit physical operation such as `mouse_click`, `drag`, or
`click_element` with `clickMode: "mouseAlways"`.

Interaction responses expose `Effects` for the mechanism actually used:
`Semantic`, `ForegroundActivated`, `WindowRestored`, `MouseInput`,
`KeyboardInput`, and `CursorMoved`; where available, `MethodUsed` identifies the
specific path. The policy and effects cover automation performed by this
server. Code reached by a semantic invocation can independently open, restore,
or activate the application's own windows.

### Phase 2 — Upgraded inspection (Snoop, in-process)

Phase 2 enriches existing tools and adds new ones. When the Snoop agent is available, inspection tools can return deeper WPF-native data. Backend-neutral tools fall back to UIA where an equivalent exists; WPF-only diagnostics return a clear injection or connection error.

**Upgraded tools:**

| Tool | Phase 2 enhancement |
|---|---|
| `get_visual_tree` | Returns the real WPF visual tree (not UIA): actual CLR types, visibility, and DataContext type. Configurable depth. Falls back to UIA tree if agent unavailable. |
| `get_element_properties` | Resolves UIA or WPF targets and returns bounded UIA properties and supported patterns. Use the diagnostics profile for the full property preset, and use `get_computed_properties` for WPF dependency-property values and value sources. |

**New tools (Phase 2 only):**

| Tool | Description | Returns |
|---|---|---|
| `inject_agent` | Inject the in-process (Snoop-based) agent | Injection status |
| `agent_ping` | Ping the injected agent | Ping result |
| `release_element` | Explicitly release a reusable element handle | Release result |
| `get_binding_info` | Inspect bindings on an element | For each binding: path, source, mode, converter, current value, status (Active/Error/Detached), and error message if broken |
| `get_binding_errors` | List broken or non-active bindings in the current visual tree | Binding path, target element/property, binding status, and available validation error details |
| `subscribe_binding_errors` | Subscribe to binding errors (poll-based) | Subscription ID |
| `poll_subscription` | Poll queued subscription events | Batch of events |
| `unsubscribe` | Unsubscribe a subscription | Unsubscribe result |
| `get_data_context` | Serialize the DataContext of an element | JSON representation of the DataContext object, its type, and property values. Configurable depth to avoid serializing the entire object graph. |
| `get_computed_properties` | Inspect computed dependency property values | Effective values + optional value-source details |
| `get_style_chain` | Inspect the applied style chain | Style/ThemeStyle and BasedOn chain summary |
| `get_template_info` | Inspect the applied template | Template summary + optional named parts |
| `uia_coverage_report` | Report UIA automation coverage gaps | Findings + suggestions (e.g., missing AutomationPeers/patterns) |
| `performance_start` | Start lightweight UI-thread latency sampling | Run ID |
| `performance_stop` | Stop a performance run | Summary |
| `trace_start` | Start MCP tool tracing | Trace ID |
| `trace_stop` | Stop tool tracing and write the complete JSON trace | Trace summary + output path; inline events are opt-in and bounded by `maxEvents` |

### Element Locator Strategies

Primary element locators combine supplied identity fields as filters rather than
treating them as independent fallback strategies. Specialized nested locators,
such as `select_item.itemLocator`, have tool-specific schemas and semantics.

```json
{
  "automationId": "SaveButton",
  "name": "Save",
  "className": "Button"
}
```

If multiple elements match a strict locator, the server returns an ambiguity
error and asks the caller to narrow the query or provide an index. `xpath` and
`index` cannot be used together. AutomationId is the preferred stable identity
when an application exposes one.

---

## Test Applications

The repository uses separate, deterministic WPF executables rather than one
large app with scenario pages:

- `WpfToolsMcp.TestApp`: the primary basic-controls fixture.
- `WpfToolsMcp.TestApp.Minimal`: fallback locators and ambiguity without stable
  AutomationIds.
- `WpfToolsMcp.TestApp.BindingErrors`: binding and DataContext diagnostics.
- `WpfToolsMcp.TestApp.BrokenAutomation`: controls with missing UIA peers.
- `WpfToolsMcp.TestApp.CustomControls`: user controls and templated controls.
- `WpfToolsMcp.TestApp.DataGrid`: editing, selection, and complex traversal.
- `WpfToolsMcp.TestApp.DeeplyNested`: deep paths and traversal limits.
- `WpfToolsMcp.TestApp.Dialogs`: modal windows and window targeting.
- `WpfToolsMcp.TestApp.DynamicContent`: changing trees and stale handles.
- `WpfToolsMcp.TestApp.FocusProbe`: foreground ownership, cursor preservation,
  activation counters, and semantic versus physical fallback behavior.
- `WpfToolsMcp.TestApp.Scroll`: off-viewport discovery and scrolling.
- `WpfToolsMcp.TestApp.Tabs`: tab selection with nested selectable content.
- `WpfToolsMcp.TestApp.TreeView`: hierarchical selection.

The fixtures start in known states. Stable AutomationIds are used where they
are part of the scenario; other fixtures intentionally omit or break UIA
metadata to exercise fallback and error behavior.

**UIA limits:** A control without a useful AutomationPeer or actionable pattern
may remain visible to WPF-native inspection while being unavailable to UIA
interaction.

---

## Testing Strategy

`tests/WpfToolsMcp.SnapshotTests` is the single NUnit and Verify integration
test project. It builds and launches the focused WPF fixtures and starts the
real stdio MCP server. Coverage includes:

- tool-profile composition and compact-schema contracts;
- session lifecycle, restart/reconnect, active-window recovery, and element
  handle recovery;
- UIA and WPF tree inspection, locator export, properties, bindings,
  DataContext, styles, templates, and coverage diagnostics;
- clicks, invocation, typing, value setting, selection, drag, scrolling, and
  waits;
- screenshots, annotations, highlighting, display coordinates, traces,
  subscriptions, and performance sampling;
- expected failures for missing peers, ambiguous locators, stale elements,
  unavailable injection assets, and protocol errors.

Approved text and image snapshots live under
`tests/WpfToolsMcp.SnapshotTests/Snapshots`. UI tests that share desktop state
are non-parallel and use STA where WPF requires it. The optional
`tools/WpfToolsMcp.McpSmokeRunner` performs a black-box run against an explicit
target executable and writes evidence under `artifacts/smoke`.

Current build, focused-test, full-test, and smoke commands are documented in
`README.md`.

---

## Scope — What This Is Not

- **Not a general Windows automation tool.** WPF only. Win32/WinForms/UWP support is not a goal.
- **Not a testing framework.** The MCP server enables AI-driven interaction, not a replacement for Appium, FlaUI test suites, or Coded UI. The test infrastructure is for testing *the MCP server itself*.
- **No pre-emptive caching.** The server queries on every tool call. Latency is irrelevant compared to the developer round-trip it replaces.
- **Phase 1 is not a throwaway.** FlaUI/UIA remains the permanent automation baseline and the fallback for backend-neutral inspection when injection is unavailable. The WPF agent now also assists selected interaction paths.
- **Not a fork of Snoop (Phase 2).** We reference `Snoop.Core` and `Snoop.InjectorLauncher` as dependencies and wrap their inspection classes in a thin DTO layer. The Snoop UI types compiled into `Snoop.Core.dll` are unused. If a future `Snoop.Core.Inspection` package is published, we can switch to that.

## Key Dependencies and Risks

### Phase 1 risks

- **UIA automation tree is a simplified projection.** It doesn't expose bindings, DataContext, dependency property sources, styles, or the full visual tree. This limits what the AI model can diagnose — but it's still far more than manual screenshots. Phase 2 addresses this.
- **AutomationId coverage varies.** UIA-based locators depend on controls having `AutomationProperties.AutomationId` set. Many real-world apps don't set these consistently. The locator system must support fallback strategies (Name, ClassName, XPath-like paths).

### Phase 2 risks (confirmed by feasibility spike)

- **Snoop.Core contains UI code.** `Snoop.Core.dll` is not a clean inspection library — it includes Snoop's WPF windows, views, and controls. We reference the assembly but only call inspection-oriented types. This means a larger-than-necessary dependency; a future optimization could extract only the needed classes, but this is not worth doing upfront.
- **`PropertyInformation` is a DependencyObject.** Snoop's primary inspection class sets up WPF bindings to keep property values live-updated for its UI grid. The agent must wrap these in plain DTOs and avoid leaking `PropertyInformation` instances across the named pipe boundary.
- **Binding detail is best effort.** The .NET 8 agent inspects `BindingExpression` status and available validation errors without rewriting bindings. Some failures expose a non-active status without a detailed error message.
- **Reflection into non-public WPF internals.** Style/template inspection uses non-public members (`ThemeStyle`, `TemplateInternal`, `Style.IsBasedOnModified`) via reflection. This works on current WPF versions but is a compatibility risk on future versions. These tools should degrade gracefully if reflection fails.
- **Dispatcher marshalling.** All Snoop.Core inspection operations must run on the owning element's `Dispatcher`. The agent must enforce this for every request. Snoop provides `RunInDispatcher()` extension methods we can reuse.
- **Multi-dispatcher applications.** WPF apps can have multiple `Dispatcher` instances. The agent must detect and handle this (Snoop has `SnoopModes.MultipleDispatcherMode` guards).
- **Snoop.Core bundles PowerShell integration.** The `Snoop.Core.csproj` includes `System.Management.Automation` references. These types won't be called by our agent but the assemblies may need to be present at load time. Needs verification during P2-M0.
- **Injection and security software.** DLL injection via `CreateRemoteThread` + `VirtualAllocEx` can trigger endpoint protection. This is a development-time tool and should be documented accordingly.
- **Injector .NET version gaps.** Snoop's `ProcessWrapper` framework detection maps to `"net462"` or `"net6.0-windows"` only; .NET 5 targets throw. Acceptable since we target .NET 8+, but worth noting for future Framework support.
- **Release-build diagnostics vary.** The agent reports the binding state and validation details exposed by the target process; applications can expose less diagnostic detail in some configurations.

---

## Original Milestone Plan

This section preserves the delivery plan used to build the project. It is not the
current roadmap: some planned public tool names were consolidated or split, and
trace/performance support has since shipped.

---

### Phase 1 — FlaUI (out-of-process automation)

Phase 1 delivers a fully functional MCP server that can see and interact with WPF applications through Microsoft UI Automation. No injection, no in-process code, no modification to the target app. The inspection is limited to what UIA exposes (the automation tree, not the full WPF visual tree), but this is already far more than what developers have today (manually screenshotting and pasting into chat).

#### P1-M0 — Walking skeleton
- MCP server starts, registers tools, communicates via stdio
- `launch_app`, `attach_to_app`, `close_session` working
- `list_windows` returns window info
- `take_screenshot` for the target window
- Test app with BasicControls page
- One passing Verify snapshot test

#### P1-M1 — See (UIA inspection)
- `get_visual_tree` returning the UIA automation tree (element type, AutomationId, Name, ClassName, BoundingRectangle, IsEnabled, IsOffscreen). Configurable depth.
- `get_element_properties` — bounded UIA property presets and supported patterns for a single element
- `take_screenshot` for individual elements (not just full window)
- All locator strategies working (AutomationId, Name, ClassName, XPath-like, index)
- Snapshot tests for all inspection tools

#### P1-M2 — Interact
- `click_element`, `type_text`, `set_value`, `select_item`, `invoke`
- Playwright-like robustness: `wait_for` (attached|visible|enabled|actionable|stable|value_equals|name_contains)
- Pointer interactions: `drag` (for sliders, splitters, reorder, etc.)
- `scroll_to_element`, `set_active_window`
- Element handles: `resolve_element` returns an `elementId` handle for re-use across subsequent tool calls (and `find_elements` can include `elementId` values). `uia_...` handles are validated best-effort (XPath + RuntimeId) while `wpf_...` handles are soft (XPath-based) and may go stale if the visual tree changes.
- Test app expanded with all pages (DataGrid, Navigation, DeeplyNested, DynamicContent, Dialogs, CustomControls)
- Integration flow tests: launch → inspect → interact → re-inspect → verify state changed
- Error handling: element not found, ambiguous locator, process not running, stale references

#### Phase 1 exit criteria
At this point, an AI model can launch a WPF app, see its automation tree and screenshots, interact with controls, and verify results. This is a complete, useful tool. Phase 2 is an enhancement, not a prerequisite.

---

### Phase 2 — Snoop (in-process WPF inspection)

Phase 2 injects a lightweight agent into the target process using Snoop's injection mechanism. This upgrades inspection from UIA's simplified automation tree to the real WPF visual tree, live binding diagnostics, DataContext, dependency properties with value sources, and style/template inspection. FlaUI remains the primary automation path, with agent-assisted WPF actions where appropriate.

The MCP tool surface is extended — new tools are added and existing inspection tools are enriched with deeper data. The AI model doesn't need to know which backend serves which tool.

#### P2-M0 — Injection + pipe
- `WpfToolsMcp.Agent` assembly with `Start(string pipeName)` entry point
- Injection via `Snoop.InjectorLauncher` into the test app
- Named pipe established between MCP server and injected agent
- Agent can walk the visual tree via Snoop's `VisualTreeService` and return a basic JSON response
- Verify PowerShell assembly dependency doesn't block agent loading
- One passing Verify snapshot test comparing Snoop visual tree to UIA tree

#### P2-M1 — Deep inspection (DTO wrappers)
- DTO layer wrapping `PropertyInformation` and related Snoop types into serializable models
- Dispatcher marshalling enforced on all inspection requests
- `get_visual_tree` upgraded: returns real WPF visual tree with actual CLR types, Visibility, DataContext type (falls back to UIA tree if agent not injected)
- Planned tool: `get_logical_tree` (not shipped as a separate public tool)
- Planned `get_element_properties` dependency-property upgrade (shipped instead as `get_computed_properties`)
- New tool: `get_binding_info` — per-element binding details (path, source, mode, converter, status, error)
- New tool: `get_binding_errors` — broken and non-active bindings across the tree
- Snapshot tests for all upgraded/new inspection tools

#### P2-M2 — Deep diagnostics
- New tool: `get_data_context` with configurable serialization depth and cycle detection
- Planned `get_styles` tool (shipped as `get_style_chain` and `get_template_info`)
- Separate BindingErrors and CustomControls test applications exercising Snoop-specific capabilities
- Graceful fallback for backend-neutral inspection; WPF-only diagnostics report injection failures directly

#### Post-Phase 2 (future considerations)
- SSE transport for remote scenarios
- Visual diff tool (screenshot comparison as MCP tool)
- Accessibility audit tool (check for missing automation properties)
- Trace + performance capture (delivered as `trace_start` / `trace_stop` and `performance_start` / `performance_stop`)
- .NET Framework 4.x target support
- Live property editing (change values through Snoop agent for rapid iteration)
- Extract minimal inspection classes from Snoop.Core into standalone library (reduce dependency footprint)

---

## Appendix

- The Snoop feasibility findings are summarized in the integration section of
  this document. The original analysis was based on Snoop commit `c1cc286`
  (2025-12-21); no separate feasibility-report file is tracked in this repository.
