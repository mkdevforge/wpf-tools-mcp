# Phase 2 Code Review Findings (Historical Snapshot)

> **Document status:** This review is a point-in-time record from 2026-02-16,
> not the current issue list. Statements labeled "Current" below refer to that
> snapshot unless a later resolution note says otherwise. Revalidate unresolved
> recommendations against current `main` before scheduling work.

This document captured a review of the early Phase 2 implementation (Snoop
injection, named-pipe agent, and the first WPF-native inspection surface).

Last reviewed: 2026-02-16

At the time of writing:
- `dotnet build -c Debug` succeeds for the repo.
- `dotnet test -c Debug` is green, including `InjectionSnapshots` (after building Snoop injector assets).

## Status (after P2-M0 hardening pass)

Implemented at the time of the review:
- ✅ Deterministic pipe name + connect-first reconnect (MCP server restart friendly)
- ✅ Pipe restricted to current user (`PipeOptions.CurrentUserOnly`)
- ✅ Pipe protocol max message size guard (25 MB)
- ✅ Agent server survives client disconnects during write
- ✅ MCP tool calls serialized to avoid concurrent state races
- ✅ `dotnet publish` includes `agent/` + `snoop/` payload folders

External dependency / setup friction:
- ⚠️ Building `Snoop.GenericInjector.*.dll` requires a working C++ toolchain + Windows SDK/toolset on the developer machine (e.g., VS “Desktop development with C++” + Windows 10/11 SDK).
- ⚠️ `references/snoopwpf` must be present (currently configured as a git submodule).

## What’s solid already

- **Overall architecture matches PRD**: out-of-proc interaction via FlaUI and in-proc inspection via an injected agent.
- **Protocol is simple and testable**: length-prefixed JSON messages with request IDs and explicit `Ok/Error`.
- **Injection is wired end-to-end** (subject to the GenericInjector binaries being present): MCP tool → `AutomationController` → `Snoop.InjectorLauncher` → `WpfToolsMcp.Agent.EntryPoint.Start(pipeName)` → named pipe server.
- **Deterministic packaging path**: `WpfToolsMcp.McpServer` copies `agent/` payloads next to the server binary, which makes it easy to resolve assets from `AppContext.BaseDirectory`.

## High-priority issues / blockers

### 1) Concurrency + session isolation (resolved architecture)

**Why it matters:** In real usage, we will routinely restart the MCP server while leaving the target app running. Phase 2 should work in that scenario without requiring users to restart their app.

**Historical posture:** `AutomationController` was a singleton carrying both
attachment and agent state, mitigated by one global async lock.

**Resolution:** `SessionManager` now creates an independent
`AutomationController` for every `launch_app` or `attach_to_app` session. Tool
calls are serialized within a session, while different sessions have separate
attachment, window, handle, and agent state.

### 2) Dependency / assembly-load collision risk (Default ALC injection)

**Why it matters:** The injected agent loads into the target’s **default** load context. Any dependencies we load can conflict with the app’s own dependencies.

**Current posture:**
- Agent loads `Snoop.Core.dll` + a small set of WpfToolsMcp assemblies from the payload folder.
- `EntryPoint` registers `AssemblyLoadContext.Default.Resolving` to load missing dependencies from the agent folder.

**Risk:** As Phase 2 expands (bindings, styles, DataContext materialization), dependency surface area grows and the chance of collision increases.

**Recommendation:** Add an “agent self-check” call that reports loaded assemblies + resolution failures; consider isolating dependencies (where possible) or aggressively minimizing what the agent references.

### 3) Injector prerequisites + packaging completeness

**Why it matters:** Phase 2’s injection story is only as good as how easy it is to build/copy the injector bits.

**Current posture:** `WpfToolsMcp.McpServer.csproj` copies `Snoop.InjectorLauncher.*` + `Snoop.GenericInjector.*` and also includes `CommandLine.dll` (required by InjectorLauncher at runtime).

## Medium-priority issues / improvements

### Architecture detection and injector selection

`ProcessArchitectureDetector` is good, but the fallback to `RuntimeInformation.ProcessArchitecture` can be wrong if the API calls fail (it gives *host* architecture, not *target*).

**Code area:** `src/WpfToolsMcp.Automation/ProcessArchitectureDetector.cs`

### Publishing / dotnet tool packaging

**Why it matters:** `WpfToolsMcp.Tool` publishes the MCP server and packages it, and users may also run `dotnet publish` directly. The published output must include the `agent/` + `snoop/` payload folders or `inject_agent` will fail.

**Previously observed behavior:** `dotnet publish src/WpfToolsMcp.McpServer/WpfToolsMcp.McpServer.csproj -c Debug -o <dir>` produced an output folder with `WpfToolsMcp.McpServer.exe` etc., but **no** `agent/` or `snoop/` folders.

**Root cause:** `WpfToolsMcp.McpServer.csproj` copies Phase 2 payloads only to `$(OutDir)` after `Build` (`CopyPhase2Assets` target). Publish uses `$(PublishDir)` and does not automatically include arbitrary files placed in `$(OutDir)`.

**Fix implemented:** Added a publish-time copy step (`AfterTargets="Publish"`) to copy payloads into `$(PublishDir)`.

### Agent pipe calls can hang without a timeout

**Why it matters:** If the agent stops responding (UI thread blocked, deadlock, pipe stuck), the MCP tool call can hang indefinitely because there is no per-call timeout on pipe reads.

**Previously observed behavior:** `AgentClient.CallRawAsync` used the tool’s cancellation token but did not impose an internal timeout. Many MCP callers won’t cancel, so hung agent calls could hang the server/tool invocation.

**Fix implemented:** Added a default timeout for agent calls (configurable via `WPF_TOOLS_MCP_AGENT_CALL_TIMEOUT_MS`).

### HWND truncation

InjectorLauncher expects `int` hwnd, so we cast `long → int`. This is likely fine on Windows, but if an HWND ever exceeds 32 bits, the value will wrap.

**Code area:** `src/WpfToolsMcp.Automation/SnoopInjector.cs`

### Connection retry window may be tight

The agent connect retry is ~3s total with short per-attempt timeouts. On slow machines or cold-start JIT, this could be flaky.

**Code area:** `src/WpfToolsMcp.Automation/AutomationController.Agent.cs`

### Injector launcher lifecycle and fault containment (resolved 2026-07-28)

**Previously observed behavior:** The upstream launcher writes to
`Environment.SpecialFolder.ApplicationData\Snoop\SnoopLog.txt` before its
top-level injection error handler. An inaccessible user profile could therefore
raise an unhandled CLR exception and a Windows application-error dialog.
Cancellation also stopped waiting without guaranteeing that a blocked launcher
and its redirected child process were terminated.

**Fix implemented:** Every launch receives a unique writable profile/temp
workspace, including a precreated Snoop log directory. The launcher subtree
inherits `SEM_NOGPFAULTERRORBOX`, `SEM_FAILCRITICALERRORS`, and
`SEM_NOOPENFILEERRORBOX` while the server error mode is restored immediately
after `Process.Start`. A bounded launcher runner drains and caps both output
streams concurrently, returns normal nonzero exits for existing diagnostics,
and requests entire-tree termination on caller cancellation or the configurable
`WPF_TOOLS_MCP_INJECTOR_TIMEOUT_MS` timeout. It boundedly waits for the launcher;
regression coverage also polls a recorded fixture child independently.
Controller disposal now cancels the active injection before waiting for the
per-session tool lock.

This remains a thin wrapper rather than a derivative Snoop launcher. Upstream
logging is not made universally best effort; instead, its known filesystem
dependency is isolated and writable, while any residual launcher crash is
contained, prevented from opening system fault UI, and surfaced as bounded
diagnostic output or an actionable start/timeout error.

### CleanupAgent blocks synchronously

`CleanupAgent()` calls async dispose synchronously, which can stall shutdown paths.

**Code area:** `src/WpfToolsMcp.Automation/AutomationController.Agent.cs`

### Concurrency status after the review

The singleton-controller issue was superseded by per-session controllers in
`SessionManager`. `AutomationController.RunExclusiveAsync` still protects
operations within each session.

## Tooling / ergonomics

- The Phase 2 “debug” tools (`inject_agent`, `agent_ping`) are fine for bring-up, but the PRD direction is to **upgrade** existing inspection tools (`get_visual_tree`, `get_element_properties`) with a `backend` switch and auto-fallback (and use `get_visual_tree backend=wpf` rather than a separate WPF-only tree tool).
- `inject_agent` likely should accept an optional `windowHandle` so multi-window apps can inject targeting the desired dispatcher window.

## Testing gaps recorded at the time

1. **Completed:**
   `InjectionSnapshots.Agent_reconnect_after_mcp_restart_snapshot` launches an
   app, injects the agent, restarts the MCP server, reattaches, and verifies
   reconnect without reinjection.
2. Historical suggestion: expand WPF tree snapshots to assert:
   - CLR type names in nodes
   - visibility and DataContext type fields are populated as expected

## Build friction: Snoop.GenericInjector

`scripts/build-snoop.ps1` builds InjectorLauncher + GenericInjector, but GenericInjector requires a working C++ toolchain + Windows 10/11 SDK. This is not a code bug, but it’s a significant developer-experience constraint; document prerequisites and consider a dev-only fallback (prebuilt injector binaries) if it becomes a frequent blocker for contributors.

## Recent test hardening note

When running the full snapshot suite, `DataGridSnapshots` could be flaky due to edit-mode timing (Name editor element sometimes not appearing quickly enough after a double-click). The test now waits for the grid to appear, retries entering edit mode, and stabilizes the “StatusBefore” read.
