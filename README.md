# WPF Tools MCP

WPF Tools MCP is a Windows-only MCP server for inspecting, controlling, and
diagnosing running WPF applications.

- **Out-of-process automation:** FlaUI/UIA3 provides window management, UI
  Automation inspection, screenshots, semantic actions, and physical fallbacks.
- **In-process WPF inspection:** an injected agent, built on Snoop, exposes the
  WPF visual tree, bindings, DataContext values, dependency properties, styles,
  templates, highlighting, UI-thread latency, and supported WPF-native semantic
  actions.
- **Transport:** MCP uses stdio; the server and injected agent communicate over
  a current-user-only named pipe.

## Requirements

- Windows 10 or 11.
- .NET 8 SDK to install or build the tool. Running an already-installed package
  requires the .NET 8 Desktop Runtime.
- Deep WPF inspection requires an x86 or x64 .NET 8+ WPF target running as the
  same user and at no higher elevation than the MCP server.

## Install

```powershell
dotnet tool install -g MkDevForge.WpfToolsMcp --version 0.1.0-preview.24
dotnet tool update -g MkDevForge.WpfToolsMcp --version 0.1.0-preview.24
```

Run the server directly with:

```powershell
wpf-tools-mcp
```

## MCP Client Configuration

The default `core` profile exposes the compact tool surface intended for normal
agent workflows:

```json
{
  "mcpServers": {
    "wpf-tools-mcp": {
      "command": "wpf-tools-mcp"
    }
  }
}
```

### Tool Profiles

| Profile | Tools | Purpose |
|---|---:|---|
| `core` (default) | 30 | Compact schemas, normal inspection and interaction, UIA locator export, and the most useful WPF diagnostics. WPF inspection is injected automatically when needed. |
| `diagnostics` | 53 | The full surface, including explicit injection, backend and screenshot controls, element picking/highlighting, subscriptions, traces, performance sampling, and window/display diagnostics. |

Enable the full profile with a command argument:

```json
{
  "mcpServers": {
    "wpf-tools-mcp": {
      "command": "wpf-tools-mcp",
      "args": ["--tool-profile", "diagnostics"]
    }
  }
}
```

The equivalent environment setting is
`WPF_TOOLS_MCP_TOOL_PROFILE=diagnostics`. Accepted command values are `core`
and `diagnostics`; `diagnostic` and `full` are aliases for `diagnostics`.

## Tool Surface

The `core` profile exposes:

- **Sessions and windows:** `launch_app`, `attach_to_app`, `detach_session`,
  `close_app`, `terminate_app`, `list_sessions`, `list_windows`,
  `set_active_window`. `close_session` remains as a compatibility path.
- **Inspection:** `take_screenshot`, `get_visual_tree`, `find_elements`,
  `resolve_element`, `get_element_properties`, `get_uia_locators`,
  `get_uia_tree`.
- **Interaction and synchronization:** `click_element`, `invoke`, `type_text`,
  `send_keys`, `set_value`, `select_item`, `scroll_to_element`, `drag`,
  `wait_for`.
- **WPF diagnostics:** `get_binding_info`, `get_binding_errors`,
  `get_data_context`, `get_computed_properties`, `get_layout_context`.

The `diagnostics` profile additionally exposes:

- `inject_agent`, `agent_ping`, `get_active_window`, `get_path_to_element`, and
  `release_element`.
- `pick_element_at_point`, `highlight_element`, `mouse_click`, `list_displays`,
  `set_window_bounds`, `set_window_viewport`, and `set_window_state`.
- `get_style_chain`, `get_template_info`, and `uia_coverage_report`.
- `subscribe_binding_errors`, `subscribe_property_changes`,
  `poll_subscription`, and `unsubscribe`.
- `trace_start`, `trace_stop`, `performance_start`, and `performance_stop`.

Its expanded `take_screenshot` schema also exposes capture controls and the
opt-in screenshot-correlation workflow described below.

### WPF Layout Context

`get_layout_context` is a bounded WPF-only relational snapshot for explaining
why an element occupies its current space. It reports target layout metrics,
nearest-first ancestors, relevant direct visual siblings, and allocation in
ancestor `Grid` panels. Sibling selection prioritizes `GridSplitter` and
grid-adjacent evidence; nested identities are compact and never allocate
reusable element handles. Selected siblings include bounds in both their own
parent and the common window coordinate space, plus physical screen bounds,
so gaps can be compared even when the elements have different parents.

WPF dimensions are DIPs unless a field is explicitly named
`ScreenBoundsPhysicalPixels`; DPI scales are returned beside both coordinate
systems. Configured `Auto` values and unbounded maximums use typed length
states rather than JSON `NaN` or `Infinity`. For Grid definitions,
`ConfiguredValue` is omitted for `Auto`, is a DIP size for `Pixel`, and is a
weight for `Star`. Explicit clipping, layout clipping, empty clip geometry,
`ClipToBounds`, layout transforms, render transforms, visual index, and panel
z-order remain distinct evidence. Missing or inapplicable fields are reported
with stable status/reason codes instead of zero-value guesses.

The compact profile fixes the budgets at 6 ancestors, 8 siblings, and 32 Grid
definitions. The diagnostics profile exposes those three controls, with agent
hard limits of 32, 128, and 256 respectively. Counts and ordered truncation
reasons distinguish discovered context from returned context. Unavailable
evidence is independently capped at 128 records; reaching it adds
`maxUnavailableEvidence` to `TruncatedReasons`.

The tool requires an agent that advertises the layout-context capability. If a
target still hosts an older injected agent, restart the target application,
start a new MCP session, and attach again; the server rejects the unsupported
call before sending it to that agent.

### Structured Dependency-Property Provenance

The `diagnostics` profile can add structured provenance to
`get_computed_properties`. It is opt-in, so existing calls and the compact
`core` schema keep their legacy response shape:

```json
{
  "sessionId": "session-id",
  "locator": { "automationId": "SaveButton" },
  "propertyNames": ["Background", "IsEnabled"],
  "includeProvenance": true,
  "maxProvenanceCandidates": 20
}
```

Each property reports the structured WPF base value source and its expression,
animation, coercion, and current-value flags. Participating sections can add
binding configuration and runtime status, style or template candidates,
resource candidates, inheritance, animation base value, coercion callback,
and default metadata. `includeSources=false` only omits the legacy flattened
`ValueSource`; requested structured provenance remains present.

Every conclusion carries field-specific evidence. `Exact` is reserved for
public WPF state, `BestEffort` marks a bounded candidate or implementation
detail, and `Unavailable` includes a stable machine-readable reason code.
Candidate lists are not presented as winners. In particular, WPF does not
retain exact static-resource origin, expose the winning style/template setter
or trigger, identify the inheritance provider or animation clock, or expose a
pre-coercion value. Dynamic-resource keys use an implementation detail and are
best effort. Unsafe custom resource keys are omitted rather than invoking
application-defined formatting.

Resource candidates cover bounded element, ancestor, application, and
pre-existing merged-dictionary scopes. The agent reads only already-existing
owner backing fields and raw dictionary storage through guarded implementation
access. It never calls lazy WPF `Resources` getters, creates a missing resource
collection, copies a whole dictionary, or realizes a deferred resource.
`ScanComplete` only describes that candidate scan; it does not upgrade a
candidate into exact WPF lookup origin or claim complete precedence/shadowing
analysis. A deferred value or an incompatible runtime marks the scan incomplete
with a stable reason. Even a complete scan therefore has `BestEffort` scan
evidence.

Provenance caps the outer property response at 100 entries with
`TruncatedReason=maxProvenanceProperties`. Explicit `propertyNames` are also
bounded to 100 entries and 512 characters each before the agent pipe, reported
as `maxProvenancePropertyNames` or `maxProvenancePropertyNameLength` when cut.
This also bounds `MissingPropertyNames`. `maxProvenanceCandidates` defaults to
20 and is clamped from 0 through 50. It bounds discovery work as well as returned
binding children and contributor/resource candidates. Style and template
sections expose declaration counts; resource sections expose the single
decrementing `ScanAttempts` budget plus dictionary and entry counts.
`ScanComplete=false` means discovered counts are lower bounds. Budget exhaustion
sets `TruncatedReason=maxProvenanceCandidates`; an unavailable section that
failed safely is incomplete but is not mislabeled as budget-truncated.

Effective values keep useful invariant summaries for explicitly supported WPF
types such as `Thickness`, `CornerRadius`, `GridLength`, colors, font values,
and common geometry structs. Unknown application objects fall back to their
type identity without invoking application-defined `ToString()` or virtual
`Type` name members. Truncated binding details, default values, or animation
base values carry `BestEffort/maxStringLength` evidence rather than `Exact`
evidence.

Provenance requires an agent advertising
`wpf/get_computed_properties:provenance-v1`. When a target still hosts an older
agent, the server rejects the opt-in request before writing the property call
to that agent. Restart the target application, start a new MCP session, and
attach again so the current agent is injected.

### Live WPF State Observation

`subscribe_property_changes` observes one resolved WPF element for a bounded
duration. Supply exactly one of `locator` or `elementId`, plus an allowlist of
dependency-property names, DataContext paths, or both. DataContext paths use
dotted identifiers such as `Phase` or `Nested.Mode`.

The target agent attaches WPF change notifications on the element dispatcher.
For an explicit window handle, it resolves the owning WPF `HwndSource` before
traversal, so windows on secondary UI dispatchers are observed and released on
their own dispatcher. The tool does not sample at `cadenceMs`: that value only
controls how often the MCP server drains the target's bounded queue. A 30 ms
transition can therefore be captured even when delivery occurs every 250 ms.
The first poll returns
`property_initial` events followed by ordered, timestamped `property_changed`
events containing structured old and new values. Optional visual metadata adds
bounds, visibility, and enabled state at observation time.

Both target and server queues are bounded. `Dropped`, `Coalesced`, and
`Truncated`, together with their cumulative totals, make lost, merged, or
shortened evidence explicit. `maxValueLength` bounds scalar values and
`maxPayloadChars` bounds serialized event payloads per poll. Completion remains
pollable for a 60-second idle grace period, renewed by each poll, or until
`unsubscribe`; detaching or ending the session releases the target-side handlers
as part of session cleanup. Live and completed-retained property subscription
handles are capped at eight per session and 64 per server process, bounding
handler, worker, retention-task, and cancellation-source growth. A completed
handle releases its slot on `unsubscribe` or after its idle grace expires.

DataContext observation follows normal WPF binding notification behavior. It
tracks dependency properties and `INotifyPropertyChanged` paths, but a plain CLR
property that emits no notification cannot be observed without polling and is
therefore best effort.

Locator resolution is also bounded: `maxNodes` defaults to 5,000 and is capped
at 20,000. Reusing a resolved `elementId` avoids that scan entirely.

### Application-Owned Native Dialogs

An attached WPF process can own native top-level windows such as the Windows
open-file dialog. `list_windows` includes these same-process HWNDs and reports
their `OwnerHandle`, nullable `IsModal`, and UI Automation `FrameworkId` when
that evidence is available. A live HWND remains stable for the lifetime of its
window; callers must not persist it after the window closes.

For a known native window, `backend=Auto` routes backend-neutral inspection,
locator-based screenshot targeting, and interaction targeting through UIA
instead of trying the WPF agent first. Prefer stable AutomationIds and control
types for common-dialog controls rather than localized captions. Session
active-window selection follows a newly opened owned/modal window and returns
to the most recent live owner or main window after it closes.

Explicit handles fail with scoped errors: `window_closed` for a window that was
observed in the session and has since closed, `window_outside_session` for an
HWND owned by another process, and `window_uia_unavailable` when a live native
window cannot be represented through UIA. Support is limited to
application-owned, same-process HWNDs with usable UIA peers. OS-brokered or
secure-desktop dialogs, cross-process windows, and owner-drawn native controls
without useful UIA remain outside this scope.

### Desktop Interaction Policy

Attaching to a process, inspecting it, and taking screenshots do not activate
the target window or send physical input. Interaction tools are semantic-first:
they prefer WPF or UI Automation patterns and direct value operations that can
run in the background, then use foreground activation or mouse/keyboard input
only when a supported fallback requires it.

Set an `interactionPolicy` on `launch_app` or `attach_to_app` to establish the
session policy. Omitting it preserves compatibility: both foreground activation
and physical input are allowed. Interaction and window operations accept a
nullable per-operation override; each omitted field inherits its session value.
For a background-only session, use:

```json
{
  "interactionPolicy": {
    "allowForegroundActivation": false,
    "allowPhysicalInput": false
  }
}
```

Under this strict policy, semantic operations can still succeed without taking
focus. An operation whose only remaining path requires a forbidden effect fails
before that fallback with `interaction_policy_blocked`. A one-call override can
allow only the needed effect. Use `set_active_window` when foreground activation
is intentional; `mouse_click`, `drag`, `send_keys`, or `click_element` with
`clickMode: "mouseAlways"` are explicit physical-input choices.

Interaction responses report what the tool actually did in `Effects`:

| Field | Meaning |
|---|---|
| `Semantic` | Used a WPF/UIA pattern or direct semantic operation. |
| `ForegroundActivated` | Brought the target window to the foreground. |
| `WindowRestored` | Restored a window as part of the operation. |
| `MouseInput` | Sent physical mouse input. |
| `KeyboardInput` | Sent physical keyboard input. |
| `CursorMoved` | Moved the system pointer. |
| `KeyboardFocusChanged` | Changed the element holding keyboard focus. Present only when a change was observed. |

Where available, `MethodUsed` provides the more specific mechanism. These
fields describe MCP automation, not arbitrary side effects of the invoked
application code. For example, a semantic command handler may independently
open or activate one of its own windows.

### Text and Keyboard Input

`type_text` makes text placement explicit with `mode=Replace`, `Append`, or
`AtSelection`. Omitting `mode` preserves the previous behavior: a specified
target uses `Replace`, while a targetless call types at the current selection of
the focused element. Replace and append prefer background-safe WPF or UIA value
operations. `AtSelection` uses WPF selection semantics when available; other
targets use keyboard input only when the interaction policy permits it.

`send_keys` sends an ordered sequence of physical keys and modifier chords. It
accepts common navigation, editing, confirmation, and cancellation keys,
letters, digits, and `F1` through `F24`; modifiers are `Control`, `Shift`, `Alt`,
and `Windows`. A sequence contains 1-100 strokes:

```json
{
  "sessionId": "session-id",
  "locator": { "automationId": "SearchBox" },
  "sequence": [
    { "key": "A", "modifiers": ["Control"] },
    { "key": "Delete" },
    { "key": "Enter" }
  ]
}
```

Unlike semantic text assignment, `send_keys` always requires physical input and
foreground keyboard focus. It focuses a specified WPF or UIA target without
moving the mouse. Both input responses report the method used plus
`ForegroundFocusRequired` and `PhysicalInputRequired`; `type_text` also reports
the resolved `ModeUsed`. Set `interactionPolicy.allowPhysicalInput=false` to
guarantee that a physical fallback fails with `interaction_policy_blocked`
before focus or input is changed.

### Session Lifecycle

Use `detach_session` for normal inspection cleanup. It removes the session,
subscriptions, traces, element handles, and client-side agent connection while
leaving the target process running. Use `close_app` only when a graceful
application close is intended, and `terminate_app` only when forceful process
termination is intended. `close_session` retains its historical close-with-
optional-force behavior for compatibility.

Shutdown responses report `SessionRemoved`, caller intent (`CloseRequested` and
`ForceTerminationRequested`), actual dispatch/attempt
(`CloseRequestDispatched` and `ForceTerminationAttempted`), and observed process
state (`ProcessAlreadyExited` and `ProcessExited`) separately. A removed session
therefore does not imply that the process exited or that a close request was
dispatched. For `close_app` and `terminate_app`, `Closed` mirrors
`ProcessExited`. The compatibility-only `close_session` preserves its historical
`Closed = true` result once the session is removed; new callers should use
`SessionRemoved` and `ProcessExited` instead. Detach reports process-probe confidence through
`ProcessWasRunningObserved` and `ProcessStillRunningObserved`; a false running
value is not authoritative when its matching observation field is false.

### Deterministic Viewports

The diagnostics-only `set_window_viewport` tool sets the client area, rather
than the outer window rectangle, to a repeatable size. Supply `clientWidth`,
`clientHeight`, and a `unit` of `physicalPixels` or `wpfDips`:

```json
{
  "sessionId": "session-id",
  "clientWidth": 1280,
  "clientHeight": 720,
  "unit": "physicalPixels"
}
```

The operation defaults to `ensureForeground=false` and
`clampToWorkArea=false`, so exact background sizing is preferred over silently
changing the requested viewport. Set `clampToWorkArea=true` when keeping the
entire outer window within the current monitor work area is more important.

The response records the normalized request, actual client and outer bounds in
physical pixels, client size in both physical pixels and WPF DIPs, non-client
frame insets, separate window-effective and monitor-effective DPI values, DPI
awareness, monitor bounds and work area, and window state. WPF-DIP requests are
first converted at the target window's render DPI and then through its DPI
virtualization scale to monitor pixels, so unaware and system-aware windows are
repeatable on mixed-DPI desktops. `Adjustment.ExactMatch`, size deltas, resize
attempts, and `Constraints` make DPI rounding, work-area clamping, minimum
sizes, and other application constraints explicit.

Set `includeViewport=true` on `take_screenshot` to include the same
`ViewportConditions` alongside the image metadata. This ties visual evidence
to the client size, DPI, monitor, and state under which it was captured. The
viewport is sampled immediately before and after capture; unstable captures
are retried and fail with `screenshot_viewport_unstable` rather than returning
mislabeled evidence.

### Screenshot Correlation

The `diagnostics` profile can correlate a point or rectangular region in a
`take_screenshot` image with bounded WPF and UIA element candidates. The
workflow is opt-in through the nested `correlation` argument; it does not add a
separate top-level tool or enlarge the compact `core` schema:

```json
{
  "sessionId": "session-id",
  "captureMode": "screen",
  "area": "client",
  "correlation": {
    "x": 320,
    "y": 180,
    "width": 48,
    "height": 24,
    "backend": "Both",
    "includeAncestors": true,
    "maxAncestors": 4,
    "maxCandidates": 8,
    "maxNodes": 10000,
    "annotate": true
  }
}
```

`x`, `y`, `width`, and `height` are physical pixels local to the returned
bitmap, with `(0, 0)` at its top-left corner. `x` and `y` must be non-negative,
dimensions must be positive, and the complete region must fit inside the
captured image. `width` and `height` default to `1`, so an `x`/`y` pair is a
point query. The result includes both that image-space region and its mapped
physical screen region. For a point query it also reports the single canonical
`ScreenPointPhysicalPixels` used by both backends, even when one image pixel
maps to multiple screen pixels.

`backend=Both` scans WPF and UIA and keeps their candidates separate. `Auto`
uses a connected agent when it advertises the current WPF capability and uses
UIA otherwise; it does not inject as a side effect. Explicit `Wpf` and `Both`
requests require a connected current agent, so call `inject_agent` first. If an
already-loaded agent lacks the capability, restart the target application,
start a new MCP session, and attach again.

Candidates report backend, identity, path, bounds, match kind, and intersection
bounds. Set `includeAncestors=true` for a bounded nearest-first ancestor chain.
Overlapping matches are returned as explicit candidates instead of silently
collapsing to one: per-backend `DirectHitIndex` and `HasOverlaps`, plus the
aggregate `Ambiguous` flag, expose the distinction.

Correlation forces stable viewport and capture-context collection even when
`includeViewport` is false. `CaptureContext` records the actual window, client
and outer bounds, effective DPI scales, requested and used capture modes,
capture area, clipping, and sampled obscuration. Obscuration sampling applies
to screen capture; other modes report it as not applicable rather than
guessing.

Annotations default to enabled. Candidate labels and colors are returned with
their image-local bounds and are drawn into the same artifact, so a small set
of selected elements can be shared without a tree dump. Set `annotate=false`
to retain correlation data without modifying the pixels.

Defaults are 8 candidates and 10,000 scanned nodes per backend, no ancestors,
and 4 ancestors per candidate when ancestor context is enabled. The server
clamps `maxCandidates` to 1-25, `maxNodes` to 1-200,000, and `maxAncestors` to
0-20. Returned, discovered, and scanned counts remain separate.
`ScanComplete`, `Truncated`, and `TruncatedReason` state whether the counts are
exact or a lower bound and which cap stopped discovery.

### Direct Search and Disambiguation

`find_elements` searches the selected WPF or UIA tree directly; callers do not
need to retrieve a broad visual-tree response first. The default `minimal`
result keeps only identity and path fields. Set `returnFields=standard` to add
class, bounds, and best-effort `IsVisible` / `IsOffscreen` context. Results are
kept in deterministic tree order for an unchanged UI and include reusable
public `elementId` handles unless `includeElementIds=false` is requested in the
diagnostics profile.

Search counts have distinct meanings: `ReturnedMatches` is the bounded result
list, `DiscoveredMatches` is the number observed before search stopped, and
`ScannedNodes` is the traversal work performed. `DiscoveredMatches` is exact
when `Truncated=false`; otherwise it is a lower bound. A result is not marked
as `maxResults`-truncated merely because it exactly fills the requested limit.

When a strict non-XPath `resolve_element` locator matches more than one element,
the tool returns an `ambiguous_element` tool error with structured,
deterministically ordered candidate data for both WPF and UIA. Up to five
candidates include their `index`, standard context, XPath, and reusable
`elementId`; retry with an index or use a candidate handle directly. A bounded
candidate summary remains in the text error for clients that do not consume
structured error content. An ambiguous XPath segment instead returns a
path-specific text error asking for a one-based `[n]` index on that segment.

### Response Budgets

Responses are concise and bounded by default. Increase a tool's explicit limit
or select its expanded preset only when the extra evidence is needed. The
complete controls live in the `diagnostics` profile when exposing them in
`core` would make the common schema substantially larger.

| Tool | Default response budget | Expanded evidence | Limit metadata |
|---|---|---|---|
| `take_screenshot` correlation (`diagnostics`) | Per backend: 8 candidates while scanning at most 10,000 nodes; no ancestor chains | Set `maxCandidates` (1-25), `maxNodes` (1-200,000), `includeAncestors`, and `maxAncestors` (0-20); use `backend=Both` for combined WPF and UIA evidence | Per backend: `ReturnedCandidates`, `DiscoveredCandidates`, `ScannedNodes`, `ScanComplete`, `Truncated`, `TruncatedReason`, `DirectHitIndex`, `HasOverlaps`; aggregate `Ambiguous` |
| `get_visual_tree` | Depth 4, at most 500 nodes, minimal fields | Set `depth`, `maxNodes`, `preset`, or `fields` in `diagnostics` | `ReturnedNodes`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_uia_tree` | Depth 4, at most 200 nodes | Increase `depth` or `maxNodes` | `ReturnedNodes`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `find_elements` | At most 25 matches while scanning at most 5,000 nodes; minimal fields | Set `maxResults` or `returnFields`; `diagnostics` also exposes backend, root, scan limit, and ID controls | `ReturnedMatches`, `DiscoveredMatches`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_element_properties` | Summary preset, at most 25 selected UIA properties; values cap strings at 2,000 characters, collections at 50 items, and nesting at depth 2, with one shared 20,000-character serialized-value budget. XPaths over 2,000 characters are omitted rather than returned incomplete. | Select the `full` preset and an explicit `maxProperties` in `diagnostics` | `ReturnedProperties`, `SelectedProperties`, `ScannedProperties`, `Truncated`, `TruncatedReason`, `TruncatedReasons` |
| `get_binding_errors` | Depth 6, at most 200 errors while scanning at most 2,000 nodes | Set the error, depth, and scan limits in `diagnostics` | `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_data_context` | Summary mode, depth 2, at most 50 properties per object and 2,000 characters per string | Use the additional mode and size controls in `diagnostics` | `Truncated` and bounded warnings |
| `get_computed_properties` | Legacy compact fields; structured provenance is off | In `diagnostics`, set `includeProvenance=true`; at most 100 properties and 20 provenance scan units/candidates by default, with a hard nested limit of 50 | Outer `TruncatedReason`; nested returned/discovered counts, scan counts, `ScanComplete`, `Truncated`, and stable evidence reasons |
| `get_layout_context` | 6 nearest ancestors, 8 relevant siblings, 32 Grid definitions, and up to 128 unavailable-evidence records | Set `maxAncestors`, `maxSiblings`, or `maxGridDefinitions` in `diagnostics`; unavailable evidence keeps its fixed 128-record cap | Discovered/returned counts for ancestors, siblings, Grid contexts, definitions, and unavailable evidence; ordered `TruncatedReasons` including `maxUnavailableEvidence` |
| `trace_stop` | Writes the complete trace artifact but returns no inline events | Set `includeEvents=true`; at most 100 events are returned by default and `maxEvents` is capped at 1,000 | `EventCount`, `ReturnedEventCount`, `Truncated`, `TruncatedReason` |

When `Truncated` is true, `TruncatedReason` names the budget that was reached.
Counts describe the work performed and the evidence returned, so callers can
decide whether to narrow the request or deliberately raise a limit.

### Tool Evolution Policy

New diagnostic depth should extend an existing concept before it creates a new
top-level tool. Specialist controls belong in the `diagnostics` profile. A new
tool is justified only when it has an independent lifecycle or cannot form a
coherent part of an existing operation. Adding a tool to `core` also requires a
common-workflow rationale plus inventory and compact-schema contract coverage.

## Typical Workflow

1. Call `launch_app` or `attach_to_app`, optionally set the session
   `interactionPolicy`, and retain the returned `sessionId`.
2. Use `list_windows` to choose among top-level windows. Call
   `set_active_window` only when foreground activation is intended. Window
   titles use native captions when available, with the UI Automation name as a
   fallback and accepted selection alias. For application-owned native dialogs,
   use `OwnerHandle`, `IsModal`, and `FrameworkId` to preserve window context;
   treat the HWND as valid only while that window is live.
3. For responsive-layout evidence, use `set_window_viewport` to establish the
   exact client size and request `includeViewport` with screenshots. In the
   diagnostics profile, add `correlation` to map a small image region to
   bounded WPF/UIA candidates and an annotated artifact.
4. Inspect with `get_visual_tree` or `find_elements`, use
   `get_layout_context` for WPF spacing/allocation evidence, then retain an
   `elementId` from `resolve_element` for follow-up calls.
5. Interact, wait for the expected state, and inspect again to verify the
   result.
6. Call `detach_session` when inspection is finished. Use `close_app` or
   `terminate_app` only when stopping the target application is intended.

In the core profile, inspection tools that support both backends prefer the WPF
agent and fall back when a UIA equivalent exists. Tree and search responses
include fallback warnings. WPF-only tools, such as binding, DataContext, and
layout context inspection, require successful injection.

## Limitations

- The server and packaged tool are Windows-only.
- ARM64 target processes are not supported for injection; UIA-only automation
  may still be available.
- Injection can be blocked by process elevation, user boundaries, or endpoint
  security software.
- Semantic actions depend on useful WPF or UIA automation peers and patterns. A
  custom control without an actionable peer may remain inspectable but require
  a physical fallback, if the session policy permits one.
- Native-dialog support covers only same-process HWNDs owned by the attached WPF
  application and exposed through UIA; it does not cross process or secure
  desktop boundaries.
- Multi-monitor coordinates use the Windows virtual screen and may be negative.

## Troubleshooting

### Injector launcher failures

Each Snoop injector launch runs with a disposable profile and temp workspace.
`USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `TEMP`, and `TMP` are overridden for
the launcher subtree, including a precreated writable `APPDATA\Snoop`
directory. This contains Snoop's upstream file logging without modifying the
pinned upstream launcher submodule. The logging calls are not universally best
effort, so an unexpected failure inside the launcher can still produce a
nonzero exit.

The launcher inherits Windows error-mode flags that suppress system fault
dialogs; the MCP server's original error mode is restored immediately after
the child starts. A normal nonzero exit is reported with captured stdout and
stderr plus the launcher path, PID, duration, and signed decimal/hex exit code.
Exit `0xE0434352` is identified as an unhandled CLR exception. Cancellation or
timeout requests termination of the entire launcher process tree, boundedly
waits for the launcher, and reports the same context with the termination
outcome and bounded output. Regression coverage independently verifies that
both a fixture launcher and its recorded child exit after cleanup. A gated
GitHub Actions test also exercises a real unhandled fixture exit; local runs
skip that test before starting the process, and the fixture itself requires a
second dedicated opt-in token before its crash mode can run.

The default injector timeout is 15 seconds. Set
`WPF_TOOLS_MCP_INJECTOR_TIMEOUT_MS` to a positive millisecond value to change
it. Valid values are clamped to 1,000 through 120,000 milliseconds; invalid or
non-positive values use the default.

## Development

Source builds require:

- .NET 8 SDK and PowerShell 7.
- Visual Studio 2022 or Build Tools with MSBuild, **Desktop development with
  C++**, and a Windows 10 or 11 SDK. These are required for Snoop's x86/x64
  generic injector.

Initialize the Snoop submodule and build the injection payload before building
or testing deep WPF inspection:

```powershell
git submodule update --init --recursive
pwsh scripts/build-snoop.ps1 -Configuration Debug
dotnet build src/WpfToolsMcp.McpServer/WpfToolsMcp.McpServer.csproj -c Debug
```

Prefer focused snapshot tests while developing:

```powershell
dotnet test tests/WpfToolsMcp.SnapshotTests/WpfToolsMcp.SnapshotTests.csproj -c Debug --filter "FullyQualifiedName~ToolProfileTests"
```

Run the complete integration/snapshot project only when the scope justifies it;
the tests launch real WPF processes and exercise UI Automation and injection:

```powershell
dotnet test tests/WpfToolsMcp.SnapshotTests/WpfToolsMcp.SnapshotTests.csproj -c Debug
```

For a black-box smoke run against another WPF executable, build the server and
pass the target explicitly:

```powershell
dotnet run --project tools/WpfToolsMcp.McpSmokeRunner -- --exe C:\path\to\App.exe
```

Smoke artifacts are written under `artifacts/smoke/<timestamp>` unless `--out`
is supplied.

## Repository Layout

- `src/WpfToolsMcp.McpServer`: stdio MCP host, tool profiles, tool definitions,
  and subscriptions.
- `src/WpfToolsMcp.Automation`: per-session controllers, FlaUI/UIA automation,
  screenshots, handles, tracing, and injected-agent orchestration.
- `src/WpfToolsMcp.Agent` and `src/WpfToolsMcp.AgentProtocol`: injected WPF
  inspector and its bounded length-prefixed JSON pipe protocol.
- `src/WpfToolsMcp.Contracts`: shared request, response, and enum contracts.
- `src/WpfToolsMcp.Tool`: global-tool launcher and NuGet packaging project.
- `src/WpfToolsMcp.TestApp*`: focused WPF integration fixtures.
- `tests/WpfToolsMcp.SnapshotTests`: NUnit and Verify integration snapshots.
- `tools/WpfToolsMcp.McpSmokeRunner`: optional black-box smoke runner.
- `references/snoopwpf`: pinned Snoop git submodule.

The PRD and phase review files under `docs/` are historical design and review
records. The README and MCP-advertised tool schemas are the current usage
reference.

## Licensing

- Original WPF Tools MCP source code is licensed under MIT (`LICENSE`).
- The packaged inspection/injection payload redistributes Snoop components
  under Ms-PL.
- See `THIRD_PARTY_NOTICES.md` and `references/snoopwpf/License.txt`.
