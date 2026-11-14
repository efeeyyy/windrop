# iOS düzeltme turu — sonuç (2026-11-14)

Esas: `DECISIONS.md` (1-10), `ios-app.md` (25 bulgu), `protocol-consistency.md` (Swift'i ilgilendirenler). Yalnız `ios/` değişti.
Derlenemedi/çalıştırılamadı (Mac yok): yalnız Kit testleri (Linux) ve `swiftc -parse` ile doğrulandı. UIKit/CoreBluetooth/NetworkExtension
kodunun tür denetimi CI'da (macOS) yapılacak.

## Bulgu → durum

| # | Durum | Not |
|---|---|---|
| 1 IK hatasında kayıt silme | Düzeltildi | Kayıt YALNIZ `WDU` (p2c'de tam 3 bayt, parçalama başlığından önce) ile silinir, sonra XX ile tek yeniden deneme (`drive`). Kopma/zaman aşımı/WDR/sıra hatası silmez; `forceXX` ve `localDrops` kaldırıldı. |
| 2 BLE el sıkışma zaman aşımı | Düzeltildi | IK/XX + ilk `hello` 10 sn (`withDeadline`); dolunca bağlantı kapatılır, GATT kesilir, yuva boşalır. |
| 3 `sendReliable` | Düzeltildi | FIFO kilit (`writeWaiter` ezilmez) + 5 sn onay zaman aşımı → `close(.timeout)`. Ölü `send`/`readyWaiter` yolu kaldırıldı. |
| 4 Uzantıya yer açma | Kısmen | `didBecomeActive` içinde `setYielding(false)`; uzantıda el sıkışma düşerse 1 kez yeniden deneme (DECISIONS 10). Uzantıda "bağlanmadan önce bekleme" eklenmedi; cihazda doğrulanmalı. |
| 5 Onay sonrası sonsuz bekleme | Düzeltildi | `watchPath` (30 sn, sonra hata + PC'ye `cancel`); `awaitNextPath` bekçiyi yeniden kurar; `peerGone` yolu olmayan onaylı satırları kapatır. |
| 6 Veri kanalı boşta kalma | Düzeltildi | TCP: keepalive + okuma/yazma başına 30 sn (`NWByteStream`); BLE: `pop(timeout: 30)` → `TransferError.timeout`; aktarma WebSocket'i: 30 sn. |
| 7 Push ortamı | Düzeltildi | `PUSH_SANDBOX` (veya DEBUG) → `sandbox`; `Release-Push` varsayılan olarak bayrağı taşır. |
| 8 `joinOnce` | Düzeltildi | Tanıdık/eşli PC: `joinOnce=false`; bilinmeyen: true. Katılma 120 sn (`raceWithTimeout`), dolunca profil geri alınır. |
| 9 App Group | Kısmen | Ana uygulama açılışta ortak anahtarı üretir; Ayarlar > Tanılama "Ortak kimlik açık/kapalı". CI'da entitlements gömerek imzalama (`.github`) kapsam dışı: ertelendi. |
| 10 Otomatik kabul varsayılanı | Değişmedi (bilinçli) | QR eşleştirme açık kullanıcı eylemi ve Ayarlar metni bunu anlatıyor; eşleştirme sonunda "sormadan kabul edilir" bilgisi eklendi. Genel varsayılanı `false` yapmak QR eşleşmesinin amacını bozar: Efe kararı gerekir. |
| 11 Hız sınırı | Düzeltildi | Bilinmeyenler toplam 3/dk, aynı anda 1 bekleyen bilinmeyen teklif (`busy`), reddedilen anahtara 2 dk sessizlik (`silenced`), neden `rate_limited`. |
| 12 Arka plan izinleri | Düzeltildi | Bildirim izni QR eşleştirme sonunda da istenir; arka planda Fotoğraflar izni `notDetermined` ise taşıma ertelenir (`photosPending`, `photosDenied` yazılmaz). |
| 13 Kit hata metinleri | Düzeltildi | `UserFacingError` (tr+en) tüm `fail`/eşleştirme yollarında. |
| 14 Medya türleri | Düzeltildi | jpg/jpeg/heic/heif/png/gif/tif/tiff/dng + mov/mp4/m4v listesi. Her dosya ayrı kaydetme yapılmadı (ertelendi). |
| 15 Teklif sayfası | Düzeltildi | `presentsIncomingOffers()` kökte, Gönder ve QR eşleştirme sayfalarında; bilgi kartı yalnız kullanıcı kapatınca "gösterildi" olur. Cihazda doğrulanmalı. |
| 16 Sahipsiz parola | Düzeltildi | Bilinmeyen PC parolası Keychain'e yazılmaz. |
| 17 `remember` | Düzeltildi | `remember(NearbyPeer)` anlık görüntüyle. |
| 18 Geç `reject` | Düzeltildi | `!isFinished`; aktarım sürerken gelen `reject` iptal sayılır. |
| 19 Boş alan | Düzeltildi | Teklif gelince (manuel/otomatik) `total ≤ boş − pay` değilse sorulmadan `reject no_space`. |
| 20 Relay adresi | Düzeltildi | `RelaySettings` + Ayarlar `SharedGroup.defaults`; `migrateSettings` listesine eklendi. |
| 21 Ad | Düzeltildi | `String(localized: "Bilgisayar")`; Cc/Cf/Zl/Zp karakterleri ayıklanır (`PeerName`). |
| 22 QR başarı koşulu | Düzeltildi | Yalnız `hello`/`pairInfo`. |
| 23 Kapalı bağlantı | Düzeltildi | `openLinkEntry` yalnız açık bağlantıyı sayar. |
| 24 Ölü kod | Kısmen | Yanıtsız yazma yolu, `acceptAutoLimit` taşındı. Boş `didClose` temsilcisi bırakıldı. |
| 25 iPad/erişilebilirlik | Düzeltildi | QR önizleme `videoRotationAngle`; `PeerTile` birleşik etiket, kod `.secondary`. |

## DECISIONS (Swift)
1 `StatusMessage.wifi` isteğe bağlı; bilinmeyen `t`/çözülemeyen sinyal `SecureChannel.receiveMessage` içinde atlanır (`IgnoredSignals`). 2 yukarıda.
3 20 sn + 60 sn (teklif), gelen teklif onayı 60 sn (`reject timeout`). 4 `path` yol başına; `localNetworkDenied` diğer yol sürerken aktarımı bitirmez.
5 `bleData`/`relay` gelince bekleyen `addr`/`hotspot` denemeleri kapatılır. 6 tüm süreler `Timeouts` (Deadline.swift). 7 MANIFEST bütçesi
`min(65518, channel.maxFrameBodySize)` (Kit testi). 8 PC tarafı. 9 yukarıda. 10 yukarıda.
protocol-consistency: U1, U2, U3 (`cancel` gönderilir), U4, U12, U19 kapandı.

## Ertelendi
Kökte tek `ActiveSheet` enum'u; `TransferCoordinator` birim testleri; Tanılama sayfasının tamamı (yalnız ortak kimlik satırı var); CI'da entitlements
gömme (bulgu 9, `.github`); U13 `caps`, U18 PC'den gelen alan doğrulaması, U25 iPhone'da `btAudio` ile BLE kesme; Fotoğraflar'a dosya başına kaydetme;
uzantıda bağlanma öncesi bekleme.

## CI'da / cihazda özellikle bakılacaklar
- macOS CI derlemesi: yeni kod hiç derlenmedi (`withDeadline`/`raceWithTimeout` eşzamanlılık uyarıları, `Release-Hotspot` ve `Release-Push` yapılandırmaları).
- PC `WDU` yollayınca: iPhone kaydı silip XX ile yeniden bağlanıyor mu; PC'siz IK zaman aşımında kayıt duruyor mu (Windows ajanı `WDU`yu üretmeli).
- `reject` nedenleri: `rate_limited`, `silenced`, `busy`, `no_space`, `timeout` (PC `timeout` nedenini tanımıyor olabilir; ham metin gösteriliyorsa uyarlanmalı).
- `peerGone` artık onaylı ama yolu olmayan satırı hemen düşürür: BLE oturumu 1 sn'lik yeniden bağlanmada kopuyorsa kısa kopmalarda satır hata verebilir.
- TCP boşta 30 sn: PC'nin RESUME/ACK'i 30 sn'den geç yolladığı çok büyük dosyalarda (hash) denenmeli.
- Etkin nokta: `joinOnce=false` ile iOS onayının yalnız ilk seferde çıkması; kilit ekranında ağın kalması.
- Paylaşım sayfası/QR sayfası açıkken gelen teklifin üstte görünmesi; bilgi kartının tekrar çıkması.
- Arka planda ilk alım: bildirim izni, `photosPending` metni.
- Kit: `swift test` 113 test geçti (3 yeni: `DecisionsTests`).
