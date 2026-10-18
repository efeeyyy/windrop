# STATUS — devir notu

Son güncelleme: 2026-10-17 (gece çalışması sonu). Yeni oturum bunu + `ARCHITECTURE.md` + `PROTOCOL.md` okur.

## Durum (özet)
- Ölçümler bitti (ARCHITECTURE "Ölçüm bulguları"): BLE yalnız keşif/sinyal (ses kesiliyor);
  eduroam'da doğrudan TCP çalışıyor (8/16 MB/s); ağsız durumda Windows Mobil etkin nokta (2,3/3,9 MB/s).
- UX kararı (Efe): kurulumsuz, AirDrop gibi; eşleştirme zorunlu değil (XX + alıcı onayı + 4 haneli kod);
  iOS paylaşım uzantısı; arka planda tanıdık PC'lere BLE; push ileride ücretli hesapla.
- Protokol: `PROTOCOL.md` v0.1 + §2b + §9a. İki opus-high incelemesi (spesifikasyon + kod) yapıldı,
  bulguların hepsi düzeltildi.

## Kod
| Parça | Yer | Doğrulama (2026-10-17) |
|---|---|---|
| C# çekirdek (Noise, çerçeve, aktarım) | `windows/src/WinDrop.Core` | 266 test geçti; 59 cacophony vektörü |
| Windows servisleri (BLE GATT, TCP 53600, etkin nokta, kimlik) | `windows/src/WinDrop.Platform` | 116 test geçti |
| Windows uygulaması (WPF Fluent, tepsi, SendTo) | `windows/src/WinDrop.App` | Derleniyor, açılıyor, BLE reklam + dinleyici logda |
| Swift çekirdek | `ios/WinDropKit` | 82 test geçti (SwiftBox WSL, swift-crypto) |
| iOS uygulaması + paylaşım uzantısı | `ios/App` (XcodeGen) | Mac'te (CI) derleniyor; cihazda denenmedi |
| C# ↔ Swift çapraz uyum | `windows/tools/WinDrop.Interop`, `docs/INTEROP.md` | 6/6 senaryo PASS (TCP; BLE çapraz değil) |
| CI | `.github/workflows/ios.yml` | Yeşil (run 39067587278); imzasız IPA dist\WinDrop-unsigned.ipa (823 KB, paylaşım uzantısı dahil) |
| Ölçüm araçları | `windows/src/WinDrop.{BleProbe,NetProbe,ApProbe,PlatformCheck}` | — |

Testleri çalıştırma:
- Windows: `dotnet test windows\WinDrop.slnx`
- Swift: `wsl -d SwiftBox -u root -- bash -lc ". ~/.local/share/swiftly/env.sh; cd /mnt/c/Users/Efe/Desktop/WinDrop/ios/WinDropKit && swift test"`
- Çapraz: `wsl -d SwiftBox -u root -- bash -lc "bash /mnt/c/Users/Efe/Desktop/WinDrop/windows/tools/WinDrop.Interop/run-interop.sh"`

## Ortam notları
- "SwiftBox" WSL dağıtımı (Ubuntu 24.04, root, Swift 6.4.1 + .NET 10) `C:\Users\Efe\WSL\swiftbox`;
  kaynak imaj `C:\Users\Efe\WSL\images\` (371 MB, silinebilir). Efe'nin "Ubuntu" dağıtımında kullanıcı
  alanına deneme Swift'i kuruldu (`~/.local/share/swiftly`, `~/swift-*`) — kullanılmıyor, silinebilir.
- Efe'nin Windows Mobil etkin noktası gece açık kaldı (dokunulmadı).
- Test güvenlik duvarı kuralları duruyor → `install\remove-test-rules.ps1`.
- GitHub: github.com/efeeyyy/windrop (özel). iOS derlemesi her push'ta; IPA artifact "WinDrop-unsigned-ipa".

## 2026-10-18 gece ikinci tur
- Bluetooth ses algısı (Windows Core Audio, iOS AVAudioSession) + küçük dosya BLE yolu (PC `bleData` ile başlatır, §6.4a).
- Aktarma sunucusu `relay/` (Cloudflare Worker + DO, 22 test) + iki uygulamada istemci ve veri kotası onayı;
  C#↔Swift aktarma çapraz testi macOS CI'da yeşil (`.github/workflows/interop.yml`, run 39070933870).
- Windows kurulum paketi: `build/publish.ps1` → `dist/WinDrop-win-x64.zip`; `install/install.ps1` (yönetici gerekmez).
- Son sayılar: C# 458 test (Core 306, Platform 152), Swift 95, interop TCP 6/6 + relay 3/3, iOS CI yeşil.

- 2026-10-18 gerçek makine: `PlatformCheck hotspot-cycle` geçti — etkin nokta WinDrop SSID'siyle açıldı
  (2,4 GHz), 10 sn sonra kapandı, Efe'nin eski ayarı (SSID/bant/parola) birebir geri yüklendi.

- 2026-10-18 üçüncü tur: klasör gönderme (§5a, iki taraf), push bildirimi (§6.5): sunucu `relay/src/push.ts`
  (43 test), Windows istemcisi, iOS `Release-Push` yapılandırması (`PUSH_ENABLED`) — CI'da derleniyor, ücretli
  hesap + APNs anahtarı olmadan kapalı. macOS CI gerçek bir hata yakaladı ve düzeltildi: APFS adları NFD
  sakladığı için Türkçe uzun adlar 255 baytı aşıyordu.
- Son sayılar: C# 507 (Core 337, Platform 170), Swift 110, relay 43, interop TCP 6/6 + relay 3/3;
  iOS CI run 39073625313 yeşil (Release + Release-Push), IPA `dist/WinDrop-unsigned.ipa` (958 KB),
  Windows `dist/WinDrop-win-x64.zip`.

## 2026-10-18 akşam: ilk gerçek cihaz testleri (Efe, iPhone + bu PC)
Çalışanlar: Sideloadly kurulumu (iTunes web sürümü + Apple Mobile Device Support gerekti); BLE keşif;
iPhone→PC ve PC→iPhone aktarım (eduroam doğrudan TCP, ~10 MB/s); etkin nokta yolu (iPhone Wi-Fi'sız,
ilk katılma elle; 2 dk sonra otomatik kapanma ve ayar geri yükleme doğrulandı); PC yeniden başlayınca
iPhone'un kendiliğinden yeniden bağlanması; QR eşleştirme; Galeri'ye kaydetme; küçük resimler.
Cihazda bulunup düzeltilenler: Windows GATT yanıtsız yazmaları düşürüyor (iOS tüm yazmalar onaylı);
oturum senkron kaybı (WDR + oturum ortasında ön ek = yeniden başlat); iOS'ta peripheral başına tek oturum;
yerel ağ izni yarışı; WPF .ico biçimi; arayüzde koşulsuz otomatik kabul (güvenlik); uygulama ve uzantının
ayrı kimlikleri (App Group ile tek kimlik — ücretsiz hesapta imza riski); iOS cihaz adı okunamıyor (ayar).
Not: Claude kabuğu MSIX sanallaştırması altında; `%LOCALAPPDATA%` okuması/yazması için
`Win32_Process.Create` ile sanallaştırma dışı süreç kullan (bkz. bu oturumun kurulum komutları).

## Açık işler (öncelik sırası)
1. Sabah testleri: `docs/MORNING-TESTS.md`.
2. Aktarma sunucusunu Efe'nin Cloudflare hesabına kurmak (`relay/README.md`).
3. Push'u açmak: ücretli Apple hesabı + APNs anahtarı (`relay/README.md`), `Release-Push` ile derleme.
4. Etkin nokta parolası yenilenirken süren başka etkin nokta aktarımı kesilebilir (bilinen risk).
5. Uygulama simgesi/marka; MSIX veya imzalı yükleyici.
