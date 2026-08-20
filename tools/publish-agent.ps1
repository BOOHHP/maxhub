# Publishes MaxHub Agent as a self-contained single-file exe for direct distribution.
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

# 直接分发 exe（单文件自包含），不再打 zip
$distPath = Join-Path $artifactsDir "MaxHubAgent-$version-win-x64.exe"
Copy-Item $exe $distPath -Force

$exeSizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $distPath).Hash.ToLowerInvariant()

# Best-effort LAN mirror. The server serves data/agent first and redirects to GitHub when absent.
$serverRoot = "\\10.2.13.8\Server\maxhub"
if (Test-Path $serverRoot) {
    try {
        $mirrorDir = Join-Path $serverRoot "data\agent"
        New-Item $mirrorDir -ItemType Directory -Force | Out-Null
        Copy-Item $distPath (Join-Path $mirrorDir (Split-Path $distPath -Leaf)) -Force
        Write-Host "Mirror:    $mirrorDir"
    }
    catch {
        Write-Warning "LAN mirror failed; GitHub release remains available: $($_.Exception.Message)"
    }
}
Write-Host "Published: $exe ($exeSizeMb MB)"
Write-Host "Dist:      $distPath"
Write-Host "SHA256:    $sha256"
Write-Host ""
Write-Host "Next: gh release create v$version $distPath --title 'MaxHub Agent v$version'"
Write-Host "Server auto-detects the latest GitHub Release (Agent:GitHubRepo); no manual registration needed."
