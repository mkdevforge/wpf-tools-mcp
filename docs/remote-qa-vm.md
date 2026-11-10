# QA virtual machine

Use a VM for QA runs that use physical input, so that the agent does not take
over your desktop. The MCP server runs in the VM's desktop session. The agent
stays on the host and connects to the server over HTTP. See
[ADR 0003](decisions/0003-remote-qa-in-a-virtual-machine.md) for the design.

The scripts support VMware Workstation Pro. They need PowerShell 7.2 or later
on the host, a Windows 11 ISO (for example Windows 11 Enterprise Evaluation
from the Microsoft Evaluation Center), and a Release build of the Snoop
injector:

```powershell
./scripts/build-snoop.ps1 -Configuration Release
```

## Create the VM

```powershell
./scripts/vm/New-WpfQaVm.ps1 -IsoPath D:\iso\Win11_Enterprise_Eval.iso -VmDirectory D:\VMs\wpf-tools-qa
```

The script needs no input after it starts. It takes about 20 to 40 minutes. It:

1. Downloads the .NET 8 SDK installer and checks its Microsoft signature.
2. Creates an SSH key (`~/.ssh/wpf-tools-qa_ed25519`) if none exists.
3. Creates the VM: legacy BIOS, 4 vCPUs, 8 GB RAM, an 80 GB NVMe disk, NAT
   network, and 3D acceleration.
4. Starts the VM without a window and installs Windows from a generated
   `autounattend.xml`. The setup checks for a TPM and Secure Boot are skipped.
   VMware needs VM encryption for a virtual TPM. UEFI boot from the Windows
   ISO waits for a key press, and VMware has no working command that sends
   keys to the guest. BIOS boot starts Setup from a blank disk without a
   prompt. For WPF testing, the firmware type makes no difference.
5. Runs `scripts/vm/guest/Setup-Guest.ps1` at the first logon. This installs
   VMware Tools, the .NET 8 SDK, and OpenSSH Server. It also keeps the session
   logged on, unlocked, and awake. It turns off the screen saver,
   notifications, and automatic updates. It also turns off the Windows Time
   service, so that only VMware Tools sets the clock. After a snapshot revert,
   the Windows Time service set the clock back to the snapshot time.
6. Removes the setup media and deletes the setup ISO, which holds the guest
   password.
7. Starts the VM, checks the guest setup result and SSH access, and sets the
   resolution to 1920x1080.
8. Installs the MCP server with `Install-WpfQaServer.ps1` (see below) and takes
   the snapshot `clean` of the running desktop. Use `-SkipServer` to create
   the VM without the server.

The VM gets your keyboard layout, regional format, and time zone. Use
`-InputLocale`, `-UserLocale`, `-TimeZone`, `-ScreenWidth`, and `-ScreenHeight`
to change them, and `-CpuCount`, `-MemoryMB`, and `-DiskGB` to change the size.

If the script stops while it waits, run it again with the same `-VmDirectory`
and `-Resume`.

## Install or update the MCP server

```powershell
./scripts/vm/Install-WpfQaServer.ps1
```

`New-WpfQaVm.ps1` runs this script for you. Run it again after you change the
server code. It:

1. Publishes the server from this repository in Release.
2. Shares the host folder `%LOCALAPPDATA%\WpfToolsMcp\vms\wpf-tools-qa\shared`
   with the VM. Use `-SharedFolder` to choose another folder.
3. Creates a random access token, or keeps the existing one. Use `-NewToken`
   to replace it.
4. Installs the server in the VM and registers a scheduled task. The task
   starts the server at the logon of the QA user, in the desktop session,
   without a console window. It restarts the server if the server stops with
   an error.
5. Checks the server from the host over HTTP.
6. Replaces the snapshot `clean`, so that a revert gives a running server. Use
   `-NoSnapshot` to keep the old snapshot.

The server listens on port 5765 and uses the `core` tool profile. Use `-Port`
and `-ToolProfile diagnostics` to change them.

At the end, the script shows the server URL and a `claude mcp add` command.
It also writes a client configuration with the token to
`%LOCALAPPDATA%\WpfToolsMcp\vms\wpf-tools-qa\mcp-client.json`:

```json
{
  "mcpServers": {
    "wpf-tools-qa": {
      "type": "http",
      "url": "http://192.168.208.128:5765/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

## Shared folder and paths

The VM sees the shared folder as `\\vmware-host\Shared Folders\wpf-qa`. The
server maps this path to the host folder:

- The server writes screenshots, screenshot sequences, and traces to the
  `artifacts` subfolder. Responses and sequence manifests give the host paths,
  so the agent can open the files directly.
- Put builds of the application under test in the shared folder, and give
  `launch_app` the host path. The server runs the application from the
  shared folder.

Paths outside the shared folder are paths in the VM. For example, an
`outputPath` on the host `C:` drive is a path on the VM's `C:` drive.

## Use the VM

- Revert to `clean` before a QA run to start from a known desktop:
  `vmrun -T ws revertToSnapshot <vmx> clean`, then
  `vmrun -T ws start <vmx> nogui`. The server runs again at once.
- To watch the VM, open its `.vmx` in VMware Workstation. The window attaches
  to the running VM. You can close the window again, and the VM continues to
  run. The guest desktop stays unlocked.
- Do not connect with Remote Desktop. A closed connection locks the session,
  and a minimized one stops rendering.
- Call `get_server_environment` first. It shows whether the server's desktop
  is available.
- The server log is `C:\ProgramData\WpfToolsMcp\server\logs\server.log` in the
  VM. The previous log is `server.previous.log`.
- The evaluation edition expires after 90 days. Create a new VM with the same
  command. Do not update the VM: an update that Windows Update installed made
  the VM fail to boot.

## Files on the host

The scripts keep their state in `%LOCALAPPDATA%\WpfToolsMcp\vms\<VmName>`:

| File | Content |
|---|---|
| `vm.json` | VMX path, guest user, IP address, SSH key, shared folder, server URL, and snapshot name. |
| `guest-credential.xml` | The random guest password. Only your Windows user can read it (DPAPI). |
| `http-token.xml` | The MCP access token. Only your Windows user can read it (DPAPI). |
| `mcp-client.json` | An MCP client configuration. It contains the token as plain text. |
| `known_hosts` | The guest SSH host key. |
| `setup-result.json`, `setup.log` | The result and log of the guest setup. |
| `shared` | The folder that the VM sees as `\\vmware-host\Shared Folders\wpf-qa`. |

To show the guest password:

```powershell
(Import-Clixml "$env:LOCALAPPDATA\WpfToolsMcp\vms\wpf-tools-qa\guest-credential.xml").GetNetworkCredential().Password
```

## Run the remote smoke test

`RemoteServerSmokeTests` drives the test application in the VM over HTTP. It
uses physical input, the injected agent, both capture modes, sequences, and
traces, and checks the host paths. Install the server with
`-ToolProfile diagnostics`, then run:

```powershell
$state = "$env:LOCALAPPDATA\WpfToolsMcp\vms\wpf-tools-qa"
$env:WPF_TOOLS_MCP_REMOTE_URL = (Get-Content -Raw "$state\vm.json" | ConvertFrom-Json).serverUrl
$env:WPF_TOOLS_MCP_REMOTE_TOKEN = (Import-Clixml "$state\http-token.xml").GetNetworkCredential().Password
$env:WPF_TOOLS_MCP_REMOTE_SHARED_DIR = "$state\shared"
dotnet test tests/WpfToolsMcp.SnapshotTests -c Release --filter FullyQualifiedName~RemoteServerSmokeTests
```
