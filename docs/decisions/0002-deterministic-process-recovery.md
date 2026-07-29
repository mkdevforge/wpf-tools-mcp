# 0002: Deterministic process recovery uses successor sessions

- Status: Accepted
- Date: 2026-07-29

## Context

A WPF rebuild normally exits the inspected process and starts another process
with the same executable name. PID, HWND, UIA runtime identity, and injected WPF
handles are all scoped to the original process instance. Selecting the newest
same-name process silently is unsafe when several instances are running, while
mutating an existing session in place can let stale handles alias reused PIDs or
HWNDs and can race with subscription cleanup.

## Decision

Process identity is PID plus process start time. Process-name attachment succeeds
only for one live match. Multiple matches return a bounded
`ambiguous_process` structured error ordered by start time descending and PID
descending. Each candidate includes an opaque `processInstanceId`; selecting it
must match the same PID and start time or fail as `stale_process_candidate`.

`attach_to_app` also accepts an exited `sessionId`. Recovery prepares a fresh
controller and successor session, establishes its active main window without
foreground activation, and preserves the prior interaction policy unless an
override is supplied. Only then does it atomically publish the successor and
retire the predecessor. It stops predecessor subscriptions after that registry
commit and before returning the new session ID. A live predecessor cannot be
replaced. Ambiguous selection or failed preparation does not change it or its
subscriptions.

Retired session IDs are retained as bounded tombstones. Any later call through
one reports `stale_session: process_replaced`, identifies the successor, and
states that all window handles and element IDs from the retired session must be
reacquired. A bounded identity snapshot is transferred to the successor so an
old element ID or window handle used with the new session reports
`stale_element: process_replaced` or `stale_window: process_replaced`, unless
Windows has already reassigned the same raw HWND value to a live successor
window.

## Consequences

- A session continues to mean one process instance; no hidden target change is
  possible.
- Recovery requires callers to adopt the returned successor `sessionId`, then
  reacquire window and element identities.
- Candidate selection is stable across PID reuse when `processInstanceId` is
  used.
- Subscription cleanup cannot remove resources registered for the successor,
  because predecessor and successor IDs differ.
- Raw HWND compatibility remains, without claiming that a numeric HWND alone
  is a cross-process-generation identity.
- MCP-server restart with the target still alive remains agent reconnection,
  not process replacement.
