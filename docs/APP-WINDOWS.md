# Windows uygulaması — yapı (v0, 2026-10-17)

Dayanak: `PROTOCOL.md` v0.1, `ARCHITECTURE.md`. Bu belge bileşenleri ve sınırlarını sabitler;
ajanlar buna göre yazar.

## Projeler
| Proje | Tür | Sorumluluk |
|---|---|---|
| `WinDrop.Core` | net10.0 kütüphane | Noise, ön ek, çerçeveleme, parçalama, ad temizleme, **aktarım motoru** (platformsuz, test edilebilir) |
| `WinDrop.Platform` | net10.0-windows10.0.22621.0 kütüphane | WinRT/Win32 servisleri (aşağıda) |
| `WinDrop.App` | WPF (`UseWPF`, Fluent tema), WinExe | Arayüz, tepsi, tek örnek, komut satırı (`--send`, `--hidden`) |
| `tests/*` | xUnit | Core ve Platform'un donanımsız kısımları |

## WinDrop.Platform servisleri
- **IdentityStore:** X25519 cihaz anahtarı; `%LOCALAPPDATA%\WinDrop\identity.bin` (DPAPI `CurrentUser`).
  Güvenilen cihazlar: `trusted.bin` (DPAPI; açık anahtar, eşleştirme adı, tarih). Kaldırma API'si.
- **HotspotConfigStore:** eşleştirmede bir kez rastgele SSID son eki (32 bit) + parola (128 bit, base32),
  DPAPI ile saklanır.
- **BleSignalServer:** 128 bit servis UUID'si (`ProtocolIds` sınıfında sabit), `c2p` / `p2c`.
  Her bağlantı için: ön ek → Noise yanıtlayan (eşleştirme modunda XXpsk3, değilse IK) → parçalama →
  `SignalMessage` olayları. Reklam: önce servis UUID'si; `Aborted` olursa yerel ad + servis verisi
  ile yeniden dener (PROTOCOL §9). Eşleştirme modu yalnız QR ekrandayken açıktır.
- **DataListener:** TCP 53600 (IPv4+IPv6). Ön ek → `transferId` beklenen bir aktarıma ait değilse
  kapat → IK yanıtlayan; karşı anahtar, o aktarımın eşi değilse kapat → aktarım motoruna devret.
- **HotspotManager:** `NetworkOperatorTetheringManager`. Açmadan önce mevcut yapılandırmayı kaydeder,
  kapatınca geri yükler; kullanıcı etkin noktayı zaten kullanıyorsa dokunmaz. Bant: önce 2,4 GHz
  (ölçüm). Son aktarımdan 2 dk sonra kapatır.
- **NetworkInfo:** aday adresler (Up, loopback/link-local hariç IPv4 + global IPv6).
- **ShellIntegration:** `SendTo\WinDrop.lnk` → `WinDrop.exe --send <dosyalar>`; otomatik başlangıç
  (`HKCU\...\Run`, `--hidden`). İkisi de ayardan açılıp kapanır, yönetici gerektirmez.
- **Settings:** indirme klasörü (varsayılan `Downloads\WinDrop`), eşli cihazdan otomatik kabul +
  üst sınır (2 GB), `%LOCALAPPDATA%\WinDrop\settings.json`.

## Akışlar
- **Eşleştir:** "iPhone ekle" → QR (QRCoder) + eşleştirme modu 2 dk → iPhone XXpsk3 → PC'de onay
  diyaloğu → güvenilen listeye ekle → etkin nokta bilgisini gönder.
- **PC'den gönder:** dosya seçimi / sürükle-bırak / `--send` → yakındaki (BLE bağlı) eşli cihaz
  listesi → `offer` (p2c) → `accept` → yol seçimi (§6) → aktarım ilerlemesi.
- **PC'ye gelen:** `offer` → onay bildirimi (otomatik kabul ayarı) → `accept` + `addr`/`hotspot` →
  iPhone bağlanır → alınan dosya indirme klasörüne (MOTW ile) → "Klasörde göster".

## Arayüz (WPF, Fluent)
Ana pencere: üstte "Gönder" bırakma alanı; eşli cihazlar (yakında/uzakta); aktarımlar listesi
(ilerleme, hız, iptal); alt menü: iPhone ekle, Ayarlar. Tepsi simgesi: aç, gönder, çıkış.
Gelen teklif: küçük onay penceresi (gönderen adı **eşleştirme kaydından**, dosya sayısı/boyutu).
Tüm metinler Türkçe; kaynak dosyasında (ileride çeviri için).

## Kurulum
`install\install.ps1` (yönetici): güvenlik duvarı kuralları (TCP 53600 Private + `192.168.137.0/24`
tüm profiller), Başlat menüsü kısayolu. `install\uninstall.ps1` geri alır. Sonra: MSIX/Inno Setup.

## Aşama C2 uygulaması (2026-10-17)
Platform: `ProtocolIds`, `BleSignalServer` (GATT + reklam kademesi: 128 bit → yedek SIG 32 bit servis + üretici verisi işareti),
`SignalSessionAcceptor` (IK/XX/XXpsk3 ayrımı ilk mesaj uzunluğundan), `PeerSession`, `DataListener` + `PendingTransferTable`,
`TransferCoordinator` (yol seçimi, onay politikası, hız sınırı), `PairingService`, `WinDropRuntime` (bileşim kökü).
App: `Services/RealServices.cs` (`DeviceService`, `TransferService`); sahte servisler yalnız `WINDROP_FAKE=1`.
Günlük: `%LOCALAPPDATA%\WinDrop\logs\app.log` (Platform `PlatformLog.Sink` → `AppLog`). Eksik: aktarma (relay) yolu,
küçük dosyalar için BLE veri yolu, klasör gönderme, Bluetooth ses (`btAudio`) algısı (şimdilik hep false).
