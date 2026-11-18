# INTEROP — C# ↔ Swift çapraz uyum denemesi

Tarih: 2026-10-17. Amaç: Windows (C#) ve iOS (Swift) uygulamalarının **birbiriyle** gerçekten çalıştığını
kanıtlamak (o zamana dek her taraf yalnız kendi kendisiyle test edilmişti). Gerçek TCP, gerçek Noise el sıkışması,
gerçek `FileSender`/`FileReceiver`; iki ayrı süreç, iki ayrı dil.

## Araçlar
- **Swift:** `ios/WinDropKit` içinde `windrop-interop` yürütülebilir hedefi (`Sources/windrop-interop/main.swift`; Kit koduna
  dokunmaz, yalnız `Package.swift`'e hedef eklendi). Her zaman bağlanan + Noise başlatan taraftır (iPhone rolü).
  - `send --host H --port P --prefix-transfer-id <b64url> --pattern ik|xx --my-key <hex32> --peer-pub <hex32> [--kill-at-bytes N] <dosyalar...>`
  - `receive-from ... --out DIR --count N --total BYTES` (C# → Swift yönü; bağlantıyı yine Swift kurar)
  - `pubkey --my-key`, `signals --out F`, `signals-verify --file F`
- **C#:** `windows/tools/WinDrop.Interop` (net10.0). Gerçek `DataListener` (Platform'dan kaynak bağlantısıyla; Platform projesi
  WinRT yüzünden yalnız Windows'ta derlenir), `PendingTransferTable`, `FileReceiver`/`FileSender`, `Sas`.
  - `serve --port P --my-key --peer-pub --transfer-id --pattern ik|xx [--mode receive|send] [--out DIR] [--count N --total B] [--expect-completed N] [--timeout S] [dosyalar]`
  - `pubkey`, `signals`, `signals-verify`
- **Betik:** `windows/tools/WinDrop.Interop/run-interop.sh [signals a b c d e]` — derler, senaryoları koşar, `RESULT <ad> PASS|FAIL` yazar.

## Nasıl çalıştırılır
Windows güvenlik duvarı, WSL2 (NAT) sanal arayüzünden gelen bağlantıyı engelliyor: WSL'den Windows ana makine IP'sine
(`172.21.96.1`) bağlanma zaman aşımına uğradı (kural **eklenmedi**). Bu yüzden iki taraf da SwiftBox içinde, `127.0.0.1` üzerinde çalışır:
SwiftBox'a yalnız orada .NET 10 SDK kuruldu (`dotnet-install.sh --channel 10.0 --install-dir /root/.dotnet`).

```
wsl -d SwiftBox -u root -- bash -lc "bash /mnt/c/Users/Efe/Desktop/WinDrop/windows/tools/WinDrop.Interop/run-interop.sh"
```
(Git Bash'ten çağırırsan `MSYS_NO_PATHCONV=1`.) `NOBUILD=1` derlemeyi atlar. Swift çıktısı `/root/swift-build`, .NET çıktısı
`/root/artifacts` altındadır (Windows `obj/` klasörleriyle çakışmasın diye). Sabit **test** anahtarları betikte (`11..`, `22..`, `33..`); gerçek anahtar değildir.

## Sonuçlar (gerçek çıktılar; özet)
| Senaryo | Sonuç |
|---|---|
| SAS vektörü (`protocol/vectors/sas.json`, "2599") | C# `Sas.Compute` aynı özeti (`ba8ee416…1db4`) ve aynı kodu **2599** üretti; test `SasVectorTests` |
| signals: 11 sinyal türü (`pairInfo` dahil) iki yönde çözme + yeniden kodlama | PASS (Swift satırları C# ile, C# satırları Swift ile) |
| (a) IK, Swift→C#, 3 dosya (29 B, 3 MB, 25 MB) | PASS; Swift `OUTCOME=completed`, C# `Completed`, 3 SHA-256 eşleşti |
| (b) XX, Swift→C#, aynı 3 dosya | PASS; SHA-256 eşleşti; SAS iki tarafta aynı (`8140`) ve el sıkışma özeti aynı |
| (c) C#→Swift 2 dosya, IK ve XX | PASS (2×2 SHA-256 eşleşti); XX'te SAS aynı (`0635`) |
| (d1) yanlış transferId | Swift `connectionClosed`; C# günlüğü: "tanınmayan transferId, kapatıldı"; işleyici çağrılmadı |
| (d2) doğru transferId, yabancı anahtar | Swift `connectionClosed`; C#: "güvenilmeyen/yanlış eş anahtarı, kapatıldı" |
| (d3) BLE oturumu IK iken TCP'de XX | el sıkışma bitti, C# "el sıkışma deseni XX, BLE oturumu IK; kapatıldı" (§2), Swift `connectionLost` |
| (e) 200 MB, Swift 66,9 MB'ta `SIGKILL` (çıkış 137), yeniden başlat | C# `SESSION_LOST=ConnectionLostException`; 2. oturumda `RESUME file=1 offset=63360000` (son ACK, 4 MB katı); SHA-256 doğru (`6ee6235e…a04b`) |

Test paketleri: `dotnet test windows\WinDrop.slnx` — Core 260/260, Platform 100/100 (Platform'da `WifiNone_StartsHotspot…` bir kez Swift
testleriyle paralel koşarken düştü, tek başına ve sonraki tam koşuda geçti: CPU yüküne duyarlı, bu işle ilgisiz görünüyor); SwiftBox `swift test` 77/77.

## Bulunan uyumsuzluklar ve düzeltmeler
1. **C# Noise `Split()` Linux'ta çöküyordu** (`windows/src/WinDrop.Core/Noise/SymmetricState.cs`). BCL `HKDF.DeriveKey` boş IKM ile Windows'ta çalışır,
   Linux/OpenSSL 3'te `CryptographicException` atar. Protokol hatası değil, taşınabilirlik hatası (üretim yalnız Windows); ama çapraz denemeyi
   engelledi. Düzeltme: Noise §4.3 HKDF'i `HMACSHA256` ile elle yapmak (temp = HMAC(ck, ""), out1 = HMAC(temp, 0x01), out2 = HMAC(temp, out1‖0x02)).
   cacophony vektörleri (59) ve tüm Core testleri geçmeye devam ediyor.
2. **Swift `SignalMessage`'ta `pairInfo` yoktu** (PROTOCOL §5/§3.2; `PairingController` `.pairInfo` kullanıyordu). Eklendi: `PairInfo {ssid, pass}`,
   `case pairInfo(PairInfo)`, `t: "pairInfo"` (`Sources/WinDropKit/Protocol/SignalMessages.swift`); gidiş-dönüş testi `ProtocolTests` listesinde; C# testi
   `SignalMessagesTests`'e eklendi. C# ile JSON uyumu yukarıdaki `signals` senaryosunda doğrulandı.
3. SAS: **uyuşmazlık yok.** İki taraf da PROTOCOL §3.1/§3.1'ya uyuyor (ilk 4 bayt big-endian uint32 mod 10000, 4 hane sıfır dolgulu).
4. Başka kodlama/akış uyumsuzluğu bulunmadı: ön ek, desen ayrımı (IK 96 / XX 32 bayt), IK boş onay mesajı, TCP çerçeveleme, MANIFEST/RESUME/ACK/DONE,
   64000 B DATA, tam dosya SHA-256, devam ofseti iki yönde uyumlu.

Küçük test yardımcıları: `Core` projesine `InternalsVisibleTo WinDrop.Platform.Tests` eklendi (sabit ephemeral ile SAS vektörü için).

## Sınırlar (doğrulanmadı)
- BLE taşıması çapraz denenmedi (yalnız TCP). Gerçek iPhone/Windows ağı, güvenlik duvarı kuralı, etkin nokta denenmedi.
- Her iki taraf aynı makinede, localhost; gecikme/kayıp yok. Swift tarafı Linux (`swift-crypto`, POSIX soket); Apple CryptoKit/Network.framework yolu denenmedi.
- Swift aracı yalnız bağlanan taraftır; Swift dinleyici/C# bağlanan yönü PROTOCOL gereği yoktur.
- Windows'ta (yerel) aynı araç yalnız derlendi (`dotnet build`); çapraz çalıştırma WSL içinde yapıldı.

## Aktarma sunucusu (relay) yolu — PROTOCOL §7.4
Aynı iki araç, doğrudan TCP yerine aktarma odası üzerinden de çalışır. iPhone başlatan (Noise initiator, `role=phone`, ön ek taşıma=3), PC yanıtlayan (`role=pc`).
Oda kimliği token'dan türetilir (`RelayRoom.ComputeRoomId` / `RelayRoom.roomId`); sunucu yalnız şifreli baytları iletir.

**Ortak bağımsız değişken adları (iki araçta aynı):** `--relay-url`, `--token`, `--transfer-id`, `--pattern ik|xx`, `--my-key <hex32>`, `--peer-pub <hex32>`.
- `--relay-url`: `wss://host[:port]`; `ws://` yalnız loopback (yerel `wrangler dev`) için kabul edilir.
- `--token`: 43 karakter base64url (32 bayt) veya 64 onaltılık karakter; iki araçta da aynı ayrıştırma.
- `--transfer-id`: 22 karakter base64url (16 bayt). Swift aracında eski ad `--prefix-transfer-id` de kabul edilir (`run-interop.sh` kullanır).
- `--mode`: **araç kendi açısından** veri yönünü seçer. C# `relay-pc`: `receive` (varsayılan) veya `send`; Swift `relay-phone`: `send` veya `receive` (eski `receive-from` de kabul).
  Beklenen eşleşme: telefon `send` ↔ PC `receive`; PC `send` ↔ telefon `receive`.
- Alma tarafında `--out DIR --count N --total BYTES` (teklif bilgisi); `--timeout SN` yalnız C#'ta (odada eşi bekleme süresi, varsayılan 60).

```
# C# (PC, yanıtlayan): odaya girince "RELAY_READY room=<id> ..." yazar
dotnet WinDrop.Interop.dll relay-pc --relay-url ws://127.0.0.1:8787 --token <43> --my-key <hex32> --peer-pub <telefon pub> \
    --transfer-id <22> --pattern ik --mode receive --out DIR --count 1 --total BYTES [--timeout 90] [dosyalar...]
# Swift (iPhone, başlatan)
windrop-interop relay-phone --relay-url ws://127.0.0.1:8787 --token <43> --my-key <hex32> --peer-pub <PC pub> \
    --transfer-id <22> --pattern ik --mode send dosya...
```
Çıktı satırları `serve`/`send` ile aynıdır (`HANDSHAKE=ok ... sas= hash=`, `FILE ...`, `OUTCOME=`); Swift ayrıca `CONNECT=relay ...`, hata durumunda `RELAY_HTTP_STATUS=<kod>`.

**Betik:** `windows/tools/WinDrop.Interop/run-relay-interop.sh [r1 r2 r3]` (macOS/Linux, bash 3.2 uyumlu; sunucunun `RELAY_URL`'de çalıştığını, `SW` ve `CS_DLL` yollarını bekler):
| Senaryo | İçerik |
|---|---|
| `relay-r1` | IK, Swift phone → C# pc, 5 MB; SHA-256, `OUTCOME`, SAS ve el sıkışma özeti iki tarafta aynı |
| `relay-r2` | XX, C# pc → Swift phone, 2 MB; aynı denetimler |
| `relay-r3` | Aynı odaya pc + phone bağlıyken üçüncü bağlantı (her iki rol) HTTP 409 ile reddedilir (curl) |

Her senaryo `RESULT relay-<ad> PASS|FAIL` yazar; biri FAIL ise betik 1 ile çıkar.
**CI:** `.github/workflows/interop.yml` (macos-latest): `npm ci` + `wrangler dev --local --port 8787` (arka planda, `/healthz` beklenir), .NET 10, `swift build --product windrop-interop`, betik.
Swift `relay-phone` Linux'ta çalışmaz (swift-corelibs-foundation `URLSessionWebSocketTask` libcurl WebSocket desteği ister; araç bunu `HINT=` satırıyla bildirir), bu yüzden gerçek çapraz deneme macOS'ta yapılır.
