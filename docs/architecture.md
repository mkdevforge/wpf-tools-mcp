# Architecture

WPF Tools MCP has three process roles. Keeping them separate lets the server use
normal Windows automation without giving up WPF-specific inspection.

```text
MCP client
    |
    | stdio
    v
WpfToolsMcp.McpServer
    |                         |
    | UI Automation           | current-user named pipe
    v                         v
Windows desktop          WpfToolsMcp.Agent
                              |
                              | target Dispatcher
                              v
                         WPF object graph
```

## Server process

`WpfToolsMcp.McpServer` hosts the MCP stdio transport and registers either the
core or diagnostics tool set. `SessionManager` owns one
`AutomationController` per attached process session. `SubscriptionManager`
owns the bounded observation workers associated with those sessions.

The automation controller uses FlaUI's UIA3 backend for process windows,
automation elements, control patterns, and desktop interaction. It also owns
screenshot capture, element-handle registries, waits, traces, and agent
orchestration.

Calls within one session use an async mutex. This prevents two operations from
changing that session's active window, agent state, or handle registry at the
same time. Different sessions have independent controllers.

## Injected agent

The WPF agent is loaded into an x86 or x64 target through Snoop's generic
injector. The server selects an injector that matches the target architecture.
The package includes both launcher architectures, both generic injector DLLs,
the agent, and its managed dependencies.

The agent opens a named-pipe server with `PipeOptions.CurrentUserOnly`. Requests
use bounded length-prefixed JSON messages with request IDs. WPF work is sent to
the target window's dispatcher. The agent returns project-owned DTOs instead of
serializing Snoop or WPF objects across the pipe.

Snoop is used as a library and injector dependency. The project does not launch
the Snoop inspector UI or require a change to the target application.

## Backend routing

UIA and WPF answer different questions. UIA sees the accessible automation tree
and native windows. The agent sees WPF objects, including elements that lack a
useful automation peer.

Automatic routing classifies the selected window. WPF windows prefer the agent.
Known non-WPF frameworks use UIA. Unknown frameworks may probe WPF first. A
backend-neutral tool can fall back to UIA and reports that choice. A WPF-only
tool returns a capability error when the agent or requested dispatcher scope is
unavailable.

Element handles retain their backend. Cross-backend mapping is an explicit,
bounded operation because UIA elements and WPF visuals do not have a guaranteed
one-to-one relationship.

## Process and session identity

A session identifies one process instance, not merely one PID or executable
name. Process selection uses PID plus process start time. Ambiguous process-name
matches return opaque `processInstanceId` candidates.

When a rebuilt application replaces the process, `attach_to_app` creates a new
controller and successor session. It publishes the successor before retiring
the old session and cleaning up old subscriptions. Retired session IDs remain
as bounded tombstones so later calls can report `process_replaced` and point to
the successor.

Window handles, UIA runtime identities, and WPF element handles are scoped to
the old process. The successor imports a bounded identity snapshot only to
produce useful stale-handle errors. Callers must reacquire windows and elements.

## Diagnostic consistency

Ordinary MCP calls are separate observations. A per-session lock prevents
server calls from interleaving, but the target dispatcher can still process its
own work between calls.

`capture_diagnostic_snapshot` reduces that gap for related evidence. It resolves
the target once and captures all requested WPF sections in one dispatcher
callback. UIA and native screenshot phases stay separate. Timestamps, capture
groups, and skew report the remaining difference rather than claiming an atomic
cross-backend snapshot.

## Failure and cleanup rules

The server returns stable tool error codes with bounded diagnostic cause data.
Agent connection state and backend readiness are reported separately. A failed
injection does not turn a WPF result into a fabricated success.

Cancellation and session disposal stop MCP-owned subscriptions, traces,
temporary artifacts, pipe clients, and injector work. Injector launch uses a
temporary writable profile, captures bounded output, suppresses system fault
dialogs for its child process tree, and requests tree termination on timeout or
cancellation.

## Build and package flow

`scripts/build-snoop.ps1` builds Snoop's x86 and x64 launcher payloads and its
Win32 generic injector configurations. Building the generic injector requires
Visual Studio C++ build tools and a Windows SDK.

Local builds should set `DisableGitVersionTask=true` after the Snoop build and
before building this repository. The pinned Snoop project otherwise tries to
derive a version from the parent checkout, which fails in some Git worktrees.

`WpfToolsMcp.McpServer` copies the agent and Snoop payload into `agent/` and
`snoop/` beside the server output. `WpfToolsMcp.Tool` publishes that server,
checks that the required payload files exist, and packs everything below
`tools/net8.0/any/server/` in the NuGet tool package.

CI performs the Snoop build, tool build, and snapshot tests on Windows. A tag
matching `v*.*.*` repeats those checks, packs the tool, authenticates to NuGet
with OIDC, and publishes the package.

## Trust boundary

The design assumes a trusted, same-user development machine. The target process
is not treated as hostile. UIA providers, WPF getters, command `CanExecute`, and
value formatting can execute target code.

Identity checks, bounded work, current-user pipe access, cancellation, and
ownership-aware cleanup protect the developer machine from accidental misuse
and stale state. They do not turn in-process inspection into a sandbox.
