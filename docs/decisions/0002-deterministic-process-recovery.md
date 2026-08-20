# 0002: Recover a replaced process with a successor session

- Status: Accepted and implemented
- Date: 2026-07-29

## Context

Rebuilding a WPF application replaces its process. The new process may have the
same executable name and can eventually reuse a PID or HWND, but its UIA runtime
identities and injected WPF handles are different.

Choosing the newest same-name process would be unsafe when several instances
are running. Reusing the old session ID would also let stale handles appear to
belong to the replacement and would complicate subscription cleanup.

## Decision

Identify a process instance by PID and start time. Process-name attachment
succeeds only when one live process matches. Multiple matches return a bounded
`ambiguous_process` error ordered by start time and PID, both descending. Each
candidate has an opaque `processInstanceId`. Selecting that ID must match the
same PID and start time or fail with `stale_process_candidate`.

`attach_to_app` may receive an exited `sessionId`. It prepares a new controller
and successor session, sets its active main window without foreground
activation, and carries forward the old interaction policy unless the caller
overrides it. Only then does it publish the successor, retire the predecessor,
and stop predecessor subscriptions. A live session cannot be replaced. Failed
preparation or ambiguous selection leaves it unchanged.

Retired session IDs remain as bounded tombstones. A later call through one
returns `stale_session` with cause `process_replaced` and identifies the
successor. The successor keeps a bounded snapshot of old identities only so it
can report `stale_element` or `stale_window` with the same cause.

## Consequences

- One session always refers to one process instance.
- Callers must adopt the successor `sessionId` and resolve fresh windows and
  elements.
- `processInstanceId` protects process selection from PID reuse.
- Old-session cleanup cannot remove successor subscriptions.
- A server restart while the same target process remains alive is an agent
  reconnection, not process replacement.

## Implementation

- Session replacement: `src/WpfToolsMcp.Automation/SessionManager.cs`
- Stale identity tracking:
  `src/WpfToolsMcp.Automation/AutomationController.ProcessReplacement.cs`
- Integration tests:
  `tests/WpfToolsMcp.SnapshotTests/ControllerStateRecoverySnapshots.cs`
