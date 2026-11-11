# Helpers shared by the QA machine scripts. Dot-source this file; the functions
# that call vmrun use $vmware from Find-VMware in the caller's scope.
#
# A QA machine is one of these kinds (the "kind" value in vm.json):
# - vmware: a VM that New-WpfQaVm.ps1 created in VMware Workstation,
# - hyperv: an existing local Hyper-V VM, registered with Register-WpfQaMachine.ps1,
# - remote: any other Windows machine that the host reaches over the network,
#   registered with Register-WpfQaMachine.ps1.

function Write-Step([string] $Message) {
    Write-Host "[$(Get-Date -Format HH:mm:ss)] $Message"
}

function Get-QaVmStateDirectory([string] $VmName) {
    Join-Path $env:LOCALAPPDATA "WpfToolsMcp\vms\$VmName"
}

function Read-QaMachineInfo([string] $VmName) {
    $path = Join-Path (Get-QaVmStateDirectory $VmName) 'vm.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "No QA machine named '$VmName' was found. Create a VMware VM with New-WpfQaVm.ps1, or register a Hyper-V VM or another machine with Register-WpfQaMachine.ps1."
    }

    $info = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    if (-not $info.PSObject.Properties['kind']) {
        # vm.json files from before Hyper-V support describe VMware VMs.
        $info | Add-Member -NotePropertyName kind -NotePropertyValue 'vmware'
    }

    return $info
}

function Save-QaMachineInfo([pscustomobject] $Info) {
    $path = Join-Path (Get-QaVmStateDirectory $Info.vmName) 'vm.json'
    $Info | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8NoBOM
}

function Set-QaInfoValue([pscustomobject] $Info, [string] $Name, $Value) {
    $Info | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

function Get-QaInfoValue([pscustomobject] $Info, [string] $Name) {
    if ($Info.PSObject.Properties[$Name]) { return $Info.$Name }
    return $null
}

function Test-QaElevated {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-QaSshPublicKey([string] $KeyPath) {
    if (-not (Test-Path -LiteralPath $KeyPath)) {
        Write-Step "Creating SSH key $KeyPath"
        New-Item -ItemType Directory -Force -Path (Split-Path $KeyPath) | Out-Null
        & ssh-keygen -t ed25519 -f $KeyPath -N '' -C "wpf-tools-mcp-qa@$env:COMPUTERNAME" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "ssh-keygen failed with exit code $LASTEXITCODE." }
    }

    $public = "$KeyPath.pub"
    if (-not (Test-Path -LiteralPath $public)) {
        throw "The public key '$public' was not found."
    }

    return (Get-Content -Raw -LiteralPath $public).Trim()
}

# Joins arguments into one command line with the quoting rules of the
# Microsoft C runtime, which ssh.exe and most Windows programs use.
function ConvertTo-QaCommandLine([string[]] $Arguments) {
    $quoted = foreach ($argument in $Arguments) {
        if ($argument -and $argument -notmatch '[\s"]') { $argument; continue }
        # Double the backslashes before a quote and before the closing quote.
        $escaped = [regex]::Replace($argument, '(\\*)"', { param($m) $m.Groups[1].Value * 2 + '\"' })
        $escaped = [regex]::Replace($escaped, '(\\+)$', { param($m) $m.Groups[1].Value * 2 })
        '"' + $escaped + '"'
    }
    return $quoted -join ' '
}

function Wait-Until([scriptblock] $Condition, [TimeSpan] $Timeout, [string] $Description, [int] $IntervalSeconds = 15) {
    $deadline = (Get-Date) + $Timeout
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) {
            $duration = if ($Timeout.TotalMinutes -ge 1) { "$([int]$Timeout.TotalMinutes) minutes" } else { "$([int]$Timeout.TotalSeconds) seconds" }
            throw "Timed out after $duration while waiting for $Description."
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}

# ---------------------------------------------------------------------------
# VMware Workstation

function Find-VMware {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'VMware\VMware Workstation'),
        (Join-Path ${env:ProgramFiles(x86)} 'VMware\VMware Workstation')
    )
    $dir = $candidates | Where-Object { Test-Path (Join-Path $_ 'vmrun.exe') } | Select-Object -First 1
    if (-not $dir) {
        throw 'VMware Workstation was not found. Install VMware Workstation Pro first.'
    }

    $tools = Join-Path $dir 'windows.iso'
    if (-not (Test-Path $tools)) {
        throw "The VMware Tools ISO '$tools' was not found. Repair the VMware Workstation installation."
    }

    [pscustomobject]@{
        VmRun = Join-Path $dir 'vmrun.exe'
        VmCli = Join-Path $dir 'vmcli.exe'
        VDiskManager = Join-Path $dir 'vmware-vdiskmanager.exe'
        ToolsIso = $tools
    }
}

function Invoke-Tool([string] $FilePath, [string[]] $Arguments, [switch] $AllowFailure) {
    $output = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "$(Split-Path $FilePath -Leaf) $($Arguments[0..1] -join ' ') failed with exit code $LASTEXITCODE`: $($output -join ' ')"
    }

    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
}

function Test-VmRunning([string] $Vmx) {
    $list = (Invoke-Tool $vmware.VmRun @('-T', 'ws', 'list')).Output
    $full = [IO.Path]::GetFullPath($Vmx)
    return [bool]($list | Where-Object { $_.Trim() -ieq $full })
}

function Invoke-GuestVmRun([string] $Vmx, [pscredential] $Credential, [string[]] $Arguments, [switch] $AllowFailure) {
    $prefix = @('-T', 'ws', '-gu', $Credential.UserName, '-gp', $Credential.GetNetworkCredential().Password)
    return Invoke-Tool $vmware.VmRun ($prefix + $Arguments) -AllowFailure:$AllowFailure
}

# "getGuestIPAddress -wait" fails at once while VMware Tools is not running yet,
# for example during a guest restart, so retry. After some snapshot reverts,
# VMware keeps reporting that Tools is not running although the guest runs
# normally. Then the last known address is used if SSH answers there with the
# host key that known_hosts holds for it, which proves that it is the same guest.
function Get-GuestIpAddress([string] $Vmx, [TimeSpan] $Timeout = [TimeSpan]::FromMinutes(10), [pscustomobject] $Info) {
    $found = @{}
    $started = [Diagnostics.Stopwatch]::StartNew()
    $known = if ($Info) { Get-QaInfoValue $Info 'address' }
    Wait-Until {
        $result = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'getGuestIPAddress', $Vmx, '-wait') -AllowFailure
        if ($result.ExitCode -eq 0) {
            $found.Value = ($result.Output | Select-Object -Last 1).Trim()
        }
        elseif ($known -and $started.Elapsed.TotalSeconds -ge 60 -and (Test-GuestSsh $Info $known -KnownHostOnly)) {
            Write-Step "VMware Tools does not report the guest address; using the last known address $known, which answers over SSH"
            $found.Value = $known
        }
        [bool]$found.Value
    } $Timeout 'the guest IP address from VMware Tools' 5
    return $found.Value
}

# ---------------------------------------------------------------------------
# Hyper-V

function Assert-HyperVModule {
    if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
        throw 'The Hyper-V PowerShell module was not found. Turn on "Hyper-V Module for Windows PowerShell" in Windows Features.'
    }
}

function Start-HyperVVm([string] $Name) {
    $vm = Get-VM -Name $Name -ErrorAction Stop
    switch ("$($vm.State)") {
        'Running' { return }
        'Paused' { Resume-VM -Name $Name -ErrorAction Stop }
        default { Start-VM -Name $Name -ErrorAction Stop }
    }
}

function Get-HyperVGuestIPv4([string] $Name, [TimeSpan] $Timeout = [TimeSpan]::FromMinutes(10)) {
    $found = @{}
    Wait-Until {
        $found.Value = Get-VMNetworkAdapter -VMName $Name -ErrorAction Stop |
            ForEach-Object { $_.IPAddresses } |
            Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' -and $_ -notlike '169.254.*' } |
            Select-Object -First 1
        [bool]$found.Value
    } $Timeout "an IPv4 address of the Hyper-V VM '$Name'. Check that its network adapter is connected and that the Key-Value Pair Exchange integration service runs" 5
    return $found.Value
}

# The address under which the host reaches the guest. On the Hyper-V Default
# Switch, the guest IP address can change when the host restarts; the name
# <computer>.mshome.net follows it, so it is used when it resolves to the guest.
function Get-HyperVGuestAddress([pscustomobject] $Info) {
    $fixed = Get-QaInfoValue $Info 'fixedAddress'
    if ($fixed) { return $fixed }

    $ip = Get-HyperVGuestIPv4 $Info.hyperVVmName
    $adapter = Get-VMNetworkAdapter -VMName $Info.hyperVVmName | Where-Object { $_.IPAddresses -contains $ip } | Select-Object -First 1
    $computerName = Get-QaInfoValue $Info 'computerName'
    if ($adapter -and $adapter.SwitchName -eq 'Default Switch' -and $computerName) {
        $name = "$computerName.mshome.net".ToLowerInvariant()
        try {
            $resolved = [Net.Dns]::GetHostAddresses($name) | ForEach-Object { $_.ToString() }
            if ($resolved -contains $ip) { return $name }
            Write-Warning "$name resolves to $($resolved -join ', '), not to the VM address $ip. Using $ip, which can change when the host restarts."
        }
        catch {
            Write-Warning "$name does not resolve ($($_.Exception.Message)). Using $ip, which can change when the host restarts."
        }
    }

    return $ip
}

# Replaces the checkpoint so that a failure never leaves the VM without one:
# 1. take the new checkpoint as "<Name> (new)",
# 2. rename the old checkpoints to "<Name> (old)",
# 3. rename the new checkpoint to <Name>; if this fails, restore the old names,
# 4. remove the old checkpoints.
# Returns the checkpoint type of the VM: only "Standard" checkpoints keep the
# running desktop.
function Save-HyperVCheckpoint([string] $VmName, [string] $Name) {
    $temporary = "$Name (new)"
    $previous = "$Name (old)"
    Get-HyperVCheckpoint $VmName $temporary | Remove-VMCheckpoint -Confirm:$false
    Get-HyperVCheckpoint $VmName $previous | Remove-VMCheckpoint -Confirm:$false

    # Get-VMCheckpoint can list a new checkpoint only some seconds after
    # Checkpoint-VM returns. Wait for it, so that the list of old checkpoints
    # is current.
    $new = Checkpoint-VM -Name $VmName -SnapshotName $temporary -Passthru -ErrorAction Stop
    Wait-Until { @(Get-VMCheckpoint -VMName $VmName -ErrorAction Stop | Where-Object Id -eq $new.Id).Count -gt 0 } `
        ([TimeSpan]::FromMinutes(2)) "Hyper-V to list the new checkpoint of '$VmName'" 1

    $old = @(Get-HyperVCheckpoint $VmName $Name | Where-Object Id -ne $new.Id)
    $old | Rename-VMCheckpoint -NewName $previous -ErrorAction Stop
    try {
        $new | Rename-VMCheckpoint -NewName $Name -ErrorAction Stop
    }
    catch {
        $old | Rename-VMCheckpoint -NewName $Name -ErrorAction Continue
        $new | Remove-VMCheckpoint -Confirm:$false -ErrorAction Continue
        throw
    }

    $old | Remove-VMCheckpoint -Confirm:$false -ErrorAction Stop
    return "$((Get-VM -Name $VmName).CheckpointType)"
}

# Get-VMCheckpoint -Name throws a terminating error when no checkpoint matches,
# even with -ErrorAction SilentlyContinue, so filter the list instead.
function Get-HyperVCheckpoint([string] $VmName, [string] $Name) {
    Get-VMCheckpoint -VMName $VmName -ErrorAction Stop | Where-Object Name -eq $Name
}

function Restore-HyperVCheckpoint([string] $VmName, [string] $Name) {
    $checkpoint = Get-HyperVCheckpoint $VmName $Name | Sort-Object CreationTime -Descending | Select-Object -First 1
    if (-not $checkpoint) { throw "The Hyper-V VM '$VmName' has no checkpoint '$Name'. Run Install-WpfQaServer.ps1 again to take it." }
    $checkpoint | Restore-VMCheckpoint -Confirm:$false -ErrorAction Stop
    # A standard checkpoint comes back saved, a production checkpoint powered off.
    Start-HyperVVm $VmName
}

# ---------------------------------------------------------------------------
# SSH

function Get-QaAccountName([pscustomobject] $Info) {
    $domain = Get-QaInfoValue $Info 'domain'
    if ($domain) { return "$domain\$($Info.userName)" }
    return $Info.userName
}

# Local accounts log on over SSH with the bare user name, domain accounts as DOMAIN\user.
function Get-QaSshUser([pscustomobject] $Info) {
    $domain = Get-QaInfoValue $Info 'domain'
    $computerName = Get-QaInfoValue $Info 'computerName'
    if ($domain -and $domain -ne $computerName) { return "$domain\$($Info.userName)" }
    return $Info.userName
}

# -KnownHostOnly accepts only a host key that known_hosts already holds for the
# address, which proves that the address belongs to the same machine.
function Get-SshArguments([pscustomobject] $Info, [switch] $KnownHostOnly) {
    $hostKeyChecking = if ($KnownHostOnly) { 'yes' } else { 'accept-new' }
    @('-i', $Info.sshKeyPath, '-o', "UserKnownHostsFile=$($Info.knownHostsPath)", '-o', "StrictHostKeyChecking=$hostKeyChecking",
        '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', '-o', 'LogLevel=ERROR', '-o', "User=$(Get-QaSshUser $Info)")
}

# Runs a PowerShell command in the guest. The command goes Base64-encoded to
# Windows PowerShell, so it works whatever the default shell of the guest's
# OpenSSH server is, and needs no quoting for cmd.exe.
# -InputText goes to the standard input of the command, for secrets that must
# not appear on a command line.
function Invoke-GuestSsh([pscustomobject] $Info, [string] $Address, [string] $Command, [switch] $KnownHostOnly, [string] $InputText) {
    $script = "`$ProgressPreference = 'SilentlyContinue'; $Command"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $sshArgs = (Get-SshArguments $Info -KnownHostOnly:$KnownHostOnly) + @($Address, "powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded")
    $output = if ($PSBoundParameters.ContainsKey('InputText')) {
        $InputText | & ssh @sshArgs 2>&1 | ForEach-Object { "$_" }
    }
    else {
        & ssh @sshArgs 2>&1 | ForEach-Object { "$_" }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "SSH command failed with exit code $LASTEXITCODE`: $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Test-GuestSsh([pscustomobject] $Info, [string] $Address, [switch] $KnownHostOnly) {
    try {
        $null = Invoke-GuestSsh $Info $Address 'exit 0' -KnownHostOnly:$KnownHostOnly
        return $true
    }
    catch {
        return $false
    }
}

function Copy-ToGuest([pscustomobject] $Info, [string] $Address, [string[]] $Sources, [string] $GuestDirectory) {
    $scpArgs = (Get-SshArguments $Info) + @('-r', '-q') + $Sources + @("$Address`:$($GuestDirectory -replace '\\', '/')")
    $output = & scp @scpArgs 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        throw "Copying to the guest failed with exit code $LASTEXITCODE`: $($output -join ' ')"
    }
}

# Waits until a user desktop (explorer.exe outside session 0) runs in the guest.
function Wait-GuestDesktop([pscustomobject] $Info, [string] $Address, [TimeSpan] $Timeout) {
    Wait-Until {
        try {
            $count = Invoke-GuestSsh $Info $Address '@(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -ne 0).Count'
            [int]($count | Select-Object -Last 1) -gt 0
        }
        catch {
            $false
        }
    } $Timeout "a logged-on desktop in $Address. Log on as $(Get-QaAccountName $Info) in the machine, or turn on automatic logon with Register-WpfQaMachine.ps1 -AutoLogon" 5
}

# Opens a connection to the server. A server that listens only on the guest
# loopback address is reached through an SSH tunnel; stop it with
# Close-QaServerConnection.
function Open-QaServerConnection([pscustomobject] $Info, [string] $Address, [int] $Port, [string] $Listen) {
    if ($Listen -ne 'Loopback') {
        return [pscustomobject]@{ Url = [Uri]"http://$($Address):$Port/mcp"; Tunnel = $null }
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $localPort = $listener.LocalEndpoint.Port
    $listener.Stop()

    $sshArgs = (Get-SshArguments $Info) + @('-N', '-o', 'ExitOnForwardFailure=yes', '-L', "$($localPort):127.0.0.1:$Port", $Address)
    # Start-Process joins an argument array without quotes, so quote it here.
    $tunnel = Start-Process ssh -ArgumentList (ConvertTo-QaCommandLine $sshArgs) -NoNewWindow -PassThru
    $connection = [pscustomobject]@{ Url = [Uri]"http://127.0.0.1:$localPort/mcp"; Tunnel = $tunnel }
    try {
        Wait-Until {
            if ($tunnel.HasExited) { throw "The SSH tunnel to $Address exited with code $($tunnel.ExitCode)." }
            $client = [Net.Sockets.TcpClient]::new()
            try { $client.Connect('127.0.0.1', $localPort); $true } catch { $false } finally { $client.Dispose() }
        } ([TimeSpan]::FromSeconds(30)) "the SSH tunnel to $Address" 1
    }
    catch {
        Close-QaServerConnection $connection
        throw
    }

    return $connection
}

function Close-QaServerConnection($Connection) {
    if ($Connection -and $Connection.Tunnel -and -not $Connection.Tunnel.HasExited) {
        $Connection.Tunnel.Kill()
        $Connection.Tunnel.WaitForExit(5000) | Out-Null
    }
}

# Runs guest/Initialize-QaMachine.ps1 on the machine of a PowerShell session
# (PowerShell Direct for Hyper-V) and returns its result.
function Invoke-QaMachineInitialization(
    [System.Management.Automation.Runspaces.PSSession] $Session,
    [string] $PublicKey,
    [securestring] $AutoLogonPassword,
    [bool] $DisableAutomaticUpdates) {
    $guestDirectory = 'C:\ProgramData\WpfToolsMcp\initialize'
    Invoke-Command -Session $Session -ScriptBlock {
        param($directory)
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    } -ArgumentList $guestDirectory
    foreach ($file in @('QaMachine.ps1', 'Initialize-QaMachine.ps1')) {
        Copy-Item -ToSession $Session -LiteralPath (Join-Path $PSScriptRoot "guest\$file") -Destination "$guestDirectory\$file" -Force
    }

    $output = Invoke-Command -Session $Session -ScriptBlock {
        param($directory, $key, $password, $disableUpdates)
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
        $arguments = @{ AuthorizedKey = $key; DisableAutomaticUpdates = $disableUpdates }
        if ($password) { $arguments.AutoLogonPassword = $password }
        try {
            & (Join-Path $directory 'Initialize-QaMachine.ps1') @arguments
        }
        finally {
            Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
        }
    } -ArgumentList $guestDirectory, $PublicKey, $AutoLogonPassword, $DisableAutomaticUpdates

    return ($output | Select-Object -Last 1) | ConvertFrom-Json
}

# Runs guest/Initialize-QaMachine.ps1 over SSH, on a machine where SSH with the
# key works already. The automatic logon password goes to the standard input.
function Invoke-QaMachineInitializationOverSsh(
    [pscustomobject] $Info,
    [string] $Address,
    [string] $PublicKey,
    [securestring] $AutoLogonPassword,
    [bool] $DisableAutomaticUpdates) {
    $guestDirectory = 'C:\ProgramData\WpfToolsMcp\initialize'
    $null = Invoke-GuestSsh $Info $Address "New-Item -ItemType Directory -Force -Path '$guestDirectory' | Out-Null"
    Copy-ToGuest $Info $Address @((Join-Path $PSScriptRoot 'guest\QaMachine.ps1'), (Join-Path $PSScriptRoot 'guest\Initialize-QaMachine.ps1')) $guestDirectory

    $command = @"
`$ErrorActionPreference = 'Stop'
`$password = [Console]::In.ReadLine()
`$arguments = @{ AuthorizedKey = '$($PublicKey.Replace("'", "''"))'; DisableAutomaticUpdates = `$$DisableAutomaticUpdates }
if (`$password) { `$arguments.AutoLogonPassword = ConvertTo-SecureString `$password -AsPlainText -Force }
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
try { & '$guestDirectory\Initialize-QaMachine.ps1' @arguments }
finally { Remove-Item -LiteralPath '$guestDirectory' -Recurse -Force -ErrorAction SilentlyContinue }
"@
    $password = if ($AutoLogonPassword) { [pscredential]::new('x', $AutoLogonPassword).GetNetworkCredential().Password } else { '' }
    $output = Invoke-GuestSsh $Info $Address $command -InputText $password
    return ($output | Select-Object -Last 1) | ConvertFrom-Json
}

# ---------------------------------------------------------------------------
# Shared folder over SMB (Hyper-V and remote machines)

# Saves a Windows credential for a server in the Credential Manager, as
# "cmdkey /add" does, but without the password on a command line.
function Save-QaWindowsCredential([string] $Target, [string] $UserName, [securestring] $Password) {
    if (-not ('WpfQaCredentialStore' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class WpfQaCredentialStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private const int CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    public static void Write(string target, string userName, IntPtr password, int passwordBytes)
    {
        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_DOMAIN_PASSWORD,
            TargetName = target,
            UserName = userName,
            CredentialBlob = password,
            CredentialBlobSize = passwordBytes,
            Persist = CRED_PERSIST_LOCAL_MACHINE
        };
        if (!CredWrite(ref credential, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
    }

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)
    try {
        [WpfQaCredentialStore]::Write($Target, $UserName, $pointer, $Password.Length * 2)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
    }
}

# Saves the guest credential for the SMB server in the Windows Credential
# Manager, so that the host (and the MCP client) opens \\<address>\<share>
# without a prompt.
function Connect-QaShare([string] $Address, [string] $ShareName, [string] $Account, [pscredential] $Credential) {
    $unc = "\\$Address\$ShareName"
    Save-QaWindowsCredential $Address $Account $Credential.Password

    try {
        Wait-Until { Test-Path -LiteralPath (Join-Path $unc 'artifacts') } ([TimeSpan]::FromMinutes(1)) "the shared folder $unc" 3
    }
    catch {
        throw "The host cannot open $unc. If Windows reports that multiple connections to a server with different user names are not allowed, run 'net use \\$Address /delete' and try again. If a policy blocks NTLM to other machines, use a machine in your domain. ($($_.Exception.Message))"
    }

    return $unc
}

# ---------------------------------------------------------------------------
# MCP server checks

function Get-QaAccessToken([string] $TokenPath) {
    if (-not (Test-Path -LiteralPath $TokenPath)) {
        throw "No access token was found at '$TokenPath'. Run Install-WpfQaServer.ps1 first."
    }

    return (Import-Clixml -LiteralPath $TokenPath).GetNetworkCredential().Password
}

function Invoke-McpRequest([Uri] $Url, [string] $Token, [string] $SessionId, [hashtable] $Message) {
    $headers = @{ Authorization = "Bearer $Token"; Accept = 'application/json, text/event-stream' }
    if ($SessionId) { $headers['Mcp-Session-Id'] = $SessionId }
    $response = Invoke-WebRequest -Method Post -Uri $Url -Headers $headers -ContentType 'application/json' `
        -Body ($Message | ConvertTo-Json -Depth 10 -Compress) -SkipHttpErrorCheck -TimeoutSec 60
    if ($response.StatusCode -ge 300) {
        throw "The MCP server answered $($response.StatusCode): $($response.Content)"
    }

    $body = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { $response.Content }
    $json = if ("$($response.Headers['Content-Type'])" -like 'text/event-stream*') {
        ($body -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1) -replace '^data:\s*', ''
    } else { $body }

    [pscustomobject]@{
        SessionId = "$($response.Headers['Mcp-Session-Id'])"
        Message = if ($json) { $json | ConvertFrom-Json -Depth 20 } else { $null }
    }
}

function Get-QaServerEnvironment([Uri] $Url, [string] $Token) {
    $init = Invoke-McpRequest $Url $Token $null @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'wpf-tools-mcp-qa-scripts'; version = '1' } }
    }
    $null = Invoke-McpRequest $Url $Token $init.SessionId @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $call = Invoke-McpRequest $Url $Token $init.SessionId @{
        jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'get_server_environment'; arguments = @{} }
    }
    if ($call.Message.error) {
        throw "get_server_environment failed: $($call.Message.error.message)"
    }

    return $call.Message.result.structuredContent
}

# Waits until the server answers and reports an available desktop.
function Wait-QaServerReady([Uri] $Url, [string] $Token, [TimeSpan] $Timeout) {
    $state = @{ Environment = $null; Problem = 'no answer yet' }
    try {
        Wait-Until {
            try {
                $environment = Get-QaServerEnvironment $Url $Token
                if ($environment.interactiveDesktop.available) {
                    $state.Environment = $environment
                    return $true
                }

                $state.Problem = "the desktop is not available: $($environment.interactiveDesktop.unavailableReason). $($environment.interactiveDesktop.detail)"
            }
            catch {
                $state.Problem = $_.Exception.Message
            }

            return $false
        } $Timeout "the MCP server at $Url" 3
    }
    catch {
        throw "$($_.Exception.Message) Last result: $($state.Problem)"
    }

    return $state.Environment
}
