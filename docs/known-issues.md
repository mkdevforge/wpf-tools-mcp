# Known engineering risks

Last checked against `main` on 2026-08-20. These are verified implementation
constraints, not a roadmap. Recheck the linked code before acting on an item.

## Agent dependencies use the target's default load context

`WpfToolsMcp.Agent.EntryPoint` registers an
`AssemblyLoadContext.Default.Resolving` handler and loads missing agent
dependencies from the packaged agent directory. A target that already loaded an
incompatible version of the same managed dependency can still produce an
assembly identity or behavior conflict.

Code: `src/WpfToolsMcp.Agent/EntryPoint.cs`

## The Snoop launcher accepts a 32-bit HWND argument

`SnoopInjector` converts the target HWND from `long` to `int` with an unchecked
cast because the upstream launcher accepts an integer argument. A handle with
nonzero upper 32 bits would be truncated before injection.

Code: `src/WpfToolsMcp.Automation/SnoopInjector.cs`

## Agent cleanup blocks on asynchronous disposal

`AutomationController.CleanupAgent` calls `AgentClient.DisposeAsync()` through
`GetAwaiter().GetResult()`. This synchronous wait can delay a failure or
shutdown path while an in-flight pipe call unwinds.

Code: `src/WpfToolsMcp.Automation/AutomationController.Agent.cs`

## Explicit injection cannot select a window

The public `inject_agent` tool accepts only `sessionId`. Initial injection uses
the controller's `FindMainWindow` result and cannot name a different HWND. This
matters for applications that create WPF windows on separate dispatcher
threads. Later WPF tools can accept a `windowHandle`, but the first injection
entry point remains tied to the selected main window.

Code: `src/WpfToolsMcp.McpServer/Tools/AgentTools.cs` and
`src/WpfToolsMcp.Automation/AutomationController.Agent.cs`
