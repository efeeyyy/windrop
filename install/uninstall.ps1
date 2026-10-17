<#
  WinDrop kurulumunu geri alır: kısayollar, Run kaydı, güvenlik duvarı kuralları, kurulum klasörü.
  Kullanıcı verisi (%LOCALAPPDATA%\WinDrop) yalnız -RemoveData ile silinir.
  -DryRun: hiçbir şeyi silmez, yapacaklarını yazar.
  Kurulum klasörünün içinden çalıştırılırsa, klasör silinirken betik dosyası da gider (sorun değil).
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\WinDrop'),
    [switch]$RemoveData,
    [switch]$SkipFirewall,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

function Remove-Thing([string]$Path, [switch]$Recurse) {
    if (-not (Test-Path $Path)) { Write-Host "Yok (atlandı): $Path"; return }
    if ($DryRun) { Write-Host "[DryRun] Silinecek: $Path"; return }
    Remove-Item $Path -Recurse:$Recurse -Force
    Write-Host "Silindi: $Path"
}

Get-Process -Name 'WinDrop.App' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "$InstallRoot*" } | ForEach-Object {
        if ($DryRun) { Write-Host "[DryRun] Kapatılacak: PID $($_.Id)"; return }
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(4000)) { $_.Kill() }
    }

Remove-Thing (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinDrop.lnk')
Remove-Thing (Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\WinDrop.lnk')

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($null -ne (Get-ItemProperty $run -Name 'WinDrop' -ErrorAction SilentlyContinue)) {
    if ($DryRun) { Write-Host '[DryRun] Run kaydı silinecek: WinDrop' }
    else { Remove-ItemProperty $run -Name 'WinDrop'; Write-Host 'Run kaydı silindi.' }
}

if (-not $SkipFirewall) {
    $fw = Join-Path $PSScriptRoot 'firewall.ps1'
    $has = Get-NetFirewallRule -DisplayName 'WinDrop data', 'WinDrop hotspot data' -ErrorAction SilentlyContinue
    if (-not $has) { Write-Host 'Güvenlik duvarı kuralı yok (atlandı).' }
    elseif ($DryRun) { Write-Host '[DryRun] Güvenlik duvarı kuralları kaldırılacak.' }
    elseif (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) { & $fw -Remove }
    else {
        Write-Host 'Güvenlik duvarı kuralları için yönetici onayı isteniyor...'
        Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$fw`"", '-Remove'
    }
}

Remove-Thing $InstallRoot -Recurse

if ($RemoveData) { Remove-Thing (Join-Path $env:LOCALAPPDATA 'WinDrop') -Recurse }
else { Write-Host 'Kullanıcı verisi korundu (%LOCALAPPDATA%\WinDrop). Silmek için -RemoveData.' }
