# Altyapı ve belge incelemesi (salt-okur) — 2026-11-13

Kapsam: relay/, .github/workflows, depo hijyeni, docs/. Kod değiştirilmedi. Önem: Y=yüksek, O=orta, D=düşük.
Doğrulanmayanlar "(belirsiz)" ile işaretlidir.

## 1. relay (Cloudflare Worker)

1.1 [O] relay/src/index.ts:57-58 — Hız sınırı anahtarı tam IP karması. IPv6'da tek bir /64 blok
    (tek abone) 2^64 adres verir; sınır kolayca aşılır. Öneri: IPv6 için ilk 64 bit (/64) ile karma.
    Aynı düzeltme push yolunda da (index.ts:44).
1.2 [O] relay/src/index.ts:126, 216 — Her oda DO'sunda `createdAt` ve `dead` kalıcı yazılır, hiç silinmez
    (depolama yalnız büyür; IP sınırını aşan saldırıda DO + depolama şişer). Öneri: ölümden sonra alarmla
    (örn. 24 sa; token tek kullanımlık ve roomId her oturumda yeni olduğundan güvenli) `deleteAll()`.
1.3 [O] relay/src/index.ts:176 — `dest.send` için geri basınç yok: hızlı gönderici + yavaş alıcıda Worker
    belleğinde birikme (128 MB sınırı) -> DO çöker, oda düşer. Öneri: `bufferedAmount` benzeri kontrol
    yok; en azından toplam uçuştaki bayt sayacı ve aşımda 1008 (belirsiz: Workers WebSocket'te
    bufferedAmount yoktur; sayaç yaklaşık olur).
1.4 [O] relay/src/push.ts:165-170 — `request.text()` gövdeyi tamamen okuyup sonra boyut denetliyor;
    IP sayacından sonra olsa da 2048 B sınırı etkisiz (100 MB'a kadar okunur). Öneri: `content-length`
    > 2048 ise 413; yoksa akışı sınırlı oku.
1.5 [O] relay/src/push.ts:204-217 — `register` kimliksiz: (a) kayıtlar hiç süresi dolmaz (`push` tablosu
    sınırsız büyür; push.ts:39); (b) aynı belirteci bilen biri yeniden kayıt ile kurbanın pushId'sini
    sildirebilir (push.ts:52-53). Öneri: `last_used` sütunu + 90 gün TTL (alarm), kayıt başına IP
    sınırı zaten var. (b) düşük olasılık (belirteç gizli sayılır).
1.6 [D] relay/src/push.ts:279 — Her 403'te JWT önbelleği siliniyor; APNs aynı anahtar için JWT'yi 20 dk'dan
    sık yenilemeyi `429 TooManyProviderTokenUpdates` ile reddeder. Yanlış anahtar/Key ID ile her istek yeni
    JWT üretir. Öneri: 403'te en erken 20 dk aralıkla yenile.
1.7 [D] relay/src/push.ts:239-240, 274 — Bildirim metni Türkçe sabit ("dosya göndermek istiyor"); uygulama
    artık İngilizce de destekliyor. Öneri: `title-loc-key/loc-key` + `loc-args` (metin istemci kataloğunda).
    `DeviceTokenNotForTopic`/`Unregistered` dışı 400'lerde kayıt korunuyor (doğru); `apns-expiration`
    gönderilmiyor (iyi: bayat teklif gitmez ama başlık açıkça `0` yazılmalı, belgelenmeli).
1.8 [D] relay/src/push.ts:185-187 — Sır yoksa 501: yapılandırma durumu dışarıya açık. Önemsiz.
1.9 [D] relay/src/push.ts:63 — `hits` temizliği yalnız `claim` içinde; hiç kullanılmayan pushId'lerin
    satırı yok, sorun değil. IpLimiter push ve oda için aynı DO sınıfı ama farklı ad (doğru).
1.10 Güvenlik değerlendirmesi (sorun bulunmadı): roomId 128 bit, rol başına tek bağlantı, Noise istemcide;
    rol bilgisi başlıkla ezilerek iletiliyor (index.ts:60-62); belirteç/pushId/IP loglanmıyor (test var,
    push.test.ts "loglanmaz"); JWT ES256 ham r||s, önbellek 50 dk (APNs 60 dk sınırı içinde) doğru.
    pushId'yi bilen biri saatte 10 sahte bildirim gönderebilir ve `from` metnini seçer (kimlik taklidi
    içeren kısa metin); pushId sır olduğundan kabul edilebilir, README'de belirtilsin.
1.11 Ücretsiz plan uyumu: SQLite DO sınıfları (wrangler.jsonc:16-19) doğru; `observability` kapalı.
    Standart (hibernate etmeyen) WebSocket: oda açıkken GB-sn faturalanır; README'deki 920 GB-sn/oda
    hesabı (README:113-115) 13.000 GB-sn/gün için ~14 uzun oda demek — normal kullanım yeter ama
    "2 saat boşta tutan" 14 istemci kotayı bitirir; idle 60 sn zaten koruyor. Not: README'deki sayılar
    "doğrulanmamış" diye işaretli, doğru yaklaşım.
1.12 Eksik testler: (a) IPv6/aynı /64 sınırı; (b) ölü odanın depolama temizliği; (c) push gövde > 2048 B
    ve `content-length` yok; (d) 403 sonrası JWT yenileme; (e) `register` 6. IP'den sınır (IP testi var,
    register ile karışık yok); (f) alıcı yokken `role` sahteciliği başlıkla (`x-windrop-role` istemciden
    gelirse ezilir — test yok); (g) WebSocket `error` olayı yolu. Mevcut: 43 test (21+22), README ile uyumlu.
1.13 README doğruluğu: relay/README.md:86-88 — `\n` açıklaması bozuk (satır sonu karakteri gerçekten
    satıra bölünmüş; ters eğik çizgi+n metni görünmüyor). README:36/140 "43 test" doğru. README:29 "Efe"
    adı geçiyor; kamuya açılırsa genelleştirin. README:20-27 not iyi. CI'da relay `npm test` HİÇ çalışmıyor
    (bkz. 2.5).

## 2. CI (.github/workflows)

Ölçüm (gh run list, son 20 koşu; hepsi 17-18 Ekim, hepsi success): iOS iş akışı 15 koşu, toplam duvar
süresi 45,7 dk (ort. 3,0 dk); Interop 5 koşu, 6,7 dk (ort. 1,3 dk). Son iOS koşusunda işler: Linux
swift:6.0 3,4 dk + macOS ~2,5 dk (ayrı işler, paralel). macOS dakikası özel depoda 10x sayılır; tahmini
kota harcaması ~(15x3 + 5x2) x 10 ≈ 550 dk (yuvarlama varsayımı, belirsiz) — aylık 2000 dk (Free planda,
belirsiz) kotanın dörtte biri 36 saatte yandı.

2.1 [Y] ios.yml:3-5 — `on: push` filtresiz: docs-only, relay-only, windows-only push'lar da tam macOS
    archive + Release-Push derlemesi çalıştırır. Öneri: `paths:` ile `ios/**` ve `.github/workflows/ios.yml`;
    `windows/**`, `docs/**`, `relay/**` hariç. Tek satır docs değişikliği 10x dk harcamasın.
2.2 [Y] ios.yml — `concurrency` yok. Öneri: `concurrency: {group: ios-${{ github.ref }}, cancel-in-progress: true}`
    (18 Ekim'de 20 dk içinde art arda push'lar var; ör. ee01c49 -> a294fb4 -> 2f90f54 koşuları gereksiz
    tamamlanmış). interop.yml için de ekle.
2.3 [O] ios.yml:35-36 — macOS işi `swift test`'i (CryptoKit yolu) tekrarlıyor; Linux işi zaten koşuyor.
    Tutulabilir (CryptoKit farkı gerçek hata yakalıyor: bkz. STATUS NFD hatası), ama bunu yalnız `main`
    ve PR'de çalıştırmak mümkün; en azından Release-Push derlemesini (ios.yml:98-111) `main` veya
    elle tetiklemeye bağla, archive'ı her push'ta değil. `Release-Push` ayrı `build` — archive ile
    aynı kaynaktan ikinci tam derleme (~yarım dk-1 dk).
2.4 [O] Önbellek yok: SwiftPM (`ios/WinDropKit/.build`), Homebrew (`brew install xcodegen` ios.yml:39,
    her koşuda indirir; macos-latest imajında xcodegen önceden yok, belirsiz). Öneri: `actions/cache`
    ile `.build` (anahtar `Package.resolved`/`Package.swift`); xcodegen için `brew` yerine sabit sürüm
    ikilisi indirip önbellekle; dependabot yok (aşağıda 3.7).
2.5 [O] Windows için CI YOK; `dotnet test` (506+ test) yalnız yerelde. Öneri: `windows-latest` işi
    (`dotnet test windows/WinDrop.slnx -c Release`; WinRT BLE/etkin nokta gerektirmeyen testler zaten
    donanımsız) — özel depoda Windows dakikası 2x, macOS'tan çok ucuz; `paths: windows/**`. Ayrıca
    ubuntu işi: `cd relay && npm ci && npm test && npm run typecheck` (macOS interop işi `npm test`
    çalıştırmıyor, interop.yml:44-47 yalnız `npm ci`).
2.6 [O] interop.yml:15-17 — macOS'ta çalışıyor çünkü Swift `relay-phone` Linux'ta çalışmıyor
    (INTEROP.md:97); doğru. Tetik yolları geniş değil, iyi. Ama `windows/tools/**` içinde `bin/obj`
    olmasa da yol filtresi çalışır. Koşu 1,3 dk — kabul edilebilir.
2.7 [D] Artifact saklama: `upload-artifact` varsayılan 90 gün (artifact listesinde son kullanma
    2027-01-16); IPA ~1,4-1,5 MB x her push. `retention-days: 7` (IPA) ve 3 (loglar) ver; xcodebuild-log
    yalnız hata olunca (`if: failure()`) yüklensin.
2.8 [D] Eylem sürümleri `@v5` etiketli (checkout, setup-node, upload-artifact); sabitleme (SHA) veya
    dependabot `github-actions` önerilir. `permissions: contents: read` zaten var (iyi).
2.9 31 Ekim boşluğu (olay): Uzak depo üstünden doğrulandı — `gh api .../commits` ve `pushed_at` son
    commit'in 2026-10-18T21:20:28Z (1b0affa), depoya 18 Ekim'den sonra HİÇ push gelmemiş; iki iş akışı
    `active` durumda. Yani 31 Ekim'de "tetiklenmeyen push" uzak tarafta görünmüyor: en olası neden push'un
    hiç uzağa gitmemesi (yalnız yerel commit; yerel `git log` da son commit 19 Ekim 00:20 gösteriyor,
    yani yerel de yok — başka makine/dal olabilir). Diğer olası nedenler (hepsi belirsiz): commit
    mesajında `[skip ci]`; yalnız etiket/silme push'u; push farklı depoya/forklara gitti; Actions
    kotası bittiyse koşu "queued/failure" olurdu, boşluk değil; 60 gün hareketsizlikte zamanlanmış
    iş akışı devre dışı kalır ama `push` tetiği etkilenmez. Kesin teşhis için: `gh api
    repos/efeeyyy/windrop/events` ve ilgili push'un yapıldığı makinede `git reflog`/`git push -v`.

## 3. Depo hijyeni

3.1 [Y] `sideloadlydaemon.log` (kök, 2,4 MB) depoya girmiş (git ls-files) ve .gitignore'da yok; şu an
    "M" (değişmiş). İçinde cihaz UDID'si ve cihaz adı ("00008110-...", "Efey") var — özel depoda bile
    gereksiz kişisel veri. Öneri: `git rm --cached sideloadlydaemon.log`, `.gitignore`'a `*.log` ve
    `sideloadlydaemon.log` ekle; geçmişten silmek istenirse `git filter-repo` (özel depo, tek kullanıcı:
    kabul edilebilir).
3.2 [O] protocol/vectors/cacophony.txt 1,6 MB: kabul edilebilir (test vektörü) ama LICENSE/kaynak
    notu gerekir (Cacophony MIT; kaynak ve sürüm belgelenmeli — belirsiz, dosya başlığına bakılmadı).
3.3 [O] .gitignore eksikleri: `node_modules/`, `.wrangler/`, `.dev.vars` relay/.gitignore'da var (iyi,
    kök .gitignore'da yok; `.p8`, `*.pem`, `*.mobileprovision`, `*.p12` desenleri ekle). Eksik:
    `*.log`, `.vscode/`, `.idea/`, `*.zip` (dist zaten), `**/publish/`, `ios/**/.swiftpm/`, `*.xcodeproj/`
    (kısmen var), `.env*`. WinDropKit/.gitignore var.
3.4 [D] `git ls-files` taraması: `dist/`, `node_modules`, `.build`, `*.ipa`, `bin/obj`, `.p8/.p12/.pem/.key`
    izlenmiyor (temiz). `windows/tools/WinDrop.Interop/bin` ve `obj` diskte var, izlenmiyor (gitignore
    kapsıyor). Sır taraması yalnız dosya adı/desen düzeyinde yapıldı; içerik taraması yapılmadı (belirsiz).
3.5 [O] LICENSE ve kök README.md YOK. Depo özel olsa da açılacaksa karar ver; bağımlılık lisansları
    (QRCoder, SwiftCrypto/Noise, Cacophony vektörleri) için NOTICE gerekebilir.
3.6 [O] `.gitattributes` YOK: Windows'ta CRLF/LF karışıklığı (özellikle `*.sh` bash betikleri CRLF olursa
    macOS CI'da `bad interpreter` hatası verir; `run-interop.sh`, `run-relay-interop.sh`). Öneri:
    `* text=auto`, `*.sh text eol=lf`, `*.ps1 text eol=crlf` (veya lf), `*.png *.ico *.ipa binary`.
3.7 [D] Ölçüm araçları (BleProbe, NetProbe, ApProbe, PlatformCheck) ürünle aynı `WinDrop.slnx` çözümünde
    (windows/WinDrop.slnx:4-7) ve `dotnet test`/derleme süresini şişirir. ARCHITECTURE ölçümleri bitti
    (STATUS:8-10). Öneri: ayrı çözüm `WinDrop.Tools.slnx` veya `windows/tools/` altına taşı; BLE/NET/AP-PROBE.md
    belgeleri `docs/archive/` altına. PlatformCheck (hotspot-cycle, bt-audio) hâlâ gerçek donanım
    doğrulaması için yararlı; tut. Dependabot yapılandırması yok.

## 4. Belgeler

4.1 [Y] docs/APP-WINDOWS.md:57 — "Eksik: aktarma (relay) yolu, küçük dosyalar için BLE veri yolu,
    klasör gönderme, Bluetooth ses algısı" artık yanlış (STATUS:39-44, git log 1e01d53/58c9d98/ef…). Sil.
    Ayrıca :49 "install.ps1 (yönetici)" ile INSTALL.md:15 "yönetici yalnız güvenlik duvarı için" çelişir;
    :46 "Tüm metinler Türkçe" — resx ile İngilizce eklendi (1b0affa); :44 vb. "iPhone ekle" QR
    akışı ARCHITECTURE:54-58 ile uyumlu mu denetle.
4.2 [Y] docs/APP-IOS.md:23 — "Paylaşım uzantısı: v1'de yok" yanlış: WinDropShare.appex var (ios.yml:67-70,
    entitlements dosyası, STATUS:20). :3 "Mac yok" doğru; CI bölümü (:30-34) Release-Push, Linux işi ve
    uzantı denetimini içermiyor. Dil/yerelleştirme (xcstrings), Galeri kaydı, App Group tek kimlik
    (STATUS) anlatılmamış.
4.3 [Y] docs/ARCHITECTURE.md:18-19 — "İlk eşleştirme QR ile" cümlesi aynı belgenin :54-58 kararıyla ("QR
    zorunlu değil; ilk temas yakındaki herkes modunda") çelişiyor; :57-58 önceki kararı iptal ettiğini
    söylese de :18-19 güncellenmedi. :19 "6 haneli kod ... PAKE" PROTOCOL.md:42 ile tutarlı. :132-154
    ölçüm bulguları tarihsel; "Güncelliği doğrulanmadı" etiketi ve tarih ekle; "ağ profili yok" vs. hâlâ geçerli mi?
4.4 [O] docs/STATUS.md:3 "Son güncelleme: 2026-10-17" ama içerik 18 Ekim 23:xx'e kadar; :20 "iOS ... cihazda
    denenmedi" (sonraki bölümde cihaz testi yapıldı, :55); :40 "relay ... 22 test" (şimdi 43); :22 CI run
    id'si eski; :29-31 testleri WSL ile çalıştırma — yeni CI/Windows testi eklenince değişir. Dosya
    ardışık "tur" notlarıyla büyüyen günlüğe dönmüş. Öneri: yeniden yaz: (1) güncel durum tablosu,
    (2) açık işler, (3) ortam; geçmişi `docs/archive/` veya git log'a bırak. :70-75 açık işler:
    "sabah testleri" (1) tamamlandı; relay dağıtımı/push/simge hâlâ açık mı güncelle. :41 ve :53 dist
    yolları "dist\" yerelde (gitignore'lı) — başka makinede çalışmaz.
4.5 [O] docs/MORNING-TESTS.md (tamamı): tek seferlik 18 Ekim listesi; :57 "hiçbir cihazda çalıştırılmadı"
    yanlış; STATUS:55-62 ilk cihaz testlerini zaten kayda geçirmiş; makineye özel yollar (`C:\Users\Efe\...`).
    Öneri: SİL (ya da `docs/archive/` + güncel "elle doğrulama kontrol listesi"ne dönüştür: tablo
    :37-52 yararlı; kişisel yolları ve "sabah" çerçevesini kaldır, TESTING.md adıyla).
4.6 [O] docs/INTEROP.md:61 "BLE taşıması çapraz denenmedi" — gerçek cihazda doğrulandı (STATUS:55);
    relay bölümü iyi. :17/:70 betik yolu doğru. docs/INSTALL.md güncel görünüyor (kök README'ye linklenmeli).
4.7 [D] Birleştirme önerisi: BLE-PROBE.md + NET-PROBE.md + AP-PROBE.md (toplam ~136 satır) tek `docs/archive/
    MEASUREMENTS.md`; ARCHITECTURE "Ölçüm bulguları" ile tekrar var. APP-WINDOWS.md + APP-IOS.md,
    ARCHITECTURE ile birleştirilebilir ya da "tasarım (v0)" yerine "uygulama yapısı" olarak güncellenmeli;
    başlıklarında "v0, 2026-10-17" geçiyor, kod ilerledi. PROTOCOL.md (290 satır) kanonik kalsın.
4.8 Yeni kullanıcı için kök README.md taslak başlıkları:
    1. WinDrop nedir (AirDrop benzeri Windows 11 <-> iPhone/iPad; uçtan uca Noise şifreli; hesap yok)
    2. Ekran görüntüsü / nasıl çalışır (BLE keşif, doğrudan TCP, Windows etkin nokta, isteğe bağlı relay)
    3. Gereksinimler (Windows 11 22621+, Bluetooth LE, iOS 17+; Mac gerekmez)
    4. Windows kurulum (zip / install.ps1; güvenlik duvarı notu) -> docs/INSTALL.md
    5. iPhone kurulum (CI'dan imzasız IPA -> Sideloadly/AltStore; ücretsiz hesapla 7 gün; push/hotspot yetkisi
       yalnız ücretli hesapla)
    6. İlk kullanım ve eşleştirme (QR isteğe bağlı; yakındaki herkes modu + 4 haneli kod)
    7. Aktarma sunucusu (isteğe bağlı) -> relay/README.md; push (isteğe bağlı)
    8. Gizlilik ve güvenlik modeli (özet; docs/PROTOCOL.md)
    9. Derleme ve test (dotnet test, swift test, relay npm test, CI)
    10. Sorun giderme (BLE ses kesilmesi, güvenlik duvarı, eduroam/istemci yalıtımı, iOS yerel ağ izni)
    11. Belge dizini, lisans, katkı, bilinen sınırlar

## Öncelik özeti
Hemen: 3.1 (log dosyası), 2.1-2.2 (macOS dakikası), 4.1-4.3 (yanlış belge), 3.6 (.gitattributes), 2.5 (CI test).
Sonra: 1.1-1.5, 4.4-4.5, 3.5, 4.8.
