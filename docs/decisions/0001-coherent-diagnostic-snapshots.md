# Coherent Diagnostic Snapshots

## Status

Accepted, 2026-07-29.

## Context

A WPF investigation often needs UIA properties, dependency properties,
bindings, DataContext, layout, binding errors, tree context, and a screenshot.
Calling each tool separately is slow and lets dispatcher work advance between
WPF reads. A server-side session lock prevents another call in that session
from interleaving, but it does not combine separate named-pipe requests into
one application dispatcher turn. Different MCP sessions can also have separate
server-side locks while sharing the same target process.

## Decision

Add the read-only `capture_diagnostic_snapshot` tool. The caller selects a
closed set of at most eight diagnostic sections and supplies one locator,
element handle, or no element target for the window root. The operation pins
the resolved session, process, HWND, element, and anchor backend once.

All requested WPF sections are sent in one capability-gated agent request and
captured in one `DispatcherPriority.Send` callback on the target HWND's
dispatcher. Each section remains independently fallible and bounded. UIA
properties and native screenshot capture remain separate phases because they
cannot be made part of the WPF dispatcher callback. Absolute timestamps,
offsets, capture groups, sources, and aggregate skew make that limitation
explicit. The response never claims cross-backend atomicity when more than one
phase contributes evidence.

The input exposes only named read sections and fixed budgets. It accepts no
method names, scripts, expressions, loops, arbitrary arguments, or action
steps. Screenshots are file-backed, omit Base64, and disable auto-scroll.
Depth, item, and node limits apply to section structures that expose those
dimensions. The value-length and payload budgets cover section evidence while
the shared target identity, its reusable handle and XPath, and screenshot paths
remain exact.

## Interaction Sequences

Short interaction sequences do not belong in this operation. Actions introduce
side effects, interaction-policy checks, idempotency, retry timing, partial-step
failure, and possible recovery semantics. Those concerns require a separate
follow-up contract and must not be hidden inside a diagnostic snapshot. No
interaction-sequence implementation is implied by this decision.

## Consequences

- One MCP call replaces repeated diagnostic orchestration for a pinned target.
- WPF evidence is coherent to one dispatcher turn; UIA and rendered pixels are
  explicitly later or earlier observations.
- Per-section failures do not discard successful evidence.
- Shared depth, item, node, value-length, deadline, and total payload limits
  keep the operation bounded.
- Existing focused tools remain the right choice for a single section or for
  specialist controls not present in the composite contract.
