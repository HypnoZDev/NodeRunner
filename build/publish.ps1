# Builds the portable, self-contained version of NodeRunner and zips it.
# Usage (from the repository root):  ./build/publish.ps1
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish/NodeRunner"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root "src/NodeRunner.App/NodeRunner.App.csproj") `
    -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$version = (Get-Item (Join-Path $out "NodeRunner.dll")).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $root "publish/NodeRunner-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

Write-Host "OK : $zip"
