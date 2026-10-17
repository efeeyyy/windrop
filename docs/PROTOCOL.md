| `pairInfo` | PC→iPhone | `ssid`, `pass` — yalnız QR eşleştirme kanalında (§2), PC onayından sonra; etkin nokta bilgisi. Eşleştirmesiz modda (§2b) kullanılmaz: `hotspot` her seferinde `ssid`/`pass` taşır. |
# WinDrop Protokolü — taslak v0.1

Durum: taslak (2026-10-17). v0 opus-high güvenlik incelemesinden geçti; bulgular bu sürüme
işlendi. Dayanak: `ARCHITECTURE.md`. Windows (C#) ve iOS (Swift) bu belgeyi uygular; iki tarafı
ortak test vektörleri (`protocol/vectors/`, yazılacak) bağlar. Belge ile kod çelişirse önce
belge düzeltilir.

## 0. Terimler ve anahtarlar
- **Cihaz anahtarı:** kalıcı X25519 çifti. Windows: DPAPI (`CurrentUser`) ile korunan dosya.
  iOS: Keychain, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (kilitliyken arka planda BLE
  el sıkışması çalışabilsin). Cihaz kimliği = açık anahtarın SHA-256'sının ilk 16 baytı.
- **Eşli cihaz:** açık anahtarı eşleştirmeyle doğrulanmış, güvenilen listedeki cihaz. Kayıtta
  eşleştirme anındaki **ad** tutulur; arayüzde her zaman bu ad gösterilir (`hello.name` değil).
- **Eşleştirmeyi kaldırma:** iki tarafta da listeden silme; karşı taraf bir sonraki IK
  denemesinde reddedilir. Anahtar yenileme = yeniden eşleştirme.
- **Başlatan:** **iPhone her zaman Noise başlatanıdır** (BLE'de GATT istemcisi, TCP'de bağlanan).
  PC her zaman yanıtlayan.

## 1. Kriptografi
- Eşleştirme: `Noise_XXpsk3_25519_ChaChaPoly_SHA256`, psk = QR'daki 32 bayt sır.
- Sonraki her bağlantı: `Noise_IK_25519_ChaChaPoly_SHA256`. PC, başlatanın statik anahtarını
  ilk mesajdan öğrenir; güvenilen listede yoksa bağlantıyı keser.
- **0-RTT yok:** IK'nin ilk mesaj yükü boştur. PC, başlatandan ilk geçerli taşıma mesajını
  almadan oturumu kurulmuş saymaz (yeniden oynatılan ilk mesaj oturum kuramaz).
- **Açık ön ek** (el sıkışmadan önce, şifresiz): `"WD" || sürüm (1 B) || taşıma (1 B: 1=BLE,
  2=TCP, 3=relay) || transferId (16 B; BLE'de 16 sıfır bayt)`. Bu 20 bayt aynen **prologue**
  olarak da kullanılır. Sürüm uyuşmazsa yanıtlayan, el sıkışmadan önce tek bayt `0xFF || desteklenen
  sürüm` gönderip kapatır (kullanıcıya "güncelleme gerekli" gösterilebilsin).
- Uygulama: Windows'ta denetlenmiş bir Noise kütüphanesi veya libsodium üstünde küçük uygulama;
  iOS'ta CryptoKit. İkisi de resmi Noise test vektörlerinden (cacophony) geçmeden kullanılmaz.

## 2. Eşleştirme (bir kez)
1. PC ekranında QR: `windrop://pair?v=1&k=<PC açık anahtarı b64url>&s=<32 B sır b64url>&n=<PC adı>`.
2. iOS kamerayla okur, BLE ile PC'ye bağlanır, `XXpsk3` el sıkışması (iPhone başlatan).
3. iOS, gelen PC statik anahtarının QR'daki `k` ile aynı olduğunu doğrular; değilse iptal.
4. **PC'de onay:** "iPhone '<ad>' eşleşsin mi?" — kullanıcı onaylamadan kayıt yapılmaz
   (QR'ın fotoğrafı çekilmiş olabilir). Onay ekranı, XXpsk3 el sıkışma özetinden §2b ile aynı biçimde 4 haneli doğrulama kodu gösterir (iPhone ekranındakiyle karşılaştırılır).
5. Sır tek kullanımlık; QR 2 dk sonra veya başarılı eşleşmede geçersiz. Başarısız denemelere
   **hız sınırı** (ör. 1/sn) uygulanır; sayaçla QR iptal edilmez (yakındaki biri iptal ettiremesin).
   32 baytlık psk çevrim dışı kaba kuvvete dayanıklıdır.
6. 6 haneli kod ile eşleştirme **yapılmaz** (XXpsk3'te kısa psk çevrim dışı kırılır). Gerekirse
   ileride CPace gibi bir PAKE ile ayrı tasarlanır.
7. Eşleştirme sonunda PC, şifreli kanaldan önce `hello`, sonra **`pairInfo {ssid, pass}`** (§4) gönderir (§6.2); iPhone bilgiyi alınca bağlantıyı kapatır.
8. QR'daki `n` **yüzde-kodlu UTF-8** PC adıdır; `k` ve `s` dolgusuz base64url. `pairInfo` öncesi PC, iPhone'un `hello`'sunu bekler (ad buradan; kontrol/bidi karakterleri ayıklanır, ≤ 64 karakter) ve onay ister. Sır, XXpsk3 el sıkışması **başarılı olunca** (onaydan önce) kilit altında atomik (`TryConsume`) tüketilir; aynı QR ile yarışan ikinci bağlantı tüketimi kaybeder ve kapatılır.

## 2b. Yakındaki herkes (eşleştirmesiz, varsayılan — 2026-10-17 kararı)
- iPhone uygulaması **açıkken** (gönderme ekranı veya ön plan) BLE taraması yapar ve gördüğü her
  WinDrop PC'sine kendisi bağlanır (iPhone yine central ve başlatan). Bilinmeyen PC ile:
  `Noise_XX_25519_ChaChaPoly_SHA256` (psk yok); bilinen PC ile IK.
- El sıkışmadan sonra iki taraf `hello` gönderir. PC bu iPhone'u "yakında" listesine ekler; iPhone
  PC'yi gönderme ekranında gösterir. Böylece PC→iPhone gönderimi de bu bağlantı üzerinden yapılır
  (PC central rolüne gerek kalmaz).
- **Doğrulama kodu:** XX el sıkışma özeti `h`'den `SHA-256("WinDrop-SAS" || h)`'in ilk 4 baytı
  mod 10000 → 4 hane; teklif ve onay ekranlarında iki tarafta gösterilir (karşılaştırma isteğe bağlı).
- Eşli olmayan cihazdan gelen `offer` **her zaman** alıcı onayı ister (otomatik kabul yok).
  Onaylanan karşı taraf "tanıdık cihaz" olarak (açık anahtar + ad + tarih) saklanır; sonraki
  bağlantılar IK. Reddedilen veya hiç onaylanmayan cihaz saklanmaz.
- Veri kanalında (§5) IK yerine, bu oturum tanıdık değilse XX kullanılır ve yanıtlayan, karşı
  statik anahtarın BLE oturumundakiyle **aynı** olduğunu doğrular (aktarım o eşe bağlı kalır).
- Kötüye kullanım: tanıdık (eşli olmayan) cihazdan eş başına dakikada en çok 3 `offer`; reddedilenden 2 dk sessizlik. **Bilinmeyen** (XX) cihazlar için sınır PeerId'ye bağlı değildir (yeni anahtarla aşılamaz): aynı anda en çok **1** onay bekleyen bilinmeyen teklif (fazlası `reject busy`) ve **toplam** 3 teklif/dk (`reject rate_limited`).

## 3. BLE katmanı (yalnız keşif ve sinyal)
Ölçüm: BLE toplu verisi Bluetooth sesini kesiyor → BLE'den yalnız küçük mesajlar.
- **Roller:** PC = yalnız GATT sunucusu (peripheral). iPhone = yalnız central, başlatan.
  - İlk keşif: iPhone ön plandayken PC reklamını tarar.
  - Eşleşmeden sonra iPhone, PC'nin BLE tanımlayıcısına zaman aşımsız **bekleyen bağlantı** tutar
    (durum geri yüklemeli); PC menzile girince arka planda da bağlanır. Reklamda servis UUID'si
    gerekmez (Windows'taki 128 bit reklam sorununu aşar).
- **Servis:** 128 bit UUID (ölçüm aracının UUID'leri kullanılmaz).
- **Karakteristikler:** `c2p` (Write Without Response + Write, iPhone→PC), `p2c` (Notify, PC→iPhone).
- **UUID'ler (2026-10-17, kod: `ProtocolIds.cs`):** servis `493c70b6-634d-4935-be1f-b79b1d05dbd8`,
  `c2p` = `c235bdc3-b25a-49a5-963b-5040e65b708d`, `p2c` = `ae26ead2-8247-4508-bcb6-31d11568bf07`.
  **Yedek servis UUID'si** `6c5a0f3d-0000-1000-8000-00805f9b34fb` (SIG tabanlı 32 bit `0x6C5A0F3D`; aynı `c2p`/`p2c`):
  Windows 128 bit servis reklamı `Aborted` olursa (Realtek RTL8852BE'de olur) PC servisi bu UUID altında kurar.
  iPhone ikisini de aramalı: `discoverServices([birincil, yedek])`, bulunan servisin `c2p`/`p2c`'sini kullanır.
- **Yerel ad işareti:** `GattServiceProvider` reklama yerel ad koyamaz (Windows bilgisayar adını kullanır) ve
  `BluetoothLEAdvertisementPublisher.LocalName` bu makinede reddedilir. Bu yüzden PC ayrıca bağlanılamaz bir
  reklam yayınlar: üretici verisi, şirket kimliği `0xFFFF`, yük = `"WD" || 0x01 || ASCII "WinDrop-<PC adı>"` (en çok 19 B).
  iPhone ön planda filtresiz tararken servis UUID'sini **veya** bu üretici verisini tanıyıp aynı cihaza bağlanır
  (aynı Bluetooth adresi varsayımı — gerçek iPhone ile doğrulanmadı).
- **Açılış:** iPhone `c2p`'ye önce §1'deki 20 baytlık ön eki, sonra el sıkışma mesajlarını yazar.
- **Parçalama birimi tek bir Noise mesajıdır** (el sıkışma mesajları dahil). Bir Noise mesajı
  en fazla 8192 bayt (şifreli metin, etiket dahil). GATT yükü = 1 B başlık + parça; başlık bit7 =
  son parça, bit0–6 = mesaj sıra no (mod 128).
- Bir yönde aynı anda **tek mesaj** iletilir; mesajlar iç içe geçmez.
- Parça kaybı, sıra no atlaması veya bağlantı kopması **oturumu düşürür**; yeni el sıkışma
  gerekir (Noise sayaçları kayar).

## 4. Sinyal mesajları (BLE oturumu içinde)
Gövde: UTF-8 JSON `{"t": "<tür>", ...}`. Bilinmeyen alanlar yok sayılır; bilinmeyen `t` hatadır.
| t | Yön | Alanlar |
|---|---|---|
| `hello` | iki yön | `v`, `name`, `caps` (["tcp","hotspot","relay"]), `wifi` (iPhone: `none`/`connected`), `btAudio` (bool) |
| `status` | iki yön | `wifi`, `btAudio` — değiştiğinde yeniden gönderilir |
| `offer` | gönderen→alıcı | `transferId`, `count`, `total` (bayt), `preview`: en çok 5 dosya adı (tam liste veri kanalında, §5) |
| `accept` / `reject` | alıcı→gönderen | `transferId`, `reason?` |
| `addr` | PC→iPhone | `transferId`, `candidates`: [{`ip`, `port`}] |
| `hotspot` | PC→iPhone | `transferId`, `ip`, `port`, `ssid`, `pass` (her zaman; §2b eşleştirmesiz modda iPhone bunları başka yoldan bilmez — yalnız şifreli kanaldan gider) |
| `relay` | iki yön | `transferId`, `token` (§6.3; URL gönderilmez) |
| `path` | iPhone→PC | `transferId`, `result`: `tcp_ok`/`tcp_fail`/`hotspot_ok`/`hotspot_fail`/`relay_ok`/`relay_fail` |
| `cancel` | iki yön | `transferId` |
| `bleData` | PC→iPhone | `transferId` (§6.4a; ardından gönderen taraf MANIFEST ile BLE üzerinden başlar) |

Aktarımın sonucu **yalnız veri kanalındaki `DONE`** ile belirlenir (BLE'de `done` yok).

## 5. Veri kanalı (TCP; doğrudan yol ve etkin nokta aynı)
- **PC dinler** (port 53600, IPv4+IPv6), **iPhone bağlanır**; yön veri akışından bağımsız.
  PC güvenlik duvarı kuralı kurulumla gelir (etkin nokta arayüzünün profili yok → kural
  `192.168.137.0/24` için profil bağımsız; ARCHITECTURE bulgusu).
- Açılış: §1 ön eki (`taşıma=2`, `transferId`), sonra IK el sıkışması.
- **TCP çerçevesi:** `uzunluk (2 B big-endian) || Noise mesajı` (≤ 65535). Düz metin = `tür (1 B) ||
  gövde`, en çok 65519 B. Kontrol çerçevelerinin gövdesi UTF-8 JSON; `DATA` gövdesi ham bayt,
  en çok **65518 B** (uygulamalar 64000 kullanır).
- Her `transferId` için **tek** veri oturumu; ikincisi gelirse eski kapatılır.
- Çerçeveler:
  - `MANIFEST` (gönderen, ilk çerçeve): `files`: [{`id`, `name`, `size`, `mtime`}]; `count`/`total`
    `offer` ile uyuşmazsa alıcı keser.
  - `RESUME {id, offset}` (alıcı → gönderen, her dosyadan önce; yeni dosyada offset=0).
  - `FILE_START {id, offset}` (gönderen, alınan RESUME ofsetiyle) → `DATA`… → `FILE_END {id, sha256}`.
  - `ACK {id, offset}` (alıcı, ~4 MB'da bir, **diske yazıp fsync ettikten sonra**).
  - `CANCEL`, `DONE {ok, failed: [id]}` (alıcı → gönderen, sonuç).
- **Bütünlük:** SHA-256 her zaman dosyanın **0. baytından itibaren tamamı** üzerinden. Devam
  ederken gönderen ilk `offset` baytı yeniden okuyup hash'e katar; alıcı kendi kısmi dosyasını
  aynı biçimde hash'ler. Uyuşmazlıkta dosya silinir, `DONE`'da `failed` listesine girer.
- **Devam ettirme:** kısmi dosya `(eş kimliği, transferId, id)` ile anahtarlanır. RESUME ofseti en
  çok son ACK'lenen ofsettir. `MANIFEST`'te `size` veya `mtime` değişmişse dosya baştan gönderilir.
- **Sınırlar (alıcı uygular):** kabulden önce `total` ≤ boş alan − pay; dosya başına bildirilen
  `size`'ı aşan veri → aktarım kesilir; en çok 10 000 dosya, ad ≤ 255 karakter; otomatik kabulün
  boyut üst sınırı (ayar, varsayılan 2 GB); cihaz başına `offer` hız sınırı (ör. 5/dk).
- **Dosya adı (alıcı):** yalnız son yol bileşeni; `..`, `/`, `\`, `:` (NTFS akışı), denetim
  karakterleri ve Windows ayrılmış adları (CON, PRN, AUX, NUL, COM1–9, LPT1–9; uzantılı biçimleri
  dahil) reddedilir veya `_` ile değiştirilir; sondaki nokta/boşluk silinir; var olan dosyanın
  üzerine yazılmaz ("ad (2).ext"); Windows'ta Mark-of-the-Web (Zone.Identifier) eklenir;
  `mime` yalnız bilgi amaçlıdır.

## 6. Yol seçimi (kullanıcı görmez)
1. **iPhone bir Wi-Fi'a bağlıysa** (`wifi=connected`): PC `addr` gönderir; iPhone adaylara paralel
   bağlanır (aday başına 1,5 sn); ilk başarılı el sıkışma kazanır. Başarısızsa → 3.
2. **iPhone hiçbir Wi-Fi'da değilse** (`wifi=none`): doğrudan yol denenmez; PC **hemen** Mobil
   etkin noktayı açar ve `hotspot` gönderir; iPhone katılır, sonra 1'deki gibi bağlanır.
   - SSID/parola: **eşleştirmede PC başına bir kez rastgele** üretilir (SSID son eki 32 bit, parola
     128 bit → base32, ≥ 22 karakter), DPAPI ile saklanır, eşleştirme kanalıyla gönderilir;
     **türetilmez**. SSID'de kullanıcı/cihaz adı yok; gizli ağ yapılmaz (gizli ağ iPhone'u her
     yerde sorgu yayınlatıp izlenebilir kılar). Tüm eşli iPhone'lar aynı SSID'yi kullanır.
   - Windows Mobil etkin nokta ayarı sistem genelindedir: açmadan önce kullanıcının mevcut
     ayarı kaydedilir, kapanınca geri yüklenir. Kullanıcı etkin noktayı zaten kullanıyorsa
     onun ayarı korunur ve bu yol atlanır (doğrulanacak: API davranışı).
   - Etkin nokta son aktarımdan **2 dk** sonra PC tarafından kapatılır.
   - iPhone bir Wi-Fi'a bağlıyken bu yol **asla** denenmez (Efe şartı).
   - **Parola yenileme (2026-10-17 güvenlik kararı):** `hotspot` (SSID + parola) yalnız **kullanıcının onayladığı**
     aktarımda gönderilir (alıcıda onay veya eşli cihaz için otomatik kabul; gönderende kullanıcının seçtiği eş). Eş, aktarım
     başlarken `TrustedDevices`'ta (eşli veya tanıdık) **değilse**, aktarım bitince (iptal/hata dahil) PC kalıcı parolayı
     **yeniler** (SSID aynı kalır, DPAPI kaydı güncellenir; açık kalan etkin nokta bir sonraki başlatmada yeni parolayla
     yeniden kurulur). Eşli/tanıdık cihazlar için parola sabit kalır (iOS ağı hatırlasın diye). iPhone her zaman o anki
     parolayı `hotspot` iletisinden alır; yenilenmiş parola kayıtlı ağın üzerine yazılır.
3. **Aktarma:** iki taraf internetteyse kullanıcıya uyarı: "İnternet/mobil veri kullanılacak
   (X MB), uçtan uca şifreli". Onaylanırsa: aktarma sunucusu adresi **uygulamaya gömülü/yapılandırılmış**
   alan adıdır, mesajla gelmez (kötü niyetli eşli cihaz iPhone'u kendi sunucusuna yönlendiremez).
   `token` tek kullanımlık, kısa ömürlü, `transferId`'ye bağlı; hesap kimlik bilgisi gönderilmez.
   Kanal: §1 ön eki (`taşıma=3`) + IK/XX (BLE oturumuyla aynı desen ve eş anahtarı) + §5 çerçeveleri.
   Uyarı metni (Efe): "Bu gönderim Wi-Fi veya hücresel veri kotanızdan gidecektir." + boyut;
   iki tarafta da gösterilir, gönderen onaylamadan bağlantı kurulmaz.

### 6.4a BLE veri yolu başlatma (2026-10-17 kesinleşti)
- Yolu **her zaman PC seçer**. BLE yolu uygunsa (total ≤ 64 KB, iki tarafta btAudio=false, diğer
  yollar yok/başarısız) PC şifreli BLE kanalından `{"t":"bleData","transferId":...}` gönderir.
  Bunu alan/gönderen taraf: gönderen ise MANIFEST'i BLE üzerinden başlatır; alıcı ise MANIFEST bekler.
- iPhone zamanlayıcıyla kendiliğinden BLE'ye geçmez (yarış olmasın). TCP/etkin nokta denemesi
  başarısız olursa iPhone `path` (`tcp_fail`/`hotspot_fail`) gönderir; PC sonraki yolu seçer.
- BLE DATA parçası ≤ 2048 B (iOS seçimi), hız ≤ ~20 KB/s.

### 6.3 Aktarma sunucusu (2026-10-17 tasarım)
- **Sunucu:** Cloudflare Worker + Durable Object (`relay/`); ücretsiz katman. Yalnız bayt aktarır;
  içerik uçtan uca Noise ile şifreli, sunucu dosya adını bile görmez.
- **Oda:** PC 32 B rastgele `token` üretir, `relay {transferId, token}` ile (şifreli BLE) iPhone'a
  verir. `roomId = hex(SHA-256("WinDrop-relay" || token))[0:32]`. İki taraf da istemcidir:
  `wss://<relay>/v1/room/<roomId>?role=pc|phone`. Token URL'de gitmez. `relay` sinyalinde `token`
  = ham 32 baytın **dolgusuz base64url**'i (43 karakter); roomId ham baytlardan hesaplanır
  (vektörler: `protocol/vectors/relay.json`). Ön ek ve her Noise mesajı **ayrı** ikili WebSocket
  mesajıdır.
- **Eşleme:** oda ilk `pc` ve ilk `phone` bağlantısını eşler; üçüncü bağlantı ve aynı rolden ikinci
  bağlantı 409 ile reddedilir. Eş 60 sn içinde gelmezse oda kapanır.
- **Aktarım:** her WebSocket ikili mesajı = bir Noise mesajı (≤ 65535 B; uzunluk öneki yok).
  Sunucu mesajları sırayla karşıya iletir; metin mesajı gelirse bağlantıyı kapatır.
- **Sınırlar:** oda başına toplam ≤ 4 GiB, ömür ≤ 2 saat, 60 sn boşta kapanış; IP başına dakikada
  ≤ 10 oda açma (kötüye kullanım). Aşımda iki bağlantı da 1008 ile kapanır.
- **Adres:** uygulama ayarında (`relayUrl`); boşsa aktarma yolu kapalıdır ve "yol yok" hatası verilir.
  Sunucuyu Efe kendi Cloudflare hesabına `wrangler deploy` ile kurar (hesap açma kullanıcıya ait).
- **Sırası:** iPhone (başlatan) bağlanınca ön ek + el sıkışma ilk mesajını gönderir; PC yanıtlar.
  Oturum düşerse yeni `token` ile yeni oda; devam ettirme §5 RESUME ile.
4. **BLE:** yalnız `total ≤ 64 KB` ve iki tarafta `btAudio=false` ise; BLE oturumu üstünden §5
   çerçeveleri (DATA ≤ 8192 − 16 − 1 B). Aksi halde "yol yok" hatası.
   - **Uygulama (PC, 2026-10-18):** addr/etkin nokta kurulamadı veya başarısız (`path` `*_fail`, bağlantı zaman aşımı) ve `total` ≤ 65536 B
     ve iki taraf `btAudio=false` ise PC `bleData` yollar (§6.4a). Çerçevelerde `transferId` yoktur: eş başına **tek** süren BLE aktarımına
     aittir; `bleData`dan önce gelen çerçeve oturumu düşürür. Gönderen PC `bleData`dan hemen sonra `MANIFEST` yollar. DATA ≤ 2048 B,
     hız ≤ ~20 KB/s. Aktarım sürerken bir tarafın `btAudio`su `true` olursa (`status`) aktarım kesilir; eş 30 sn çerçeve göndermezse düşer.
     Uygun değilse özgün "yol yok" nedenine BLE nedeni (boyut / Bluetooth ses) eklenir.
   - `btAudio` (PC): varsayılan ses çıkışı Bluetooth (PKEY_Device_EnumeratorName `BTH*`) **ve** aktif ses oturumu var; oturum
     durumu okunamazsa Bluetooth çıkış varsayılansa `true`. `hello`'da gerçek değer, değişince `status`.

## 7. Kullanıcı onayı
- Eşli cihazdan gelen `offer` varsayılan olarak onay ister; "eşli cihazlardan otomatik kabul"
  ayarı boyut üst sınırıyla. Eşli olmayan cihaz IK'yi tamamlayamaz.
- iOS arka plandayken gelen `offer` yerel bildirim olur; dokununca uygulama öne gelir ve aktarım
  başlar (arka planda büyük aktarım hedeflenmez).

## 8. Sürümleme
- Sürüm §1 açık ön ekte (şifresiz) taşınır; uyuşmazlık el sıkışmadan önce bildirilir.
- `hello.v` bilgi amaçlıdır; büyük sürüm ön ekle belirlenir.

## 9a. Kodlama ayrıntıları (2026-10-17 kesinleşti; C# `WinDrop.Core` ile)
- **Çerçeve tür baytı:** MANIFEST=1, RESUME=2, FILE_START=3, DATA=4, FILE_END=5, ACK=6, CANCEL=7, DONE=8.
  Bilinmeyen tür → protokol hatası. Sıfır uzunluklu TCP çerçevesi geçersiz.
- **JSON (camelCase, alanlar zorunlu):** `id` int; `size`, `offset` int64 bayt; `mtime` int64 Unix
  epoch **milisaniye**; `sha256` 64 karakter küçük harf hex; CANCEL gövdesi `{}` veya `{"reason": "..."}`;
  DONE `{"ok": bool, "failed": [int]}`. null alanlar yazılmaz.
- **Sinyal JSON (§4):** `transferId` = 16 bayt, **base64url, dolgusuz** (22 karakter); `wifi` dize
  (`none`/`connected`); `caps` dize listesi; `count` int; `total` int64; `btAudio` bool; `v` int.
- **Sürüm uyuşmazlığı yanıtı:** 2 bayt `0xFF, desteklenen_sürüm`, sonra kapatma.
- **BLE:** 20 baytlık ön ek `c2p`'ye **tek, parçalanmamış** yazmadır (MTU ≥ 23). Sonraki Noise
  mesajlarında **sıra no mesaj başınadır** (bir mesajın tüm parçaları aynı no'yu taşır), 0'dan başlar,
  her mesajda 1 artar (mod 128); beklenmeyen no → oturum düşer. Parça yükü ≤ ATT MTU − 3 − 1.
- **MANIFEST sayfalama:** gövde `{"files": [...], "more": bool}` (`more` yoksa false). Gönderen,
  her çerçeve ≤ 65518 B olacak şekilde listeyi böler; alıcı `more=false` gelene kadar toplar, sonra
  `count`/`total` kontrolünü yapar.
- **IK onayı:** başlatan el sıkışmadan hemen sonra **boş düz metinli** bir taşıma mesajı gönderir;
  yanıtlayan bunu alınca oturumu kurulmuş sayar ve mesajı uygulamaya iletmez (§1 0-RTT kuralı).
- **Yanıtlayan deseni (2026-10-17, tel üzerinde işaret yok):** PC, ön ekten sonraki **ilk el sıkışma mesajının
  uzunluğundan** deseni ayırır: **32 bayt = XX** (`e`), **48 bayt = XXpsk3** (`e` + boş yükün 16 B etiketi; yalnız
  eşleştirme modu açıksa kabul, değilse kapatılır), **96 bayt = IK** (`e` + şifreli `s` + etiket). Böylece QR ekrandayken
  bile IK ve XX çalışır. IK'de karşı anahtar güvenilen listede değilse msg2 gönderilmeden kapatılır.
- **XX onayı:** XX'te yanıtlayan msg3'ten sonra kurulmuş sayılır (taze `e` yeniden oynatmayı engeller); başlatan
  IK gibi boş taşıma mesajı göndermez. Doğrulama kodu (§2b) XX el sıkışma özeti `h` üzerinden, ilk 4 bayt **big-endian uint32** mod 10000, 4 hane (sıfır dolgulu).
- **Veri kanalı deseni:** BLE oturumu IK ise TCP'de IK, XX ise TCP'de XX; her ikisinde karşı statik anahtar BLE
  oturumundakiyle aynı olmalı, aksi halde PC bağlantıyı kapatır (XX'te başlatan kapanışı ilk okumada anlar).
- **BLE oturumunda ayrım:** düz metnin ilk baytı `{` ise sinyal JSON'u, 1–8 ise §5 çerçevesi.
- **Dosya adı:** uygun olmayan karakter/ad reddedilmez, `_` ile **değiştirilir**; ek olarak
  `< > " | ? *`, C1 denetim ve bidi karakterleri de değiştirilir.

## 9. Açık sorular
- ~~Windows'ta ilk keşif için hangi reklam biçimi çalışıyor~~ (2026-10-17): 128 bit `Aborted`; SIG tabanlı 32 bit yedek servis + üretici verisi işareti (§3) kullanılıyor. Gerçek iPhone ile doğrulanacak: iPhone'un işareti/UUID'yi görüp bağlanması, her iki servisin keşfi.
- iOS bekleyen BLE bağlantısının ücretsiz hesapta, arka planda gerçek davranışı.
- `NetworkOperatorTetheringManager`: internet yokken açılabiliyor mu; ayarı kaydet/geri yükle
  mümkün mü; bant seçimi.
- Aktarma sunucusu: TURN mı, kendi WebSocket aktarması mı; kötüye kullanım sınırları.
- Windows Noise kütüphanesi seçimi (bakım, lisans, test vektörleri, IK + XXpsk3 desteği).
