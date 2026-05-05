# WPF Tools MCP

WPF Tools MCP is an **MCP server** that gives AI assistants the ability to **inspect and control running WPF applications**.

- **Interaction (out-of-process):** FlaUI (UIA3)
- **Deep WPF inspection (in-process):** an injected agent powered by Snoop

## Install (dotnet tool)

```powershell
dotnet tool install -g MkDevForge.WpfToolsMcp --version 0.1.0-preview.23
dotnet tool update -g MkDevForge.WpfToolsMcp --version 0.1.0-preview.23
```

## Run

```powershell
wpf-tools-mcp
```

The server speaks MCP over **stdio**.

## Debugging tools

- `list_displays`: list connected displays and virtual screen bounds (helps with multi-monitor coordinate debugging).
- `trace_start` / `trace_stop`: record MCP tool timings and write a JSON trace file (defaults to `%TEMP%`).
- `performance_start` / `performance_stop`: lightweight UI-thread latency sampling.
- `set_window_bounds` / `set_window_state`: resize/restore windows (useful for deterministic screenshots).
- `take_screenshot`: supports optional annotation (rect/label) for debugging.

## MCP client config

Example (generic MCP config):

```json
{
  "mcpServers": {
    "wpf-tools-mcp": {
      "command": "wpf-tools-mcp"
    }
  }
}
```

## Notes / limitations

- **Windows only.**
- `inject_agent` requires the target process to be running as the **same user** and **not elevated** above the server.
- **ARM64 target processes are not supported** for injection (x86/x64 only).
- Custom controls that do not expose meaningful UIA peers/patterns may not be interactable via UI Automation.
- Multi-monitor setups are supported; tool coordinates are in **virtual screen** coordinates (which may be negative).

## Licensing

- WPF Tools MCP source code is licensed under MIT (`LICENSE`).
- The packaged Phase 2 payload redistributes Snoop components under Ms-PL.
- See `THIRD_PARTY_NOTICES.md` and `references/snoopwpf/License.txt`.

## Development

This repo uses a git submodule for Snoop:

- `references/snoopwpf`

Build the injector payloads (required for Phase 2 injection tools):

```powershell
pwsh scripts/build-snoop.ps1 -Configuration Debug
pwsh scripts/build-snoop.ps1 -Configuration Release
```
