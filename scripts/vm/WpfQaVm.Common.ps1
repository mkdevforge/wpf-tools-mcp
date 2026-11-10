# Helpers shared by the QA VM scripts. Dot-source this file; the functions that
# call vmrun use $vmware from Find-VMware in the caller's scope.

function Write-Step([string] $Message) {
    Write-Host "[$(Get-Date -Format HH:mm:ss)] $Message"
}

function Get-QaVmStateDirectory([string] $VmName) {
    Join-Path $env:LOCALAPPDATA "WpfToolsMcp\vms\$VmName"
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

function Get-SshArguments([pscustomobject] $Info) {
    @('-i', $Info.sshKeyPath, '-o', "UserKnownHostsFile=$($Info.knownHostsPath)", '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', '-o', 'LogLevel=ERROR')
}

function Invoke-GuestSsh([pscustomobject] $Info, [string] $IpAddress, [string] $Command) {
    $sshArgs = (Get-SshArguments $Info) + @("$($Info.userName)@$IpAddress", $Command)
    $output = & ssh @sshArgs 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        throw "SSH command failed with exit code $LASTEXITCODE`: $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Copy-ToGuest([pscustomobject] $Info, [string] $IpAddress, [string[]] $Sources, [string] $GuestDirectory) {
    $scpArgs = (Get-SshArguments $Info) + @('-r', '-q') + $Sources + @("$($Info.userName)@$IpAddress`:$($GuestDirectory -replace '\\', '/')")
    $output = & scp @scpArgs 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        throw "Copying to the guest failed with exit code $LASTEXITCODE`: $($output -join ' ')"
    }
}

function Get-GuestIpAddress([string] $Vmx) {
    ((Invoke-Tool $vmware.VmRun @('-T', 'ws', 'getGuestIPAddress', $Vmx, '-wait')).Output | Select-Object -Last 1).Trim()
}
