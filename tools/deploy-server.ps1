# Deploys MaxHub.Server to the production shared path.
# Run AFTER stopping MaxHub.Server.exe on the server (10.2.13.8).
# ASCII-only script for Windows PowerShell 5.1 compatibility.
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$src = Join-Path $repoRoot "artifacts\publish\server"
$dst = "\\10.2.13.8\Server\maxhub"

if (-not (Test-Path $dst)) { throw "Server share not reachable: $dst" }

# Always publish fresh so wwwroot and binaries match the current source tree
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
dotnet publish (Join-Path $repoRoot "src\MaxHub.Server\MaxHub.Server.csproj") -c Release -o $src
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Preserve server-local files: data/ (db + signing key) and appsettings.Local.json
# Copy everything else (program files + wwwroot portal pages)
Get-ChildItem $src | Where-Object {
    $_.Name -notin @('appsettings.json', 'appsettings.Development.json', 'appsettings.Local.json') -and $_.Name -ne 'data'
} | ForEach-Object {
    Copy-Item $_.FullName $dst -Recurse -Force
}

Write-Host "Deployed to $dst"
Write-Host "Preserved: data/ and appsettings.Local.json (server-local)"
