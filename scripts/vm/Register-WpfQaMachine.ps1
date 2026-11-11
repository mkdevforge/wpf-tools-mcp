#Requires -Version 7.3
<#
.SYNOPSIS
Registers an existing Hyper-V VM or another Windows machine as a WPF Tools MCP QA machine.

.DESCRIPTION
Use this script for a machine that New-WpfQaVm.ps1 did not create: a local
Hyper-V VM (for example one with a GPU), or a remote Windows machine. Then run
Install-WpfQaServer.ps1 -VmName <Name>.

The QA user must be an administrator of the machine. The server drives that
user's desktop, so use a user that exists only for QA, and do not use the
machine for other work while the server runs.

The script:
1. Stores the QA user's password for the current Windows user only (DPAPI).
   The host needs it to open the shared folder on the machine over SMB.
2. Creates an SSH key (~/.ssh/wpf-tools-qa_ed25519) if none exists.
3. Prepares the machine with guest/Initialize-QaMachine.ps1: OpenSSH Server
   with the key, a desktop that stays awake and unlocked, and optionally
   automatic logon and no automatic updates.
   - Hyper-V: the script runs it in the VM over PowerShell Direct. This needs
     an elevated PowerShell on the host.
   - Remote: if SSH with the key does not work yet, the script shows the
     command to run on the machine, and stops.
   Use -SkipInitialize if you prepared the machine yourself.
4. Checks SSH access and saves the machine details.

.EXAMPLE
./scripts/vm/Register-WpfQaMachine.ps1 -HyperVVmName 'QA Win11 GPU' -Name wpf-qa-hv -AutoLogon

.EXAMPLE
./scripts/vm/Register-WpfQaMachine.ps1 -ComputerName qa-box.corp.example -Name qa-box
#>
[CmdletBinding(DefaultParameterSetName = 'HyperV')]
param(
    [Parameter(Mandatory, ParameterSetName = 'HyperV')] [string] $HyperVVmName,
    [Parameter(Mandatory, ParameterSetName = 'Remote')] [string] $ComputerName,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$')] [string] $Name,
    [pscredential] $Credential,
    [Parameter(ParameterSetName = 'HyperV')] [string] $Address,
    [string] $SshKeyPath = (Join-Path $HOME '.ssh\wpf-tools-qa_ed25519'),
    [switch] $AutoLogon,
    [switch] $DisableAutomaticUpdates,
    [switch] $SkipInitialize
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
. (Join-Path $PSScriptRoot 'WpfQaVm.Common.ps1')

$kind = if ($PSCmdlet.ParameterSetName -eq 'HyperV') { 'hyperv' } else { 'remote' }
if (-not $Name) {
    $Name = (($HyperVVmName, $ComputerName | Where-Object { $_ } | Select-Object -First 1) -replace '[^A-Za-z0-9_.-]', '-').Trim('-')
}

$stateDir = Get-QaVmStateDirectory $Name
$infoPath = Join-Path $stateDir 'vm.json'
$credentialPath = Join-Path $stateDir 'guest-credential.xml'
if ((Test-Path -LiteralPath $infoPath) -and (Read-QaMachineInfo $Name).kind -eq 'vmware') {
    throw "'$Name' is a VMware VM that New-WpfQaVm.ps1 created. Choose another -Name."
}

if ($kind -eq 'hyperv') {
    Assert-HyperVModule
    $null = Get-VM -Name $HyperVVmName -ErrorAction Stop
    if (-not $SkipInitialize -and -not (Test-QaElevated)) {
        throw 'PowerShell Direct needs an elevated PowerShell on the host. Run this script as administrator, or run guest/Initialize-QaMachine.ps1 in the VM yourself and use -SkipInitialize.'
    }
}

if (-not $Credential) {
    $Credential = Get-Credential -Message "The QA user of $($HyperVVmName ?? $ComputerName) (an administrator). Use COMPUTER\user or DOMAIN\user."
}
if (-not $Credential) { throw 'A credential for the QA user is required.' }

# Split DOMAIN\user and user@domain. A bare user name is a local account; its
# domain is the computer name, which Initialize-QaMachine.ps1 reports.
$userName = $Credential.UserName
$domain = $null
if ($userName -match '^(?<domain>[^\\]+)\\(?<user>.+)$') {
    $userName = $Matches.user
    $domain = $Matches.domain
}
elseif ($userName -match '^(?<user>[^@]+)@(?<domain>.+)$') {
    $userName = $Matches.user
    $domain = $Matches.domain
}

New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$publicKey = Get-QaSshPublicKey $SshKeyPath

# Register again over an existing registration: keep what Install-WpfQaServer.ps1
# saved (port, listen mode, snapshot, server URL), so that Reset-WpfQaVm.ps1
# still finds the installed server.
$info = if (Test-Path -LiteralPath $infoPath) { Read-QaMachineInfo $Name } else {
    [pscustomobject][ordered]@{ vmName = $Name; createdAtUtc = (Get-Date).ToUniversalTime().ToString('o'); snapshot = $null }
}
Set-QaInfoValue $info 'kind' $kind
Set-QaInfoValue $info 'hyperVVmName' $(if ($kind -eq 'hyperv') { $HyperVVmName } else { $null })
Set-QaInfoValue $info 'fixedAddress' $(if ($kind -eq 'remote') { $ComputerName } elseif ($Address) { $Address } else { $null })
Set-QaInfoValue $info 'userName' $userName
Set-QaInfoValue $info 'domain' $domain
Set-QaInfoValue $info 'computerName' $null
Set-QaInfoValue $info 'sshKeyPath' $SshKeyPath
Set-QaInfoValue $info 'knownHostsPath' (Join-Path $stateDir 'known_hosts')

$guestAddress = $null
if ($kind -eq 'hyperv') {
    Write-Step "Starting the Hyper-V VM '$HyperVVmName'"
    Start-HyperVVm $HyperVVmName
    $guestAddress = Get-HyperVGuestAddress $info
}
else {
    $guestAddress = $ComputerName
}

$initialized = $null
if (-not $SkipInitialize) {
    if ($kind -eq 'hyperv') {
        Write-Step "Preparing the VM over PowerShell Direct"
        $session = New-PSSession -VMName $HyperVVmName -Credential $Credential -ErrorAction Stop
        try {
            $password = if ($AutoLogon) { $Credential.Password } else { $null }
            $initialized = Invoke-QaMachineInitialization $session $publicKey $password $DisableAutomaticUpdates.IsPresent
        }
        finally {
            Remove-PSSession $session
        }
    }
    else {
        $sshError = $null
        try {
            $null = Invoke-GuestSsh $info $guestAddress 'exit 0'
        }
        catch {
            $sshError = $_.Exception.Message
        }

        if ($sshError -match 'IDENTIFICATION HAS CHANGED|Host key verification failed') {
            throw "SSH to $ComputerName failed: $sshError If the machine was set up again, delete its line from $($info.knownHostsPath)."
        }
        elseif ($sshError -match 'Could not resolve hostname') {
            throw "SSH to $ComputerName failed: $sshError"
        }
        elseif ($sshError) {
            # No OpenSSH Server yet, or the key is not authorized: the machine
            # must run the preparation itself.
            $logon = if ($AutoLogon) { " -AutoLogonPassword (Read-Host -AsSecureString 'Password of $($Credential.UserName)')" } else { '' }
            $updates = if ($DisableAutomaticUpdates) { ' -DisableAutomaticUpdates' } else { '' }
            Write-Host ''
            Write-Host "SSH with the key does not work on $ComputerName yet: $sshError"
            Write-Host ''
            Write-Host "On $ComputerName, as $($Credential.UserName), in an elevated PowerShell, copy"
            Write-Host 'scripts\vm\guest\QaMachine.ps1 and Initialize-QaMachine.ps1 to one folder and run:'
            Write-Host ''
            Write-Host "  powershell -ExecutionPolicy Bypass -Command `"& .\Initialize-QaMachine.ps1 -AuthorizedKey '$publicKey'$logon$updates`""
            Write-Host ''
            Write-Host 'Then run this script again.'
            $Credential | Export-Clixml -LiteralPath $credentialPath
            exit 1
        }
        else {
            Write-Step "Preparing $ComputerName over SSH"
            $password = if ($AutoLogon) { $Credential.Password } else { $null }
            $initialized = Invoke-QaMachineInitializationOverSsh $info $guestAddress $publicKey $password $DisableAutomaticUpdates.IsPresent
        }
    }
}

Write-Step "Checking SSH access to $guestAddress"
# Read the account from the logon token: $env:USERDOMAIN is "WORKGROUP" in
# OpenSSH sessions, not the computer name of a local account.
$facts = (Invoke-GuestSsh $info $guestAddress '$domain, $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name.Split([char]92, 2); $json = [ordered]@{ computerName = $env:COMPUTERNAME; userName = $user; domain = $domain } | ConvertTo-Json -Compress; [regex]::Replace($json, "[^\x00-\x7F]", { param($m) "\u{0:x4}" -f [int][char]$m.Value })' |
    Select-Object -Last 1) | ConvertFrom-Json
Set-QaInfoValue $info 'userName' $facts.userName
Set-QaInfoValue $info 'domain' $facts.domain
Set-QaInfoValue $info 'computerName' $facts.computerName

$Credential | Export-Clixml -LiteralPath $credentialPath
Save-QaMachineInfo $info

if ($kind -eq 'hyperv' -and -not $info.fixedAddress) {
    # With the computer name known, prefer <computer>.mshome.net on the Default Switch.
    $guestAddress = Get-HyperVGuestAddress $info
}

Write-Step "The QA machine '$Name' is registered."
Write-Host ''
Write-Host "  Machine:  $guestAddress ($kind)"
Write-Host "  User:     $(Get-QaAccountName $info)"
if ($initialized) {
    Write-Host "  Prepared: automatic logon $(if ($initialized.autoLogon) { 'on' } else { 'unchanged' }), automatic updates $(if ($initialized.automaticUpdatesDisabled) { 'off' } else { 'unchanged' }), time source for $($initialized.hypervisor)"
}
Write-Host "  Next:     ./scripts/vm/Install-WpfQaServer.ps1 -VmName $Name"
