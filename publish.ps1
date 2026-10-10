param(
    [string]$Configuration = "Release",
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [switch]$SelfContained,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "RVZStudio\RVZStudio.csproj"

[xml]$projectXml = Get-Content $project
$version = @($projectXml.Project.PropertyGroup.FileVersion) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { $version = "0.0.0" }

foreach ($rid in $Rids) {
    $out = Join-Path $PSScriptRoot "publish\$rid"

    # Remove any stale output so old files cannot leak into the archive.
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    $publishArgs = @(
        "publish", $project, "-c", $Configuration, "-r", $rid, "-o", $out,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true"
    )

    if ($SelfContained) {
        $publishArgs += @("--self-contained", "true")
    }
    else {
        # Default: framework-dependent single-file bundles (smaller; requires the .NET runtime).
        $publishArgs += @("--self-contained", "false")
    }

    Write-Host "Publishing $rid..." -ForegroundColor Cyan
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $rid"
    }

    # Ship the license, readme and release notes with every bundle.
    foreach ($file in @("LICENSE.txt", "ReadMe.md")) {
        $source = Join-Path $PSScriptRoot $file
        if (Test-Path $source) { Copy-Item $source $out -Force }
    }
    $whatsNew = Join-Path $PSScriptRoot "docs\WhatsNew.md"
    if (Test-Path $whatsNew) { Copy-Item $whatsNew (Join-Path $out "WhatsNew.md") -Force }

    if (-not $NoZip) {
        $zipPath = Join-Path $PSScriptRoot "publish\release_${version}_${rid}.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Write-Host "Creating $([System.IO.Path]::GetFileName($zipPath))..." -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zipPath
    }
}

Write-Host "All publish targets completed." -ForegroundColor Green
