# Protokol tutarlılık incelemesi: PROTOCOL.md × C# × Swift

Tarih: 2026-11-13. Salt-okur inceleme; kod ve PROTOCOL.md değiştirilmedi. Yöntem: üç kaynağın satır satır okunması (derleme/test çalıştırılmadı; aşağıdaki "doğrulanamadı" bölümüne bak).

Kısaltmalar: **S** = `docs/PROTOCOL.md`; **C#** = `windows/src/WinDrop.Core` + `WinDrop.Platform`; **Sw** = `ios/WinDropKit/Sources/WinDropKit` + `ios/App`. Satır numaraları 2026-11-13 durumuna göredir. Önem: **Y** yüksek (gerçek cihazda kırar), **O** orta, **D** düşük.

## 0. Özet

- Tel biçimi (ön ek, TCP/BLE çerçeveleme, çerçeve türleri 1–8, JSON alan adları, RESUME/ACK/DONE, MANIFEST `more`/`dir`, SAS, roomId, pushId) iki kodda **uyumlu**; SAS ve roomId ortak vektörle bağlı, geri kalanı yalnız TCP/relay interop betiğiyle kısmen sınanıyor.
- **1 yüksek** bulgu: PC'nin `status` mesajı iOS'ta çözülemiyor ve BLE oturumunu düşürüyor (U1).
- 5 orta bulgu (U2–U6), geri kalanı düşük; PROTOCOL.md'nin bölüm düzeni dağınık (numaralama, yetim satır, eskimiş maddeler; bölüm 6).

## 1. Tablo: sinyal mesajları (S §4/§9a) × (S, C#, Swift)

Gösterim: ✓ uyumlu, ✗ uyumsuz, ~ kısmi/yorum farkı, — yok. "Üretim/İşleme" sütunu: kim gönderir (G) / kim işler (İ).

| Mesaj / alan | Spec | C# (tanım; üretim/işleme) | Swift (tanım; üretim/işleme) | Uyum |
|---|---|---|---|---|
| `hello` | iki yön | SignalMessages.cs:27; G: TransferCoordinator.cs:325, PairingService.cs:225; İ: :364 | SignalMessages.swift:9; G: SignalHub.swift:560, PairingController.swift:119; İ: TransferCoordinator.swift:195 (yok sayar; ad/btAudio SignalHub.swift:575-584) | ✓ |
| `hello.v` int | bilgi amaçlı | zorunlu int | zorunlu Int | ✓ |
| `hello.name` | ≤64, bidi/kontrol ayıklanır (§2.8, yalnız PC için) | PairingService.cs:236 ayıklar | SignalHub.swift:575-581 yalnız trim+64 (ayıklamaz) | ~ U19 |
| `hello.caps` | ["tcp","hotspot","relay"] | zorunlu; PC `["tcp","hotspot"]` yollar; yalnız `hotspot` denetlenir (:932,:1004) | zorunlu; `["tcp","hotspot"]` yollar (SignalHub.swift:14); okumaz | ~ U13 |
| `hello.wifi` | iPhone: none/connected | `string?`, PC null yollar, değer doğrulanır (:199) | `String?`, her zaman yollar; doğrulama yok | ✓ |
| `hello.btAudio` | bool | zorunlu | zorunlu | ✓ |
| `status.wifi` | `wifi`, `btAudio` (iki yön) | `string?` (null → alan yazılmaz) (:35); PC null yollar (TransferCoordinator.cs:214) | **`String` zorunlu** (SignalMessages.swift:21) | **✗ U1** |
| `status.btAudio` | bool | zorunlu | zorunlu | ✓ |
| `offer.transferId` | 16 B b64url dolgusuz | `TransferId` (JsonConverter, 22 kar.) | String, decode'da `TransferId.parse` (SignalMessages.swift:200) | ✓ |
| `offer.count/total/preview` | int/int64/≤5 ad | zorunlu; negatif ve preview>5 → ProtocolException (:183-184) | zorunlu; ek doğrulama yok | ~ U18 |
| `offer` üretim/işleme | gönderen→alıcı | G: :743 İ: :395 (`bad_count`, hız sınırı) | G: TransferCoordinator.swift:115,143 İ: :159 | ✓ |
| `accept` / `reject` | `transferId`, `reason?` | AcceptMessage/RejectMessage; reason'lar: `silenced`,`bad_count`,`busy`,`rate_limited`,`declined`,`no_space` | tek `ReplyMessage`; reason'lar: `busy`,`rate` (TransferCoordinator.swift:229,237) | ~ U12 |
| `addr` | PC→iPhone, `candidates[{ip,port}]` | G: :985; port 1–65535 doğrulanır | İ: TransferCoordinator.swift:172; yalnız İ | ✓ (tek yön) |
| `hotspot.ip/port` | PC→iPhone | zorunlu | zorunlu | ✓ |
| `hotspot.ssid/pass` | **"her zaman"** (§4 satır 113) | `string? = null`; belge yorumu "yalnız değiştiyse" (SignalMessages.cs:55); PC her zaman doldurur (:969) | `String?`; eksikse kayıtlı bilgiye düşer; eski TODO (TransferCoordinator.swift:435) | ~ U10 |
| `pairInfo` | PC→iPhone, `ssid`,`pass`; **tabloda yok, dosyanın 1. satırında yetim** | zorunlu; G: PairingService.cs:226 | `PairInfo`; İ: PairingController.swift:124; ayrıca eski `hotspot`'u da kabul eder | ✓ (S ✗ U22) |
| `relay` | **"iki yön"** `transferId`,`token` | G yalnız PC (:1232); alınan `relay` "beklenmeyen" (:418) | İ: TransferCoordinator.swift:193; G yok | ~ U14 |
| `relay.token` | 43 kar. dolgusuz b64url | RelayRoom.cs:25-40 | RelayTransport.swift:30-39 (kanonik doğrular) | ✓ (U17) |
| `path` | iPhone→PC, 6 sonuç | `PathResult.All`; bilinmeyen → ProtocolException; `*_ok` yok sayılır (:432-442) | `enum PathResult`; bilinmeyen sonuç = decode hatası; G: :678 | ✓ |
| `cancel` | iki yön | G: :1163,:1351,:1423 İ: :411 | G: :337 İ: :181 | ✓ |
| `bleData` | PC→iPhone (§6.4a) | G: :1121 | İ: TransferCoordinator.swift:188 | ✓ (tek yön) |
| `pushId` | iPhone→PC (§6.5) | İ: :387 (yalnız tanıdık+geçerli biçim) | G: SignalHub.swift:325 (yalnız `isKnown`) | ✓ |
| `pushId` biçimi | 16 B hex (=32 küçük hex) | `PushIds.IsValid` 32 `[0-9a-f]` | PushManager.swift:115 aynı | ✓; Worker regex `{32}` (push.ts:18) |
| bilinmeyen alan | yok sayılır | ✓ | ✓ | ✓ |
| bilinmeyen `t` | hata | ProtocolException | invalidMessage | ✓ |
| gövde üst sınırı | (§3: Noise ≤8192) | 8176 B, kodlama+çözmede (:136) | gönderimde `maxPlaintextSize`; alımda yok | ~ U18 |

## 2. Tablo: veri kanalı çerçeveleri ve sınırlar

| Konu | Spec | C# | Swift | Uyum |
|---|---|---|---|---|
| Tür baytları | MANIFEST=1,RESUME=2,FILE_START=3,DATA=4,FILE_END=5,ACK=6,CANCEL=7,DONE=8 (§9a) | Frames.cs:13-20 | Framing.swift:4-13 | ✓ |
| Düz metin/DATA sınırı | 65519 / 65518 | Frames.cs:82-83 | Framing.swift:85-87 | ✓ |
| TCP çerçeve | 2 B BE + Noise ≤65535, sıfır uzunluk geçersiz | TcpFraming.cs | Framing.swift:154-186 | ✓ |
| DATA parça (uygulama) | 64000 | TransferLimits.DefaultChunkSize | `defaultChunkSize` 64_000 | ✓ |
| MANIFEST `files[]` | `id,name,size,mtime(ms)` + `dir?` | Frames.cs:30-35 | Framing.swift:17-29 | ✓ |
| MANIFEST `more` | `{"files":[],"more":bool}`, false/yok = son | `WhenWritingDefault` | `Bool?`, yalnız true yazılır | ✓ |
| MANIFEST sayfa bütçesi | her çerçeve ≤65518 | `min(65518, kanal.MaxFrameBodySize)` (FileSender.cs:75) | **sabit** `65518-64` (FileSender.swift:198); BLE'de kanal sınırını yok sayar | **✗ U2** |
| RESUME/FILE_START/ACK | `{id,offset}` | ✓ | ✓ | ✓ |
| FILE_END | `{id,sha256}` 64 küçük hex | alıcı `OrdinalIgnoreCase` karşılaştırır (FileReceiver.cs:229) | küçük-hex-64 değilse protokol hatası (Framing.swift:129) | ~ U9 |
| DONE / CANCEL | `{ok,failed:[id]}` / `{}`+`reason?` | ✓ | ✓ | ✓ |
| RESUME id = MANIFEST id | (spec yalnız `id`) | gönderen `id = indeks` varsayar (FileSender.cs:103) | `item.id` (iOS de 0..n-1 atar, TransferCoordinator.swift:93) | ✓ (örtük: bkz. U20) |
| Dosya sayısı | ≤10 000 | TransferLimits.MaxFiles | maxFiles | ✓ |
| Ad uzunluğu doğrulama | ≤255 karakter | `Name.Length` (UTF-16) | `utf16.count` | ✓ |
| `dir` | ≤16 seviye, ≤1024 kar. | RelativeDirectory.cs:10-11 | FolderPath.swift:16-18 (UTF-16) | ✓ |
| ACK aralığı | ~4 MB, fsync sonrası | 4 MiB | 4 MiB | ✓ |
| Boş alan payı | "pay" | 64 MiB | 64 MiB | ✓ |
| BLE toplam | ≤64 KB (§6.4a), ≤65536 (§6 m.4 uygulama) | 64*1024 (TransferCoordinator.cs:165) | 64*1024 (TransferTypes.swift:20) | ✓ |
| BLE DATA parçası | **§6.4a: ≤2048; §6 m.4: 8192−16−1** | 2048 (BleDataChannel.cs:44) | 2048 (TransferTypes.swift:22) | ✓ kod, S ✗ U6 |
| BLE hız | "~20 KB/s" | 20*1024 (TransferCoordinator.cs:168) | 20_000 (TransferTypes.swift:24) | ~ U24 |
| Noise mesajı BLE | ≤8192; GATT = 1 B başlık+parça; sıra no mesaj başına mod 128 | BleFragmenter.cs | BleFragmenter.swift (birebir aynı mantık) | ✓ |
| BLE ön ek | 20 B tek yazma, parçalanmaz | ✓ | `writeReliable` (yanıtlı yazma) | ✓ (U21: spec'te yanıtlı yazma yok) |
| Ön ek | "WD"‖sürüm‖taşıma(1/2/3)‖transferId(16, BLE=0) | Prefix.cs | Prefix.swift | ✓ |
| Sürüm uyuşmazlığı | 2 B `0xFF‖sürüm` | Prefix.cs:129-131 | Prefix.swift:64-67 | ✓ |
| Sürüm yanıtını ayırma | **spec'te yok** (kodda: TCP ilk bayt 0xFF; BLE/relay tam 2 B ve ilk bayt 0xFF, yalnız el sıkışma aşaması) | TcpFraming.cs:46-48, Transports.cs:160 | MessageTransport.swift:68,129; RelayTransport.swift:123 | ✓ kod, S ✗ U15 |
| Relay | 4 GiB, 2 sa, 60 sn eş, 60 sn boşta, 10 oda/dk/IP, 409/1008 | Worker index.ts:23-33,110,170-231 | istemci | ✓ (sunucu = spec) |
| Zaman aşımları | spec: aday başı 1,5 sn; hotspot 2 dk; BLE 30 sn çerçeve; push 5 dk; QR 2 dk | 30 sn (Connect), hotspot 2 dk, push 5 dk, QR 2 dk, **Accept/Approval 2 dk**, **RelayPeer 75 sn** | aday 1,5 sn; **teklif yanıtı 20+35 sn**; RESUME 60/30 sn | ✗ U3, U23 |

## 3. Uyumsuzluklar (numaralı)

### U1 (Y) `status`: PC `wifi` göndermiyor, iOS `wifi`'yi zorunlu sayıyor

- C#: `StatusMessage(string? Wifi, bool BtAudio)`, null alan yazılmıyor — `windows/src/WinDrop.Core/Protocol/SignalMessages.cs:35`. Üretim: `windows/src/WinDrop.Platform/TransferCoordinator.cs:214` `new StatusMessage(null, state.BtAudio)` (PC'nin Bluetooth ses durumu her değiştiğinde).
- Swift: `public var wifi: String` (zorunlu) — `ios/WinDropKit/Sources/WinDropKit/Protocol/SignalMessages.swift:21`; `decode` eksik anahtarda `invalidMessage` atar (:204-207). `SignalHub.swift:566` okuma döngüsü hatayı yakalayıp oturumu düşürür (:590, "Hata/kopma: oturum düşer").
- Sonuç: PC kulaklık bağlayınca/çıkarınca iPhone'un BLE oturumu kopar (sonra 1 sn'de yeniden kurulur); süren BLE aktarımı ve onay bekleyen teklif ölür. Testler yalnız `wifi` dolu `status` sınıyor (BleTransferTests.swift:131, ProtocolTests.swift:216,238), PC biçimi hiçbir yerde sınanmıyor.
- **Önerilen tek doğru davranış:** S §4: `status.wifi` isteğe bağlıdır (PC göndermez; yalnız iPhone gönderir); `btAudio` zorunlu. Swift `StatusMessage.wifi: String?` yapılır, `hello` ile aynı `none/connected` doğrulaması eklenir. Çapraz vektör: `{"t":"status","btAudio":true}` iki yönde çözülmeli.

### U2 (O) iOS gönderen MANIFEST sayfalamasında kanal sınırını yok sayıyor

- C#: `limit = Math.Min(FrameCodec.MaxBody, _channel.MaxFrameBodySize)` — `windows/src/WinDrop.Core/Transfer/FileSender.cs:75`; BLE'de 2048 (BleDataChannel.cs:44).
- Swift: `budget = FrameCodec.maxBody - 64` sabit — `ios/WinDropKit/Sources/WinDropKit/Transfer/FileSender.swift:197-198`; `sendEncodedFrame` 8175 B'ı aşan MANIFEST sayfasında `messageTooLarge` atar (SecureChannel.swift:128), `ProtocolError` → "protocol error" CANCEL.
- Sonuç: iPhone→PC BLE yolunda (toplam ≤64 KB ama yaklaşık ≥90–100 dosya) aktarım protokol hatasıyla biter. Toplam ≤64 KB koşulu dosya sayısını sınırlamıyor.
- **Öneri:** Swift `paginate(budget:)` bütçesini `min(maxBody, channel.maxFrameBodySize) - zarf` yapar; S §9a: "MANIFEST çerçevesi ≤ taşımanın çerçeve gövdesi sınırı (TCP 65518, BLE gönderen ≤2048)".

### U3 (O) Teklif zaman aşımı: iPhone 55 sn'de vazgeçiyor, PC kullanıcı onayı 2 dk bekliyor

- Swift: 20 sn + 35 sn sonra `fail` (iPhone→PC teklif) — `ios/App/Shared/Transfers/TransferCoordinator.swift:53-54,137-151`; `fail` PC'ye `cancel` göndermez (:681-686), sonradan gelen `accept` yok sayılır (:162, state `.failed`).
- C#: `Timeouts.Accept` ve `Approval` 2 dk — `windows/src/WinDrop.Platform/TransferCoordinator.cs:76-78`; PC onay penceresi iPhone vazgeçtikten sonra da açık kalır, onaylanınca veri bağlantısı hiç gelmez.
- **Öneri:** Tek değer: gönderen teklif yanıtı bekleme = alıcı onay süresi + pay (2 dk 15 sn); iPhone zaman aşımında `cancel` sinyali yollar. S'ye "teklif yanıt/onay süreleri" bölümü.

### U4 (O) IK reddi için tel işareti yok; iOS herhangi bir IK hatasında eşleşmeyi siliyor

- C#: güvenilmeyen anahtar → `PeerNotTrustedException`, msg2 gönderilmez, WDR yok, `fault=false` (`windows/src/WinDrop.Platform/SignalSessionAcceptor.cs:169-172`).
- Swift: IK denemesi *herhangi* bir nedenle patlar ve yerel düşme değilse "PC reddetti" sayılıp `identity.remove(known)` (kayıt + otomatik kabul + hotspot parolası) çağrılır — `ios/App/Shared/Ble/SignalHub.swift:520-529`. Geçici BLE hatası/zaman aşımı da eşleşmeyi siler.
- S §0 (16), §1 (22-23), §3a: ret biçimi tanımsız.
- **Öneri:** S: iPhone kaydı yalnız aynı bağlantı turunda XX el sıkışması **başarıyla** bitip PC IK'yi kabul etmediği anlaşılınca (ör. XX kabul edildi) siler; tek başına IK hatası silme sebebi değildir. İsteğe bağlı: PC ret için `WDR` yollasın ki iPhone nedeni ayırt etsin.

### U5 (O) Yol sırası: S §6 madde 3 (relay) / madde 4 (BLE), kod BLE'yi relay'den önce deniyor

- Kod: doğrudan → etkin nokta → BLE (küçük) → relay — `windows/src/WinDrop.Platform/TransferCoordinator.cs:1096-1131`; Swift PC'nin seçimini bekler (TransferCoordinator.swift:508-513). S §6 listesi 1,2,3(relay),4(BLE) ve madde 4 "aksi halde yol yok hatası" diyor (satır 235-241), relay'e düşüşten söz etmiyor.
- **Öneri:** S: sıra "doğrudan → etkin nokta → BLE → aktarma sunucusu"; BLE uygun değilse relay (ayarlıysa), yoksa "yol yok". Madde numaraları buna göre.

### U6 (O) BLE DATA/MANIFEST sınırı S içinde üç farklı değer

- S satır 236 (§6 m.4): "DATA ≤ 8192 − 16 − 1 B"; satır 199 (§6.4a) ve 239: "≤ 2048 B (iOS seçimi)"; satır 83-85: Noise mesajı ≤8192. Kod: gönderen 2048, alıcı ≤8175 kabul eder (SecureChannel `MaxFrameBodySize`).
- **Öneri:** S: "BLE'de gönderen DATA ve MANIFEST gövdesini ≤2048 B tutar; alıcı ≤8175 B (8192−16−1) kabul eder." Satır 236 buna göre düzeltilir.

### U7 (D) Relay/Push adresinde yol öneki: C# atıyor, Swift koruyor

- C#: `UriBuilder(..., "/v1/room/" + roomId, ...)` — `windows/src/WinDrop.Core/Session/RelayRoom.cs:70`; push `.../PushNotifier.cs:63`. Swift: `comps.path = path + "/v1/room/" + roomId` — `ios/WinDropKit/Sources/WinDropKit/Session/RelayTransport.swift:68`; push `ios/App/Sources/Push/PushManager.swift:150`.
- Worker yalnız kökte dinler (`relay/src/index.ts:33`, `push.ts:18`): `relayUrl = wss://host/ön-ek` girilirse PC ve iPhone farklı yollara gider.
- **Öneri:** S §6.3 "Adres": `relayUrl` yalnız `host[:port]` (yol öneki yok); iki taraf da önek içeren adresi reddeder.

### U8 (D) `ws://` yerel döngü ayırt etme farklı

- C#: `Uri.IsLoopback` (127.0.0.0/8, ::1, localhost) — RelayRoom.cs:67; Swift: yalnız `localhost`/`127.0.0.1`/`::1` — RelayTransport.swift:56. Tek doğru: S'de açıkça "localhost, 127.0.0.1, ::1".

### U9 (D) FILE_END `sha256` doğrulaması

- Swift büyük harf/yanlış uzunluğu protokol hatası sayar (Framing.swift:127-130); C# `OrdinalIgnoreCase` ile kabul eder, uzunluk yanlışsa yalnız bütünlük hatası (DONE.failed) (FileReceiver.cs:229, :265).
- **Öneri:** S §9a zaten "64 küçük hex" der; C# `DecodeFileEnd` Swift gibi katılaştırılır (hata = protokol hatası).

### U10 (D) `hotspot.ssid/pass` "her zaman" ama tipler isteğe bağlı, yorumlar eskimiş

- S satır 113 "her zaman"; C# yorum "yalnız değiştiyse" (SignalMessages.cs:55), alanlar `= null`; Swift eski `TODO(windows-sync)` (TransferCoordinator.swift:435) ve isteğe bağlı alan.
- **Öneri:** İki tarafta `ssid`/`pass` zorunlu yapılır (PC zaten her zaman dolduruyor, TransferCoordinator.cs:969); yorum/TODO silinir. (Swift `PairingController.swift:126` eski `hotspot`'tan kimlik bilgisi alma kolaylığı kalabilir; S §2 madde 7 yalnız `pairInfo` der.)

### U11 (D) Dosya adı: kısaltma birimi ve Unicode normalizasyonu (madde 3)

- C#: sınır 255 **UTF-16 kod birimi**, normalizasyon yok (FileNameSanitizer.cs:25, 105-113). Swift: sınır 255 **UTF-8 bayt**, NFC ve NFD'nin büyüğü; kesim karakter sınırında (FileNameSanitizer.swift:13,113-116,163-170).
- Çapraz etkisi (uyumsuzluk değil, yerel fark): (a) 200 karakterlik Çince/Türkçe ad Windows'ta aynen, iOS'ta kısaltılarak kaydedilir; (b) iOS gönderen APFS'ten ham (bazen NFD) ad yollar, C# alıcı normalleştirmez → Windows'ta ayrışık "ş" saklanır; aynı görünümlü NFC ve NFD ad ikisi de kaydedilebilir (NTFS normalizasyon duyarsız değil), iOS alıcıda APFS ikincisini "ad (2)" yapar. Veri kaybı/ezme yok; sanitizer yalnız alıcıya yerel (MANIFEST adı ham gider), hash'e etki etmez.
- Hata/ezme üretmez, ama S §5 "ad ≤255 karakter" (satır 143) belirsiz. **Öneri:** S: "gönderen ≤255 UTF-16 birimi; alıcı yerel sınıra (NTFS 255 UTF-16, APFS 255 UTF-8 bayt NFD) göre kısaltır, uzantıyı (≤20) korur; Windows alıcı adı NFC'ye çevirir (iOS dokunmaz)". NFC adımı C# sanitizer'a eklenirse test eklenmeli.

### U12 (D) Reddetme nedeni kodları serbest metin

- C# `silenced/bad_count/busy/rate_limited/declined/no_space` (TransferCoordinator.cs:774-902); Swift `busy`, `rate` (TransferCoordinator.swift:229,237). S yalnız `busy`, `rate_limited` adlandırıyor (satır 61). C# `reason`'ı ham metin olarak kullanıcıya gösteriyor (:750).
- **Öneri:** S'te sabit küme tanımla; iOS `rate` → `rate_limited`; arayüz koda göre yerelleştirir.

### U13 (D) `hello.caps`: "relay" hiçbir tarafta yok, denetim yalnız "hotspot"

- S satır 108 `["tcp","hotspot","relay"]`; ikisi de `["tcp","hotspot"]` yollar (TransferCoordinator.cs:325, PairingService.cs:225, SignalHub.swift:14); INTEROP örneği "relay" içerir.
- **Öneri:** Ya `relay` yalnız ayarlı (relayUrl dolu) tarafta eklenip PC'de denetlenir, ya da S: "caps bilgi amaçlıdır; yalnız `hotspot` yol seçimini etkiler".

### U14 (D) `relay` ve `path` için yön

- S satır 114 `relay` "iki yön"; yalnız PC→iPhone üretilir/işlenir (iPhone'dan gelen `relay` C#'ta "beklenmeyen", TransferCoordinator.cs:418). `path`: `*_ok` sonuçları PC'de işlenmez (OnPath :432).
- **Öneri:** S: `relay` yönü PC→iPhone; `path.*_ok` bilgi amaçlı.

### U15 (D) Sürüm uyuşmazlığı ayrımı kuralı yalnız kod yorumunda

- Prefix.cs:22-25, Transports.cs:160, MessageTransport.swift:34,129, RelayTransport.swift:122. S §1 (28-29) "tek bayt `0xFF‖sürüm`" der, §9a (263) "2 bayt".
- **Öneri:** S §1: "Yanıtlayan `0xFF‖sürüm` (2 bayt, ham) yollar. Başlatan bunu yalnız ilk el sıkışma yanıtında arar: TCP'de ilk bayt 0xFF (gerçek yanıtın uzunluk üst baytı 0x00), BLE/relay'de tam 2 baytlık ve ilk baytı 0xFF olan yük."

### U16 (D) Oturum yeniden başlatma (§3a): bekleme döngüsü her taşıma ön ekini kabul ediyor

- C#: `Prefix.TryDecode(prefix, out _)` yeter (SignalSessionAcceptor.cs:67); oturum ortası yeniden başlatma `Transport==Ble` şartlı (Transports.cs:155); S satır 94-96 "geçerli BLE ön eki". **Öneri:** bekleme döngüsüne de `Transport==Ble` şartı.

### U17 (D) Token/transferId "kanonik" ayrıştırma farkı

- Swift yeniden kodlayıp eşitlik arar (TransferId.swift:36-37, RelayTransport.swift:37); C# `Base64Url.TryDecodeFromChars` (TransferId.cs:36-50, RelayRoom.cs:25-40) son bit kanonikliğini ayrıca denetlemiyor (BCL davranışı **doğrulanmadı**). Tek doğru: "kanonik kodlama zorunlu"; C# `ToString()==text` denetimi eklenir.

### U18 (D) Doğrulama asimetrisi (iOS PC'nin reddettiğini kabul ediyor)

- C# `Validate` (SignalMessages.cs:172-208): bilinmeyen `wifi`, negatif `count/total`, preview >5, port ∉ 1–65535, 8176 B üstü → ProtocolException (oturum düşer). Swift `decode` bunları denetlemez (SignalMessages.swift:176-209). Sorun üretmez (iOS bunları üretmez); PC'den gelen bozuk mesaj iOS'ta işlenebilir. **Öneri:** doğrulama kuralları S §4'e yazılır, Swift'e eklenir.

### U19 (D) PC adı/telefon adı ayıklaması simetrik değil

- PC `hello.name`'i kontrol/bidi karakterlerinden arındırır (PairingService.cs:236-248, S §2.8); iOS PC adını (XX, tanıdık değilse) yalnız `trim` + 64 karakter (SignalHub.swift:575-581). Bidi içeren PC adıyla arayüz yanıltılabilir. **Öneri:** S: her iki taraf `hello.name`'e aynı ayıklamayı uygular.

### U20 (D) `RESUME.id`/`FILE_START.id` = MANIFEST `id` ama `id = indeks` örtük

- C# gönderen RESUME id'yi indeksle karşılaştırır (FileSender.cs:103); iOS id'leri 0..n-1 atar (TransferCoordinator.swift:93). `FolderScanner.items(startId:)` (FolderScanner.swift:15) farklı id'ye izin verir. **Öneri:** S §5: "`id` = MANIFEST sırasındaki 0 tabanlı indeks."

### U21 (D) BLE yazma türü: spec yanıtlı yazma şartını söylemiyor

- S satır 71: `c2p` Write Without Response + Write; kod: iOS tüm yazmaları yanıtlı yapar (MessageTransport.swift:82-91, BlePeripheralLink); STATUS.md: Windows GATT yanıtsız yazmaları düşürüyor. **Öneri:** S §3: "iPhone `c2p`'ye yalnız Write With Response yazar."

### U22 (D) Klasör kuralları: iOS'ta aynı adlı iki klasör, `X:` biçimi, sınır aşımı sonucu

- (a) C# gönderen aynı adlı iki klasörü "ad (2)" yapar (FolderScanner.cs `UniqueTopName`); Swift yapmaz → alıcı aynı kökte birleştirir (FileReceiver.swift:238-247). S bunu "Windows uygulama" notu olarak yazar (satır 189-190).
- (b) C#: bileşen tam `X:` ise protokol hatası (RelativeDirectory.cs:28), `C:foo` → `:`→`_`; Swift `u[1]==':'` ile `C:foo`'yu da hata sayar (FolderPath.swift:36; yorum "Windows alıcıyla aynı" yanlış).
- (c) 16 seviye/1024 aşımı: C# `ProtocolException` (CANCEL "protocol error", FileReceiver.cs:140); Swift `AbortSignal` (CANCEL "dir limit exceeded", FileReceiver.swift:128). S sınır aşımının sonucunu tanımlamıyor.
- **Öneri:** (a) kural gönderen tarafa (iki platform) taşınır; (b) "2. karakteri `:` olan bileşen = protokol hatası"; (c) "sınır aşımı = alıcı CANCEL (`limit`), biçim hatası = protokol hatası".

### U23 (D) Diğer zaman aşımı/sayı farkları

- RESUME bekleme: Swift gönderen 60 sn (TCP)/30 sn (BLE) (TransferTypes.swift:26-27); C# gönderen sınırsız bekler (FileSender.cs:102), yalnız BLE'de 30 sn bekçi var (TransferCoordinator.cs:`RunBleTransferAsync`). Relay eş bekleme: PC 75 sn (TransferCoordinator.cs:86) > sunucu 60 sn (index.ts:27). BLE hız: C# 20480 B/sn, Swift 20000 B/sn (U24). **Öneri:** S'e tek "zaman aşımları" tablosu: RESUME 60/30 sn, relay eş 60 sn, BLE sessizlik 30 sn, BLE hız 20 000 B/sn.

### U24 (D) BLE hız sabiti — U23'e bak (20 480 vs 20 000).

### U25 (D) Süren BLE aktarımında `btAudio=true` kesmesi yalnız PC'de

- S satır 240 "bir tarafın btAudio'su true olursa kesilir" (PC uygulaması olarak yazılı); Swift `status`'tan yalnız `remoteBtAudio` saklar (SignalHub.swift:583-584), aktarımı kesmez; PC keser (TransferCoordinator.cs:393). **Öneri:** S: iki taraf da `status.btAudio=true` alınca BLE aktarımı CANCEL eder (iOS koduna eklenir).

### U26 (D) Veri oturumu yenileme kuralı S içinde çelişkili

- S §5 satır 129: ikinci bağlantıda eski kapatılır; §6 "Yol değişimi" satır 171: aktif oturum varken ikinci bağlantı kapatılır. Kod: `keepExistingActive`/`DualPath` bayrağı (DataListener.cs:21-27).
- **Öneri:** S: "Varsayılan: yeni (doğrulanmış) bağlantı eskiyi kapatır (yeniden bağlanma). PC iki yolu birden açtıysa (`addr`+`hotspot`) mevcut etkin oturum korunur, ikinci bağlantı kapatılır."

### U27 (D) Eskimiş kod yorumları ve belgeler

- Frames.cs:8-26 ("spesifikasyon tür baytını vermiyor"; §9a artık veriyor), BleFragmenter.cs:6-12 ("iOS ile uyuşmazlık demektir; netleştirilmeli"; iOS aynı okumayı kullanıyor, BleFragmenter.swift:4-5), SignalMessages.cs:55 (hotspot), Swift TransferCoordinator.swift:435 (TODO). `docs/INTEROP.md`: "11 sinyal türü", "6/6 senaryo" — şimdi 13 tür var (`bleData`, `pushId`) ve kapsam U1'i yakalayamıyor.

## 4. Madde madde: istenen kontrollerin cevapları

1. **Sinyaller:** yukarıdaki tablo. Tek yönlü (doğru): `addr`, `hotspot`, `pairInfo`, `relay`, `bleData` yalnız PC→iPhone (Swift hiç üretmez; C# bunları alırsa günlüğe yazıp yok sayar, TransferCoordinator.cs:418); `path`, `pushId` yalnız iPhone→PC. Hata sayılan fark: U1 (status). Yok sayılan: iPhone'dan gelen `relay` (U14).
2. **Çerçeveler/sabitler:** hepsi aynı (tablo 2); farklar: MANIFEST bütçesi (U2), sha256 katılığı (U9), BLE hızı (U23), BLE DATA S içi (U6).
3. **Dosya adı:** MANIFEST adı ham taşınır; temizleme yalnız alıcıda; tel uyumsuzluğu yok. Yerel farklar U11 (UTF-16 vs UTF-8 bayt, NFC/NFD), U22(b). Alıcı kuralları (yasak karakterler, ayrılmış adlar, bidi, sondaki nokta/boşluk, `_` ile değiştirme) iki tarafta aynı kümeyi uygular (karşılaştırıldı: FileNameSanitizer.cs:90-137 ↔ .swift:91-192). Ayrılmış ad karşılaştırması C# `OrdinalIgnoreCase`, Swift ASCII büyük harf (özel Unicode kıvrımlarında fark olabilir, **doğrulanmadı**).
4. **El sıkışma:** desen seçimi (32=XX, 48=XXpsk3, 96=IK) yalnız C# `AcceptAutoAsync`'te (SecureChannel.cs:322-359); Swift hep başlatan, deseni kimlikten seçer (SignalHub.swift:513-535), veri kanalında BLE oturumunun desenini kullanır (`useIK: peer.usedIK`, TransferCoordinator.swift:529). XX'te boş onay mesajı yok / IK'de var: iki tarafta aynı. 48/32/96 dışı uzunluk: S tanımlamıyor (kod IK sayıp hata atar). `WDR` iki tarafta aynı (BleCentral.swift:574; Transports.cs:89-92). Yeniden başlatma: U4, U16. Sürüm yanıtı: U15.
5. **SAS / roomId / pushId:** SAS: `SHA-256("WinDrop-SAS"‖h)` ilk 4 B big-endian mod 10000, `%04d`: Sas.cs ↔ SecureChannel.swift:18-26, vektör `protocol/vectors/sas.json`. IK oturumunda kod gösterilmez (Swift `code: ""`, SignalHub.swift:552) — S yalnız XX diyor, açıkça "IK'de yok" demiyor. roomId: RelayRoom.cs:43-50 ↔ RelayTransport.swift:21-26, vektör `relay.json`. pushId: 32 küçük hex, üç yerde aynı (C#, Swift, Worker).
6. **S iç çelişkileri:** bölüm 5.
7. **Test kapsamı:** bölüm 6.

## 5. PROTOCOL.md iç çelişkiler, eskimiş maddeler, numaralama

| # | Sorun | Yer (satır) | Düzeltme |
|---|---|---|---|
| S1 | `pairInfo` tablo satırı dosyanın **1. satırında**, başlıktan önce yetim; §4 tablosunda yok | 1 ve 106-117 | Satırı §4 tablosuna taşı |
| S2 | §2 "Eşleştirme (bir kez)" zorunlu gibi, §2b "eşleştirmesiz varsayılan". §0 "Eşli cihaz" tanımında **tanıdık cihaz** yok; §1 "Sonraki her bağlantı IK" (22-23) ve §7 "Eşli olmayan IK'yi tamamlayamaz" (246-247) XX gerçeğini yansıtmıyor | 9-16, 22-23, 33-45, 47-61, 246-247 | §0'a "tanıdık" tanımı; §1/§7'de "IK yalnız eşli/tanıdık, bilinmeyen XX"; §2'yi "İsteğe bağlı QR eşleştirme (güvenilir/otomatik kabul)" yap |
| S3 | §0 "kaldırılınca karşı taraf bir sonraki **IK** denemesinde reddedilir" ama ret biçimi tanımsız, sonraki bağlantı XX+onay (§9a/kod) | 15-16 | U4 |
| S4 | §6 SSID/parola "eşleştirmede bir kez üretilir, eşleştirme kanalıyla gönderilir, tüm eşli iPhone'lar aynı SSID" — oysa eşleştirmesiz modda `hotspot` her seferinde `ssid/pass` taşır, parola yenileme var (165-170) | 156-159 ↔ 113, 165-170 | "İlk kullanımda üretilir, `hotspot`/`pairInfo` ile gönderilir; eşli olmayan eş sonrası yenilenir" |
| S5 | §6 numaralama: 1, 2, (madde dışı "Yol değişimi"), 3, sonra §5a/§6.4a/§6.5/§6.3 başlıkları, **sonra yetim "4. BLE"** (235). "§6.4" başlığı yok (kod ve S bu adla atıf yapıyor) | 151-243 | Bölüm 6'yı yeniden yaz (bkz. plan) |
| S6 | §6 madde 4 ↔ §6.4a: DATA boyutu (8192−16−1 / 2048), "yol yok hatası" (relay'e düşüş yok), sıra (BLE relay'den önce) | 235-243 ↔ 193-199 | U5, U6 |
| S7 | §5 "ikinci bağlantıda eskisi kapatılır" ↔ §6 "ikinci bağlantıyı kapatır" | 129 ↔ 171 | U26 |
| S8 | §1 "tek bayt `0xFF‖sürüm`" ↔ §9a "2 bayt" | 28-29 ↔ 263 | U15 |
| S9 | §5 "`mime` yalnız bilgi amaçlıdır" — MANIFEST'te `mime` alanı yok | 149 | Sil |
| S10 | `caps` listesi "relay" içerir, kimse göndermez | 108 | U13 |
| S11 | `relay` "iki yön" | 114 | U14 |
| S12 | §4 `status` alanları (wifi zorunlu gibi) ↔ PC `wifi` yollamıyor | 109 | U1 |
| S13 | §9 "Açık sorular": ① cevaplanmış ama "gerçek iPhone ile doğrulanacak" (STATUS 2026-10-18: BLE keşif cihazda çalıştı); ③ `NetworkOperatorTetheringManager`: ayar kaydet/geri yükle ve internet yokken açma doğrulandı (STATUS: `PlatformCheck hotspot-cycle`, ağsız etkin nokta); ④ aktarma sunucusu cevaplandı (§6.3, Worker); ⑤ Windows Noise kütüphanesi cevaplandı (NSec + kendi Noise, cacophony 59 vektör). Yalnız ② (arka planda bekleyen BLE, ücretsiz hesap) açık | 284-290 | ①③④⑤ kapat/ilgili bölüme taşı; ②'yi "açık" bırak |
| S14 | Başlık "taslak v0.1, 2026-10-17" ve §1 "vektörler (yazılacak)" — içerik 2026-10-18 ekli, `protocol/vectors/` var | 2-7 | Sürüm/tarih güncelle, vektör dizinine atıf |
| S15 | §1'de normatif kurallar §9a'ya dağılmış: yanıtlayan desen seçimi (272-276), IK/XX onayı (270-277), BLE ayrımı `{`/1–8 (280), sürüm uyuşmazlığı (263) | 255-282 | §9a'yı "Kodlama" olarak tut ama normatif kuralları §1/§3/§4'e taşı |
| S16 | §5a "Windows uygulama (2026-10-18)" notu platform-özel ama iki tarafı bağlayan kurallar içeriyor (aynı ad "ad (2)", `X:`) | 189-191 | Platform-nötr kurala çevir (U22) |
| S17 | §3a ön ek eşiği "WD+sürüm+1+16 sıfır" ↔ §9a "tek, parçalanmamış yazma" tekrarı | 95-98, 264 | Tek yerde tut |
| S18 | Numara dizisi: §0,1,2,2b,3,3a,4,5,6,5a,6.4a,6.5,6.3,7,8,9a,9 | tüm dosya | Plan aşağıda |

## 6. Testlerle kapsanmayan çapraz davranışlar

**Ortak vektörler** (`protocol/vectors/`): cacophony.txt (Noise, 59 giriş), `sas.json`, `relay.json`. **Yok:** BLE parça/sıra no, çerçeve/JSON gövde, ön ek, sinyal JSON, dosya adı, MANIFEST sayfalama vektörü.

**Interop betiği** (`windows/tools/WinDrop.Interop`, `docs/INTEROP.md`, CI `.github/workflows/interop.yml`): gerçek TCP IK/XX, 3 dosya (29 B, 3 MB, 25 MB) Swift→C#, C#→Swift 2 dosya, yanlış transferId, yabancı anahtar, BLE=IK/TCP=XX uyuşmazlığı, 200 MB SIGKILL + RESUME; relay r1–r3 (IK, XX, 409); 11 sinyal örneğinin iki yönde gidiş-dönüşü (Program.cs:59-76 ↔ main.swift:180-194).

**Kapsam dışı (öneri sırasıyla):**

1. **PC'nin ürettiği JSON biçimleri**: `status` `wifi`'siz (U1), `hello` `wifi`'siz, `hotspot` ssid/pass'siz/ile, `accept/reject` `reason`'sız; `bleData`, `pushId` sinyalleri (örnek listede yok, 11/13 tür).
2. **MANIFEST `more=true`** (çok sayfa) çapraz; **`dir`** (klasör) çapraz; boş dosya (size 0); Unicode/NFD/uzun adlar çapraz (yalnız tek taraflı testler: FileNameNfdTests.swift, FileNameSanitizerTests.cs).
3. **BLE hattı hiç çapraz denenmedi** (INTEROP "Sınırlar"): fragment/reassemble sıra no, ön ek tek yazma, `WDR`, oturum ortası yeniden başlatma, `bleData` akışı, BLE'de MANIFEST sayfalama (U2), 2048 B DATA, hız sınırı. Gerçek cihaz testi var (STATUS 2026-10-18) ama otomatik yok.
4. **CANCEL/DONE.failed/RESUME-ACK**: yalnız SIGKILL+RESUME; çapraz CANCEL (iki yön, `reason`), `DONE.failed` (bütünlük hatası), sınır ihlali kesmeleri (10 000 dosya, boyut aşımı, `dir` sınırları) çapraz yok.
5. **XXpsk3 eşleştirme** (QR ayrıştırma, `pairInfo` akışı, SAS XXpsk3) çapraz yok; QR `n` yüzde-kodu ve Swift `URLComponents` çözümü karşılaştırılmadı.
6. **Rate limit / sessizlik / busy** (S §2b) iki tarafta farklı politika (C# tam; Swift yalnız 5/3 dk) ve çapraz test yok.
7. **Relay token/URL biçimi** (U7, U8, U17), push `from`/HTTP kodları: Worker testleri (43) kendi başına; C#/Swift istemcileri Worker'a karşı çapraz denenmiyor (yalnız relay r1–r3).
8. **Zaman aşımı tablosu** (U3, U23): hiçbir test iki tarafın zaman aşımlarını birlikte denemiyor.
9. **IK reddi** (U4) ve eşleşme kaldırma sonrası yeniden bağlanma.

**Öneri:** `protocol/vectors/signals.json` (13 tür × "PC biçimi" ve "iPhone biçimi", beklenen decode sonucu) ve `frames.json` (MANIFEST sayfa, `dir`, CANCEL, DONE) eklenip iki testin okuması; interop betiğine `signals` örneklerinin 13 türe çıkarılması ve BLE-fragmenter için bayt vektörü.

## 7. PROTOCOL.md için yeniden düzenleme önerisi (başlık planı)

Önerilen tek sıra (eski numaralar parantezde; kodun atıf yaptığı §1/§2/§2b/§3/§3a/§4/§5/§5a/§6.3/§6.5/§9a numaraları korunacaksa yalnız yeni "Ek A" olarak eski→yeni tablosu ver):

0. Durum, sürüm, tarih; ortak vektörler (`protocol/vectors/`)
1. Terimler ve anahtarlar (eski §0): cihaz anahtarı, eşli / **tanıdık** / bilinmeyen, başlatan = iPhone
2. Kripto ve açık ön ek (eski §1 + §9a'dan: desen seçimi 32/48/96, IK/XX boş onay, sürüm uyuşmazlığı ayrım kuralı)
3. Güven ve tanışma
   3.1 Yakındaki herkes, XX, doğrulama kodu (eski §2b)
   3.2 İsteğe bağlı QR eşleştirme (eski §2)
   3.3 Eşleştirme kaldırma ve IK reddi (S3/U4)
   3.4 Kötüye kullanım sınırları ve ret nedeni kodları (U12)
4. BLE katmanı: servis/UUID, parçalama, sıra no, oturum sıfırlama `WDR` (eski §3 + §3a + §9a BLE maddeleri + U21)
5. Sinyal mesajları: tek tablo (13 tür, yön, zorunlu/isteğe bağlı alan, doğrulama), JSON türleri (eski §4 + §9a sinyal JSON)
6. Veri kanalı (eski §5 + §9a): çerçeveler, MANIFEST sayfalama (taşıma başına sınır), bütünlük/RESUME, sınırlar, dosya adı kuralları, **klasör gönderme** (eski §5a), veri oturumu yenileme (U26)
7. Yol seçimi — **tek bölüm, sırayla**: 7.1 doğrudan (addr), 7.2 etkin nokta (hotspot, parola yönetimi), 7.3 BLE küçük dosya (eski §6.4a/§6 m.4), 7.4 aktarma sunucusu (eski §6.3), 7.5 yol değişimi, 7.6 zaman aşımları tablosu (U3, U23)
8. Kullanıcı onayı ve otomatik kabul (eski §7)
9. Push bildirimi (eski §6.5)
10. Sürümleme (eski §8)
11. Açık sorular (yalnız gerçekten açık olanlar: arka planda bekleyen BLE)
Ek A: eski→yeni bölüm eşlemesi (kod yorumlarındaki §atıfları için). Ek B: ortak vektör ve interop kapsamı.

Kod yorumlarındaki `§6.4`, `§6 madde 3`, `§9a` atıfları yeniden düzenlemeyle birlikte güncellenmeli (grep: `PROTOCOL §`).

## 8. Doğrulanamadı / belirsiz

- Derleme/test/interop çalıştırılmadı (salt-okur); yukarıdaki davranışlar kod okumasına dayanır. U1 için bire bir yeniden üretim (gerçek PC→iPhone `status`) yapılmadı; kod yolu net (zorunlu alan + yakalanan hata).
- .NET `Base64Url` kanonik olmayan son bitleri reddediyor mu (U17) ve `OrdinalIgnoreCase`'in Unicode özel kıvrımları (U11) **doğrulanmadı**.
- iOS tarafı yalnız kaynaktan okundu; CoreBluetooth/NWConnection davranışı (yanıtlı yazma, bekleyen bağlantı) gerçek cihaz bulgularına (STATUS.md) güvenildi.
