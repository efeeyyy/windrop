<#
  WinDrop kullanıcı başına kurulum (yönetici gerekmez; yalnız güvenlik duvarı adımı yükseltilir).
    - Uygulamayı InstallRoot altına kopyalar (varsayılan %LOCALAPPDATA%\Programs\WinDrop)
    - Başlat menüsü kısayolu, SendTo kısayolu ("WinDrop.lnk", --send; ShellIntegration ile aynı)
    - -AutoStart: HKCU Run "WinDrop" = "<exe>" --hidden
    - Güvenlik duvarı: firewall.ps1 ayrı yükseltilmiş süreçte çalışır (-SkipFirewall ile atlanır)
  -DryRun: Başlat menüsü / SendTo / Run kaydına ve güvenlik duvarına YAZMAZ, yalnız yazdırır
  (kopyalama InstallRoot'a yine olur). Yeniden çalıştırmak güvenlidir.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\WinDrop'),
    [string]$SourceDir,
    [switch]$AutoStart,
    [switch]$NoSendTo,
    [switch]$SkipFirewall,
    [switch]$DryRun,
    [int]$Port = 53600
)
$ErrorActionPreference = 'Stop'

if (-not $SourceDir) {
    foreach ($c in (Join-Path $PSScriptRoot 'app'), (Join-Path $PSScriptRoot '..\dist\WinDrop-win-x64')) {
        if (Test-Path (Join-Path $c 'WinDrop.App.exe')) { $SourceDir = $c; break }
    }
}
if (-not $SourceDir -or -not (Test-Path (Join-Path $SourceDir 'WinDrop.App.exe'))) {
    throw 'Uygulama dosyaları bulunamadı (WinDrop.App.exe). Önce build\publish.ps1 çalıştırın ya da -SourceDir verin.'
}
$SourceDir = (Resolve-Path $SourceDir).Path
New-Item -ItemType Directory -Force $InstallRoot | Out-Null
$InstallRoot = (Resolve-Path $InstallRoot).Path
$exe = Join-Path $InstallRoot 'WinDrop.App.exe'

# Çalışan örneği kapat (dosyalar kilitli olmasın)
Get-Process -Name 'WinDrop.App' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "$InstallRoot*" } | ForEach-Object {
        Write-Host "Çalışan WinDrop kapatılıyor (PID $($_.Id))"
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(4000)) { $_.Kill() }
    }

Write-Host "Kopyalanıyor: $SourceDir -> $InstallRoot"
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallRoot -Recurse -Force
foreach ($f in 'uninstall.ps1', 'firewall.ps1') {
    $p = Join-Path $PSScriptRoot $f
    if (Test-Path $p) { Copy-Item $p $InstallRoot -Force }
}

function New-Shortcut([string]$Path, [string]$Arguments, [string]$Description) {
    if ($DryRun) { Write-Host "[DryRun] Kısayol: $Path -> $exe $Arguments"; return }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    $sc.TargetPath = $exe
    $sc.Arguments = $Arguments
    $sc.WorkingDirectory = $InstallRoot
    $sc.Description = $Description
    $sc.IconLocation = "$exe,0"
    $sc.Save()
    Write-Host "Kısayol: $Path"
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinDrop.lnk'
$sendTo    = Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\WinDrop.lnk'
New-Shortcut $startMenu '' 'WinDrop'
if (-not $NoSendTo) { New-Shortcut $sendTo '--send' 'WinDrop ile gönder' }

if ($AutoStart) {
    $cmd = "`"$exe`" --hidden"
    if ($DryRun) { Write-Host "[DryRun] HKCU Run WinDrop = $cmd" }
    else {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        Set-ItemProperty -Path $runKey -Name 'WinDrop' -Value $cmd
        Write-Host 'Otomatik başlangıç açıldı.'
    }
}

if ($SkipFirewall) {
    Write-Host 'Güvenlik duvarı adımı atlandı (-SkipFirewall).'
} elseif ($DryRun) {
    Write-Host "[DryRun] Güvenlik duvarı: yükseltilmiş firewall.ps1 -Port $Port çalıştırılacaktı."
} else {
    $fw = Join-Path $PSScriptRoot 'firewall.ps1'
    if (-not (Test-Path $fw)) { $fw = Join-Path $InstallRoot 'firewall.ps1' }
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')
    if ($isAdmin) { & $fw -Port $Port }
    else {
        Write-Host 'Güvenlik duvarı kuralları için yönetici onayı isteniyor...'
        $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList `
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$fw`"", '-Port', $Port
        if ($p.ExitCode -ne 0) { Write-Warning 'Güvenlik duvarı adımı başarısız; sonra yönetici olarak firewall.ps1 çalıştırın.' }
    }
}
Write-Host "Tamam. Kurulum: $InstallRoot"
