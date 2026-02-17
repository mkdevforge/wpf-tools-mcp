# WpfPilot

WpfPilot is an **MCP server** that gives AI assistants the ability to **inspect and control running WPF applications**.

- **Interaction (out-of-process):** FlaUI (UIA3)
- **Deep WPF inspection (in-process):** an injected agent powered by Snoop

## Install (dotnet tool)

```powershell
dotnet tool install -g MkDevForge.WpfPilot --version 0.1.0-preview.1
```

## Run

```powershell
wpfpilot
```

The server speaks MCP over **stdio**.

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

## Development

This repo uses a git submodule for Snoop:

- `references/snoopwpf`

Build the injector payloads (required for Phase 2 injection tools):

```powershell
pwsh scripts/build-snoop.ps1 -Configuration Debug
pwsh scripts/build-snoop.ps1 -Configuration Release
```

