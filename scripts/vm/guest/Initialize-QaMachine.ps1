<#
.SYNOPSIS
Prepares an existing Windows machine or VM for QA runs with the WPF Tools MCP server.

.DESCRIPTION
Run this script once on the QA machine, in Windows PowerShell 5.1 or
PowerShell 7, elevated, as the user whose desktop the server will drive. That
user must be an administrator. Register-WpfQaMachine.ps1 on the host runs it
for you in a local Hyper-V VM.

The script:
- installs and starts OpenSSH Server and authorizes the host's public key,
- keeps the desktop of the current user awake and unlocked, without screen
  saver or notifications,
- with -AutoLogonPassword, logs the current user on automatically at every
  start (the password is stored as an LSA secret),
- with -DisableAutomaticUpdates, turns automatic Windows updates off,
- in a VM, sets the clock source so that a snapshot or checkpoint revert does
  not leave a wrong time.

It writes a JSON result to standard output.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File Initialize-QaMachine.ps1 -AuthorizedKey 'ssh-ed25519 AAAA... wpf-tools-mcp-qa' -AutoLogonPassword (Read-Host -AsSecureString 'Password')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AuthorizedKey,
    [securestring] $AutoLogonPassword,
    [switch] $DisableAutomaticUpdates,
    [ValidateSet('Auto', 'None', 'HyperV', 'VMware')] [string] $Hypervisor = 'Auto'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QaMachine.ps1')

if (-not (Test-QaAdministrator)) {
    throw 'Run this script in an elevated PowerShell, as the QA user. The QA user must be an administrator.'
}

if ($AuthorizedKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+) [A-Za-z0-9+/=]+( .*)?$') {
    throw 'The authorized key is not an OpenSSH public key. Pass the content of the .pub file.'
}

if ($Hypervisor -eq 'Auto') {
    # Surface devices also report "Microsoft Corporation"; Hyper-V VMs report
    # the product "Virtual Machine".
    $bios = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' -ErrorAction SilentlyContinue
    $Hypervisor = if ("$($bios.SystemManufacturer)" -like 'VMware*') { 'VMware' }
        elseif ($bios.SystemManufacturer -eq 'Microsoft Corporation' -and $bios.SystemProductName -eq 'Virtual Machine') { 'HyperV' }
        else { 'None' }
}

# The account name from the logon token. $env:USERDOMAIN is "WORKGROUP" in
# OpenSSH sessions, not the computer name of a local account.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$domain, $userName = $identity.Name.Split('\', 2)

Enable-QaOpenSsh -AuthorizedKeys @($AuthorizedKey)
Set-QaDesktopSession

if ($AutoLogonPassword) {
    Enable-QaAutoLogon -UserName $userName -Domain $domain -Password $AutoLogonPassword
}

if ($DisableAutomaticUpdates) {
    Disable-QaAutomaticUpdates
}

if ($Hypervisor -ne 'None') {
    Set-QaTimeSource -Hypervisor $Hypervisor
}

[ordered]@{
    computerName = $env:COMPUTERNAME
    userName = $userName
    domain = $domain
    userSid = $identity.User.Value
    hypervisor = $Hypervisor
    autoLogon = [bool]$AutoLogonPassword
    automaticUpdatesDisabled = [bool]$DisableAutomaticUpdates
    sshd = (Get-Service -Name sshd).Status.ToString()
} | ConvertTo-Json -Compress | ConvertTo-AsciiJson
