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
| `core` (default) | 28 | Compact schemas, normal inspection and interaction, UIA locator export, and the most useful WPF diagnostics. WPF inspection is injected automatically when needed. |
| `diagnostics` | 51 | The full surface, including explicit injection, backend and screenshot controls, element picking/highlighting, subscriptions, traces, performance sampling, and window/display diagnostics. |

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
  `set_value`, `select_item`, `scroll_to_element`, `drag`, `wait_for`.
- **WPF diagnostics:** `get_binding_info`, `get_binding_errors`,
  `get_data_context`, `get_computed_properties`.

The `diagnostics` profile additionally exposes:

- `inject_agent`, `agent_ping`, `get_active_window`, `get_path_to_element`, and
  `release_element`.
- `pick_element_at_point`, `highlight_element`, `mouse_click`, `list_displays`,
  `set_window_bounds`, `set_window_viewport`, and `set_window_state`.
- `get_style_chain`, `get_template_info`, and `uia_coverage_report`.
- `subscribe_binding_errors`, `subscribe_property_changes`,
  `poll_subscription`, and `unsubscribe`.
- `trace_start`, `trace_stop`, `performance_start`, and `performance_stop`.

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
as part of session cleanup. Active property subscriptions are capped at eight
per session and 64 per server process so total handler and worker growth is also
bounded.

DataContext observation follows normal WPF binding notification behavior. It
tracks dependency properties and `INotifyPropertyChanged` paths, but a plain CLR
property that emits no notification cannot be observed without polling and is
therefore best effort.

Locator resolution is also bounded: `maxNodes` defaults to 5,000 and is capped
at 20,000. Reusing a resolved `elementId` avoids that scan entirely.

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
is intentional; `mouse_click`, `drag`, or `click_element` with
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

Where available, `MethodUsed` provides the more specific mechanism. These
fields describe MCP automation, not arbitrary side effects of the invoked
application code. For example, a semantic command handler may independently
open or activate one of its own windows.

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

### Response Budgets

Responses are concise and bounded by default. Increase a tool's explicit limit
or select its expanded preset only when the extra evidence is needed. The
complete controls live in the `diagnostics` profile when exposing them in
`core` would make the common schema substantially larger.

| Tool | Default response budget | Expanded evidence | Limit metadata |
|---|---|---|---|
| `get_visual_tree` | Depth 4, at most 500 nodes, minimal fields | Set `depth`, `maxNodes`, `preset`, or `fields` in `diagnostics` | `ReturnedNodes`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_uia_tree` | Depth 4, at most 200 nodes | Increase `depth` or `maxNodes` | `ReturnedNodes`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `find_elements` | At most 25 matches while scanning at most 5,000 nodes; minimal fields | Set `maxResults`, `maxNodes`, or `returnFields` in `diagnostics` | `ReturnedMatches`, `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_element_properties` | Summary preset, at most 25 selected UIA properties; values cap strings at 2,000 characters, collections at 50 items, and nesting at depth 2, with one shared 20,000-character serialized-value budget. XPaths over 2,000 characters are omitted rather than returned incomplete. | Select the `full` preset and an explicit `maxProperties` in `diagnostics` | `ReturnedProperties`, `SelectedProperties`, `ScannedProperties`, `Truncated`, `TruncatedReason`, `TruncatedReasons` |
| `get_binding_errors` | Depth 6, at most 200 errors while scanning at most 2,000 nodes | Set the error, depth, and scan limits in `diagnostics` | `ScannedNodes`, `Truncated`, `TruncatedReason` |
| `get_data_context` | Summary mode, depth 2, at most 50 properties per object and 2,000 characters per string | Use the additional mode and size controls in `diagnostics` | `Truncated` and bounded warnings |
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
   `set_active_window` only when foreground activation is intended.
3. For responsive-layout evidence, use `set_window_viewport` to establish the
   exact client size and request `includeViewport` with screenshots.
4. Inspect with `get_visual_tree` or `find_elements`, then retain an
   `elementId` from `resolve_element` for follow-up calls.
5. Interact, wait for the expected state, and inspect again to verify the
   result.
6. Call `detach_session` when inspection is finished. Use `close_app` or
   `terminate_app` only when stopping the target application is intended.

In the core profile, inspection tools that support both backends prefer the WPF
agent and fall back when a UIA equivalent exists. Tree and search responses
include fallback warnings. WPF-only tools, such as binding and DataContext
inspection, require successful injection.

## Limitations

- The server and packaged tool are Windows-only.
- ARM64 target processes are not supported for injection; UIA-only automation
  may still be available.
- Injection can be blocked by process elevation, user boundaries, or endpoint
  security software.
- Semantic actions depend on useful WPF or UIA automation peers and patterns. A
  custom control without an actionable peer may remain inspectable but require
  a physical fallback, if the session policy permits one.
- Multi-monitor coordinates use the Windows virtual screen and may be negative.

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
