# Changelog

This file covers every published WPF Tools MCP preview. Bullets group related
commits. Version links open the exact Git comparison where available;
preview.1 links to its release. Release-only changes are included when they
affect packaging, CI, licensing, or compatibility.

## [Unreleased]

- Added this repository changelog and reconstructed notes for every published
  preview from its tagged commit range.

## [0.1.0-preview.28] - 2026-08-27

- `find_elements` can limit a query to a bounded subtree with a `root` locator.
  ([#70](https://github.com/mkdevforge/wpf-tools-mcp/pull/70))
- Added `get_computed_properties_batch` for reading selected properties from a
  bounded list of WPF elements in one request.
  ([#70](https://github.com/mkdevforge/wpf-tools-mcp/pull/70))
- `get_data_context` can return bounded dotted `propertyPaths` without expanding
  an entire object graph. ([#70](https://github.com/mkdevforge/wpf-tools-mcp/pull/70))
- `inject_agent` can validate and target an optional window handle. Agent and
  session cleanup no longer blocks on captured synchronization contexts.
  ([#68](https://github.com/mkdevforge/wpf-tools-mcp/pull/68))
- Agent-private assemblies load in a dedicated `AssemblyLoadContext` to avoid
  conflicts with target application dependencies.
  ([#68](https://github.com/mkdevforge/wpf-tools-mcp/pull/68))
- Replaced stale planning documents with guidance checked against the current
  implementation. Added structured issue forms and updated the GitHub Actions
  and logging dependencies. ([#9](https://github.com/mkdevforge/wpf-tools-mcp/pull/9),
  [#62](https://github.com/mkdevforge/wpf-tools-mcp/pull/62),
  [#63](https://github.com/mkdevforge/wpf-tools-mcp/pull/63),
  [#64](https://github.com/mkdevforge/wpf-tools-mcp/pull/64))

## [0.1.0-preview.27] - 2026-07-31

- Added explicit agent-owned error codes and preserved bounded underlying causes
  across MCP errors, traces, subscriptions, property provenance, and process
  diagnostics.
- Hardened custom UIA control type and path formatting, property provenance
  failure labels, and release snapshots containing bounded causes.

## [0.1.0-preview.26] - 2026-07-31

- Fixed tagged release builds for detached Snoop submodule checkouts and enabled
  the hosted-runner crash fixture during release validation.

## [0.1.0-preview.25] - 2026-07-31

- Made MCP responses compact by default. Diagnostic expansion is bounded, tool
  successes use typed results, and failures use structured error envelopes.
- Added non-intrusive inspection and semantic interaction policies, with explicit
  reports of focus, foreground, and physical-input effects.
- Added deterministic DPI-aware client viewport sizing and capture evidence.
- Split session detachment from application close and termination. Process
  restart recovery preserves policy and rejects stale process identities.
- Added bounded event-driven dependency-property and `DataContext` observation,
  typed wait conditions, and correlated runtime event streams with sequence
  metadata.
- Added direct search context, layout diagnostics, dependency-property
  provenance, WPF command inspection, and validation-error inspection.
- Hardened injector startup against modal crash dialogs and returned bounded
  launcher failure details.
- Added stable backend and attachment failure codes, failure stages, retry and
  recovery guidance, trusted-local causes, and bounded candidate evidence.
- Added screenshot correlation with WPF and UIA elements, plus ordered PNG
  screenshot sequences with manifests and pinned capture context.
- Added explicit keys, modifier chords, text-entry modes, and observed keyboard
  navigation tracing.
- Added routing for application-owned native dialogs and diagnostic snapshots
  that collect several inspection sections against one target.
- Explained WPF-to-UIA and UIA-to-WPF mappings, rejected recycled UIA handles,
  fixed accessible-name coverage, and reported scan and truncation metadata.
- Added explicit realization of virtualized items through `ItemContainerPattern`
  and `VirtualizedItemPattern`.

## [0.1.0-preview.24] - 2026-06-20

- Corrected package license metadata to `MIT AND MS-PL`.
- Updated structured MCP content serialization for compatibility across MCP SDK
  versions.
- Added grouped Dependabot updates for NuGet and GitHub Actions, then updated the
  logging and workflow dependencies.

## [0.1.0-preview.23] - 2026-05-05

- Top-level window discovery now starts from the application window, uses
  visible Win32 enumeration, filters by process, bounds, and title, and avoids
  broad UIA scans.

## [0.1.0-preview.22] - 2026-05-05

- Window-handle lookup verifies that the HWND belongs to the attached process
  and resolves it directly instead of scanning all UIA top-level windows.

## [0.1.0-preview.21] - 2026-05-04

- WPF locators now match control text, headers, automation names, and peer names.
  Interaction tools resolve WPF targets consistently and recover stale handles.
- Added WPF `set_value` support for text, password, editable combo, and range
  controls, with WPF-to-UIA ambiguity and bounds evidence.
- `list_sessions` refreshes WPF capabilities. Focused `type_text` is supported,
  and `close_session` reports session removal and observed process state.
- Single WPF clicks try semantic UIA invocation before mouse fallback. Closing
  without an attached application is idempotent.
- Added bounded `get_uia_tree` and `get_uia_locators` exports with reusable
  locator suggestions and FlaUI snippets.

## [0.1.0-preview.20] - 2026-05-04

- Completed the internal rename to `WpfToolsMcp` across assemblies, namespaces,
  projects, agent payload paths, tests, workflow files, and environment variables.

## [0.1.0-preview.19] - 2026-05-04

- Renamed the public package and command to `MkDevForge.WpfToolsMcp` and
  `wpf-tools-mcp`. Updated MCP configuration examples plus the agent pipe,
  trace-file, and highlight-overlay names.

## [0.1.0-preview.18] - 2026-04-30

- Added stable WPF element handles for inspection, highlighting, scrolling,
  actions, and explicit release, with `wpf_handle_stale` errors.
- Added a compact default `core` tool profile. The `diagnostics` profile exposes
  backend controls, subscriptions, traces, performance sampling, picking,
  highlighting, and window tools.
- `take_screenshot`, `get_visual_tree`, `find_elements`, and `resolve_element`
  auto-inject the WPF agent when available and fall back to UIA for supported
  operations.
- Element screenshots require full visibility after auto-scroll by default and
  report `element_offscreen_after_scroll` failures. Active highlights can be
  drawn into requested screenshots.
- MCP failures include tool names and known error codes. Failed automatic WPF
  injection can be retried after 10 seconds.

## [0.1.0-preview.17] - 2026-02-28

- Added mouse-drag fallback for slider and multi-thumb `set_value` operations.
- Returned structured disabled-element and UIA action errors, including HRESULT
  details.
- Improved screenshot resource handling and capture error reporting.
- Included UIA class names and bounds in resolved element responses and raised
  search limits.

## [0.1.0-preview.16] - 2026-02-28

- `highlight_element` maps UIA handles to WPF visuals for in-process highlighting
  when the agent is available, with an overlay fallback.

## [0.1.0-preview.15] - 2026-02-27

- Screen screenshots include layered windows and overlays through `CAPTUREBLT`.

## [0.1.0-preview.14] - 2026-02-27

- Added `set_window_bounds` and `set_window_state`, including virtual-screen
  clamping and restore handling.
- Added screenshot rectangle annotations and optional annotated screenshots from
  `highlight_element`.
- `pick_element_at_point` accepts screen or client coordinates and returns the
  resolved screen point. Highlighting prefers the in-process WPF path.
- Advanced the bundled Snoop revision from `c1cc286` to `76d3d78`.

## [0.1.0-preview.13] - 2026-02-23

- Multiple MCP sessions can share one injected agent.
- Added `list_displays` with virtual-screen, work-area, primary-display, and DPI
  details.
- Screenshots and interactions can auto-scroll WPF and UIA elements into view.
  Capture and overlays account for multi-monitor virtual-screen bounds.
- Added shallow and detached repository guards to the Snoop GitVersion build.

## [0.1.0-preview.12] - 2026-02-22

- Added `mouse_click` with screen or client coordinates, three mouse buttons,
  and single or double clicks.
- Added `includeOffViewport` handling to tree, search, resolve, and UIA coverage
  operations.
- UIA coverage reports include issue counts. Non-finite WPF numbers serialize as
  `{NaN}`, `{Infinity}`, or `{-Infinity}`.
- Added shared reference build settings and explicit x86 and x64 Snoop restore
  before no-restore builds.

## [0.1.0-preview.11] - 2026-02-22

- Fixed release builds for the x86 and x64 Snoop injector payloads in detached
  or shallow CI checkouts.

## [0.1.0-preview.10] - 2026-02-22

- `take_screenshot` supports client or full-window capture and explicit clipping,
  with corrected bounds and PrintWindow or screen fallback.
- Point picking resolves the window under the requested point and validates a
  supplied window handle for both WPF and UIA picking.

## [0.1.0-preview.9] - 2026-02-22

- `get_data_context` defaults to bounded summary output with type and string
  summaries, truncation, and warnings. Full object serialization remains
  available within configured depth and property limits.
- Reworked UIA highlighting as a Win32 layered overlay with method and error
  reporting.
- UIA element handles can recover from runtime ID changes when their XPath still
  resolves.

## [0.1.0-preview.8] - 2026-02-21

- Improved backend-aware element screenshots with requested and captured bounds,
  clipping metadata, and WPF fallback.
- WPF highlighting uses the injected agent and restores Snoop highlight options.
  UIA highlighting uses a DPI-aware layered overlay.
- Improved scrolling for zero-bound targets and expanded tracing across
  controller, session, and subscription operations.

## [0.1.0-preview.7] - 2026-02-21

- Added `wait_for` conditions for attachment, visibility, enabled or actionable
  state, stability, values, and names. Interaction tools can auto-wait.
- Expanded locators with contains and type filters, visibility preference,
  strict selection, deterministic ordering, and action-aware ranking.
- Added `trace_start` and `trace_stop` to write tool durations, summaries, and
  errors as JSON.

## [0.1.0-preview.6] - 2026-02-21

- Added session IDs, session listing and closing, and active-window selection.
- Added reusable UIA and WPF element handles with resolution, release, stale
  handling, and drag actions.
- Added point picking and configurable highlighting with WPF and UIA backends.
- Added WPF inspection for bindings, computed properties, style chains,
  templates, and template-part references.
- Added binding-error subscriptions, UIA coverage reports, and UI-thread latency
  sampling with percentile summaries.

## [0.1.0-preview.5] - 2026-02-18

- Hardened application launch with shell and direct-process strategies,
  working-directory defaults, and Windows environment defaults.
- Process-name attachment accepts dotted names and an optional `.exe` suffix,
  then selects the newest matching process.

## [0.1.0-preview.4] - 2026-02-18

- Documented Snoop's Ms-PL license and included its notice and license text in
  packaged payloads.

## [0.1.0-preview.3] - 2026-02-17

- `take_screenshot` writes PNG or JPEG files and returns the path, dimensions,
  and format. Base64 output is optional.
- `launch_app` accepts a main-window timeout and can attach to an existing
  instance when the launched process has no resolvable main window.

## [0.1.0-preview.2] - 2026-02-17

- Added `reset_state` and cleaned up failed launch or attachment state so stale
  controller state cannot block subsequent launch or attach calls.

## [0.1.0-preview.1] - 2026-02-17

- Published the first .NET global tool with application launch, attachment,
  window listing, close, screenshot, and MCP server support.
- Added UIA tree and property inspection plus locator-based focus, click, invoke,
  text entry, value setting, selection, and scrolling.
- Added Snoop agent injection, agent health checks, and WPF visual-tree
  inspection.
- `select_item` supports text, index, item locators, and scroll-search across
  list, combo, tab, and tree controls.
- MCP failures preserve underlying error details.
- Added the initial release workflow and packaged the Snoop agent and injector
  payloads.
- Restored snapshot tests in CI and fixed package publication to select each
  `.nupkg` file explicitly.

[Unreleased]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.28...HEAD
[0.1.0-preview.28]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.27...v0.1.0-preview.28
[0.1.0-preview.27]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.26...v0.1.0-preview.27
[0.1.0-preview.26]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.25...v0.1.0-preview.26
[0.1.0-preview.25]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.24...v0.1.0-preview.25
[0.1.0-preview.24]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.23...v0.1.0-preview.24
[0.1.0-preview.23]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.22...v0.1.0-preview.23
[0.1.0-preview.22]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.21...v0.1.0-preview.22
[0.1.0-preview.21]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.20...v0.1.0-preview.21
[0.1.0-preview.20]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.19...v0.1.0-preview.20
[0.1.0-preview.19]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.18...v0.1.0-preview.19
[0.1.0-preview.18]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.17...v0.1.0-preview.18
[0.1.0-preview.17]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.16...v0.1.0-preview.17
[0.1.0-preview.16]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.15...v0.1.0-preview.16
[0.1.0-preview.15]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.14...v0.1.0-preview.15
[0.1.0-preview.14]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.13...v0.1.0-preview.14
[0.1.0-preview.13]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.12...v0.1.0-preview.13
[0.1.0-preview.12]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.11...v0.1.0-preview.12
[0.1.0-preview.11]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.10...v0.1.0-preview.11
[0.1.0-preview.10]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.9...v0.1.0-preview.10
[0.1.0-preview.9]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.8...v0.1.0-preview.9
[0.1.0-preview.8]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.7...v0.1.0-preview.8
[0.1.0-preview.7]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.6...v0.1.0-preview.7
[0.1.0-preview.6]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.5...v0.1.0-preview.6
[0.1.0-preview.5]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.4...v0.1.0-preview.5
[0.1.0-preview.4]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.3...v0.1.0-preview.4
[0.1.0-preview.3]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.2...v0.1.0-preview.3
[0.1.0-preview.2]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/mkdevforge/wpf-tools-mcp/releases/tag/v0.1.0-preview.1
