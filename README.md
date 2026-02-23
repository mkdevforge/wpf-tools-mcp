# WpfPilot

WpfPilot is an **MCP server** that gives AI assistants the ability to **inspect and control running WPF applications**.

- **Interaction (out-of-process):** FlaUI (UIA3)
- **Deep WPF inspection (in-process):** an injected agent powered by Snoop

## Install (dotnet tool)

```powershell
dotnet tool install -g MkDevForge.WpfPilot --version 0.1.0-preview.12
dotnet tool update -g MkDevForge.WpfPilot --version 0.1.0-preview.12
```

## Run

```powershell
wpfpilot
```

The server speaks MCP over **stdio**.

## Debugging tools

- `list_displays`: list connected displays and virtual screen bounds (helps with multi-monitor coordinate debugging).
- `trace_start` / `trace_stop`: record MCP tool timings and write a JSON trace file (defaults to `%TEMP%`).
- `performance_start` / `performance_stop`: lightweight UI-thread latency sampling.

## MCP client config

Example (generic MCP config):

```json
{
  "mcpServers": {
    "wpfpilot": {
      "command": "wpfpilot"
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

- WpfPilot source code is licensed under MIT (`LICENSE`).
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
