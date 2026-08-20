# 0001: Capture related WPF diagnostics in one dispatcher callback

- Status: Accepted and implemented
- Date: 2026-07-29

## Context

A WPF investigation often needs bindings, `DataContext`, dependency properties,
layout, tree context, UIA properties, and a screenshot. Separate MCP calls are
separate observations. The session lock prevents other calls in that session
from interleaving, but the target dispatcher can process application work
between them.

UIA reads and native screenshot capture cannot run as part of an in-process WPF
dispatcher callback. A useful snapshot must expose that timing difference.

## Decision

Provide the read-only `capture_diagnostic_snapshot` tool. The caller chooses at
most eight named sections and one target: a locator, an element ID, or the
window root.

The server resolves and pins the session, process, HWND, element, and anchor
backend once. It sends all requested WPF sections in one agent request, and the
agent reads them in one `DispatcherPriority.Send` callback. Each section has
its own status and limits.

UIA properties and screenshots run in separate phases. The response records
timestamps, sources, capture groups, offsets, and aggregate skew. It does not
claim that evidence from different phases is atomic.

The input accepts a fixed set of read operations and budgets. It does not accept
scripts, expressions, arbitrary method calls, loops, or interaction steps.
Screenshots are file-backed, omit Base64, and do not auto-scroll.

## Consequences

- Related WPF evidence describes one dispatcher turn.
- UIA and rendered pixels remain explicitly separate observations.
- One failed section does not discard successful sections.
- Shared deadlines and item, node, value, and payload limits bound the call.
- Focused tools remain better for one section or for diagnostics-only controls
  not present in the composite request.
- Actions and recovery semantics remain separate from read-only capture.

## Implementation

- Tool entry point: `src/WpfToolsMcp.McpServer/Tools/DiagnosticTools.cs`
- Coordinator: `src/WpfToolsMcp.Automation/DiagnosticSnapshotCoordinator.cs`
- WPF capture: `src/WpfToolsMcp.Agent/WpfVisualTreeInspector.DiagnosticSnapshot.cs`
- Tests: `tests/WpfToolsMcp.SnapshotTests/DiagnosticSnapshot*Tests.cs`
