# AP-PROBE: Wi-Fi Direct erişim noktası + eduroam aynı anda

## Ne test eder
PC bir Wi-Fi ağına (eduroam) bağlıyken `WiFiDirectAdvertisementPublisher` + `LegacySettings`
ile "legacy" soft AP (telefonun normal Wi-Fi gibi katılabildiği SSID/parola) açılabiliyor mu,
bu sırada mevcut Wi-Fi/internet kopuyor mu. Mimari bağlam: `ARCHITECTURE.md` > "Ortak ağ yoksa
taşıma sırası", 2. yol.

## Çalıştırma
```
dotnet run --project windows\src\WinDrop.ApProbe -- --duration 60
```
- `--duration N` (sn; verilmezse Ctrl+C'ye kadar), `--ssid ad` (isteğe bağlı). Ctrl+C `publisher.Stop()` ile temiz kapanır.
- Başlangıçta SSID, parola ve QR metni (`WIFI:T:WPA;S:ssid;P:parola;;`) yazılır. Parola her çalıştırmada rastgeledir.
- Her 5 sn: AP IPv4 (genelde `192.168.137.1`, "Yerel Ağ Bağlantısı*"), `netsh wlan show interfaces`
  durumu/SSID'si ve 1.1.1.1:443 TCP erişimi (2 sn zaman aşımı) tek satırda yazılır.
- Yönetici yetkisi, güvenlik duvarı ya da sistem ayarı değişikliği gerekmez.

## Telefon adımları (hız testi)
1. ApProbe çalışırken telefonda Wi-Fi ayarlarından yazılan SSID'ye parola ile katıl (internetsiz uyarısı normal).
2. Ayrı terminalde NetProbe'u çalıştır (`[::]:53999` dinler, AP arayüzünde de erişilebilir olmalı).
3. Telefonda Safari: `http://<AP IPv4>:53999/` (örn. `http://192.168.137.1:53999/`) ve hız testini yap.
4. Güvenlik duvarı kuralı "WinDrop test 53999" yalnız Private profilde. AP arayüzü hangi profilde
   görünüyor: `Get-NetConnectionProfile` (ApProbe çalışırken). Sayfa açılmazsa ilk şüpheli bu kural.

## Bulgular (2026-10-16, Realtek RTL8852BE)
- Soft AP eduroam bağlıyken başladı: `Status=Started, Error=Success`; AP `192.168.137.1`.
- 25 sn boyunca eduroam `connected`, internet (1.1.1.1:443) sürekli erişilebilir; Stop sonrası da bağlı.
- `Get-NetConnectionProfile` çalışırken yalnız `itu.edu.tr / Wi-Fi / Private` gösterdi; AP arayüzü
  için profil kaydı yok. Duvar kuralının AP üzerinde geçerli olup olmadığı telefonla doğrulanmadı.
- Telefondan bağlantı henüz denenmedi (gerçek istemci, hız ve kural doğrulaması bekliyor).
