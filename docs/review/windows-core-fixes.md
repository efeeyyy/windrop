# Windows çekirdek düzeltmeleri — sonuç (2026-11-13)

Kapsam: `WinDrop.Core`, `WinDrop.Platform`, `windows/tests`. App'e dokunulmadı. Yüksek bulguların hepsi, DECISIONS 1-8 ve Orta/Düşüklerin çoğu yapıldı; kalanlar aşağıda "ertelendi".

Doğrulama: `dotnet build windows\WinDrop.slnx` 0 uyarı/0 hata; `dotnet test windows\WinDrop.slnx` (Platform 211, Core 340) geçti
(tekrarlar ve interop sonucu rapor mesajında).

## Bulgu → durum

| # | Durum | Not / dosya |
|---|-------|-------------|
| 1 Boşta zaman aşımı + keepalive | düzeltildi | `TransferTimeouts.DataIdle` (30 sn) + `WaitForDataAsync` (TransferCoordinator.cs); TCP keepalive 15/5/3 `DataListener.EnableKeepAlive`. Test: `ActiveDataSession_WithoutProgress_FailsWithIdleTimeout` |
| 2 Çift yolda ölü bağlantı reddi | düzeltildi | `KeepExisting`: koruma yalnız ilk 10 sn (`ActiveSinceTicks`), sonra `DualPath=false` |
| 3 Tek `*_fail` bütün beklemeyi bitiriyor | düzeltildi | `AddrFailed/HotspotFailed` + `EvaluatePathFailed`; yalnız gönderilmiş tüm yollar başarısızsa geçiş. Testler: `DualPath_TcpFail_KeepsWaitingForHotspot_ThenCompletes`, `DualPath_BothFail_FallsBack` |
| 4 Yedek yola geçişte kapanmama | düzeltildi | `ClosePaths` (RunPathAsync catch), `IsWaitingForPath` (+PathClosed/PathFailed), `Finish` Cts iptal + kayıt kapatma, `SendHotspotPathAsync` gate altında yeniden denetim, `TransferStarted/Finished` dengesi gate altında, parola yenileme `HotspotSent` (gönderim başlayınca, gate altında). Test: `CanceledWhileHotspotStarting_...` |
| 5 Dar istisna süzgeci | düzeltildi | `OnDataConnectionAsync` catch-all → `s.Fail` (yerel IO/yetki: `TransferFailedWith`, diğer: `TransferFailedUnexpected`). Test: `CommitUnauthorizedAccess_FailsTransfer_...` |
| 6 Protokol/yerel IO "bağlantı koptu" | kısmen | `ProtocolException` artık hemen `TransferCutWith "protocol"` ile biter (yeniden bağlanma beklenmez); kaynak dosya `FileShare.ReadWrite|Delete` ile açılır (`Storage.cs`). **Ertelendi:** `LocalIoException` türü (kaynak/disk IO'sunu ağ kopmasından ayırma; kaynak dosya kısalması hâlâ "kopma" sayılır) |
| 7 Eski alıcı bitmeden yenisi | düzeltildi | `State.ActiveRun` + `OnDataConnectionAsync` başında en çok 5 sn bekleme |
| 8 HotspotManager yedek ezme | düzeltildi, **testsiz** | `HotspotManager.cs` (`_owned` iken `LoadBackup()` kullanılır). WinRT API'si sahtelenemediği için birim testi yok |
| 9 Parola yenileme / yeniden başlatma | kısmen, **testsiz** | `IHotspotController.CurrentConfig` + `PassphraseRotated` (varsayılan arayüz üyeleri; `StartAsync` imzası değişmedi). Başka aktarım sürerken yeniden başlatma yapılmaz, `hotspot` iletisi gerçekte geçerli ayarı taşır; yenilemeden sonra boştaysa etkin nokta hemen durdurulur. WinRT nedeniyle test yok |
| 10 Eşleştirme kaldırma | düzeltildi | `DropPeer` → `FailAllOf` (Connecting/Active/push bekleyen), `NotifyPushWaiters` yalnız `IsKnown`. Test: `DropPeer_FailsActiveTransfer`. **Ertelendi:** `DataListener.samePeer` güvenilen listeye bakmıyor (kayıt kapatıldığı için pratik açık kapandı) |
| 11 hello.name kaydı değiştiriyor | düzeltildi | Güncelleme kaldırıldı (PROTOCOL §0 korunur). Test `DuplicateIdentityTests` tersine çevrildi. Ad uzunluğu sabitinin birleştirilmesi ertelendi (`UpdateName` artık çağrılmıyor) |
| 12 BLE sessizlik bekçisi yalnız ilk çerçeve | düzeltildi | `TimeSpan BleIdle` (30 sn) döngü + `BleDataChannel.TakeFrameSeenSinceLastCheck` |
| 13 HandleSessionAsync try dışı IO | düzeltildi | `NoteSeen` kendi try/catch'inde (günlüğe yazılır) |
| 14 Yinelenen transferId | düzeltildi, testsiz | `OnOffer` ikinci kilitte `_states.ContainsKey` → `duplicate` reddi (yarış testle üretilemedi) |
| 15 AggregateException | düzeltildi | `HotspotManager` artık `HotspotException(HotspotStartFailed, Aggregate)` fırlatır |
| 16 Sınırsız sözlükler | kısmen | `_lastPeerByBle`/`_identityChanges` 256'yı aşınca budanır (`PruneBleTablesLocked`). **Ertelendi:** `BleSignalServer` `SessionStatusChanged` aboneliğini kaldırma |
| 17 Ham/İngilizce metinler | **ertelendi** (yeni `UserErrorCode` resx gerektirir; App dosyalarına dokunulmadı) |
| 18 OneDrive yer tutucuları | düzeltildi (kısmen) | `FolderScanner`: yalnız `LinkTarget != null` atlanır. Atlanan sayısını kullanıcıya bildirme ertelendi. Cihazda doğrulanmadı |
| 19 Arayüzde eşzamanlı tarama | **ertelendi** | `Task.Run` sarması App'te (`RealServices.cs`) yapılmalı; App'e dokunulmadı |
| 20 Kısmi dosya temizliği | kısmen | Başarısız/iptal alıcı aktarımda `RunGuardedAsync.finally` içinde `DeleteTransferAsync`. `PurgeStaleTransfers` dizin başına try/catch (yapıldı) |
| 21 Sadeleştirme (`PathState`) | **ertelendi** (kapsamlı refaktör; yalnız bayrak eklendi) |

## DECISIONS (C#)

1. Sinyal toleransı: yapıldı. `wifi` C#'ta zaten isteğe bağlı. Yeni `SignalDecodeException : ProtocolException` (bilinmeyen/eksik `t`, bozuk JSON, eksik alan, geçersiz değer); `HandleSessionAsync` bunu yakalayıp mesajı atar ve oturumu sürdürür. Test: `UnknownSignalType_AndBrokenJson_AreDropped_SessionSurvives`, `Decode_UnknownTypeOrBadMessage_ThrowsSignalDecodeException`. Not: bilinmeyen `wifi` değeri de artık yalnız o mesajı atar.
2. WDU: yapıldı (`BleResync.UnknownMarker`, `IBleLink.SendUnknownAsync`, `SignalSessionAcceptor`, `BleSignalServer`). Yalnız `PeerNotTrustedException` (IK ilk mesaj) WDU gönderir; WDR gönderilmez. Test: `Ik_WithUnknownKey_SendsWdu_NotWdr`.
3. Süreler: Approval 60 sn, Accept 80 sn (`DefaultTimeouts_FollowDecisions`).
4. Yol anlamı: yapıldı (#3).
5. Yedek yol temizliği + Finish'te iptal: yapıldı (#4).
6. Zaman aşımları: BLE idle 30, TCP idle 30 + keepalive, hotspot katılma 120: yapıldı. BLE el sıkışması 10 sn zaten vardı (`HandshakeTimeout`, doğrulanmadı). **Ertelendi:** "onaylı BLE yazması 5 sn", "yol bilgisi bekleme 30 sn" (iOS tarafı ağırlıklı).
7. BLE DATA/MANIFEST sınırı: `FileSender` zaten `min(MaxBody, kanal.MaxFrameBodySize)` kullanıyor (BLE 2048); değişiklik gerekmedi.
8. Parola yenileme: yapıldı (#4). Test: mevcut `HotspotRotationTests` geçiyor.

## protocol-consistency.md (C#): hiçbiri yapılmadı → ertelendi (U1 `status` toleransı C# tarafı zaten `wifi` isteğe bağlı; U7, U8, U9, U16, U17, U20)

## Değişen dosyalar
- `windows/src/WinDrop.Platform/TransferCoordinator.cs`, `DataListener.cs`, `HotspotManager.cs`, `BleDataChannel.cs`, `BleSignalServer.cs`, `SignalSessionAcceptor.cs`
- `windows/src/WinDrop.Core/Session/Transports.cs`, `Session/SecureChannel.cs` (`SendPlaintextAsync` internal, test için), `Protocol/SignalMessages.cs`, `Transfer/Storage.cs`, `Transfer/FolderScanner.cs`
- `windows/tests/WinDrop.Platform.Tests/CoreFixTests.cs` (yeni, 8 test), `Support.cs` (FakeHotspot.StartGate, Harness commitHook, BleIdle=3 sn), `DuplicateIdentityTests.cs` (ad testi tersine); `WinDrop.Core.Tests/Protocol/SignalMessagesTests.cs` (ThrowsAny + yeni test)
