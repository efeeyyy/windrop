# WinDrop Protokolü — v0.2

Durum: v0.2 (2026-11-15). Tel protokol sürümü 1 (§10). Dayanak: `ARCHITECTURE.md`. Windows (C#, `WinDrop.Core`) ve
iOS (Swift, `WinDropKit` + uygulama) bu belgeyi uygular. İki tarafı ortak test vektörleri (`protocol/vectors/`,
Ek B) bağlar. Belge ile kod çelişirse önce belge düzeltilir. İnceleme sonrası bağlayıcı kararlar
`docs/review/DECISIONS.md` dosyasındadır; bu belgede ilgili bölümlere normatif metin olarak işlenmiştir.

## 1. Terimler ve anahtarlar
- **Cihaz anahtarı:** kalıcı X25519 çifti. Windows: DPAPI (`CurrentUser`) ile korunan dosya.
  iOS: Keychain, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (kilitliyken arka planda BLE
  el sıkışması çalışabilsin). Cihaz kimliği = açık anahtarın SHA-256'sının ilk 16 baytı.
- **Eşli cihaz:** açık anahtarı QR eşleştirmesiyle (§3.2) doğrulanmış, güvenilen listedeki cihaz. Kayıtta
  eşleştirme anındaki **ad** tutulur; arayüzde her zaman bu ad gösterilir (`hello.name` değil).
- **Tanıdık cihaz:** eşleştirilmemiş ama "yakındaki herkes" akışında (§3.1) kullanıcının bir teklifi onayladığı
  cihaz; açık anahtar + ad + tarih saklanır. Sonraki bağlantılar IK ile yapılır.
- **Bilinmeyen cihaz:** ne eşli ne tanıdık. İlk temas XX ile yapılır; her teklifi alıcı onayı ister (§8).
- **Güvenilen liste:** eşli ve tanıdık cihazlar (PC'de `TrustedDevices`). IK yalnız bu listedeki anahtarlarla kurulur.
- **Başlatan:** **iPhone her zaman Noise başlatanıdır** (BLE'de GATT istemcisi, TCP'de bağlanan).
  PC her zaman yanıtlayan.

## 2. Kriptografi ve açık ön ek
- **Desenler:**
  - Bilinmeyen cihazla ilk temas ("yakındaki herkes", §3.1): `Noise_XX_25519_ChaChaPoly_SHA256` (psk yok).
  - Eşleştirme (§3.2): `Noise_XXpsk3_25519_ChaChaPoly_SHA256`, psk = QR'daki 32 bayt sır.
  - Güvenilen listedeki (eşli/tanıdık) cihazla her bağlantı: `Noise_IK_25519_ChaChaPoly_SHA256`. PC, başlatanın
    statik anahtarını ilk mesajdan öğrenir; güvenilen listede yoksa IK reddi işlemi (§3.3) uygulanır.
- **Açık ön ek** (el sıkışmadan önce, şifresiz): `"WD" || sürüm (1 B) || taşıma (1 B: 1=BLE,
  2=TCP, 3=relay) || transferId (16 B; BLE'de 16 sıfır bayt)`. Bu 20 bayt aynen **prologue**
  olarak da kullanılır.
- **Sürüm uyuşmazlığı:** yanıtlayan, el sıkışmadan önce ham **2 bayt** `0xFF || desteklenen sürüm` gönderip
  kapatır (kullanıcıya "güncelleme gerekli" gösterilebilsin). Sıfır uzunluklu TCP çerçevesi geçersiz olduğundan
  başlatan bu yanıtı yalnız ilk el sıkışma yanıtında arar: TCP'de ilk bayt `0xFF` (gerçek yanıtın uzunluk üst baytı 0x00),
  BLE ve relay'de tam 2 baytlık ve ilk baytı `0xFF` olan yük.
- **Yanıtlayan deseni (tel üzerinde ayrı işaret yok):** PC, ön ekten sonraki **ilk el sıkışma mesajının
  uzunluğundan** deseni ayırır: **32 bayt = XX** (`e`), **48 bayt = XXpsk3** (`e` + boş yükün 16 B etiketi; yalnız
  eşleştirme modu açıksa kabul, değilse kapatılır), **96 bayt = IK** (`e` + şifreli `s` + etiket). Böylece QR ekrandayken
  bile IK ve XX çalışır.
- **0-RTT yok:** IK'nin ilk mesaj yükü boştur. Yanıtlayan, başlatandan ilk geçerli taşıma mesajını
  almadan oturumu kurulmuş saymaz (yeniden oynatılan ilk mesaj oturum kuramaz).
  - **IK:** başlatan el sıkışmadan hemen sonra **boş düz metinli** bir taşıma mesajı gönderir; yanıtlayan bunu
    alınca oturumu kurulmuş sayar ve mesajı uygulamaya iletmez.
  - **XX:** yanıtlayan msg3'ten sonra kurulmuş sayılır (taze `e` yeniden oynatmayı engeller); başlatan IK gibi
    boş taşıma mesajı göndermez.
- Uygulama: Windows'ta denetlenmiş bir Noise uygulaması (NSec üstünde kendi Noise motoru); iOS'ta CryptoKit. İkisi de
  resmi Noise test vektörlerinden (cacophony, `protocol/vectors/cacophony.txt`) geçmeden kullanılmaz.

## 3. Güven ve tanışma

### 3.1 Yakındaki herkes (eşleştirmesiz, varsayılan)
- iPhone uygulaması **açıkken** (gönderme ekranı veya ön plan) BLE taraması yapar ve gördüğü her
  WinDrop PC'sine kendisi bağlanır (iPhone yine central ve başlatan). Bilinmeyen PC ile XX, bilinen PC ile IK.
- El sıkışmadan sonra iki taraf `hello` gönderir. PC bu iPhone'u "yakında" listesine ekler; iPhone
  PC'yi gönderme ekranında gösterir. Böylece PC→iPhone gönderimi de bu bağlantı üzerinden yapılır
  (PC central rolüne gerek kalmaz).
- **Doğrulama kodu:** XX el sıkışma özeti `h` üzerinden `SHA-256("WinDrop-SAS" || h)`'in ilk 4 baytı
  **big-endian uint32** mod 10000 → 4 hane (sıfır dolgulu); teklif ve onay ekranlarında iki tarafta gösterilir
  (karşılaştırma isteğe bağlı). Ortak vektör: `protocol/vectors/sas.json`.
- Eşli olmayan cihazdan gelen `offer` **her zaman** alıcı onayı ister (otomatik kabul yok).
  Onaylanan karşı taraf "tanıdık cihaz" olarak (açık anahtar + ad + tarih) saklanır; sonraki
  bağlantılar IK. Reddedilen veya hiç onaylanmayan cihaz saklanmaz.
- Veri kanalında (§6) IK yerine, bu oturum tanıdık değilse XX kullanılır ve yanıtlayan, karşı
  statik anahtarın BLE oturumundakiyle **aynı** olduğunu doğrular (aktarım o eşe bağlı kalır).

### 3.2 İsteğe bağlı QR eşleştirme
QR eşleştirme zorunlu değildir; eşli cihaz, tanıdık cihazdan farklı olarak otomatik kabul ayarına (§8) uygundur.
1. PC ekranında QR: `windrop://pair?v=1&k=<PC açık anahtarı b64url>&s=<32 B sır b64url>&n=<PC adı>`.
   `n` **yüzde-kodlu UTF-8** PC adıdır; `k` ve `s` dolgusuz base64url.
2. iOS kamerayla okur, BLE ile PC'ye bağlanır, `XXpsk3` el sıkışması (iPhone başlatan).
3. iOS, gelen PC statik anahtarının QR'daki `k` ile aynı olduğunu doğrular; değilse iptal.
4. **PC'de onay:** "iPhone '<ad>' eşleşsin mi?" — kullanıcı onaylamadan kayıt yapılmaz
   (QR'ın fotoğrafı çekilmiş olabilir). Onay ekranı, XXpsk3 el sıkışma özetinden §3.1 ile aynı biçimde 4 haneli
   doğrulama kodu gösterir (iPhone ekranındakiyle karşılaştırılır).
5. Sır tek kullanımlık; QR 2 dk sonra veya başarılı eşleşmede geçersiz. Başarısız denemelere
   **hız sınırı** (ör. 1/sn) uygulanır; sayaçla QR iptal edilmez (yakındaki biri iptal ettiremesin).
   32 baytlık psk çevrim dışı kaba kuvvete dayanıklıdır.
6. 6 haneli kod ile eşleştirme **yapılmaz** (XXpsk3'te kısa psk çevrim dışı kırılır). Gerekirse
   ileride CPace gibi bir PAKE ile ayrı tasarlanır.
7. Eşleştirme sonunda PC, şifreli kanaldan önce `hello`, sonra **`pairInfo {ssid, pass}`** (§5) gönderir (§7.2);
   iPhone bilgiyi alınca bağlantıyı kapatır.
8. `pairInfo` öncesi PC, iPhone'un `hello`'sunu bekler (ad buradan; kontrol/bidi karakterleri ayıklanır, ≤ 64 karakter)
   ve onay ister. Sır, XXpsk3 el sıkışması **başarılı olunca** (onaydan önce) kilit altında atomik (`TryConsume`)
   tüketilir; aynı QR ile yarışan ikinci bağlantı tüketimi kaybeder ve kapatılır.

### 3.3 Eşleştirmeyi kaldırma ve IK reddi
- Eşleştirme/tanıdık kaydı iki tarafta da listeden silinerek kaldırılır. Anahtar yenileme = yeniden eşleştirme.
- **IK reddi işareti:** PC, IK ilk mesajındaki statik anahtar güvenilen listede yoksa, msg2 göndermeden ve bağlantıyı
  kapatmadan önce `p2c` üzerinden tek bildirim olarak 3 bayt ASCII **`WDU`** (unknown) gönderir (§4.3). iPhone eşleşme/tanıdık
  kaydını **yalnız `WDU` alınca** siler ve aynı çevre birimine XX ile yeniden dener (kaldırılan cihaz bilinmeyen gibi
  davranır: teklifler onay ister). Başka hiçbir IK hatası (kopma, zaman aşımı, `WDR`, sıra hatası) kaydı silmez.

### 3.4 Kötüye kullanım sınırları ve ret nedenleri
- Tanıdık (eşli olmayan) cihazdan eş başına dakikada en çok 3 `offer`; reddedilenden 2 dk sessizlik.
- **Bilinmeyen** (XX) cihazlar için sınır PeerId'ye bağlı değildir (yeni anahtarla aşılamaz): aynı anda en çok **1** onay
  bekleyen bilinmeyen teklif (fazlası `reject busy`) ve **toplam** 3 teklif/dk (`reject rate_limited`).
- Cihaz başına genel `offer` hız sınırı için bkz. §6.5.

## 4. BLE katmanı (yalnız keşif ve sinyal)
Ölçüm: BLE toplu verisi Bluetooth sesini kesiyor → BLE'den yalnız küçük mesajlar.

### 4.1 Roller, servis, reklam
- **Roller:** PC = yalnız GATT sunucusu (peripheral). iPhone = yalnız central, başlatan.
  - İlk keşif: iPhone ön plandayken PC reklamını tarar.
  - Eşleşmeden sonra iPhone, PC'nin BLE tanımlayıcısına zaman aşımsız **bekleyen bağlantı** tutar
    (durum geri yüklemeli); PC menzile girince arka planda da bağlanır. Reklamda servis UUID'si
    gerekmez (Windows'taki 128 bit reklam sorununu aşar).
- **Servis:** 128 bit UUID (ölçüm aracının UUID'leri kullanılmaz).
- **Karakteristikler:** `c2p` (Write Without Response + Write, iPhone→PC), `p2c` (Notify, PC→iPhone).
- **UUID'ler (kod: `ProtocolIds.cs`):** servis `493c70b6-634d-4935-be1f-b79b1d05dbd8`,
  `c2p` = `c235bdc3-b25a-49a5-963b-5040e65b708d`, `p2c` = `ae26ead2-8247-4508-bcb6-31d11568bf07`.
  **Yedek servis UUID'si** `6c5a0f3d-0000-1000-8000-00805f9b34fb` (SIG tabanlı 32 bit `0x6C5A0F3D`; aynı `c2p`/`p2c`):
  Windows 128 bit servis reklamı `Aborted` olursa (Realtek RTL8852BE'de olur) PC servisi bu UUID altında kurar.
  iPhone ikisini de aramalı: `discoverServices([birincil, yedek])`, bulunan servisin `c2p`/`p2c`'sini kullanır.
- **Yerel ad işareti:** `GattServiceProvider` reklama yerel ad koyamaz (Windows bilgisayar adını kullanır) ve
  `BluetoothLEAdvertisementPublisher.LocalName` bu makinede reddedilir. Bu yüzden PC ayrıca bağlanılamaz bir
  reklam yayınlar: üretici verisi, şirket kimliği `0xFFFF`, yük = `"WD" || 0x01 || ASCII "WinDrop-<PC adı>"` (en çok 19 B).
  iPhone ön planda filtresiz tararken servis UUID'sini **veya** bu üretici verisini tanıyıp aynı cihaza bağlanır
  (aynı Bluetooth adresi varsayımı).

### 4.2 Açılış, parçalama, sıra numarası
- **Açılış:** iPhone `c2p`'ye önce §2'deki 20 baytlık ön eki, sonra el sıkışma mesajlarını yazar. Ön ek `c2p`'ye
  **tek, parçalanmamış** yazmadır (en küçük ATT yükü, MTU ≥ 23).
- **Parçalama birimi tek bir Noise mesajıdır** (el sıkışma mesajları dahil). Bir Noise mesajı
  en fazla 8192 bayt (şifreli metin, etiket dahil); dolayısıyla düz metin ≤ 8176 B. GATT yükü = 1 B başlık + parça
  (parça yükü ≤ ATT MTU − 3 − 1); başlık bit7 = son parça, bit0–6 = mesaj sıra no (mod 128).
- **Sıra no mesaj başınadır:** bir mesajın tüm parçaları aynı no'yu taşır; 0'dan başlar, her mesajda 1 artar (mod 128);
  beklenmeyen no → oturum düşer.
- Bir yönde aynı anda **tek mesaj** iletilir; mesajlar iç içe geçmez.
- Parça kaybı, sıra no atlaması veya bağlantı kopması **oturumu düşürür**; yeni el sıkışma
  gerekir (Noise sayaçları kayar).
- **Oturumda ayrım:** şifre çözülmüş düz metnin ilk baytı `{` ise sinyal JSON'u (§5), 1–8 ise veri çerçevesi (§6).

### 4.3 Oturum sıfırlama ve ret işaretleri
- **`WDR`:** PC bir BLE oturumunu herhangi bir protokol hatasıyla (sıra no atlaması, şifre çözme hatası, beklenmeyen
  ham yük) kapattığında, aynı istemciye `p2c` üzerinden **tek bildirim olarak 3 bayt ASCII `WDR`** gönderir
  (parçalama başlığı yok; el sıkışma/oturum dışı işaret). Ardından o istemciden gelen yazmaları, yeni bir
  20 baytlık ön ek gelene kadar yok sayar (hata vermez).
- **`WDU`:** IK reddi işareti (§3.3); aynı biçim (tek bildirim, 3 bayt ASCII, parçalama başlığı yok). `WDR`'den farkı:
  iPhone kaydı silip XX ile yeniden dener.
- **İstemci yeniden başlatması:** oturum ortasında (el sıkışma veya taşıma aşaması) gelen GATT yükü **tam 20 bayt ve geçerli BLE ön eki**
  (`WD` + sürüm + taşıma=1 + 16 sıfır bayt) ise PC bunu "istemci oturumu yeniden başlattı" sayar: mevcut oturumu
  sessizce kapatır (`WDR` göndermez) ve bu ön ekle yeni yanıtlayan el sıkışmasını başlatır. Normal parçayla
  karışma olasılığı ihmal edilir: 16 sıfır bayt koşulu yeterli sayılır (Noise şifreli yükü 'WD' + 16 sıfırla başlamaz).
- iPhone `WDR` aldığında veya kendi tarafında herhangi bir BLE protokol/şifre hatası olduğunda oturumu
  kapatır, GATT bağlantısını keser ve 1 sn sonra yeniden bağlanıp ön ek + yeni el sıkışma yapar.

### 4.4 Ana uygulama ve paylaşım uzantısı (iOS)
- Aynı iPhone'da ana uygulama ve paylaşım uzantısı aynı anda bağlanırsa PC ikisini aynı cihaz olarak görür;
  bu yüzden uzantı açıkken ana uygulama BLE bağlantılarını bırakır (uzantı kapanınca yeniden kurar). Bu sinyal Darwin bildirimiyle gider.
- İki süreç App Group konteyneri üzerinden tek cihaz kimliğini ve ortak ayarları paylaşır. Konteyner çalışma zamanında
  yoksa uzantı ayrı kimlikle, yalnız açıkken bağlanır.
- Ana uygulama askıdayken Darwin sinyali ulaşmayabilir: uzantı, hedef PC'de "başka oturum" yüzünden düşerse **1 kez**
  yeniden dener. PC zaten aynı BLE kimliğinden gelen yeni oturumu tutar (§4.3 istemci yeniden başlatması).

## 5. Sinyal mesajları (BLE oturumu içinde)
Gövde: UTF-8 JSON `{"t": "<tür>", ...}`, alan adları camelCase. BLE'de düz metin ≤ 8176 B (§4.2).

**Çözme kuralları (ileri uyumluluk):**
- Aşağıdaki tabloda "zorunlu" yazmayan her alan çözümde **isteğe bağlıdır**; alıcı eksik isteğe bağlı alanı hata saymaz.
- Bilinmeyen alanlar yok sayılır.
- Bilinmeyen `t` oturumu **düşürmez**: mesaj yok sayılır ve günlüğe yazılır.
- Çözme hatası (bozuk JSON, eksik zorunlu alan, geçersiz değer) yalnız o mesajı atar, oturumu düşürmez.
- `hello`/`status` içindeki `wifi` PC tarafından gönderilmez; alıcı için isteğe bağlıdır (iOS zorunlu saymaz).

| t | Yön | Alanlar |
|---|---|---|
| `hello` | iki yön | zorunlu: `v`, `name`, `caps` (["tcp","hotspot","relay"]), `btAudio` (bool); isteğe bağlı: `wifi` (yalnız iPhone gönderir: `none`/`connected`) |
| `status` | iki yön | zorunlu: `btAudio`; isteğe bağlı: `wifi` (yalnız iPhone gönderir) — değiştiğinde yeniden gönderilir |
| `offer` | gönderen→alıcı | zorunlu: `transferId`, `count`, `total` (bayt), `preview`: en çok 5 dosya adı (tam liste veri kanalında, §6) |
| `accept` / `reject` | alıcı→gönderen | zorunlu: `transferId`; isteğe bağlı: `reason` |
| `addr` | PC→iPhone | zorunlu: `transferId`, `candidates`: [{`ip`, `port`}] |
| `hotspot` | PC→iPhone | zorunlu: `transferId`, `ip`, `port`; `ssid`, `pass` PC tarafından her zaman gönderilir (eşleştirmesiz modda iPhone bunları başka yoldan bilmez; yalnız şifreli kanaldan gider), alıcı için isteğe bağlıdır |
| `pairInfo` | PC→iPhone | zorunlu: `ssid`, `pass` — yalnız QR eşleştirme kanalında (§3.2), PC onayından sonra; etkin nokta bilgisi. Eşleştirmesiz modda kullanılmaz: `hotspot` her seferinde `ssid`/`pass` taşır. |
| `relay` | iki yön | zorunlu: `transferId`, `token` (§7.4; URL gönderilmez) |
| `path` | iPhone→PC | zorunlu: `transferId`, `result`: `tcp_ok`/`tcp_fail`/`hotspot_ok`/`hotspot_fail`/`relay_ok`/`relay_fail` (anlam: §7.5) |
| `cancel` | iki yön | zorunlu: `transferId` |
| `bleData` | PC→iPhone | zorunlu: `transferId` (§7.3; ardından gönderen taraf MANIFEST ile BLE üzerinden başlar) |
| `pushId` | iPhone→PC | zorunlu: `pushId` (§9; yalnız tanıdık/eşli PC'ye) |

**JSON türleri:** `transferId` = 16 bayt, **base64url, dolgusuz** (22 karakter); `wifi` dize (`none`/`connected`);
`caps` dize listesi; `count` int; `total` int64; `btAudio` bool; `v` int. `hello.v` bilgi amaçlıdır (§10).

Aktarımın sonucu **yalnız veri kanalındaki `DONE`** ile belirlenir (BLE'de `done` yok).

## 6. Veri kanalı (TCP; doğrudan yol ve etkin nokta aynı)

### 6.1 Taşıma ve çerçeveleme
- **PC dinler** (port 53600, IPv4+IPv6), **iPhone bağlanır**; yön veri akışından bağımsız.
  PC güvenlik duvarı kuralı kurulumla gelir (etkin nokta arayüzünün profili yok → kural
  `192.168.137.0/24` için profil bağımsız; ARCHITECTURE bulgusu).
- Açılış: §2 ön eki (`taşıma=2`, `transferId`), sonra el sıkışma. **Desen:** BLE oturumu IK ise TCP'de IK, XX ise TCP'de XX;
  her ikisinde karşı statik anahtar BLE oturumundakiyle aynı olmalı, aksi halde PC bağlantıyı kapatır (XX'te başlatan
  kapanışı ilk okumada anlar).
- **TCP çerçevesi:** `uzunluk (2 B big-endian) || Noise mesajı` (1..65535; sıfır uzunluklu çerçeve geçersiz). Düz metin =
  `tür (1 B) || gövde`, en çok 65519 B. Kontrol çerçevelerinin gövdesi UTF-8 JSON; `DATA` gövdesi ham bayt,
  en çok **65518 B** (uygulamalar 64000 kullanır).
- Her `transferId` için **tek** veri oturumu (yenileme kuralı: §6.8).

### 6.2 Çerçeve türleri ve gövde biçimleri
- **Tür baytı:** MANIFEST=1, RESUME=2, FILE_START=3, DATA=4, FILE_END=5, ACK=6, CANCEL=7, DONE=8.
  Bilinmeyen tür → protokol hatası.
- Çerçeveler:
  - `MANIFEST` (gönderen, ilk çerçeve): `files`: [{`id`, `name`, `size`, `mtime`}]; `count`/`total`
    `offer` ile uyuşmazsa alıcı keser.
  - `RESUME {id, offset}` (alıcı → gönderen, her dosyadan önce; yeni dosyada offset=0).
  - `FILE_START {id, offset}` (gönderen, alınan RESUME ofsetiyle) → `DATA`… → `FILE_END {id, sha256}`.
  - `ACK {id, offset}` (alıcı, ~4 MB'da bir, **diske yazıp fsync ettikten sonra**).
  - `CANCEL`, `DONE {ok, failed: [id]}` (alıcı → gönderen, sonuç).
- **JSON (camelCase, alanlar zorunlu):** `id` int; `size`, `offset` int64 bayt; `mtime` int64 Unix
  epoch **milisaniye**; `sha256` 64 karakter küçük harf hex; CANCEL gövdesi `{}` veya `{"reason": "..."}`;
  DONE `{"ok": bool, "failed": [int]}`. null alanlar yazılmaz.

### 6.3 MANIFEST sayfalama
- Gövde `{"files": [...], "more": bool}` (`more` yoksa false). Gönderen listeyi, her çerçeve gövdesi **taşımanın çerçeve
  gövdesi sınırını** aşmayacak şekilde böler: TCP/relay'de ≤ 65518 B, BLE'de Noise mesajı ≤ 8192 B'a (düz metin ≤ 8176 B,
  yani gövde ≤ 8175 B) sığacak şekilde. Alıcı `more=false` gelene kadar toplar, sonra `count`/`total` kontrolünü yapar.
- BLE yolunda DATA parçası ≤ 2048 B'tır (§7.3).

### 6.4 Bütünlük ve devam ettirme
- **Bütünlük:** SHA-256 her zaman dosyanın **0. baytından itibaren tamamı** üzerinden. Devam
  ederken gönderen ilk `offset` baytı yeniden okuyup hash'e katar; alıcı kendi kısmi dosyasını
  aynı biçimde hash'ler. Uyuşmazlıkta dosya silinir, `DONE`'da `failed` listesine girer.
- **Devam ettirme:** kısmi dosya `(eş kimliği, transferId, id)` ile anahtarlanır. RESUME ofseti en
  çok son ACK'lenen ofsettir. `MANIFEST`'te `size` veya `mtime` değişmişse dosya baştan gönderilir.

### 6.5 Sınırlar (alıcı uygular)
- Kabulden önce `total` ≤ boş alan − pay; dosya başına bildirilen `size`'ı aşan veri → aktarım kesilir;
  en çok 10 000 dosya, ad ≤ 255 karakter; otomatik kabulün boyut üst sınırı (ayar, varsayılan 2 GB);
  cihaz başına `offer` hız sınırı (ör. 5/dk).

### 6.6 Dosya adı (alıcı)
- Yalnız son yol bileşeni; `..`, `/`, `\`, `:` (NTFS akışı), denetim karakterleri ve Windows ayrılmış adları
  (CON, PRN, AUX, NUL, COM1–9, LPT1–9; uzantılı biçimleri dahil) reddedilmez, `_` ile **değiştirilir**; ek olarak
  `< > " | ? *`, C1 denetim ve bidi karakterleri de değiştirilir; sondaki nokta/boşluk silinir.
- Var olan dosyanın üzerine yazılmaz ("ad (2).ext"); Windows'ta Mark-of-the-Web (Zone.Identifier) eklenir.

### 6.7 Klasör gönderme
- MANIFEST dosya öğesine isteğe bağlı `dir`: gönderilen kökten göreli klasör yolu, ayırıcı `/`,
  en çok 16 seviye, toplam ≤ 1024 karakter; boş klasörler için `size=0` ve `name=""` olmayan bir öğe
  gönderilmez (boş klasör taşınmaz, v1).
- Alıcı her bileşeni ayrı ayrı §6.6 ad kurallarıyla temizler; `.`/`..`/boş bileşen → protokol hatası.
  Hedef: `indirme klasörü / <ilk bileşen (çakışmada "ad (2)")> / ...`; çakışma yalnız en üst klasörde
  çözülür, alt klasörlerde dosya çakışması §6.6 kuralıyla.
- `offer.preview` klasör adlarını gösterebilir; `count` dosya sayısıdır. Windows gönderen klasör girdisini en üst klasör adı +
  sonda `/` ile yazar (ör. `Proje/`, aynı klasör bir kez); alıcı `/` ile bitenleri klasör olarak gösterir. Bilgi amaçlıdır.
- Windows uygulaması: gönderen symlink/junction'ı (kök seçim hariç) izlemez, gizli/sistem dosyalarını gönderir, erişilemeyeni
  atlayıp loglar; iki seçili klasör aynı adı taşırsa ikincisi `ad (2)` olur. Alıcı çözülmüş kök klasör adını aktarım dizininde saklar
  (devam oturumunda aynı kök). `\`, `.`/`..`, boş bileşen ve `X:` biçimli bileşen → protokol hatası (CANCEL "protocol error").

### 6.8 Veri oturumu yenileme
- Her `transferId` için tek veri oturumu vardır. Varsayılan: yeni (kimliği doğrulanmış) bağlantı eskisini kapatır
  (kopmadan sonra yeniden bağlanma serbesttir).
- İstisna: PC aynı `transferId` için iki yolu birden açtıysa (`addr` + `hotspot`, §7.5), yol yarışının ilk 10 sn'sinde mevcut
  etkin oturum korunur ve ikinci bağlantı kapatılır; sonra yeniden bağlanma serbesttir.

## 7. Yol seçimi (kullanıcı görmez)
Yolu **her zaman PC seçer**. Sıra: doğrudan (`addr`) → etkin nokta (`hotspot`) → BLE (küçük dosya) → aktarma sunucusu.
`wifi=connected` ise doğrudan yol, `wifi=none` ise etkin nokta denenir; ikisi birden gerekebilir (§7.5).

### 7.1 Doğrudan yol (iPhone bir Wi-Fi'a bağlı: `wifi=connected`)
PC `addr` gönderir; iPhone adaylara paralel bağlanır (aday başına 1,5 sn); ilk başarılı el sıkışma kazanır.
Başarısızsa sonraki yola geçilir (§7.5).

### 7.2 Etkin nokta (iPhone hiçbir Wi-Fi'da değil: `wifi=none`)
- Doğrudan yol denenmez; PC **hemen** Mobil etkin noktayı açar ve `hotspot` gönderir; iPhone katılır, sonra §7.1'deki gibi bağlanır.
  iPhone bir Wi-Fi'a bağlıyken bu yol **asla** denenmez (Efe şartı).
- **SSID/parola:** PC başına, ilk kullanımda rastgele üretilir (SSID son eki 32 bit, parola 128 bit → base32, ≥ 22 karakter),
  DPAPI ile saklanır; `hotspot` (QR eşleştirmede ayrıca `pairInfo`) ile iPhone'a gönderilir; **türetilmez**. SSID'de
  kullanıcı/cihaz adı yok; gizli ağ yapılmaz (gizli ağ iPhone'u her yerde sorgu yayınlatıp izlenebilir kılar). Tüm eşli
  iPhone'lar aynı SSID'yi kullanır.
- Windows Mobil etkin nokta ayarı sistem genelindedir: açmadan önce kullanıcının mevcut ayarı kaydedilir, kapanınca geri
  yüklenir. Kullanıcı etkin noktayı zaten kullanıyorsa onun ayarı korunur ve bu yol atlanır.
- Etkin nokta son aktarımdan **2 dk** sonra PC tarafından kapatılır.
- **Parola yenileme:** `hotspot` (SSID + parola) yalnız **kullanıcının onayladığı** aktarımda gönderilir (alıcıda onay veya eşli
  cihaz için otomatik kabul; gönderende kullanıcının seçtiği eş). `hotspot` mesajı eşli olmayan bir eşe (tanıdık dahil değil:
  yalnız **bilinmeyen** eş) gittiyse, aktarım hangi sonuçla biterse bitsin (başarı, iptal, hata ve zaman aşımı dahil) PC kalıcı
  parolayı **yeniler** (SSID aynı kalır, DPAPI kaydı güncellenir; açık kalan etkin nokta bir sonraki başlatmada yeni parolayla
  yeniden kurulur). Eşli/tanıdık cihazlar için parola sabit kalır (iOS ağı hatırlasın diye). iPhone her zaman o anki
  parolayı `hotspot` iletisinden alır; yenilenmiş parola kayıtlı ağın üzerine yazılır.

### 7.3 BLE veri yolu (küçük dosya)
- **Koşul:** `total` ≤ 64 KB (65536 B), iki tarafta `btAudio=false`, diğer yollar yok veya başarısız. Uygun değilse özgün
  "yol yok" nedenine BLE nedeni (boyut / Bluetooth ses) eklenir.
- **Başlatma:** PC şifreli BLE kanalından `{"t":"bleData","transferId":...}` gönderir. Bunu alan/gönderen taraf: gönderen ise MANIFEST'i
  BLE üzerinden başlatır (PC gönderense `bleData`dan hemen sonra `MANIFEST` yollar); alıcı ise MANIFEST bekler. iPhone zamanlayıcıyla
  kendiliğinden BLE'ye geçmez (yarış olmasın).
- **Çerçeveler:** BLE oturumu üstünden §6 çerçeveleri; çerçevelerde `transferId` yoktur: eş başına **tek** süren BLE aktarımına aittir;
  `bleData`dan önce gelen çerçeve oturumu düşürür.
- **Sınırlar:** DATA parçası ≤ 2048 B, hız ≤ ~20 KB/s; MANIFEST sayfalama §6.3. Aktarım sürerken bir tarafın `btAudio`su `true`
  olursa (`status`) aktarım kesilir (PC uygulaması); eş 30 sn çerçeve göndermezse oturum düşer.
- **`btAudio` (PC):** varsayılan ses çıkışı Bluetooth (PKEY_Device_EnumeratorName `BTH*`) **ve** aktif ses oturumu var; oturum
  durumu okunamazsa Bluetooth çıkış varsayılansa `true`. `hello`'da gerçek değer, değişince `status`.

### 7.4 Aktarma sunucusu (relay)
- **Uyarı ve onay:** iki taraf internetteyse kullanıcıya uyarı: "İnternet/mobil veri kullanılacak (X MB), uçtan uca şifreli".
  Metin (Efe): "Bu gönderim Wi-Fi veya hücresel veri kotanızdan gidecektir." + boyut; iki tarafta da gösterilir, gönderen
  onaylamadan bağlantı kurulmaz.
- **Sunucu:** Cloudflare Worker + Durable Object (`relay/`); ücretsiz katman. Yalnız bayt aktarır;
  içerik uçtan uca Noise ile şifreli, sunucu dosya adını bile görmez.
- **Adres:** sunucu adresi **uygulamaya gömülü/yapılandırılmış** alan adıdır (`relayUrl`), mesajla gelmez (kötü niyetli eşli
  cihaz iPhone'u kendi sunucusuna yönlendiremez). Boşsa aktarma yolu kapalıdır ve "yol yok" hatası verilir. Sunucuyu Efe kendi
  Cloudflare hesabına `wrangler deploy` ile kurar (hesap açma kullanıcıya ait).
- **Oda:** PC 32 B rastgele `token` üretir, `relay {transferId, token}` ile (şifreli BLE) iPhone'a verir. `token` tek kullanımlık,
  kısa ömürlü, `transferId`'ye bağlı; hesap kimlik bilgisi gönderilmez. `roomId = hex(SHA-256("WinDrop-relay" || token))[0:32]`.
  İki taraf da istemcidir: `wss://<relay>/v1/room/<roomId>?role=pc|phone`. Token URL'de gitmez. `relay` sinyalinde `token`
  = ham 32 baytın **dolgusuz base64url**'i (43 karakter); roomId ham baytlardan hesaplanır (vektörler: `protocol/vectors/relay.json`).
- **Eşleme:** oda ilk `pc` ve ilk `phone` bağlantısını eşler; üçüncü bağlantı ve aynı rolden ikinci
  bağlantı 409 ile reddedilir. Eş 60 sn içinde gelmezse oda kapanır.
- **Aktarım:** kanal = §2 ön eki (`taşıma=3`) + IK/XX (BLE oturumuyla aynı desen ve eş anahtarı) + §6 çerçeveleri. Ön ek ve her
  Noise mesajı **ayrı** ikili WebSocket mesajıdır; her WebSocket ikili mesajı = bir Noise mesajı (≤ 65535 B; uzunluk öneki yok).
  Sunucu mesajları sırayla karşıya iletir; metin mesajı gelirse bağlantıyı kapatır.
- **Sırası:** iPhone (başlatan) bağlanınca ön ek + el sıkışma ilk mesajını gönderir; PC yanıtlar.
  Oturum düşerse yeni `token` ile yeni oda; devam ettirme §6 RESUME ile.
- **Sınırlar:** oda başına toplam ≤ 4 GiB, ömür ≤ 2 saat, 60 sn boşta kapanış; IP başına dakikada
  ≤ 10 oda açma (kötüye kullanım). Aşımda iki bağlantı da 1008 ile kapanır.

### 7.5 Yol değişimi ve yedek yola geçiş
- **`path` anlamı:** iPhone her yol için sonucunu ayrı bildirir. `tcp_fail` yalnız `addr` yolunu, `hotspot_fail` yalnız etkin nokta
  yolunu kapatır.
- **Birden çok açık yol:** iki yol da açıkken biri başarısız olursa PC diğerini beklemeye devam eder; **tümü** başarısız olunca
  sonraki yola geçer (BLE küçük dosya → aktarma sunucusu, §7.3, §7.4). BLE uygun değilse aktarma (ayarlıysa ve kullanıcı onaylarsa),
  hiçbiri yoksa "yol yok" hatası.
- **Yedek yola geçiş:** BLE/aktarma yoluna geçerken açık yol beklemeleri kapatılır (bekleyen tablo kaydı silinir). Aktarım bittiğinde
  (`Finish`) o aktarımın iptal belirteci her durumda iptal edilir.
- **Yol değişimi:** bekleme sırasında wifi durumu değişirse PC ek yolu (`addr`/`hotspot`) aynı `transferId` ile gönderir; iPhone ilk
  başarılı bağlantıyı kullanır (veri oturumu kuralı: §6.8).

### 7.6 Zaman aşımları (iki taraf)
Aşımda aktarım anlaşılır, yerelleştirilmiş hatayla biter; hiçbir satır süresiz "bekleniyor" kalmaz.

| Süre | Ne için |
|---|---|
| 10 sn | BLE el sıkışması |
| 5 sn | onaylı BLE yazması |
| 60 sn | PC teklif onay penceresi (zaman aşımı = ret); iPhone onay sayfası (PC→iPhone teklif) |
| 20 sn / 80 sn | iPhone gönderen: 20 sn yanıt yoksa oturumu tazeler; toplam 80 sn sonra "yanıt vermedi" (§8) |
| 80 sn | PC gönderen: iPhone'un teklif yanıtını bekleme |
| 30 sn | kabulden sonra yol bilgisi (`addr`/`hotspot`/`bleData`/`relay`) bekleme |
| 1,5 sn | doğrudan yolda aday başına bağlanma (§7.1) |
| 120 sn | etkin noktaya katılma bekleme |
| 30 sn | TCP veri oturumunda boşta kalma (TCP keepalive açık); BLE veri oturumunda eşin çerçeve göndermemesi |
| 2 dk | etkin noktanın son aktarımdan sonra kapanması (§7.2) |
| 60 sn | relay: eş gelmezse oda kapanır; boşta kapanış (§7.4) |
| 5 dk | push sonrası PC'nin bekleyen teklifi tutması (§9) |
| 2 dk | QR eşleştirme penceresi (§3.2) |

## 8. Kullanıcı onayı ve otomatik kabul
- Eşli cihazdan gelen `offer` varsayılan olarak onay ister; "eşli cihazlardan otomatik kabul"
  ayarı boyut üst sınırıyla. Eşli olmayan (tanıdık/bilinmeyen) cihazın teklifi **her zaman** onay ister (§3.1).
- **Onay süreleri:**
  - PC alıcı: onay penceresi 60 sn; zaman aşımı = ret.
  - iPhone alıcı (PC→iPhone teklif): onay sayfası 60 sn; PC gönderen 80 sn bekler.
  - iPhone gönderen (iPhone→PC teklif): 20 sn yanıt gelmezse oturumu tazeler (yalnız oturum yenilendiyse teklifi bir kez
    yeniden gönderir); toplam 80 sn sonra "yanıt vermedi" der.
- iOS arka plandayken gelen `offer` yerel bildirim olur; dokununca uygulama öne gelir ve aktarım
  başlar (arka planda büyük aktarım hedeflenmez).

## 9. Push bildirimi (hazırlık; yalnız ücretli Apple hesabıyla açılır)
- iOS'ta `PUSH_ENABLED` derleme bayrağı ve ayar; kapalıysa hiçbir push kodu çalışmaz.
- **Ortam:** iOS `env` derleme yetkisine göre belirlenir: `aps-environment` development → `sandbox`, aksi halde `production`.
  Derleme zamanı bayrağı `PUSH_SANDBOX` sandbox'ı seçer; varsayılan Release-Push yapılandırması sandbox'tır (Sideloadly/geliştirici imzası).
- **Kayıt:** iPhone APNs cihaz belirtecini alınca aktarma sunucusuna `POST /v1/push/register`
  `{deviceToken, env: "sandbox"|"production"}` gönderir; sunucu rastgele 16 B `pushId` (hex) döndürür,
  `pushId → deviceToken` saklar. Cihaz belirteci sunucudan başka yere gitmez. Yeniden kayıt eskisini siler.
- iPhone, **tanıdık/eşli** PC'lere şifreli BLE kanalında `{"t":"pushId","pushId":...}` verir.
- **Tetik:** PC, iPhone BLE ile yakında değilken ona gönderim başlatılırsa (ve `relayUrl` doluysa)
  `POST /v1/push/<pushId>` `{from: "<PC adı, ≤ 40 karakter>"}`. Sunucu genel bir bildirim yollar:
  başlık "WinDrop", gövde "<from> dosya göndermek istiyor" (dosya adı/boyutu yok). pushId başına
  saatte ≤ 10, IP başına dakikada ≤ 10.
- Bildirime dokunulunca uygulama öne gelir, PC'leri tarar ve bağlanır; PC bekleyen teklifi yeniden gönderir
  (en çok 5 dk bekler).
- Sunucu sırları (Efe girer): `APNS_KEY_P8`, `APNS_KEY_ID`, `APNS_TEAM_ID`, `APNS_TOPIC`. Eksikse
  `/v1/push/*` 501 döner.

## 10. Sürümleme
- Sürüm §2 açık ön ekte (şifresiz) taşınır; uyuşmazlık el sıkışmadan önce bildirilir.
- `hello.v` bilgi amaçlıdır; büyük sürüm ön ekle belirlenir.

## 11. Açık konular
- iOS bekleyen BLE bağlantısının ücretsiz hesapta, arka planda gerçek davranışı.
- Gerçek cihazda henüz denenmedi (`docs/STATUS.md`): BLE küçük dosya yolu (§7.3), Bluetooth ses çalarken gönderim, arka planda bildirim.

## Ek A. Eski → yeni bölüm eşlemesi
Kod yorumları ve diğer belgeler eski numaralarla atıf yapabilir.

| Eski | Yeni |
|---|---|
| §0 Terimler ve anahtarlar | §1 |
| §1 Kriptografi | §2 |
| §2 Eşleştirme | §3.2 |
| §2b Yakındaki herkes (doğrulama kodu dahil) | §3.1; kötüye kullanım sınırları §3.4 |
| §3 BLE katmanı | §4.1, §4.2 |
| §3a BLE oturum sıfırlama | §4.3; uzantı/ana uygulama notu §4.4 |
| §4 Sinyal mesajları | §5 |
| §5 Veri kanalı | §6.1–§6.6, §6.8 |
| §5a Klasör gönderme | §6.7 |
| §6 Yol seçimi, madde 1 / madde 2 | §7.1 / §7.2 |
| §6 "Yol değişimi" | §7.5 |
| §6 madde 3 (Aktarma) ve §6.3 Aktarma sunucusu | §7.4 |
| §6 madde 4 (BLE) ve §6.4 / §6.4a | §7.3 |
| §6.5 Push bildirimi | §9 |
| §7 Kullanıcı onayı | §8 |
| §8 Sürümleme | §10 |
| §9a Kodlama ayrıntıları | çerçeve türleri/JSON §6.2; MANIFEST sayfalama §6.3; dosya adı §6.6; sinyal JSON §5; sürüm uyuşmazlığı, desen seçimi, IK/XX onayı §2; BLE sıra no ve ayrım §4.2; veri kanalı deseni §6.1 |
| §9 Açık sorular | §11 |

## Ek B. Ortak vektörler ve interop kapsamı
- `protocol/vectors/`: `cacophony.txt` (Noise), `sas.json` (doğrulama kodu), `relay.json` (roomId/token). İki tarafın testleri
  bunları okur.
- TCP/relay interop betiği ve kapsamı: `docs/INTEROP.md`. BLE hattı otomatik çapraz sınanmaz (gerçek cihaz testi).
