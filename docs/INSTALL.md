# WinDrop — derleme, kurma, güncelleme, kaldırma

## Gereksinimler
- **Windows 11 22H2 (derleme 22621) veya üstü**, 64 bit (x64). Uygulama `net10.0-windows10.0.22621.0` hedefler;
  Windows 10'da açılmaz. .NET çalışma zamanı paketin içindedir (self-contained), ayrıca kurmanız gerekmez.
- Bluetooth 4.0+ (LE) bağdaştırıcısı açık olmalı (iPhone'u yakında bulmak ve küçük dosyalar için). Wi-Fi/Ethernet doğrudan aktarım içindir.
- Güvenlik duvarı kuralı için bir kerelik yönetici onayı (UAC) gerekir; uygulamanın kendisi yöneticisiz çalışır.

## Derleme
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1 -Version 0.1.0
```
Çıktı: `dist\WinDrop-win-x64\` (self-contained, win-x64, tek dosya değil), `dist\WinDrop-win-x64.zip`
(`app\` + `install.ps1` + `uninstall.ps1` + `firewall.ps1` + `INSTALL.md`) ve `dist\WinDrop-win-x64.zip.sha256`.
Derleme başarısız olursa önceki `dist\` korunur. `dist\` gitignore'dadır.
İndirdiğiniz zip'in bütünlüğünü doğrulamak için: `Get-FileHash .\WinDrop-win-x64.zip -Algorithm SHA256` çıktısı `.sha256` dosyasıyla aynı olmalı.

## Kurma (kullanıcı başına)
1. Zip'i bir klasöre açın (ya da repoda `install\` içinden çalıştırın).
2. **PowerShell'i** o klasörde açın ve çalıştırın:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 [-AutoStart] [-NoSendTo] [-SkipFirewall] [-InstallRoot <yol>] [-DryRun]
   ```
   `.\install.ps1` yazmak çoğu bilgisayarda **çalışmaz**: varsayılan ExecutionPolicy (Restricted) betik çalıştırmayı kapatır ve
   internetten indirilen zip'teki dosyalar "İnternetten indirildi" (Mark of the Web) işaretini taşır. Yukarıdaki komut yalnız bu
   çalıştırma için ilkeyi aşar (kalıcı ayar değiştirmez). İsterseniz önce `Get-ChildItem -Recurse | Unblock-File` ile işareti kaldırabilirsiniz.
3. **SmartScreen uyarısı:** exe imzasızdır; ilk açılışta "Windows bilgisayarınızı korudu" çıkabilir. "Ek bilgi → Yine de çalıştır" deyin
   (yalnız kendi derlemenizden/güvendiğiniz kaynaktan aldıysanız; zip'in SHA-256'sını doğrulayın).

- Uygulama `%LOCALAPPDATA%\Programs\WinDrop` altına kopyalanır; Başlat menüsü ve SendTo (`WinDrop.lnk`, `--send`) kısayolları eklenir;
  "Ayarlar → Uygulamalar" listesine **WinDrop** kaydı (Kaldır düğmesi `uninstall.ps1`'i çalıştırır) yazılır.
- `-AutoStart`: `HKCU\...\Run` altına `"<exe>" --hidden` yazar (pencere açmadan tepside başlar).
- Güvenlik duvarı: yönetici değilseniz betik yalnız `firewall.ps1`'i UAC ile yükseltir (onay vermezseniz kurulum yine tamamlanır, uyarı verilir;
  `firewall.ps1`'i sonra yönetici olarak çalıştırabilirsiniz). Kurallar porta bağlıdır (TCP 53600: **Private** profil + `192.168.137.0/24` tüm profiller), exe yoluna değil.
  Ağınız Windows'ta **Genel (Public)** görünüyorsa doğrudan Wi-Fi bağlantısı engellenir; betik bunu uyarır. Ağı "Özel" yapın ya da yalnız Bluetooth/etkin nokta yolunu kullanın.
  Uygulama veri portunu (53600) sabit kullanır; `-Port` yalnız güvenlik duvarı kuralını değiştirir, varsayılanı bırakın.
- `-DryRun`: **hiçbir şeyi değiştirmez** — çalışan uygulamayı kapatmaz, dosya kopyalamaz, kısayol/kayıt/güvenlik duvarına yazmaz; yalnız yapacaklarını yazdırır.

## Güncelleme
Yeni zip'i açıp `install.ps1`'i **aynı komutla yeniden çalıştırın**. Betik yeni dosyaları önce `...\WinDrop.new` klasörüne kopyalar, çalışan WinDrop'u
uygulamanın kendi "kapat" isteğiyle (tek örnek borusu) zarifçe kapatır (12 sn içinde kapanmazsa son çare sonlandırır), sonra klasörü değiştirir; eski
sürümden artık dosya kalmaz. Ayarlarınız, eşleşmeleriniz ve günlük (`%LOCALAPPDATA%\WinDrop`) korunur. Başlat/SendTo kısayolları ve (varsa) otomatik başlatma kaydı yeni konuma yönlendirilir.

## Kaldırma
"Ayarlar → Uygulamalar → WinDrop → Kaldır" ya da:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\WinDrop\uninstall.ps1" [-RemoveData] [-SkipFirewall] [-DryRun]
```
Çalışan uygulamayı kapatır; kısayolları, Run kaydını, "Uygulamalar" kaydını, güvenlik duvarı kurallarını ve kurulum klasörünü siler. `-InstallRoot` verirseniz
yalnız **o klasöre işaret eden** kısayol/kayıtlar silinir. Kullanıcı verisi (`%LOCALAPPDATA%\WinDrop`: ayarlar, eşleşmeler, günlük) yalnız `-RemoveData` ile silinir.
Eski NetProbe test kuralları için: `install\remove-test-rules.ps1`.

## Sorun giderme
- Günlük: `%LOCALAPPDATA%\WinDrop\logs\app.log` (uygulamada durum bandındaki "Günlüğü aç" düğmesi de açar).
- Ayar dosyası (`%LOCALAPPDATA%\WinDrop\settings.json`) bozulursa uygulama `settings.json.corrupt` olarak yedekler ve varsayılanlarla açılır; üzerine sessizce yazmaz.
- "Yakında cihaz yok": iki cihazda da Bluetooth açık olmalı; iPhone'da WinDrop ön planda olmalı. Servisler başlamazsa ana pencerede uyarı çıkar.
