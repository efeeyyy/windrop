# STATUS — devir notu

Son güncelleme: 2026-11-15. Yeni oturum önce bunu, sonra `docs/review/DECISIONS.md`, gerekirse
`ARCHITECTURE.md` / `PROTOCOL.md` okur. Ayrıntılı geçmiş git log'da.

## Ne bu proje
Windows 11 ↔ iPhone/iPad, AirDrop benzeri dosya aktarımı. BLE yalnız keşif/sinyal; veri doğrudan TCP
(aynı ağ) → PC Mobil etkin noktası (iPhone Wi-Fi'sız) → BLE (≤64 KB, kulaklık yokken) → Cloudflare
aktarma sunucusu (izolasyonlu ağ, veri kotası onayıyla). Uçtan uca Noise (IK / XX / XXpsk3).
UX (Efe): kurulumsuz, eşleştirme isteğe bağlı (XX + alıcı onayı + 4 haneli kod), izinler yalnız "Gönder"de,
gelen medya Galeri'ye en yeni tarihle, paylaşım menüsü rehberi, tema Sistem/Açık/Koyu, TR + EN.

## Kod ve doğrulama (2026-11-15)
| Parça | Yer | Durum |
|---|---|---|
| C# çekirdek | `windows/src/WinDrop.Core` | 340 test geçiyor |
| Windows servisleri | `windows/src/WinDrop.Platform` | 211 test geçiyor |
| Windows uygulaması (WPF) | `windows/src/WinDrop.App` | Derleniyor, 0 uyarı |
| Swift çekirdek | `ios/WinDropKit` | 113 test (SwiftBox WSL) |
| iOS uygulaması + uzantı | `ios/App` (XcodeGen) | CI yeşil (run 43595692985), IPA `dist/WinDrop-unsigned.ipa` (1,55 MB, 14 Kas) |
| Aktarma/push sunucusu | `relay/` | 52 test; Cloudflare'e henüz kurulmadı |
| Çapraz uyum | `windows/tools/WinDrop.Interop` | TCP 6/6 + relay 3/3 PASS |
| CI | `.github/workflows/{ios,interop,windows,relay}.yml` | Yol filtreli + concurrency; hepsi yeşil |

Komutlar:
- Windows: `dotnet test windows\WinDrop.slnx` (dotnet: `C:\Program Files\dotnet\dotnet.exe`)
- Swift: `wsl -d SwiftBox -u root -- bash -lc ". ~/.local/share/swiftly/env.sh; cd /mnt/c/Users/Efe/Desktop/WinDrop/ios/WinDropKit && swift test"` (önbellek bozuksa `--scratch-path /root/wdbuild`)
- Çapraz: `wsl -d SwiftBox -u root -- bash -lc "bash /mnt/c/Users/Efe/Desktop/WinDrop/windows/tools/WinDrop.Interop/run-interop.sh"`
- Windows paket: `build\publish.ps1 -Version x.y.z` → `dist\WinDrop-win-x64.zip`
- iOS çeviri denetimi: `py -3 ios\App\Localization\check-xcstrings.py`

## ŞU AN YARIM (2026-11-15) — ilk iş bunlar
1. **Commit edilmemiş Windows değişiklikleri (48 dosya):** inceleme düzeltmeleri — Core+Platform
   (`docs/review/windows-core-fixes.md`, tamam) + App/kurulum (`docs/review/windows-app.md` bulguları;
   ajan durduruldu, `windows-app-fixes.md` YAZILMADI). Derleme 0 uyarı, testler geçiyor. Yapılacak:
   `git diff` ile App/kurulum kısmını gözden geçir (teklif penceresinde Kabul varsayılan değil + odak
   çalmıyor, install/uninstall düzgün kapatma, BOM'lu settings.json, erişilebilirlik, Ayarlar kaydırma,
   INSTALL.md) → eksikleri tamamla → commit → push → `windows.yml` yeşil.
2. **Birlikte güncelleme:** iOS yeni sürümü (WDU işareti, zaman aşımları) yeni Windows sürümünü bekliyor.
   Windows'u kur, sonra Efe IPA'yı Sideloadly ile yükler (ücretsiz hesap: 7 günde bir yeniden yükleme).
3. **PROTOCOL.md yeniden düzenleme:** `docs/review/protocol-consistency.md` sonundaki başlık planı +
   `DECISIONS.md` maddeleri spesifikasyona işlenecek; eskimiş §9 açık sorular, §2/§2b çelişkisi temizlenecek.
4. Ertelenen inceleme maddeleri: `windows-core-fixes.md` ve `ios-app-fixes.md` içindeki "ertelendi" listeleri.
5. Karar bekleyen: iOS "eşli bilgisayarlardan otomatik kabul" varsayılanı açık (inceleme riskli buldu; Efe'ye soruldu).

## Kurulum / ortam notları (önemli)
- **Claude kabuğu MSIX sanallaştırması altında:** `%LOCALAPPDATA%` okuma/yazma sanal kopyaya gidebilir.
  Gerçek günlük/ayar okumak ve kurulum için `Invoke-CimMethod Win32_Process Create` ile süreç başlat;
  örnek: kur → `dist\setup\install.ps1 -SkipFirewall` bu yolla, sonra uygulamayı aynı yolla başlat.
  Gerçek günlük: `C:\Users\Efe\AppData\Local\WinDrop\logs\app.log`.
- Yeniden kurulumda çalışan `WinDrop.App` kapatılmalı (Stop-Process); Efe'ye haber ver.
- iPhone kurulum: Sideloadly + iTunes **web** sürümü + Apple Mobile Device Support (Store sürümü çalışmaz).
- SwiftBox WSL: `C:\Users\Efe\WSL\swiftbox` (imaj `C:\Users\Efe\WSL\images\` silinebilir).
- GitHub: `efeeyyy/windrop` (özel). macOS CI dakikaları sınırlı — yalnız iOS değişince derle.
  `sideloadlydaemon.log` (UDID içerir) git geçmişinde duruyor; depo açılacaksa geçmiş temizlenmeli.
- Güvenlik duvarı: ürün kuralları kurulu ("WinDrop data", "WinDrop hotspot data").

## Cihazda doğrulananlar (Efe, iPhone + bu PC, 18–19 Eki)
BLE keşif, iki yönde aktarım (~10 MB/s eduroam), etkin nokta yolu + 2 dk otomatik kapanma + ayar geri
yükleme, PC yeniden başlayınca otomatik yeniden bağlanma, QR eşleştirme, Galeri kaydı, küçük resimler,
yol değişimi (Wi-Fi açılınca doğrudan yola geçiş).
Henüz denenmedi: AirPods çalarken gönderim, paylaşım menüsünden gönderim (tek kimlik), arka planda
bildirim, 500 MB+ video, aktarım ortasında Wi-Fi kopması/devam, BLE küçük dosya, iPad, İngilizce arayüz.

## Kullanıcıya bağlı / ileride
- Aktarma sunucusunu Cloudflare'e kurmak (`relay/README.md`, Efe'nin hesabı).
- Push + otomatik Wi-Fi katılma + 7 gün sınırının kalkması: ücretli Apple hesabı.
- İmzalı Windows yükleyici (MSIX/Inno), LICENSE seçimi.
