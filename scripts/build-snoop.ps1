param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$snoopRoot = Join-Path $repoRoot "references\\snoopwpf"

if (-not (Test-Path $snoopRoot)) {
    throw "Snoop repo not found at '$snoopRoot'. Ensure the git submodule is initialized."
}

# GitVersion.MsBuild can fail in CI when building from a detached submodule HEAD.
# Disable it aggressively for all Snoop builds.
$env:DisableGitVersionTask = "true"
$env:GitVersion_NoFetchEnabled = "true"
$env:GitVersion_NoNormalizeEnabled = "true"
$env:GitVersion_AllowShallowEnabled = "true"

$injectorLauncherProj = Join-Path $snoopRoot "Snoop.InjectorLauncher\\Snoop.InjectorLauncher.csproj"
$genericInjectorProj = Join-Path $snoopRoot "Snoop.GenericInjector\\Snoop.GenericInjector.vcxproj"

$gitVersionProps = @(
    "-p:DisableGitVersionTask=true",
    "-p:GitVersion_NoFetchEnabled=true",
    "-p:GitVersion_NoNormalizeEnabled=true",
    "-p:GitVersion_AllowShallowEnabled=true"
)

Write-Host "Building Snoop.InjectorLauncher ($Configuration)..." -ForegroundColor Cyan
foreach ($platformTarget in @("x86", "x64")) {
    Write-Host "  -> $platformTarget" -ForegroundColor DarkCyan
    dotnet restore $injectorLauncherProj -p:RootBuild=False -p:PlatformTarget=$platformTarget @gitVersionProps
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    dotnet build $injectorLauncherProj -c $Configuration --no-restore -p:RootBuild=False -p:PlatformTarget=$platformTarget @gitVersionProps
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Write-Host "Building Snoop.GenericInjector ($Configuration)..." -ForegroundColor Cyan
$msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\\Installer\\vswhere.exe"
    if (Test-Path $vswhere) {
        $msbuildPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\\**\\Bin\\MSBuild.exe" | Select-Object -First 1
        if ($msbuildPath) {
            $msbuild = Get-Command $msbuildPath -ErrorAction SilentlyContinue
        }
    }
}

if ($msbuild) {
    & $msbuild.Path $genericInjectorProj /m /p:Configuration=$Configuration /p:Platform=Win32 /p:RunCodeAnalysis=false /p:EnableMicrosoftCodeAnalysis=false
}
else {
    Write-Warning "Could not find MSBuild.exe from Visual Studio. Falling back to 'dotnet msbuild' (may not build C++ projects)."
    & dotnet msbuild $genericInjectorProj /m /p:Configuration=$Configuration /p:Platform=Win32 /p:RunCodeAnalysis=false /p:EnableMicrosoftCodeAnalysis=false
}

if ($LASTEXITCODE -ne 0) {
    Write-Warning "Generic injector build failed. Ensure 'Desktop development with C++' and a Windows 10/11 SDK are installed."
    exit $LASTEXITCODE
}

$binDir = Join-Path $snoopRoot "bin\\$Configuration"
Write-Host "Checking outputs under '$binDir'..." -ForegroundColor Cyan

$expected = @(
    "Snoop.InjectorLauncher.x86.exe",
    "Snoop.InjectorLauncher.x64.exe",
    "Snoop.GenericInjector.x86.dll",
    "Snoop.GenericInjector.x64.dll"
)

$missing = @()
foreach ($file in $expected) {
    $path = Join-Path $binDir $file
    if (-not (Test-Path $path)) {
        $missing += $file
    }
}

if ($missing.Count -gt 0) {
    Write-Warning "Missing expected outputs: $($missing -join ', ')"
    Write-Warning "If you don't have the C++ build tools installed, Snoop.GenericInjector may fail to build."
    exit 1
}

Write-Host "OK: Snoop injector assets built." -ForegroundColor Green
