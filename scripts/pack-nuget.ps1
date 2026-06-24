[CmdletBinding()]
param(
    [ValidateSet("All", "ExpandOpenAI", "ExpandVectorStore.Qdrant")]
    [string]$Package = "All",
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

$packageProjects = [ordered]@{
    "ExpandOpenAI" = "ExpandOpenAI\ExpandOpenAI.csproj"
    "ExpandVectorStore.Qdrant" = "ExpandVectorStore.Qdrant\ExpandVectorStore.Qdrant.csproj"
}

if (-not [string]::IsNullOrWhiteSpace($Version) -and
    $Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Version must look like SemVer, for example: 1.0.0 or 1.0.0-preview.1"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\nuget"
}

$selectedPackages = @(
    if ($Package -eq "All") {
        $packageProjects.GetEnumerator()
    }
    else {
        $packageProjects.GetEnumerator() | Where-Object { $_.Key -eq $Package }
    }
)

foreach ($packageProject in $selectedPackages) {
    $projectPath = Join-Path $repoRoot $packageProject.Value
    if (-not (Test-Path $projectPath)) {
        throw "Project file not found for package '$($packageProject.Key)': $projectPath"
    }
}

$outputPath = (New-Item -ItemType Directory -Path $OutputDir -Force).FullName

$existingPackages = @{}
Get-ChildItem -Path $outputPath -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".nupkg", ".snupkg" } |
    ForEach-Object {
        $existingPackages[$_.FullName] = $_.LastWriteTimeUtc
    }

Write-Host "Package selection: $Package"
Write-Host "Output: $outputPath"
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Version override: $Version"
}

foreach ($packageProject in $selectedPackages) {
    $packageId = $packageProject.Key
    $projectPath = Join-Path $repoRoot $packageProject.Value

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
        $packArgs += "-p:Version=$Version"
        $packArgs += "-p:PackageVersion=$Version"
    }

    if (-not $NoSymbols) {
        $packArgs += "-p:IncludeSymbols=true"
        $packArgs += "-p:SymbolPackageFormat=snupkg"
    }

    Write-Host ""
    Write-Host "Packing NuGet package '$packageId' from $projectPath"
    & dotnet @packArgs

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for package '$packageId' with exit code $LASTEXITCODE"
    }
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
