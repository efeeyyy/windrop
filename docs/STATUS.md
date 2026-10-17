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

## Açık işler (öncelik sırası)
1. Sabah testleri: `docs/MORNING-TESTS.md`.
2. Aktarma sunucusunu Efe'nin Cloudflare hesabına kurmak (`relay/README.md`).
3. Push bildirimleri (ücretli Apple hesabı); klasör gönderme.
4. Etkin nokta parolası yenilenirken süren başka etkin nokta aktarımı kesilebilir (bilinen risk).
5. Uygulama simgesi/marka; MSIX veya imzalı yükleyici.
