# WPF Tools MCP

WPF Tools MCP lets an MCP client inspect and operate a running WPF app. UI
Automation handles windows and controls from outside the process. Windows APIs
capture screenshots and send physical input. An injected agent reads WPF details
such as the visual tree, bindings, `DataContext`, dependency properties, layout,
commands, styles, and templates.

The target app needs no package or source change.
The server speaks MCP over stdio. UIA tools still work when injection is
unavailable.

## Install

You need Windows 10 or 11. Installing the global tool requires the .NET 8 SDK or
newer. The tool runs on the .NET 8 Desktop Runtime. The injected agent supports
x86 and x64 WPF apps running on .NET 8 or newer.

Published versions are previews, so install with `--prerelease`:

```powershell
dotnet tool install --global MkDevForge.WpfToolsMcp --prerelease
```

Update an existing installation with:

```powershell
dotnet tool update --global MkDevForge.WpfToolsMcp --prerelease
```

The command is `wpf-tools-mcp`. See the [changelog](CHANGELOG.md) for release
notes.

## Configure your MCP client

Start with the `core` profile:

```json
{
  "mcpServers": {
    "wpf-tools-mcp": {
      "command": "wpf-tools-mcp"
    }
  }
}
```

Use `diagnostics` for manual injection, backend selection, traces, subscriptions,
performance samples, element picking or highlighting, and window controls:

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

You can also set `WPF_TOOLS_MCP_TOOL_PROFILE=diagnostics`. The aliases
`diagnostic` and `full` select the same profile.

## Run a session

1. Call `launch_app` or `attach_to_app`. Keep the returned `sessionId`.
2. Call `list_windows`. Keep a window handle when the process owns several
   top-level windows.
3. Use `get_visual_tree` to browse or `find_elements` to search.
4. Use `resolve_element` when several later calls will target the same element.
5. Interact, then check the result with `wait_for`, another inspection, or a
   screenshot.
6. Finish with `detach_session`, `close_app`, or `terminate_app`.

`detach_session` leaves the app running. `close_app` asks it to close.
`terminate_app` kills the process. `close_session` remains for older clients.

## Tools

The `core` profile includes:

| Area | Tools |
|---|---|
| Sessions | `launch_app`, `attach_to_app`, `detach_session`, `close_app`, `terminate_app`, `close_session`, `list_sessions` |
| Windows and screenshots | `list_windows`, `set_active_window`, `take_screenshot`, `take_screenshot_sequence` |
| Inspection | `get_visual_tree`, `find_elements`, `resolve_element`, `get_element_properties`, `get_uia_locators`, `get_uia_tree`, `capture_diagnostic_snapshot` |
| WPF details | `get_binding_info`, `get_command_info`, `get_binding_errors`, `get_validation_errors`, `get_data_context`, `get_computed_properties`, `get_computed_properties_batch`, `get_layout_context` |
| Interaction | `click_element`, `invoke`, `type_text`, `send_keys`, `set_value`, `select_item`, `realize_item`, `scroll_to_element`, `drag`, `wait_for` |

The `diagnostics` profile adds:

| Area | Tools |
|---|---|
| Agent and handles | `inject_agent`, `agent_ping`, `get_path_to_element`, `release_element` |
| Desktop inspection | `list_displays`, `get_active_window`, `pick_element_at_point`, `highlight_element` |
| Window control | `set_window_bounds`, `set_window_viewport`, `set_window_state`, `mouse_click` |
| WPF details | `get_style_chain`, `get_template_info`, `uia_coverage_report` |
| Observation | `subscribe_property_changes`, `subscribe_binding_errors`, `poll_subscription`, `unsubscribe` |
| Tracing and performance | `trace_keyboard_navigation`, `trace_start`, `trace_stop`, `performance_start`, `performance_stop` |

Some shared tools accept more options in `diagnostics`. Use the schema returned
by the MCP server for exact fields, defaults, and response types.

## Find and inspect elements

A locator can match Automation ID, name, class, control type, XPath, or a
contains filter. Each supplied field must match.

Tools that resolve one element use strict locators by default. If several
elements match, they return `ambiguous_element` with a short candidate list.
Add a unique field, use `index`, or set `strict=false` if any matching element
will do. `find_elements` returns several matches by design.

`resolve_element` returns an `elementId` for later calls. Element IDs belong to
one session and one process instance, and they retain their WPF or UIA backend.
Resolve them again after the app restarts or rebuilds the relevant UI. The
`diagnostics` profile adds `release_element` for clients that keep many IDs.

Use a shallow `get_visual_tree` when hierarchy matters. Use `find_elements` when
you know what you are looking for. A `root` locator keeps the search inside one
subtree.

For repeated WPF elements, pass their IDs to
`get_computed_properties_batch` instead of making one call per element. Use
dotted `propertyPaths` with `get_data_context` when you need only a few nested
values.

See the [tool guide](docs/tool-guide.md) for full tool behavior.

## WPF and UI Automation

With `backend=Auto`, WPF windows use the injected agent when the operation needs
WPF data. Native windows and supported fallbacks use UI Automation. Responses
include the backend used and whether the server fell back.

WPF-only calls return an injection or backend error when the agent is
unavailable.

`get_uia_locators` explains matches between WPF and UIA elements. A mapping can
be exact, heuristic, ambiguous, or unavailable. A UIA locator can still work
when no WPF match exists.

## Interaction policy

Sessions allow foreground activation and physical input by default. Each flag
can block its corresponding effect. For a run that must not disturb the desktop,
set both to `false` and use semantic actions:

```json
{
  "interactionPolicy": {
    "allowForegroundActivation": false,
    "allowPhysicalInput": false
  }
}
```

Tools use WPF or UIA control patterns when the target supports them. `send_keys`
and `drag` use physical input; other actions may fall back to it. A blocked
fallback returns `interaction_policy_blocked` and names the setting it needs.

`type_text` supports `Replace`, `Append`, and `AtSelection`. `send_keys` accepts
named keys and modifier chords. Physical mouse and keyboard paths use the real
desktop. They can move the pointer, change focus, and bring a window forward.

## Windows and screenshots

`list_windows` returns visible top-level windows owned by the target process.
This includes accessible native dialogs opened by the app. Window handles expire
when that process exits. `ownerHandle`, `isModal`, and `frameworkId` describe a
dialog's relationship to the WPF window.

The `diagnostics` profile adds `set_window_viewport` for sizing the client area
in physical pixels or WPF device-independent pixels. Screenshot responses can
include the measured viewport and DPI.

`take_screenshot_sequence` writes a bounded set of PNG frames and a JSON
manifest. In the `diagnostics` profile, `take_screenshot` can return nearby WPF
and UIA candidates for a small region. Overlapping controls can produce several
candidates.

Coordinates use the Windows virtual screen. Monitors left of or above the
primary display have negative coordinates.

## Partial results

Tool failures set MCP `isError` and include a stable `code` plus readable
`detail`. Branch on `code`; application messages inside `detail` may change.

Tree and search operations stop at their configured limits. Check `truncated`,
`truncatedReasons`, and `scanComplete` before treating a result as exhaustive.
Where relevant, compare returned, discovered, and scanned counts. Screenshot
sequences cap frame count and delay; individual screenshots report clipping.
Property reads, queues, waits, strings, and files also have limits.
Subscriptions report dropped or coalesced events.

## Trust the app you inspect

Inspection is not passive. Reading a WPF property, asking UIA for a value,
evaluating `CanExecute`, or formatting an object can run code from the target
app. That code may throw, hang, or change state. Only attach to apps you trust.

Agent injection requires the server and target to run as the same Windows user.
The target cannot run at a higher integrity level than the server. Agent traffic
uses a named pipe restricted to that user.

The server checks session, process, window, and element identity before use, so
stale handles fail instead of reaching a different process.

## Known limits

- The server runs only on Windows.
- Agent injection does not support ARM64 target processes.
- Injection fails when the target runs as another Windows user or at a higher
  integrity level. Endpoint security software or an incompatible runtime can
  also block it.
- Native-dialog support stops at accessible windows owned by the target process.
  It cannot inspect another process or the secure desktop.
- Custom controls without a useful WPF or UIA peer may not support semantic
  interaction.
- Physical input requires an active interactive desktop and can disturb the
  user's mouse, keyboard focus, or foreground window.

## When injection fails

Switch to the `diagnostics` profile and call `inject_agent`, then `agent_ping`.
The failure reports a stage such as `injection`, `pipe_connection`, or
`protocol`.

The default timeout is 15 seconds. Set `WPF_TOOLS_MCP_INJECTOR_TIMEOUT_MS` to a
value from 1,000 through 120,000 milliseconds to change it.

Common blockers are missing packaged files, a target at higher integrity,
endpoint security software, an unsupported architecture, or an incompatible
runtime. Exit code `0xE0434352` means the launcher ended with an unhandled CLR
exception.

## Build and test

Source builds need the .NET 8 and .NET 10 SDKs, PowerShell 7, and Visual Studio
2022 or Build Tools with MSBuild, Desktop development with C++, and a Windows 10
or 11 SDK. The Snoop submodule pins SDK 10.0.100 and allows later .NET 10 feature
bands. The C++ toolchain builds its x86 and x64 injectors.

```powershell
git submodule update --init --recursive
pwsh scripts/build-snoop.ps1 -Configuration Debug
$env:DisableGitVersionTask = "true"
dotnet build src/WpfToolsMcp.McpServer/WpfToolsMcp.McpServer.csproj -c Debug
```

`DisableGitVersionTask` skips Snoop's GitVersion step, which can fail on a
detached submodule checkout or in a worktree. Keep it set when running tests.

Run the server from source:

```powershell
dotnet run --project src/WpfToolsMcp.McpServer -- --tool-profile diagnostics
```

Run a focused test while developing:

```powershell
dotnet test tests/WpfToolsMcp.SnapshotTests/WpfToolsMcp.SnapshotTests.csproj -c Debug --filter "FullyQualifiedName~ToolProfileTests"
```

The full snapshot project starts real WPF processes and exercises UIA and agent
injection:

```powershell
dotnet test tests/WpfToolsMcp.SnapshotTests/WpfToolsMcp.SnapshotTests.csproj -c Debug
```

The smoke runner takes a WPF executable and writes a JSON report, screenshots,
and tree captures under `artifacts/smoke/<timestamp>`:

```powershell
dotnet run --project tools/WpfToolsMcp.McpSmokeRunner -- --exe C:\path\to\App.exe
```

CI runs the full snapshot project on Windows. A `v*.*.*` tag runs a Release
build and the full snapshot project, then packs `MkDevForge.WpfToolsMcp` and
publishes it to NuGet.

Build a local release package with:

```powershell
pwsh scripts/build-snoop.ps1 -Configuration Release
$env:DisableGitVersionTask = "true"
dotnet pack src/WpfToolsMcp.Tool/WpfToolsMcp.Tool.csproj -c Release -o artifacts
```

## Repository layout

| Path | Purpose |
|---|---|
| `src/WpfToolsMcp.McpServer` | MCP host and tool definitions |
| `src/WpfToolsMcp.Automation` | sessions, UIA, screenshots, input, traces, and agent control |
| `src/WpfToolsMcp.Agent` | injected WPF inspector |
| `src/WpfToolsMcp.AgentProtocol` | named-pipe messages |
| `src/WpfToolsMcp.Contracts` | request, response, and enum types |
| `src/WpfToolsMcp.Tool` | global-tool launcher and NuGet package |
| `src/WpfToolsMcp.TestApp*` | integration test apps |
| `tests/WpfToolsMcp.SnapshotTests` | NUnit and Verify tests |
| `tools/WpfToolsMcp.McpSmokeRunner` | smoke runner for another WPF app |
| `references/snoopwpf` | pinned Snoop submodule |

Read [architecture](docs/architecture.md) for process boundaries, routing, and
cleanup. Design decisions live under [`docs/decisions`](docs/decisions/).

## License

Project code uses the MIT license. The packaged agent includes Snoop components
under the Microsoft Public License. See
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
