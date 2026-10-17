# BLE ölçüm aracı (Adım 0)

`windows/src/WinDrop.BleProbe` — Windows'u BLE GATT çevre birimi yapan konsol uygulaması.
Ürün protokolü değildir; amaç: iPhone ile bağlantı, MTU ve iki yönde hız ölçmek
(bkz. `ARCHITECTURE.md`, "Açık sorular").

## UUID'ler (kod: `ProbeUuids.cs`)

| Ad | UUID | Özellik |
|---|---|---|
| Servis (varsayılan) | `351fca71-0000-1000-8000-00805f9b34fb` (32 bit: `0x351FCA71`) | |
| Servis (`--uuid128`) | `351fca71-398a-4d79-b872-bcb6f4101984` | |
| `info` | `d66fb783-a725-47aa-8e14-c00f66435c36` | Read; UTF-8 JSON `{"name":..., "probe":"0.1"}` |
| `rx` | `c4204721-2c6b-4c83-9c8c-c4985b560f40` | Write + WriteWithoutResponse; gelen baytlar sayılır |
| `tx` | `a19b2217-bc01-4b50-9b4f-77ea776974cf` | Notify + Read |
| `ctrl` | `8daa0d14-5554-4b2a-8e35-525cbf9e73b7` | Write; metin komutlar: `stream <KB>`, `reset` |

`stream <KB> <KBps>`: opsiyonel hedef hız (örn. `stream 1024 40`); verilmezse parçalar olabildiğince hızlı gönderilir.

`--sweep <liste>` (örn. `--sweep 20,50,100`): tx'e ilk abone gelince 5 sn sonra her hız için sırayla
`stream` çalıştırır (aşama başına `--sweep-kb <KB>`, varsayılan 512); aşamalar arası 10 sn ara, sonunda `TARAMA BİTTİ`.
Log'da `===== AŞAMA i/n: ... =====` satırlarını izle. Ctrl+C / `--duration` taramayı durdurur.

### Bilinen sorun: 128 bit servis UUID'si reklamı
Bu makinede (Realtek RTL8852BE) `GattServiceProvider` 128 bit özel servis UUID'siyle
`AdvertisementStatus = Aborted` veriyor (tekrarlanabilir; UUID'den bağımsız, Nordic UART UUID'si dahil).
Bluetooth taban UUID'sine gömülü 16/32 bit biçimde `Started` oluyor. Bu yüzden varsayılan servis
UUID'si rastgele 32 bit değerin (`351FCA71`) taban UUID'ye gömülmüş hâli. Nedeni doğrulanmadı
(reklam yükü boyutu veya sürücü şüphesi). Karakteristik UUID'leri 128 bit kalır, sorun çıkarmaz.
İleride ürün için 128 bit gerekirse alternatifler araştırılmalı (sürücü güncelleme, başka adapter).

## Çalıştırma

```
dotnet run --project windows/src/WinDrop.BleProbe -- --check      # adapter yetenekleri
dotnet run --project windows/src/WinDrop.BleProbe                 # sunucu; Ctrl+C ile çık
dotnet run --project windows/src/WinDrop.BleProbe -- --duration 30   # 30 sn sonra kendiliğinden kapan
dotnet run --project windows/src/WinDrop.BleProbe -- --uuid128    # 128 bit servis UUID'si dene
```

Bluetooth radyosu açık olmalı. Yönetici yetkisi gerekmez. Başlangıçta log'da önce `Aborted`,
hemen ardından `Started` görmek normaldir; asıl bakılacak son durum `Started`.

## iPhone'da test (nRF Connect, App Store)

1. nRF Connect'i aç, Scanner'da tara. Bilgisayar adıyla (veya adsız, servis `0x351FCA71`)
   bir cihaz bulunur; yoksa filtreyi kapat, RSSI ile bul.
2. **CONNECT**. PC log'unda `Yeni oturum` satırı çıkmalı. Bağlantı olup olmadığını not et.
3. Servisi aç. `info`'ya Read (↓ oku simgesi): JSON gelmeli.
4. `tx`'e abone ol (üç ok simgesi, Notify). PC log'unda `tx abone sayısı: 1` ve
   `MaxNotificationSize` / `MaxPduSize` görünür.
5. `ctrl`'e Write (UTF-8 / Text) olarak `stream 256` yaz. PC `stream bitti` satırında
   süre ve KB/s yazar (PC → iPhone). iPhone'da gelen bildirimleri say/süreyi gözle.
6. `rx`'e veri yaz: nRF Connect'te Write seçeneğinden hex baytlar gir; mümkünse uzun veri ve
   "Write without response" ile tekrar tekrar gönder (veya Macros). PC her saniye `rx: ... KB/s`
   yazar (iPhone → PC). Yazma sırasında `İlk rx yazması: MaxPduSize=...` satırına bak.
7. `ctrl`'e `reset` yazarak sayaçları sıfırla; testi tekrarla.
8. Telefonu birkaç metre uzaklaştır / ekranı kilitle; kopma olup olmadığını izle.

## Not edilecekler

- MTU / `MaxPduSize` (PC log'u) ve nRF Connect'in gösterdiği MTU.
- PC → iPhone KB/s (`stream` sonucu), başarısız bildirim sayısı.
- iPhone → PC KB/s (`rx` satırları), With/Without response ayrı.
- Beklenmedik kopma, `Oturum durumu: Closed`, `BluetoothError` değerleri.

## Sonuçlar

| Tarih | Cihaz (iPhone/iOS) | MaxPduSize | PC→iPhone KB/s | iPhone→PC KB/s (resp / no-resp) | Kopma | Not |
|---|---|---|---|---|---|---|
| | | | | | | |
| | | | | | | |
