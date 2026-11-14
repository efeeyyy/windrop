# WinDrop Windows uygulaması incelemesi

Tarih: 2026-11-13. Kapsam: `windows\src\WinDrop.App`, `install\*.ps1`, `build\*.ps1`, `docs\INSTALL.md`
(+ okunması gereken yerlerde `WinDrop.Platform\AppSettings.cs`, `ShellIntegration.cs`).
Kaynak dosya değiştirilmedi. Etiketler: **[D]** = çalıştırarak doğrulandı, **[K]** = yalnız kod okuma
(çalıştırılmadı), **[?]** = belirsiz. Önem: Yüksek / Orta / Düşük.

Yöntem notu: Uygulama, `bin\Debug` çıktısının kopyasından (sahte servisler, `WINDROP_INSTANCE=review`) çalıştırıldı;
Efe'nin gerçek WinDrop'u (PID 10844, `%LOCALAPPDATA%\Programs\WinDrop`) çalışırken dokunulmadı. Kurulum betikleri
yalnız `-DryRun` + geçici `-InstallRoot` ile denendi. Sonda tüm inceleme süreçleri kapatıldı, `settings.review.json` silindi.

## 1. Kod incelemesi

### 1.1 Kullanıcıya hata gösterimi / dayanıklılık

1. **Orta** — `App.xaml.cs:89-93` (`StartRuntimeAsync`). Platform servisleri (BLE, TCP 53600, etkin nokta) başlatılamazsa
   (port dolu, Bluetooth kapalı/yok) yalnız günlüğe yazılır; pencere normal açılır ve "Yakında cihaz yok" der. Kullanıcı nedenini bilemez. [K]
   Öneri: `MainViewModel`e kalıcı bir "durum" satırı (Bluetooth kapalı / port kullanımda / servis başlamadı) ve "Yeniden dene".
2. **Orta** — `App.xaml.cs:38-42`. `DispatcherUnhandledException` her hatayı yutuyor (`Handled = true`) ve yalnız logluyor; komut
   (ör. `SendAsync`) sessizce başarısız olur. `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` hiç dinlenmiyor;
   `Program.Main` try/catch'siz → başlangıç hatası sessiz çökme. [K]
   Öneri: iş parçacığı dışı hatalar için de log; kullanıcıya görünür bir "Beklenmeyen hata — günlüğü aç" bandı.
3. **Orta** — `MainViewModel.cs:173-186`. `StagedPaths.Clear()` gönderimden ÖNCE yapılıyor ve `catch` yalnız
   IO/InvalidOperation/UnauthorizedAccess yakalıyor. Hata olursa seçim kaybolur (yeniden seçmek gerekir); başka istisna türleri
   kullanıcıya hiç gösterilmez. [K] Öneri: temizlemeyi başarıdan sonra yap (ya da hata durumunda geri koy), `catch (Exception)` + `Main.SendError`.
4. **Orta** — `PairingWindow.xaml.cs:17` + `PairingViewModel.cs:47-56`. `Loaded += async` içinde `StartAsync` try/catch'siz; eşleştirme
   başlatılamazsa (BLE/ağ) pencere boş QR + "iPhone bekleniyor" ile sonsuza kadar kalır. [K]
5. **Orta** — `Platform\AppSettings.cs:55-60`. `settings.json` UTF-8 **BOM**'lu ise (Not Defteri, PowerShell 5.1 `Set-Content -Encoding UTF8`)
   `JsonSerializer.Deserialize` `JsonException` verir, hata sessizce yutulur ve varsayılanlar yüklenir; sonraki herhangi bir `Save()`
   (ör. tepsi ipucu) bozuk dosyanın üzerine varsayılanları yazar → dil, indirme klasörü, relay adresi kaybolur. [D] (bu incelemede
   BOM'lu `settings.review.json` dil/klasör değerlerini kaybetti ve app tarafından varsayılanlarla yeniden yazıldı.)
   Öneri: BOM'u kırp (`JsonSerializer` öncesi), bozuk dosyayı `.bad` olarak yedekle, günlüğe yaz.
6. **Düşük** — `MainViewModel.cs:52-61` (`async void UpdateThumbnail`) + `Thumbnails.cs:49-52`. Dış `catch` yalnız IO/UnauthorizedAccess/Argument
   yakalıyor; `ShellIcon` içindeki `COMException`/`ExternalException` hatalı Task'ı önbelleğe koyar ve `async void` üzerinden dispatcher'a sızar. [K]

### 1.2 İş parçacığı / MVVM

7. **Düşük (doğrulanmadı)** — `RealServices.cs:13,83`. `SynchronizationContext.Current ?? new SynchronizationContext()` — UI bağlamı yoksa
   `Post` thread-pool'a düşer ve `ObservableCollection` UI dışı iş parçacığında güncellenir. Şu an servisler `OnStartup` (dispatcher) içinde
   kurulduğu için büyük olasılıkla `DispatcherSynchronizationContext` var; gerçek servislerle denenmedi (Efe'nin çalışan örneği ile port çakışırdı). [?]
   Öneri: `Dispatcher` yakala (`Application.Current.Dispatcher`) ya da bağlam yoksa fırlat.
8. **Düşük** — `MainViewModel.cs:163-169`. `Transfers` koleksiyonunda her değişimde TÜM satırlar yeniden oluşturuluyor (`Detach`+yeni `TransferRow`);
   satır başı `OnPropertyChanged(string.Empty)` (`:91`) ilerleme olayı başına tüm bağları ve `Strings.Format`'ı yeniden hesaplar. Küçük listede sorun değil,
   ama yeni aktarım gelince animasyon/odak sıfırlanır. `RefreshDevices` (`:152-161`) de her `PeersChanged`'de listeyi boşaltıp dolduruyor
   (BLE sık tetikler → seçim/kaydırma titremesi riski). [K]
9. **Orta** — `SettingsViewModel.cs:37`. `_devices.DevicesChanged += lambda` hiç çıkarılmıyor; Ayarlar her açılışında VM (ve `RefreshDevices`
   çağrısı) servise bağlı kalır → sızıntı + kapalı pencerenin koleksiyonuna yazma. `PairingViewModel` doğru yapıyor (`Dispose`'da abonelik çıkar), Ayarlar yapmıyor. [K]
   Öneri: `SettingsViewModel : IDisposable`, `Window.Closed`'da çağır.
10. **Düşük** — `SingleInstance.cs:52-77`. Boru sunucusu `maxInstances=1`, okuma zaman aşımı yok: boruya bağlanıp kapanmayan bir istemci
    (veya bozuk ikinci örnek) döngüyü tıkar; sonraki `Forward` 3 sn sonra `false` döner (kullanıcı için "uygulama açılmıyor"). [K]
    Öneri: `CancelAfter(2 s)` ile okuma, satır sayısı/uzunluk sınırı.

### 1.3 Pencere yaşam döngüsü (tek örnek, tepsi, --hidden, --send)

11. **Yüksek (güvenlik/UX)** — `IncomingOfferWindow.xaml:6,15`. `Topmost + ShowActivated` ve **Kabul et `IsDefault="True"`**: kullanıcı başka
    pencerede yazarken teklif odağı alır, o anda basılan Enter/Boşluk teklifi KABUL eder (bilinmeyen cihaz dahil). [K] (pencere odağı çalarken
    görüntüsü doğrulandı, Enter basışı denenmedi.) Öneri: varsayılan düğmeyi kaldır ya da Reddet yap; ilk ~1,5 sn düğmeleri devre dışı; `ShowActivated=False` + tepsi balonu.
12. **Orta** — `App.xaml.cs:187-194`, `IncomingOfferWindow.xaml.cs:43-48`. Birden fazla teklif/aktarma onayı aynı köşeye (`WorkArea.Bottom-16`)
    bire bir üst üste biner; alttaki görünmez, 30 sn sonra sessizce reddedilir. [K] Öneri: açık pencere sayısına göre yukarı kaydır veya tek kuyruk penceresi.
13. **Orta** — `MainWindow.xaml.cs:23-32`. `OnClosing` her zaman `Cancel=true`; Windows oturumu kapanırken (`SessionEnding`) pencere kapanmayı
    engelleyip "bu uygulama kapanmayı engelliyor" ekranı çıkarabilir. `App`'de `SessionEnding` işleyicisi yok (`AllowClose=true` yapılmalı). [K, doğrulanmadı]
14. **Düşük** — `Program.cs:18`, `SingleInstance.cs:94`, `App.xaml.cs:95-103`. İkinci örnek `--hidden` ile açılsa bile `SHOW` iletilir ve
    ana pencere öne gelir (ör. oturum açılışında Run anahtarı çalışırken uygulama zaten açıksa). [D: ikinci örnek SHOW → pencere geri geldi; `--hidden` ikinci örnek denenmedi]
    Ayrıca `Forward` öncesi `AllowSetForegroundWindow` çağrılmıyor; `Activate()` arka plandan yalnız görev çubuğunda yanıp sönebilir. [K]
15. **Düşük** — `App.xaml.cs:124-141`. Çıkış, devam eden aktarım için onay istemiyor; `OnExit` içinde `Wait(8 sn)` UI iş parçacığını bloklar
    (pencereler donuk kalır). Yeniden başlatmada (`Program.cs:26`) 8 sn aşılırsa yeni süreç port 53600'ü eski süreçten alamayabilir. [K]
16. **Düşük** — `App.xaml.cs:147,157`. Açık Eşleştirme/Ayarlar penceresi simge durumundaysa `Activate()` geri getirmez (`WindowState` normale çekilmiyor). [K]
17. Olumlu [D]: ana pencere X → süreç yaşıyor, pencere gizleniyor; `--hidden` ile pencere açılmıyor; teklif penceresi X ile kapatılınca
    `Closed→Answer(false)→Close()` hata vermiyor (günlükte yalnız "teklif reddedildi"); ikinci örnek çıkış kodu 0 ve pencereyi geri getiriyor; `--send` dosyaları hazırlıyor.

### 1.4 Ayarlar, dil, kayıt

18. **Orta** — `SettingsViewModel.cs:64`. `RestartNeeded = SelectedLanguage != Normalize(_settings.Language)`. "Kaydet" (yeniden başlatmadan) sonrası
    `_settings.Language` yeni değere eşitlenir; Ayarlar yeniden açılınca uyarı kaybolur ama arayüz hâlâ eski dilde. Çalışan dil ayrı tutulmalı
    (ör. `AppLanguage.Active`). [K]
19. **Orta** — `SettingsViewModel.cs:116-137`. Canlı `_settings` nesnesi `Save()` başarılı olmadan değiştiriliyor; kayıt hatasında bellekteki ayarlar
    (indirme klasörü, otomatik kabul, relay) yine de etkin kalır ama diske yazılmamıştır. Doğrulama yok: `RelayUrl` (hata ancak gönderimde,
    `Err.RelayUrlInvalidHint`), `DownloadFolder` (olmayan/geçersiz yol), `AutoAcceptMaxMb` (`Math.Max(1,x)*1024*1024` taşabilir; geçersiz metin için
    ekranda hata yok). [K]
20. **Düşük** — `SettingsWindow.xaml:22`. `AutoAcceptMaxMb` `UpdateSourceTrigger=LostFocus` + `Kaydet` `IsDefault`: kutuda yazarken Enter'a basılırsa
    değer işlenmeden kaydedilebilir (WPF'te bilinen durum). [K, denenmedi] Öneri: `PropertyChanged`.
21. **Orta** — `SettingsWindow.xaml:5`. `SizeToContent=Height` + kaydırma yok: 2 cihazla pencere 710 dip yüksek (125% ölçekte 888 px, 864 dip ekranda
    zaten sığmaya yakın [D]); ~4-5 eşli cihazdan sonra Kaydet/Vazgeç ekran dışına taşar. Cihaz listesini `ScrollViewer` (MaxHeight) içine al, düğmeleri altta sabitle.
22. **Düşük** — `ShellIntegration.cs:114`. `IsAutoStartEnabled` Run değerini **büyük/küçük harf duyarlı** `==` ile karşılaştırıyor; yol harf farkı
    (ör. kurucu `Resolve-Path` vs. `Environment.ProcessPath`) ya da taşınmış kurulum → onay kutusu yanlışlıkla "kapalı". `OrdinalIgnoreCase` kullan. Ayrıca
    Görev Yöneticisi'nde (StartupApproved) devre dışı bırakılmış girişi "açık" gösterir. [K]
23. **Orta (deney güvenliği)** — `ShellIntegration` `WINDROP_INSTANCE`'tan etkilenmiyor: ayrı örnekte Ayarlar→Kaydet gerçek SendTo kısayolunu ve
    HKCU Run anahtarını (review exe'sine) değiştirirdi. Günlük de ortak. Bu incelemede Kaydet/Yeniden başlat'a hiç basılmadı. [K]

### 1.5 Yerelleştirme

24. **Düşük** — `ShellIntegration.cs:75` ve `install.ps1:66`: SendTo kısayol açıklaması `"WinDrop ile gönder"` sabit Türkçe (EN'de de Türkçe; kısayol özelliklerinde görünür). [K]
25. **Orta** — `SettingsViewModel.cs:90,98`: `MessageBox` düğmeleri (Evet/Hayır) ve `OpenFileDialog/OpenFolderDialog` (`App.xaml.cs:168,174`) **işletim sistemi** diliyle
    çıkar, uygulama diliyle değil (uygulama EN, Windows TR iken karışık dil). Özel onay penceresi ya da `TaskDialog` ile uygulama dili. [K]
26. **Düşük** — `Strings.resx:104-105` (her iki dilde): "Uygulamayı yeniden başlatınca uygulanır / Applies after restart" iki dilli sabit metin; EN arayüzde de Türkçe görünür (kasıtlı olabilir). [D]
27. **Düşük** — Dilbilgisi: TR `Transfer.To`/`Transfer.From` "{0} cihazına/cihazından" → "Efe'nin iPhone'u cihazına" (tamlama çift); EN "file(s)" çoğul kalıbı
    (`Main.StagedFormat`, `Transfer.Detail`, `Offer.Text`). `Err.*` metinlerinde düz `'`, diğerlerinde `’` karışık. `Offer.Timeout` anahtarı aktarma onayında da "reddedilir" der
    ama düğme "İptal" (`RelayApprovalWindow.xaml.cs:43`). [D]
28. **Düşük** — `AppLanguage.cs:30`: "Sistem" dili `CultureInfo.InstalledUICulture` ile çözülüyor (Windows'un KURULU dili); kullanıcının seçili görüntüleme dili değil (`CurrentUICulture`). [K]
29. Kontrol [D]: TR/EN resx 162/162 anahtar, eksik anahtar yok.

### 1.6 Küçük resim önbelleği

30. **Orta** — `Thumbnails.cs:16-18`. `Task<ImageSource?>` yol başına **sonsuza kadar** önbellekte; sonuç `null` ise de (dosya henüz yazılmadı / geçici hata)
    kalıcı. Alıcı satırında `SavedPath`/`FirstFilePath` dosya tamamlanmadan istenirse küçük resim oturum boyunca gelmez; ayrıca dosya değişse de eski görüntü kalır.
    Boyut: 96×96 BGRA ≈ 37 KB × aktarım sayısı (1000 aktarım ≈ 37 MB) — sınırsız büyüme. [K] Öneri: `null`/hatalı sonucu önbellekten düşür, LRU (örn. 200 öğe).
31. Olumlu [D]: küçük resim 3 dosyalı aktarımda ilk dosyanın görseliyle geliyor (png), dondurulmuş (Freeze), `OnLoad` ile akış bırakılıyor.

### 1.7 Erişilebilirlik

32. **Orta** [D, UIA ağacı]: `AutomationProperties` hiçbir XAML'de yok.
    - Cihaz listesi öğeleri ekran okuyucuya `WinDrop.App.ViewModels.DeviceViewModel` olarak görünüyor (`MainWindow.xaml:43-60`, `ToString` yok);
      Ayarlar listesinde `DeviceInfo { Id = fake-1, Name = …, IsNearby = True … }` kayıt dökümü okunuyor (`SettingsWindow.xaml:44-62`).
    - Ayarlar'daki 3 `TextBox` (indirme klasörü, otomatik kabul MB, relay adresi) ve `ComboBox` adsız (`LabeledBy` yok).
    - Aktarım satırı düğmeleri hepsi "İptal"/"Klasörde göster" (hangi aktarım?), `ProgressBar` adsız/yüzdesiz (`MainWindow.xaml:110`); satırlar odaklanamaz (ItemsControl).
    - Tüm "Kaldır" düğmeleri aynı adı taşır. Klavye kısayolu/erişim tuşu (`_`) yok; sürükle-bırak yalnız klavye dışı yol (Dosya seç düğmesi var, iyi).
    Öneri: `AutomationProperties.Name` (`{Binding Name}`), `LabeledBy`, `ProgressBar` için `AutomationProperties.Name`+`HelpText`, satırlara birleşik ad.
33. **Düşük** — Kontrast: `Foreground="OrangeRed"` hata metni (`MainWindow.xaml:134`, `SettingsWindow.xaml:66`) açık temada beyaz üzerinde ≈3,5:1 (AA'nın altı); koyu temada iyi. `Opacity="0.7"` 12 px yardımcı metin koyu temada sınırda. [K; koyu tema [D]]
34. **Düşük** — `MainWindow.xaml.cs:38-47`: sürükleme sonrası kenarlık `Brushes.Gray`/`HighlightBrush`'a sabitleniyor; XAML'deki `#80808080` tema uyumlu kenarlık bir daha geri gelmiyor (ilk sürüklemeden sonra kenarlık farklı). Tema renk kaynağı kullan. [K]
35. **Düşük** — `TrayIcon.cs`: WinForms `ContextMenuStrip` Windows 11 koyu temasında açık/eski görünüm; tek tık açmaz (yalnız çift tık); simge boyutu DPI değişimine uyarlanmaz. [K]

### 1.8 Bildirim eksikleri

36. **Orta** — Gizli/tepsideyken gelen dosya tamamlanınca bildirim yok: `ShowBalloon` yalnız ilk "arka planda çalışıyor" ipucunda kullanılıyor (`App.xaml.cs:72`).
    Alıcı "Klasörde göster"e ancak pencereyi açıp ulaşır. [K]
37. **Orta** — `IncomingOfferWindow.xaml.cs:25`. `offer.Preview` dosya ADLARINI da taşıyor (`TransferCoordinator.BuildPreview`) ama pencere yalnız klasörleri
    ("/" ile bitenler) gösteriyor; kullanıcı neyi kabul ettiğini görmez (özellikle bilinmeyen gönderici). Temizlenmiş ilk 3-5 dosya adı + "+N" gösterilmeli. [K]

## 2. Görsel bulgular

Görüntüler `C:\Users\Efe\Desktop\WinDrop\docs\review\shots\` altında (TR ve EN; Windows koyu tema, %125 ölçek, PerMonitorV2; ≤700 px).
Sahte servis verisi kullanıldı (cihaz adları Türkçe sabit — yalnız fake).

| Pencere | TR | EN |
|---|---|---|
| Ana — dosya hazırlandı | `tr-main-staged.png` | `en-main-staged.png` |
| Ana — aktarım sürüyor | `tr-main-active.png` | `en-main-active.png` |
| Ana — tamamlandı | `tr-main-done.png` | `en-main-done.png` |
| Ayarlar | `tr-settings.png` | `en-settings.png` |
| Eşleştirme (QR / onay) | `tr-pairing.png`, `tr-pairing-confirm.png` | `en-pairing.png`, `en-pairing-confirm.png` |
| Gelen teklif | `tr-offer.png` | `en-offer.png` |
| Aktarma (internet) onayı | `tr-relay.png` | `en-relay.png` (not: sahte modda tetik yok; ayrı bir deney harness'ı ile aynı pencere sınıfı gösterildi) |

V1. **Taşma yok**: TR/EN metinleri tüm pencerelerde sığıyor; Ayarlar'da uzun indirme yolu kesiliyor (tooltip/yol sonu yok) — `*-settings.png`.
V2. **Hazırlanan dosya listesi yok** (`*-main-staged.png`): yalnız "3 öğe gönderilmeye hazır"; ne seçildiği görünmüyor, tek tek çıkarma yok.
    Bırakma alanı ile Gönder/Temizle aynı kutuda; Gönder birincil vurgulu değil (hepsi aynı gri düğme).
V3. **Aktarım satırı** (`*-main-active.png`, `*-main-done.png`): dosya adı hiç yok ("Efe'nin iPhone'u cihazına · 3 dosya · 1 MB"); yüzde sayısı yok (yalnız ince çubuk);
    gönderilen/alınan iki satırın küçük resmi aynı görünürse ayırt edilmez (yön ikonu yok); tarih/saat yok; satırı kaldırma/temizleme yok. Satır düzeni (48 px küçük resim, başlık/ayrıntı/durum, sağda tek düğme) hizalı ve tutarlı.
V4. **Seçili cihaz satırı** (`*-main-*.png`): düz mavi dolgu çok baskın; "Yakında" rozeti (yeşilimsi) mavi üstünde zayıf kontrast. Cihaz listesi ile "Aktarımlar" arasındaki boşluklar tutarlı (16 dip), ama pencerenin alt yarısı büyük boş alan.
V5. **Boş durum**: "Henüz aktarım yok." tek satır gri metin, yönlendirme yok (yalnız metin); cihaz yokken metin uzun ve iPhone'a özel.
V6. **Ayarlar** (`*-settings.png`): bölüm başlıkları tutarlı, ama aralıklar eşit değil (14/8/4 dip karışık; "Dil / Language" altı ile onay kutuları arası dar); gruplama çizgisi/başlık yok; otomatik kabul kapalıyken alt kutu soluk (iyi).
    Eşli cihaz kutusunda "Son görülme: bilinmiyor" sahte veri; liste yüksekliği sınırsız (bkz. 21).
V7. **Eşleştirme** (`*-pairing-confirm.png`): durum metni onay istenirken hâlâ "iPhone bekleniyor…" ve geri sayım sürüyor (çelişkili); onay kutusu iyi hizalı, QR beyaz zemin iyi. Eşleştir/Reddet eşit ağırlıkta (birincil yok).
V8. **Gelen teklif** (`*-offer.png`): sağ altta küçük, net; geri sayım gri 12 px; Kabul/Reddet'te birincil vurgu yok; dosya adı yok (bkz. 37).
V9. **Aktarma onayı** (`*-relay.png`): metin "(48 MB)" cümle ortasında parantez; "59 sn içinde yanıt verilmezse reddedilir" ↔ düğme "İptal" tutarsız.
V10. **Pencere kromu**: Ayarlar/Eşleştirme/Teklif pencerelerinde yalnız Kapat düğmesi var (kasıtlı); ana pencerede Küçült/Büyüt var ama `MinWidth=440` ile sığıyor.

## 3. Kurulum betikleri

Çalıştırılan denemeler (PowerShell 5.1.26100): `install.ps1 -DryRun -AutoStart -SkipFirewall -InstallRoot <scratch>` (başarılı: kopyalama + yalnız yazdırma);
SourceDir yok → `throw`, çıkış kodu 1, InstallRoot oluşmadı (iyi); çalışan örnek + DryRun; `uninstall.ps1 -DryRun -RemoveData -InstallRoot <scratch>`.

38. **Yüksek** — `install.ps1:36-41`, `uninstall.ps1:23-28`. Çalışan örneği `CloseMainWindow()` ile kapatma, uygulama X'te **tepsiye gizlendiği** için işe yaramaz
    (pencere kapanır, süreç yaşar; gizliyken `MainWindowHandle` 0 → `false`). 4 sn sonra `Kill()` çalışır: her güncelleme/kaldırmada sert sonlandırma
    (BLE reklamı/port temiz bırakılmaz, tepside hayalet simge kalabilir) ve `Kill` sonrası `WaitForExit` yok → hemen ardından `Copy-Item`/`Remove-Item` dosya kilidine
    çarpabilir. [D: gizleme + DryRun denemesinde süreç sonlandırıldı] Öneri: uygulamaya `--quit` komutu (boru `QUIT`) ekle; betik önce onu çağırsın, sonra `Wait`, en son `Kill`+`Wait`.
39. **Orta** — `install.ps1:36-41`: bu kapatma **`-DryRun`'ı hiç dikkate almaz**; DryRun denemesinde çalışan örnek kapatıldı ve dosyalar kopyalandı (belgede "kopyalama olur" yazıyor, ama
    kapatma yazmıyor). [D]
40. **Orta** — `install.ps1:44`. Kopyalama kısmi/atomik değil: `Copy-Item … -Force` ile eski sürümün üstüne yazılır, kaldırılan/yeniden adlandırılan DLL'ler **artık kalır**;
    kilit yüzünden yarıda hata olursa (ErrorActionPreference=Stop) karışık sürümlü kurulum kalır. Öneri: `InstallRoot.new`'e kopyala → eski klasörü `.old`'a taşı → yeniden adlandır; ya da önce sürüm klasörünü temizle (kullanıcı verisi ayrı olduğu için güvenli).
41. **Orta** — `uninstall.ps1:30-37,39-49`. `-InstallRoot` verilse bile **gerçek** Başlat/SendTo kısayollarını, Run anahtarını ve güvenlik duvarı kurallarını siler (DryRun çıktısında doğrulandı) — test kurulumunu kaldırmak gerçek kurulumun kayıtlarını siler. Yalnız hedefi bu `InstallRoot`'u gösteren kısayol/Run değerini sil.
42. **Orta** — `install.ps1:89-91`, `uninstall.ps1:47`. UAC iptal edilirse `Start-Process -Verb RunAs` sonlandırıcı hata verir (`ErrorActionPreference=Stop`):
    `install`'da "Tamam" yazılmadan kesilir; `uninstall`'da **kurulum klasörü silinmeden** durur (yarım kaldırma). `uninstall`'da yükseltilmiş sürecin çıkış kodu da denetlenmiyor. try/catch + anlaşılır uyarı + devam.
43. **Düşük** — `-like "$InstallRoot*"` (`install.ps1:37`, `uninstall.ps1:24`): joker karakterli yol (`[`) ve ön ek eşleşmesi (`...\WinDrop2`) yanlış sonuç verir; `StartsWith(…, OrdinalIgnoreCase)` + ayırıcı. `.Path` erişim hatası (yükseltilmiş süreç) `Stop` altında betiği keser.
44. **Düşük** — `install.ps1:66-76`. `-NoSendTo` mevcut kısayolu kaldırmaz; `-AutoStart` verilmezse eski Run değeri (eski `InstallRoot`'a işaret edebilir) olduğu gibi kalır; kurulum bayrakları `settings.json`'a yansımaz. Güncelleme senaryosunda `InstallRoot` değişirse Run/Başlat kısayolu eski yolu gösterebilir (SendTo/Başlat her seferinde yeniden yazılıyor, Run yazılmıyor).
45. **Düşük** — `firewall.ps1:30`. "WinDrop data" kuralı yalnız **Private** profil; Windows 11'de yeni/tanınmayan ağlar çoğu zaman Public → gelen TCP 53600 engellenir, doğrudan yol sessizce başarısız olur (yalnız hata metni "yol yok"). Kurulum ağın profilini kontrol edip uyarabilir; uygulama "Doğrudan bağlantı engellenmiş olabilir" ipucu gösterebilir. Kural programa değil porta bağlı (belgeli), iyi.
46. **Düşük** — `install.ps1:12`, `uninstall.ps1:9` varsayılan `LOCALAPPDATA\Programs\WinDrop`; "Uygulamalar ve özellikler"de kayıt yok (kaldırma yalnız betikle). `install.ps1` kurulu klasöre kopyalanmıyor (güncelleme için zip gerekir).
47. **Düşük** — PS 5.1 uyumu: `install/uninstall/firewall/publish` BOM'lu (Türkçe doğru). `install\remove-test-rules.ps1` ve `build\make-ico.ps1` **BOM'suz + Türkçe karakterli** → PS 5.1'de ANSI okunur, çıktı bozuk görünür (işlev etkilenmez). `Start-Process powershell` PS 7'de de çalışır. `New-Object -ComObject WScript.Shell` ve `Get-NetFirewallRule` 5.1'de sorunsuz.
48. **Düşük** — `build\publish.ps1:17-20`. `Remove-Item $out,$zip,$stage` publish'ten ÖNCE; derleme başarısız olursa önceki `dist` kaybolur. `Compress-Archive` (5.1) zip girişlerinde ters eğik çizgi kullanır ve 2 GB sınırı var (şimdi 77,6 MB zip / 199 MB, 484 dosya: sorun yok). Sürüm yalnız `-p:Version` ile geçer, uygulamada "Hakkında/sürüm" görünmez; hash/imza üretilmez; `dist\install.log` ve `dist\setup` önceki deneme artığı.
49. **Yüksek (belge)** — `docs\INSTALL.md:11-14`. `.\install.ps1` doğrudan yazılıyor; varsayılan ilke (Restricted) ve internetten indirilen zip'in MOTW'u nedeniyle betik **çalışmaz**
    (bu makinede kabukta "running scripts is disabled" görüldü). `powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1` veya `Unblock-File` yönergesi gerekli.
50. **Orta (belge)** — `INSTALL.md` eksikleri: asgari işletim sistemi (TFM `net10.0-windows10.0.22621.0` ⇒ **Windows 11 22H2+**; Windows 10'da açılmaz), imzasız exe için SmartScreen uyarısı,
    güncelleme adımı ("yeniden çalıştır"; çalışan uygulama kapatılır), Bluetooth/güvenlik duvarı ön koşulları, günlük konumu (`%LOCALAPPDATA%\WinDrop\logs\app.log`), `-Port` parametresinin uygulamada karşılığı olmadığı (53600 sabit mi?). [? port sabitliği doğrulanmadı]

## 4. En fazla 6 kullanıcı deneyimi iyileştirmesi

1. **Güvenli teklif penceresi**: Enter ile kazara kabulü kaldır (varsayılan düğme yok, kısa kilit), dosya adlarından ilk birkaçını göster, birden çok teklifi üst üste değil kuyrukta/yığında göster.
2. **Durum şeridi + bildirimler**: ana pencerede Bluetooth/servis/güvenlik duvarı durumu ("Bluetooth kapalı", "Gelen bağlantı engelli — düzelt"); tepside olduğunda aktarım bitince Windows bildirimi ("3 dosya alındı — Klasörde göster").
3. **Zengin aktarım satırı**: ilk dosya adı + "+N", yüzde, yön ikonu, saat; "Temizle", başarısızda "Tekrar dene"; hazırlanan dosyaları listele, tek tek çıkar, Gönder'i birincil (accent) düğme yap, tüm pencereyi bırakma alanı yap.
4. **Erişilebilirlik turu**: `AutomationProperties` (cihaz, kutular, ilerleme, bağlamlı Kaldır/İptal), `DeviceViewModel.ToString`, odaklanabilir satırlar, açık temada AA kontrastlı hata rengi.
5. **Ayarlar sağlamlaştırma**: kaydırılabilir cihaz listesi, doğrulama (klasör var mı/oluştur, relay biçimi, MB sayısı) satır içi hata mesajıyla, "Yeniden başlat" uyarısını gerçek çalışan dile göre göster, uygulama dilinde onay iletişim kutuları, bozuk ayar dosyasında uyarı + yedek.
6. **Güncelleme/kurulum cilası**: `--quit` ile zarif kapatma, `.new→swap` ile atomik güncelleme, "Uygulamalar ve özellikler" kaydı, INSTALL.md'ye ExecutionPolicy/SmartScreen/Windows 11 notları, `-DryRun`'ın hiçbir şeyi (kapatma, kopyalama) değiştirmemesi.
