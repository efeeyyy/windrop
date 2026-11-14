# İnceleme sonrası kararlar (yönetici, 2026-11-13)

İki tarafı (Windows, iOS) bağlayan kararlar. Çelişki halinde bu dosya, inceleme raporlarındaki
önerilerin önündedir. PROTOCOL.md düzeltme turundan sonra buna göre yeniden düzenlenecek.

1. **Sinyal JSON toleransı:** `hello`/`status` içinde `wifi` PC tarafından gönderilmez; alıcı için
   **isteğe bağlıdır** (iOS zorunlu saymaz). Genel kural: spesifikasyonda "zorunlu" yazmayan her alan
   çözümde isteğe bağlıdır; bilinmeyen alan yok sayılır; bilinmeyen `t` oturumu DÜŞÜRMEZ, yok sayılır
   ve günlüğe yazılır (ileri uyumluluk). Çözme hatası tek mesajı atar, oturumu düşürmez.
2. **IK reddi işareti:** PC, IK ilk mesajındaki statik anahtar güvenilen listede yoksa bağlantıyı
   kapatmadan önce `p2c` ile tek bildirim olarak 3 bayt ASCII **`WDU`** (unknown) gönderir. iOS eşleşme/
   tanıdık kaydını **yalnız `WDU` alınca** siler ve XX ile yeniden dener. Başka hiçbir IK hatası
   (kopma, zaman aşımı, WDR, sıra hatası) kaydı silmez.
3. **Teklif onay süreleri:** PC onay penceresi 60 sn (zaman aşımı = ret). iPhone: 20 sn yanıt yoksa
   oturumu tazeler (yalnız oturum yenilendiyse teklifi bir kez yeniden gönderir), toplam 80 sn sonra
   "yanıt vermedi". iPhone onay sayfası (PC→iPhone) 60 sn; PC gönderen 80 sn bekler.
4. **`path` anlamı:** `tcp_fail` yalnız `addr` yolunu, `hotspot_fail` yalnız etkin nokta yolunu
   kapatır. İki yol da açıkken biri başarısız olursa PC diğerini beklemeye devam eder; **tümü**
   başarısız olunca sonraki yola (BLE küçük dosya → aktarma) geçer. iOS her yol için sonucunu ayrı bildirir.
5. **Yedek yola geçiş:** BLE/aktarma yoluna geçerken açık yol beklemeleri kapatılır (bekleyen tablo
   kaydı silinir); aktarım bittiğinde (`Finish`) o aktarımın iptal belirteci her durumda iptal edilir.
6. **Zaman aşımları (iki taraf):** BLE el sıkışması 10 sn; onaylı BLE yazması 5 sn; kabulden sonra
   yol bilgisi (addr/hotspot/bleData/relay) bekleme 30 sn; TCP veri oturumunda boşta kalma 30 sn
   (TCP keepalive açık); BLE veri oturumunda 30 sn; etkin noktaya katılma bekleme 120 sn.
   Aşımda aktarım anlaşılır, yerelleştirilmiş hatayla biter; hiçbir satır süresiz "bekleniyor" kalmaz.
7. **BLE DATA parçası:** ≤ 2048 bayt, ~20 KB/s. MANIFEST BLE yolunda da ≤ 8176 baytlık Noise
   mesajına sığacak şekilde sayfalanır.
8. **Etkin nokta parolası:** `hotspot` mesajı eşli olmayan (tanıdık dahil değil: yalnız *bilinmeyen*)
   eşe gittiyse aktarım hangi sonuçla biterse bitsin (iptal, hata, zaman aşımı dahil) parola yenilenir.
9. **Push ortamı:** iOS `env` = derleme yetkisine göre: `aps-environment` development → `sandbox`.
   Derleme zamanı bayrağı (`PUSH_SANDBOX`) ile; varsayılan Release-Push = sandbox (Sideloadly/geliştirici imzası).
10. **Uzantı / App Group:** App Group konteyneri çalışma zamanında yoksa uzantı ayrı kimlikle,
    yalnız açıkken bağlanır (mevcut geri dönüş). Ana uygulama askıdayken Darwin sinyali ulaşmayabilir:
    uzantı, hedef PC'de "başka oturum" yüzünden düşerse 1 kez yeniden dener — PC zaten aynı BLE
    kimliğinden gelen yeni oturumu tutuyor.
