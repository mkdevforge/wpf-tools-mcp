#Requires -Version 7.2
<#
.SYNOPSIS
Creates a VMware Workstation Windows 11 VM for remote WPF Tools MCP QA.

.DESCRIPTION
The script needs only a Windows 11 ISO. It:

1. Creates the VM (legacy BIOS, NVMe disk, NAT network).
2. Installs Windows without prompts from a generated autounattend.xml. The
   setup checks for a TPM and Secure Boot are skipped. VMware needs VM
   encryption for a virtual TPM. UEFI boot from the Windows ISO waits for a key
   press, and VMware has no working command that sends keys to the guest, so
   the VM uses BIOS boot, which starts Setup from a blank disk without a prompt.
3. Runs guest/Setup-Guest.ps1 at the first logon. It installs VMware Tools, the
   .NET 8 SDK, and OpenSSH Server, and keeps the session logged on and unlocked.
4. Removes the setup media, starts the VM, checks the guest setup result and
   SSH access, and takes a snapshot named "clean" of the running desktop.

Windows installation takes about 20 to 40 minutes. If the script stops while
it waits, run it again with -Resume.

The guest password is random. The script stores it as a PSCredential that only
the current Windows user can read (DPAPI), together with the VM details, in
%LOCALAPPDATA%\WpfToolsMcp\vms\<VmName>.

.EXAMPLE
./scripts/vm/New-WpfQaVm.ps1 -IsoPath D:\iso\Win11_Enterprise_Eval.iso -VmDirectory D:\VMs\wpf-tools-qa
#>
[CmdletBinding()]
param(
    [string] $IsoPath,
    [string] $VmName = 'wpf-tools-qa',
    [string] $VmDirectory,
    [ValidateRange(2, 64)] [int] $CpuCount = 4,
    [ValidateRange(4096, 262144)] [int] $MemoryMB = 8192,
    [ValidateRange(64, 2048)] [int] $DiskGB = 80,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,19}$')] [string] $UserName = 'qa',
    [ValidatePattern('^[A-Za-z0-9-]{1,15}$')] [string] $ComputerName = 'WPF-QA',
    [string] $UILanguage = 'en-US',
    [string] $InputLocale,
    [string] $UserLocale,
    [string] $TimeZone,
    [ValidateRange(800, 7680)] [int] $ScreenWidth = 1920,
    [ValidateRange(600, 4320)] [int] $ScreenHeight = 1080,
    [string] $SshKeyPath = (Join-Path $HOME '.ssh\wpf-tools-qa_ed25519'),
    [ValidateRange(10, 600)] [int] $InstallTimeoutMinutes = 120,
    [switch] $Resume
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'

$stateDir = Join-Path $env:LOCALAPPDATA "WpfToolsMcp\vms\$VmName"
$credentialPath = Join-Path $stateDir 'guest-credential.xml'
$infoPath = Join-Path $stateDir 'vm.json'
$cacheDir = Join-Path $env:LOCALAPPDATA 'WpfToolsMcp\vm-cache'
$guestStateDir = 'C:\ProgramData\WpfToolsMcp\vm-setup'
$sdkUrl = 'https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe'

function Write-Step([string] $Message) {
    Write-Host "[$(Get-Date -Format HH:mm:ss)] $Message"
}

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

function Get-DefaultInputLocale {
    $tip = Get-WinUserLanguageList |
        ForEach-Object { $_.InputMethodTips } |
        Where-Object { $_ -match '^[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}$' } |
        Select-Object -First 1
    if ($tip) { return $tip }
    return '0409:00000409'
}

function New-GuestPassword {
    $alphabet = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    do {
        $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
        $password = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
    } until ($password -cmatch '[A-Z]' -and $password -cmatch '[a-z]' -and $password -match '[0-9]')
    return $password
}

function Get-SdkInstaller {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $path = Join-Path $cacheDir 'dotnet-sdk-8-win-x64.exe'
    $maxAge = [TimeSpan]::FromDays(30)
    if (-not (Test-Path $path) -or ((Get-Date) - (Get-Item $path).LastWriteTime) -gt $maxAge) {
        Write-Step "Downloading the .NET 8 SDK installer from $sdkUrl"
        $partial = "$path.partial"
        Invoke-WebRequest -Uri $sdkUrl -OutFile $partial -UseBasicParsing
        Move-Item -Force $partial $path
    }

    $signature = Get-AuthenticodeSignature -FilePath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        Remove-Item -Force $path
        throw "The .NET SDK installer signature is not a valid Microsoft signature ($($signature.Status)). The download was removed."
    }

    return $path
}

function Get-SshPublicKey {
    if (-not (Test-Path $SshKeyPath)) {
        Write-Step "Creating SSH key $SshKeyPath"
        New-Item -ItemType Directory -Force -Path (Split-Path $SshKeyPath) | Out-Null
        & ssh-keygen -t ed25519 -f $SshKeyPath -N '' -C "wpf-tools-mcp-qa@$env:COMPUTERNAME" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "ssh-keygen failed with exit code $LASTEXITCODE." }
    }

    $public = "$SshKeyPath.pub"
    if (-not (Test-Path $public)) {
        throw "The public key '$public' was not found."
    }

    return (Get-Content -Raw $public).Trim()
}

function ConvertTo-XmlText([string] $Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function New-AutounattendXml([string] $Password) {
    $e = @{
        UILanguage = ConvertTo-XmlText $UILanguage
        InputLocale = ConvertTo-XmlText $InputLocale
        UserLocale = ConvertTo-XmlText $UserLocale
        TimeZone = ConvertTo-XmlText $TimeZone
        ComputerName = ConvertTo-XmlText $ComputerName
        UserName = ConvertTo-XmlText $UserName
        Password = ConvertTo-XmlText $Password
    }
    $component = 'processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"'
    $firstLogon = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s = Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root ''wpf-qa-setup\Setup-Guest.ps1'' } | Where-Object { Test-Path $_ } | Select-Object -First 1; &amp; $s"'

    return @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" $component>
      <SetupUILanguage><UILanguage>$($e.UILanguage)</UILanguage></SetupUILanguage>
      <InputLocale>$($e.InputLocale)</InputLocale>
      <SystemLocale>$($e.UILanguage)</SystemLocale>
      <UILanguage>$($e.UILanguage)</UILanguage>
      <UserLocale>$($e.UserLocale)</UserLocale>
    </component>
    <component name="Microsoft-Windows-Setup" $component>
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add"><Order>1</Order><Path>reg.exe add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add"><Order>2</Order><Path>reg.exe add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
      </RunSynchronous>
      <DiskConfiguration>
        <Disk wcm:action="add">
          <DiskID>0</DiskID>
          <WillWipeDisk>true</WillWipeDisk>
          <CreatePartitions>
            <CreatePartition wcm:action="add"><Order>1</Order><Type>Primary</Type><Size>500</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>2</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>NTFS</Format><Label>System</Label><Active>true</Active></ModifyPartition>
            <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>
        </Disk>
      </DiskConfiguration>
      <ImageInstall>
        <OSImage>
          <InstallTo><DiskID>0</DiskID><PartitionID>2</PartitionID></InstallTo>
          <InstallToAvailablePartition>false</InstallToAvailablePartition>
        </OSImage>
      </ImageInstall>
      <UserData><AcceptEula>true</AcceptEula></UserData>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <ComputerName>$($e.ComputerName)</ComputerName>
      <TimeZone>$($e.TimeZone)</TimeZone>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" $component>
      <InputLocale>$($e.InputLocale)</InputLocale>
      <SystemLocale>$($e.UILanguage)</SystemLocale>
      <UILanguage>$($e.UILanguage)</UILanguage>
      <UserLocale>$($e.UserLocale)</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>$($e.UserName)</Name>
            <Group>Administrators</Group>
            <Password><Value>$($e.Password)</Value><PlainText>true</PlainText></Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <AutoLogon>
        <Username>$($e.UserName)</Username>
        <Enabled>true</Enabled>
        <LogonCount>9999999</LogonCount>
        <Password><Value>$($e.Password)</Value><PlainText>true</PlainText></Password>
      </AutoLogon>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add">
          <Order>1</Order>
          <CommandLine>$firstLogon</CommandLine>
          <Description>WPF Tools MCP QA setup</Description>
        </SynchronousCommand>
      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
"@
}

function New-IsoImage([string] $SourceDirectory, [string] $Path, [string] $VolumeName) {
    if (-not ('WpfQaIsoWriter' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class WpfQaIsoWriter
{
    public static void Write(object imageStream, string path, int blockSize, int totalBlocks)
    {
        var stream = (IStream)imageStream;
        long remaining = (long)blockSize * totalBlocks;
        var buffer = new byte[blockSize * 512];
        IntPtr bytesRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                while (remaining > 0)
                {
                    int request = (int)Math.Min(buffer.Length, remaining);
                    stream.Read(buffer, request, bytesRead);
                    int read = Marshal.ReadInt32(bytesRead);
                    if (read <= 0)
                    {
                        throw new IOException("The ISO image stream ended before the expected size.");
                    }

                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bytesRead);
        }
    }
}
'@
    }

    $image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $image.ChooseImageDefaultsForMediaType(13) # DVD+RW dual layer: room for the SDK installer
    $image.FileSystemsToCreate = 7             # ISO 9660, Joliet, and UDF
    $image.VolumeName = $VolumeName
    $image.Root.AddTree($SourceDirectory, $false)
    $result = $image.CreateResultImage()
    [WpfQaIsoWriter]::Write($result.ImageStream, $Path, $result.BlockSize, $result.TotalBlocks)
}

function New-VmxContent([string] $WindowsIso, [string] $SetupIso) {
    $lines = @(
        '.encoding = "UTF-8"'
        'config.version = "8"'
        'virtualHW.version = "22"'
        "displayName = `"$VmName`""
        'guestOS = "windows11-64"'
        'firmware = "bios"'
        "numvcpus = `"$CpuCount`""
        "cpuid.coresPerSocket = `"$CpuCount`""
        "memsize = `"$MemoryMB`""
        "nvram = `"$VmName.nvram`""
        'svga.present = "TRUE"'
        'svga.graphicsMemoryKB = "8388608"'
        'mks.enable3d = "TRUE"'
        'pciBridge0.present = "TRUE"'
        'pciBridge4.present = "TRUE"'
        'pciBridge4.virtualDev = "pcieRootPort"'
        'pciBridge4.functions = "8"'
        'pciBridge5.present = "TRUE"'
        'pciBridge5.virtualDev = "pcieRootPort"'
        'pciBridge5.functions = "8"'
        'pciBridge6.present = "TRUE"'
        'pciBridge6.virtualDev = "pcieRootPort"'
        'pciBridge6.functions = "8"'
        'pciBridge7.present = "TRUE"'
        'pciBridge7.virtualDev = "pcieRootPort"'
        'pciBridge7.functions = "8"'
        'vmci0.present = "TRUE"'
        'hpet0.present = "TRUE"'
        'nvme0.present = "TRUE"'
        'nvme0:0.present = "TRUE"'
        "nvme0:0.fileName = `"$VmName.vmdk`""
        'sata0.present = "TRUE"'
        'sata0:0.present = "TRUE"'
        'sata0:0.deviceType = "cdrom-image"'
        "sata0:0.fileName = `"$WindowsIso`""
        'sata0:0.startConnected = "TRUE"'
        'sata0:1.present = "TRUE"'
        'sata0:1.deviceType = "cdrom-image"'
        "sata0:1.fileName = `"$SetupIso`""
        'sata0:1.startConnected = "TRUE"'
        'sata0:2.present = "TRUE"'
        'sata0:2.deviceType = "cdrom-image"'
        "sata0:2.fileName = `"$($vmware.ToolsIso)`""
        'sata0:2.startConnected = "TRUE"'
        'ethernet0.present = "TRUE"'
        'ethernet0.connectionType = "nat"'
        'ethernet0.virtualDev = "e1000e"'
        'ethernet0.addressType = "generated"'
        'ethernet0.startConnected = "TRUE"'
        'usb.present = "TRUE"'
        'ehci.present = "TRUE"'
        'usb_xhci.present = "TRUE"'
        'sound.present = "FALSE"'
        'floppy0.present = "FALSE"'
        'tools.syncTime = "TRUE"'
        'tools.upgrade.policy = "manual"'
        # Answer VMware notices by default, so that no dialog stops an unattended power-on.
        'msg.autoAnswer = "TRUE"'
    )
    return ($lines -join "`r`n") + "`r`n"
}

function Remove-SetupMedia([string] $Vmx, [string] $SetupIso) {
    # Keep one disconnected CD drive. Remove the setup and Tools media, so the
    # VM never opens the setup ISO (which holds the guest password) again.
    # vmcli edits the configuration in a way that Workstation does not overwrite.
    foreach ($entry in @(
            @('sata0:0.startConnected', 'FALSE'),
            @('sata0:1.present', 'FALSE'),
            @('sata0:2.present', 'FALSE'))) {
        $null = Invoke-Tool $vmware.VmCli @($Vmx, 'ConfigParams', 'SetEntry', $entry[0], $entry[1])
    }

    if (Test-Path -LiteralPath $SetupIso) {
        Remove-Item -LiteralPath $SetupIso -Force
    }
}

function Wait-Until([scriptblock] $Condition, [TimeSpan] $Timeout, [string] $Description, [int] $IntervalSeconds = 15) {
    $deadline = (Get-Date) + $Timeout
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $([int]$Timeout.TotalMinutes) minutes while waiting for $Description."
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}

function Invoke-GuestVmRun([string] $Vmx, [pscredential] $Credential, [string[]] $Arguments, [switch] $AllowFailure) {
    $prefix = @('-T', 'ws', '-gu', $Credential.UserName, '-gp', $Credential.GetNetworkCredential().Password)
    return Invoke-Tool $vmware.VmRun ($prefix + $Arguments) -AllowFailure:$AllowFailure
}

# ---------------------------------------------------------------------------

$vmware = Find-VMware
if (-not $VmDirectory) {
    $VmDirectory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "Virtual Machines\$VmName"
}

$VmDirectory = [IO.Path]::GetFullPath($VmDirectory)
$vmx = Join-Path $VmDirectory "$VmName.vmx"
$setupIso = Join-Path $VmDirectory 'wpf-qa-setup.iso'

if (-not $Resume) {
    if (-not $IsoPath) { throw 'Specify -IsoPath, the path to a Windows 11 ISO.' }
    $IsoPath = [IO.Path]::GetFullPath($IsoPath)
    if (-not (Test-Path -LiteralPath $IsoPath)) { throw "The ISO '$IsoPath' was not found." }
    if ((Test-Path -LiteralPath $VmDirectory) -and (Get-ChildItem -LiteralPath $VmDirectory -Force | Select-Object -First 1)) {
        throw "The VM directory '$VmDirectory' is not empty. Choose another -VmDirectory, delete the old VM, or use -Resume."
    }

    if (-not $InputLocale) { $InputLocale = Get-DefaultInputLocale }
    if (-not $UserLocale) { $UserLocale = (Get-Culture).Name }
    if (-not $TimeZone) { $TimeZone = (Get-TimeZone).Id }

    $sdk = Get-SdkInstaller
    $publicKey = Get-SshPublicKey
    $password = New-GuestPassword
    $credential = [pscredential]::new($UserName, (ConvertTo-SecureString $password -AsPlainText -Force))

    New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
    $credential | Export-Clixml -LiteralPath $credentialPath

    Write-Step 'Building the setup ISO'
    $staging = Join-Path ([IO.Path]::GetTempPath()) "wpf-qa-setup-$([guid]::NewGuid().ToString('N'))"
    try {
        $setupDir = Join-Path $staging 'wpf-qa-setup'
        New-Item -ItemType Directory -Force -Path $setupDir | Out-Null
        Set-Content -LiteralPath (Join-Path $staging 'autounattend.xml') -Value (New-AutounattendXml $password) -Encoding utf8NoBOM
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'guest\Setup-Guest.ps1') -Destination $setupDir
        Copy-Item -LiteralPath $sdk -Destination (Join-Path $setupDir 'dotnet-sdk-win-x64.exe')
        Set-Content -LiteralPath (Join-Path $setupDir 'authorized_keys') -Value $publicKey -Encoding ascii

        New-Item -ItemType Directory -Force -Path $VmDirectory | Out-Null
        New-IsoImage -SourceDirectory $staging -Path $setupIso -VolumeName 'WPFQASETUP'
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Step "Creating the VM in $VmDirectory"
    $null = Invoke-Tool $vmware.VDiskManager @('-c', '-s', "$($DiskGB)GB", '-a', 'lsilogic', '-t', '0', (Join-Path $VmDirectory "$VmName.vmdk"))
    Set-Content -LiteralPath $vmx -Value (New-VmxContent $IsoPath $setupIso) -Encoding utf8NoBOM -NoNewline

    [ordered]@{
        vmName = $VmName
        vmx = $vmx
        userName = $UserName
        computerName = $ComputerName
        sshKeyPath = $SshKeyPath
        knownHostsPath = Join-Path $stateDir 'known_hosts'
        createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $infoPath -Encoding utf8NoBOM

    # "gui" waits until someone closes any VMware notice in the window, so start headless.
    # Open the .vmx in VMware Workstation to watch; the window attaches to the running VM.
    Write-Step 'Starting the VM'
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'start', $vmx, 'nogui')
}
else {
    if (-not (Test-Path -LiteralPath $vmx)) { throw "No VM was found at '$vmx'." }
    if (-not (Test-Path -LiteralPath $credentialPath)) { throw "The guest credential '$credentialPath' was not found." }
    $credential = Import-Clixml -LiteralPath $credentialPath
}

if (Test-Path -LiteralPath $setupIso) {
    Write-Step "Installing Windows and configuring the guest. This takes about 20 to 40 minutes. The VM shuts down when it is done."
    Wait-Until { -not (Test-VmRunning $vmx) } ([TimeSpan]::FromMinutes($InstallTimeoutMinutes)) 'Windows Setup and the guest setup to finish' 30

    Write-Step 'Removing the setup media'
    Remove-SetupMedia $vmx $setupIso
}

if (-not (Test-VmRunning $vmx)) {
    Write-Step 'Starting the configured VM'
    $null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'start', $vmx, 'nogui')
}

# Guest operations succeed only when VMware Tools runs in the guest. (checkToolsState
# reports "installed" rather than "running" for current Tools, so it is not used.)
Write-Step 'Waiting for VMware Tools and the automatic logon'
Wait-Until {
    $processes = Invoke-GuestVmRun $vmx $credential @('listProcessesInGuest', $vmx) -AllowFailure
    $processes.ExitCode -eq 0 -and ($processes.Output -match 'explorer\.exe')
} ([TimeSpan]::FromMinutes(20)) 'VMware Tools and the QA user desktop in the guest. Check C:\ProgramData\WpfToolsMcp\vm-setup\setup.log in the VM' 10

$resultHostPath = Join-Path $stateDir 'setup-result.json'
$null = Invoke-GuestVmRun $vmx $credential @('CopyFileFromGuestToHost', $vmx, "$guestStateDir\result.json", $resultHostPath)
$null = Invoke-GuestVmRun $vmx $credential @('CopyFileFromGuestToHost', $vmx, "$guestStateDir\setup.log", (Join-Path $stateDir 'setup.log')) -AllowFailure
$setupResult = Get-Content -Raw -LiteralPath $resultHostPath | ConvertFrom-Json
$failed = @($setupResult.steps.PSObject.Properties | Where-Object { $_.Value -ne 'ok' })
foreach ($step in $setupResult.steps.PSObject.Properties) {
    Write-Step "Guest step $($step.Name): $($step.Value)"
}

if ($failed.Count -gt 0) {
    throw "Guest setup did not complete: $($failed.Name -join ', ') failed. The VM is running without a snapshot. See $(Join-Path $stateDir 'setup.log')."
}

Write-Step "Setting the guest resolution to $($ScreenWidth)x$($ScreenHeight)"
$null = Invoke-Tool $vmware.VmCli @($vmx, 'MKS', 'SetGuestResolution', "$ScreenWidth", "$ScreenHeight")

$ip = ((Invoke-Tool $vmware.VmRun @('-T', 'ws', 'getGuestIPAddress', $vmx, '-wait')).Output | Select-Object -Last 1).Trim()
$info = Get-Content -Raw -LiteralPath $infoPath | ConvertFrom-Json
$knownHosts = $info.knownHostsPath
Write-Step "Checking SSH access to $UserName@$ip"
$sshArgs = @('-i', $info.sshKeyPath, '-o', "UserKnownHostsFile=$knownHosts", '-o', 'StrictHostKeyChecking=accept-new', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', "$($info.userName)@$ip")
$sshCheck = & ssh @sshArgs 'Write-Output ([Environment]::UserName)' 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "SSH access failed with exit code $LASTEXITCODE`: $sshCheck. The VM is running without a snapshot."
}

Start-Sleep -Seconds 20
Write-Step 'Taking the snapshot "clean" of the running desktop'
$null = Invoke-Tool $vmware.VmRun @('-T', 'ws', 'snapshot', $vmx, 'clean')

$info | Add-Member -NotePropertyName ipAddress -NotePropertyValue $ip -Force
$info | Add-Member -NotePropertyName snapshot -NotePropertyValue 'clean' -Force
$info | ConvertTo-Json | Set-Content -LiteralPath $infoPath -Encoding utf8NoBOM

Write-Step 'The QA VM is ready.'
Write-Host ''
Write-Host "  VM:         $vmx"
Write-Host "  Guest:      $($info.userName)@$ip (automatic logon, desktop unlocked)"
Write-Host "  SSH:        ssh -i `"$($info.sshKeyPath)`" -o UserKnownHostsFile=`"$knownHosts`" $($info.userName)@$ip"
Write-Host "  Password:   (Import-Clixml '$credentialPath').GetNetworkCredential().Password"
Write-Host "  Reset:      vmrun -T ws revertToSnapshot `"$vmx`" clean; vmrun -T ws start `"$vmx`" nogui"
Write-Host "  Watch:      open the .vmx in VMware Workstation; the window attaches to the running VM."
