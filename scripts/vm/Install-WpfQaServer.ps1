#Requires -Version 7.2
<#
.SYNOPSIS
Installs the WPF Tools MCP server in the QA VM so that MCP clients on the host can use it.

.DESCRIPTION
Run New-WpfQaVm.ps1 first. This script:

1. Publishes the server from this repository (Release), or uses -ServerDirectory.
2. Shares a host folder with the VM. The server writes screenshots, sequences,
   and traces to its "artifacts" subfolder, and reports them under their host
   paths. Put builds of the application under test in this folder too.
3. Creates an access token, or keeps the existing one.
4. Installs the server in the VM. It starts at the logon of the QA user in the
   desktop session, and listens on http://<vm-ip>:<port>/mcp. Clients must send
   the token as a bearer token.
5. Checks the server from the host over HTTP.
6. Replaces the snapshot "clean", so that a revert returns to a running server.

Run it again after you change the server; it keeps the token and the shared folder.

.EXAMPLE
./scripts/vm/Install-WpfQaServer.ps1
#>
[CmdletBinding()]
param(
    [string] $VmName = 'wpf-tools-qa',
    [string] $SharedFolder,
    [ValidateRange(1024, 65535)] [int] $Port = 5765,
    [ValidateSet('core', 'diagnostics')] [string] $ToolProfile = 'core',
    [string] $ServerDirectory,
    [switch] $NewToken,
    [switch] $NoSnapshot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
. (Join-Path $PSScriptRoot 'WpfQaVm.Common.ps1')

$shareName = 'wpf-qa'
$stateDir = Get-QaVmStateDirectory $VmName
$infoPath = Join-Path $stateDir 'vm.json'
$credentialPath = Join-Path $stateDir 'guest-credential.xml'
$tokenPath = Join-Path $stateDir 'http-token.xml'
$guestStaging = 'C:\ProgramData\WpfToolsMcp\server-staging'

function Publish-Server {
    $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $project = Join-Path $repoRoot 'src\WpfToolsMcp.McpServer\WpfToolsMcp.McpServer.csproj'
    $output = Join-Path ([IO.Path]::GetTempPath()) "wpf-qa-server-$([guid]::NewGuid().ToString('N'))"
    Write-Step 'Publishing the server (Release)'
    $publish = & dotnet publish $project -c Release -o $output -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $($publish -join [Environment]::NewLine)"
    }

    return $output
}

function Get-AccessToken {
    if (-not $NewToken -and (Test-Path -LiteralPath $tokenPath)) {
        return (Import-Clixml -LiteralPath $tokenPath).GetNetworkCredential().Password
    }

    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [pscredential]::new('mcp', (ConvertTo-SecureString $token -AsPlainText -Force)) | Export-Clixml -LiteralPath $tokenPath
    return $token
}

function Set-SharedFolder([string] $Vmx, [string] $HostPath) {
    New-Item -ItemType Directory -Force -Path (Join-Path $HostPath 'artifacts') | Out-Null
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'enableSharedFolders', $Vmx)
    $added = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'addSharedFolder', $Vmx, $shareName, $HostPath) -AllowFailure
    if ($added.ExitCode -ne 0) {
        # The share exists already; point it at the requested folder.
        $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'setSharedFolderState', $Vmx, $shareName, $HostPath, 'writable')
    }
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

function Test-ServerFromHost([Uri] $Url, [string] $Token) {
    $init = Invoke-McpRequest $Url $Token $null @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'Install-WpfQaServer'; version = '1' } }
    }
    $null = Invoke-McpRequest $Url $Token $init.SessionId @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $call = Invoke-McpRequest $Url $Token $init.SessionId @{
        jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'get_server_environment'; arguments = @{} }
    }
    return $call.Message.result.structuredContent
}

# ---------------------------------------------------------------------------

$vmware = Find-VMware
if (-not (Test-Path -LiteralPath $infoPath)) { throw "No QA VM named '$VmName' was found. Run New-WpfQaVm.ps1 first." }
$info = Get-Content -Raw -LiteralPath $infoPath | ConvertFrom-Json
$vmx = $info.vmx
$credential = Import-Clixml -LiteralPath $credentialPath
if (-not $SharedFolder) {
    $SharedFolder = if ($info.PSObject.Properties['sharedFolder']) { $info.sharedFolder } else { Join-Path $stateDir 'shared' }
}
$SharedFolder = [IO.Path]::GetFullPath($SharedFolder)

if (-not (Test-VmRunning $vmx)) {
    Write-Step 'Starting the VM'
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'start', $vmx, 'nogui')
}

Write-Step 'Waiting for the QA user desktop'
Wait-Until {
    $processes = Invoke-GuestVmRun $vmx $credential @('listProcessesInGuest', $vmx) -AllowFailure
    $processes.ExitCode -eq 0 -and ($processes.Output -match 'explorer\.exe')
} ([TimeSpan]::FromMinutes(10)) 'VMware Tools and the QA user desktop in the guest' 5
$ip = Get-GuestIpAddress $vmx

$published = $null
if (-not $ServerDirectory) {
    $ServerDirectory = $published = Publish-Server
}

if (-not (Test-Path -LiteralPath (Join-Path $ServerDirectory 'snoop\Snoop.InjectorLauncher.x64.exe'))) {
    throw "The server in '$ServerDirectory' has no Snoop injector, so WPF inspection would fail. Run scripts/build-snoop.ps1 -Configuration Release first."
}

Write-Step "Sharing $SharedFolder with the VM as '$shareName'"
Set-SharedFolder $vmx $SharedFolder

$token = Get-AccessToken
$localStaging = Join-Path ([IO.Path]::GetTempPath()) "wpf-qa-server-staging-$([guid]::NewGuid().ToString('N'))"
try {
    Write-Step "Copying the server to the VM ($ip)"
    New-Item -ItemType Directory -Force -Path $localStaging | Out-Null
    Copy-Item -LiteralPath $ServerDirectory -Destination (Join-Path $localStaging 'app') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'guest\Install-Server.ps1') -Destination $localStaging
    Set-Content -LiteralPath (Join-Path $localStaging 'http-token.txt') -Value $token -Encoding ascii -NoNewline
    [ordered]@{
        userName = $info.userName
        port = $Port
        toolProfile = $ToolProfile
        shareName = $shareName
        clientSharedPath = $SharedFolder
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localStaging 'config.json') -Encoding utf8NoBOM

    $null = Invoke-GuestSsh $info $ip "Remove-Item -LiteralPath '$guestStaging' -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path '$guestStaging' | Out-Null"
    Copy-ToGuest $info $ip @(Get-ChildItem -LiteralPath $localStaging | ForEach-Object FullName) $guestStaging
}
finally {
    Remove-Item -LiteralPath $localStaging -Recurse -Force -ErrorAction SilentlyContinue
    if ($published) { Remove-Item -LiteralPath $published -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Step 'Installing the server and starting it in the desktop session'
$installOutput = Invoke-GuestSsh $info $ip "powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$guestStaging\Install-Server.ps1' -ConfigPath '$guestStaging\config.json'"
$install = ($installOutput | Select-Object -Last 1) | ConvertFrom-Json
if (-not $install.listening) {
    throw "The server did not start listening on port $Port within $($install.startSeconds) seconds. Server log:$([Environment]::NewLine)$($install.logTail -join [Environment]::NewLine)"
}
Write-Step "The server listens after $($install.startSeconds) seconds"

if ($install.sessionId -eq 0) {
    throw 'The server runs in session 0, where it cannot use the desktop. Check that the QA user is logged on.'
}
if (-not $install.shareAvailable) {
    Write-Warning "The guest cannot see \\vmware-host\Shared Folders\$shareName yet. Artifacts fail until VMware Tools shows the share."
}

$url = [Uri]"http://$($ip):$Port/mcp"
Write-Step "Checking the server from the host at $url"
$environment = Test-ServerFromHost $url $token
if (-not $environment.interactiveDesktop.available) {
    throw "The server reports that the desktop is not available: $($environment.interactiveDesktop.unavailableReason). $($environment.interactiveDesktop.detail)"
}

if (-not $NoSnapshot) {
    Write-Step 'Replacing the snapshot "clean"'
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'deleteSnapshot', $vmx, 'clean') -AllowFailure
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'snapshot', $vmx, 'clean')
}

$info | Add-Member -NotePropertyName ipAddress -NotePropertyValue $ip -Force
$info | Add-Member -NotePropertyName sharedFolder -NotePropertyValue $SharedFolder -Force
$info | Add-Member -NotePropertyName serverUrl -NotePropertyValue $url.ToString() -Force
$info | ConvertTo-Json | Set-Content -LiteralPath $infoPath -Encoding utf8NoBOM

$clientConfig = [ordered]@{
    mcpServers = [ordered]@{
        $VmName = [ordered]@{
            type = 'http'
            url = $url.ToString()
            headers = [ordered]@{ Authorization = "Bearer $token" }
        }
    }
}
$clientConfigPath = Join-Path $stateDir 'mcp-client.json'
$clientConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $clientConfigPath -Encoding utf8NoBOM

Write-Step 'The MCP server in the QA VM is ready.'
Write-Host ''
Write-Host "  URL:          $url"
Write-Host "  Session:      $($environment.interactiveDesktop.sessionId) ($($environment.interactiveDesktop.inputDesktop)), WPF render tier $($environment.rendering.wpfRenderTier)"
Write-Host "  Shared:       $SharedFolder (artifacts in $(Join-Path $SharedFolder 'artifacts'))"
Write-Host "  Client JSON:  $clientConfigPath (contains the token)"
Write-Host "  Claude Code:  claude mcp add --transport http $VmName $url --header `"Authorization: Bearer $token`""
