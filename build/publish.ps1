<#
  WinDrop yayın paketi: dist\WinDrop-win-x64\ (uygulama) ve dist\WinDrop-win-x64.zip
  (app\ + install.ps1 + uninstall.ps1 + firewall.ps1 + INSTALL.md) + dist\WinDrop-win-x64.zip.sha256.
  Kullanım: build\publish.ps1 -Version 0.1.0
  Derleme başarısız olursa önceki dist\ çıktısı KORUNUR (yeni çıktı önce geçici klasöre alınır, başarıdan sonra yer değiştirir).
  Zip girişleri PowerShell 5.1'in Compress-Archive'ı gibi ters eğik çizgi kullanmaz (ZipFile, '/' ayırıcı) ve 2 GB sınırı yoktur.
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
$work = Join-Path $dist ('.build-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$newOut = Join-Path $work 'WinDrop-win-x64'
$stage = Join-Path $work 'stage'
$newZip = Join-Path $work 'WinDrop-win-x64.zip'

New-Item -ItemType Directory -Force $work | Out-Null
try {
    & $dotnet publish (Join-Path $repo 'windows\src\WinDrop.App') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:Version=$Version -o $newOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız ($LASTEXITCODE); önceki dist korundu." }

    New-Item -ItemType Directory -Force (Join-Path $stage 'app') | Out-Null
    Copy-Item (Join-Path $newOut '*') (Join-Path $stage 'app') -Recurse
    foreach ($f in 'install.ps1', 'uninstall.ps1', 'firewall.ps1') { Copy-Item (Join-Path $repo "install\$f") $stage }
    Copy-Item (Join-Path $repo 'docs\INSTALL.md') $stage

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $newZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash $newZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$newZip.sha256", "$hash  WinDrop-win-x64.zip`n", (New-Object System.Text.UTF8Encoding($false)))

    # Başarılı: eski çıktıyı yenisiyle değiştir
    Remove-Item $out, $zip, "$zip.sha256" -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item $newOut $out
    Move-Item $newZip $zip
    Move-Item "$newZip.sha256" "$zip.sha256"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

"{0}  {1:N1} MB  SHA256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash
