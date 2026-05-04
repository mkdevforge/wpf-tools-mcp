# Phase 2 Code Review Findings (Injection + Pipe + WPF Inspection)

This document captures a thorough code review of the current Phase 2 implementation (Snoop injection + named pipe agent + first WPF-native inspection surface), with a focus on correctness, robustness, compatibility with “real apps”, and what to harden before expanding to deeper DTO wrappers (P2-M1/P2-M2).

Last reviewed: 2026-02-16

At the time of writing:
- `dotnet build -c Debug` succeeds for the repo.
- `dotnet test -c Debug` is green, including `InjectionSnapshots` (after building Snoop injector assets).

## Status (after P2-M0 hardening pass)

Implemented in the current codebase:
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

### 1) Concurrency + session isolation (singleton controller + mutable state)

**Why it matters:** In real usage, we will routinely restart the MCP server while leaving the target app running. Phase 2 should work in that scenario without requiring users to restart their app.

**Current posture:** `AutomationController` is registered as a DI singleton and carries mutable attachment state *and* mutable agent session state.

**Risk:** If MCP clients (or orchestrators) issue concurrent tool calls (`close_session` + `take_screenshot` + `inject_agent`, etc.), state can interleave in undefined ways.

**Mitigation implemented:** All MCP tool calls are serialized via a global async lock (`AutomationController.RunExclusiveAsync`) so state cannot interleave across concurrent tool calls.

**Still recommended:** Move to an explicit “session” concept (each `attach/launch` returns a session id and all other tools require it) if we want multiple independent app sessions in a single server process.

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

**Code reference:**
- `src/WpfToolsMcp.Automation/ProcessArchitectureDetector.cs:29`

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

**Code reference:**
- `src/WpfToolsMcp.Automation/SnoopInjector.cs:37`

### Connection retry window may be tight

The agent connect retry is ~3s total with short per-attempt timeouts. On slow machines or cold-start JIT, this could be flaky.

**Code reference:**
- `src/WpfToolsMcp.Automation/AutomationController.Agent.cs:146`

### CleanupAgent blocks synchronously

`CleanupAgent()` calls async dispose synchronously. With concurrent tool calls (known Phase 1 risk), this can deadlock or stall shutdown paths.

**Code reference:**
- `src/WpfToolsMcp.Automation/AutomationController.Agent.cs:204`

### Concurrency remains a fundamental risk (singleton controller + mutable state)

`AutomationController` is a DI singleton with mutable attachment state *and now* mutable agent session state. If MCP clients issue concurrent tool calls (common with some LLM orchestrators), calls like `close_session` / `inject_agent` / `take_screenshot` can interleave in unsafe ways.

This is called out in `docs/Phase1-CodeReviewFindings.md`, but it becomes more important as Phase 2 adds more long-running in-proc operations.

## Tooling / ergonomics

- The Phase 2 “debug” tools (`inject_agent`, `agent_ping`) are fine for bring-up, but the PRD direction is to **upgrade** existing inspection tools (`get_visual_tree`, `get_element_properties`) with a `backend` switch and auto-fallback (and use `get_visual_tree backend=wpf` rather than a separate WPF-only tree tool).
- `inject_agent` likely should accept an optional `windowHandle` so multi-window apps can inject targeting the desired dispatcher window.

## Testing gaps (recommended next)

1. Add a test that simulates the “server restart” path:
   - launch app
   - inject agent
   - dispose MCP client/server
   - reattach (new MCP server process)
   - ensure it can connect without re-injecting (requires deterministic pipe name or restartable server)
2. Expand WPF tree snapshot to assert:
   - CLR type names in nodes
   - visibility and DataContext type fields are populated as expected

## Build friction: Snoop.GenericInjector

`scripts/build-snoop.ps1` builds InjectorLauncher + GenericInjector, but GenericInjector requires a working C++ toolchain + Windows 10/11 SDK. This is not a code bug, but it’s a significant developer-experience constraint; document prerequisites and consider a dev-only fallback (prebuilt injector binaries) if it becomes a frequent blocker for contributors.

## Recent test hardening note

When running the full snapshot suite, `DataGridSnapshots` could be flaky due to edit-mode timing (Name editor element sometimes not appearing quickly enough after a double-click). The test now waits for the grid to appear, retries entering edit mode, and stabilizes the “StatusBefore” read.
