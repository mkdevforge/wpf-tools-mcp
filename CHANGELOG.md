# Changelog

This changelog records notable changes to WPF Tools MCP. Curated release notes
begin with `v0.1.0-preview.28`; earlier preview history remains available in the
repository tags.

## [0.1.0-preview.28] - 2026-08-27

### Inspection

- `find_elements` can limit a query to a bounded subtree by accepting a `root`
  locator. ([#70](https://github.com/mkdevforge/wpf-tools-mcp/pull/70))
- Added `get_computed_properties_batch` for reading selected properties from a
  bounded list of WPF elements in one request. ([#70](https://github.com/mkdevforge/wpf-tools-mcp/pull/70))
- `get_data_context` can return bounded dotted `propertyPaths` without expanding
  an entire object graph. ([#70](https://github.com/mkdevforge/wpf-tools-mcp/pull/70))

### Reliability

- `inject_agent` can validate and target an optional window handle. Agent and
  session cleanup no longer blocks on captured synchronization contexts.
  ([#68](https://github.com/mkdevforge/wpf-tools-mcp/pull/68))
- Agent-private assemblies now load in a dedicated `AssemblyLoadContext` to
  avoid conflicts with target application dependencies.
  ([#68](https://github.com/mkdevforge/wpf-tools-mcp/pull/68))

### Maintenance

- Replaced stale planning documents with installation, configuration,
  architecture, tool, troubleshooting, and verification guidance checked
  against the implementation. ([#63](https://github.com/mkdevforge/wpf-tools-mcp/pull/63))
- Added structured forms for bug reports, feature requests, and engineering
  tasks. ([#64](https://github.com/mkdevforge/wpf-tools-mcp/pull/64))
- Updated the GitHub Actions and logging dependencies.
  ([#9](https://github.com/mkdevforge/wpf-tools-mcp/pull/9),
  [#62](https://github.com/mkdevforge/wpf-tools-mcp/pull/62))

[0.1.0-preview.28]: https://github.com/mkdevforge/wpf-tools-mcp/compare/v0.1.0-preview.27...v0.1.0-preview.28
