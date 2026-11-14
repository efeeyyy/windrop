# iOS uygulaması — derin kod incelemesi (salt-okur)

Tarih: 2026-11-13. Kapsam: `ios/App` (Shared, Sources, ShareExtension, project.yml, entitlements, Localization).
Yöntem: statik okuma; kod derlenmedi/çalıştırılmadı (Mac yok). Gerektiğinde `ios/WinDropKit` ve Windows kodu çapraz kontrol edildi.
`py -3 ios/App/Localization/check-xcstrings.py` → 233 + 5 + 3 anahtar, eksik çeviri 0.

Özet: 25 bulgu — Kritik 0, Yüksek 5, Orta 10, Düşük 10. "doğrulanmadı" = cihazda/Mac'te sınanması gereken varsayım.

---

## Bulgular

### 1. [Yüksek] IK hatasında eşleşme kaydı çok kolay siliniyor
- **Yer:** `Shared/Ble/SignalHub.swift:519-531`; `Shared/Ble/BleCentral.swift:65-73, 146-156, 509-520, 575-577`
- **Sorun:** IK el sıkışması hangi nedenle düşerse düşsün (yerel kesme dışında) tanıdık/eşli kayıt (`autoAccept`, etkin nokta bilgisi dahil) siliniyor. iPhone "PC anahtarımı reddetti" ile "bağlantı koptu" durumlarını ayırt edemiyor: PC tarafı reddi yalnız kapanma/WDR ile anlaşılıyor. `localDrops` yalnız `dropConnection`/`tearDownAllLinks` içinde yazılıyor. `resetSession` (WDR ya da `didModifyServices`) `cancelPeripheralConnection`'ı doğrudan çağırıyor, bu yüzden yerel kesme sayılmıyor.
- **Senaryo:** (a) Bekleyen bağlantı tam menzil sınırında kuruluyor ve IK sırasında bağlantı denetimi zaman aşımına düşüyor. (b) PC'de WinDrop yeniden başlıyor (`didModifyServices`). (c) Cihaz testinde görülen sıra no atlaması nedeniyle PC `WDR` yolluyor. Üçünde de QR eşleşmesi sessizce siliniyor; otomatik kabul ve sabit etkin nokta parolası kayboluyor, kullanıcı yeniden QR okutmak zorunda kalıyor.
- **Öneri:** Kaydı hemen silme. Önce XX ile yeniden bağlan. XX aynı PC anahtarıyla başarılı olursa PC canlıdır ve yalnız IK'yı reddetmiştir; kayıt ancak o zaman düşürülsün (ya da yalnız `autoAccept` kalksın), tercihen art arda 2 kez görülünce. Daha iyisi: PROTOCOL §3a'ya açık bir "güvenilmeyen anahtar" işareti (ör. `WDU`) eklemek.

### 2. [Yüksek] BLE el sıkışmasında zaman aşımı yok: oturum yuvası asılı kalabiliyor
- **Yer:** `SignalHub.swift:516-517, 535` (`connectIK`/`connectXX` süresiz bekleniyor), `SignalHub.swift:409` (yalnız `.idle` yuvaya yeni oturum), `BlePeripheralLink.swift:112-118`
- **Sorun:** PC ön eki/ilk mesajı alıp hiç yanıt vermezse (PC uygulaması takıldı, yazma düştü ama bağlantı açık) `receiveHandshakeReply` sonsuza dek bekliyor. Yuva `.handshaking` durumunda kalıyor; taramada bulunan aynı PC yok sayılıyor (`slot.phase == .idle` koşulu) ve `handshake` arka plan görevi iOS'un süre sınırına kadar açık kalıyor.
- **Senaryo:** PC yakında görünüyor ama iPhone listesine hiç düşmüyor. Uygulama yeniden başlatılana ya da Bluetooth kapatılıp açılana kadar düzelmiyor.
- **Öneri:** El sıkışma + ilk `hello` gönderimini 10–15 sn'lik bir zaman aşımıyla sar (süre dolunca `link.close()` → `central.closeLink`). Zaman aşımı yerel kesme sayılsın (1. bulguyla birlikte).

### 3. [Orta] `BlePeripheralLink.sendReliable`: bekleyen yazma üzerine yazılabiliyor, zaman aşımı yok
- **Yer:** `Shared/Ble/BlePeripheralLink.swift:65-71, 91-95`; `BleCentral.swift:582-585`
- **Sorun:** `writeWaiter` doluyken ikinci çağrı gelirse ilk continuation kaybolur (hiç resume edilmez, görev sonsuza dek asılı kalır), ikinci çağrı da birincinin onayıyla resume edilir. Bugün tek yazıcı varsayımı dışarıda korunuyor (Kit `SendGate`, el sıkışma sırası); sınıfın kendisi bunu zorlamıyor. Onay hiç gelmezse bekleme yalnız iOS ATT zaman aşımıyla (~30 sn, bağlantı kopunca) bitiyor. Bu sürede `SignalHub` kilidi ve `SendGate` tutulduğu için accept/cancel/status iletileri de bekliyor. `didWrite` çevre birimine göre yönlendiriliyor, yazma kimliğine göre değil.
- **Senaryo:** İleride eşleştirme ya da tanılama kodu aynı bağlantıya `SendGate` dışından yazarsa görev sessizce asılı kalır.
- **Öneri:** `guard writeWaiter == nil` (aksi halde FIFO kuyruğu ya da hata), 10 sn zaman aşımı → `close(error: .timeout)`.

### 4. [Yüksek] Paylaşım uzantısına yer açma (yield) Darwin bildirimine dayanıyor; askıdaki uygulama bildirimi alamaz
- **Yer:** `Sources/App/AppModel.swift:110, 123-139`; `ShareExtension/ShareViewController.swift:15-25`; `BleCentral.swift:122, 242`
- **Sorun:** Uzantı çoğunlukla ana uygulama arka planda ve askıdayken açılır. Askıdaki süreç Darwin bildirimini işleyemez (doğrulanmadı: bildirimler sürdürmede gecikmeli gelebilir). Bu arada iOS ana uygulamanın GATT bağlantısını açık tutar. Sonuç: aynı BLE adresinden iki oturum (§3a), sıra no karışması/WDR ve uzantıda "PC görünmüyor". Ters yönde: `active` alınıp `inactive` kaçırılırsa `isYielding` 120 sn'ye kadar true kalır. Bu sürede uygulama öne gelse bile `ensureConnections`/`applyScan` çalışmaz, "yakında kimse yok" görünür.
- **Öneri:** `didBecomeActive` içinde `setYielding(false)` yap (uygulama öndeyken başka uygulamanın paylaşım sayfası açık olamaz). Uzantı tarafında PC'ye bağlanmadan önce kısa bir bekleme ekle; ana uygulama bağlantıyı bırakmazsa PC tarafında "aynı adres, yeni ön ek" kuralı (§3a) zaten eski oturumu kapatır, bunu cihazda doğrula.

### 5. [Orta] Onaydan sonra veya tüm yollar başarısız olunca aktarım satırı sonsuza dek bekliyor
- **Yer:** `Shared/Transfers/TransferCoordinator.swift:161-166, 208-213, 509-513`; `watchOffer` (131-153) yalnız onaydan önceki aşamayı kapsıyor
- **Sorun:** `peerAccepted`/`userAccepted` sonrasında `.awaitingPeer` için zaman aşımı yok. `peerGone` yalnız `.awaitingUser` satırlarını iptal ediyor; `.awaitingPeer` ve `.awaitingRelayConsent` satırları PC gidince de "Bağlantı hazırlanıyor…" olarak kalıyor.
- **Senaryo:** Arka planda gelen teklif bildirimine 30 sn'den sonra "Kabul" deniyor (PC'nin onay penceresi kapanmış). `accept` gidiyor ama PC `addr` göndermiyor, satır asılı kalıyor. Aynısı PC'nin kabulden sonra çökmesinde ya da `awaitNextPath` sonrasında PC'nin sonraki yolu seçememesinde de oluyor.
- **Öneri:** Onaydan sonra yol mesajı için bir bekçi ekle (ör. 60 sn → "Bilgisayar bağlantıyı başlatmadı"). `peerGone` geldiğinde etkin yolu olmayan bekleyen satırları hatayla kapat.

### 6. [Orta] Veri kanalında okuma/boşta kalma zaman aşımı yok (TCP ve BLE)
- **Yer:** `Shared/Net/DataClient.swift:37-47, 69-83, 102-105`; `Shared/Transfers/TransferRunner.swift:149-151` (alıcıya zaman aşımlı `frameReader` verilmiyor; `SignalHub.swift:298-300` `pop(timeout: nil)`)
- **Sorun:** `NWConnection.receive` süresiz bekliyor ve TCP keepalive kapalı. BLE alımında PROTOCOL §6.3/4'teki "eş 30 sn çerçeve göndermezse düşer" kuralı iOS'ta uygulanmıyor.
- **Senaryo:** iPhone etkin nokta menzilinden çıkıyor ya da PC uykuya geçiyor (RST gelmiyor). Satır "Alınıyor…" durumunda kalıyor ve arka plan görevi iOS süre sınırına kadar tutuluyor.
- **Öneri:** `NWProtocolTCP.Options.enableKeepalive` + `keepaliveIdle` ekle ve okuma başına 30–60 sn zaman aşımı koy. BLE `frameReader` için `pop(timeout: TransferLimits.bleResumeTimeout)` kullan.

### 7. [Yüksek] Push derlemesinde APNs ortamı uyuşmuyor
- **Yer:** `Sources/Push/PushManager.swift:124-131`; `WinDrop-Push.entitlements` (`aps-environment = development`); `project.yml:42, 117-119` (`Release-Push: release` → `DEBUG` tanımsız)
- **Sorun:** Release-Push yapılandırması sunucuya `env: "production"` gönderiyor. Geliştirme profiliyle imzalanmış uygulamanın belirteci ise sandbox belirtecidir.
- **Senaryo:** Ücretli hesapla geliştirme sertifikasıyla yüklenen uygulamada APNs `BadDeviceToken` döner, bildirim hiç gelmez. Belirteç sunucuya kaydolduğu için hata görünmez.
- **Öneri:** Ortamı `embedded.mobileprovision` içindeki `aps-environment` değerinden ya da yapılandırma başına bir build ayarından (`APNS_ENV` → Info.plist) oku. Sunucunun `BadDeviceToken` yanıtını kayıt hatası olarak döndürmesi de yararlı olur.

### 8. [Orta] Etkin nokta `joinOnce = true`: kararla çelişiyor, arka planda kopabilir
- **Yer:** `Shared/Hotspot/HotspotJoiner.swift:35`; karar: `docs/ARCHITECTURE.md:107-109`, `docs/PROTOCOL.md:169-170`
- **Sorun:** Karara göre eşli/tanıdık PC'nin parolası sabit tutuluyor ki iOS ağı hatırlasın ve katılma onayı yalnız ilk seferde çıksın. `joinOnce` ise profili kalıcı yapmıyor, iOS onayı her aktarımda yeniden çıkar. Apple belgelerine göre `joinOnce` ağları uygulama arka plana geçince bir süre sonra kesilir (doğrulanmadı: güncel iOS sürümünde süre).
- **Senaryo:** Etkin nokta üzerinden uzun bir alım sürerken kullanıcı ekranı kilitliyor, iPhone ağdan düşüyor ve aktarım kopuyor.
- **Öneri:** Tanıdık/eşli PC için `joinOnce = false`, bilinmeyen PC için `true`. `abandon` yalnız yarışı kaybeden katılmada çağrılsın.

### 9. [Orta] App Group ortak kimliği pratikte devreye girmeyebilir
- **Yer:** `.github/workflows/ios.yml:55-57` (`CODE_SIGNING_ALLOWED=NO` → entitlements ikiliye gömülmüyor); `*.entitlements`; `Shared/App/SharedGroup.swift:16-19`; `Shared/Identity/IdentityStore.swift:42-53`
- **Sorun:** İmzasız IPA'da `application-groups` yetkisi yok. Sideloadly/ücretsiz hesap grubu kaydetmezse (doğrulanmadı) `containerURL` nil döner ve uzantı her zaman ayrı kimlikle çalışır: PC'de iki "iPhone", iki ayrı tanıdık kaydı. Ayrıca grup olsa bile, ana uygulama hiç gönderim yapmadıysa ortak anahtar üretilmemiş olur ve uzantı kendi yerel kimliğini kullanır; sonra ana uygulama anahtar üretince uzantı kimlik değiştirir.
- **Öneri:** CI'da entitlements'ı gömerek imzala (`codesign -s - --entitlements …` / `ldid -S`). Ana uygulama ortak alan varken açılışta anahtarı üretsin (izin istemez). Ayarlar'da "Ortak kimlik: açık/kapalı" tanılama satırı göster.

### 10. [Orta] Otomatik kabul varsayılan olarak açık (spesifikasyondan sapma)
- **Yer:** `Shared/App/AutoAcceptSettings.swift:26-28`; `Sources/UI/SettingsView.swift:6`; `Sources/Pairing/PairingController.swift:144`
- **Sorun:** PROTOCOL §7 (`PROTOCOL.md:246-247`): "Eşli cihazdan gelen offer varsayılan olarak onay ister". Kodda QR eşleştirmesi `autoAccept = true` yazıyor ve ayar varsayılanı `true`, sınır 2 GB.
- **Senaryo:** Kullanıcı yalnız "eşleştir" diyor; PC'den gelen 2 GB'a kadar dosyalar arka planda sorgusuz alınıp Fotoğraflar'a taşınıyor.
- **Öneri:** Varsayılanı `false` yap ya da eşleştirme bitiminde açık bir "Bu bilgisayardan gelenleri otomatik kabul et" seçimi sun. Windows tarafındaki varsayılanla hizalanması gerekiyor (doğrulanmadı).

### 11. [Orta] Teklif hız sınırı §2b'den sapıyor
- **Yer:** `TransferCoordinator.swift:215-224, 236-238`
- **Sorun:** Sınır eş başına uygulanıyor; bilinmeyen cihaz her teklifte yeni anahtar üreterek sınırı aşabiliyor. "Aynı anda en çok 1 bekleyen bilinmeyen teklif (`busy`)" ve "reddedilenden 2 dk sessizlik" kuralları yok. Ret nedeni `"rate"`, spesifikasyonda ise `rate_limited`.
- **Senaryo:** Yakındaki kötü niyetli bir cihaz sürekli onay sayfası ve bildirim yağdırıyor.
- **Öneri:** Bilinmeyenler için toplam 3/dk + tek bekleyen teklif kuralını uygula, reddedilen anahtara 2 dk sessizlik uygula, nedeni `rate_limited` yap.

### 12. [Orta] Arka planda alım akışı izinlere takılıyor
- **Yer:** `Sources/Notifications/NotificationManager.swift:35-40`; `TransferCoordinator.swift:257-268, 585-587`; `Shared/Transfers/PhotoSaver.swift:50-51`
- **Sorun:** Bildirim izni yalnız ilk Gönder'de ya da ilk teklifte isteniyor. Hiç gönderim yapmamış kullanıcıya gelen ilk teklif arka plandaysa izin istemi gösterilemiyor (doğrulanmadı) ve `postOffer` sessizce başarısız oluyor; "uygulama kapalıyken PC→iPhone" akışının ana adımı görünmez kalıyor. Aynı şekilde arka planda otomatik kabul edilen ilk medyada `.addOnly` istemi gösterilemiyor; sonuç `photosDenied` oluyor ve kullanıcı izni reddetmemiş olsa da "izin verilmedi" metni çıkıyor.
- **Öneri:** Bildirim iznini kurulumsuz deneyimi bozmadan, ilk ön plan etkileşiminde iste: QR eşleştirme sonu, ilk "Kabul" ya da ilk başarılı aktarım kartı. Arka planda `notDetermined` ise Fotoğraflar'a taşıma ertelensin, `photosDenied` yazılmasın.

### 13. [Orta] Kit hataları kullanıcıya teknik metinle gösteriliyor
- **Yer:** `TransferCoordinator.swift:117, 549, 666`, `PairingController.swift:164`; Kit hata türlerinin (`SessionError`, `ProtocolError`, `NoiseError`, `TransferError`) hiçbiri `LocalizedError` değil
- **Senaryo:** PC veri bağlantısını el sıkışmada kapatıyor ve satırda "İşlem tamamlanamadı. (WinDropKit.SessionError hata 1.)" yazıyor. `summary.error` Kit içinden geldiği için İngilizce arayüzde Türkçe metin kalabilir (doğrulanmadı).
- **Öneri:** Uygulama hedefinde Kit hatalarını anlaşılır, yerelleştirilmiş metne çeviren tek bir eşleme yaz (ör. "Bağlantı koptu, tekrar deneyin", "Sürüm uyumsuz").

### 14. [Düşük] Fotoğraflar'a kaydetme ya hep ya hiç; "medya" tanımı çok geniş
- **Yer:** `Shared/Transfers/TransferRunner.swift:42-47`; `PhotoSaver.swift:55-66`
- **Sorun:** `conforms(to: .image)` SVG, PSD, ICO gibi Fotoğraflar'ın kabul etmediği türleri de kapsıyor. Tek bir `performChanges` içinde biri reddedilince tüm toplu işlem `photosFailed` oluyor.
- **Öneri:** Desteklenen türleri listeyle sınırla (jpeg, heic, png, gif, tiff, dng, mov, mp4, m4v) ya da her dosyayı ayrı kaydet.

### 15. [Orta] Başka bir sayfa açıkken gelen teklif sayfası görünmeyebilir
- **Yer:** `Sources/UI/RootView.swift:31-41`; `HomeView.swift:90`; `SettingsView.swift:205`
- **Sorun:** Teklif sayfası kökten `.sheet` ile açılıyor. Alt görünümde Gönder (NearbyView) ya da QR eşleştirme sayfası açıkken SwiftUI ikinci sayfayı sunmaz (doğrulanmadı). Ayrıca bilgi kartı, teklif geldiği anda `set(false)` ile kapanıyor ve kullanıcı görmeden "gösterildi" olarak işaretleniyor.
- **Senaryo:** Kullanıcı Gönder ekranındayken PC dosya yolluyor; yalnız sekme rozeti değişiyor ve PC 30 sn sonra zaman aşımına düşüyor.
- **Öneri:** Kökte tek bir `ActiveSheet` enum'u kullan ya da teklifi alt sayfaların üstünde (overlay/ayrı pencere) göster. Bilgi kartı yalnız kullanıcı kapatınca işaretlensin.

### 16. [Düşük] Bilinmeyen PC'nin etkin nokta parolası Keychain'de kalıcı ve sahipsiz kalıyor
- **Yer:** `TransferCoordinator.swift:431-433`; `IdentityStore.swift:88-95, 114-119, 136-143`
- **Sorun:** `setHotspot` PC tanıdık olmasa da Keychain'e yazıyor. `reload`/`remove` yalnız tanıdık listesindeki kimlikleri biliyor, bu kayıtlar hiç silinmiyor. PC zaten bu parolayı aktarım sonunda yeniliyor (§6.2).
- **Öneri:** Tanıdık olmayan PC'nin parolasını yalnız `item.hotspotCredentials` içinde (bellekte) tut.

### 17. [Düşük] `remember` canlı BLE oturumu istiyor; uzun gönderim sonunda tanıdık kaydı düşebilir
- **Yer:** `SignalHub.swift:280-292`; `TransferCoordinator.swift:557-560`
- **Sorun:** `remember`, `peers[keyID]` olmadan hiçbir şey yapmıyor. Uzun TCP aktarımı sırasında BLE oturumu düşerse `DONE ok` sonrası kayıt yapılmıyor; bir sonraki seferde yine XX ve kod gösteriliyor.
- **Öneri:** `remember(peer: NearbyPeer)` biçiminde anlık görüntüyle kaydet.

### 18. [Düşük] Geç gelen `reject` bitmiş gönderimi "Reddedildi"ye çeviriyor
- **Yer:** `TransferCoordinator.swift:167-171`
- **Sorun:** `!item.isFinished` denetimi yok (`accept` ve `cancel` dallarında koruma var).
- **Öneri:** `guard !item.isFinished` ekle; aktarım sürerken gelen `reject` iptal gibi ele alınsın.

### 19. [Düşük] Boş alan denetimi kabulden önce yapılmıyor
- **Yer:** `TransferCoordinator.swift:226-284` (Kit `FileReceiver` boş alanı MANIFEST'ten sonra denetliyor)
- **Sorun:** PROTOCOL §5 (`PROTOCOL.md:142`): "kabulden önce `total` ≤ boş alan − pay". Kullanıcı 5 GB'ı kabul ediyor; etkin nokta açılıp bağlantı kurulduktan sonra aktarım başarısız oluyor.
- **Öneri:** Onay sayfasında ve otomatik kabulde `volumeAvailableCapacityForImportantUsage` ile denetle; yetmiyorsa `reject` gönder (neden ör. `no_space`).

### 20. [Düşük] Aktarma sunucusu adresi uzantıyla paylaşılmıyor
- **Yer:** `Shared/Net/RelaySocket.swift:10` (`.standard`); `SettingsView.swift:8`; `project.yml:15-17` ("ayarları paylaşır" deniyor)
- **Sorun:** App Group olsa bile uzantıda aktarma yolu her zaman kapalı; `relay` gelince hemen `relay_fail` gidiyor.
- **Öneri:** `relayUrl` için `SharedGroup.defaults` kullan ve anahtarı `migrateSettings` listesine ekle.

### 21. [Düşük] Yerelleştirilmemiş varsayılan ad; PC adında bidi/kontrol karakteri ayıklanmıyor
- **Yer:** `SignalHub.swift:549` (`"Bilgisayar"` düz String; katalogdaki anahtar kullanılmıyor), `SignalHub.swift:575-580`
- **Sorun:** İngilizce arayüzde "Bilgisayar" görünüyor. Bilinmeyen PC'nin `hello.name` değerinde RLO/kontrol karakterleri ayıklanmadan onay sayfasında ve bildirimde gösteriliyor; ad sahteciliğine açık (§2.8'deki kural PC tarafı için yazılmış, iPhone tarafı için de uygulanmalı).
- **Öneri:** `String(localized: "Bilgisayar")` kullan; `Cc`/`Cf` kategorisindeki karakterleri at.

### 22. [Düşük] QR eşleştirme başarıyı "herhangi bir ilk sinyal" ile sayıyor
- **Yer:** `Sources/Pairing/PairingController.swift:132-147`
- **Sorun:** PC'nin ret durumunda hiçbir şey göndermemesine güveniyor (bugün öyle: `windows/src/WinDrop.Platform/PairingService.cs:225-226`). PC ileride bir `reject` gönderirse kayıt `autoAccept = true` ile yapılır.
- **Öneri:** Yalnız `hello` ya da `pairInfo` başarı sayılsın; diğer her sinyal ret kabul edilsin.

### 23. [Düşük] Kapatılmış ama `links` içinde kalan bağlantı yeni bağlantıyı engelleyebilir (doğrulanmadı)
- **Yer:** `BleCentral.swift:134, 558-559`; `SignalHub.swift:602-604`
- **Sorun:** `SignalHub` bağlantıyı kendi kapattığında (`interrupt`) `wasOpen` false olur ve GATT kesilmez. `BleCentral.links[id]` kapalı nesneyi tutmaya devam eder. Bu durumda `ensureConnections` servis keşfini atlar; `openLink` ise `links[id] == nil` koşulu yüzünden 10 sn zaman aşımına düşer. Bugünkü çağrı yolları çoğunlukla ardından `resetLink`/`closeLink` çağırıyor, ama kapsanmayan bir yol (ör. yeni bağlantıyla sıradaki `nextLink`) kalabilir.
- **Öneri:** `BleCentral` içinde `link.isOpen == false` olan girdiyi boş say ya da `BlePeripheralLink.close` → `BleCentral` geri bildirimiyle girdiyi sil.

### 24. [Düşük] Ölü/yinelenen kod
- **Yer:** `BlePeripheralLink.swift:53-62, 73-85` (`send`, `waitUntilReady`, `readyWaiter`; artık tüm yazmalar onaylı) ve `BleCentral.swift:587-589`; `BleLinkAdapter.write`/`writeReliable` aynı işi yapıyor (137-143); `SignalHub.bleCentral(_:didClose:)` boş (375-377); `TransferRunner.acceptAutoLimit` (85) yalnız `AutoAcceptSettings` tarafından kullanılıyor.
- **Öneri:** Yanıtsız yazma yolunu kaldır (ya da bir bayrakla bırak); sabiti `AutoAcceptSettings` içine taşı.

### 25. [Düşük] iPad/yatay: QR kamera önizlemesi döndürülmüyor; erişilebilirlik eksikleri
- **Yer:** `Sources/Pairing/QRScannerView.swift` (`videoRotationAngle`/yön işlemesi yok), `project.yml:82-90` (yatay yön açık); `Shared/UI/PeerTile.swift:9-27`
- **Sorun:** iPad'de ve yatay tutuşta önizleme yan duruyor (okuma büyük olasılıkla yine çalışır). `PeerTile` için birleşik erişilebilirlik etiketi yok: VoiceOver simge adını, PC adını, "Tanıdık"ı ve 4 haneli kodu ayrı ayrı okuyor; kod `.tertiary` renkte, kontrastı düşük. Uygulamada toplam yalnız 10 `accessibility*` kullanımı var.
- **Öneri:** `AVCaptureConnection.videoRotationAngle`'ı arayüz yönüne bağla. `PeerTile`'a `.accessibilityElement(children: .combine)` ve "‹ad›, tanıdık bilgisayar, doğrulama kodu 1234" biçiminde etiket ekle; kodu `.secondary` yap.

---

## Doğrulanan güçlü yanlar (sorun bulunmadı)
- `hasConsent` / `userAccepted` / `peerAccepted` kuralı `addr`, `hotspot`, `bleData` ve `relay` yollarının hepsinde uygulanıyor. `confirmRelay` yalnız `.awaitingRelayConsent` durumundan ilerliyor. Otomatik kabul yalnız `isKnown && autoAccept && ayar && sınır` koşulunda (`TransferCoordinator.swift:173-194, 246`).
- `hotspot` yalnız `wifi=none` iken işleniyor (Efe şartı).
- Veri kanalında XX kullanılıyorsa PC anahtarı 3. mesajdan önce BLE oturumundaki anahtarla karşılaştırılıyor (`TransferRunner.swift:96-100`).
- Keychain erişim sınıfı `AfterFirstUnlockThisDeviceOnly` uygun: kilitliyken arka plan IK çalışır, yedeğe gitmez.
- Açılışta izin istenmiyor: `startIfAlreadyAuthorized` yalnız izin zaten verilmişse merkezi kuruyor; bildirim, kamera ve fotoğraf izinleri tembel.
- `check-xcstrings.py`: eksik çeviri 0.

## İyileştirme önerileri
1. **Tek zaman aşımı yardımcısı** (`withDeadline(seconds) { … } onTimeout: { link.close() }`) yazıp tüm süresiz beklemelere uygula: el sıkışma, `sendReliable`, onay sonrası yol bekleme, TCP/BLE okuma. Bulgu 2, 3, 5 ve 6 tek kalıpla kapanır.
2. **PROTOCOL §3a'ya açık "güvenilmeyen anahtar" işareti** ekle (ör. `WDU`). iPhone eşleşmeyi yalnız bu işaret gelince silsin (bulgu 1). Windows ile birlikte değişmeli.
3. **Ayarlar > Tanılama** sayfası: ortak kimlik (App Group) açık mı, Bluetooth/yerel ağ/bildirim/fotoğraf izinleri, push ortamı, son BLE hatası. Cihaz testlerinde ve kullanıcı desteğinde zaman kazandırır.
4. **`TransferCoordinator` için birim testleri:** `SignalSending` ve `OfferNotifying` zaten protokol; sahte nesnelerle onay kuralları, hız sınırı, `peerGone` ve geç `reject` senaryoları macOS CI'da test hedefi olarak koşabilir.
5. **Kökte tek sayfa yöneticisi** (`enum ActiveSheet { offer, infoCard, send, pairing }`): teklif sayfası başka bir sayfa açıkken de görünür (bulgu 15).
6. **CI'da entitlements'ı gömerek imzala** ve imzalanmış ikilide `application-groups` / `aps-environment` değerlerini kontrol eden bir adım ekle (bulgu 7 ve 9).
