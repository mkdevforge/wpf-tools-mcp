# Settings that make a Windows machine usable for unattended QA runs with the
# WPF Tools MCP server. Setup-Guest.ps1 (new VMware VMs) and
# Initialize-QaMachine.ps1 (existing machines) dot-source this file. It runs in
# Windows PowerShell 5.1, elevated, as the QA user: the HKCU settings apply to
# the user who runs it.

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

# Escapes every character outside ASCII as \uXXXX. Windows PowerShell writes
# to SSH in the OEM code page, which changes or drops such characters, and can
# map some of them to a quote that breaks the JSON.
function ConvertTo-AsciiJson {
    param([Parameter(ValueFromPipeline)] [string] $Json)
    process {
        [regex]::Replace($Json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
    }
}

function Test-QaAdministrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Keeps the desktop of the current user awake and unlocked, without screen saver
# or notifications that could cover the application under test.
function Set-QaDesktopSession {
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
}

# Windows Update restarts the machine outside active hours even with a
# logged-on user. On the VMware QA VM, an update applied this way left the VM
# unable to boot.
function Disable-QaAutomaticUpdates {
    Set-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoUpdate' 1
    Set-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoRebootWithLoggedOnUsers' 1
}

# Sets the clock source of a VM so that a snapshot revert does not leave a
# wrong time.
function Set-QaTimeSource {
    param([ValidateSet('VMware', 'HyperV')] [string] $Hypervisor)

    # Secure Time Seeding can set the clock from stale data after a revert.
    Set-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config' 'UtilizeSslTimeData' 0

    if ($Hypervisor -eq 'VMware') {
        # VMware Tools keeps the clock on host time (tools.syncTime). After a
        # snapshot revert, the Windows Time service set the clock back to the
        # snapshot time about a minute after VMware Tools had corrected it.
        # Use only VMware Tools as the time source.
        Stop-Service -Name W32Time -ErrorAction SilentlyContinue
        Set-Service -Name W32Time -StartupType Disabled
        return
    }

    # Hyper-V time synchronization runs through the Windows Time service
    # (VMICTimeProvider), so the service must keep running. After a restore,
    # the integration service sets the clock itself.
    Restart-Service -Name W32Time -ErrorAction SilentlyContinue
}

# Installs OpenSSH Server and authorizes the given public keys for
# administrators. Keys that are already authorized are kept.
function Enable-QaOpenSsh {
    param([string[]] $AuthorizedKeys)

    # Use an OpenSSH Server that is already installed, also one from the
    # Win32-OpenSSH installer instead of the Windows capability.
    $capability = $null
    if (-not (Get-Service -Name sshd -ErrorAction SilentlyContinue)) {
        $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
        if (-not $capability) {
            throw 'The OpenSSH.Server capability is not available on this Windows image. Install OpenSSH Server from https://github.com/PowerShell/Win32-OpenSSH/releases and run this script again.'
        }
    }

    if ($capability -and $capability.State -ne 'Installed') {
        try {
            Add-WindowsCapability -Online -Name $capability.Name | Out-Null
        }
        catch {
            throw "OpenSSH Server could not be installed: $($_.Exception.Message) If a WSUS server or a policy blocks Features on Demand, install OpenSSH Server from https://github.com/PowerShell/Win32-OpenSSH/releases and run this script again."
        }
    }

    Set-Service -Name sshd -StartupType Automatic
    Start-Service -Name sshd

    if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
            -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
    }

    # sshd reads keys for administrators only from this file, and only if
    # Administrators and SYSTEM are its sole owners.
    $keysPath = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
    $existing = @()
    if (Test-Path -LiteralPath $keysPath) {
        $existing = @(Get-Content -LiteralPath $keysPath | Where-Object { $_.Trim() })
    }

    $missing = @($AuthorizedKeys | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $existing -notcontains $_ })
    if ($missing.Count -gt 0 -or -not (Test-Path -LiteralPath $keysPath)) {
        # A key file copied from a CD, as earlier versions of Setup-Guest.ps1
        # did, is read-only.
        if (Test-Path -LiteralPath $keysPath) { (Get-Item -LiteralPath $keysPath).IsReadOnly = $false }
        Set-Content -LiteralPath $keysPath -Value (@($existing) + $missing) -Encoding ASCII
    }

    Invoke-Native icacls.exe @($keysPath, '/inheritance:r', '/grant', '*S-1-5-32-544:F', '/grant', '*S-1-5-18:F')
}

function Add-QaLogonType {
    if ('WpfQaLogon' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class WpfQaLogon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    private const uint POLICY_CREATE_SECRET = 0x00000020;
    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaStorePrivateData(IntPtr policy, ref LSA_UNICODE_STRING keyName, IntPtr privateData);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool ValidateCredentials(string user, string domain, string password)
    {
        IntPtr token;
        if (!LogonUser(user, domain, password, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out token))
        {
            return false;
        }

        CloseHandle(token);
        return true;
    }

    // Stores the automatic logon password as the LSA secret "DefaultPassword",
    // where Winlogon reads it, instead of as plain text in the registry.
    // A null password deletes the secret.
    public static void StoreDefaultPassword(string password)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES)) };
        IntPtr policy;
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_SECRET, out policy));
        var key = ToLsaString("DefaultPassword");
        var data = password == null ? (LSA_UNICODE_STRING?)null : ToLsaString(password);
        IntPtr dataPointer = IntPtr.Zero;
        try
        {
            if (data.HasValue)
            {
                dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LSA_UNICODE_STRING)));
                Marshal.StructureToPtr(data.Value, dataPointer, false);
            }

            Check(LsaStorePrivateData(policy, ref key, dataPointer));
        }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(key.Buffer);
            if (data.HasValue) Marshal.FreeHGlobal(data.Value.Buffer);
            LsaClose(policy);
        }
    }

    private static LSA_UNICODE_STRING ToLsaString(string value)
    {
        return new LSA_UNICODE_STRING
        {
            Buffer = Marshal.StringToHGlobalUni(value),
            Length = (ushort)(value.Length * 2),
            MaximumLength = (ushort)(value.Length * 2 + 2)
        };
    }

    private static void Check(uint status)
    {
        if (status != 0)
        {
            throw new Win32Exception(LsaNtStatusToWinError(status));
        }
    }
}
'@
}

# Logs the given user on automatically at every start. The password is stored
# as an LSA secret, not as plain text in the registry.
function Enable-QaAutoLogon {
    param(
        [Parameter(Mandatory)] [string] $UserName,
        [Parameter(Mandatory)] [string] $Domain,
        [Parameter(Mandatory)] [securestring] $Password
    )

    $plain = ([pscredential]::new($UserName, $Password)).GetNetworkCredential().Password
    Add-QaLogonType
    if (-not [WpfQaLogon]::ValidateCredentials($UserName, $Domain, $plain)) {
        throw "The password for $Domain\$UserName is not correct."
    }

    [WpfQaLogon]::StoreDefaultPassword($plain)

    $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-RegistryValue $winlogon 'AutoAdminLogon' '1' 'String'
    Set-RegistryValue $winlogon 'DefaultUserName' $UserName 'String'
    Set-RegistryValue $winlogon 'DefaultDomainName' $Domain 'String'
    Remove-ItemProperty -LiteralPath $winlogon -Name 'DefaultPassword' -ErrorAction SilentlyContinue
    Remove-ItemProperty -LiteralPath $winlogon -Name 'AutoLogonCount' -ErrorAction SilentlyContinue

    # "Only allow Windows Hello sign-in" turns automatic logon off.
    Set-RegistryValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device' 'DevicePasswordLessBuildVersion' 0
}
