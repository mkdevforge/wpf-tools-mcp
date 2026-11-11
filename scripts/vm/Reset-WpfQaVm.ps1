#Requires -Version 7.3
<#
.SYNOPSIS
Returns a QA machine to its clean state and waits until the MCP server is ready.

.DESCRIPTION
- VMware: reverts to the snapshot "clean" and starts the VM.
- Hyper-V: restores the checkpoint "wpf-qa-clean" and starts the VM. If the
  VM has no checkpoint (Hyper-V refuses checkpoints for some VMs with an
  assigned GPU), the script restarts Windows in the VM instead.
- Remote machines: restarts Windows.
Use -Restart to restart Windows instead of a revert.

The script then waits until the server answers get_server_environment with an
available desktop. A restart needs automatic logon (Register-WpfQaMachine.ps1
-AutoLogon), or someone who logs on.

.EXAMPLE
./scripts/vm/Reset-WpfQaVm.ps1
#>
[CmdletBinding()]
param(
    [string] $VmName = 'wpf-tools-qa',
    [switch] $Restart,
    [ValidateRange(1, 60)] [int] $TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
. (Join-Path $PSScriptRoot 'WpfQaVm.Common.ps1')

$info = Read-QaMachineInfo $VmName
$stateDir = Get-QaVmStateDirectory $VmName
$token = Get-QaAccessToken (Join-Path $stateDir 'http-token.xml')
$port = (Get-QaInfoValue $info 'port') ?? 5765
$listen = (Get-QaInfoValue $info 'listen') ?? 'Network'
$snapshot = Get-QaInfoValue $info 'snapshot'
$timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
$started = [Diagnostics.Stopwatch]::StartNew()

function Get-GuestBootTime([string] $Address) {
    $value = Invoke-GuestSsh $info $Address '(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString("o")'
    return ($value | Select-Object -Last 1).Trim()
}

# Restarts Windows and waits until it has started again. The boot time proves
# the restart, so that the readiness check never sees the server of the old boot.
# $GetAddress returns the current address, which can change with the restart.
function Restart-Guest([scriptblock] $GetAddress) {
    $address = & $GetAddress
    Wait-Until { Test-GuestSsh $info $address } $timeout "SSH on $address" 3
    $before = Get-GuestBootTime $address
    Write-Step "Restarting Windows on $address"
    try {
        $null = Invoke-GuestSsh $info $address 'shutdown.exe /r /t 0 /f /d p:4:1 /c "WPF Tools MCP QA reset"'
    }
    catch {
        # The SSH connection can drop before shutdown.exe reports success; the
        # boot time below shows whether the restart happened.
        Write-Verbose "shutdown.exe over SSH: $($_.Exception.Message)"
    }

    Wait-Until {
        try {
            $current = Get-GuestBootTime (& $GetAddress)
            $current -and $current -ne $before
        }
        catch {
            $false
        }
    } $timeout "Windows on $address to restart" 5
}

switch ($info.kind) {
    'vmware' {
        $vmware = Find-VMware
        if ($Restart -or -not $snapshot) {
            Restart-Guest { Get-GuestIpAddress $info.vmx -Info $info }
        }
        else {
            Write-Step "Reverting to the snapshot '$snapshot'"
            $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'revertToSnapshot', $info.vmx, $snapshot)
            if (-not (Test-VmRunning $info.vmx)) {
                $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'start', $info.vmx, 'nogui')
            }
        }
        $address = Get-GuestIpAddress $info.vmx -Info $info
    }
    'hyperv' {
        Assert-HyperVModule
        if ($Restart -or -not $snapshot) {
            Start-HyperVVm $info.hyperVVmName
            Restart-Guest { Get-HyperVGuestAddress $info }
        }
        else {
            Write-Step "Restoring the checkpoint '$snapshot'"
            Restore-HyperVCheckpoint $info.hyperVVmName $snapshot
        }
        $address = Get-HyperVGuestAddress $info
    }
    'remote' {
        $address = $info.fixedAddress
        Restart-Guest { $info.fixedAddress }
    }
    default { throw "Unknown QA machine kind '$($info.kind)' in vm.json." }
}

$recorded = Get-QaInfoValue $info 'address'
if ($recorded -and $recorded -ne $address) {
    Write-Warning "The machine address changed from $recorded to $address. MCP clients configured with $recorded cannot reach the server. Run Install-WpfQaServer.ps1 -VmName $VmName again to update the client configuration."
}

if ($listen -eq 'Loopback') {
    Write-Step 'Waiting for SSH'
    Wait-Until { Test-GuestSsh $info $address } $timeout "SSH on $address" 3
}

$connection = Open-QaServerConnection $info $address $port $listen
try {
    Write-Step "Waiting for the MCP server at $($connection.Url)"
    $environment = Wait-QaServerReady $connection.Url $token ($timeout - $started.Elapsed)
}
finally {
    Close-QaServerConnection $connection
}

Write-Step "The server is ready after $([int]$started.Elapsed.TotalSeconds) seconds: session $($environment.interactiveDesktop.sessionId), WPF render tier $($environment.rendering.wpfRenderTier)."
