# Code Design Review Backlog

## Source

- Origin: review
- Created: 2026-05-06
- Scope: Horvat-style design review findings for non-test production code.

## Operating Instructions

- Work one item at a time.
- Read the relevant code thoroughly before editing.
- Keep changes minimal, clean, and correct; avoid workarounds.
- Update this backlog after each meaningful progress step.
- Review the result before marking an item Done.
- Run targeted verification for each item; run broader tests when the change affects shared behavior.
- Commit project changes after each completed fix.

## Progress

| ID | Status | Priority | Item | Source |
| --- | --- | --- | --- | --- |
| 001 | Pending | P2 | [Faulted subscriptions lose terminal event](001-faulted-subscriptions-lose-terminal-event.md) | review finding |
| 002 | Pending | P2 | [Element targets are nullable bags](002-element-targets-are-nullable-bags.md) | review finding |
| 003 | Pending | P2 | [Tool errors depend on message prefixes](003-tool-errors-depend-on-message-prefixes.md) | review finding |
| 004 | Pending | P3 | [Tool input grammar drifts by profile](004-tool-input-grammar-drifts-by-profile.md) | review finding |
| 005 | Pending | P3 | [Agent responses permit impossible states](005-agent-responses-permit-impossible-states.md) | review finding |

## Order Notes

- Start with 001 because it is a concrete lifecycle bug with narrow blast radius.
- Do 003 before or alongside 002 if element target refactoring needs typed error propagation.
- Defer 002 until there is enough time for careful boundary migration; it is the largest design change.
- 004 is low risk and can be handled independently.
- 005 is small, but should be coordinated with existing agent protocol snapshots.

## Status Legend

- Pending: not started
- In Progress: actively being changed
- Review: implemented and under review
- Done: implemented, reviewed, and validated
- Blocked: cannot proceed without external input
