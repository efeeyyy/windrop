# WinDrop — Mimari ve temel kararlar

Durum: taslak v0 (2026-10-16). Bu dosyadaki kararlar projenin temelidir; değişiklik
gerekirse buraya gerekçesiyle yazılır, kod sessizce farklılaşmaz.

## Amaç
Windows 11 ↔ iPhone/iPad (ve ileride Windows ↔ Windows) arasında AirDrop benzeri gönderim.
Şart: telefon eduroam/kurumsal Wi-Fi'dan **kopmadan**, başka ağa bağlanmadan gönderim.
LocalSend bu ağlarda çalışmaz: keşif multicast (224.0.0.167:53317) + alt ağ HTTP taramasına,
aktarım peer'e doğrudan TCP'ye dayanır; client isolation ikisini de keser.

## Kesin kararlar
1. **Taşımadan bağımsız şifreli kanal.** Uygulama verisi her zaman uçtan uca şifreli bir
   oturumdan geçer; BLE, LAN ve aktarma sunucusu yalnız bayt taşır. Aktarma sunucusu içeriği
   göremez. Şifreleme kendi icadımız olmaz: **Noise Protocol Framework** (eşleştirme `XXpsk3`,
   sonraki bağlantılar `IK`; ayrıntı `PROTOCOL.md` §2), iki tarafta da resmi test vektörleriyle doğrulanır.
   - Windows: NSec (libsodium) veya denetlenmiş bir Noise kütüphanesi. iOS: CryptoKit.
2. **Cihaz kimliği ve eşleştirme.** Her cihazın kalıcı X25519 anahtarı var. İlk eşleştirme QR
   ile (6 haneli kod yalnız ileride PAKE/CPace ile; psk olarak kullanılamaz); sonra güvenilen cihaz listesi. LocalSend'deki
   doğrulamasız TOFU yapılmaz.
3. **Keşif BLE ile.** Özel bir GATT servis UUID'si yayınlanır. Multicast/alt ağ taramasına
   güvenilmez.
4. **Taşıma seçimi otomatik** (kullanıcı görmez), sırasıyla:
   1. BLE üzerinden adres adayları (IPv4/IPv6 + port) değiş tokuşu → doğrudan TCP/QUIC denemesi
      (izolasyon yoksa en hızlısı; multicast gerekmez).
   2. Küçük dosya (eşik prototipte ölçülecek) → BLE GATT.
   3. Aksi halde → internet üzerinden aktarma (Cloudflare ücretsiz katmanı; TURN mı R2 mi açık).
5. **Aktarım çerçevesi:** uzunluk önekli çerçeveler, parça (chunk) + ofset ile **devam
   ettirilebilir** aktarım, dosya başına SHA-256 doğrulaması, oturum/dosya kimlikleri, iptal.
   (LocalSend'de devam ettirme yok; büyük dosyada sorun.)
6. **Dil/platform:** Windows = C# / .NET 10 (WinRT API'leri; arayüz sonra WinUI 3).
   iOS/iPadOS = Swift, SwiftUI, CoreBluetooth. iOS derlemesi GitHub Actions macOS'ta imzasız
   IPA; yükleme Sideloadly/AltStore + ücretsiz Apple hesabı (7 gün). Mac yok.
7. **Çekirdek ile platform ayrık:** protokol/çerçeve/şifreleme `WinDrop.Core` (platformsuz,
   birim testli); BLE/WinRT/arayüz ayrı projelerde. iOS tarafı aynı spesifikasyonu
   (`docs/PROTOCOL.md`, yazılacak) uygular; iki tarafı ortak test vektörleri bağlar.

## LocalSend'den alınan fikirler (REA incelemesi, LocalSend 1.17.0, 2026-10-16)
- Oturum + dosya başına token, SHA-256 uyuşmazlığında red, deneme sayacı (PIN/eşleştirme).
- Favori/güvenilen cihazdan **otomatik kabul** (quick-save), otomatik başlangıç `--hidden`, tray.
- Explorer entegrasyonu: `SendTo` klasörüne kısayol (yönetici/kayıt defteri gerektirmez);
  paylaşım menüsü ileride.
- Ağ arayüzü filtresi, "keşif hata ayıklama" sayfası (eduroam testleri için değerli).
- Uygulaması olmayan cihaza tarayıcıdan indirme sayfası — ileride, opsiyonel.
- Kaçınılacak: yalnız multicast keşfi, doğrulamasız self-signed TLS, devam ettirmesiz yükleme,
  yönetici gerektiren güvenlik duvarı müdahalesi.
- LocalSend protokol uyumu (aynı LAN'da LocalSend uygulamalarıyla konuşma) **çekirdeğe
  girmez**; ileride opsiyonel LAN modu olabilir. Kod kopyalanmaz (Apache-2.0; yalnız fikir).

## Kullanıcı deneyimi kararı (Efe, 2026-10-17) — "AirDrop gibi, hiç uğraşmadan"
- Uygulama açılınca **kurulum/eşleştirme adımı yok**. İzinler (Bluetooth, yerel ağ, fotoğraflar)
  yalnız kullanıcı **Paylaş/Gönder**'e bastığında istenir (iOS'ta CBCentralManager vb. tembel oluşturulur).
- Gönder'e basınca yakındaki cihazlar AirDrop gibi **kendiliğinden belirir**; kullanıcı birine dokunur.
- Bu nedenle QR eşleştirme **zorunlu değil**; ilk temas "yakındaki herkes" modunda (PROTOCOL §3.1):
  Noise XX + **alıcı ekranında onay** (AirDrop'taki gibi) + iki ekranda gösterilen 4 haneli doğrulama
  kodu (isteğe bağlı karşılaştırma). Kabul edilen karşı taraf "tanıdık cihaz" olarak hatırlanır;
  sonraki bağlantılar IK. QR eşleştirme yalnız "otomatik kabul" için isteğe bağlı kalır.
  (Önceki "QR şart, TOFU yok" kararının yerini alır; güvenlik alıcı onayı + kod ile sağlanır.)
- Gönderim ekranında gerekli uyarılar: aktarma yolu seçilirse "Bu gönderim Wi-Fi veya hücresel
  veri kotanızdan gidecektir"; cihaz görünmüyorsa "Her iki cihazda da WinDrop'un açık olduğundan
  ve Bluetooth'un açık olduğundan emin olun".

### iPhone'da uygulama açık değilken (Efe, 2026-10-17: "WhatsApp'tan farkı kalmamalı")
- **iPhone → PC:** iOS **paylaşım menüsü uzantısı** (Fotoğraflar/Dosyalar → Paylaş → WinDrop):
  uygulamayı açmadan, uzantı ekranında yakındaki PC'ler belirir, dokununca gider. Uzantı kendi BLE +
  TCP bağlantısını kurar (ekran açıkken). App Groups ücretsiz hesapta yoksa uzantı kendi cihaz
  anahtarını kullanır (PC'de ayrı "tanıdık" kayıt olur).
- **PC → iPhone:** uygulama **bir kez açıldıktan sonra** (ve kullanıcı uygulamayı kaydırıp
  kapatmadıkça) iOS, `bluetooth-central` arka plan modu + durum geri yükleme ile tanıdık PC'lere
  bekleyen BLE bağlantısını kendisi kurar, uygulamayı arka planda kısa süre uyandırır → `hello` →
  PC iPhone'u "yakında" gösterir. PC'den `offer` gelince iPhone **bildirim** gösterir
  ("EFEY 3 dosya göndermek istiyor"); dokununca uygulama öne gelir ve aktarım başlar (AirDrop'taki
  "Kabul" dokunuşunun karşılığı). Sınırlar: uygulama elle kapatılırsa (zorla çıkış) iOS onu
  uyandırmaz; ilk kez görülen PC için uygulama bir kez açık olmalı.
- Ek (sonra): Windows, iOS arka plan reklamının Apple "overflow" alanındaki (üretici verisi
  0x004C) servis bit maskesini okuyarak, bağlantı kurulmadan da yakındaki iPhone'u fark edebilir
  (ekran açıkken; doğrulanmadı).
- Uygulamayı hiç açmadan/bildirime dokunmadan dosya **almak** iOS'ta üçüncü taraf uygulamaya
  mümkün değil (yalnız Apple'ın AirDrop'u yapabiliyor).
- **Zorla kapatılmış uygulama için push (WhatsApp/Instagram yöntemi) — ücretli hesapla:** PC,
  tanıdık iPhone'a teklifi internet üzerinden küçük bir sunucuya (aktarma sunucusuyla aynı,
  Cloudflare Worker) iletir; sunucu APNs ile bildirim yollar; bildirim uygulama zorla kapatılmış
  olsa bile görünür; dokununca uygulama açılır, BLE/TCP ile aktarım başlar. Gereken: ücretli Apple
  Developer hesabı (ücretsiz hesapta push yetkisi yok), iki tarafta internet, PC ile iPhone'un daha
  önce bir kez karşılaşmış olması (APNs belirteci o zaman şifreli kanaldan PC'ye verilir).
  Bildirim içeriği yalnız "EFEY dosya göndermek istiyor" (dosya adı sunucuya gitmez).
- **Uyarılar uygulamada yazılı olacak** (Efe, 2026-10-17): Ayarlar > "Nasıl çalışır" ve ilk
  aktarımdan sonra bir kez gösterilen bilgi kartı: (1) uygulamayı yukarı kaydırıp kapatırsanız
  bilgisayar telefonu göremez — push yoksa; (2) yeni bir bilgisayar için uygulamayı bir kez
  açın; (3) gelen dosyalar için bildirime dokunmanız gerekir (AirDrop'taki Kabul gibi).
  Açılışta gösterilmez (kurulumsuz deneyim kararı).

## Ortak ağ yoksa taşıma sırası (taslak, 2026-10-16)
Keşif ve anahtar değişimi her durumda BLE ile; sonra sırayla:
1. **Aynı ağ + erişilebilir** → doğrudan TCP (en hızlı).
2. **iPhone hiçbir Wi-Fi'a bağlı değilse** (dışarıda, mobil veride) → PC kendi Wi-Fi Direct
   erişim noktasını açar, SSID/parolayı şifreli BLE kanalından yollar, iOS uygulaması
   `NEHotspotConfiguration` ile katılır (tek seferlik iOS onayı). İnternet gerekmez, Wi-Fi hızı.
   iPhone internetini mobil veriden sürdürür. iPhone eduroam'daysa bu yol **kullanılmaz** (Efe şartı).
   Doğrulanacak: ücretsiz Apple hesabında Hotspot Configuration yetkisi; Windows'ta Wi-Fi Direct
   erişim noktası + mevcut Wi-Fi aynı anda.
3. **İki taraf da internette ama birbirine ulaşamıyor** → uçtan uca şifreli aktarma sunucusu
   (mobil veri harcar; kullanıcıya boyut uyarısı).
4. **Hiçbiri yoksa** → yalnız çok küçük dosyalar BLE ile (ses çalarken hiç).

Efe'nin gereksinimleri (2026-10-16):
- 2. yol **tamamen otomatik ve hızlı**; kullanıcı hiçbir adım yapmaz. Uygulanacak: PC başına tek,
  sabit SSID/parola (eşleştirmede rastgele üretilir, türetilmez), böylece iOS katılma onayı yalnız
  ilk seferde çıkar (iOS'un kendi onayı kaldırılamaz). iPhone hiçbir Wi-Fi'da değilse etkin nokta
  `offer` anında, doğrudan yol denenmeden açılır (PROTOCOL §7.2).
- 3. yol (aktarma) sessizce kullanılmaz: ağ izolasyonlu ve cihazlar doğrudan ulaşamıyorsa
  **"İnternet/mobil veri kullanılacak (X MB)" uyarısı ve onay**. Kullanıcı aksi halde Drive gibi
  bir servise yükler; bizim yolun farkı uçtan uca şifreli olması (sunucu içeriği göremez) —
  uyarıda bu da söylenir.

## Ölçüm bulguları
- 2026-10-16, Efe'nin PC'si (Realtek RTL8852BE) + iPhone/nRF Connect: MaxPduSize 527, bildirim
  yükü 524 B. `stream 256` "486 KB/s" gösterdi; BLE'nin fiziksel sınırı (~170–180 KB/s) üstünde,
  yani gönderen tarafın ölçümü kontrolcü tamponunu sayıyor — **gerçek hız yalnız alıcıda ölçülür.**
- **Aynı anda AirPods (A2DP) ile çalan müzik, stream sırasında kesildi** (Efe gözlemi). Kombine
  Wi-Fi/BT çipinde BLE trafiği klasik BT ses akışını aç bırakabiliyor. Gereksinim: BLE veri
  aktarımı **hız sınırlı (pacing)** olmalı; Bluetooth ses çıkışı etkinken BLE'ye yalnız kontrol/
  küçük veri, büyük veri diğer yollardan.
- 2026-10-16 hız taraması (`--sweep 20,50,100,150`, aşama başına 512 KB, AirPods çalarken):
  **20 KB/s'de bile ses önce parazitlendi, sonra tamamen kesildi** (Efe gözlemi; tam aşama eşleşmesi
  kesin değil, 1. aşamada başladığı anlaşılıyor). Sonuç (bu donanım için): Bluetooth ses aktifken
  BLE ile toplu veri aktarımı **kullanılamaz**. Taslak karar: BLE = keşif + anahtar değişimi +
  adres/sinyal (birkaç KB); dosya verisi doğrudan IP veya aktarma sunucusundan. "Küçük dosya
  BLE'den" kuralı yalnız Bluetooth ses kapalıyken ve çok küçük dosyalar için opsiyonel kalır.

- 2026-10-16 İTÜ eduroam doğrudan IP testi (NetProbe, Safari): PC `161.9.21.119`, iPhone
  `161.9.20.244` (ikisi de genel IPv4; IPv6 bu ağda yok). **Telefon PC'ye doğrudan ulaştı**
  (güvenlik duvarı kuralı açıldıktan sonra): PC→telefon 20 MB 8,2 MB/s, telefon→PC 10 MB ~16 MB/s
  (sunucu ölçümü). Çıkarım: bu ağda sorun **unicast izolasyonu değil, multicast keşfin
  engellenmesi** (LocalSend'i bu kırıyor). Tek ağ/tek zaman ölçümü; başka kurumlarda izolasyon
  olabilir, aktarma yolu yedek olarak kalır.
- Windows güvenlik duvarı gelen bağlantıyı varsayılan olarak keser; ürün kurulumunda kural
  gerekecek (yükleyici yönetici yetkisiyle bir kez ekler) — yoksa doğrudan yol hiç çalışmaz.

- 2026-10-16 ApProbe: eduroam bağlıyken Wi-Fi Direct legacy AP **açıldı** (192.168.137.1);
  25 sn boyunca eduroam ve internet kopmadı → PC tarafında eşzamanlılık var.
  **Ama iPhone bu AP'ye katılamadı** ("failed to join", 2 deneme). Aynı anda Windows **Mobil
  etkin nokta** (bant: Any available) ile iPhone katılabildi. Şüphe: Wi-Fi Direct AP, eduroam'ın
  kanalında (5 GHz kanal 100, DFS) açılıyor; Mobil etkin nokta başka bant/kanal seçiyor.
  Kartta tek GO yuvası var (`P2P GO ports count: 1`): ikisi aynı anda açık olamaz.
  Sonraki deneme: Mobil etkin nokta API'si (`NetworkOperatorTetheringManager`, bant seçilebilir).
- 2026-10-17 Mobil etkin nokta (2,4 GHz, PC aynı anda eduroam 5 GHz'te), Wi-Fi kapat/aç sonrası:
  iPhone katıldı (192.168.137.210). PC→telefon 20 MB **2,3 MB/s**, telefon→PC 10 MB **3,9 MB/s**.
  Eduroam doğrudan yoldan (8/16 MB/s) yavaş: 2,4 GHz + iki kanal arası paylaşım (MCC). PC
  eduroam'da değilken (asıl senaryo: dışarıda) daha hızlı olması beklenir — doğrulanmadı.
  Etkin nokta arayüzünün ağ profili yok → güvenlik duvarı "Genel" gibi davranıyor; ürün kuralı
  `192.168.137.0/24` uzak adresle, profil bağımsız eklenmeli.
- 2026-10-16 araştırma: `NEHotspotConfiguration` (otomatik ağa katılma) ücretsiz Apple hesabında
  büyük olasılıkla **yok** (Apple DTS: Network Extension yetkileri ücretli takıma; kesin değil,
  ilk iOS derlemesinde görülecek). Ücretsiz hesapta: tek seferlik elle katılma (sabit SSID
  sayesinde sonra otomatik); ücretli hesapta tam otomatik. Wi-Fi Aware: Windows desteklemiyor.

## Açık sorular (prototiple cevaplanacak)
- iOS arka planda BLE yayını Windows'tan görünmez (overflow alanı yalnız iOS okur).
  PC → iPhone gönderiminde iPhone uygulaması açık olmalı mı? Alternatifler değerlendirilecek.
- Windows GATT sunucusunda gerçek MTU ve bildirim hızı; L2CAP CoC Windows'ta kullanılabilir mi.
- BLE/aktarma eşiği; eduroam'da doğrudan IPv6 bağlantısı ne sıklıkta geçiyor.
- Aktarma: Cloudflare TURN (WebRTC) mi, kendi WebSocket aktarması mı, R2 mi.

## İş bölümü (Efe'nin isteği, 2026-10-16)
Temel (mimari, protokol, şifreleme, BLE katmanı) Haiku'ya verilmez. Haiku: derleme/test
çalıştırma, hata satırı raporu, net tarifli mekanik düzenleme, tarama.
