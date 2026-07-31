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

### Local Trust Model

WPF Tools MCP is a local developer tool for inspecting an application running
under the same user account, not a multi-tenant service boundary. Normal WPF
property getters and UIA provider calls are part of inspection. Diagnostic
formatting may also call application-defined `ToString()` or `Exception.Message`
on observed values; those calls are best effort, their failures are caught, and
their returned text is bounded. The tool therefore does not promise that
inspection is a zero-execution view of target memory.

The remaining constraints protect correctness and the developer machine rather
than treating the target application as a hostile remote client. Session,
process, window, and element identities are validated; scans, queues, payloads,
artifacts, and wait times stay bounded; cancellation is honored; cleanup removes
only MCP-owned resources; and responses do not claim WPF or UIA capabilities
that the current target and agent have not demonstrated.

## Install

```powershell
dotnet tool install -g MkDevForge.WpfToolsMcp --version 0.1.0-preview.25
dotnet tool update -g MkDevForge.WpfToolsMcp --version 0.1.0-preview.25
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
| `core` (default) | 35 | Compact schemas, normal inspection and interaction, UIA locator export, and the most useful WPF diagnostics. WPF inspection is injected automatically when needed. |
| `diagnostics` | 59 | The full surface, including explicit injection, backend and screenshot controls, element picking/highlighting, subscriptions, traces, performance sampling, and window/display diagnostics. |

Every advertised tool includes an MCP `outputSchema` with exactly two `oneOf`
branches: the tool's typed success schema and the common tool-error schema.
Successful calls return their typed result as an object in `structuredContent`
and retain the same compact JSON in one text content block. Tool failures set
`isError=true`, put `{ "error": { "code", "detail", ... } }` in
`structuredContent`, and include concise `code: detail` compatibility text.
Callers should branch on `error.code`; malformed JSON-RPC, unknown tools, and
request cancellation remain protocol errors rather than tool-error envelopes.

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
- **Inspection:** `take_screenshot`, `take_screenshot_sequence`,
  `get_visual_tree`, `find_elements`, `resolve_element`,
  `get_element_properties`, `get_uia_locators`, `get_uia_tree`,
  `capture_diagnostic_snapshot`.
- **Interaction and synchronization:** `click_element`, `invoke`, `type_text`,
  `send_keys`, `set_value`, `select_item`, `realize_item`, `scroll_to_element`,
  `drag`, `wait_for`.
- **WPF diagnostics:** `get_binding_info`, `get_command_info`, `get_binding_errors`,
  `get_validation_errors`, `get_data_context`, `get_computed_properties`,
  `get_layout_context`.

The `diagnostics` profile additionally exposes:

- `inject_agent`, `agent_ping`, `get_active_window`, `get_path_to_element`, and
  `release_element`.
- `pick_element_at_point`, `highlight_element`, `mouse_click`, `list_displays`,
  `set_window_bounds`, `set_window_viewport`, and `set_window_state`.
- `get_style_chain`, `get_template_info`, and `uia_coverage_report`.
- `subscribe_binding_errors`, `subscribe_property_changes`,
  `poll_subscription`, and `unsubscribe`.
- `trace_keyboard_navigation`, `trace_start`, `trace_stop`, `performance_start`,
  and `performance_stop`.

Its expanded screenshot schemas also expose capture mode, area, and clipping
controls. `take_screenshot` additionally exposes the opt-in
screenshot-correlation workflow described below.

`get_validation_errors` is a bounded, read-only snapshot of the current
`Validation.Errors` attached state in one WPF visual-tree scope. It does not
subscribe, retain history, move focus, send input, or invoke
`IDataErrorInfo`/`INotifyDataErrorInfo` directly. The response reports the
observed validation rule, active binding metadata, bounded best-effort error
content and exception type/message, and whether a validation adorner was active,
not observed, or unavailable. Custom `ToString()` and `Exception.Message`
failures are caught and reported with a stable unavailable reason and failure
type. Source classification marks public WPF rule evidence as exact and the
internal conversion-rule name match as best effort.

`get_command_info` is a read-only snapshot of one WPF command source and its
nearby instance bindings. It accepts a locator or registered WPF element ID and
returns a reusable public `wpf_...` element reference. The tool reads the
ordinary `ICommandSource` members and calls `ICommand.CanExecute(parameter)`;
for a `RoutedCommand`, it mirrors WPF command-source targeting with
`CommandTarget ?? source as IInputElement`. Getters and `CanExecute` may run
application code, as they do during normal WPF commanding. The tool never calls
`Execute`.

The command source's `IsEnabled` state is reported separately from
`CanExecute`, since either can disable the UI independently. Routed-command
context starts at the explicit `CommandTarget` when present and otherwise at
the source (`Depth=0`), then walks the nearest public WPF parents. It reports
only the existing per-instance `CommandBindings` and `InputBindings`
collections; empty collections are not allocated just to inspect them. This is
useful route context, not a claim about the exact route a later input event will
take.
Key and mouse bindings expose their typed gestures, while custom gestures are
reported as unsupported without calling `InputGesture.Matches`. Getter,
`CanExecute`, and value-formatting failures remain structured in the response.

### Explained Bidirectional WPF/UIA Mapping

`get_uia_locators` accepts UIA-origin input when `backend` is omitted or set to
`Uia`. A locator that begins in the WPF tree must explicitly set `backend=Wpf`
and must be strict with an exact `automationId` or `xpath`. An `elementId` keeps
its registered backend; supplying a conflicting backend or window handle is
rejected.

WPF-origin calls scan only the selected window's in-process UIA control tree.
`maxNodes` defaults to 5,000 and accepts 1 through 50,000. It is one shared
node-visit budget for the control scan and bounded raw/control path work, so
reported `ScannedNodes` never exceeds it. The `UiaMapping` result reports
`Exact`, `Heuristic`, `Ambiguous`, or `Unmapped`, the stable method
`scoredWindowScan`, an integer ranking score, symbolic evidence, scan counts,
and at most ten ranked candidates. The score ranks evidence; it is not a
probability or confidence percentage.

`Exact` requires a complete scan with one UIA candidate having the exact
AutomationId, a compatible control type, and a runtime identity that can be
verified and registered. `Heuristic` requires a complete scan, a reusable
unique winner scoring at least 150 and leading the runner-up by at least 40.
Incomplete scans, ties, weak heuristic scores, insufficient heuristic leads,
and unverifiable runtime identity never select a UIA element. In those cases `Uia`,
`LocatorSuggestions`, `FlaUi`, `SelectedXPath`, and `SelectedElementId` are
omitted; bounded candidates explain the ambiguity when available.
Candidate `Reusable` means that a public `uia_...` handle was actually
registered and returned. Non-winners are diagnostic only: their evidence may
report provider runtime-identity availability, but they are not registered.

Successful WPF-origin results include reusable `wpf_...` and `uia_...`
element IDs, WPF and UIA paths, automation properties, and bounds. The source
WPF handle is validated against its pinned agent identity before scanning, so
a missing, evicted, or replaced source fails with `stale_element` rather than
healing to a locator occupant. Returned UIA handles retain their registration
metadata. Operations that interact with or mutate a target validate the
registered runtime identity and fail with `stale_element` if a different UIA
element now occupies that XPath. A read-only operation may explicitly observe
the current XPath occupant instead; its result then describes current state and
does not assert continuity with the originally registered element. Each tool
chooses that mode deliberately. When `get_uia_locators` observes a replacement,
it returns `SourceElementIdentityStatus=Changed` and registers a fresh UIA ID for
that occupant rather than attaching the stale source ID to new evidence. WPF
XPath is never evaluated against the different UIA tree.

UIA-origin calls return the normal UIA locator recommendations and also attempt
an explained mapping into the same window's WPF visual tree. The injected agent
projects each WPF element through its `AutomationPeer`, applies the same
deterministic scorer used by WPF-to-UIA mapping, and returns `WpfMapping` with
scan counts, status, integer score, symbolic evidence, and at most ten ranked
candidates. Only an `Exact` or sufficiently separated `Heuristic` winner is
registered as a reusable public `wpf_...` handle. The source UIA element also
gets a reusable `uia_...` handle when its runtime identity is available.

This reverse mapping is supplementary: valid UIA locator output still succeeds
when the WPF agent is missing, outdated, or fails. In that case
`WpfMapping.Available=false` includes a structured `Failure`. A window known to
be native or another non-WPF framework completes without injection as
`Available=true`, `Status=Unmapped`, and method `frameworkClassification`.
Read-only UIA-handle observation keeps the existing current-XPath-occupant
semantics. If the registered source was replaced, the result reports
`SourceElementIdentityStatus=Changed`, registers fresh UIA and WPF handles for
the observed replacement when possible, and never treats the stale source ID
as the replacement's identity.

### Backend Status and Failures

`list_sessions` is passive and observational. It reports current process,
attachment, and backend state and may verify or reconnect to an already-running
WPF agent, but it never injects the agent or initializes the WPF backend. An
explicit WPF operation initializes the backend when required; core inspection
routes do so only when their automatic-injection path is enabled.

`BackendCapabilities` remains the compatibility list of confirmed-ready
backends. Each `BackendCapabilityStates` entry reports `ready`, `unavailable`,
or `not_initialized` and can include a structured `FailureInfo` when a backend
is unavailable:

| Field | Meaning |
|---|---|
| `Code` | Stable lower-snake-case failure identifier. Branch on this rather than parsing `Detail`. |
| `Stage` | Stable phase: `process_discovery`, `attachment`, `architecture_detection`, `injection`, `pipe_connection`, `protocol`, or `target_shutdown`. |
| `Detail` | Stable, bounded human-readable summary. |
| `Cause` | Optional bounded diagnostic cause with an exception `Type`, best-effort `Message`, optional adapter `Details`, and a reason when reading `Message` failed. It is evidence, not a stable branching contract. |
| `Retryable` / `RetryAfterMs` | Optional retry guidance; an omitted value means no claim is made. |
| `RecoveryActions` | Optional machine-readable next steps such as `retry`, `use_uia`, `reattach`, `restart_target`, `restart_and_reattach`, `match_elevation`, `use_supported_architecture`, `repair_installation`, or `select_process_instance`. |

When `backend=Auto` uses UIA for `get_visual_tree`, `find_elements`, or
`resolve_element`, the response includes structured `Fallback` metadata:
`FromBackend`, `ToBackend`, `Attempted`, `Available`, `Used`, and an optional
`FailureInfo`. Tree and search responses also retain their text warning for
compatibility; callers should use `Fallback` for machine decisions. `Attempted`
is true only when the WPF path performed work for that request; native routing
and a cached retry gate report false even when the prior failure is included.
`Available` reports whether the destination UIA backend could serve the request,
and `Used` reports whether the returned payload came from that backend.

Successful one-shot inspections whose target is resolved relative to a window
report `windowHandleUsed`, including when the caller omitted `windowHandle` and
the session selected its active window. Composite snapshots keep the same value
under `target.windowHandle`. New `backendUsed` metadata is added only when a
tool can meaningfully select between inspection backends; compatibility fields
already exposed by fixed-backend tools, such as `get_validation_errors`, remain
unchanged. `fallback` remains optional and is emitted only by
routes that already model an alternate backend and have a fallback decision to
report. It is omitted for fixed-backend tools and ordinary no-fallback success.
For `take_screenshot`, backend and fallback metadata applies only to
element-targeted captures; an untargeted window capture omits both.
WPF diagnostics that depend on the audited response shape require the current
agent capability; a previously injected agent is rejected with restart and
reattach guidance rather than returning invented counts or missing identity.

Public actionable errors retain a stable failure code and bounded detail.
`FailureInfo` remains embedded in backend capability and fallback metadata and
preserves its bounded observed `Cause` when classification had an exception.
This means a UIA fallback and a later `list_sessions` call retain the useful WPF
failure evidence rather than only its category. A failed tool call instead uses
the common `error` envelope, whose optional retry fields mirror the same
semantics and whose `Cause` follows the same bounds.

Error context can include validated session, window, element, and backend
identity plus bounded process or element candidates. Candidate evidence may
include observed process names, window titles, start times, control types,
element names, AutomationIds, paths, and bounds. Bounded filesystem paths,
injector output, and target exception messages are also useful local evidence
and may appear in `Cause` or traces. Adapter `Details` can likewise carry bounded
remote diagnostic text. Payload limits still apply to every field.

### Coherent Diagnostic Snapshots

`capture_diagnostic_snapshot` reduces repeated orchestration without exposing a
general scripting surface. A call selects one to eight unique sections from
`VisualTree`, `UiaProperties`, `WpfProperties`, `Layout`, `Bindings`,
`DataContext`, `BindingErrors`, and `Screenshot`. Supply at most one of
`locator` or `elementId`; omitting both targets the pinned window. A
`WpfProperties` section requires an explicit `propertyNames` allowlist.

The target is resolved once and the session remains exclusively locked for the
whole capture. Requested WPF sections run in one `DispatcherPriority.Send`
callback on the target window's dispatcher. UIA evidence and the rendered
screenshot necessarily run outside that dispatcher turn. Every section
therefore reports its evidence source, schema, capture group, UTC timestamps,
offsets, and one of `Success`, `Unavailable`, `Truncated`, or `Failed`.
`Consistency.WpfSectionsSingleDispatcherTurn` records the WPF guarantee;
`CrossBackendAtomic` remains false for multi-phase evidence, and
`TimingSkewMs` reports the observed span rather than presenting different
frames as simultaneous.

The default shared budget is depth 3, 25 items, 200 scanned nodes, 1,000
characters per evidence value, and 40,000 serialized evidence characters.
Hard limits are depth 6, 100 items, 1,000 nodes, 2,000 characters per evidence
value, and 100,000 evidence characters. Structural limits apply wherever a
section exposes that dimension; for UIA property values they also bound nested
collection depth and item count. The value limit covers strings inside section
data and section messages. Shared target metadata, the target's reusable
element ID and XPath, and screenshot paths remain exact rather than being
rewritten. The payload limit covers section `Data`, not that common context
metadata.

A call also has a 10-second deadline, configurable from 100 ms through 30
seconds. Later evidence that cannot fit the remaining total payload budget is
omitted with `Truncated/maxPayloadChars`; other requested sections still retain
independent results. Screenshot evidence is a file-backed PNG with viewport
conditions, never inline Base64, and capture does not auto-scroll the target.

The snapshot is deliberately read-only. Short interaction sequences have a
different side-effect, retry, and policy lifecycle and are deferred to a
separate follow-up rather than accepted as arbitrary steps here. The decision
record is [docs/decisions/0001-coherent-diagnostic-snapshots.md](docs/decisions/0001-coherent-diagnostic-snapshots.md).

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
best effort. Custom resource keys use bounded best-effort formatting; a failed
formatter leaves type evidence and an explicit unavailable reason.

Resource candidates cover bounded element, ancestor, application, and
pre-existing merged-dictionary scopes. The agent reads only already-existing
owner backing fields and raw dictionary storage through guarded implementation
access. It never calls lazy WPF `Resources` getters, creates a missing resource
collection, copies a whole dictionary, or realizes a deferred resource.
Same-type application resource values are compared through caught best-effort
`Equals`; a throwing comparison marks the scan incomplete with explicit
`resource_value_comparison_failed` evidence instead of silently omitting a
candidate while claiming completeness.
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
and common geometry structs. Unknown application objects use bounded
best-effort `ToString()` and mark that evidence as `BestEffort`; a throwing
formatter falls back to type identity with explicit unavailable evidence.
`ComputedPropertyInfo.ValueEvidence` describes effective-value formatting;
style/template contributor candidates separately report `ValueEvidence` and
`ConditionsEvidence` so winner uncertainty does not hide formatter failures.
Truncated binding details, default values, or animation base values carry
`BestEffort/maxStringLength` evidence rather than `Exact` evidence.

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
`maxPayloadChars` bounds whole serialized subscription events, including their
envelope and payload, and the combined events returned by one poll. Completion remains
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

### Runtime Event Correlation

MCP-owned asynchronous events from binding-error subscriptions, property-change
subscriptions, and tool traces carry an additive version-1 `envelope`. Runtime
producers populate `version`, UTC `observedAtUtc`, `sourceKind`, `sessionId`,
`streamId`, and a monotonic per-stream `sequence`. Optional `windowHandle`,
`elementId`, and `xpath` fields add live target context when it is available.
An XPath longer than 2,000 characters is omitted in full and reported with
`xpathOmitted=true`; it is never returned as a misleading partial locator.

Sequence values order events only within one stream. A property subscription's
outer sequence describes server delivery, while the `ObserveStateEvent` sequence
inside its payload describes the target-side observation source. Neither sequence
establishes ordering across streams. Clients may use `observedAtUtc` to display a
best-effort merged chronology, but scheduler and process-clock boundaries mean it
is not a total or causal order.

The runtime envelope currently covers MCP-owned streams. WPF applications do
not expose one generic application-log source or ordering contract, so arbitrary
application logs require explicit source adapters that define capture, parsing,
ordering, and bounds. An unhandled-exception adapter is also a valid
source-specific extension when explicitly enabled and bounded; it must observe
only and must never mark an exception handled or otherwise change the
application's exception flow. This is a source-contract boundary, not a
confidentiality prohibition for the same-user local tool.

`poll_subscription` exposes canonical `droppedSinceLastPoll`,
`coalescedSinceLastPoll`, and `truncatedSinceLastPoll` fields alongside the legacy
`dropped`, `coalesced`, and `truncated` aliases; each pair has the same value.
Cumulative totals remain available. Natural or source-failure completion appends
exactly one `subscription_terminal` event with a typed `{ code, completedAtUtc }`
payload. Keep polling while `hasMore=true` so the terminal event can be drained.
The legacy `completed`, `completionReason`, and `completedAtUtc` fields remain
available throughout the completed subscription's retention window.

Tool traces retain at most 1,000 newest events. `trace_stop` and its JSON artifact
report observed, retained, and dropped event counts plus the retention limit and
whether retention truncated the trace. Inline `maxEvents` truncation is reported
separately. Tool names are capped at 128 characters, and summaries plus error
strings are capped at 1,000 characters. Trace errors retain the stable code and
detail when available, followed by a bounded best-effort underlying exception
type and message; a throwing message getter is reported as unavailable.

### Typed Wait Conditions

`wait_for` accepts either a compatibility `state` string or a structured
`condition`; the two forms are mutually exclusive. Existing states remain
available: `attached`, `visible`, `enabled`, `actionable`, `stable`,
`value_equals`, and `name_contains`. Structured conditions advertise the same
element checks as `Attached`, `Visible`, `Enabled`, `Actionable`,
`BoundsStable`, `NumericValueEquals`, and `NameContains`, and add WPF value and
window lifecycle checks. The MCP schema is discriminated by `condition.kind`:
each variant advertises only its legal fields and marks its required operands.

WPF `DependencyPropertyValue` and `DataContextValue` conditions observe a named
property or dotted DataContext path without evaluating caller-supplied code.
Expected values are explicitly typed as `String`, `Number`, `Boolean`, or
`Null`. Comparisons support `Equals`, `NotEquals`, ordinal string `Contains`,
and the four ordered numeric comparisons. The element-oriented `NameContains`
condition retains the legacy case-insensitive matching behavior. For example:

```json
{
  "sessionId": "session-id",
  "locator": { "automationId": "StatusText" },
  "condition": {
    "kind": "DataContextValue",
    "dataContextPath": "Operation.Status",
    "comparison": "Equals",
    "expected": { "kind": "String", "stringValue": "Complete" },
    "holdForMs": 100
  },
  "timeoutMs": 5000
}
```

These WPF value waits use the target-side change-notification machinery rather
than repeatedly walking or serializing the visual tree. The structured
`WindowOpen` and `WindowClosed` conditions sample visible, same-process
top-level HWNDs by handle, exact/partial title, owner, or framework. An exact
handle close wait captures the HWND, owning thread, native class, and owner as
a best-effort live identity; a different identity at the same numeric handle is
treated as replacement. Windows exposes no HWND generation token, so an
immediate same-thread reuse with the same class and owner is indistinguishable
and conservatively remains open. Title-only close waits mean that no visible
matching window remains.

`BoundsStable` means the element's exact `x`, `y`, `width`, and `height` remain
unchanged for `holdForMs` (or the compatibility `stableMs`). It does not claim
pixel, animation, or whole-render stability. A generic UIA collection-count
condition is intentionally excluded because virtualized controls expose only
realized children. Observe an application-owned scalar such as the
`Items.Count` DataContext path or a dedicated dependency property instead.

Structured MCP waits return timeouts rather than throwing by default. Results
include `backendUsed`, `elapsedMs`, `attempts`, `reasonCode`, the evaluation
`failureReason`, and `lastObservedValue`; target exit and WPF element unload
have distinct reason codes. Legacy waits retain their throwing default. The
effective timeout is bounded to 0-60 seconds, polling to 25-2,000 ms, and
`holdForMs` to 0-5,000 ms. Queues and event batches remain bounded as described
under live WPF state observation. Each window sample stops at 2,048 desktop
HWNDs or 128 same-process candidates, and probes UIA framework metadata for at
most 16 native-prefiltered candidates; exceeding a scan limit fails explicitly.

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
active-window selection follows a newly opened owned modal dialog and returns
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

### Virtualized Item Realization

`realize_item` is an explicit UIA mutation for one item exposed by an
`ItemContainerPattern` provider. Supply exactly one container locator or
registered container element ID, and exactly one zero-based provider-order
`index` or exact UIA `name`. A Name is passed to the provider unchanged,
including casing and leading or trailing whitespace. Indexes describe the
provider's order, not an application data-source index. Name uniqueness is only
what that provider exposes; the tool does not claim collection-wide duplicate
detection when the provider suppresses equal items.

The operation uses `ItemContainerPattern.FindItemByProperty` and
`VirtualizedItemPattern.Realize` only. It does not inject the WPF agent, activate
the window, send physical input, or run a scrolling search. An already realized
provider item returns `methodUsed=alreadyRealized`; an out-of-tree placeholder
without `VirtualizedItemPattern` is unsupported. For Name selection, the tool
probes for a second provider-observed match and reacquires the unique first item
before realization because provider calls can invalidate placeholders.

Calling the tool is sufficient mutation intent; there is no separate
acknowledgement flag. Realization may move the viewport and trigger data or
container loading. After `Realize()` is invoked, the response retains that fact
even if deferred container generation cannot be verified. It reports the
requested identity, method, provider-call and postcondition-poll counts,
elapsed time, stop and recovery reasons, and mutation flags. A reusable
`uia_...` handle is returned only after path reacquisition verifies process,
window, and runtime identity; missing, changed, or recycled identity leaves
`reusable=false` without erasing the mutation evidence.
When present, that handle uses the existing action-time identity checks for
inspection, selection, scroll-to, and element-screenshot workflows.

`maxProviderCalls` counts every `FindItemByProperty` call. The elapsed limit is
advisory and checked between provider calls and postcondition polls; it cannot
make one blocking provider call or `Realize()` obey a hard timeout.

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

The diagnostics-only `trace_keyboard_navigation` tool records an observed focus
path rather than predicting WPF's private navigation order. It can start from a
locator, a registered element ID, or the current focus, then take `Next` or
`Previous` steps. `Physical` mode sends Tab or Shift+Tab and honors the session
interaction policy. `WpfSemantic` mode calls WPF `MoveFocus` through the current
agent and never falls back to physical input.

Physical mode only passively reconnects for optional WPF metadata. An explicitly
supplied `wpf_...` start ID may connect or inject the agent because focusing that
WPF target requires it, matching the existing WPF-target behavior of `send_keys`.

Traces default to 20 steps and clamp at 100. Each step reports its method,
elapsed time, separate UIA and WPF focus identities when observed, and WPF
`TabIndex`, tab-stop, focus-scope, and navigation-group metadata when available.
Stable stop reasons distinguish the step limit, no change, a repeated focus
cycle, focus leaving the pinned window, window closure, unavailable focus, and
a WPF-to-interop boundary. `restoreFocus=true` attempts to restore the focus
captured before any optional start target was focused and reports the result; it
does not claim to undo event-handler or application state changes caused by the
traversal.

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

Each session is bound to one process instance, identified by PID and process
start time. `attach_to_app` never guesses between multiple live processes with
the same name. Instead it returns an `ambiguous_process` error with bounded,
deterministically ordered candidates containing their index, opaque
`processInstanceId`, PID, positive main-window handle, and bounded observed
process name, window title, start time, and executable path when available. If
the public process API cannot read the executable path, the candidate carries a
bounded `executablePathUnavailableReason` instead of silently hiding the field.
Retry with the opaque `processInstanceId` (preferred) or an explicit PID. Dotted
process names are preserved; only a terminal `.exe` suffix is removed. Command
line discovery is not currently included because `Process` has no equivalent
public in-process source contract; this is a platform scope choice, not local
path or argument confidentiality policy.

When `launch_app` uses its existing-instance fallback (for example, a
single-instance launcher exits without owning a window), it applies the same
rule and returns structured `ambiguous_process` candidates rather than choosing
among multiple existing instances.

After the target exits, call `attach_to_app` with its old `sessionId`. With no
other selector, the server searches for the same process name; an explicit
`pid`, `processName`, or `processInstanceId` may be supplied instead. Recovery
fully initializes a successor session and pins its main window before retiring
the predecessor. It returns a new `sessionId`, the selected active window, and
an identity-invalidation record. The prior interaction policy is preserved
unless the call overrides it. A still-running target cannot be replaced, and
an ambiguous or failed replacement leaves the old session untouched.

Window handles and element IDs are scoped to their originating session and
process instance. Calls through a replaced session fail with `stale_session`
and require window and element identities to be reacquired from the successful
replacement response. Passing an old identity to the successor reports
`stale_window` or `stale_element` rather than exposing its last-known target
details. A raw numeric HWND cannot encode a process generation, so a
value Windows has already reassigned to a live successor window represents that
new window. This cross-session invalidation is absolute: read-only
current-occupant observation applies only within the originating live session
and never revives an identity across process instances. Existing subscriptions
are stopped during the successful
replacement after the successor and predecessor tombstone are committed, and
before the recovery response is returned. A failed pre-commit replacement does
not remove them. Restarting only the MCP server while the target stays alive
remains a separate agent-reconnect path and does not require process replacement.

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

### Screenshot Sequences

`take_screenshot_sequence` captures an ordered PNG sequence for an
already-running animation, a change scheduled before capture, or a change
triggered manually or by another external actor. It captures the first frame
immediately, then waits at least `intervalMs` after each completed capture. The
manifest records actual UTC observation time, monotonic elapsed time, and
capture duration rather than claiming an exact frame rate.

The tool accepts 2-300 frames, and the requested inter-frame delays may total at
most 30 seconds. It resolves and optionally scrolls an element only for the
first frame, then pins the HWND, captured screen rectangle, and actual capture
mode. Later frames neither re-resolve the target nor silently switch modes. A
GUID-named child of `outputDirectory` contains `frame-0000.png` and subsequent
frames plus an atomically published `manifest.json`. Later capture failures
return `complete=false` while preserving completed frames; first-frame failures
remain normal tool errors. The compact response retains the pinned window,
bounds, capture mode, and clipping state beside the artifact paths.

Sequence capture is synchronous and holds the session's normal operation lock.
Another MCP action on the same session waits until the sequence finishes, so
this version does not coordinate a click or key action after capture starts.
The `diagnostics` profile additionally exposes `captureMode`, `area`, and `clip`;
base64, JPEG, annotations, correlation, embedded actions, video encoding, and
background recording are intentionally outside this workflow.

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
candidates include their `index`, reusable `elementId`, and bounded observed
type, name, AutomationId, XPath, and bounds when available; the error context
separately reports backend, window, returned/discovered counts, and truncation.
Retry with an index or use a candidate handle directly. Compatibility text
remains the fixed `ambiguous_element` code and stable detail. An
ambiguous XPath segment instead returns a
path-specific text error asking for a one-based `[n]` index on that segment.

### Response Budgets

Responses are concise and bounded by default. Increase a tool's explicit limit
or select its expanded preset only when the extra evidence is needed. The
complete controls live in the `diagnostics` profile when exposing them in
`core` would make the common schema substantially larger.

| Tool | Default response budget | Expanded evidence | Limit metadata |
|---|---|---|---|
| `capture_diagnostic_snapshot` | 1-8 explicitly selected sections; applicable section structures use depth 3, 25 items, and 200 nodes; section evidence strings use 1,000 characters; section `Data` shares 40,000 characters | Raise limits up to depth 6, 100 items, 1,000 nodes, 2,000 characters per evidence string, and 100,000 section-data characters | Exact shared target identity and timing; per-section source, schema, capture group, `Status`, `Code`, `PayloadChars`, and timing offsets |
| `take_screenshot` correlation (`diagnostics`) | Per backend: 8 candidates while scanning at most 10,000 nodes; no ancestor chains | Set `maxCandidates` (1-25), `maxNodes` (1-200,000), `includeAncestors`, and `maxAncestors` (0-20); use `backend=Both` for combined WPF and UIA evidence | Per backend: `ReturnedCandidates`, `DiscoveredCandidates`, `ScannedNodes`, `ScanComplete`, `Truncated`, `TruncatedReason`, `DirectHitIndex`, `HasOverlaps`; aggregate `Ambiguous` |
| `get_visual_tree` | Depth 4, at most 500 nodes, minimal fields | Set `depth`, `maxNodes`, `preset`, or `fields` in `diagnostics` | `ReturnedNodes`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_uia_tree` | Depth 4, at most 200 nodes | Increase `depth` or `maxNodes` | `ReturnedNodes`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `find_elements` | At most 25 matches while scanning at most 5,000 nodes; minimal fields | Set `maxResults` or `returnFields`; `diagnostics` also exposes backend, root, scan limit, and ID controls | `ReturnedMatches`, `DiscoveredMatches`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `realize_item` | At most 100 ItemContainer provider calls, a 5,000 ms advisory elapsed limit, and 50 ms postcondition polling | In `diagnostics`, set `maxProviderCalls` (1-1,000), `advisoryElapsedLimitMs` (1-60,000), or `pollIntervalMs` (10-1,000) | `FindItemByPropertyCalls`, `PostconditionPolls`, `ElapsedMs`, `StopReason`, optional `RecoveryReason`, mutation flags, `PostconditionVerified`, and `Reusable` |
| `get_element_properties` | Summary preset, at most 25 selected UIA properties; values cap strings at 2,000 characters, collections at 50 items, and nesting at depth 2, with one shared 20,000-character serialized-value budget. XPaths over 2,000 characters are omitted rather than returned incomplete. | Select the `full` preset and an explicit `maxProperties` in `diagnostics` | `ReturnedProperties`, `SelectedProperties`, `ScannedProperties`, `Truncated`, `TruncatedReason`, `TruncatedReasons` |
| `get_binding_info` | Return at most 2,000 bindings after inspecting the target's dependency properties | In `diagnostics`, set `includeUnbound`, `maxProperties`, or `valueFormat` | `returnedBindings`, `discoveredBindings`, `scannedProperties`, `scanComplete`, `truncated`, compatibility `truncatedReason`, and ordered `truncatedReasons` |
| `get_command_info` | Effective context start plus 8 nearest public WPF parent levels, at most 128 command/input binding entries total, and 500 characters per formatted value | In `diagnostics`, set `maxAncestors`, `maxBindings`, or `maxValueLength`; hard caps are 32, 512, and 2,000 respectively | Separate source, `ControlIsEnabled`, and `CanExecute` states; returned context and discovered/returned binding counts; ordered truncation reasons; structured getter, formatter, gesture, and evaluation failures |
| `get_binding_errors` | Depth 6, at most 200 errors while scanning at most 2,000 nodes | Set the error, depth, and scan limits in `diagnostics` | `returnedErrors`, `discoveredErrors`, `scannedNodes`, `scanComplete`, `truncated`, compatibility `truncatedReason`, and ordered `truncatedReasons` |
| `get_validation_errors` | Current state at depth 6; at most 100 errors while scanning 2,000 nodes; best-effort content is capped at 500 characters; hidden visual-tree elements are included | In `diagnostics`, set `visibleOnly`, `depth`, `maxErrors`, `maxNodes`, and `maxValueLength`. Hard caps are depth 100, 1,000 returned errors, 200,000 scanned nodes, and 2,000 characters per value | `ReturnedErrors`, `DiscoveredErrors`, `ScannedNodes`, `ScanComplete`, `TruncatedReasons`; response root XPath is capped at 2,000 characters, binding metadata is bounded with a per-error `Truncated` flag, formatting failures retain type/reason evidence, and warnings report returned/discovered counts with a fixed 20-entry cap |
| `uia_coverage_report` | At most 200 findings while scanning at most 5,000 WPF nodes | Set the finding, node, visibility, interaction, or root controls in `diagnostics` | `summary.returnedFindings`, `summary.discoveredFindings`, `summary.scannedNodes`, `summary.scanComplete`, `summary.discoveredIssueCounts`, and `summary.truncatedReasons`; `summary.findingsCount` and `summary.issueCounts` remain returned-subset compatibility fields |
| `get_data_context` | Summary mode, depth 2, at most 50 properties per object and 2,000 characters per string | Use the additional mode and size controls in `diagnostics` | Resolved `element`, `windowHandleUsed`, `truncated`, ordered `truncatedReasons`, and bounded warnings |
| `get_computed_properties` | Legacy compact values; structured provenance is off | In `diagnostics`, set `includeProvenance=true`; at most 100 properties and 20 provenance scan units/candidates by default, with a hard nested limit of 50 | `returnedProperties`, `discoveredProperties`, `scannedProperties`, `scanComplete`, ordered `truncatedReasons`; nested provenance retains its own bounded evidence metadata |
| `get_layout_context` | 6 nearest ancestors, 8 relevant siblings, 32 Grid definitions, and up to 128 unavailable-evidence records | Set `maxAncestors`, `maxSiblings`, or `maxGridDefinitions` in `diagnostics`; unavailable evidence keeps its fixed 128-record cap | Discovered/returned counts for ancestors, siblings, Grid contexts, definitions, and unavailable evidence; ordered `TruncatedReasons` including `maxUnavailableEvidence` |
| `get_style_chain` | At most 10 returned `BasedOn` styles per style entry | Set `maxBasedOnDepth` in `diagnostics` | Per entry: `returnedBasedOnStyles`, `discoveredBasedOnStyles`, `basedOnScanComplete`, `basedOnTruncated`, and the effective `maxBasedOnDepth` |
| `get_template_info` | Named elements are omitted unless requested; at most 50 are returned when enabled | Set `includeNamedElements` and `maxNamedElements` in `diagnostics` | `returnedNamedElements`, `discoveredNamedElements`, `namedElementsScanComplete`, `namedElementsTruncated`, and the effective `maxNamedElements` |
| `poll_subscription` | Whole events and each poll share the effective `maxPayloadChars` budget; queues retain at most the effective `maxQueue` | Set subscription bounds explicitly and keep polling while `HasMore=true` | Per-poll and cumulative dropped/coalesced/truncated counts; typed terminal event and retained completion state |
| `trace_stop` | Writes a bounded artifact retaining the newest 1,000 trace events and returns no inline events by default | Set `includeEvents=true`; at most 100 events are returned by default and `maxEvents` is capped at 1,000 | Observed, retained, and dropped event counts; retention limit/truncation; separate inline `Truncated` and `TruncatedReason` |

For the bounded inspections above, `returned*` is the exact serialized
collection count, `discovered*` is the amount observed before discovery stopped,
and `scanned*` is the work actually inspected. `scanComplete=false` makes
discovered totals and discovered issue counts lower bounds. Truncation is true
when evidence or requested scope was omitted; `truncatedReason` or ordered
`truncatedReasons`, as exposed by that response, names the applicable budget.
Where both fields exist, the singular value remains the first ordered reason
for compatibility. Exactly filling a limit is not itself proof of truncation.
If a target-side property API prevents complete discovery, the affected
binding or computed-property response adds `propertyInspectionUnavailable` and
reports `scanComplete=false` instead of certifying a partial scan.
The existing `find_elements` contract is unchanged: it retains `ReturnedMatches`,
`DiscoveredMatches`, `ScannedNodes`, `Truncated`, and singular
`TruncatedReason` with the semantics described above.

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
4. Inspect with `get_visual_tree` or `find_elements`, then retain an `elementId`
   from `resolve_element` for follow-up calls. Use
   `capture_diagnostic_snapshot` when several property, binding, layout,
   DataContext, tree, or screenshot sections must describe one bounded capture;
   use `get_layout_context` alone for focused WPF spacing/allocation evidence.
5. Interact, wait for the expected state, and inspect again to verify the
   result.
6. Call `detach_session` when inspection is finished. Use `close_app` or
   `terminate_app` only when stopping the target application is intended.

If the target is rebuilt or restarted between steps, call `attach_to_app` with
the exited session's `sessionId`. If candidate discovery is ambiguous, choose a
returned `processInstanceId` explicitly. Continue with the successor
`sessionId`, call `list_windows`, and resolve fresh element IDs before further
inspection or interaction.

In the core profile, inspection tools that support both backends prefer the WPF
agent and fall back when a UIA equivalent exists. When auto tree, search, or
resolve uses UIA as that fallback, the response includes structured fallback
metadata; tree and search also keep their compatibility warnings. WPF-only
tools, such as binding, DataContext, and layout context inspection, require
successful injection.

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
the child starts. The runner captures bounded stdout, stderr, and process
context so nonzero exits can be classified and cleaned up. Public MCP errors
retain the stable injection failure while their bounded `Cause`, and a running
tool trace, can include the launcher path, exit interpretation, and captured
output. Cancellation or timeout requests termination of the entire launcher
process tree and boundedly waits for it. Regression coverage independently
verifies that both a fixture launcher and its recorded child exit after cleanup.
A gated GitHub Actions test also exercises a real unhandled fixture exit; local
runs skip that test before starting the process, and the fixture itself requires
a second dedicated opt-in token before its crash mode can run.

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
