# Phase 1 Code Review Findings (defer until post–Phase 2)

This document captures Phase 1 review notes so we can intentionally defer them while implementing Phase 2 (Snoop injection + deep inspection), then return and harden Phase 1 behavior with better ergonomics, stability, and performance.

Snapshot tests were green at the time this was written (`dotnet test -c Debug`).

Updates since this review:
- Tool calls are now serialized via `AutomationController.RunExclusiveAsync` to mitigate concurrency races.
- `click_element` now differentiates `clickMode=auto` vs `clickMode=invokePreferred` (auto only prefers invoke for common invokables like buttons).
- `take_screenshot` defaults to `captureMode=screen` (CopyFromScreen of the client area; `auto` remains available for PrintWindow-first capture).

## Summary

Phase 1 is in good shape for the intended baseline: it can launch/attach, enumerate windows (including owned modal dialogs), inspect the UIA tree/properties, take screenshots, and interact (click/type/select/scroll) across a growing set of realistic WPF surfaces.

The main “return later” items are about:

- performance/scalability of locator resolution for large trees
- semantics/ergonomics of locator and click/type behavior in real apps
- concurrency/thread-safety for multiple tool calls
- better diagnostics without destabilizing snapshots

## Findings / follow-ups

### Window enumeration / dialogs

- **Current:** Window enumeration was upgraded to include owned modal dialogs by unioning FlaUI’s `GetAllTopLevelWindows()` with Win32 `EnumWindows` filtered by PID + visibility, then converting handles via `automation.FromHandle(hwnd).AsWindow()` and filtering to reasonable windows.
- **Why this matters:** Real apps frequently use owned modal dialogs; excluding them breaks `set_active_window`, element targeting by `windowHandle`, and screenshot flows.
- **Follow-ups:**
  - Consider exposing an opt-in `includeUntitledWindows` / `includeAllWindows` mode for cases where a legitimate window has an empty title (current filter skips empty titles).
  - Consider additional noise filtering (tool windows, hidden WPF helper windows) if it becomes an issue in real apps.

### Locator resolution semantics (strategy order vs combined matching)

- **Current:** Locator resolution tries strategies in priority order (AutomationId → Name → ClassName → XPath → Index-only) and returns the first strategy that resolves.
- **Risk:** If multiple fields are provided (e.g., both `name` and `className`), users may expect AND-filtering (“match all”) rather than “try best/first”. Today it does *not* combine.
- **Follow-ups:**
  - Document the behavior clearly in tool docs / PRD addendum.
  - Optionally add a mode for combined matching (AND-filter) when multiple fields are provided.

### Locator resolution performance (large trees)

- **Current:** Each strategy enumerates the full tree and often materializes arrays (for ambiguity reporting / index selection).
- **Risk:** On large/virtualized/custom-control-heavy apps, repeated full-tree enumeration can be slow.
- **Follow-ups:**
  - Stream results and early-exit when possible (e.g., stop after 2 matches when `index` is null and we only need to know “ambiguous”).
  - Avoid `ToArray()` unless necessary; consider iterators + counters.
  - Consider caching within a single tool call (not cross-call caching, per PRD).

### `clickMode` behavior

- **Current:** `click_element` uses InvokePattern for single-click when not `mouseAlways`; otherwise it uses mouse clicks. `InvokePreferred` currently behaves the same as `Auto`.
- **Risk:** Users may assume `InvokePreferred` is meaningfully different from `Auto`.
- **Follow-ups:**
  - Either simplify (remove/alias modes) or implement distinct behavior:
    - `Auto`: mouse-first (or invoke-first) with clear rules
    - `InvokePreferred`: try invoke first, then mouse fallback
    - `MouseAlways`: always use mouse

### `type_text` fallback is destructive (Ctrl+A/Delete)

- **Current:** If ValuePattern isn’t available, typing focuses the element and clears via Ctrl+A/Delete before typing.
- **Risk:** In real apps (custom editors, masked inputs, incremental search boxes), unconditional clearing may be undesirable.
- **Follow-ups:**
  - Add parameters such as `clearFirst` (default true/false), `append`, or `sendKeys` to control behavior.
  - Consider a “best effort” heuristic (e.g., only clear if the element supports text pattern / has selection) if we want smart defaults.

### Concurrency/thread-safety

- **Current:** `AutomationController` is a DI singleton holding mutable attachment state (`_application`, `_automation`).
- **Risk:** Concurrent tool calls can interleave (e.g., `close_session` during `click_element`) and corrupt state or fail unpredictably.
- **Follow-ups:**
  - Gate tool execution with a `SemaphoreSlim` (serialize calls) **or**
  - Move to per-session/per-connection controller lifetime if/when MCP transport supports it cleanly.

### Diagnostics and swallowed exceptions

- **Current:** Many “best effort” helpers swallow exceptions to keep the tools resilient.
- **Risk:** When something fails against a real app, root-causing can be hard without any debug output.
- **Follow-ups:**
  - Add optional debug logging (e.g., env var toggles) that prints useful details to stderr without impacting normal snapshot stability.

### Screenshot capture modes (Screen vs PrintWindow vs Auto)

- **Current:** Screenshot capture supports `screen`, `printWindow`, and `auto` (PrintWindow-first then screen fallback). There’s a manual dump test that writes both captures for comparison when `WPF_TOOLS_MCP_DUMP_SCREENSHOTS=1`.
- **Follow-ups:**
  - Decide on default capture mode for “real world” usage (likely `auto`) and ensure documentation/tool schema reflect that expectation.

## When to revisit

After Phase 2 is functional (injection + pipe + deep inspection), revisit this list and decide what to address next based on real usage feedback on non-demo, custom-control-heavy WPF apps.
