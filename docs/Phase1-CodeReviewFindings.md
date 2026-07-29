# Phase 1 Code Review Findings (Historical Snapshot)

> **Document status:** This is a point-in-time review record. Statements labeled
> "Current" in the findings describe the implementation at the time of review,
> not necessarily current `main`. Revalidate any remaining recommendation before
> turning it into work.

This document captured Phase 1 review notes before Phase 2 and later hardening.

Snapshot tests were green at the time this was written (`dotnet test -c Debug`).

Important changes since this review:
- `SessionManager` now creates one `AutomationController` per session, and calls are serialized within each session.
- Primary element-locator fields are composed as filters rather than tried as unrelated fallback strategies.
- `click_element` differentiates `clickMode=auto` from `clickMode=invokePreferred`.
- `take_screenshot` defaults to `captureMode=auto` (PrintWindow-first with screen fallback).

## Summary

Phase 1 is in good shape for the intended baseline: it can launch/attach, enumerate windows (including owned modal dialogs), inspect the UIA tree/properties, take screenshots, and interact (click/type/select/scroll) across a growing set of realistic WPF surfaces.

At the time, the main "return later" items were about:

- performance/scalability of locator resolution for large trees
- semantics/ergonomics of locator and click/type behavior in real apps
- better diagnostics without destabilizing snapshots

## Findings / follow-ups

### Window enumeration / dialogs

- **Current:** Window enumeration was upgraded to include owned modal dialogs by unioning FlaUI’s `GetAllTopLevelWindows()` with Win32 `EnumWindows` filtered by PID + visibility, then converting handles via `automation.FromHandle(hwnd).AsWindow()` and filtering to reasonable windows.
- **Why this matters:** Real apps frequently use owned modal dialogs; excluding them breaks `set_active_window`, element targeting by `windowHandle`, and screenshot flows.
- **Follow-ups:**
  - Consider exposing an opt-in `includeUntitledWindows` / `includeAllWindows` mode for cases where a legitimate window has an empty title (current filter skips empty titles).
  - Consider additional noise filtering (tool windows, hidden WPF helper windows) if it becomes an issue in real apps.

### Primary locator resolution semantics

- **Resolved:** Current primary element locators combine supplied identity
  fields as filters. Strict locators report ambiguity, while `index` or
  `strict=false` provide explicit non-unique selection behavior. Specialized
  nested locators can define narrower tool-specific semantics.

### Locator resolution performance (large trees)

- **Current:** Each strategy enumerates the full tree and often materializes arrays (for ambiguity reporting / index selection).
- **Risk:** On large/virtualized/custom-control-heavy apps, repeated full-tree enumeration can be slow.
- **Follow-ups:**
  - Stream results and early-exit when possible (e.g., stop after 2 matches when `index` is null and we only need to know “ambiguous”).
  - Avoid `ToArray()` unless necessary; consider iterators + counters.
  - Consider caching within a single tool call (not cross-call caching, per PRD).

### `clickMode` behavior

- **Resolved:** `InvokePreferred` tries InvokePattern whenever it is available;
  `Auto` only prefers invoke for common invokable controls; `MouseAlways`
  bypasses pattern invocation.

### `type_text` fallback is destructive (Ctrl+A/Delete)

- **Resolved:** `type_text` exposes explicit `Replace`, `Append`, and
  `AtSelection` modes while preserving its legacy omitted-mode behavior.
  `send_keys` separately exposes structured keys, modifier chords, and ordered
  sequences. Physical paths are policy-gated and report their focus/input
  requirements and actual effects.

### Concurrency/thread-safety

- **Resolved architecture:** `SessionManager` owns an independent
  `AutomationController` per session, and `RunExclusiveAsync` serializes calls
  that target the same session. Separate sessions no longer share attachment or
  agent state.

### Diagnostics and swallowed exceptions

- **Current:** Many “best effort” helpers swallow exceptions to keep the tools resilient.
- **Risk:** When something fails against a real app, root-causing can be hard without any debug output.
- **Follow-ups:**
  - Add optional debug logging (e.g., env var toggles) that prints useful details to stderr without impacting normal snapshot stability.

### Screenshot capture modes (Screen vs PrintWindow vs Auto)

- **Resolved default:** Screenshot capture supports `screen`, `printWindow`, and
  `auto`; the default is `auto` (PrintWindow-first, then screen fallback). The
  manual dump test still writes comparison captures when
  `WPF_TOOLS_MCP_DUMP_SCREENSHOTS=1`.

## When to revisit

Review any still-relevant items against current code and real application
feedback before adding them to an active backlog.
