# QA machines

Use a separate machine for QA runs that use physical input, so that the agent
does not take over your desktop. The MCP server runs in the desktop session of
the QA machine. The agent stays on the host and connects to the server over
HTTP. See [ADR 0003](decisions/0003-remote-qa-in-a-virtual-machine.md) for the
design.

The scripts in `scripts/vm` support three kinds of QA machine:

| Kind | Create or register | Shared folder | Reset |
|---|---|---|---|
| VMware VM | `New-WpfQaVm.ps1` creates it | VMware shared folder on the host | Snapshot `clean` |
| Hyper-V VM | `Register-WpfQaMachine.ps1 -HyperVVmName` | SMB share on the VM | Checkpoint `wpf-qa-clean`, or a restart |
| Remote machine | `Register-WpfQaMachine.ps1 -ComputerName` | SMB share on the machine | A restart |

All scripts need PowerShell 7.3 or later on the host and a Release build of
the Snoop injector:

```powershell
./scripts/build-snoop.ps1 -Configuration Release
```

The scripts keep the state of each machine in
`%LOCALAPPDATA%\WpfToolsMcp\vms\<Name>`. The default name is `wpf-tools-qa`.
Give `-VmName <Name>` to `Install-WpfQaServer.ps1` and `Reset-WpfQaVm.ps1` for
other machines.

## Create a VMware VM

This needs VMware Workstation Pro and a Windows 11 ISO, for example Windows 11
Enterprise Evaluation from the Microsoft Evaluation Center.

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

## Register a Hyper-V VM or another machine

Use an existing Windows 10 or 11 machine, for example a Hyper-V VM with a GPU
or a test machine on your network. The QA user must be an administrator of
the machine. The server drives the desktop of this user, so use a user that
exists only for QA.

For a Hyper-V VM on this host, run the script in an elevated PowerShell:

```powershell
./scripts/vm/Register-WpfQaMachine.ps1 -HyperVVmName 'QA Win11' -Name wpf-qa-hv -AutoLogon
```

For another machine:

```powershell
./scripts/vm/Register-WpfQaMachine.ps1 -ComputerName qa-box.example.com -Name qa-box -AutoLogon
```

The script asks for the credential of the QA user. Use `COMPUTER\user` for a
local account and `DOMAIN\user` for a domain account. It:

1. Stores the credential so that only your Windows user can read it (DPAPI).
   The host needs it to open the shared folder on the machine.
2. Creates an SSH key (`~/.ssh/wpf-tools-qa_ed25519`) if none exists.
3. Prepares the machine with `scripts/vm/guest/Initialize-QaMachine.ps1`:
   - It installs OpenSSH Server and authorizes the SSH key.
   - It keeps the desktop of the QA user awake and unlocked, without screen
     saver or notifications.
   - With `-AutoLogon`, it logs the QA user on at every start. Windows keeps
     the password as an LSA secret, not as plain text in the registry.
   - With `-DisableAutomaticUpdates`, it turns automatic updates off. Do not
     use this option on a machine that your IT department manages.
   - In a Hyper-V VM, it turns off Secure Time Seeding. The Windows Time
     service stays on, because Hyper-V time synchronization uses it.
4. Checks SSH access and saves the machine details.

For a Hyper-V VM, the script runs the preparation in the VM over PowerShell
Direct, which needs an elevated PowerShell on the host. For another machine,
the script runs the preparation over SSH if SSH with the key works already.
The automatic logon password goes to the machine over the standard input of
SSH, not on a command line. If SSH with the key does not work yet, the script
shows the SSH error and the command to run on the machine, and stops. Copy
`scripts/vm/guest/QaMachine.ps1` and `Initialize-QaMachine.ps1` to the machine,
run the command there in an elevated PowerShell as the QA user, and then run
`Register-WpfQaMachine.ps1` again.

If you register a machine again under the same name, the script keeps the
details of the installed server, such as the port and the listen mode.

To prepare the machine yourself, run `Initialize-QaMachine.ps1` on it and give
`-SkipInitialize` to `Register-WpfQaMachine.ps1`.

If OpenSSH Server cannot be installed because a WSUS server or a policy blocks
Features on Demand, install it from
[Win32-OpenSSH](https://github.com/PowerShell/Win32-OpenSSH/releases), and run
the preparation again.

## Install or update the MCP server

```powershell
./scripts/vm/Install-WpfQaServer.ps1 -VmName <Name>
```

`New-WpfQaVm.ps1` runs this script for you. Run it again after you change the
server code. It:

1. Publishes the server from this repository in Release. The server is
   self-contained, so the QA machine needs no .NET runtime.
2. Sets up the shared folder (see below).
3. Creates a random access token, or keeps the existing one. Use `-NewToken`
   to replace it.
4. Installs the server on the machine and registers a scheduled task. The
   task starts the server at the logon of the QA user, in the desktop session,
   without a console window. If the server stops, a loop in its launcher
   starts it again after 10 seconds.
5. Checks the server from the host over HTTP.
6. VMware: replaces the snapshot `clean`. Hyper-V: replaces the checkpoint
   `wpf-qa-clean`. A revert then gives a running server. Use `-NoSnapshot` to
   keep the old snapshot or checkpoint. Remote machines get no snapshot.

The server listens on port 5765 and uses the `core` tool profile. Use `-Port`
and `-ToolProfile diagnostics` to change them.

At the end, the script shows the server URL and a `claude mcp add` command.
It also writes a client configuration with the token to
`%LOCALAPPDATA%\WpfToolsMcp\vms\<Name>\mcp-client.json`:

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

### Network access and the SSH tunnel

The firewall rules for the MCP port and for SMB allow connections only from
the local subnet of the machine and from the host that ran the installation.
The local subnet includes the host of a local VM. If the address of the host
changes, run `Install-WpfQaServer.ps1` again.

HTTP has no encryption. Anyone who can read the network traffic can read the
token. The network between the host and a local VM stays on the host, so
plain HTTP is acceptable there. For a remote machine, use
`-Listen Loopback`:

```powershell
./scripts/vm/Install-WpfQaServer.ps1 -VmName qa-box -Listen Loopback
```

The server then accepts connections only from the machine itself. MCP clients
connect to `http://127.0.0.1:5765/mcp` on the host, through an SSH tunnel. The
script shows the `ssh ... -N -L 5765:127.0.0.1:5765 ...` command for the
tunnel. Keep the tunnel open while the MCP client uses the server.

## Shared folder and paths

The server writes screenshots, screenshot sequences, and traces to the
`artifacts` subfolder of the shared folder. Responses and sequence manifests
give the paths that the host sees, so the agent can open the files directly.
Put builds of the application under test in the `apps` subfolder, and give
`launch_app` the host path. The server runs the application from its own view
of the folder.

| Kind | Host path | Path on the QA machine |
|---|---|---|
| VMware VM | `%LOCALAPPDATA%\WpfToolsMcp\vms\<Name>\shared` (or `-SharedFolder`) | `\\vmware-host\Shared Folders\wpf-qa` |
| Hyper-V VM, remote machine | `\\<address>\wpf-qa` | `C:\WpfQa\shared` (or `-GuestSharedFolder`) |

For the SMB share, the install script saves the QA user's credential for the
machine's address in the Windows Credential Manager (`cmdkey`). The host and
the MCP client then open `\\<address>\wpf-qa` without a prompt. If Windows
reports that multiple connections to a server with different user names are
not allowed, run `net use \\<address> /delete` and install again.

Paths outside the shared folder are paths on the QA machine. For example, an
`outputPath` on the host `C:` drive is a path on the `C:` drive of the QA
machine.

The server starts from a batch file, which `cmd.exe` reads in the OEM code
page of the QA machine. The shared folder paths can contain letters such as
`ä` and `ö` if this code page has them, as code pages 437 and 850 do. If a
path has a character that the code page does not have, the install script
stops with an error.

## Reset the machine

Reset the machine before a QA run to start from a known desktop:

```powershell
./scripts/vm/Reset-WpfQaVm.ps1 -VmName <Name>
```

- VMware VM: the script reverts to the snapshot `clean`.
- Hyper-V VM: the script restores the checkpoint `wpf-qa-clean`. If the VM has
  no checkpoint, the script restarts Windows in the VM.
- Remote machine: the script restarts Windows.

Use `-Restart` to restart Windows instead of a revert. The script then waits
until the server reports an available desktop. A restart needs automatic
logon. A revert to `clean` takes about 10 seconds. A restart takes about 40
to 60 seconds.

## Use the machine

- Call `get_server_environment` first. It shows whether the server's desktop
  is available, the displays, and the WPF render tier.
- To watch a VMware VM, open its `.vmx` in VMware Workstation. The window
  attaches to the running VM. You can close the window again, and the VM
  continues to run. The guest desktop stays unlocked.
- To watch a Hyper-V VM, use a basic session in Virtual Machine Connection.
  An enhanced session is a Remote Desktop session. When you close it, the
  desktop of the QA user stays locked.
- Do not connect with Remote Desktop. A closed connection locks the session,
  and a minimized one stops rendering.
- The server log is `C:\ProgramData\WpfToolsMcp\server\logs\server.log` on
  the QA machine. The previous log is `server.previous.log`.
- The evaluation edition of Windows expires after 90 days. Create a new VMware
  VM with the same command. Do not update the VM: an update that Windows
  Update installed made the VM fail to boot.

## Hyper-V notes

These points come from Microsoft documentation and community reports. They
are not tested on a Hyper-V host, because the test host has no Hyper-V. The
Hyper-V commands of the scripts are tested against the Hyper-V PowerShell
module on a VM that cannot start, and the full Hyper-V flow is tested with
stand-ins for the Hyper-V commands.

- Checkpoints: only standard checkpoints keep the running desktop. With
  production checkpoints, a reset starts the VM from the checkpoint, and the
  server starts at the automatic logon. Hyper-V can refuse checkpoints for a
  VM with an assigned GPU. The install script then shows a warning, and
  `Reset-WpfQaVm.ps1` restarts the VM instead.
- Address: on the Default Switch, the VM address can change when the host
  restarts. The scripts use `<computer>.mshome.net` when it resolves to the
  VM. Otherwise they use the IP address. If the address changes,
  `Reset-WpfQaVm.ps1` shows a warning. Run `Install-WpfQaServer.ps1` again to
  update the client configuration. Use `-Address` with
  `Register-WpfQaMachine.ps1` to set a fixed address.
- GPU partitioning: with a GPU partition, the console session can have no
  display path. Screen captures can then be black, while `PrintWindow`
  captures work. Check `get_server_environment` and compare a `screen` and a
  `printWindow` screenshot before you rely on screen captures.
- Time: Hyper-V time synchronization runs through the Windows Time service, so
  the preparation keeps that service on.

## Files on the host

The scripts keep their state in `%LOCALAPPDATA%\WpfToolsMcp\vms\<Name>`:

| File | Content |
|---|---|
| `vm.json` | The machine kind, VMX path or Hyper-V VM name, guest user, address, SSH key, shared folder, server URL, and snapshot name. |
| `guest-credential.xml` | The guest password. Only your Windows user can read it (DPAPI). |
| `http-token.xml` | The MCP access token. Only your Windows user can read it (DPAPI). |
| `mcp-client.json` | An MCP client configuration. It contains the token as plain text. |
| `known_hosts` | The guest SSH host key. |
| `setup-result.json`, `setup.log` | VMware VMs only: the result and log of the guest setup. |
| `shared` | VMware VMs only: the folder that the VM sees as `\\vmware-host\Shared Folders\wpf-qa`. |

To show the guest password:

```powershell
(Import-Clixml "$env:LOCALAPPDATA\WpfToolsMcp\vms\wpf-tools-qa\guest-credential.xml").GetNetworkCredential().Password
```

## Run the remote smoke test

`RemoteServerSmokeTests` drives the test application on the QA machine over
HTTP. It uses physical input, the injected agent, both capture modes,
sequences, and traces, and checks the host paths. Install the server with
`-ToolProfile diagnostics`, then run:

```powershell
$state = "$env:LOCALAPPDATA\WpfToolsMcp\vms\wpf-tools-qa"
$machine = Get-Content -Raw "$state\vm.json" | ConvertFrom-Json
$env:WPF_TOOLS_MCP_REMOTE_URL = $machine.serverUrl
$env:WPF_TOOLS_MCP_REMOTE_TOKEN = (Import-Clixml "$state\http-token.xml").GetNetworkCredential().Password
$env:WPF_TOOLS_MCP_REMOTE_SHARED_DIR = $machine.sharedFolder
dotnet test tests/WpfToolsMcp.SnapshotTests -c Release --filter FullyQualifiedName~RemoteServerSmokeTests
```

With `-Listen Loopback`, open the SSH tunnel before you run the test.
