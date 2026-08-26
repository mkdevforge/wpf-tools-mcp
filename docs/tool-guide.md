# Tool guide

This guide explains how the tools fit together. The MCP server's advertised
schemas remain the source of truth for parameters, defaults, enum values, and
response shapes. The `core` profile deliberately gives several tools smaller
schemas than the `diagnostics` profile.

## Choose a profile

Start with `core`. It supports application lifecycle, windows, screenshots,
tree inspection, search, reusable element handles, common WPF diagnostics,
interaction, and waits. WPF inspection injects the agent when needed.

Use `diagnostics` for work that needs any of the following:

- explicit injection and backend troubleshooting
- display, window-bound, viewport, picker, or highlight controls
- style, template, or UIA coverage inspection
- property and binding-error subscriptions
- keyboard navigation, tool traces, or UI-thread performance samples
- the expanded controls on shared inspection and screenshot tools

Pass `--tool-profile diagnostics` to the server or set
`WPF_TOOLS_MCP_TOOL_PROFILE=diagnostics`.

## Start and end a session

`launch_app` starts an executable. `attach_to_app` accepts one process selector:
PID, process name, or a `processInstanceId` returned after an ambiguous process
match. A successful call returns the `sessionId` used by every later operation.

Process-name attachment does not guess when several live processes match. It
returns bounded candidates. Select one by `processInstanceId` so PID reuse
cannot silently choose a different process instance.

End the session according to intent:

| Tool | Effect on the target |
|---|---|
| `detach_session` | Releases MCP-owned state and leaves the process running |
| `close_app` | Requests a graceful close, waits for an observed outcome, then removes the session |
| `terminate_app` | Kills the process, waits for an observed outcome, then removes the session |
| `close_session` | Compatibility operation with optional forced termination |

Calls that target one session are serialized. Separate sessions own separate
controllers, active-window state, handles, and agent connections.

## Recover after a rebuild

When a target process exits, its PID generation, HWNDs, UIA identities, and WPF
handles are no longer valid. Pass the exited `sessionId` to `attach_to_app` to
create a successor session. You may omit the target selector to reuse the prior
process name, but ambiguous candidates still require an explicit choice.

Adopt the returned successor `sessionId`, call `list_windows`, and resolve new
element IDs. Calls through the retired session report the successor rather than
silently changing the old session's target.

## Pick a window

`list_windows` returns accessible visible top-level windows owned by the target
process. Native dialogs can appear beside WPF windows. Keep the returned handle
with the operation when the process has more than one window.

`set_active_window` changes the session's active window and may bring it to the
foreground. The diagnostics profile also provides:

- `get_active_window`
- `set_window_bounds` for the outer window rectangle
- `set_window_viewport` for an exact client-area request
- `set_window_state` for normal, minimized, or maximized state
- `list_displays` for monitor and virtual-screen bounds

Physical coordinates are Windows virtual-screen coordinates. A display to the
left or above the primary display can have negative values.

## Inspect without dumping everything

Use `get_visual_tree` when hierarchy matters. Set a small depth and node budget,
then inspect a subtree if the first response is too broad.

Use `find_elements` when you know some identity text or control type. Search is
bounded and returns stable ordering, match counts, scan counts, truncation, and
optional element IDs. Pass `root` to keep the scan inside a known subtree. It is
usually cheaper and easier to interpret than a deep tree dump.

`get_element_properties` reads bounded UIA properties and supported patterns.
Use the WPF-specific tools for data that UIA cannot expose:

| Question | Tool |
|---|---|
| Why is a binding not updating? | `get_binding_info`, `get_binding_errors` |
| Why is a command disabled? | `get_command_info` |
| What validation error is active? | `get_validation_errors` |
| What object is behind this view? | `get_data_context` |
| Where did this dependency-property value come from? | `get_computed_properties` |
| What are the same properties across several elements? | `get_computed_properties_batch` |
| Why is this element clipped, offset, or allocated this size? | `get_layout_context` |
| Which style or template is applied? | `get_style_chain`, `get_template_info` in `diagnostics` |
| Which WPF elements lack useful automation peers? | `uia_coverage_report` in `diagnostics` |

WPF getters, binding APIs, command `CanExecute`, UIA providers, and value
formatters can run application code. These tools are read-only in the sense that
they do not intentionally invoke a command or send input. They cannot promise
that target-defined getters are free of side effects.

For repeated controls, call `find_elements` once and pass the returned WPF
element IDs to `get_computed_properties_batch`. The batch reports its applied
element and property limits, scanned and returned counts, and truncation.
For a nested view-model value, pass dotted `propertyPaths` to `get_data_context`
instead of serializing the surrounding object graph. Both operations have hard
request limits and reject malformed or unbounded input.

## Use locators deliberately

Core locators expose `automationId`, `name`, `nameContains`, `className`, `type`,
`xpath`, `index`, and `strict`. The diagnostics schemas expose the fuller shared
locator where a tool supports it.

All supplied identity fields must match. Comparisons are not a fallback chain.
Prefer Automation ID when the application supplies one. Use a combined locator
when one field is not unique:

```json
{
  "automationId": "SaveButton",
  "type": "Button"
}
```

Strict resolution is the default. An ambiguous result includes bounded
candidates instead of choosing one. `index` is zero-based for match selection.
XPath child indexes are one-based, as in `/Window/Grid/Button[2]`.

`resolve_element` returns a reusable `elementId`. This avoids repeating a tree
scan and preserves the resolved UIA or WPF backend. An element ID is not a
durable application identifier. It can become stale when controls are recreated
and always becomes stale after process replacement.

## Understand backend results

The backend choices are `Auto`, `Uia`, and `Wpf` where a schema exposes them.
Automatic routing uses the window framework and available agent capability:

- WPF windows prefer the injected agent.
- Known Win32, WinForms, XAML, and Qt windows use UIA.
- Unknown frameworks probe WPF when possible, then use UIA.

Backend-neutral operations report a WPF-to-UIA fallback when one occurs.
WPF-only tools return an error if injection or the requested WPF scope is
unavailable.

`get_uia_locators` returns locator suggestions and mapping evidence. UIA and WPF
do not always have a one-to-one relationship. Templates, content presenters,
custom peers, and native child windows can produce a heuristic, ambiguous, or
missing mapping. Treat the returned status and evidence as part of the result.

This tool requires exactly one locator or element ID. For a UIA locator, omit
`backend` or set it to `Uia`. For a WPF locator, set `backend=Wpf` and use a
strict locator with an exact `automationId` or `xpath`. `backend=Auto` is not
accepted because the mapping direction must be explicit.

## Interact and verify

Prefer semantic operations because they do not depend on pointer position:

- `invoke` uses an invocation pattern.
- `set_value` writes a supported text or range value.
- `select_item` selects combo-box, list, tab, or tree items.
- `scroll_to_element` uses available scroll patterns or WPF support.
- `realize_item` asks a UIA item-container provider to create one virtualized
  item by provider index or exact name.

`click_element` can use semantic invocation or a physical click according to
the element and requested mode. `drag`, `mouse_click`, `type_text`, and
`send_keys` may require foreground activation and physical desktop input.

Set `allowForegroundActivation` or `allowPhysicalInput` to `false` on the
session when those effects are unacceptable. Individual action tools can accept
an override. The effective policy is returned with session data, and actions
report the effects that occurred.

After an action, verify a concrete postcondition. Useful choices are:

- `wait_for` for visibility, enabled state, text, focus, window state, bounds,
  or a structured combination of conditions
- `get_element_properties` or a focused WPF diagnostic
- `take_screenshot` for rendered output
- `find_elements` when an action creates or removes controls

Do not treat a successful input call as proof that application state changed.

## Work with virtualized controls

A virtualized item may exist in the provider's data set without a materialized
UIA or WPF element. Inspection does not realize missing items as a hidden side
effect.

Use `realize_item` explicitly with an ItemContainer provider. Realization can
load data, create containers, and move the viewport. The tool bounds provider
calls and polls for a usable postcondition. Resolve a fresh element ID after the
item appears.

## Capture rendered evidence

`take_screenshot` can capture a window or element. The full diagnostics schema
adds capture mode, clipping, annotation, viewport evidence, and correlation
controls. File output is preferred over Base64 for large evidence.

`take_screenshot_sequence` resolves the target once, pins the HWND and capture
rectangle, then writes ordered PNG files and a manifest. Use it for an animation
or a visual change triggered outside the call. It does not trigger the change
for you.

Screenshot correlation examines a bounded point or region and returns ranked
WPF and UIA candidates plus an annotated artifact. Pixel overlap alone does not
prove element identity. Inspect the score, evidence, ambiguity, and truncation
fields.

## Capture related diagnostics together

`capture_diagnostic_snapshot` accepts a closed list of diagnostic sections for
one pinned target. It is useful when binding, property, layout, tree, UIA, and
screenshot evidence must refer to one short observation window.

The WPF sections share one target dispatcher callback. UIA reads and native
screen capture cannot join that callback. The response identifies capture
groups, sources, timestamps, and skew. Each section can fail independently, so
inspect section status before using the aggregate result.

The operation does not accept scripts or interaction steps. Use separate action
and verification calls for side effects.

## Observe changes over time

The diagnostics profile offers two subscription sources:

- `subscribe_property_changes` observes allowlisted WPF dependency properties
  and dotted `DataContext` paths through target-side notifications.
- `subscribe_binding_errors` polls a bounded WPF tree scope for binding errors.

Use `poll_subscription` to consume ordered events. Queue and payload limits can
drop, coalesce, or truncate evidence. The response reports loss for the current
poll and for the subscription lifetime. A source failure emits a terminal event.
Call `unsubscribe` when finished.

`trace_start` records MCP tool activity for a session. `trace_stop` writes the
newest bounded events to a JSON artifact and can return a smaller inline subset.
`trace_keyboard_navigation` performs and records a focus traversal, so it is an
interaction rather than passive observation. `performance_start` and
`performance_stop` sample target UI-thread latency through the WPF agent.

## Read bounded results correctly

Large inspections distinguish three ideas:

- `returned` is serialized in this response.
- `discovered` was found before discovery stopped.
- `scanned` was examined while looking for results.

`scanComplete=false` means discovered totals can be lower bounds. A full
collection at exactly its limit does not by itself prove that data was omitted,
so use the explicit truncation fields. When a response has both
`truncatedReason` and `truncatedReasons`, the singular field is the first reason
kept for compatibility.

Tool errors use a stable `error.code`. The `detail`, cause, and target exception
text explain the observed failure but are not stable programmatic identifiers.
Malformed JSON-RPC, unknown tool names, and request cancellation can remain MCP
protocol errors rather than normal tool-error envelopes.
