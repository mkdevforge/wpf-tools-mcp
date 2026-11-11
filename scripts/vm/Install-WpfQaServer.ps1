#Requires -Version 7.3
<#
.SYNOPSIS
Installs the WPF Tools MCP server on a QA machine so that MCP clients on the host can use it.

.DESCRIPTION
Create the machine first: New-WpfQaVm.ps1 for a VMware VM, or
Register-WpfQaMachine.ps1 for a Hyper-V VM or another Windows machine. This
script:

1. Publishes the server from this repository (Release, self-contained, so
   the machine needs no .NET runtime), or uses -ServerDirectory.
2. Sets up the shared folder. The server writes screenshots, sequences, and
   traces to its "artifacts" subfolder, and reports them under the paths that
   the host sees. Put builds of the application under test in this folder too.
   - VMware: a host folder, shared with the VM as a VMware shared folder.
   - Hyper-V and remote machines: a folder on the machine (-GuestSharedFolder),
     shared over SMB. The host opens it as \\<address>\wpf-qa with the QA
     user's credential, which the script saves in the Credential Manager.
3. Creates an access token, or keeps the existing one.
4. Installs the server on the machine. It starts at the logon of the QA user
   in the desktop session, and listens on http://<address>:<port>/mcp. Clients
   must send the token as a bearer token. With -Listen Loopback, the server
   accepts connections only from the machine itself, and clients connect
   through an SSH tunnel.
5. Checks the server from the host over HTTP.
6. Replaces the snapshot (VMware: "clean") or checkpoint (Hyper-V:
   "wpf-qa-clean"), so that Reset-WpfQaVm.ps1 returns to a running server.
   Remote machines have no snapshot; Reset-WpfQaVm.ps1 restarts them.

Run it again after you change the server; it keeps the token and the shared folder.

.EXAMPLE
./scripts/vm/Install-WpfQaServer.ps1

.EXAMPLE
./scripts/vm/Install-WpfQaServer.ps1 -VmName qa-box -Listen Loopback
#>
[CmdletBinding()]
param(
    [string] $VmName = 'wpf-tools-qa',
    [string] $SharedFolder,
    [string] $GuestSharedFolder = 'C:\WpfQa\shared',
    [ValidateRange(1024, 65535)] [int] $Port = 5765,
    [ValidateSet('core', 'diagnostics')] [string] $ToolProfile = 'core',
    [ValidateSet('Network', 'Loopback')] [string] $Listen = 'Network',
    [string] $ServerDirectory,
    [switch] $NewToken,
    [switch] $NoSnapshot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
. (Join-Path $PSScriptRoot 'WpfQaVm.Common.ps1')

$shareName = 'wpf-qa'
$stateDir = Get-QaVmStateDirectory $VmName
$credentialPath = Join-Path $stateDir 'guest-credential.xml'
$tokenPath = Join-Path $stateDir 'http-token.xml'
$guestStaging = 'C:\ProgramData\WpfToolsMcp\server-staging'

function Publish-Server {
    $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $project = Join-Path $repoRoot 'src\WpfToolsMcp.McpServer\WpfToolsMcp.McpServer.csproj'
    $output = Join-Path ([IO.Path]::GetTempPath()) "wpf-qa-server-$([guid]::NewGuid().ToString('N'))"
    Write-Step 'Publishing the server (Release, self-contained)'
    $publish = & dotnet publish $project -c Release -r win-x64 --self-contained -o $output -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $($publish -join [Environment]::NewLine)"
    }

    return $output
}

function Get-OrCreateAccessToken {
    if (-not $NewToken -and (Test-Path -LiteralPath $tokenPath)) {
        return Get-QaAccessToken $tokenPath
    }

    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [pscredential]::new('mcp', (ConvertTo-SecureString $token -AsPlainText -Force)) | Export-Clixml -LiteralPath $tokenPath
    return $token
}

function Set-VMwareSharedFolder([string] $Vmx, [string] $HostPath) {
    New-Item -ItemType Directory -Force -Path (Join-Path $HostPath 'artifacts'), (Join-Path $HostPath 'apps') | Out-Null
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'enableSharedFolders', $Vmx)
    $added = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'addSharedFolder', $Vmx, $shareName, $HostPath) -AllowFailure
    if ($added.ExitCode -ne 0) {
        # The share exists already; point it at the requested folder.
        $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'setSharedFolderState', $Vmx, $shareName, $HostPath, 'writable')
    }
}

# ---------------------------------------------------------------------------

$info = Read-QaMachineInfo $VmName
$kind = $info.kind
$credential = Import-Clixml -LiteralPath $credentialPath

# Start the machine and find its address.
switch ($kind) {
    'vmware' {
        $vmware = Find-VMware
        if (-not (Test-VmRunning $info.vmx)) {
            Write-Step 'Starting the VM'
            $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'start', $info.vmx, 'nogui')
        }

        Write-Step 'Waiting for the QA user desktop'
        Wait-Until {
            $processes = Invoke-GuestVmRun $info.vmx $credential @('listProcessesInGuest', $info.vmx) -AllowFailure
            $processes.ExitCode -eq 0 -and ($processes.Output -match 'explorer\.exe')
        } ([TimeSpan]::FromMinutes(10)) 'VMware Tools and the QA user desktop in the guest' 5
        $address = Get-GuestIpAddress $info.vmx -Info $info
    }
    'hyperv' {
        Assert-HyperVModule
        Write-Step "Starting the Hyper-V VM '$($info.hyperVVmName)'"
        Start-HyperVVm $info.hyperVVmName
        $address = Get-HyperVGuestAddress $info
        Write-Step "Waiting for the QA user desktop on $address"
        Wait-GuestDesktop $info $address ([TimeSpan]::FromMinutes(10))
    }
    'remote' {
        $address = $info.fixedAddress
        Write-Step "Waiting for the QA user desktop on $address"
        Wait-GuestDesktop $info $address ([TimeSpan]::FromMinutes(10))
    }
    default { throw "Unknown QA machine kind '$kind' in vm.json." }
}

# Where the server and the host see the shared folder.
if ($kind -eq 'vmware') {
    if (-not $SharedFolder) {
        $SharedFolder = (Get-QaInfoValue $info 'sharedFolder') ?? (Join-Path $stateDir 'shared')
    }
    # Without a trailing backslash, which would escape the closing quote in the launcher.
    $clientSharedPath = [IO.Path]::GetFullPath($SharedFolder).TrimEnd('\')
    $serverSharedPath = "\\vmware-host\Shared Folders\$shareName"
    $smbShareName = $null
}
else {
    if ($SharedFolder) { throw '-SharedFolder applies to VMware VMs only. Use -GuestSharedFolder for a folder on the machine.' }
    if (-not [IO.Path]::IsPathFullyQualified($GuestSharedFolder)) { throw "-GuestSharedFolder '$GuestSharedFolder' must be an absolute path on the machine." }
    $clientSharedPath = "\\$address\$shareName"
    $serverSharedPath = $GuestSharedFolder.TrimEnd('\')
    $smbShareName = $shareName
}

$published = $null
if (-not $ServerDirectory) {
    $ServerDirectory = $published = Publish-Server
}

if (-not (Test-Path -LiteralPath (Join-Path $ServerDirectory 'snoop\Snoop.InjectorLauncher.x64.exe'))) {
    throw "The server in '$ServerDirectory' has no Snoop injector, so WPF inspection would fail. Run scripts/build-snoop.ps1 -Configuration Release first."
}

if ($kind -eq 'vmware') {
    Write-Step "Sharing $clientSharedPath with the VM as '$shareName'"
    Set-VMwareSharedFolder $info.vmx $clientSharedPath
}

$token = Get-OrCreateAccessToken
$localStaging = Join-Path ([IO.Path]::GetTempPath()) "wpf-qa-server-staging-$([guid]::NewGuid().ToString('N'))"
try {
    Write-Step "Copying the server to $address"
    New-Item -ItemType Directory -Force -Path $localStaging | Out-Null
    Copy-Item -LiteralPath $ServerDirectory -Destination (Join-Path $localStaging 'app') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'guest\Install-Server.ps1') -Destination $localStaging
    Set-Content -LiteralPath (Join-Path $localStaging 'http-token.txt') -Value $token -Encoding ascii -NoNewline
    [ordered]@{
        userAccount = if (Get-QaInfoValue $info 'domain') { Get-QaAccountName $info } else { $info.userName }
        port = $Port
        toolProfile = $ToolProfile
        listenAddress = if ($Listen -eq 'Loopback') { '127.0.0.1' } else { '0.0.0.0' }
        serverSharedPath = $serverSharedPath
        clientSharedPath = $clientSharedPath
        smbShareName = $smbShareName
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localStaging 'config.json') -Encoding utf8NoBOM

    $null = Invoke-GuestSsh $info $address "Remove-Item -LiteralPath '$guestStaging' -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path '$guestStaging' | Out-Null; icacls.exe '$guestStaging' /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; if (`$LASTEXITCODE) { throw 'icacls failed on the staging folder.' }"
    Copy-ToGuest $info $address @(Get-ChildItem -LiteralPath $localStaging | ForEach-Object FullName) $guestStaging
}
finally {
    Remove-Item -LiteralPath $localStaging -Recurse -Force -ErrorAction SilentlyContinue
    if ($published) { Remove-Item -LiteralPath $published -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Step 'Installing the server and starting it in the desktop session'
$installOutput = Invoke-GuestSsh $info $address "powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$guestStaging\Install-Server.ps1' -ConfigPath '$guestStaging\config.json'"
$install = ($installOutput | Select-Object -Last 1) | ConvertFrom-Json
if (-not $install.listening) {
    throw "The server did not start listening on port $Port within $($install.startSeconds) seconds. Server log:$([Environment]::NewLine)$($install.logTail -join [Environment]::NewLine)"
}
Write-Step "The server listens after $($install.startSeconds) seconds"

if ($install.sessionId -eq 0) {
    throw 'The server runs in session 0, where it cannot use the desktop. Check that the QA user is logged on.'
}
if (-not $install.shareAvailable) {
    Write-Warning "The server cannot see $serverSharedPath yet. Writing artifacts fails until the share is available."
}

if ($smbShareName) {
    Write-Step "Opening $clientSharedPath from the host"
    $null = Connect-QaShare $address $shareName (Get-QaAccountName $info) $credential
}

$connection = Open-QaServerConnection $info $address $Port $Listen
try {
    Write-Step "Checking the server from the host at $($connection.Url)"
    $environment = Get-QaServerEnvironment $connection.Url $token
}
finally {
    Close-QaServerConnection $connection
}
if (-not $environment.interactiveDesktop.available) {
    throw "The server reports that the desktop is not available: $($environment.interactiveDesktop.unavailableReason). $($environment.interactiveDesktop.detail)"
}

$snapshot = Get-QaInfoValue $info 'snapshot'
$snapshotNote = $null
if (-not $NoSnapshot) {
    switch ($kind) {
        'vmware' {
            Write-Step 'Replacing the snapshot "clean"'
            $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'deleteSnapshot', $info.vmx, 'clean') -AllowFailure
            $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'snapshot', $info.vmx, 'clean')
            $snapshot = 'clean'
        }
        'hyperv' {
            Write-Step 'Replacing the checkpoint "wpf-qa-clean"'
            try {
                $checkpointType = Save-HyperVCheckpoint $info.hyperVVmName 'wpf-qa-clean'
                $snapshot = 'wpf-qa-clean'
                if ($checkpointType -ne 'Standard') {
                    $snapshotNote = "The VM uses $checkpointType checkpoints, which do not keep the running desktop. After a reset, the VM starts from the checkpoint and the server starts at the automatic logon. Set-VM -CheckpointType Standard keeps the desktop."
                }
            }
            catch {
                $failure = $_.Exception.Message
                if (Get-HyperVCheckpoint $info.hyperVVmName 'wpf-qa-clean') {
                    $snapshot = 'wpf-qa-clean'
                    $snapshotNote = "Hyper-V could not replace the checkpoint 'wpf-qa-clean': $failure The previous checkpoint is kept. It has the previously installed server."
                }
                else {
                    $snapshot = $null
                    $snapshotNote = "Hyper-V did not take a checkpoint: $failure Hyper-V refuses checkpoints for some VMs with an assigned GPU. Reset-WpfQaVm.ps1 restarts the VM instead."
                }
            }
        }
    }
}
if ($snapshotNote) { Write-Warning $snapshotNote }

Set-QaInfoValue $info 'address' $address
Set-QaInfoValue $info 'sharedFolder' $clientSharedPath
Set-QaInfoValue $info 'port' $Port
Set-QaInfoValue $info 'listen' $Listen
Set-QaInfoValue $info 'snapshot' $snapshot
$clientUrl = if ($Listen -eq 'Loopback') { "http://127.0.0.1:$Port/mcp" } else { "http://$($address):$Port/mcp" }
Set-QaInfoValue $info 'serverUrl' $clientUrl
Save-QaMachineInfo $info

$clientConfig = [ordered]@{
    mcpServers = [ordered]@{
        $VmName = [ordered]@{
            type = 'http'
            url = $clientUrl
            headers = [ordered]@{ Authorization = "Bearer $token" }
        }
    }
}
$clientConfigPath = Join-Path $stateDir 'mcp-client.json'
$clientConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $clientConfigPath -Encoding utf8NoBOM

Write-Step 'The MCP server on the QA machine is ready.'
Write-Host ''
Write-Host "  URL:          $clientUrl"
if ($Listen -eq 'Loopback') {
    $tunnelArgs = (Get-SshArguments $info) + @('-N', '-L', "$($Port):127.0.0.1:$Port", $address)
    Write-Host "  Tunnel:       ssh $(ConvertTo-QaCommandLine $tunnelArgs)"
    Write-Host '                (keep it running while the MCP client uses the server)'
}
Write-Host "  Session:      $($environment.interactiveDesktop.sessionId) ($($environment.interactiveDesktop.inputDesktop)), WPF render tier $($environment.rendering.wpfRenderTier)"
Write-Host "  Shared:       $clientSharedPath (artifacts in $(Join-Path $clientSharedPath 'artifacts'))"
Write-Host "  Reset:        $(if ($snapshot) { "./scripts/vm/Reset-WpfQaVm.ps1 -VmName $VmName (reverts to '$snapshot')" } else { "./scripts/vm/Reset-WpfQaVm.ps1 -VmName $VmName (restarts the machine)" })"
Write-Host "  Client JSON:  $clientConfigPath (contains the token)"
Write-Host "  Claude Code:  claude mcp add --transport http $VmName $clientUrl --header `"Authorization: Bearer $token`""
