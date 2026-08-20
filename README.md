# WPF Tools MCP

WPF Tools MCP is a Windows-only MCP server for inspecting, testing, and
diagnosing running WPF applications. It combines two views of the target:

- FlaUI and UI Automation run outside the target process. They handle windows,
  screenshots, the automation tree, semantic control patterns, and physical
  mouse or keyboard input.
- An injected WPF agent reads the visual tree, bindings, validation state,
  `DataContext`, dependency properties, layout, commands, styles, and templates.

The server uses stdio for MCP. Target applications do not need a package or
source-code change.

## Requirements

- Windows 10 or Windows 11.
- The .NET 8 SDK or newer to install the global tool or build the repository.
- The .NET 8 Desktop Runtime to run an installed package.
- An x86 or x64 .NET 8 or newer WPF target for in-process inspection.

The MCP server and target must run as the same Windows user. The target cannot
run at a higher integrity level than the server. UIA-only features can still
work when agent injection is unavailable.

## Install

The package is currently published as a preview. `--prerelease` avoids pinning
the README to an old preview number.

```powershell
dotnet tool install --global MkDevForge.WpfToolsMcp --prerelease
```

Update an existing installation with:

```powershell
dotnet tool update --global MkDevForge.WpfToolsMcp --prerelease
```

The installed command is `wpf-tools-mcp`.

## Configure an MCP client

The default `core` profile covers normal inspection and interaction:

```json
{
  "mcpServers": {
    "wpf-tools-mcp": {
      "command": "wpf-tools-mcp"
    }
  }
}
```

Use the `diagnostics` profile when you need explicit backend controls,
subscriptions, traces, performance sampling, element picking, highlighting, or
window geometry tools:

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

`WPF_TOOLS_MCP_TOOL_PROFILE=diagnostics` is equivalent. The accepted profile
names are `core` and `diagnostics`; `diagnostic` and `full` are aliases for the
latter.

## Tool profiles

The `core` profile exposes these tools:

| Area | Tools |
|---|---|
| Sessions | `launch_app`, `attach_to_app`, `detach_session`, `close_app`, `terminate_app`, `close_session`, `list_sessions` |
| Windows and screenshots | `list_windows`, `set_active_window`, `take_screenshot`, `take_screenshot_sequence` |
| Inspection | `get_visual_tree`, `find_elements`, `resolve_element`, `get_element_properties`, `get_uia_locators`, `get_uia_tree`, `capture_diagnostic_snapshot` |
| WPF diagnostics | `get_binding_info`, `get_command_info`, `get_binding_errors`, `get_validation_errors`, `get_data_context`, `get_computed_properties`, `get_layout_context` |
| Interaction | `click_element`, `invoke`, `type_text`, `send_keys`, `set_value`, `select_item`, `realize_item`, `scroll_to_element`, `drag`, `wait_for` |

The `diagnostics` profile adds:

| Area | Tools |
|---|---|
| Agent and handles | `inject_agent`, `agent_ping`, `get_path_to_element`, `release_element` |
| Desktop inspection | `list_displays`, `get_active_window`, `pick_element_at_point`, `highlight_element` |
| Window control | `set_window_bounds`, `set_window_viewport`, `set_window_state`, `mouse_click` |
| WPF detail | `get_style_chain`, `get_template_info`, `uia_coverage_report` |
| Observation | `subscribe_property_changes`, `subscribe_binding_errors`, `poll_subscription`, `unsubscribe` |
| Tracing and performance | `trace_keyboard_navigation`, `trace_start`, `trace_stop`, `performance_start`, `performance_stop` |

The diagnostics profile also exposes more input controls on several shared
tools. Ask the MCP client for the advertised input and output schemas rather
than relying on copied parameter lists. Those schemas are the current contract.

## A normal inspection run

1. Call `launch_app` or `attach_to_app` and keep the returned `sessionId`.
2. Call `list_windows`. Use the returned handle when the process owns more than
   one top-level window.
3. Inspect with `get_visual_tree` or search with `find_elements`.
4. Call `resolve_element` when several later calls will use the same element.
5. Interact, then verify the result with `wait_for`, another inspection, or a
   screenshot.
6. Call `detach_session` to leave the application running. Use `close_app` for
   a graceful close or `terminate_app` to force the process to exit.

`close_session` remains for compatibility. New clients should choose one of the
three explicit lifecycle operations.

## Locators and element IDs

Locators can use Automation ID, name, class, control type, XPath, and related
contains filters. Fields in one locator are combined. They are not tried as an
ordered set of alternatives.

Locators are strict by default. More than one match returns an
`ambiguous_element` error with bounded candidate details. Use `index` or set
`strict=false` only when selecting a non-unique match is intentional.

`resolve_element` returns an `elementId` that can be passed to later tools.
Handles keep their original UIA or WPF backend and are scoped to the session and
process instance. Re-resolve them after the target restarts or the relevant UI
is recreated. The diagnostics profile exposes `release_element` for callers
that retain many handles.

## UIA and WPF routing

`backend=Auto` prefers WPF inspection for WPF windows and uses UIA for known
native or non-WPF windows. When a WPF route is unavailable and the operation has
a UIA equivalent, the response reports the fallback. WPF-only diagnostics fail
with a structured backend or injection error instead of returning UIA data
under a WPF label.

The core profile injects the WPF agent on demand. The diagnostics profile also
offers `inject_agent` and `agent_ping` for direct troubleshooting.

`get_uia_locators` can explain a bounded UIA-to-WPF or WPF-to-UIA mapping. A
mapping may be exact, heuristic, ambiguous, or unavailable. A valid UIA locator
is still useful when no WPF match exists.

## Interaction policy

Sessions allow foreground activation and physical input by default. Set either
policy field to `false` when an automation run must avoid that effect:

```json
{
  "interactionPolicy": {
    "allowForegroundActivation": false,
    "allowPhysicalInput": false
  }
}
```

The server prefers semantic WPF or UIA actions. A tool that requires a blocked
effect returns `interaction_policy_blocked` and names the required setting.
Interaction responses report observed effects such as foreground activation,
mouse input, keyboard input, and cursor movement.

`type_text` supports `Replace`, `Append`, and `AtSelection`. `send_keys` sends an
ordered sequence of named keys and modifier chords. Both physical paths require
the target to receive real desktop input, so do not run them against an
untrusted or unattended desktop session.

## Windows, viewports, and screenshots

`list_windows` includes visible same-process top-level windows, including
application-owned native dialogs when UIA can expose them. Use `ownerHandle`,
`isModal`, and `frameworkId` to keep dialog context. Window handles are valid
only for the current process instance.

In the diagnostics profile, `set_window_viewport` sets the client area in
physical pixels or WPF device-independent pixels. Screenshot calls can return
the measured viewport so a test can distinguish requested size from the actual
window and DPI state.

`take_screenshot_sequence` captures a bounded series of PNG frames and writes a
manifest with actual timing and frame metadata. Diagnostics-only screenshot
correlation can annotate a small image region with bounded WPF and UIA
candidates. It is evidence for investigation, not an assertion that pixels map
to one unique element.

Multi-monitor coordinates use the Windows virtual screen and may be negative.

## Diagnostics and observation

`capture_diagnostic_snapshot` reads selected evidence for one pinned window or
element. WPF sections share one dispatcher callback. UIA properties and native
screenshots run in separate phases, and the response reports timing skew rather
than claiming cross-backend atomicity.

`get_layout_context` reports bounds, transforms, clipping, nearby visual-tree
context, and Grid allocation. `get_computed_properties` can include dependency
property value-source evidence in the diagnostics profile. These tools report
unavailable evidence and truncation instead of filling gaps with inferred
values.

Subscriptions are bounded queues. Poll until `hasMore` is false, and inspect the
dropped, coalesced, truncated, and terminal-event fields before treating an
observation as complete. `trace_stop` and screenshot sequences write artifacts
to disk rather than returning an unbounded event or image stream.

For more detail, see the [tool guide](docs/tool-guide.md).

## Errors and response limits

Tool failures set MCP `isError` and return a structured error with a stable
`code` and human-readable `detail`. Branch on `error.code`. Diagnostic cause
text is supporting evidence and can contain target application messages.

Tree scans, property reads, queues, strings, artifacts, waits, and returned
collections have explicit limits. Responses include returned, discovered, or
scanned counts where the distinction matters. Check `truncated`,
`truncatedReasons`, and `scanComplete` before treating a result as exhaustive.

## Local trust model

This is a same-user developer tool, not a security boundary for hostile target
processes. WPF property getters and UIA providers run application code during
normal inspection. Formatting observed values may call application-defined
`ToString()` or `Exception.Message`. The server catches those failures and
bounds returned text, but it cannot make inspection side-effect free.

The server validates session, process, window, and element identity. It bounds
work and artifacts, honors cancellation, and removes only resources it owns.
The injected agent communicates over a current-user-only named pipe.

## Limitations

- The server and packaged tool run only on Windows.
- Agent injection supports x86 and x64 targets, not ARM64 targets.
- Elevation, user boundaries, endpoint security software, or an incompatible
  target runtime can block injection.
- Custom controls without useful WPF or UIA peers may be inspectable but not
  semantically actionable.
- Native-dialog support is limited to accessible same-process windows. It does
  not cross process or secure-desktop boundaries.
- Physical input depends on the active interactive desktop and can disturb the
  user's mouse, keyboard focus, or foreground window.

## Troubleshoot injection

The default injector timeout is 15 seconds. Override it with
`WPF_TOOLS_MCP_INJECTOR_TIMEOUT_MS`. Positive values are clamped to 1,000 through
120,000 milliseconds.

Each injector launch receives a temporary writable profile. The server captures
bounded stdout, stderr, exit information, and process context. It also suppresses
Windows fault dialogs for the launcher process tree and requests tree
termination on cancellation or timeout. An exit code of `0xE0434352` indicates
an unhandled CLR exception in the launcher.

Common causes are a missing packaged payload, target/server elevation mismatch,
security software blocking injection, or an unsupported target architecture.
Use the diagnostics profile with `inject_agent`, `agent_ping`, and a tool trace
to separate launcher failure from agent connection failure.

## Build and test

Source builds need the .NET 8 SDK, PowerShell 7, and Visual Studio 2022 or Build
Tools with MSBuild, Desktop development with C++, and a Windows 10 or 11 SDK.
The C++ toolchain builds Snoop's x86 and x64 generic injectors.

```powershell
git submodule update --init --recursive
pwsh scripts/build-snoop.ps1 -Configuration Debug
$env:DisableGitVersionTask = "true"
dotnet build src/WpfToolsMcp.McpServer/WpfToolsMcp.McpServer.csproj -c Debug
```

The environment setting prevents the pinned Snoop project's GitVersion task
from trying to normalize the parent repository. It is required in Git worktrees
and harmless in a normal clone. Keep it set in the same PowerShell session when
running the tests below.

Run the server from source with:

```powershell
dotnet run --project src/WpfToolsMcp.McpServer -- --tool-profile diagnostics
```

Prefer a focused snapshot test while developing:

```powershell
dotnet test tests/WpfToolsMcp.SnapshotTests/WpfToolsMcp.SnapshotTests.csproj -c Debug --filter "FullyQualifiedName~ToolProfileTests"
```

The complete snapshot project launches real WPF processes and exercises UIA and
agent injection:

```powershell
dotnet test tests/WpfToolsMcp.SnapshotTests/WpfToolsMcp.SnapshotTests.csproj -c Debug
```

The smoke runner accepts another WPF executable and writes a JSON report,
screenshots, and tree captures under `artifacts/smoke/<timestamp>` by default:

```powershell
dotnet run --project tools/WpfToolsMcp.McpSmokeRunner -- --exe C:\path\to\App.exe
```

CI runs on Windows, builds the Snoop payload and tool, and runs the snapshot
project. Tags matching `v*.*.*` run the same checks, pack the tool, and publish
it to NuGet.

Build a local Release package with:

```powershell
pwsh scripts/build-snoop.ps1 -Configuration Release
$env:DisableGitVersionTask = "true"
dotnet pack src/WpfToolsMcp.Tool/WpfToolsMcp.Tool.csproj -c Release -o artifacts
```

## Repository layout

| Path | Purpose |
|---|---|
| `src/WpfToolsMcp.McpServer` | stdio host, tool profiles, tool definitions, subscriptions |
| `src/WpfToolsMcp.Automation` | sessions, FlaUI/UIA, screenshots, input, tracing, agent orchestration |
| `src/WpfToolsMcp.Agent` | injected WPF inspector |
| `src/WpfToolsMcp.AgentProtocol` | bounded named-pipe request and response protocol |
| `src/WpfToolsMcp.Contracts` | shared request, response, and enum types |
| `src/WpfToolsMcp.Tool` | global-tool launcher and NuGet package |
| `src/WpfToolsMcp.TestApp*` | focused integration fixtures |
| `tests/WpfToolsMcp.SnapshotTests` | NUnit and Verify integration coverage |
| `tools/WpfToolsMcp.McpSmokeRunner` | black-box smoke runner |
| `references/snoopwpf` | pinned Snoop submodule |

The [architecture note](docs/architecture.md)
describes the process boundaries and recovery model. Accepted design decisions
live under `docs/decisions/`. The current implementation constraints are listed
in [known engineering risks](docs/known-issues.md).

## License

Original project code is MIT licensed. The packaged inspection payload includes
Snoop components under the Microsoft Public License. See
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
