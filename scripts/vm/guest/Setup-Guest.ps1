<#
.SYNOPSIS
Configures a fresh Windows guest as an unattended WPF Tools MCP QA machine.

.DESCRIPTION
New-WpfQaVm.ps1 puts this script on the setup CD. Windows Setup runs it once,
elevated, at the first logon of the QA user (FirstLogonCommands). It runs in
Windows PowerShell 5.1.

The script:
- installs VMware Tools from the Tools CD,
- installs the .NET 8 SDK from the setup CD,
- installs and starts OpenSSH Server and authorizes the host key,
- keeps the session logged on, unlocked, and awake, without screen saver,
  notifications, automatic updates, or automatic restarts.

Each step records "ok" or its error in
C:\ProgramData\WpfToolsMcp\vm-setup\result.json. The full log is setup.log in
the same folder. At the end the script shuts the VM down. The host script then
removes the setup media, starts the VM, and reads the result.
#>
$ErrorActionPreference = 'Stop'

$setupRoot = $PSScriptRoot
$stateDir = Join-Path $env:ProgramData 'WpfToolsMcp\vm-setup'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
Start-Transcript -Path (Join-Path $stateDir 'setup.log') -Append | Out-Null

$steps = [ordered]@{}

function Invoke-SetupStep {
    param([string] $Name, [scriptblock] $Action)

    Write-Host "== $Name"
    try {
        & $Action
        $steps[$Name] = 'ok'
    }
    catch {
        $steps[$Name] = "failed: $($_.Exception.Message)"
        Write-Warning "$Name failed: $($_.Exception.Message)"
    }
}

function Invoke-Installer {
    param([string] $Path, [string] $Arguments)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Installer '$Path' was not found."
    }

    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
        throw "$(Split-Path $Path -Leaf) exited with code $($process.ExitCode)."
    }
}

. (Join-Path $PSScriptRoot 'QaMachine.ps1')

try {
    Invoke-SetupStep 'vmware_tools' {
        # The Windows ISO also has setup.exe at its root, so find the Tools CD by
        # its upgrader. Older Tools media name the 64-bit installer setup64.exe.
        $toolsRoot = Get-PSDrive -PSProvider FileSystem |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.Root 'VMwareToolsUpgrader.exe') } |
            Select-Object -First 1 -ExpandProperty Root
        if (-not $toolsRoot) {
            throw 'The VMware Tools CD (VMwareToolsUpgrader.exe) was not found.'
        }

        $installer = @('setup64.exe', 'setup.exe') |
            ForEach-Object { Join-Path $toolsRoot $_ } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if (-not $installer) {
            throw "The VMware Tools CD at $toolsRoot has no setup64.exe or setup.exe."
        }

        # REBOOT=R defers the restart; the shutdown at the end of this script completes it.
        Invoke-Installer $installer '/S /v "/qn REBOOT=R"'
    }

    Invoke-SetupStep 'dotnet_sdk' {
        Invoke-Installer (Join-Path $setupRoot 'dotnet-sdk-win-x64.exe') '/install /quiet /norestart'
    }

    Invoke-SetupStep 'network_private' {
        Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private
    }

    Invoke-SetupStep 'openssh_server' {
        Enable-QaOpenSsh -AuthorizedKeys @(Get-Content -LiteralPath (Join-Path $setupRoot 'authorized_keys'))
        Set-RegistryValue 'HKLM:\SOFTWARE\OpenSSH' 'DefaultShell' `
            "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" 'String'
    }

    Invoke-SetupStep 'unattended_session' {
        # Keep the automatic logon from the answer file after its logon count runs out.
        $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-RegistryValue $winlogon 'AutoAdminLogon' '1' 'String'
        Remove-ItemProperty -LiteralPath $winlogon -Name 'AutoLogonCount' -ErrorAction SilentlyContinue

        Set-QaDesktopSession
        Disable-QaAutomaticUpdates
        Set-QaTimeSource -Hypervisor VMware
    }
}
finally {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    $runtimes = @()
    if (Test-Path -LiteralPath $dotnet) {
        $runtimes = @(& $dotnet --list-runtimes)
    }

    $result = [ordered]@{
        completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        steps = $steps
        dotnetRuntimes = $runtimes
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stateDir 'result.json') -Encoding UTF8

    Stop-Transcript | Out-Null
    shutdown.exe /s /t 15 /c 'WPF Tools MCP QA setup finished. The VM shuts down so the host script can finish.'
}
