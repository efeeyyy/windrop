# Sabah test listesi (Efe için) — 2026-10-17

Gece yalnız bilgisayarda doğrulanabilenler yapıldı. Aşağıdakiler gerçek iPhone/iPad ve senin
ellerini gerektiriyor. Sırayla git; her adımda sonucu (çalıştı / hata mesajı) yaz.

## 0. Hazırlık (5 dk)
1. Windows **Mobil etkin nokta** gece açık kaldı (SSID "EFEY 9308") — kapat.
2. Yönetici PowerShell'de eski test kurallarını kaldır, ürün kurallarını kur:
   ```powershell
   & "C:\Users\Efe\Desktop\WinDrop\install\remove-test-rules.ps1"
   & "C:\Users\Efe\Desktop\WinDrop\install\install.ps1"
   ```
3. Etkin nokta API testi (etkin nokta kapalıyken, ~15 sn; eski ayarı geri yükler):
   ```powershell
   dotnet run --project C:\Users\Efe\Desktop\WinDrop\windows\src\WinDrop.PlatformCheck -- hotspot-cycle
   ```

## 1. iOS uygulamasını yükle
- GitHub Actions'ın ürettiği `WinDrop-unsigned.ipa`'yı indir (bağlantı STATUS.md'de) →
  Sideloadly ile ücretsiz Apple hesabınla iPhone'a yükle → Ayarlar > Genel > VPN ve Cihaz Yönetimi'nde
  geliştiriciye güven. (CI ilk derlemede hata verirse bu adım bekler; STATUS.md'de yazar.)

## 2. Windows uygulaması
```powershell
dotnet run --project C:\Users\Efe\Desktop\WinDrop\windows\src\WinDrop.App
```
Pencere açılmalı; günlük: `%LOCALAPPDATA%\WinDrop\logs\app.log`.

## 3. Asıl testler (iPhone eduroam'da, PC eduroam'da)
| # | Test | Beklenen |
|---|---|---|
| 1 | iPhone'da WinDrop'u aç | Hiçbir izin/kurulum sorulmaz |
| 2 | "Dosya gönder" → bir fotoğraf | Bluetooth izni şimdi sorulur; PC birkaç sn içinde belirir |
| 3 | PC'ye dokun | PC'de onay penceresi, iki ekranda aynı 4 haneli kod; Kabul → dosya `Downloads\WinDrop`'ta |
| 4 | 500 MB+ video gönder | Hız (MB/s) ve süreyi not et |
| 5 | PC'den iPhone'a gönder (uygulama açık) | iPhone'da onay sayfası → Fotoğraflar'a kaydet |
| 6 | Uygulamayı arka plana al (kaydırıp KAPATMA), PC'den gönder | iPhone'da bildirim; dokununca aktarım |
| 7 | Fotoğraflar → Paylaş → WinDrop | Uygulamayı açmadan PC belirir, gönderim |
| 8 | AirPods'ta müzik çalarken 3 | Müzik kesilmemeli (veri BLE'den gitmiyor) |
| 9 | iPhone Wi-Fi kapalı (mobil veri açık), PC'den/PC'ye gönder | PC etkin nokta açar; ilk sefer "bir kereye mahsus" katılma ekranı; sonra otomatik |
| 10 | Aktarım ortasında iPhone Wi-Fi'ını kapat/aç | Kaldığı yerden devam |
| 11 | iPad ile 3 ve 5 | Aynı |

## 4. Bilinen eksikler (bilerek bırakıldı)
- Aktarma (internet) yolu ve push bildirimleri yok (push ücretli hesap ister).
- Bluetooth ses algısı yok; küçük dosyalar için BLE veri yolu yok.
- iOS kodu Mac'te hiç derlenmedi; ilk CI derlemesinde tip hataları çıkabilir.
