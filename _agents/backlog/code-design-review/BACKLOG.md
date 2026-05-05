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
| 001 | Done | P2 | [Faulted subscriptions lose terminal event](001-faulted-subscriptions-lose-terminal-event.md) | review finding |
| 005 | In Progress | P3 | [Agent responses permit impossible states](005-agent-responses-permit-impossible-states.md) | review finding |
| 003a | Pending | P2 | [MCP tool boundary uses typed error codes](003a-mcp-tool-boundary-uses-typed-error-codes.md) | split from 003 |
| 003b | Pending | P2 | [Agent WPF resolve failures use stable codes](003b-agent-wpf-resolve-failures-use-stable-codes.md) | split from 003 |
| 003c | Pending | P2 | [Automation fallback logic consumes typed error codes](003c-automation-fallback-logic-consumes-typed-error-codes.md) | split from 003 |
| 002a | Pending | P2 | [Element target parser for click element](002a-element-target-parser-for-click-element.md) | split from 002 |
| 002b | Pending | P2 | [Central locator shape validation](002b-central-locator-shape-validation.md) | split from 002 |
| 002c | Pending | P2 | [Migrate remaining target-bearing operations](002c-migrate-remaining-target-bearing-operations.md) | split from 002 |
| 004 | Pending | P3 | [Tool input grammar drifts by profile](004-tool-input-grammar-drifts-by-profile.md) | review finding |

## Split Items

| ID | Status | Item | Replacement Items |
| --- | --- | --- | --- |
| 002 | Split | [Element targets are nullable bags](002-element-targets-are-nullable-bags.md) | 002a, 002b, 002c |
| 003 | Split | [Tool errors depend on message prefixes](003-tool-errors-depend-on-message-prefixes.md) | 003a, 003b, 003c |

## Order Notes

- Start with 001 because it is a concrete lifecycle bug with narrow blast radius.
- Do 005 next because it is a small protocol invariant fix that strengthens typed result/error work before expanding error-code handling.
- Do 003a-003c before the element target migration so error propagation is typed before more boundary parsing is introduced.
- Do 002a-002c after the error model slices. Start with one representative action path, then central locator validation, then the remaining target-bearing operations.
- Keep 004 last because it is independent profile grammar cleanup with lower correctness risk.
- QA on 2026-05-06 found no unsupported or stale items. Items 002 and 003 were split because their original scope crossed too many responsibilities for one focused implementation pass.

## Status Legend

- Pending: not started
- In Progress: actively being changed
- Review: implemented and under review
- Done: implemented, reviewed, and validated
- Blocked: cannot proceed without external input
- Split: retained as provenance; active work moved to child items
