param(
    [string]$Configuration = "Release",
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [switch]$FrameworkDependent,
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

    $publishArgs = @("publish", $project, "-c", $Configuration, "-r", $rid, "-o", $out)

    if ($FrameworkDependent) {
        $publishArgs += @("--self-contained", "false")
    }
    else {
        $publishArgs += @(
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true"
        )
    }

    Write-Host "Publishing $rid..." -ForegroundColor Cyan
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $rid"
    }

    if (-not $NoZip) {
        $zipPath = Join-Path $PSScriptRoot "publish\release_${version}_${rid}.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Write-Host "Creating $([System.IO.Path]::GetFileName($zipPath))..." -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zipPath
    }
}

Write-Host "All publish targets completed." -ForegroundColor Green
