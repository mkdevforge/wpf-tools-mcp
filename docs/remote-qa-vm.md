# QA virtual machine

Use a VM for QA runs that use physical input, so that the agent does not take
over your desktop. See
[ADR 0003](decisions/0003-remote-qa-in-a-virtual-machine.md) for the design.

The setup script supports VMware Workstation Pro. It needs PowerShell 7.2 or
later on the host and a Windows 11 ISO, for example Windows 11 Enterprise
Evaluation from the Microsoft Evaluation Center.

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
   logged on, unlocked, and awake, and turns off the screen saver,
   notifications, and restarts for updates while the user is logged on.
6. Removes the setup media and deletes the setup ISO, which holds the guest
   password.
7. Starts the VM, checks the guest setup result and SSH access, sets the
   resolution to 1920x1080, and takes the snapshot `clean` of the running
   desktop.

The VM gets your keyboard layout, regional format, and time zone. Use
`-InputLocale`, `-UserLocale`, `-TimeZone`, `-ScreenWidth`, and `-ScreenHeight`
to change them, and `-CpuCount`, `-MemoryMB`, and `-DiskGB` to change the size.

If the script stops while it waits, run it again with the same `-VmDirectory`
and `-Resume`.

## Files on the host

The script keeps its state in `%LOCALAPPDATA%\WpfToolsMcp\vms\<VmName>`:

| File | Content |
|---|---|
| `vm.json` | VMX path, guest user, IP address, SSH key, and snapshot name. |
| `guest-credential.xml` | The random guest password. Only your Windows user can read it (DPAPI). |
| `known_hosts` | The guest SSH host key. |
| `setup-result.json`, `setup.log` | The result and log of the guest setup. |

To show the guest password:

```powershell
(Import-Clixml "$env:LOCALAPPDATA\WpfToolsMcp\vms\wpf-tools-qa\guest-credential.xml").GetNetworkCredential().Password
```

## Use the VM

- Revert to `clean` before a QA run to start from a known desktop:
  `vmrun -T ws revertToSnapshot <vmx> clean`, then
  `vmrun -T ws start <vmx> nogui`.
- To watch the VM, open its `.vmx` in VMware Workstation. The window attaches
  to the running VM. You can close the window again, and the VM continues to
  run. The guest desktop stays unlocked.
- Do not connect with Remote Desktop. A closed connection locks the session,
  and a minimized one stops rendering.
- The evaluation edition expires after 90 days. Create a new VM with the same
  command.
