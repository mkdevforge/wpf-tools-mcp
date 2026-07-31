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

### Local trust model

This is a same-user local development tool, not a multi-tenant backend. Reading
live WPF and UIA state normally invokes framework getters and provider code, and
bounded diagnostic formatting may invoke application-defined `ToString()` or
`Exception.Message`. Those calls are best effort and caught when they fail. The
correctness boundaries remain strict identity validation, bounded work and
artifacts, cancellation, cleanup limited to MCP-owned resources, and honest
reporting of the capabilities the current target actually exposes.

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
                          │     │  - Command status      │ │
                          │     │  - DataContext          │ │
                          │     │  - Dependency props     │ │
                          │     │  - Layout context       │ │
                          │     │  - Styles & templates   │ │
                          │     └───────────────────────┘ │
                          └───────────────────────────────┘
```

The MCP server manages both channels in Phase 2. Backend-neutral inspection tools route through the named pipe for deep WPF data and fall back to FlaUI/UIA when they have a UIA equivalent. Known application-owned native windows route directly to FlaUI/UIA rather than failing WPF window resolution. WPF-only diagnostics require the agent. Interaction is semantic-first across WPF-native and UIA patterns, with foreground and physical-input fallbacks only where needed and permitted. The MCP tool surface remains unified.

---

## MCP Tools

The tables below describe the full `diagnostics` profile. The default `core`
profile intentionally exposes a smaller 34-tool surface with compact schemas;
see `README.md` for the current profile split and configuration.

### Phase 1 — Inspection (FlaUI / UIA)

| Tool | Description | Returns |
|---|---|---|
| `list_windows` | Enumerate all windows of the target process | Native window captions (UI Automation name fallback), handles, dimensions, process info, owner HWND, nullable modal state, and UIA framework identity |
| `list_displays` | List connected displays and virtual screen bounds (multi-monitor diagnostics) | Virtual screen bounds + per-display bounds |
| `take_screenshot` | Capture the target window or a specific element (defaults: `captureMode=auto`, `autoScroll=true`, `includeOverlay=false`). Supports optional annotation (`annotate` + `annotation*`), viewport evidence (`includeViewport=true`), and diagnostics-only point/region correlation (`correlation`). | File path + image metadata (`width`, `height`, `format`), optional Base64 payload, `ViewportConditions`, and bounded WPF/UIA correlation evidence |
| `get_visual_tree` | Return an inspection tree (UIA or WPF) for the main window or a subtree | Structured JSON. Configurable depth. `visibleOnly=true` means **in-viewport**; use `includeOffViewport=true` to include offscreen elements. |
| `find_elements` | Find elements without dumping the full tree; minimal or standard result context | Deterministically ordered matches, returned/discovered/scanned counts, truncation metadata, and optional `elementId`s |
| `resolve_element` | Resolve one element and return an `elementId` handle for re-use | ElementRef on success; non-XPath ambiguity returns an `ambiguous_element` tool error with up to five index-addressable candidates containing reusable identity and bounded observed type/name/AutomationId/path/bounds evidence; ambiguous XPath segments return a path-specific indexing error |
| `get_path_to_element` | Get the XPath for a resolved element | XPath string |
| `pick_element_at_point` | Pick an element at a coordinate (`coordSpace`: screen/client) | ElementRef + optional ancestor chain |
| `highlight_element` | Highlight an element on-screen. Can optionally return an annotated screenshot (`returnScreenshot=true`). | Highlight result + bounds + method used |
| `get_element_properties` | Inspect a single element via UIA | Bounded UIA properties, supported patterns, current values, and property-count/truncation metadata. The summary preset is the default; diagnostics can request the full preset with an explicit limit. Property and pattern values cap strings at 2,000 characters, collections at 50 items, and nesting at depth 2, and share a 20,000-character serialized-value budget. XPaths over 2,000 characters are explicitly omitted rather than truncated. |
| `get_uia_locators` | Export stable UIA locator recommendations for a UIA element or explained bounded WPF-to-UIA mappings when `backend=Wpf` | Reusable WPF/UIA IDs and identity on success; exact/heuristic/ambiguous/unmapped status, integer score, symbolic evidence, bounded candidates, ranked locators, and FlaUI snippets |
| `get_uia_tree` | Return a bounded UIA automation tree for a window or subtree | UIA identity, paths, and children |
| `capture_diagnostic_snapshot` | Capture an explicit bounded set of tree, UIA property, WPF property, layout, binding, DataContext, binding-error, and screenshot evidence for one pinned target | Shared session/process/window/element context; a single-dispatcher WPF capture group; timestamped UIA/screenshot phases; per-section success, unavailable, truncated, or failed results |

### Phase 1 — Interaction (WPF / UIA)

| Tool | Description | Parameters |
|---|---|---|
| `click_element` | Click semantic-first, with mouse fallback when allowed | Locator strategy + optional click type (single/double/right) and click mode |
| `mouse_click` | Send physical mouse input at a coordinate (Playwright-style) | `x`, `y`, `coordSpace` (screen/client), button, clickType |
| `type_text` | Replace, append, or enter text at the current selection, using semantic WPF/UIA paths before keyboard fallback | Optional locator/elementId + text + optional mode |
| `send_keys` | Send ordered physical keys and modifier chords to the focused or specified element | Optional locator/elementId + 1-100 structured key strokes |
| `set_value` | Set a value through WPF-native or UIA value semantics | Locator + value |
| `select_item` | Select semantically, with mouse fallback when allowed | Locator + item identifier (`text`, `index`, or `itemLocator`) |
| `realize_item` | Explicitly realize one provider-observed virtualized UIA item without foreground or physical input | ItemContainer locator/elementId plus exactly one provider-order index or exact UIA Name; bounded provider calls and advisory elapsed/poll controls |
| `invoke` | Invoke through WPF-native or UIA semantic patterns | Locator |
| `scroll_to_element` | Scroll a container to bring an element into view | Locator of the target element |
| `drag` | Send physical pointer input from an element to another element or screen coordinates | Source locator/elementId + target locator/elementId or `toX/toY` |
| `wait_for` | Wait for a typed element, WPF value, or same-process window condition; compatibility string states remain supported | Locator/elementId or window selector + advertised condition + bounded timeout; returns backend, reason code, and last observed value |
| `get_active_window` | Get the active window for this session | `sessionId` |
| `set_active_window` | Bring a window to the foreground and set it as the session’s active window | `sessionId` + window handle or title |
| `set_window_bounds` | Move/resize a window by setting its bounds (outer window rectangle) | `sessionId` + optional `windowHandle` + `x/y/width/height` |
| `set_window_viewport` | Set an exact client-area size and report the resulting physical, logical, DPI, monitor, and constraint conditions | `sessionId` + `clientWidth/clientHeight` + `unit` (`physicalPixels` or `wpfDips`) + optional window/policy controls |
| `set_window_state` | Set a window state (normal/minimized/maximized) | `sessionId` + optional `windowHandle` + state |

### Phase 1 — App Lifecycle

| Tool | Description | Parameters |
|---|---|---|
| `launch_app` | Start a WPF application and create a session | Executable path, optional arguments, working directory, interaction policy |
| `attach_to_app` | Attach to one unambiguous process without activating it, or replace an exited session with a fully initialized successor | Process name, PID, or candidate `processInstanceId`; optional exited `sessionId`; interaction policy |
| `detach_session` | Remove inspection state and release client resources without stopping the application | `sessionId` |
| `close_app` | Request a graceful application close, remove the session, and report request/process outcomes separately | `sessionId` + timeout |
| `terminate_app` | Forcefully terminate the application, remove the session, and report the observed process outcome | `sessionId` + timeout |
| `close_session` | Compatibility path for the historical close-with-optional-force behavior | `sessionId` + optional force and timeout |
| `list_sessions` | Passively observe active sessions, effective interaction policies, and confirmed backend capability state without initializing WPF; unavailable backend states can include `FailureInfo` | None |

Session removal, a graceful close request, force termination, and observed
process exit are distinct lifecycle facts. Normal inspection cleanup uses
`detach_session`; it must not send close, shutdown, or kill requests to the
target. `close_app` and `terminate_app` make those side effects explicit, while
their responses distinguish caller intent, actual close dispatch or force
attempt, and observed process exit. The deprecated `Closed` field mirrors
`ProcessExited` for those explicit tools; `close_session` alone preserves the
historical `Closed = true` response after session removal. Detach also reports
whether its before/after process-state probes succeeded so an unobservable state
is not reported as a confirmed exit.

A session represents exactly one process instance, keyed by PID plus process
start time. Process-name attachment returns structured candidates rather than
silently choosing when multiple live instances match. Candidate retries use an
opaque `processInstanceId` so PID reuse or candidate exit fails as
`stale_process_candidate` without falling through to another process.
The `launch_app` existing-instance fallback uses the same structured ambiguity
result instead of choosing an existing process by recency.

Replacing an exited session creates a fresh controller, session ID, and active
window history. The successor is fully attached and its main window is pinned
before the predecessor is atomically retired. Predecessor subscriptions are
stopped after that registry commit and before the response is returned, so a
failed pre-commit replacement does not remove them. The durable interaction
policy is inherited unless explicitly overridden. Retired session IDs remain
as bounded tombstones and report `stale_session`; every prior HWND and element
ID is explicitly stale and must be reacquired from the successful replacement
response. The successor also recognizes transferred predecessor identities and
reports `stale_window` or `stale_element` without exposing last-known target
details. Ambiguous or failed preparation is atomic and leaves the predecessor
unchanged.

`list_sessions` may verify or reconnect to an already-running WPF agent, but it
does not inject one. WPF initialization occurs only for an explicit WPF
operation or an inspection path whose automatic-injection behavior is enabled.
`BackendCapabilities` lists only confirmed-ready backends;
`BackendCapabilityStates` distinguishes `ready`, `unavailable`, and
`not_initialized`, with an optional structured failure for unavailable states.

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
or an explicit physical operation such as `mouse_click`, `drag`, `send_keys`,
or `click_element` with `clickMode: "mouseAlways"`.

Interaction responses expose `Effects` for the mechanism actually used:
`Semantic`, `ForegroundActivated`, `WindowRestored`, `MouseInput`,
`KeyboardInput`, `CursorMoved`, and the optional `KeyboardFocusChanged`; where
available, `MethodUsed` identifies the specific path. Text and keyboard input
responses also distinguish foreground-focus and physical-input requirements
from effects that occurred. The policy and effects cover automation performed by this
server. Code reached by a semantic invocation can independently open, restore,
or activate the application's own windows.

### Virtualized Item Realization Contract

`realize_item` is a UIA-only mutation through `ItemContainerPattern` and
`VirtualizedItemPattern`. The explicit call itself communicates intent; no
additional acknowledgement flag is required. It accepts exactly one container
locator or registered element ID and exactly one zero-based provider-order
index or exact UIA Name. Name is forwarded unchanged under provider-defined
equality. Provider order is not represented as a data-source index, and Name
ambiguity is limited to matches the provider exposes.

Every `FindItemByProperty` call counts against `maxProviderCalls`. Name lookup
probes for a second provider-observed match and then reacquires the unique first
item before realization. The operation recognizes an already realized item and
rejects an out-of-tree placeholder that lacks `VirtualizedItemPattern`. It does
not inject, activate the foreground window, send physical input, or approximate
realization with a scrolling loop.

Default bounds are 100 provider calls, a 5,000 ms advisory elapsed limit, and a
50 ms postcondition poll interval. Diagnostics callers can select 1-1,000
provider calls, 1-60,000 ms advisory elapsed time, and a 10-1,000 ms poll
interval. Elapsed checks occur between provider calls and polls; they are not a
hard timeout for one blocking provider call or `Realize()`.

After `Realize()` is invoked, its response always preserves mutation evidence:
requested identity, method, invocation and postcondition state, provider and
poll counts, elapsed time, stop/recovery reason, and whether viewport or
data/container loading may have changed. A reusable handle requires fresh path
reacquisition plus process, window, and runtime-identity verification. Missing,
changed, or recycled identity suppresses the handle and sets `reusable=false`;
it does not turn an invoked mutation into an opaque failure.
Verified handles keep the existing action-time identity checks for
inspection, selection, scroll-to, and element-screenshot workflows.

### Application-Owned Native Window Contract

The session window model includes WPF windows and application-owned native
top-level HWNDs in the attached process. `OwnerHandle`, nullable `IsModal`, and
`FrameworkId` preserve enough context to identify a common dialog without
depending on localized captions. For a known native target, `Auto` inspection,
locator-based screenshot targeting, and interaction targeting use UIA directly.
Explicit WPF selection remains WPF-only.

Active-window reconciliation follows a live owned modal dialog and restores the
most recent live owner or main HWND after the dialog closes. A window handle is
stable only for that live-window interval. A previously observed destroyed
handle reports `window_closed`; an HWND from another process reports
`window_outside_session`; and a live native HWND without a usable UIA root
reports `window_uia_unavailable`.

This support is deliberately narrower than general Windows automation. The
native window must belong to the attached WPF process and expose useful UIA.
Brokered system pickers, secure-desktop surfaces, cross-process dialogs, and
owner-drawn controls without UIA are not inferred to be part of the session.

### Wait Condition Contract

The advertised `wait_for.condition` object replaces undiscoverable free-form
state expansion while preserving all historical state strings. It includes
typed element conditions (`Attached`, `Visible`, `Enabled`, `Actionable`,
`BoundsStable`, `NumericValueEquals`, and `NameContains`), WPF
`DependencyPropertyValue` and `DataContextValue` comparisons, and same-process
`WindowOpen`/`WindowClosed` selectors. A request supplies either `state` or
`condition`, never both. The `condition.kind` discriminator exposes a separate
schema for each variant, including its required operands and only the fields it
accepts. MCP calls default to throwing on a legacy timeout and returning a
structured result for a typed timeout.

WPF value operands are closed scalar values (`String`, `Number`, `Boolean`, or
`Null`), not expressions. Their string comparison is ordinal, numeric values
must be finite, and no coercion or caller-supplied code is evaluated. The
element-oriented `NameContains` condition retains the compatibility
case-insensitive match. The WPF
agent resolves the allowlisted dependency property or dotted DataContext path
once and uses change notifications. Held comparisons use the observation
timeline and are reset by a mismatch or delivery gap.

`BoundsStable` observes only exact element bounds (`x`, `y`, `width`, and
`height`) for the requested hold duration. Pixel equality, animation quiescence,
and whole-render stability are outside this contract. Generic UIA child counts
are also excluded: virtualization makes realized-child counts different from
application collection counts. Callers should expose or observe a bounded
application scalar such as `Items.Count` through DataContext or a dependency
property.

Timeout evidence includes `BackendUsed`, `ElapsedMs`, `Attempts`, stable
`ReasonCode`/`FailureReason` values, `LastObservation`, and
`LastObservedValue`. Process exit, agent connection loss, and WPF target unload
are terminal and distinguishable from timeout. Effective timeouts are clamped
to 0-60,000 ms, poll intervals to 25-2,000 ms, and hold durations are restricted
to 0-5,000 ms. WPF comparisons reuse bounded target-side event queues; UIA
element and Win32 window checks use bounded polling.

Each Win32 sample stops after 2,048 desktop HWNDs or 128 same-process visible
candidates. Native handle, title, and owner filters run before UIA, and at most
16 prefiltered candidates may require a framework-ID probe. A limit produces an
explicit scan/probe error instead of a partial absence result.

An exact-handle close wait captures a best-effort native identity from the HWND,
owning thread, window class, and owner. A different identity at the same numeric
handle counts as replacement of the original window. Windows provides no HWND
generation token, so immediate reuse on the same thread with the same class and
owner cannot be distinguished and is conservatively treated as still open.

### Deterministic Viewport Contract

`set_window_viewport` is a diagnostics-profile operation for responsive-layout
review. It sizes the client area rather than treating the outer window frame as
the viewport. The caller supplies a positive `clientWidth` and `clientHeight`
in either physical pixels or WPF device-independent pixels. Physical pixels are
the default unit. WPF DIPs are converted to the target window's logical/render
pixels using its window-effective DPI, then to final screen pixels through the
target HWND's DPI-virtualization mapping. This preserves the 1/96-inch WPF unit
for unaware, system-aware, and per-monitor-aware windows. The response reports
window-effective DPI and monitor-effective DPI separately.

The non-intrusive defaults are `ensureForeground=false` and
`clampToWorkArea=false`. Callers can explicitly request work-area clamping or a
foreground transition. The normal session and operation `interactionPolicy`
rules apply when foreground activation is requested.

The response contains:

- `Requested`: the caller's unit and size normalized to physical pixels and
  WPF DIPs, including the requested client bounds;
- `Actual`: client and outer physical bounds, physical and logical client
  sizes, frame insets, separate window and monitor DPI scales, DPI-awareness
  context, monitor bounds/work area, and window state;
- `Adjustment`: the applied physical size, physical and DIP deltas, exact-match
  and clamping flags, minimum-size status, resize attempts, and structured
  constraint reasons;
- `Effects`: any foreground activation or restore performed by the operation.

`take_screenshot(includeViewport=true)` returns the same `ViewportConditions`
with the image metadata. Screenshot evidence can therefore be compared using
the actual client size and DPI conditions instead of an approximate outer
window size. Capture conditions are sampled immediately before and after the
bitmap operation and must match; unstable attempts are discarded and retried,
then reported as `screenshot_viewport_unstable` if stability cannot be reached.

In the diagnostics profile, the optional `correlation` object maps a
capture-local physical-pixel point or region to bounded WPF/UIA candidates.
`backend=Both` preserves candidates from both trees, overlapping matches remain
explicit, and optional nearest-first ancestors add context without a full tree
dump. A point query reports one canonical physical screen point shared by both
backends, including when capture scaling maps one image pixel to multiple screen
pixels. Candidate annotations are enabled by default and are drawn into the
returned artifact. Correlation always includes stable viewport, window, DPI,
capture-mode, clipping, and obscuration context. It defaults to 8 candidates
and 10,000 scanned nodes per backend, no ancestors, and at most 4 ancestors per
candidate when enabled; the public hard caps are 25 candidates per backend,
200,000 nodes per backend, and 20 ancestors per candidate. Returned, discovered,
and scanned counts plus truncation metadata distinguish bounded evidence from
a complete scan.

### Phase 2 — Upgraded inspection (Snoop, in-process)

Phase 2 enriches existing tools and adds new ones. When the Snoop agent is
available, inspection tools can return deeper WPF-native data. Backend-neutral
tools fall back to UIA where an equivalent exists. UIA fallbacks in auto tree,
search, and resolve responses include structured WPF-to-UIA metadata, while
tree and search retain compatibility warning text. WPF-only diagnostics return
a stable bounded failure code and detail; callers can inspect `list_sessions`
for the full structured backend failure state.

Every tool advertises a success-or-error `outputSchema`. Tool execution failures
set `isError=true` and return `{ "error": { "code", "detail", ... } }` as
structured content plus fixed `code: detail` compatibility text. The envelope
can add optional `stage`, `retryable`, `retryAfterMs`, `recoveryActions`, and
validated session/window/element/backend or bounded candidate/count/truncation
context. An optional bounded cause retains observed exception type/message and
adapter details. Candidate context can include bounded process/window/element
names, paths, times, and bounds. Embedded backend `FailureInfo` values retain the
same retry semantics and cause evidence in capability and fallback state; a
throwing message getter is represented by a bounded unavailable reason.
Malformed JSON-RPC, unknown tools, request cancellation, and server lifecycle
failures remain protocol errors. Bounded local paths, injector output, target
exception messages, and adapter-provided remote details are diagnostic evidence.

Successful response metadata is intentionally targeted rather than a universal
envelope. A one-shot inspection resolved relative to a window reports the
actual `windowHandleUsed`; a composite diagnostic snapshot keeps it under
`target.windowHandle`. New `backendUsed` metadata appears only when backend
selection is a meaningful part of the operation. Compatibility fields already
exposed by fixed-backend tools, including `get_validation_errors`, remain.
Optional `fallback` follows the existing auto-routing convention: it is
present only when that route has a fallback decision to report and is omitted
from fixed-backend and ordinary no-fallback responses. Element-targeted
`take_screenshot` results report the actual routed backend and any used
fallback; untargeted window captures omit both fields.
Deep WPF observations advertise `wpf/inspection-response-metadata:v1`; an
already-injected agent without that capability is rejected with restart and
reattach guidance instead of being normalized into invented completeness.

Bounded inspection metadata distinguishes payload size from discovery work:
`returned*` is the exact serialized collection count, `discovered*` is what was
observed before discovery stopped, and `scanned*` is the work actually
inspected. `scanComplete=false` means discovered totals are lower bounds.
Truncation is true when evidence or requested scope was omitted; exactly
filling a limit is not sufficient. Ordered `truncatedReasons` report every
applicable budget where the response supports multiple reasons. Where a legacy singular
`truncatedReason` remains, it is the first ordered reason.
Discovery-affecting target-side property failures add
`propertyInspectionUnavailable` and force `scanComplete=false`; their
discovered totals are lower bounds rather than invented exhaustive counts.

| Response | Required bounded metadata semantics |
|---|---|
| `get_binding_info` | `returnedBindings` equals `bindings.Count`; `discoveredBindings` counts bindings observed; `scannedProperties` counts dependency properties inspected; `scanComplete` distinguishes a complete property scan. `truncatedReasons` reports all omitted evidence while singular `truncatedReason` remains the first-reason compatibility field. |
| `get_command_info` | `returnedContexts` counts the effective routed-command target (or source fallback) plus returned nearest-first public WPF parents. Discovered and returned command/input binding counts remain separate; `truncatedReasons` identifies parent or shared binding-budget omissions. Getter, formatting, gesture, and `CanExecute` failures use structured states rather than guessed values. |
| `get_binding_errors` | `returnedErrors` equals `errors.Count`; `discoveredErrors` includes errors omitted from the payload; `scannedNodes` is traversal work; `scanComplete` and ordered `truncatedReasons` distinguish node-scan and returned-error limits. |
| `uia_coverage_report` | `summary.returnedFindings` equals `findings.Count`; `summary.discoveredFindings` includes omitted findings; `summary.discoveredIssueCounts` counts all discovered findings rather than only the returned subset; `summary.scannedNodes`, `summary.scanComplete`, and ordered `summary.truncatedReasons` describe traversal completeness. Existing `summary.findingsCount` and `summary.issueCounts` describe the returned subset. |
| `get_computed_properties` | `returnedProperties` equals `properties.Count`; `discoveredProperties` counts values matching the request before the response cap; `scannedProperties` is actual property work; `scanComplete` and ordered `truncatedReasons` remain separate from nested provenance limits. |
| `get_data_context` | The response identifies the resolved `element` and `windowHandleUsed`. Ordered `truncatedReasons` identify graph-depth, per-object property, and string omissions instead of relying on warnings alone. |
| `get_style_chain` | Each style entry reports `returnedBasedOnStyles`, `discoveredBasedOnStyles`, `basedOnScanComplete`, `basedOnTruncated`, and the effective `maxBasedOnDepth`; the returned count matches `basedOnChainTargetTypes.Count`. |
| `get_template_info` | When named elements are requested, `returnedNamedElements` equals the serialized list count; `discoveredNamedElements`, `namedElementsScanComplete`, `namedElementsTruncated`, and effective `maxNamedElements` describe the bounded enumeration. |

`find_elements` deliberately keeps its established contract unchanged:
`ReturnedMatches`, `DiscoveredMatches`, `ScannedNodes`, `Truncated`, and singular
`TruncatedReason` retain their current exact-versus-lower-bound semantics.

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
| `get_binding_info` | Inspect bindings on an element | Per-binding path, source, mode, converter, value, status, and error detail plus truthful returned/discovered/property-scan metadata |
| `get_command_info` | Inspect a WPF command source without executing it | Source command/parameter/target, separate `IsEnabled` and `CanExecute`, and bounded nearest-first instance `CommandBinding`/`InputBinding` context starting at the effective routed target (or source) with typed key/mouse gestures and structured unavailable states |
| `get_binding_errors` | List broken or non-active bindings in the current visual tree | Binding path, target element/property, status, validation detail, and returned/discovered/scan-completeness metadata with ordered truncation reasons |
| `get_validation_errors` | Read the current `Validation.Errors` attached state in a bounded visual-tree scope without invoking validators | Deterministic element/error order; exact, best-effort, or unavailable source evidence; bounded binding, content, exception, and adorner evidence; returned/discovered/scan counts and ordered truncation reasons |
| `subscribe_binding_errors` | Subscribe to binding errors (poll-based) | Subscription ID and effective poll, queue, and whole-event bounds |
| `subscribe_property_changes` | Observe an allowlist of dependency properties and dotted DataContext paths on one WPF element using target-side change notifications | Subscription ID, effective bounds, selected watches, and start/expiry metadata |
| `poll_subscription` | Poll bounded, versioned subscription events and delivery-loss metadata | Per-stream ordered event batch; canonical and compatibility loss counters; typed terminal event and retained completion state |
| `unsubscribe` | Unsubscribe a subscription | Unsubscribe result |
| `get_data_context` | Serialize the DataContext of an element | Resolved element/window identity, DataContext type and JSON values, plus ordered bounded-serialization reasons. Configurable depth avoids serializing the entire object graph. |
| `get_computed_properties` | Inspect computed dependency property values | Effective values, optional value-source details, and returned/discovered/scanned/completeness metadata distinct from nested provenance limits |
| `get_layout_context` | Explain a WPF element's current layout using bounded relational evidence | Target metrics, nearest-first ancestors, relevant siblings, Grid allocation/definitions, transforms, clipping, DPI/physical bounds, unavailable evidence, and deterministic counts/truncation |
| `get_style_chain` | Inspect the applied style chain | Style/ThemeStyle summary plus per-entry bounded `BasedOn` counts, completeness, truncation, and effective depth |
| `get_template_info` | Inspect the applied template | Template summary plus optional named parts with returned/discovered counts, completeness, truncation, and effective limit |
| `uia_coverage_report` | Report UIA automation coverage gaps | Findings and suggestions plus returned/discovered counts, discovered issue counts, scan completeness, and ordered truncation reasons |
| `performance_start` | Start lightweight UI-thread latency sampling | Run ID |
| `performance_stop` | Stop a performance run | Summary |
| `trace_start` | Start MCP tool tracing | Trace ID |
| `trace_stop` | Stop tool tracing and write a bounded JSON trace | Trace summary + output path; newest 1,000 events retained with observed/retained/dropped counts; inline events are opt-in and bounded by `maxEvents` |

Subscription and trace events emitted by the MCP server use an additive version-1
runtime envelope with UTC observation time, source kind, session and stream IDs,
and a monotonic sequence scoped to that stream. Optional live window/element/path
identity is bounded, and overlong paths are omitted rather than truncated.
Sequences do not define cross-stream or causal ordering. Subscription loss counters
have explicit per-poll and cumulative forms, whole serialized events are subject to
the negotiated payload budget, and completion is represented by exactly one typed
terminal event while compatibility completion fields remain pollable during the
retention window. Trace retention loss and inline response truncation are reported
separately. The correlation contract is limited to MCP-owned events; it does not
collect application logs or install unhandled-exception hooks.

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

If multiple elements match a strict non-XPath locator, the server returns a
structured ambiguity error with bounded index and reusable-element-ID candidates,
and asks the caller to narrow the query or provide an index. An ambiguous XPath
segment instead asks the caller to add a one-based
`[n]` index to that segment; `xpath` and locator `index` cannot be used together.
AutomationId is the preferred stable identity when an application exposes one.

---

## Test Applications

The repository uses separate, deterministic WPF executables rather than one
large app with scenario pages:

- `WpfToolsMcp.TestApp`: the primary basic-controls fixture.
- `WpfToolsMcp.TestApp.Minimal`: fallback locators and ambiguity without stable
  AutomationIds.
- `WpfToolsMcp.TestApp.ObservationProbe`: ordered short-lived dependency-property
  and DataContext transitions, queue pressure, truncation, primary/secondary
  dispatcher routing, and lifecycle cleanup.
- `WpfToolsMcp.TestApp.ProvenanceProbe`: local, metadata-default, inherited,
  bound, explicit/implicit/theme-styled, static/dynamic/ambiguous-resource,
  template-triggered, animated, and coerced dependency-property origins, plus
  bounded priority/multi-binding, element/ancestor/application/merged resource
  scopes, deferred BAML, unsafe resource-key, and truncated-value evidence
  cases.
- `WpfToolsMcp.TestApp.BindingErrors`: binding and DataContext diagnostics.
- `WpfToolsMcp.TestApp.BrokenAutomation`: controls with missing UIA peers.
- `WpfToolsMcp.TestApp.CustomControls`: user controls and templated controls.
- `WpfToolsMcp.TestApp.DataGrid`: editing, selection, and complex traversal.
- `WpfToolsMcp.TestApp.DeeplyNested`: deep paths and traversal limits.
- `WpfToolsMcp.TestApp.LayoutProbe`: deterministic spacing, Pixel/Auto/Star
  Grid allocation, dedicated spacer columns, splitter ownership, implicit
  cells, empty and bounded clipping, transforms, z-order, and comparable
  window-DIP/physical-screen bounds.
- `WpfToolsMcp.TestApp.LifecycleProbe`: deterministic process exit, same-name
  multi-instance candidate reporting, successor-session recovery, stable UIA
  identities, graceful-close veto, and child-process lifecycle coverage.
- `WpfToolsMcp.TestApp.Dialogs`: WPF modal windows plus a deterministic native
  open-file dialog with owner/modal metadata, UIA semantics, and lifecycle
  restoration.
- `WpfToolsMcp.TestApp.DynamicContent`: changing trees and stale handles.
- `WpfToolsMcp.TestApp.FocusProbe`: foreground ownership, cursor preservation,
  activation counters, and semantic versus physical fallback behavior.
- `WpfToolsMcp.TestApp.Scroll`: off-viewport discovery and scrolling.
- `WpfToolsMcp.TestApp.Tabs`: tab selection with nested selectable content.
- `WpfToolsMcp.TestApp.TreeView`: hierarchical selection.
- `WpfToolsMcp.TestApp.VirtualizedItems`: bounded ItemContainer realization,
  provider-observed duplicate Names, and recycling/stale-identity behavior.
- `WpfToolsMcp.TestApp.ViewportProbe`: independent WPF logical-size, physical
  client-size, DPI, minimum-size, and application-coercion verification.

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
- session lifecycle, MCP-server reconnect, target-process replacement,
  same-name candidate ambiguity, process-instance selection, active-window
  recovery, and stale session/window/element identity reporting;
- UIA and WPF tree inspection, locator export, properties, bindings,
  DataContext, computed-property provenance, layout context, styles, templates,
  and coverage diagnostics;
- clicks, invocation, typing, value setting, selection, drag, scrolling, and
  waits;
- application-owned native common-dialog discovery, Auto-to-UIA routing,
  semantic open/cancel workflows, strict physical-input rejection, owner
  restoration, and stale/foreign HWND errors;
- screenshots, annotations, highlighting, deterministic viewport sizing and
  DPI context, display coordinates, traces, subscriptions, and performance
  sampling;
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

- **Not a general Windows automation tool.** The target remains a WPF process.
  Same-process native HWNDs owned by that application are supported only as
  bounded dialog workflow surfaces; general Win32/WinForms/UWP automation is
  not a goal.
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
- **Property provenance has explicit evidence tiers.** Public WPF value-source
  flags, binding state, animation base values, and metadata facts can be exact.
  Style, template, and resource contributors are bounded candidates when WPF
  does not expose a winner. Static-resource origin, inheritance provider,
  animation clock, and pre-coercion value remain unavailable rather than being
  inferred. Theme-style and dynamic-resource details use guarded implementation
  access and degrade per section if that access changes. Bounded resource
  candidate scans read existing `FrameworkElement`/`FrameworkContentElement`
  uncommon fields and `Style`/`FrameworkTemplate`/`Application` backing fields
  (holding the WPF application resource lock where required), then inspect raw
  dictionary storage. They never call lazy `Resources` getters, copy an
  unbounded key set, or inflate deferred values; their scan evidence remains
  best effort even when the bounded scope scan completes. Missing or changed
  internal storage access makes only the resource section incomplete.
- **Provenance request and rendering work is bounded.** Opt-in calls inspect at
  most 100 explicit property names, bound each name before the agent pipe, and
  cap `MissingPropertyNames` accordingly. Effective values use invariant WPF
  formatters where available and bounded best-effort application `ToString()`
  otherwise. Formatter failures retain type identity and explicit unavailable
  evidence on the effective value and on style/template contributor values and
  conditions. A formatted default or animation value is exact only when it is
  complete; bounded text is best effort with `maxStringLength`.
- **DataContext change notifications are best effort.** Live state observation uses WPF bindings so dependency properties and `INotifyPropertyChanged` paths are event-driven. Plain CLR properties that emit no notification do not produce changes; the tool does not add invasive target polling.
- **Live observation work is capped across subscriptions.** Each subscription has bounded watches and queues, and the server admits at most eight live or completed-retained property subscription handles per session and 64 per server process. Completed handles free capacity on explicit unsubscribe or idle-grace retirement.
- **Live state observation requires a live WPF window source.** Explicit window handles are resolved to their owning `HwndSource` before traversal, including windows on secondary UI dispatchers; non-WPF or already-destroyed handles fail without attaching handlers.
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
- `launch_app`, `attach_to_app`, `detach_session`, `close_app`, and
  `terminate_app` working; `close_session` retained for compatibility
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
- `click_element`, `type_text`, `send_keys`, `set_value`, `select_item`,
  `realize_item`, `invoke`
- Playwright-like robustness: `wait_for` (attached|visible|enabled|actionable|stable|value_equals|name_contains)
- Pointer interactions: `drag` (for sliders, splitters, reorder, etc.)
- `scroll_to_element`, `set_active_window`
- Element handles: `resolve_element` returns an `elementId` handle for re-use across subsequent tool calls (and `find_elements` can include `elementId` values). Strict non-XPath ambiguity is a structured tool error whose bounded WPF/UIA candidates use the same deterministic ordering as locator `index`; ambiguous XPath segments retain their path-specific one-based indexing error. `uia_...` handles are validated best-effort (XPath + RuntimeId) while `wpf_...` handles are soft (XPath-based) and may go stale if the visual tree changes.
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
- `get_computed_properties` structured provenance (shipped as an opt-in,
  capability-gated diagnostics extension with bounded scan work and explicit
  exact/best-effort/unavailable evidence)
- New tool: `get_binding_info` — per-element binding details (path, source, mode, converter, status, error)
- New tool: `get_command_info` — read-only `ICommandSource` metadata,
  non-executing `CanExecute`, and bounded existing instance command/input
  bindings on the effective routed target (or source) and nearest public WPF
  parent chain
- New tool: `get_binding_errors` — broken and non-active bindings across the tree
- New tool: `get_validation_errors` — capability-gated, read-only current WPF
  validation state. Core fixes depth/error/node/value budgets at 6/100/2,000/500;
  diagnostics exposes those controls plus `visibleOnly`, with hard caps of
  100/1,000/200,000/2,000. The tool does not subscribe, focus, send input, or
  call `IDataErrorInfo`/`INotifyDataErrorInfo` directly.
- Snapshot tests for all upgraded/new inspection tools

#### P2-M2 — Deep diagnostics
- New tool: `get_data_context` with configurable serialization depth and cycle detection
- New tool: `get_layout_context` with compact default budgets and expanded diagnostics controls for WPF-only relational layout evidence
- New tool: `capture_diagnostic_snapshot` for bounded, read-only composite evidence. Requested WPF sections share one target-window dispatcher turn; UIA and screenshot phases report unavoidable timing skew instead of claiming cross-backend atomicity.
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
