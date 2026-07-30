[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$desktopDir = Join-Path $repoRoot 'seekclaw_desktop'
$runtimeStage = Join-Path $desktopDir 'runtime\win-x64'
$builderOutput = Join-Path $desktopDir 'release'
$unpackedOutput = Join-Path $builderOutput 'win-unpacked'
$publishRoot = Join-Path $repoRoot 'publish'
$finalOutput = Join-Path $publishRoot 'SeekClaw-win-x64'

function Assert-WorkspacePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $repoRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $fullPath"
    }
    return $fullPath
}

function Reset-Directory([string]$Path) {
    $fullPath = Assert-WorkspacePath $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Remove-WorkspaceDirectory([string]$Path) {
    $fullPath = Assert-WorkspacePath $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-Native([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        Write-Host "`n> $Command $($Arguments -join ' ')" -ForegroundColor Cyan
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Command failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK is required but dotnet was not found in PATH.'
}
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    throw 'pnpm is required but was not found in PATH.'
}

Reset-Directory $runtimeStage
Reset-Directory $finalOutput
Remove-WorkspaceDirectory $builderOutput

try {
    if (-not $SkipInstall) {
        Invoke-Native 'pnpm' @('install', '--frozen-lockfile') $desktopDir
    }

    if (-not $SkipTests) {
        Invoke-Native 'dotnet' @('test', 'SeekClaw.slnx', '-c', 'Release') $repoRoot
        Invoke-Native 'pnpm' @('test') $desktopDir
    }

    Invoke-Native 'dotnet' @(
        'publish',
        'seekclaw_cli/seekclaw_cli.csproj',
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $runtimeStage
    ) $repoRoot

    Invoke-Native 'pnpm' @('build') $desktopDir
    Invoke-Native 'pnpm' @('exec', 'electron-builder', '--win', 'dir', '--x64') $desktopDir

    if (-not (Test-Path -LiteralPath $unpackedOutput)) {
        throw "Electron builder output was not found: $unpackedOutput"
    }
    Get-ChildItem -LiteralPath $unpackedOutput -Force | Copy-Item -Destination $finalOutput -Recurse -Force

    $desktopExecutable = Join-Path $finalOutput 'SeekClaw.exe'
    $runtimeExecutable = Join-Path $finalOutput 'resources\runtime\seekclaw.exe'
    if (-not (Test-Path -LiteralPath $desktopExecutable)) {
        throw "Desktop executable is missing: $desktopExecutable"
    }
    if (-not (Test-Path -LiteralPath $runtimeExecutable)) {
        throw "Bundled Runtime executable is missing: $runtimeExecutable"
    }

    Write-Host "`nSeekClaw release is ready:" -ForegroundColor Green
    Write-Host $finalOutput
    Write-Host "Run: $desktopExecutable" -ForegroundColor Green
}
finally {
    Remove-WorkspaceDirectory (Join-Path $desktopDir 'runtime')
    Remove-WorkspaceDirectory $builderOutput
}
