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
1. **Tamam (18 Kas):** `fix/windows-app-review` main'e birleştirildi (hızlı ileri, `00f18ba`). İçerik: Windows
   inceleme düzeltmeleri (Core+Platform+App+kurulum, `docs/review/windows-app-fixes.md`) + yeniden düzenlenmiş `PROTOCOL.md`.
2. **Birlikte güncelleme:** Windows **0.3.0 bu PC'ye kuruldu** (15 Kas; `dist\WinDrop-win-x64.zip`). Yeni sürümde
   `QUIT` ile zarif kapanma canlı doğrulandı. Sıra Efe'de: `dist\WinDrop-unsigned.ipa`'yı Sideloadly ile yükle
   (ücretsiz hesap: 7 günde bir yeniden yükleme), sonra iki yönde aktarım + teklif penceresini dene
   (pencere odak çalmıyor; 60 sn'de fark edilmezse ret — ses/yanıp sönme yok, gerekirse eklenir).
3. **PROTOCOL.md `§` atıfları:** bölüm numaraları değişti (eski → yeni: PROTOCOL.md Ek A). Kod yorumları ve
   diğer belgelerdeki ~350 atıf eski numarayı taşıyor: `docs/review/protocol-section-refs.txt`. Mekanik
   düzeltme; `ios/` dosyalarına dokunmak macOS CI dakikası harcar — iOS değişikliğiyle birlikte yap.
4. **Kod–belge uyuşmazlıkları (belge değişmedi, karar gerek):** `protocol-consistency.md` U12 (iOS `rate` ↔
   belge `rate_limited`), U13 (`caps.relay` gönderilmiyor), U14 (`relay` yalnız PC→iPhone), U25 (`btAudio` ile
   BLE kesmesi yalnız PC'de), U22 (klasör kuralları platformlar arası farklı).
5. Ertelenen inceleme maddeleri: `windows-core-fixes.md`, `windows-app-fixes.md`, `ios-app-fixes.md` sonundaki
   "ertelendi" listeleri.
6. Karar bekleyen: iOS "eşli bilgisayarlardan otomatik kabul" varsayılanı açık (inceleme riskli buldu; Efe'ye soruldu).
7. Ortam: bu makinede git `user.name`/`user.email` ayarlı değil; commit için `git -c user.name=... -c user.email=...`
   (önceki commit'lerdeki kimlik) kullanıldı.

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
