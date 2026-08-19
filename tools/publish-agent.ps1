# Publishes MaxHub Agent as a self-contained single-file exe and zips it for distribution.
# ASCII-only script for Windows PowerShell 5.1 compatibility.
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot "src\MaxHub.Agent.Tray\MaxHub.Agent.Tray.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\agent"
$artifactsDir = Join-Path $repoRoot "artifacts"

dotnet publish $project -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publishDir "MaxHubAgent.exe"
if (-not (Test-Path $exe)) { throw "Publish output missing: $exe" }
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

$zipPath = Join-Path $artifactsDir "MaxHubAgent-$version-win-x64.zip"
Compress-Archive -Path $exe -DestinationPath $zipPath -Force

$exeSizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$zipSizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
Write-Host "Published: $exe ($exeSizeMb MB)"
Write-Host "Packaged:  $zipPath ($zipSizeMb MB)"
Write-Host "SHA256:    $sha256"
Write-Host ""
Write-Host "Next: upload $zipPath to GitHub Release, then set in appsettings.Local.json:"
Write-Host "  Agent:LatestVersion = $version"
Write-Host "  Agent:DownloadUrl   = https://github.com/<owner>/<repo>/releases/download/v$version/MaxHubAgent-$version-win-x64.zip"
Write-Host "  Agent:Sha256        = $sha256"
