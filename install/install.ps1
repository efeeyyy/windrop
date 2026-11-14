<#
  WinDrop kullanıcı başına kurulum (yönetici gerekmez; yalnız güvenlik duvarı adımı yükseltilir).
    - Uygulamayı InstallRoot altına kopyalar (varsayılan %LOCALAPPDATA%\Programs\WinDrop)
    - Başlat menüsü kısayolu, SendTo kısayolu ("WinDrop.lnk", --send; ShellIntegration ile aynı)
    - -AutoStart: HKCU Run "WinDrop" = "<exe>" --hidden
    - "Uygulamalar ve özellikler" kaydı (HKCU Uninstall\WinDrop; Kaldır = uninstall.ps1)
    - Güvenlik duvarı: firewall.ps1 ayrı yükseltilmiş süreçte çalışır (-SkipFirewall ile atlanır)
  Çalıştırma: varsayılan ExecutionPolicy ve internetten indirilen zip (Mark of the Web) yüzünden betik doğrudan
  çalışmayabilir. Şunu kullanın:
      powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 [seçenekler]
  Güncelleme: aynı betiği yeni sürümle yeniden çalıştırın. Çalışan WinDrop önce uygulamanın kendi "kapat" isteğiyle
  (tek örnek borusu, QUIT) zarifçe kapatılır; kapanmazsa en son çare zorla sonlandırılır. Dosyalar önce
  "<InstallRoot>.new" klasörüne kopyalanır, sonra klasör değiştirilir (karışık sürüm/artık dosya kalmaz).
  Kullanıcı verisi (%LOCALAPPDATA%\WinDrop) ayrıdır ve korunur.
  -DryRun: HİÇBİR ŞEYİ değiştirmez (uygulamayı kapatmaz, kopyalamaz, kısayol/kayıt/güvenlik duvarına yazmaz), yalnız yazdırır.
  -StartMenuPath / -SendToPath / -SkipRegistry: yalnız deneme (gerçek Başlat/SendTo/HKCU'ya dokunmadan sınama).
  Yeniden çalıştırmak güvenlidir.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\WinDrop'),
    [string]$SourceDir,
    [switch]$AutoStart,
    [switch]$NoSendTo,
    [switch]$SkipFirewall,
    [switch]$DryRun,
    [int]$Port = 53600,
    [string]$StartMenuPath = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinDrop.lnk'),
    [string]$SendToPath = (Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\WinDrop.lnk'),
    [switch]$SkipRegistry
)
$ErrorActionPreference = 'Stop'

function Test-UnderPath([string]$Path, [string]$Root) {
    if (-not $Path) { return $false }
    return $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-ProcPath($Process) { try { $Process.Path } catch { $null } }

# Uygulamaya tek örnek borusu üzerinden "QUIT" gönderir (uygulama X'te tepsiye gizlendiği için CloseMainWindow işe yaramaz).
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

# Yalnız $Root altından çalışan WinDrop'u kapatır: QUIT -> bekle -> son çare Kill + bekle.
function Stop-WinDrop([string]$Root) {
    $procs = @(Get-Process -Name 'WinDrop.App' -ErrorAction SilentlyContinue | Where-Object { Test-UnderPath (Get-ProcPath $_) $Root })
    foreach ($p in $procs) {
        if ($DryRun) { Write-Host "[DryRun] Çalışan WinDrop kapatılacaktı (PID $($p.Id))"; continue }
        Write-Host "Çalışan WinDrop kapatılıyor (PID $($p.Id))"
        [void](Send-QuitRequest)
        if (-not $p.WaitForExit(12000)) {
            Write-Warning "WinDrop zarifçe kapanmadı; zorla sonlandırılıyor (PID $($p.Id))."
            try { $p.Kill() } catch { }
            [void]$p.WaitForExit(5000)
        }
    }
}

function Get-ShortcutTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (New-Object -ComObject WScript.Shell).CreateShortcut($Path).TargetPath } catch { return $null }
}

if (-not $SourceDir) {
    foreach ($c in (Join-Path $PSScriptRoot 'app'), (Join-Path $PSScriptRoot '..\dist\WinDrop-win-x64')) {
        if (Test-Path (Join-Path $c 'WinDrop.App.exe')) { $SourceDir = $c; break }
    }
}
if (-not $SourceDir -or -not (Test-Path (Join-Path $SourceDir 'WinDrop.App.exe'))) {
    throw 'Uygulama dosyaları bulunamadı (WinDrop.App.exe). Önce build\publish.ps1 çalıştırın ya da -SourceDir verin.'
}
$SourceDir = (Resolve-Path $SourceDir).Path
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ((Test-UnderPath $SourceDir $InstallRoot) -or (Test-UnderPath $InstallRoot $SourceDir)) {
    throw "Kaynak ($SourceDir) ve kurulum klasörü ($InstallRoot) iç içe olamaz."
}
$exe = Join-Path $InstallRoot 'WinDrop.App.exe'
$staging = $InstallRoot + '.new'
$backup = $InstallRoot + '.old'
$rootName = Split-Path $InstallRoot -Leaf

# ---- Kopyalama: önce .new'e, sonra çalışan uygulamayı kapat, klasörü değiştir ----
if ($DryRun) {
    Write-Host "[DryRun] Kopyalanacaktı: $SourceDir -> $staging, sonra $InstallRoot ile değiştirilecekti (eskisi $backup olarak yedeklenip silinir)."
    Stop-WinDrop $InstallRoot
} else {
    New-Item -ItemType Directory -Force (Split-Path $InstallRoot) | Out-Null
    Remove-Item $staging, $backup -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Kopyalanıyor: $SourceDir -> $staging"
    New-Item -ItemType Directory -Force $staging | Out-Null
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $staging -Recurse -Force
    foreach ($f in 'uninstall.ps1', 'firewall.ps1') {
        $p = Join-Path $PSScriptRoot $f
        if (Test-Path $p) { Copy-Item $p $staging -Force }
    }

    Stop-WinDrop $InstallRoot

    if (Test-Path $InstallRoot) {
        try { Rename-Item -LiteralPath $InstallRoot -NewName ($rootName + '.old') }
        catch {
            Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
            throw "Eski kurulum klasörü değiştirilemedi (dosyalar kilitli olabilir; WinDrop'u ve bu klasörü açan pencereleri kapatın): $($_.Exception.Message)"
        }
    }
    try { Rename-Item -LiteralPath $staging -NewName $rootName }
    catch {
        if (Test-Path $backup) { Rename-Item -LiteralPath $backup -NewName $rootName }   # eskiyi geri koy
        throw "Yeni sürüm yerine konamadı: $($_.Exception.Message)"
    }
    if (Test-Path $backup) {
        Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $backup) { Write-Warning "Eski sürüm klasörü silinemedi, elle silebilirsiniz: $backup" }
    }
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

$sendDesc = if ([System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'tr') { 'WinDrop ile gönder' } else { 'Send with WinDrop' }
New-Shortcut $StartMenuPath '' 'WinDrop'
if (-not $NoSendTo) { New-Shortcut $SendToPath '--send' $sendDesc }
else {
    # -NoSendTo: bu kuruluma ait eski SendTo kısayolu kalmasın (başka yola işaret edene dokunulmaz)
    $t = Get-ShortcutTarget $SendToPath
    if ($t -and (Test-UnderPath $t $InstallRoot)) {
        if ($DryRun) { Write-Host "[DryRun] SendTo kısayolu silinecekti: $SendToPath" }
        else { Remove-Item -LiteralPath $SendToPath -Force; Write-Host "SendTo kısayolu kaldırıldı: $SendToPath" }
    }
}

# ---- Kayıt defteri (HKCU): otomatik başlatma + "Uygulamalar ve özellikler" ----
if ($SkipRegistry) {
    Write-Host 'Kayıt defteri adımları atlandı (-SkipRegistry).'
} else {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $existingRun = (Get-ItemProperty -Path $runKey -Name 'WinDrop' -ErrorAction SilentlyContinue).WinDrop
    # Var olan değer (eski InstallRoot'a işaret edebilir) güncellemede yeni exe'ye yönlendirilir.
    if ($AutoStart -or $existingRun) {
        $cmd = "`"$exe`" --hidden"
        if ($DryRun) { Write-Host "[DryRun] HKCU Run WinDrop = $cmd" }
        else {
            Set-ItemProperty -Path $runKey -Name 'WinDrop' -Value $cmd
            Write-Host 'Otomatik başlangıç ayarlandı.'
        }
    }

    $uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinDrop'
    $uninstCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallRoot 'uninstall.ps1')`""
    if ($DryRun) { Write-Host "[DryRun] Uygulamalar ve özellikler kaydı: $uninstKey (Kaldır = $uninstCmd)" }
    else {
        $ver = try { (Get-Item $exe).VersionInfo.ProductVersion } catch { $null }
        New-Item -Path $uninstKey -Force | Out-Null
        $vals = @{
            DisplayName = 'WinDrop'; Publisher = 'WinDrop'; InstallLocation = $InstallRoot
            DisplayIcon = $exe; UninstallString = $uninstCmd
        }
        if ($ver) { $vals.DisplayVersion = $ver }
        foreach ($k in $vals.Keys) { Set-ItemProperty -Path $uninstKey -Name $k -Value $vals[$k] }
        foreach ($k in 'NoModify', 'NoRepair') { Set-ItemProperty -Path $uninstKey -Name $k -Value 1 -Type DWord }
    }
}

# ---- Güvenlik duvarı ----
if ($SkipFirewall) {
    Write-Host 'Güvenlik duvarı adımı atlandı (-SkipFirewall).'
} else {
    # Kural yalnız Private profil içindir; Public sayılan ağda gelen bağlantı engellenir (salt okunur kontrol).
    try {
        $public = @(Get-NetConnectionProfile -ErrorAction Stop | Where-Object { $_.NetworkCategory -eq 'Public' })
        if ($public.Count -gt 0) {
            Write-Warning ("Ağ profili 'Genel (Public)': {0}. Doğrudan Wi-Fi bağlantısı engellenebilir; ağı 'Özel (Private)' yapın (Ayarlar > Ağ ve İnternet) ya da yalnız Bluetooth/etkin nokta yolunu kullanın." -f (($public | ForEach-Object { $_.Name }) -join ', '))
        }
    } catch { }

    if ($DryRun) {
        Write-Host "[DryRun] Güvenlik duvarı: yükseltilmiş firewall.ps1 -Port $Port çalıştırılacaktı."
    } else {
        $fw = Join-Path $PSScriptRoot 'firewall.ps1'
        if (-not (Test-Path $fw)) { $fw = Join-Path $InstallRoot 'firewall.ps1' }
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')
        if ($isAdmin) { & $fw -Port $Port }
        else {
            Write-Host 'Güvenlik duvarı kuralları için yönetici onayı isteniyor...'
            try {
                $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList `
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$fw`"", '-Port', $Port
                if ($p.ExitCode -ne 0) { Write-Warning 'Güvenlik duvarı adımı başarısız; sonra yönetici olarak firewall.ps1 çalıştırın.' }
            } catch {
                # UAC iptal edildi / yükseltme yapılamadı: kurulum yine de tamamlanır
                Write-Warning "Güvenlik duvarı adımı atlandı (yönetici onayı verilmedi: $($_.Exception.Message)). Sonra yönetici olarak firewall.ps1 çalıştırın."
            }
        }
    }
}
Write-Host "Tamam. Kurulum: $InstallRoot"
