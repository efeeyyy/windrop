# WinDrop aktarma sunucusu (relay)

Cloudflare Worker + Durable Object. İki WinDrop istemcisi (PC ve iPhone) doğrudan veya etkin nokta
yoluyla bağlanamazsa, uçtan uca şifreli baytları bu sunucu üzerinden aktarır. Esas spesifikasyon:
`docs/PROTOCOL.md` §6.3. Sunucuyu Efe kendi Cloudflare hesabına kurar; bu depo hesaba giriş yapmaz.

## Arayüz

- `GET /healthz` → 200 `ok`
- `GET /v1/room/<roomId 32 küçük hex>?role=pc|phone` + `Upgrade: websocket`
  - yol/roomId geçersiz → 404; `role` geçersiz veya `Upgrade` yok → 400
  - aynı rolden ikinci veya üçüncü bağlantı → 409; kapanmış oda → 410
  - IP başına dakikada 10'dan fazla yeni oda → 429 (`Retry-After: 60`)
- Yalnız **ikili** WebSocket mesajları (≤ 65535 B) karşıya sırayla iletilir.
- `POST /v1/push/register` `{deviceToken, env}` ve `POST /v1/push/<pushId>` `{from}`: APNs push
  (`docs/PROTOCOL.md` §6.5). Ayrıntı aşağıda "Push bildirimi (APNs)".

| Durum | Sonuç |
|---|---|
| Metin mesajı | iki bağlantı 1003 ile kapanır |
| Mesaj > 65535 B | 1009 |
| Eş yokken tampon > 1 MiB | 1008 |
| Eş 60 sn içinde gelmedi / 60 sn trafik yok / oda ömrü 2 saat / oda toplamı 4 GiB | 1008 |
| Bir taraf kapandı | diğeri de kapanır; oda yeniden kullanılamaz |

Not: Spesifikasyon "aşımda iki bağlantı da 1008" der. IP sınırı odayı daha açılmadan, WebSocket
yükseltmesinden önce HTTP 429 ile reddeder (istemci ayrımı kolay olsun diye); diğer aşımlar 1008'dir.

## Kurulum (Efe'nin kendi Cloudflare hesabı)

Önkoşul: Node 20+ ve ücretsiz bir Cloudflare hesabı.

```powershell
cd relay
npm install
npm test                 # isteğe bağlı: 43 test
npx wrangler login       # tarayıcıda Cloudflare hesabına giriş
npx wrangler deploy      # Worker + üç Durable Object sınıfını yükler
```

`deploy` çıktısında `https://windrop-relay.<hesap-alt-alanı>.workers.dev` biçiminde bir adres yazar.
Bunu Windows ve iOS uygulama ayarlarında `relayUrl` olarak gir; `wss://` biçiminde yaz:
`wss://windrop-relay.<hesap-alt-alanı>.workers.dev`. Doğrulama:
`curl https://windrop-relay.<hesap-alt-alanı>.workers.dev/healthz` → `ok`.

Worker adını değiştirmek istersen `wrangler.jsonc` içindeki `name` alanını düzenle.

### Yerel deneme

```powershell
npx wrangler dev --local          # http://127.0.0.1:8787
node scripts/smoke.mjs            # 2 istemci, her yönde 10 MB, SHA-256 doğrulaması
```

Yerelde `CF-Connecting-IP` olmadığından tüm bağlantılar tek IP sayılır; `smoke.mjs`'i dakikada
10'dan fazla çalıştırırsan 429 alırsın (beklenen davranış).

## Push bildirimi (APNs) — isteğe bağlı, ücretli Apple hesabı gerekir

Bu bölüm kurulmadan sunucu normal çalışır; sırlar girilmezse `/v1/push/*` **501** döner.
**APNs anahtarı için ücretli Apple Developer Program üyeliği gerekir** (ücretsiz hesapta push
yetkisi ve `.p8` anahtarı yoktur).

Arayüz (yanıtlar): `register` -> 200 `{"pushId":"<32 hex>"}`, geçersiz girdi 400. `/v1/push/<pushId>` ->
200 (gönderildi), 404 (pushId yok), 410 (APNs belirteci geçersiz saydı; kayıt silindi), 429
(pushId başına saatte 10 veya IP başına dakikada 10 aşıldı), 502 (APNs/ağ hatası).

1. https://developer.apple.com/account -> *Certificates, Identifiers & Profiles* -> *Keys* -> **+**.
2. Ad ver, **Apple Push Notifications service (APNs)** kutusunu işaretle, *Continue* -> *Register*.
3. **Download** ile `AuthKey_<KEYID>.p8` dosyasını indir (yalnız bir kez indirilir; güvenli sakla,
   depoya koyma). *Key ID* dosya adındaki 10 karakterdir. *Team ID* sayfanın sağ üstünde
   (*Membership details*) yazar. APNs anahtarı sandbox ve production için ortaktır.
4. `APNS_TOPIC` iOS uygulamasının *Bundle ID*'sidir (uygulamada Push Notifications yeteneği açık olmalı).
5. Sırları Worker'a gir (her komut değeri sorar; `.p8` içeriğini `-----BEGIN...` satırlarıyla birlikte yapıştır
   veya dosyadan yönlendir):

```powershell
cd relay
Get-Content AuthKey_XXXXXXXXXX.p8 -Raw | npx wrangler secret put APNS_KEY_P8
npx wrangler secret put APNS_KEY_ID      # 10 karakter Key ID
npx wrangler secret put APNS_TEAM_ID     # 10 karakter Team ID
npx wrangler secret put APNS_TOPIC       # örn. com.example.windrop
npx wrangler deploy
```

Yerel deneme için aynı dört değeri `relay/.dev.vars` dosyasına `ANAHTAR=değer` yaz (`.gitignore` ile
dışlanmış olmalı; `.p8` tek satırda `
` ile yazılabilir).

Davranış: cihaz belirteci yalnız `PushRegistry` Durable Object'inde (SQLite) durur; belirteç ve
pushId loglanmaz. Aynı belirteçle yeniden kayıt eski pushId'yi siler. Bildirim metni sabittir
(`WinDrop` / `<from> dosya göndermek istiyor`); dosya adı ve boyutu sunucuya gitmez. `from` denetim
karakterlerinden arındırılır ve 40 karakterle kesilir. Sağlayıcı JWT'si (ES256) 50 dk önbelleklenir.

## IP sınırı yöntemi ve gerekçe

Seçilen: IP karmasına göre adlandırılmış **ayrı bir SQLite Durable Object (`IpLimiter`), kayan pencere**
(son 60 sn'de en çok 10 oda). Odayı yaratan ilk bağlantı sayılır; mevcut odaya katılan eş sayılmaz.

Gerekçe: SQLite tabanlı Durable Object ücretsiz planda çalışır ve sınır kesin (tek nesne, sıralı)
olur. Workers Rate Limiting binding'i yalnız 10 veya 60 sn'lik sabit dönemleri destekler, sayaçları
veri merkezi başına ve "eventually consistent" tutar (kaçamaklı); ayrıca yerel testte kesin
doğrulanamaz. Bedel: oda açma başına bir ek Durable Object isteği (günlük ücretsiz kotadan yenir).
IP açık haliyle saklanmaz ve loglanmaz; nesne adı `SHA-256("WinDrop-ip|" + IP)` değeridir. Nesne,
pencere dolunca kendi verisini siler.

## Ücretsiz plan sınırları (güncel olmayabilir; kurmadan önce Cloudflare belgesinden doğrula)

Bu değerler yazım anındaki bilgiden alınmıştır ve **doğrulanmamıştır**:

- Workers: günde ~100.000 istek.
- Durable Objects: yalnız SQLite tabanlı sınıflar; günde ~100.000 istek, günde ~13.000 GB-sn süre,
  günde ~100.000 satır yazma. WebSocket mesajları gelen istek sayılırken oran düşük sayılır
  (yaklaşık 20 mesaj = 1 istek); 4 GiB'lık bir oda ≈ 65.000 mesajdır.
- Bu sunucu hibernasyon kullanmaz (ön tampon bellekte): bir oda açıkken süre faturalanır. 128 MB
  varsayımıyla 2 saatlik bir oda ≈ 920 GB-sn. Normal kullanımda (dakikalar süren aktarımlar) kota
  rahat yeter; aşılırsa Cloudflare istekleri reddeder, WinDrop "yol yok" der.
- Depolamaya yalnız oda başına birkaç satır ve IP başına en çok 10 zaman damgası yazılır.

## Güvenlik modeli

- **Sunucu içerik göremez.** Kanal uçtan uca Noise ile şifrelidir (`docs/PROTOCOL.md` §1, §6); sunucu
  yalnız şifreli bloklar, boyutları ve zamanlarını görür. Dosya adı bile şifrelidir. Kimlik doğrulama
  Noise anahtarlarıyla istemcilerdedir; sunucuda hesap, parola, anahtar yoktur.
- **Oda erişimi:** `roomId = SHA-256("WinDrop-relay" || token)[0:32]`; token URL'ye girmez ve tek
  kullanımlıktır. `roomId`'yi bilen ilk `pc` ve ilk `phone` odayı alır. Yanlış kişi araya girse bile
  Noise el sıkışması başarısız olur (veri sızmaz, en fazla aktarım düşer).
- **Loglama:** içerik, roomId ve IP yazılmaz. Yalnız toplu sayaç satırları (`room_created`,
  `room_closed` + kapanış nedeni, bayt/mesaj sayısı) basılır. `observability` kapalıdır. Yerel
  `wrangler dev` kendi konsoluna istek yolunu yazar; bu yalnız kendi makinendedir.
- **DoS sınırları:** mesaj ≤ 65535 B, eş yokken tampon ≤ 1 MiB, oda ≤ 4 GiB ve ≤ 2 saat, 60 sn boşta
  kapanış, eş bekleme 60 sn, IP başına dakikada 10 oda, aynı rolden tek bağlantı. Dağıtık saldırı
  (çok sayıda IP) ücretsiz kotayı tüketebilir; bu durumda yalnız aktarma yolu etkilenir, yerel yollar
  çalışır. Gerekirse Cloudflare panelinden Worker'ı devre dışı bırak veya yeni ad ver.
- Oda kimliği kapanınca yeniden kullanılamaz (410); oturum düşerse yeni token ile yeni oda açılır.

## Geliştirme

```powershell
npm test          # vitest + @cloudflare/vitest-plugin (eski adıyla vitest-pool-workers), 43 test
npm run typecheck
```

Testlerde sahte zaman: `Room.clockSkewMs` (yalnız test için) saati ileri alır ve `runDurableObjectAlarm`
alarmı hemen çalıştırır. `@cloudflare/vitest-pool-workers` yeniden adlandırıldığı ve eski sürümü
yeni `compatibility_date` değerini tanımadığı için `@cloudflare/vitest-plugin` kullanıldı.
