# 0003: Remote QA runs the server inside a virtual machine

- Status: Accepted (step 1 implemented; later steps planned)
- Date: 2026-11-07

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
network. The design does not depend on a specific hypervisor; it is developed
on VMware Workstation and is intended for Hyper-V as well.

The work proceeds in steps:

1. Interactive desktop readiness. Before physical input, foreground activation,
   or screen capture, the server checks its session and fails with
   `interactive_desktop_unavailable` and a precise reason, instead of losing
   input or returning a black capture. `get_server_environment` reports the
   status, displays and adapters, OS, firmware system identity, and WPF render
   tier. This also helps local use.
2. Screenshots as MCP image content, so that a remote agent receives the image
   instead of a path on the server's disk.
3. Streamable HTTP transport as an opt-in mode. It binds to loopback by
   default; the recommended path from the host is an SSH tunnel. A non-loopback
   bind requires a bearer token. One MCP client holds the server at a time; a
   second client gets a clear busy error. Stdio stays the default.
4. VM setup scripts (auto-logon, no lock screen or screen saver, server started
   at logon in the interactive session) and a README section for remote mode,
   including the changed trust boundary.
5. Host-to-VM path mapping for `launch_app` and artifact access for traces.

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
  makes the render tier visible.
- Remote mode (step 3) changes the trust model in the README from
  same-user local use to an authenticated network endpoint, so that step must
  update the documented trust boundary.
