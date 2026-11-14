# Windows çekirdek kod incelemesi — WinDrop.Core + WinDrop.Platform

Tarih: 2026-11-13. Salt-okur inceleme. Kapsam: `windows/src/WinDrop.Core` (Transfer, Session) ve `windows/src/WinDrop.Platform`
(özellikle `TransferCoordinator.cs`). Dayanak: `docs/PROTOCOL.md`. Satır numaraları incelemedeki hâliyle verilmiştir.
Derleme ve test çalıştırılmadı; bulgular kod okumasına dayanır. Cihazda denenmemiş olanlar "doğrulanmadı" diye işaretlidir.

Özet: Kritik 0 · Yüksek 5 · Orta 8 · Düşük 8 (toplam 21)

---

## Yüksek

### 1. Etkin TCP veri oturumunda boşta kalma zaman aşımı yok; aktarım sonsuza dek "Active" kalabiliyor
- **Dosya:** `TransferCoordinator.cs:1279-1288`, `DataListener.cs:192-235`, `FileReceiver.cs:273-275`, `FileSender.cs:139`
- **Sorun:** `WaitForDataAsync` zaman aşımını yalnız `!s.ConnectionActive` iken uyguluyor. El sıkışmadan sonra soketin
  okuma zaman aşımı yok, TCP keepalive de yok (`DataListener` yalnız `NoDelay` ayarlıyor). Bağlantı yarı açık kalırsa
  (FIN/RST hiç gelmezse) `ReceiveFrameAsync` sonsuza dek bekler.
- **Senaryo:** iPhone etkin noktaya bağlı, PC alıcı. PC bir ACK gönderiyor, sonra iOS uygulamayı askıya alıyor ya da telefon
  menzilden çıkıyor. PC'de bekleyen gönderilmemiş veri olmadığı için TCP hata vermez. Aktarım "Active" kalır.
  `HotspotManager._activeTransfers` hiç azalmadığından etkin nokta da hiç kapanmaz (`HotspotManager.cs:197-210`).
  PC gönderen olduğunda da aynı şey olur: bütün veri gitmiş, `DONE` bekleniyorsa (`FileSender.cs:139`) yine takılır.
- **Düzeltme:** `HandleClientAsync` içinde keepalive açılmalı:
  `client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);`
  ve `SocketOptionName.TcpKeepAliveTime = 15`, `TcpKeepAliveInterval = 5`, `TcpKeepAliveRetryCount = 3`.
  Ayrıca `WaitForDataAsync`'a bir veri boşta sınırı eklenmeli:
  `if (s.ConnectionActive && now - s.LastActivity > _o.Timeouts.DataIdle) s.Registration?.Dispose()/aktif bağlantıyı kapat`.
  `LastActivity` zaten `OnProgress` ile güncelleniyor.

### 2. Çift yolda (`keepExistingActive`) ölü bağlantı, yeniden bağlanmayı kalıcı olarak reddediyor
- **Dosya:** `TransferCoordinator.cs:740`, `906` (`() => s.DualPath`), `996`, `1005`; `DataListener.cs:77`
- **Sorun:** `DualPath` bir kez true olunca `TrySetActive`, eski `_active` durdukça her yeni (kimliği doğrulanmış) bağlantıyı
  reddeder. Bu kural yalnız ilk yol yarışı için gerekli. Ancak aktarım boyunca geçerli kalıyor. PROTOCOL §6'ya göre
  ise kopmadan sonra yeniden bağlanmak serbesttir.
- **Senaryo:** addr ile etkin nokta birlikte gönderilmiş, aktarım etkin noktadan sürüyor. Telefon bilinen Wi-Fi ağına geçiyor.
  Eski TCP bağlantısı yarı açık kalıyor (bkz. 1). Telefon addr yolundan yeniden bağlanıyor, ama reddediliyor
  ("başka bir veri oturumu sürüyor"). 1 numaralı bulguyla birleşince aktarım kalıcı olarak takılır.
- **Düzeltme:** Koruma yalnız yarış penceresiyle sınırlanmalı. `OnDataConnectionAsync` başında `s.ActiveSince = now` yazılır.
  Kayıtta da `keepExistingActive: () => s.DualPath && now - s.ActiveSince < TimeSpan.FromSeconds(10)` kullanılır.
  Başka bir yol: ilk `DATA`/`RESUME` görülünce `s.DualPath = false` yapılır.

### 3. Çift yolda tek bir `*_fail` iletisi bütün yol beklemesini bitiriyor
- **Dosya:** `TransferCoordinator.cs:438-439`, `1276-1277`
- **Sorun:** `PathFailed` tek bir TCS. Hangi yol başarısız olursa olsun `tcp_fail` veya `hotspot_fail` gelince
  `PathUnavailableException` fırlatılıyor. Öteki yol henüz denenmekte olsa bile sonuç değişmiyor.
- **Senaryo (§6 yol değişimi):** addr gönderildi, kullanıcı iPhone'da Wi-Fi'ı kapattı. `status wifi=none` gelince ek etkin nokta
  açılıp gönderiliyor (`TrySendExtraHotspotAsync`). Telefonun süren addr denemesi Wi-Fi kapandığı için başarısız oluyor ve
  `tcp_fail` yolluyor. PC etkin nokta yolunu beklemeden BLE'ye veya aktarma sunucusuna düşüyor. Böylece yol değişimi özelliği
  tam da hedeflediği durumda çalışmıyor. iOS'un ek yol aldıktan sonra yine `tcp_fail` gönderip göndermediği doğrulanmadı.
- **Düzeltme:** Yol başına başarısızlık bayrağı tutulmalı (`AddrFailed`, `HotspotFailed`). `OnPath` yalnız ilgili bayrağı
  işaretler. `PathFailed` ancak gönderilmiş (claimed) bütün yollar başarısız olunca tamamlanır. Ek yol gönderilince
  `Touch` ve `ConnectTimeout` o yola göre yeniden kurulmalı. `SendHotspotPathAsync` bunu zaten yapıyor.

### 4. Yedek yola (BLE/aktarma) geçince yol beklemesi kapanmıyor: paralel TCP aktarımı, yanlış sonuç, parola yenilenmemesi
- **Dosya:** `TransferCoordinator.cs:990-991` (`IsWaitingForPath`), `1017-1040`, `1109-1131`, `1168-1170`, `946-971`,
  `1405-1414`, `1467-1483`
- **Sorun:** `RunPathAsync`, `PathUnavailableException` sonrasında aktarma onayını 2 dk bekliyor. Bu sırada
  `IsWaitingForPath` hâlâ true. TCP kaydı (`s.Registration`) da onaydan sonraya kadar açık. Wi-Fi durumu değişince
  `OnPeerWifiChanged` ek addr ya da etkin nokta gönderebiliyor. Gelen TCP bağlantısı da `OnDataConnectionAsync`'i aktarma
  akışına paralel çalıştırıyor. Bunun üstüne `Finish` `s.Cts`'yi iptal etmiyor. Bu yüzden son duruma geçmiş bir aktarımın yan
  görevleri (ek yol gönderimi, `HotspotManager.StartAsync`) sürmeye devam ediyor.
- **Senaryolar:**
  - (a) Aktarma onayı beklenirken telefon etkin noktadan bağlanıp bütün dosyaları alıyor. Kullanıcı aktarmayı reddedince aktarım
    "İptal" (`RelayNotApproved`) diye bitiyor. Onaylarsa `Registration.Dispose()` süren TCP aktarımını kesiyor
    (`DataListener.cs:93-104`), ardından aktarma denemesi zaman aşımına uğrayıp "başarısız" oluyor.
  - (b) **Güvenlik:** Eş bilinmeyen bir cihaz. Ek etkin nokta `StartAsync` birkaç saniye sürüyor. Bu arada kullanıcı aktarmayı
    reddediyor, `RunGuardedAsync.finally` çalışıyor ve `HotspotSent == false` olduğundan parola yenilenmiyor. Ardından
    `StartAsync` dönüyor ve `ct` iptal edilmediği için `hotspot` (SSID + parola) bilinmeyen cihaza gidiyor. Parola bir daha
    hiç yenilenmiyor (PROTOCOL §6.2 ihlali).
  - (c) Dar bir pencerede `TransferStarted()` çağrısı `finally`'deki `TransferFinished()`'den sonra kalırsa sayaç dengesiz
    kalıyor ve etkin nokta boşta kapanmıyor.
- **Düzeltme:**
  - `RunPathAsync`'in catch bloğunda `s.PathClosed = true; s.Registration?.Dispose(); s.Registration = null;` yapılmalı.
  - `IsWaitingForPath` içine `&& !s.PathClosed` eklenmeli.
  - `Finish` içinde son durum yazıldıktan sonra `s.Cts.Cancel()` çağrılmalı.
  - `SendHotspotPathAsync` ileti göndermeden hemen önce `if (s.IsTerminal || s.PathClosed) return;` diye yeniden denetlemeli.
  - Parola yenileme kararı `HotspotSent` yerine "etkin nokta iletisi gönderilmeye başlandı" bayrağına bağlanmalı. Bu bayrak
    `s.Gate` altında, bir `Finalized` bayrağıyla birlikte tutulmalı. Geç kalan gönderim ya iptal edilmeli ya da yenilemeyi
    kendisi tetiklemeli.

### 5. `OnDataConnectionAsync` dar istisna süzgeci: beklenmeyen istisnada aktarım sonsuza dek "Active" kalıyor
- **Dosya:** `TransferCoordinator.cs:1069-1082`, `DataListener.cs:256-259`, `Storage.cs:185-189, 347, 353, 415`
- **Sorun:** Yalnız `IOException`, `ObjectDisposedException`, `ProtocolException`, `NoiseException` ve
  `OperationCanceledException` yakalanıyor. `UnauthorizedAccessException` (`File.Move` hedefe, kısmi dosya oluşturma),
  `ArgumentOutOfRangeException` (`ResumeAtAsync`) ve `InvalidOperationException` gibi istisnalar `DataListener`'ın
  "beklenmeyen hata" dalında günlüğe yazılıp yutuluyor. Bu durumda `ConnectionActive` true kalıyor, `DataDone` hiç
  tamamlanmıyor ve `WaitForDataAsync` hiç zaman aşımına uğramıyor (yalnız `!ConnectionActive` iken bakıyor).
- **Senaryo:** İndirme klasörü Denetimli Klasör Erişimi ile korunuyor ya da salt-okur yapılmış. Dosya tamamen alınıyor, ama
  `CommitAsync` içindeki `File.Move` `UnauthorizedAccessException` veriyor. Arayüzde aktarım %100'de "Active" kalıyor, etkin
  nokta kapanmıyor, kullanıcı hata görmüyor. `1272`'deki "TransferException olabilir" yorumu da geçersiz:
  `DataDone.TrySetException` hiçbir yerde çağrılmıyor.
- **Düzeltme:** En sona şu dal eklenmeli:
  `catch (Exception ex) { if (gen == s.Generation) s.DataDone.TrySetException(new TransferException(UserError.Of(UserErrorCode.TransferFailedWith, ex.Message))); }`
  (gerekirse `s.Fail(...)` da). `ConnectionActive = false` da `finally` içinde yapılmalı.

---

## Orta

### 6. Protokol ihlali ve yerel G/Ç hataları "bağlantı koptu" sayılıyor; kullanıcı 2 dk bekleyip yanlış ileti görüyor
- **Dosya:** `TransferCoordinator.cs:1069-1078`, `FileReceiver.cs:78-82`, `FileSender.cs:147`, `FolderScanner.cs:93` ile `Storage.cs:113` karşılaştırması
- **Sorun:** `ProtocolException` (ör. §5a geçersiz `dir`, yanlış `FILE_START`) ve yerel disk/kaynak `IOException`'ı
  (disk dolu, paylaşım ihlali, kaynak dosya kısaldı) yeniden bağlanılabilir kopma gibi ele alınıyor. Karşı tarafa zaten
  `CANCEL "protocol error"` gitmiş oluyor, yani karşı taraf yeniden bağlanmayacak. PC 2 dk (`Reconnect`) bekliyor, sonunda da
  `ConnectionLostNoReconnect` ("bağlantı koptu") gösteriyor.
  Ek olarak `FolderScanner`, okunabilirlik denetimini `FileShare.ReadWrite` ile yapıyor. `FileSystemFileSource.OpenReadAsync`
  ise `FileShare.Read` ile açıyor. Başka bir uygulamanın yazmak için açık tuttuğu dosya (günlük, Outlook PST, inmekte olan
  dosya) taramayı geçiyor, gönderimde paylaşım ihlali veriyor ve "bağlantı koptu" diye görünüyor.
- **Düzeltme:** `ProtocolException` → hemen `s.Fail(UserError.Of(TransferCutWith, "protocol"))` ve yeniden bağlanma
  beklenmez. Yerel dosya hataları `FileSender`/`FileReceiver` içinde ayrı bir `LocalIoException` türüne sarılıp hemen
  başarısız sayılmalı. Kaynak dosya `FileShare.ReadWrite | FileShare.Delete` ile açılmalı (bütünlüğü SHA-256 zaten koruyor).

### 7. Yeniden bağlanmada eski alıcı/gönderici bitmeden yenisi başlıyor (aynı kısmi dosya)
- **Dosya:** `DataListener.cs:71-83` (`old?.Dispose()` beklenmiyor), `TransferCoordinator.cs:1047-1067`, `Storage.cs:347-348, 382-394`
- **Sorun:** Yeni bağlantı eskisinin soketini kapatıyor, ancak eski `FileReceiver`'ın bitmesini beklemeden yeni bir
  `FileReceiver` başlatıyor. Eski alıcı `.part` dosyasını `FileShare.None` ile açık tutuyor. Yeni alıcı paylaşım ihlali
  (`IOException`) alıyor ve bu da "kopma" sayılıyor. Böylece iki bağlantı birden ölüyor ve telefonun bir kez daha bağlanması
  gerekiyor. Ayrıca iki örnek aynı `.meta.tmp` yoluna yazabiliyor (`PersistMetaAsync`).
- **Düzeltme:** `State` içinde `Task? ActiveRun` tutulmalı. `OnDataConnectionAsync` başında
  `await prev.WaitAsync(TimeSpan.FromSeconds(5))` (hata yutularak) çağrılmalı, ardından yeni koşucu başlatılmalı.

### 8. HotspotManager, kullanıcının etkin nokta yedeğinin üzerine WinDrop ayarını yazabiliyor
- **Dosya:** `HotspotManager.cs:115-117`, `126-136`
- **Sorun:** `_owned == true` iken etkin nokta kapalı bulunursa "kapalı" dalına giriliyor. Burada
  `original = Snapshot(current)` alınıyor, oysa o anki ayar WinDrop'un SSID/parolası. Bu `SaveBackup` ile kullanıcının
  özgün yedeğinin üzerine yazılıyor. Hata dalında da WinDrop ayarı "geri yükleniyor" ve yedek siliniyor.
- **Senaryo:** WinDrop etkin noktayı açtı. Kullanıcı hızlı ayarlardan elle kapattı ya da Windows güç tasarrufuyla kapandı
  (doğrulanmadı). 2 dk dolmadan yeni bir aktarım başladı. Kullanıcının kendi SSID/parolası kalıcı olarak kayboluyor.
- **Düzeltme:** `var original = _owned ? (LoadBackup() ?? Snapshot(...)) : Snapshot(...); if (!_owned) SaveBackup(original);`.
  Hata dalında yedek yalnız bu çağrı oluşturduysa silinmeli.

### 9. Parola yenilendikten sonraki yeniden başlatma, süren etkin nokta aktarımlarını koparıyor; bilinmeyen eş ağda kalıyor
- **Dosya:** `HotspotManager.cs:88-111`, `TransferCoordinator.cs:952-969`, `1409-1414`
- **Sorun:** Bilinmeyen eşin aktarımı bitince parola yenileniyor. Sonraki `StartAsync`, `_activeTransfers > 0` olsa bile etkin
  noktayı yeni parolayla durdurup yeniden başlatıyor. O sırada etkin noktadan aktarım yapan öteki telefon (eski parolayı biliyor)
  düşüyor ve geri katılamıyor. Bunun tersine, başka aktarım yoksa yenilemeden sonra etkin nokta 2 dk boşta süresi boyunca eski
  parolayla açık kalıyor ve bilinmeyen cihaz PC'nin 192.168.137.0/24 ağında kalmaya devam ediyor. Bu PROTOCOL §6.2'ye uygun,
  ama güvenlik açısından zayıf.
- **Düzeltme:** `StartAsync` gerçekte geçerli olan yapılandırmayı döndürmeli (`Task<HotspotConfig?>`). Koordinatör `hotspot`
  iletisinde o yapılandırmayı göndermeli. Yeni parolayla yeniden başlatma yalnız `_activeTransfers == 0` iken yapılmalı.
  Yenilemeden sonra başka aktarım yoksa etkin nokta hemen durdurulmalı (`StopAsync`).

### 10. Eşleştirmeyi kaldırmak süren veya bekleyen aktarımları durdurmuyor
- **Dosya:** `TransferCoordinator.cs:263-277`, `444-456` (`PushWaiting` atlanıyor), `DataListener.cs:216`, `RealServices.cs:72-73`
- **Sorun:** `DropPeer` yalnız `Negotiating` ve `AwaitingApproval` aşamasındaki aktarımları düşürüyor. `Connecting`/`Active`
  aktarımlar ile push bekleyenler sürüyor. Veri kanalında `samePeer` yalnız anahtar eşitliğine bakıyor, güvenilen listeye
  bakmıyor. Bu yüzden kaldırılmış cihaz 2 dk içinde aynı `transferId` ile IK üzerinden yeniden bağlanabiliyor. Push bekleyen
  gönderim de cihaz XX ile geri geldiğinde (`NotifyPushWaiters` oturum türüne bakmıyor) teklifi göndermeye devam ediyor.
  `KnownAtStart = true` olduğu için etkin nokta parolası da yenilenmiyor.
- **Düzeltme:** `DropPeer` içinde `PeerId` eşleşen bütün son duruma geçmemiş aktarımlar için `s.Fail(UserError.Of(PairingRemoved))`
  çağrılmalı ve `s.Registration?.Dispose()` yapılmalı. `NotifyPushWaiters` yalnız `session.IsKnown` ise uyandırmalı.

### 11. Spesifikasyondan sapma: kayıtlı ad `hello.name` ile değiştiriliyor (§0)
- **Dosya:** `TransferCoordinator.cs:367-374`, `TrustedDevices.cs:81-94`, `PeerSession.cs:37-38`
- **Sorun:** PROTOCOL §0: "Kayıtta eşleştirme anındaki ad tutulur; arayüzde her zaman bu ad gösterilir (`hello.name` değil)."
  Kod ise IK oturumunda gelen her `hello` adını kayda yazıyor. Eşli ya da tanıdık bir cihaz kendini başka bir güvenilen cihazın
  adıyla yeniden adlandırıp onay penceresinde onun yerine geçebilir. Ad uzunluğu da tutarsız: eşleştirmede ≤ 64
  (`PairingService.cs:246`), güncellemede ≤ 40 (`TrustedDevices.cs:101`). İlk IK oturumunda uzun ad sessizce kısalıyor.
- **Düzeltme:** Ya PROTOCOL §0 bilinçli bir kararla güncellenmeli ya da güncelleme kaldırılmalı. Ara çözüm olarak kayıtlı ad
  korunup "(şimdiki adı: X)" diye gösterilebilir. Uzunluk sınırı tek bir sabitte birleştirilmeli.

### 12. BLE veri yolunda "eş 30 sn çerçeve göndermezse düşer" yalnız ilk çerçeve için uygulanıyor (§6.4)
- **Dosya:** `TransferCoordinator.cs:519-528`, `BleDataChannel.cs:54-62`
- **Sorun:** Bekçi görev yalnız bir kez, `Connect` süresi sonunda `FrameSeen`'e bakıyor. İlk çerçeveden sonra eş susarsa ve BLE
  oturumu açık kalırsa (iOS arka plan), `RunBleTransferAsync` süresiz bekliyor.
- **Düzeltme:** `Post` içinde `_lastFrameTicks` güncellenmeli. Bekçi döngüde (ör. 5 sn'de bir) bakmalı:
  `now - last > 30 s` olursa `s.Fail(BleNoResponse)` çağrılmalı.

### 13. `HandleSessionAsync` içinde try dışındaki disk G/Ç'si oturumu "Yakında" listesinde asılı bırakıyor
- **Dosya:** `TransferCoordinator.cs:289-319` (try `322`'de başlıyor)
- **Sorun:** Oturum `_sessions`'a eklendikten sonra `_o.Trusted.NoteSeen` (DPAPI ve atomik dosya yazımı) ile `PeersChanged`
  işleyicisi try/finally dışında çalışıyor. `IOException` alınırsa (ör. AV/OneDrive dosyayı kilitledi) `finally` hiç
  çalışmıyor. Oturum `_sessions`'ta kalıyor, kanalı ise `SignalSessionAcceptor` tarafından kapatılıyor. Cihaz "Yakında"
  görünüyor, gönderim `ObjectDisposedException` ile başarısız oluyor.
- **Düzeltme:** `NoteSeen` ve `PeersChanged` try bloğunun içine alınmalı ya da `NoteSeen` kendi try/catch'iyle sarılıp hata
  günlüğe yazılmalı.

---

## Düşük

### 14. Yinelenen `transferId` denetimi ile ekleme ayrı kilitlerde; `AddLocked` sessizce üzerine yazıyor
- **Dosya:** `TransferCoordinator.cs:761-768`, `786-805`, `1449`
- **Sorun:** İki farklı oturum aynı kimlikle teklif gönderirse (aynı telefonun uygulaması ve paylaşım uzantısı ya da kötü niyetli
  bir eş) ikinci teklif birinci durumun üzerine yazılıyor. Bu durumda `Pending.Register` "zaten kayıtlı" hatası veriyor.
  Birinci eşin `cancel`/`accept` iletileri de `Owned` içindeki `PeerId` denetimi yüzünden yok sayılıyor.
- **Düzeltme:** Kilit içinde `if (_states.ContainsKey(id)) rejectReason = "duplicate"` denetlenmeli. `AddLocked` içinde
  `TryAdd` kullanılmalı.

### 15. `HotspotManager.StartAsync` içinden çıkan `AggregateException` yakalanmıyor
- **Dosya:** `HotspotManager.cs:131-132`, `TransferCoordinator.cs:957-960`, `1006-1010`, `1033-1034`
- **Sorun:** "Başlatılamadı ve eski ayar geri yüklenemedi" durumu `AggregateException` fırlatıyor. Ana yolda bu
  `TransferFailedUnexpected` olarak görünüyor (kullanıcı asıl nedeni görmüyor). Ek yol görevinde (`Task.Run`) ise süzgeçte
  olmadığı için gözlenmeyen istisna olarak kayboluyor, günlüğe bile yazılmıyor.
- **Düzeltme:** `HotspotException(UserError.Of(HotspotRestoreFailed), new AggregateException(...))` fırlatılmalı ya da
  `SendHotspotPathAsync` `AggregateException`'ı da yakalamalı.

### 16. Sınırsız büyüyen sözlükler ve kaldırılmayan olay abonelikleri
- **Dosya:** `TransferCoordinator.cs:182-183`, `302-309`; `BleSignalServer.cs:200-203`
- **Sorun:** `_lastPeerByBle` ile `_identityChanges` hiç budanmıyor. Her yeni BLE kimliği bir kayıt ekliyor. iPhone'un rastgele
  adresi dönerse bunlar uzun süre çalışan tepsi uygulamasında birikir (Windows `GattSession.DeviceId` davranışı doğrulanmadı).
  `SessionStatusChanged` aboneliği de hiç kaldırılmıyor ve `ClientConn` nesnelerini canlı tutuyor.
- **Düzeltme:** Kayıtlar `IdentityFlapWindow`'dan eskiyse budanmalı (ya da üst sınır 256). Abonelik `conn.Run` biterken
  kaldırılmalı.

### 17. Kullanıcıya ham/İngilizce/teknik metinler gidiyor (UserError)
- **Dosya:** `TransferCoordinator.cs:750` (`RejectedByPeerReason` + ham `busy`/`declined`/`no_space`), `1277` (`tcp_fail`),
  `1307` (`AbortException` İngilizce metni: "manifest total … does not match offer"), `1397` (`ex.Message`), `1196`, `1209`,
  `599` (`UserError.Raw`); `HotspotManager.cs:12` (`Raw`)
- **Sorun:** Arayüz dilinden bağımsız olarak ham neden belirteçleri ve .NET/OS iletileri gösteriliyor.
- **Düzeltme:** Bilinen ret ve iptal nedenleri koda eşlenmeli (`declined`→`RejectedByPeer`, `busy`, `rate_limited`, `silenced`,
  `no_space`, `bad_count`, `protocol error`). `ex.Message` yalnız günlüğe gitmeli. Kullanıcıya genel bir kod ve
  "ayrıntı günlükte" ifadesi gösterilmeli.

### 18. Klasör gönderiminde OneDrive yer tutucuları (reparse point) sessizce atlanıyor
- **Dosya:** `FolderScanner.cs:78-82`
- **Sorun:** `HasFlag(FileAttributes.ReparsePoint)`, symlink/junction dışındaki bulut dosyası yer tutucularını da kapsıyor.
  OneDrive'a yedeklenen Masaüstü ya da Belgeler klasörü gönderilince dosyalar eksik gidiyor ve kullanıcı bunu fark etmiyor
  (yalnız günlüğe yazılıyor). Cihazda doğrulanmadı.
- **Düzeltme:** Yalnız `entry.LinkTarget is not null` (symlink/junction) olanlar atlanmalı. Atlanan dosya sayısı kullanıcıya
  bildirilmeli.

### 19. `Send` klasör taramasını eşzamanlı yapıyor (arayüz iş parçacığı)
- **Dosya:** `TransferCoordinator.cs:569`, `577-590`; `RealServices.cs:105`; `FolderScanner.cs:93`
- **Sorun:** Tarama her dosyayı açıp kapatıyor. 10 000 dosyalı bir klasörde arayüz donar.
- **Düzeltme:** `SendAsync` içinde `await Task.Run(() => _coord.Send(...))` kullanılmalı ya da `CollectFiles` eşzamansız
  yapılmalı.

### 20. Başarısız/iptal aktarımın kısmi dosyaları 24 saate kadar diskte kalıyor; temizlik ilk hatada duruyor
- **Dosya:** `TransferCoordinator.cs:1405-1416` (temizlik yok), `Storage.cs:214-241`, `WinDropRuntime.cs:79-93`
- **Sorun:** Son duruma geçmiş aktarımın kaydı kaldırılıyor. Böylece aynı `transferId` ile devam etmek artık mümkün değil
  (`DataListener` "tanınmayan transferId" diyor). Yine de `.part`/`.done` dosyaları 24 saatlik temizliğe kadar kalıyor.
  Bunlar hassas içerikli kısmi dosyalar olabilir. `PurgeStaleTransfers` içinde kilitli bir dizinde fırlayan istisna bütün
  döngüyü durduruyor.
- **Düzeltme:** Alıcı rolünde `Failed`/`Canceled` sonucunda `RunGuardedAsync.finally` içinde
  `s.Store?.DeleteTransferAsync(...)` çağrılmalı (en iyi çabayla). Temizlikte her dizin kendi try/catch'iyle ele alınmalı.

### 21. Aşırı karmaşıklık ve yineleme (sadeleştirme)
- **Dosya:** `TransferCoordinator.cs:1544-1591` (yol durumu için 8+ bayrak: `HotspotRequested`, `HotspotSent`,
  `HotspotClaimed`, `AddrClaimed`, `DualPath`, `ConnectionActive`, `EverConnected`, `Ble`), `539-546` ile `1242-1249`
  (aynı sonuç işleme), `515-516`, `1169-1170`, `1407` (kayıt kapatma üç yerde), `1272` (ölü yorum yolu),
  `PairingService.cs:170` (`TryConsume` sonrası gereksiz `End`).
- **Sorun:** 1-5 numaralı bulguların ortak kökü bu bayrakların kilitsiz ve dağınık güncellenmesi.
- **Öneri:** `PathState` adında küçük bir sınıf yazılmalı: `enum Stage { Offer, Direct, Hotspot, Dual, Ble, Relay, Done }`,
  yol başına `Sent/Failed` bilgisi ve `s.Gate` altında `TryTransition(from, to)`. Sonuç işleme tek bir
  `CompleteFromChannel(State)` yardımcısına alınmalı.

---

## İyileştirme önerileri (değeri yüksek, riski düşük)
1. `Finish()` içinde son durum yazıldıktan sonra `s.Cts.Cancel()` çağrılmalı ve `s.Registration` kapatılmalı. Böylece son
   duruma geçmiş aktarımın bütün yan görevleri (ek yol, etkin nokta başlatma) kendiliğinden durur (4 ve 10'u büyük ölçüde
   kapatır).
2. `TransferTimeouts.DataIdle` (ör. 60 sn) eklenmeli. `DataListener`'da TCP keepalive açılmalı (1 ve 2).
3. `OnDataConnectionAsync`'e genel bir `catch (Exception)` eklenmeli, `DataDone.TrySetException` ile sonuçlanmalı (5). Tek
   satırlık bir değişiklik, takılmaların tamamını görünür kılar.
4. Eksik testler eklenmeli: (a) çift yolda `tcp_fail` sonrası etkin noktadan bağlanma; (b) commit sırasında
   `UnauthorizedAccessException`; (c) `Active` aktarımda `DropPeer`; (d) WinDrop etkin noktası elle kapatılınca yeniden
   başlatma ve yedeğin korunması; (e) aktarma onayı beklenirken TCP bağlantısı gelmesi.
5. Ret/iptal neden belirteçleri tek bir `PeerReason → UserErrorCode` tablosuna alınmalı (17). Bu, iOS ile ortak belirteç
   listesinin PROTOCOL §4'e yazılmasını da kolaylaştırır.
6. Kaynak dosyalar `FileShare.ReadWrite | FileShare.Delete` ile açılmalı, `Send` ise arayüz iş parçacığı dışında taranmalı
   (6, 19).
