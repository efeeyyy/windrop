# Sabah test listesi (Efe için) — 2026-10-18

Gece yalnız bilgisayarda doğrulanabilenler yapıldı. Aşağıdakiler gerçek iPhone/iPad ve senin
ellerini gerektiriyor. Sırayla git; her adımda sonucu (çalıştı / hata mesajı) yaz.

## 0. Hazırlık (5 dk)
1. Windows **Mobil etkin nokta** gece açık kaldı (SSID "EFEY 9308") — kapat.
2. Ekranda açık bir Windows Güvenlik Duvarı izin penceresi kalmışsa kapat (gece arayüz testinden).
3. Windows uygulamasını kur (yönetici gerekmez; güvenlik duvarı adımı için izin penceresi çıkar → Evet):
   ```powershell
   & "C:\Users\Efe\Desktop\WinDrop\install\remove-test-rules.ps1"
   Expand-Archive C:\Users\Efe\Desktop\WinDrop\dist\WinDrop-win-x64.zip -DestinationPath $env:TEMP\windrop-setup -Force
   & "$env:TEMP\windrop-setup\install.ps1"
   ```
   (`remove-test-rules.ps1` yönetici PowerShell ister.) Ayrıntı: `docs/INSTALL.md`.
4. Etkin nokta API testi (etkin nokta kapalıyken, ~15 sn; eski ayarı geri yükler):
   ```powershell
   dotnet run --project C:\Users\Efe\Desktop\WinDrop\windows\src\WinDrop.PlatformCheck -- hotspot-cycle
   ```
5. AirPods bağlı ve müzik çalarken (Bluetooth ses algısı gerçek donanımda hiç denenmedi):
   ```powershell
   dotnet run --project C:\Users\Efe\Desktop\WinDrop\windows\src\WinDrop.PlatformCheck -- bt-audio 10
   ```
   Beklenen: `btAudio=True`.

## 1. iOS uygulamasını yükle
- IPA hazır: `C:\Users\Efe\Desktop\WinDrop\dist\WinDrop-unsigned.ipa` → Sideloadly ile ücretsiz Apple
  hesabınla iPhone'a (ve iPad'e) yükle → Ayarlar > Genel > VPN ve Cihaz Yönetimi'nde geliştiriciye güven.
  Yeni sürüm: GitHub Actions > son "iOS" koşusu > "WinDrop-unsigned-ipa".

## 2. (İsteğe bağlı) Aktarma sunucusu
İzolasyonlu ağlar için. Cloudflare hesabı senin: `relay\README.md` adımları (`npx wrangler login`,
`npx wrangler deploy`), çıkan `https://...workers.dev` adresini Windows ve iOS Ayarlar'ında
"Aktarma sunucusu adresi"ne gir. Girmezsen aktarma yolu kapalı kalır.

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
| 12 | AirPods'u çıkar, Wi-Fi'ı kapat, 50 KB'lık dosya gönder | Bluetooth üzerinden (yavaş ama) gider |
| 13 | (Sunucu kurulduysa) farklı ağlarda gönder | "Bu gönderim Wi-Fi veya hücresel veri kotanızdan gidecektir" onayı; sonra aktarım |
| 14 | PC'den bir klasörü (alt klasörlü, Türkçe adlı) sürükle-bırakla gönder; iPhone'dan Dosyalar'da bir klasör gönder | Klasör yapısı aynen gelir |

## 4. Bilinen eksikler
- Push bildirimleri (uygulama zorla kapatılmışken): kodu hazır, ücretli Apple hesabı + APNs anahtarıyla
  `Release-Push` derlemesi ve sunucu sırları girilince açılır (`relay/README.md`).
- iOS kodu Mac'te derleniyor ama hiçbir cihazda çalıştırılmadı; Bluetooth ve arka plan davranışları
  ilk kez bu testlerde görülecek.
