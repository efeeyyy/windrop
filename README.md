# WinDrop

**English summary.** WinDrop is an AirDrop-like file transfer between Windows 11 and iPhone/iPad, with
end-to-end encryption (Noise protocol) and no accounts. Devices find each other over Bluetooth LE, then
transfer over the local network / Windows mobile hotspot, or over an optional self-hosted relay
(Cloudflare Worker, `relay/`). The iOS app is built by CI as an unsigned IPA and installed with
Sideloadly (no Mac needed; free Apple ID apps expire after 7 days). Windows: `build\publish.ps1`, then
`install.ps1` (see `docs/INSTALL.md`). Tests: `dotnet test windows\WinDrop.slnx`, `swift test` in
`ios/WinDropKit`, `npm test` in `relay/`. No license has been chosen yet.

## WinDrop nedir

Windows 11 ile iPhone/iPad arasında AirDrop benzeri dosya aktarımı. Hesap yok; içerik uçtan uca
Noise ile şifrelenir (protokol: `docs/PROTOCOL.md`). Mac gerekmez.

## Nasıl çalışır

Cihazlar Bluetooth LE ile birbirini bulur; aktarma üç yoldan biriyle yapılır:

1. **Yerel ağ (doğrudan TCP)**: aynı Wi-Fi'de ya da Windows'un mobil etkin noktasında, PC üzerinde TCP 53600.
2. **Bluetooth LE**: küçük dosyalar için BLE veri yolu.
3. **Aktarma sunucusu (relay, isteğe bağlı)**: farklı ağlarda; yalnız şifreli baytları iletir
   (`relay/README.md`). Push bildirimi de isteğe bağlıdır ve ücretli Apple geliştirici hesabı gerektirir.

## Windows kurulumu

```powershell
build\publish.ps1 -Version 0.1.0     # dist\WinDrop-win-x64.zip üretir
.\install.ps1 [-AutoStart]           # zip içinden ya da install\ klasöründen
```

Yönetici yalnız güvenlik duvarı kuralı (`firewall.ps1`) için gerekir. Ayrıntı ve kaldırma: `docs/INSTALL.md`.
Gereksinim: Windows 11 (22621+), Bluetooth LE.

## iOS kurulumu (Sideloadly)

1. GitHub Actions "iOS" iş akışının `WinDrop-unsigned-ipa` çıktısını indirin (imzasız IPA; saklama 7 gün).
2. Windows'ta **Sideloadly** ile kendi Apple ID'nizle imzalayıp cihaza yükleyin.
3. Sideloadly'nin çalışması için **iTunes'un web (Apple'dan indirilen, Microsoft Store olmayan) sürümü** ve
   **Apple Mobile Device Support** bileşeni kurulu olmalıdır.
4. Ücretsiz Apple ID ile yüklenen uygulama **7 gün** sonra süresi dolar; yeniden yüklemek gerekir.
   Push ve bazı yetenekler (etkin nokta yetkisi vb.) yalnız ücretli hesapla çalışır (`docs/review/infra-docs.md` §4.8, doğrulanmadı).

Gereksinim: iOS 17+ (bkz. `docs/APP-IOS.md`).

## Geliştirme

| Bileşen | Komut |
|---|---|
| Windows (C#) | `dotnet test windows\WinDrop.slnx` |
| iOS çekirdeği (Swift) | `cd ios/WinDropKit && swift test` (Linux/macOS; Windows'ta WSL: bkz. `docs/STATUS.md`) |
| Relay | `cd relay && npm ci && npm test && npx tsc --noEmit` |
| C# <-> Swift çapraz uyum | `windows/tools/WinDrop.Interop/run-interop.sh` (bkz. `docs/INTEROP.md`) |

CI (`.github/workflows`): `windows.yml`, `relay.yml`, `ios.yml` (macOS, imzasız IPA), `interop.yml`.
Güncel durum ve devir notu: `docs/STATUS.md`.

## Klasör yapısı

- `windows/` — .NET 10 çözümü (`src/` Core, Platform, App ve ölçüm araçları; `tests/`; `tools/WinDrop.Interop`)
- `ios/` — `WinDropKit` (Swift paketi) ve `App` (XcodeGen ile üretilen uygulama + paylaşım uzantısı)
- `relay/` — Cloudflare Worker + Durable Object aktarma sunucusu
- `protocol/` — test vektörleri
- `install/`, `build/` — kurulum ve yayın betikleri
- `docs/` — protokol, mimari, kurulum, durum belgeleri

## Bilinen sınırlar

- Lisans henüz seçilmedi; depo özel.
- Ücretsiz Apple hesabı: uygulama 7 günde sona erer; push için ücretli hesap gerekir.
- Windows CI'da BLE/etkin nokta donanımı yoktur; bu yollar yalnız gerçek cihazda denenir.
- `docs/` altındaki bazı belgeler güncelliğini yitirmiştir (`docs/review/infra-docs.md`).
- Aktarma sunucusu kota/ücretsiz plan sınırlarına tabidir (`relay/README.md`).
