<#
  WinDrop kurulumunu geri alır: kısayollar, Run kaydı, "Uygulamalar ve özellikler" kaydı, güvenlik duvarı kuralları, kurulum klasörü.
  Kullanıcı verisi (%LOCALAPPDATA%\WinDrop) yalnız -RemoveData ile silinir.
  Çalıştırma (varsayılan ExecutionPolicy altında):
      powershell -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\WinDrop\uninstall.ps1"
  Çalışan WinDrop önce uygulamanın kendi "kapat" isteğiyle (tek örnek borusu, QUIT) kapatılır; kapanmazsa son çare sonlandırılır.
  Yalnız HEDEFİ bu -InstallRoot olan kısayol / Run değeri / kayıt silinir (başka bir kurulumun kayıtlarına dokunulmaz).
  Güvenlik duvarı kuralları port bazlıdır (tüm kurulumlar için ortaktır): varsayılan konumda başka bir kurulum
  duruyorsa (ve bu -InstallRoot varsayılan değilse) dokunulmaz.
  -DryRun: hiçbir şeyi kapatmaz/silmez, yapacaklarını yazar.
  -StartMenuPath / -SendToPath / -SkipRegistry: yalnız deneme (gerçek Başlat/SendTo/HKCU'ya dokunmadan sınama).
  Kurulum klasörünün içinden çalıştırılırsa, klasör silinirken betik dosyası da gider (sorun değil).
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\WinDrop'),
    [switch]$RemoveData,
    [switch]$SkipFirewall,
    [switch]$DryRun,
    [string]$StartMenuPath = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinDrop.lnk'),
    [string]$SendToPath = (Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\WinDrop.lnk'),
    [switch]$SkipRegistry
)
$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$defaultRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\WinDrop')).TrimEnd('\')

function Test-UnderPath([string]$Path, [string]$Root) {
    if (-not $Path) { return $false }
    return $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-ProcPath($Process) { try { $Process.Path } catch { $null } }

function Get-ShortcutTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (New-Object -ComObject WScript.Shell).CreateShortcut($Path).TargetPath } catch { return $null }
}

function Remove-Thing([string]$Path, [switch]$Recurse) {
    if (-not (Test-Path -LiteralPath $Path)) { Write-Host "Yok (atlandı): $Path"; return }
    if ($DryRun) { Write-Host "[DryRun] Silinecek: $Path"; return }
    Remove-Item -LiteralPath $Path -Recurse:$Recurse -Force
    Write-Host "Silindi: $Path"
}

# Yalnız bu kuruluma işaret eden kısayolu siler.
function Remove-OwnShortcut([string]$Path) {
    $target = Get-ShortcutTarget $Path
    if (-not $target) { Write-Host "Yok (atlandı): $Path"; return }
    if (-not (Test-UnderPath $target $InstallRoot)) { Write-Host "Başka bir kuruluma işaret ediyor, atlandı: $Path -> $target"; return }
    Remove-Thing $Path
}

function Send-QuitRequest {
    $pipe = 'WinDrop.App.Pipe.' + [Environment]::UserName + $env:WINDROP_INSTANCE
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::Out)
    try {
        $client.Connect(2000)
        $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
        $writer.NewLine = "`n"
        $writer.WriteLine('QUIT')
        $writer.Flush()
        return $true
    } catch { return $false } finally { $client.Dispose() }
}

$procs = @(Get-Process -Name 'WinDrop.App' -ErrorAction SilentlyContinue | Where-Object { Test-UnderPath (Get-ProcPath $_) $InstallRoot })
foreach ($p in $procs) {
    if ($DryRun) { Write-Host "[DryRun] Kapatılacak: PID $($p.Id)"; continue }
    Write-Host "Çalışan WinDrop kapatılıyor (PID $($p.Id))"
    [void](Send-QuitRequest)
    if (-not $p.WaitForExit(12000)) {
        Write-Warning "WinDrop zarifçe kapanmadı; zorla sonlandırılıyor (PID $($p.Id))."
        try { $p.Kill() } catch { }
        [void]$p.WaitForExit(5000)
    }
}

Remove-OwnShortcut $StartMenuPath
Remove-OwnShortcut $SendToPath

if (-not $SkipRegistry) {
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runValue = (Get-ItemProperty $run -Name 'WinDrop' -ErrorAction SilentlyContinue).WinDrop
    if ($null -ne $runValue) {
        if ($runValue.IndexOf($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Write-Host "Run kaydı başka bir kuruluma işaret ediyor, atlandı: $runValue"
        }
        elseif ($DryRun) { Write-Host '[DryRun] Run kaydı silinecek: WinDrop' }
        else { Remove-ItemProperty $run -Name 'WinDrop'; Write-Host 'Run kaydı silindi.' }
    }

    $uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinDrop'
    $loc = (Get-ItemProperty $uninstKey -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
    if ($loc) {
        if ([IO.Path]::GetFullPath($loc).TrimEnd('\') -ne $InstallRoot) { Write-Host "Uygulamalar ve özellikler kaydı başka bir kuruluma ait, atlandı: $loc" }
        elseif ($DryRun) { Write-Host "[DryRun] Uygulamalar ve özellikler kaydı silinecek: $uninstKey" }
        else { Remove-Item $uninstKey -Recurse -Force; Write-Host 'Uygulamalar ve özellikler kaydı silindi.' }
    }
}

# Güvenlik duvarı kuralları port bazlıdır (kuruluma özel değil): başka bir kurulum kalmışsa dokunma.
$otherInstall = ($InstallRoot -ne $defaultRoot) -and (Test-Path (Join-Path $defaultRoot 'WinDrop.App.exe'))
if (-not $SkipFirewall) {
    $fw = Join-Path $PSScriptRoot 'firewall.ps1'
    if (-not (Test-Path $fw)) { $fw = Join-Path $InstallRoot 'firewall.ps1' }
    $has = Get-NetFirewallRule -DisplayName 'WinDrop data', 'WinDrop hotspot data' -ErrorAction SilentlyContinue
    if ($otherInstall) { Write-Host "Güvenlik duvarı kuralları atlandı: varsayılan konumda ($defaultRoot) başka bir kurulum var." }
    elseif (-not $has) { Write-Host 'Güvenlik duvarı kuralı yok (atlandı).' }
    elseif ($DryRun) { Write-Host '[DryRun] Güvenlik duvarı kuralları kaldırılacak.' }
    elseif (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) { & $fw -Remove }
    else {
        Write-Host 'Güvenlik duvarı kuralları için yönetici onayı isteniyor...'
        try {
            $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$fw`"", '-Remove'
            if ($p.ExitCode -ne 0) { Write-Warning "Güvenlik duvarı kuralları kaldırılamadı (çıkış kodu $($p.ExitCode)); sonra yönetici olarak firewall.ps1 -Remove çalıştırın." }
        } catch {
            # UAC iptal edildi: yarım kaldırma olmasın, kalan adımlar sürer
            Write-Warning "Güvenlik duvarı kuralları kaldırılmadı (yönetici onayı verilmedi: $($_.Exception.Message)). Sonra yönetici olarak firewall.ps1 -Remove çalıştırın."
        }
    }
}

Remove-Thing $InstallRoot -Recurse

if ($RemoveData) { Remove-Thing (Join-Path $env:LOCALAPPDATA 'WinDrop') -Recurse }
else { Write-Host 'Kullanıcı verisi korundu (%LOCALAPPDATA%\WinDrop). Silmek için -RemoveData.' }
