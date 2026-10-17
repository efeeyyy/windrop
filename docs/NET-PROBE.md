# NetProbe — doğrudan IP erişim ve hız testi

Amaç: eduroam gibi ağlarda (client isolation şüphesi) iPhone'un aynı Wi-Fi'daki PC'ye
doğrudan IP ile ulaşıp ulaşamadığını, ulaşabiliyorsa hızını ölçmek. iPhone'a uygulama gerekmez; Safari yeter.

## Ne ölçer
- Erişilebilirlik: sayfa telefonda açılıyorsa doğrudan TCP erişimi var.
- İndirme (PC→telefon) ve yükleme (telefon→PC) MB/s. Yüklemede sunucu tarafı süre de ölçülür.

## Çalıştırma
```
dotnet run --project windows\src\WinDrop.NetProbe -- [--port 53999] [--duration SANIYE]
```
Açılışta arayüz adresleri, URL'ler ve birincil IPv4 için QR kod yazılır. Ctrl+C veya `--duration` ile kapanır.
Yönetici yetkisi gerekmez. Güvenlik duvarı izin sorarsa **Özel ağlar** için İzin ver'e basın
(eduroam profili Private ise bu yeterlidir). Yalnız `/`, `/down?mb=N`, `/up` vardır (en fazla 200 MB); diske yazılmaz.

## iPhone adımları
1. iPhone ve PC aynı eduroam ağında olsun (Wi-Fi açık, mobil veri fark etmez).
2. Kamera ile konsoldaki QR'ı okuyun veya Safari'ye `http://<PC-IPv4>:53999/` yazın.
3. Sayfada "Bağlantı başarılı" görünür; "İndirme testi" ve "Yükleme testi" düğmelerine basın, özet satırını not edin.
4. PC konsolunda `===== TELEFON BAĞLANDI: <ip> =====` satırı ve istek logları görünmelidir.

## Yorum
- Sayfa açılıyor ve hız makul: doğrudan IP yolu çalışıyor; BLE ile adres değişimi + doğrudan TCP mümkün.
- Sayfa hiç açılmıyor: izolasyon **veya** PC güvenlik duvarı. Ayırmak için:
  - [ ] Güvenlik duvarı izni "Özel ağlar" için verildi mi? (Ağ profili Public ise Public'e de izin gerekir.)
  - [ ] PC'de `http://localhost:53999` açılıyor mu? (Açılmıyorsa program sorunu.)
  - [ ] PC'nin kendi IP'si (`http://<PC-IPv4>:53999`) PC'den açılıyor mu?
  - [ ] Başka bir cihaz (örn. ikinci laptop) aynı ağdan açabiliyor mu? Açabiliyorsa güvenlik duvarı değil;
        telefon özelinde sorun. Hiçbir cihaz açamıyorsa büyük olasılıkla client isolation.
  - [ ] Konsolda hiç `TELEFON BAĞLANDI` yoksa istek PC'ye ulaşmıyor demektir.
- IPv6 global adres listede varsa `http://[adres]:53999/` ile de deneyin; link-local (fe80::) adresler tarayıcıdan genelde işe yaramaz.
