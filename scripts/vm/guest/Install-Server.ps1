<#
.SYNOPSIS
Installs the WPF Tools MCP server in the QA VM and starts it in the desktop session.

.DESCRIPTION
Install-WpfQaServer.ps1 on the host copies this script, the published server,
the access token, and config.json to the staging folder, and runs this script
over SSH. It runs in Windows PowerShell 5.1 as the QA user, who is an
administrator.

config.json holds:
- userAccount: the QA user (COMPUTER\user or DOMAIN\user),
- port, toolProfile, and listenAddress (0.0.0.0, or 127.0.0.1 when clients
  connect through an SSH tunnel),
- serverSharedPath and clientSharedPath: the shared folder as this machine and
  as the MCP client see it,
- smbShareName: if set, this script shares serverSharedPath under this name.

Tools that send input or capture the screen need the interactive desktop.
Processes started over SSH run in session 0, so a scheduled task starts the
server at the logon of the QA user, in that user's desktop session. The task
runs the server through a headless console host, so that no console window
covers the application under test.

The script writes a JSON result to standard output.
#>
param([Parameter(Mandatory)] [string] $ConfigPath)

$ErrorActionPreference = 'Stop'

# The host writes config.json as UTF-8 without a byte order mark.
$config = Get-Content -Raw -Encoding UTF8 -LiteralPath $ConfigPath | ConvertFrom-Json
$staging = Split-Path -Parent $ConfigPath
$root = Join-Path $env:ProgramData 'WpfToolsMcp\server'
$app = Join-Path $root 'app'
$logs = Join-Path $root 'logs'
$tokenPath = Join-Path $root 'http-token.txt'
$launcher = Join-Path $root 'start-server.cmd'
$exe = Join-Path $app 'WpfToolsMcp.McpServer.exe'
$taskPath = '\WpfToolsMcp\'
$taskName = 'MCP server'
$share = $config.serverSharedPath

foreach ($value in @($config.clientSharedPath, $config.serverSharedPath, $config.userAccount)) {
    if ($value -match '[%^&|<>"]') {
        throw "'$value' contains a character that the server launcher cannot quote: % ^ & | < > `""
    }
}

# Escapes every character outside ASCII as \uXXXX. Windows PowerShell writes
# to SSH in the OEM code page, which changes or drops such characters, and can
# map some of them to a quote that breaks the JSON.
function ConvertTo-AsciiJson {
    param([Parameter(ValueFromPipeline)] [string] $Json)
    process {
        [regex]::Replace($Json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
    }
}

function Stop-Server {
    if (Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskPath $taskPath -TaskName $taskName
    }

    # Stop the launcher loop first, so that it does not start the server again.
    Get-CimInstance Win32_Process -Filter "Name = 'cmd.exe'" |
        Where-Object { $_.CommandLine -like '*start-server.cmd*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    # Other copies of the server on this machine, such as stdio servers of a
    # local MCP client, keep running.
    $running = @(Get-Process -Name 'WpfToolsMcp.McpServer' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith("$app\", [StringComparison]::OrdinalIgnoreCase) })
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
}

Stop-Server

# Server files.
New-Item -ItemType Directory -Force -Path $root, $logs | Out-Null
if (Test-Path -LiteralPath $app) {
    Remove-Item -LiteralPath $app -Recurse -Force
}
Move-Item -LiteralPath (Join-Path $staging 'app') -Destination $app
# The files keep the ACL of the staging folder, which only SYSTEM and
# administrators can read. The server runs without administrator rights, so
# give the files the permissions of their new folder.
& icacls.exe $app /reset /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls /reset failed on $app with exit code $LASTEXITCODE." }

# Access token: readable only by the QA user, administrators, and SYSTEM.
Move-Item -LiteralPath (Join-Path $staging 'http-token.txt') -Destination $tokenPath -Force
& icacls.exe $tokenPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "$($config.userAccount):R" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed with exit code $LASTEXITCODE." }

# The launcher keeps the previous log and appends server output to the current one.
$arguments = @(
    '--transport http',
    "--http-url http://$($config.listenAddress):$($config.port)/mcp",
    "--http-token-file `"$tokenPath`"",
    "--tool-profile $($config.toolProfile)",
    "--artifact-dir `"$share\artifacts`"",
    "--path-map `"$share=$($config.clientSharedPath)`""
) -join ' '
# The launcher restarts the server whenever it exits. The scheduled task cannot
# do this: it sees only the exit code of the console host, which is always 0.
# Each start moves the log of the previous run to server.previous.log.
$log = Join-Path $logs 'server.log'
$launcherText = @(
    '@echo off',
    ':run',
    "if exist `"$log`" move /y `"$log`" `"$(Join-Path $logs 'server.previous.log')`" >nul",
    "`"$exe`" $arguments >> `"$log`" 2>&1",
    "echo The server exited with code %ERRORLEVEL%. Restarting in 10 seconds. >> `"$log`"",
    'timeout /t 10 /nobreak >nul',
    'goto run',
    ''
) -join "`r`n"

# cmd.exe reads a batch file in the OEM code page. A path with characters that
# this code page cannot represent would reach the server changed.
$oemCodePage = [int](Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').OEMCP
$oem = [Text.Encoding]::GetEncoding($oemCodePage)
if ($oem.GetString($oem.GetBytes($launcherText)) -cne $launcherText) {
    throw "The server command line has characters that code page $oemCodePage cannot represent: $($config.serverSharedPath), $($config.clientSharedPath). Use paths without these characters."
}
[IO.File]::WriteAllText($launcher, $launcherText, $oem)

# The firewall rules allow only the local subnet (the host-only or NAT network
# of a local VM) and the host that runs this installation over SSH.
$allowedRemote = @('LocalSubnet')
$sshClient = "$env:SSH_CLIENT".Split(' ')[0]
if ($sshClient) { $allowedRemote += $sshClient }

# Allow MCP clients in, unless they connect through an SSH tunnel. Never show
# the firewall prompt on the QA desktop.
$ruleName = 'WpfToolsMcp-Server-In-TCP'
Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($config.listenAddress -ne '127.0.0.1') {
    New-NetFirewallRule -Name $ruleName -DisplayName 'WPF Tools MCP server (HTTP)' -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $config.port -Program $exe -Profile Any -RemoteAddress $allowedRemote | Out-Null
}
Set-NetFirewallProfile -All -NotifyOnListen False

# Share the folder with the MCP client over SMB.
if ($config.smbShareName) {
    New-Item -ItemType Directory -Force -Path (Join-Path $share 'artifacts'), (Join-Path $share 'apps') | Out-Null
    & icacls.exe $share /grant "$($config.userAccount):(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed with exit code $LASTEXITCODE." }

    $existing = Get-SmbShare -Name $config.smbShareName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Path -ne $share) {
        Remove-SmbShare -Name $config.smbShareName -Force
        $existing = $null
    }
    if (-not $existing) {
        New-SmbShare -Name $config.smbShareName -Path $share -FullAccess $config.userAccount -CachingMode None | Out-Null
    }

    $smbRule = 'WpfToolsMcp-SMB-In-TCP'
    Get-NetFirewallRule -Name $smbRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $smbRule -DisplayName 'WPF Tools MCP shared folder (SMB)' -Direction Inbound `
        -Action Allow -Protocol TCP -LocalPort 445 -Profile Any -RemoteAddress $allowedRemote | Out-Null
}

# Start at logon in the QA user's desktop session, without a console window.
# The task has no time limit. Stopping the task stops the launcher and the server.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument "--headless `"$env:SystemRoot\System32\cmd.exe`" /d /c `"$launcher`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $config.userAccount
$principal = New-ScheduledTaskPrincipal -UserId $config.userAccount -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName

# Wait until the server listens. Right after a snapshot revert, VMware loads
# guest memory on demand, and the first start of the copied server can take
# more than a minute.
$started = [Diagnostics.Stopwatch]::StartNew()
$listener = $null
while (-not $listener -and $started.Elapsed.TotalSeconds -lt 300) {
    Start-Sleep -Milliseconds 500
    $listener = Get-NetTCPConnection -State Listen -LocalPort $config.port -ErrorAction SilentlyContinue | Select-Object -First 1
}

$process = if ($listener) { Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $staging -Recurse -Force

[ordered]@{
    listening = [bool]$listener
    startSeconds = [int]$started.Elapsed.TotalSeconds
    processName = if ($process) { $process.ProcessName } else { $null }
    sessionId = if ($process) { $process.SessionId } else { $null }
    shareAvailable = Test-Path -LiteralPath $share
    # The server writes its console output in the OEM code page.
    logTail = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log -Encoding Oem -Tail 20) } else { @() }
} | ConvertTo-Json -Compress | ConvertTo-AsciiJson
