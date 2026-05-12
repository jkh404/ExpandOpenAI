[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$OutputDir,
    [switch]$SkipBuild,
    [switch]$NoSymbols
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
$projectPath = Join-Path $repoRoot "ExpandOpenAI\ExpandOpenAI.csproj"

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\nuget"
}

$outputPath = (New-Item -ItemType Directory -Path $OutputDir -Force).FullName

$existingPackages = @{}
Get-ChildItem -Path $outputPath -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".nupkg", ".snupkg" } |
    ForEach-Object {
        $existingPackages[$_.FullName] = $_.LastWriteTimeUtc
    }

$packArgs = @(
    "pack"
    $projectPath
    "--configuration"
    $Configuration
    "--output"
    $outputPath
    "-p:ContinuousIntegrationBuild=true"
)

if ($SkipBuild) {
    $packArgs += "--no-build"
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
        throw "Version must look like SemVer, for example: 1.0.0 or 1.0.0-preview.1"
    }

    $packArgs += "-p:Version=$Version"
    $packArgs += "-p:PackageVersion=$Version"
}

if (-not $NoSymbols) {
    $packArgs += "-p:IncludeSymbols=true"
    $packArgs += "-p:SymbolPackageFormat=snupkg"
}

Write-Host "Packing NuGet package from $projectPath"
Write-Host "Output: $outputPath"
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Version override: $Version"
}

& dotnet @packArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

$packages = Get-ChildItem -Path $outputPath -File |
    Where-Object {
        $_.Extension -in ".nupkg", ".snupkg" -and (
            -not $existingPackages.ContainsKey($_.FullName) -or
            $_.LastWriteTimeUtc -gt $existingPackages[$_.FullName]
        )
    } |
    Sort-Object Name

if ($packages.Count -eq 0) {
    throw "No package files were produced."
}

Write-Host ""
Write-Host "Generated package files:"
$packages | ForEach-Object {
    Write-Host " - $($_.FullName)"
}
