# 0003: Remote QA runs the server inside a virtual machine

- Status: Accepted (implemented)
- Date: 2026-11-07, revised 2026-11-11

## Context

AI-driven QA sessions use physical mouse and keyboard input, foreground
activation, and screen capture. On the developer's own machine these effects
take over the desktop that the developer is using. Semantic-only interaction
(`interactionPolicy`) reduces this, but it cannot test drag, hover, real typing,
or keyboard navigation honestly, and the target windows still appear on the
developer's screen.

UI Automation, `SendInput`, `SetForegroundWindow`, and screen capture all need an
active, unlocked, interactive desktop in the server's own session. A process
started over SSH or as a service has no such desktop. A locked or disconnected
session drops input and can produce black captures.

We considered these alternatives:

- Semantic-only mode on the host: useful, but not full QA.
- Running the whole agent inside the VM: works without changes, but it does not
  involve this project and it moves the developer's workflow into the VM.
- A hidden desktop (`CreateDesktop`), Windows virtual desktops, or a second
  session on a client edition of Windows: input goes only to the input desktop,
  and client editions allow one interactive session.

## Decision

The MCP server runs inside a Windows VM, in the logged-on user's interactive
session. The agent stays on the host and connects to the server over the
network. The design does not depend on a specific hypervisor. The scripts
support VMware Workstation VMs, local Hyper-V VMs, and other Windows machines
that the host reaches over the network.

1. Interactive desktop readiness. Before physical input, foreground activation,
   or screen capture, the server checks its session and fails with
   `interactive_desktop_unavailable` and a precise reason, instead of losing
   input or returning a black capture. `get_server_environment` reports the
   status, displays and adapters, OS, firmware system identity, and WPF render
   tier. This also helps local use.
2. Streamable HTTP transport as an opt-in mode. It listens on loopback by
   default. An address other than loopback requires a bearer token of at least
   32 characters. The server rejects requests with an `Origin` header, so web
   pages cannot use it. Stdio stays the default.
3. Artifacts stay files. Screenshots, sequences, and traces are written to an
   artifact folder, and responses give their paths. On a QA machine, the
   artifact folder is in a shared folder: a host folder that VMware shares into
   the guest, or an SMB share on a Hyper-V VM or another machine. A path map
   makes the server publish host paths, also in sequence manifests, and accept
   host paths for `launch_app` and output paths.
4. QA machine scripts. `New-WpfQaVm.ps1` creates a VMware VM with automatic
   logon, no lock screen, no screen saver, and no automatic updates.
   `Register-WpfQaMachine.ps1` prepares an existing Hyper-V VM or another
   machine in the same way. Automatic updates stay on there unless the user
   turns them off. `Install-WpfQaServer.ps1` installs a self-contained server,
   which a scheduled task starts at logon in the desktop session, and replaces
   the snapshot or checkpoint. `Reset-WpfQaVm.ps1` reverts or restarts the
   machine and waits until the server is ready.
5. SSH for setup only. The scripts use SSH to install the server. MCP traffic
   uses HTTP. For a remote machine, the server can listen on loopback only, and
   the client connects through an SSH tunnel.

Two parts of the first plan were dropped:

- Screenshots as MCP image content. The file-and-path design already works when
  the client can read the files, and the shared folder gives it that. Image
  content would make every response large and would depend on client support.
- A single-client lease. Several clients can share the server, like several
  stdio servers on one desktop. Sessions survive when a client reconnects.

## Consequences

- The readiness check blocks only on positive evidence. Checks that Windows does
  not let the server read are reported as unverified and do not block.
- PrintWindow captures are also blocked when the desktop is unavailable. It is
  not verified that they render correctly in a locked or disconnected session,
  and a blank image must not be reported as a real screenshot.
- A minimized Remote Desktop client can stop rendering while the session still
  reports as active and unlocked. The check cannot see this; the VM guidance
  uses the hypervisor console instead.
- A VM without GPU acceleration renders WPF in software (render tier 0). Visual
  results can differ slightly from a developer machine; the environment report
  makes the render tier visible. VMware's 3D adapter gives render tier 2.
- The tool needs the ASP.NET Core Runtime in addition to the Desktop Runtime,
  also for stdio. The .NET SDK, which installs the tool, contains both. The
  install script publishes a self-contained server, so a QA machine needs no
  runtime.
- HTTP has no encryption. On the host-only network of a local VM this is
  acceptable. Over other networks, use `-Listen Loopback` and an SSH tunnel.
- Hyper-V can refuse checkpoints for a VM with an assigned GPU. A reset then
  restarts Windows, which takes about a minute instead of seconds.
- With GPU partitioning, community reports say that screen captures can be
  black in the console session while `PrintWindow` captures work. This is not
  tested, because the test host has no Hyper-V.
- Windows Update restarted the VM outside active hours although a user was
  logged on, and the VM did not boot after that update. The VM therefore has
  automatic updates turned off. Create a new VM from a current ISO instead of
  updating it.
