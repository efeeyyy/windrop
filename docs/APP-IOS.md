# iOS/iPadOS uygulaması — yapı (v0, 2026-10-17)

Dayanak: `PROTOCOL.md` v0.1. Mac yok: proje XcodeGen ile üretilir, GitHub Actions macOS'ta
imzasız IPA derlenir, Efe Sideloadly/AltStore ile ücretsiz Apple hesabıyla yükler.

## Yapı
- `ios/WinDropKit` (Swift paketi): Noise, ön ek, çerçeveleme, parçalama, sinyal mesajları, ad
  temizleme, **aktarım motoru** — platformsuz; Linux/WSL'de `swift test` ile doğrulanır.
- `ios/App` (XcodeGen `project.yml`): SwiftUI uygulaması, hedef iOS 17+, iPhone + iPad.
  - **Identity:** Curve25519 anahtar Keychain'de (`AfterFirstUnlockThisDeviceOnly`), eşli PC listesi.
  - **Pairing:** QR tarayıcı (AVFoundation) → `windrop://pair` ayrıştır → BLE ile PC'yi bul →
    XXpsk3 → `k` doğrulaması → kaydet + etkin nokta bilgisini Keychain'e yaz.
  - **BleCentral:** CoreBluetooth, durum geri yükleme (`restoreIdentifier`), arka plan modu
    `bluetooth-central`; eşli PC'lere zaman aşımsız bekleyen bağlantı; ön ek → IK başlatan.
  - **DataClient:** `Network.framework` NWConnection ile TCP; adaylara paralel 1,5 sn deneme.
  - **HotspotJoiner:** `NEHotspotConfiguration` yalnız yetki varsa (derleme bayrağı
    `HOTSPOT_ENTITLEMENT`); yoksa ilk seferde "Ayarlar'dan şu ağa bir kez katıl" ekranı (SSID/parola
    kopyalanabilir), sonra iOS ağı hatırlar.
  - **Transfers:** gönderme (PhotosPicker + dosya seçici) ve alma; alınanlar uygulamanın
    Documents klasörüne (Dosyalar uygulamasında görünür: `UIFileSharingEnabled`,
    `LSSupportsOpeningDocumentsInPlace`), fotoğraf/video için "Fotoğraflar'a kaydet".
  - **Notifications:** arka planda gelen `offer` → yerel bildirim → dokununca öne gelir.
- Paylaşım uzantısı: v1'de yok (App Groups'un ücretsiz hesapta durumu belirsiz); uygulama içi
  seçicilerle başlanır.

## Info.plist / yetkiler
`NSBluetoothAlwaysUsageDescription`, `NSCameraUsageDescription`, `NSLocalNetworkUsageDescription`,
`NSPhotoLibraryAddUsageDescription`, `UIBackgroundModes: [bluetooth-central]`. Hotspot yetkisi yalnız
ücretli hesapla açılır.

## CI (`.github/workflows/ios.yml`)
macos-latest: `swift test` (WinDropKit) → `brew install xcodegen` → `xcodegen generate` →
`xcodebuild -scheme WinDrop -sdk iphoneos -configuration Release CODE_SIGNING_ALLOWED=NO archive`
→ `Payload/WinDrop.app` zip → `WinDrop-unsigned.ipa` artifact.
