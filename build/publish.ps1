<#
  WinDrop yayın paketi: dist\WinDrop-win-x64\ (uygulama) ve dist\WinDrop-win-x64.zip
  (app\ + install.ps1 + uninstall.ps1 + firewall.ps1). Kullanım: build\publish.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$dist = Join-Path $repo 'dist'
$out = Join-Path $dist 'WinDrop-win-x64'
$zip = Join-Path $dist 'WinDrop-win-x64.zip'
$stage = Join-Path $dist 'stage'

Remove-Item $out, $zip, $stage -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet publish (Join-Path $repo 'windows\src\WinDrop.App') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force (Join-Path $stage 'app') | Out-Null
Copy-Item (Join-Path $out '*') (Join-Path $stage 'app') -Recurse
foreach ($f in 'install.ps1', 'uninstall.ps1', 'firewall.ps1') { Copy-Item (Join-Path $repo "install\$f") $stage }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

"{0}  {1:N1} MB" -f $zip, ((Get-Item $zip).Length / 1MB)
