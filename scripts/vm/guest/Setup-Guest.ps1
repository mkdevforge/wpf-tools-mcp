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

function Invoke-Native {
    param([string] $FilePath, [string[]] $Arguments)

    & $FilePath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') exited with code $LASTEXITCODE."
    }
}

function Set-RegistryValue {
    param([string] $Path, [string] $Name, $Value, [string] $Type = 'DWord')

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }

    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

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
        $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
        if (-not $capability) {
            throw 'The OpenSSH.Server capability is not available on this Windows image.'
        }

        if ($capability.State -ne 'Installed') {
            Add-WindowsCapability -Online -Name $capability.Name | Out-Null
        }

        Set-Service -Name sshd -StartupType Automatic
        Start-Service -Name sshd

        if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
                -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
        }

        # sshd reads keys for administrators only from this file, and only if
        # Administrators and SYSTEM are its sole owners.
        $keys = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
        Copy-Item -LiteralPath (Join-Path $setupRoot 'authorized_keys') -Destination $keys -Force
        Invoke-Native icacls.exe @($keys, '/inheritance:r', '/grant', '*S-1-5-32-544:F', '/grant', '*S-1-5-18:F')

        Set-RegistryValue 'HKLM:\SOFTWARE\OpenSSH' 'DefaultShell' `
            "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" 'String'
    }

    Invoke-SetupStep 'unattended_session' {
        # Keep automatic logon after the unattend logon count runs out.
        $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-RegistryValue $winlogon 'AutoAdminLogon' '1' 'String'
        Remove-ItemProperty -LiteralPath $winlogon -Name 'AutoLogonCount' -ErrorAction SilentlyContinue

        # Never sleep, turn off the display, or lock when the display turns off.
        Invoke-Native powercfg.exe @('/change', 'monitor-timeout-ac', '0')
        Invoke-Native powercfg.exe @('/change', 'standby-timeout-ac', '0')
        Invoke-Native powercfg.exe @('/change', 'hibernate-timeout-ac', '0')
        Invoke-Native powercfg.exe @('/hibernate', 'off')
        Invoke-Native powercfg.exe @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_NONE', 'CONSOLELOCK', '0')
        Invoke-Native powercfg.exe @('/setactive', 'SCHEME_CURRENT')

        # No screen saver and no idle lock.
        Set-RegistryValue 'HKCU:\Control Panel\Desktop' 'ScreenSaveActive' '0' 'String'
        Set-RegistryValue 'HKCU:\Control Panel\Desktop' 'ScreenSaverIsSecure' '0' 'String'
        Set-RegistryValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'InactivityTimeoutSecs' 0
        Set-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization' 'NoLockScreen' 1

        # Toasts can cover the application under test.
        Set-RegistryValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' 'ToastEnabled' 0

        # No automatic updates. Windows Update restarts the VM outside active
        # hours even with a logged-on user, and an update applied this way left
        # the VM unable to boot. Create a new VM from a current ISO instead.
        Set-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoUpdate' 1
        Set-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoRebootWithLoggedOnUsers' 1

        # VMware Tools keeps the clock on host time (tools.syncTime). After a
        # snapshot revert, the Windows Time service set the clock back to the
        # snapshot time about a minute after VMware Tools had corrected it. Use
        # only VMware Tools as the time source.
        Set-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config' 'UtilizeSslTimeData' 0
        Stop-Service -Name W32Time -ErrorAction SilentlyContinue
        Set-Service -Name W32Time -StartupType Disabled
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
