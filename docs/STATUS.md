# STATUS — devir notu

Son güncelleme: 2026-11-18. Yeni oturum önce bunu, sonra `docs/review/DECISIONS.md`, gerekirse
`ARCHITECTURE.md` / `PROTOCOL.md` okur. Ayrıntılı geçmiş git log'da.

## Ne bu proje
Windows 11 ↔ iPhone/iPad, AirDrop benzeri dosya aktarımı. BLE yalnız keşif/sinyal; veri doğrudan TCP
(aynı ağ) → PC Mobil etkin noktası (iPhone Wi-Fi'sız) → BLE (≤64 KB, kulaklık yokken) → Cloudflare
aktarma sunucusu (izolasyonlu ağ, veri kotası onayıyla). Uçtan uca Noise (IK / XX / XXpsk3).
UX (Efe): kurulumsuz, eşleştirme isteğe bağlı (XX + alıcı onayı + 4 haneli kod), izinler yalnız "Gönder"de,
gelen medya Galeri'ye en yeni tarihle, paylaşım menüsü rehberi, tema Sistem/Açık/Koyu, TR + EN.

## Kod ve doğrulama (2026-11-18)
| Parça | Yer | Durum |
|---|---|---|
| C# çekirdek | `windows/src/WinDrop.Core` | 340 test geçiyor |
| Windows servisleri | `windows/src/WinDrop.Platform` | 211 test geçiyor |
| Windows uygulaması (WPF) | `windows/src/WinDrop.App` | Derleniyor, 0 uyarı |
| Swift çekirdek | `ios/WinDropKit` | 113 test (SwiftBox WSL) |
| iOS uygulaması + uzantı | `ios/App` (XcodeGen) | CI yeşil (run 44389711940), IPA `dist/WinDrop-unsigned.ipa` + Release `v0.3.0` (1,56 MB, 18 Kas) |
| Aktarma/push sunucusu | `relay/` | 52 test; Cloudflare'e henüz kurulmadı |
| Çapraz uyum | `windows/tools/WinDrop.Interop` | TCP 6/6 + relay 3/3 PASS |
| CI | `.github/workflows/{ios,interop,windows,relay}.yml` + `ios-release.yml` (elle) | Yol filtreli + concurrency; hepsi yeşil. macOS arm64 kapasite kısıtında iş atanmayıp iptal olabilir → `gh run rerun <id> --failed` |

Komutlar:
- Windows: `dotnet test windows\WinDrop.slnx` (dotnet: `C:\Program Files\dotnet\dotnet.exe`)
- Swift: `wsl -d SwiftBox -u root -- bash -lc ". ~/.local/share/swiftly/env.sh; cd /mnt/c/Users/Efe/Desktop/WinDrop/ios/WinDropKit && swift test"` (önbellek bozuksa `--scratch-path /root/wdbuild`)
- Çapraz: `wsl -d SwiftBox -u root -- bash -lc "bash /mnt/c/Users/Efe/Desktop/WinDrop/windows/tools/WinDrop.Interop/run-interop.sh"`
- Windows paket: `build\publish.ps1 -Version x.y.z` → `dist\WinDrop-win-x64.zip`
- iOS çeviri denetimi: `py -3 ios\App\Localization\check-xcstrings.py`
- iOS Release (yeniden derlemeden, son başarılı iOS CI IPA'sı): `gh workflow run ios-release.yml -f version=x.y.z`

## ŞU AN YARIM (2026-11-18) — ilk iş bunlar
1. **Cihaz denemesi (Efe):** Windows 0.3.0 bu PC'de kurulu. iOS 0.3.0: GitHub Release `v0.3.0` (`8f77917`) ya da
   `dist\WinDrop-unsigned.ipa` (aynı dosya, SHA256 `7e5c4e9e…`). IPA artık App Group yetkisini ad-hoc imzayla
   taşıyor → Sideloadly/SideStore grubu görür, uygulama ile paylaşım uzantısı tek kimlik olmalı (Ayarlar > Tanılama
   "Ortak kimlik"). Denenecek: iki yönde aktarım, paylaşım menüsünden gönderim, PC teklif penceresi (odak çalmıyor,
   60 sn'de ret; ses/yanıp sönme yok).
2. **SideStore'a geçiş (Efe kararı: önce dene):** Kurulum yolu iloader (Windows) + iPhone'da LocalDevVPN; sonra
   yenileme bilgisayarsız. SideStore paket kimliğini değiştirebilir (`.TEAMID` eki) → PC iPhone'u yeni cihaz görür,
   yeniden tanışma gerekir. Grup adı `group.<yeni kimlik>` olur; `SharedGroup` bunu dener. Doğrulanmadı: uzantı ve
   grubun SideStore'da gerçekten paylaşılması, eduroam'da yenileme, kararlı 0.6.x'te oturum açma sorunu (alpha/nightly
   gerekebilir). Ücretsiz hesap: SideStore dahil 3 uygulama, haftada 10 App ID (uzantı da sayılıyor olabilir).
3. **Kod–belge uyuşmazlıkları (belge değişmedi, karar gerek):** `protocol-consistency.md` U12 (iOS `rate` ↔
   belge `rate_limited`), U13 (`caps.relay` gönderilmiyor), U14 (`relay` yalnız PC→iPhone), U25 (`btAudio` ile
   BLE kesmesi yalnız PC'de), U22 (klasör kuralları platformlar arası farklı).
4. Ertelenen inceleme maddeleri: `windows-core-fixes.md`, `windows-app-fixes.md`, `ios-app-fixes.md` sonundaki
   "ertelendi" listeleri.
5. Karar bekleyen: iOS "eşli bilgisayarlardan otomatik kabul" varsayılanı açık (inceleme riskli buldu; Efe'ye soruldu).
6. Ortam: bu makinede git `user.name`/`user.email` ayarlı değil; commit için `git -c user.name=... -c user.email=...`
   (önceki commit'lerdeki kimlik) kullanıldı. `fix/windows-app-review` dalı birleştirildi, silinmedi.

Yapıldı (15–18 Kas): Windows inceleme düzeltmeleri main'de; PROTOCOL.md yeniden düzenlendi ve tüm kod/belge `§`
atıfları yeni numaralarda (kalan yalnız `docs/review/` tarihsel raporları: `docs/review/protocol-section-refs.txt`);
RollingFileLog kararsız testi düzeltildi; iOS IPA'ya yetki gömme; `ios-release.yml`.

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
