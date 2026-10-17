# WinDrop — derleme, kurma, kaldırma

## Derleme
```powershell
build\publish.ps1 -Version 0.1.0
```
Çıktı: `dist\WinDrop-win-x64\` (self-contained, win-x64, tek dosya değil) ve `dist\WinDrop-win-x64.zip`
(`app\` + `install.ps1` + `uninstall.ps1` + `firewall.ps1`). `dist\` gitignore'dadır.

## Kurma (kullanıcı başına)
Zip'i açın (ya da repoda `install\` içinden) ve çalıştırın:
```powershell
.\install.ps1 [-AutoStart] [-NoSendTo] [-SkipFirewall] [-InstallRoot <yol>] [-DryRun]
```
- Uygulama `%LOCALAPPDATA%\Programs\WinDrop` altına kopyalanır; Başlat menüsü ve SendTo (`WinDrop.lnk`, `--send`) kısayolları eklenir.
- `-AutoStart`: `HKCU\...\Run` altına `"<exe>" --hidden` yazar.
- Yönetici yalnız güvenlik duvarı için gerekir: yönetici değilseniz betik yalnız `firewall.ps1`'i UAC ile yükseltir.
  Kurallar porta bağlıdır (TCP 53600: Private + `192.168.137.0/24` tüm profiller), exe yoluna değil.
- `-DryRun`: Başlat menüsü/SendTo/Run kaydına ve güvenlik duvarına yazmaz, yalnız yazdırır (kopyalama `-InstallRoot`'a olur).

## Kaldırma
```powershell
%LOCALAPPDATA%\Programs\WinDrop\uninstall.ps1 [-RemoveData] [-SkipFirewall] [-DryRun]
```
Kısayolları, Run kaydını, güvenlik duvarı kurallarını ve kurulum klasörünü siler. Kullanıcı verisi
(`%LOCALAPPDATA%\WinDrop`: ayarlar, eşleşmeler, günlük) yalnız `-RemoveData` ile silinir.
Eski NetProbe test kuralları için: `install\remove-test-rules.ps1`.
