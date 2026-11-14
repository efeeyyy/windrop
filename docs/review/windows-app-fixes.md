# Windows uygulaması / kurulum düzeltmeleri — sonuç (2026-11-15)

Kapsam: `windows\src\WinDrop.App`, `install\*.ps1`, `build\*.ps1`, `docs\INSTALL.md` (+ `Platform\AppSettings.cs`'te BOM/bozuk dosya düzeltmesi).
Kaynak: `docs\review\windows-app.md` (50 numaralı bulgu + V1-V10 görsel notlar). Çelişkide `DECISIONS.md` önceliklidir (madde 3: PC onay penceresi 60 sn, zaman aşımı = ret).
Bu belge, önceki ajanın yarıda kalan işinin gözden geçirilip tamamlanmasının sonucudur; bulgu durumları kod okunarak ve derleme/test ile doğrulandı.

Doğrulama: `dotnet build windows\WinDrop.slnx` 0 uyarı / 0 hata; `dotnet test windows\WinDrop.slnx` Platform 211, Core 340 test, hepsi geçti.
Betikler yalnız `-DryRun` + geçici `-InstallRoot/-StartMenuPath/-SendToPath/-SkipRegistry` ile denendi (gerçek kurulum, kısayol, HKCU ve çalışan WinDrop'a dokunulmadı).
Canlı (çalışan uygulamaya `QUIT` gönderen) kapatma denenmedi: makinedeki çalışan örnek gerçek kurulum. `QUIT` yolu yalnız kod okumasıyla doğrulandı (boru adı iki tarafta aynı).

Sayım (47 işlem gerektiren bulgu; #17, #29, #31 olumlu/kontrol notu): **37 tam yapıldı, 8 kısmen, 2 yapılmadı**. Bu turda yalnız iki küçük temizlik eklendi (aşağıda "Bu turda").

## Bulgu → durum

| # | Durum | Not / dosya |
|---|-------|-------------|
| 1 Servis başlamazsa sessiz | yapıldı | Kalıcı durum bandı (`MainViewModel.ShowNotice`, `MainWindow.xaml`), "Günlüğü aç" / "Yeniden başlat"; `App.xaml.cs StartRuntimeAsync` |
| 2 Yakalanmamış hatalar | yapıldı | `Program.Main` try/catch + `AppDomain.UnhandledException` + `UnobservedTaskException`; UI hatası banda düşer (`App.xaml.cs`, `Program.cs`) |
| 3 Seçim gönderim öncesi siliniyor | yapıldı | Hata olursa seçim geri konur, `catch (Exception)` (`MainViewModel.SendAsync`) |
| 4 Eşleştirme başlatılamazsa takılı | yapıldı | `PairingViewModel.StartAsync` try/catch, hata metni + yeniden başlatma; `PairingWindow.xaml.cs` |
| 5 BOM'lu settings.json | yapıldı | `AppSettings.Load` BOM'u atlar, bozuk dosyayı `.corrupt` yedekler, `onProblem` ile bildirir (`Program.cs` + ana pencere bandı). Test: `StoreTests.Utf8Bom_Settings_Are_Read` |
| 6 Küçük resim istisnaları | yapıldı | `Thumbnails.cs` (`ExternalException`), `TransferRow.UpdateThumbnail` try/catch |
| 7 SynchronizationContext | yapıldı | `UiContext.Capture()` (Strings.cs); Real/FakeServices kullanır |
| 8 Listeler baştan kuruluyor | yapıldı | `MainViewModel`: aktarımlar artımlı Add/Remove; `RefreshDevices` değişmediyse dokunmaz |
| 9 SettingsViewModel sızıntısı | yapıldı | `IDisposable`, `SettingsWindow.Closed` |
| 10 Boru sunucusu tıkanması | yapıldı | 2 sn okuma zaman aşımı, 2000 satır / 32 KB sınırı (`SingleInstance.cs`) |
| 11 Teklif Enter ile kabul | yapıldı | `IncomingOfferWindow.xaml`: `ShowActivated=False`, `IsDefault` yok (yalnız Reddet `IsCancel`); Kabul vurgulu ama varsayılan değil. `RelayApprovalWindow` aynı |
| 12 Pencereler üst üste | yapıldı | `Views\PopupStack.cs` (yukarı doğru dizer, kapanınca yeniden dizer) |
| 13 Oturum kapanırken engelleme | yapıldı | `App.SessionEnding` → `AllowClose = true` |
| 14 `--hidden` ikinci örnek / ön plan | yapıldı | `Forward`: yalnız `--hidden` iletilmez; `AllowSetForegroundWindow` |
| 15 Çıkış onayı / 8 sn donma | kısmen | Devam eden aktarımda uygulama dilinde onay (`ConfirmQuitWithActiveTransfers`), pencereler önce gizlenir. `OnExit` içindeki `Wait(8 sn)` ve yeniden başlatmada 53600 portu çakışma riski duruyor |
| 16 Simge durumunda `Activate` | yapıldı | `App.Restore` |
| 18 Çalışan dil | yapıldı | `AppLanguage.Applied`; `RestartNeeded` buna göre |
| 19 Ayar doğrulama / kayıt hatası | yapıldı | Satır içi doğrulama (klasör, MB 1..1 TB, relay biçimi), klasör oluşturma, kayıt hatasında eski değerler geri yüklenir (`SettingsViewModel`) |
| 20 `LostFocus` + Enter | yapıldı | `UpdateSourceTrigger=PropertyChanged`, MB kutusu metin (`AutoAcceptMaxText`) |
| 21 Ayarlar kaydırma | yapıldı | İçerik `ScrollViewer`, cihaz listesi `MaxHeight=190`, Kaydet/Vazgeç altta sabit, `MaxHeight = WorkArea - 32` (`SettingsWindow.xaml(.cs)`) |
| 22 `IsAutoStartEnabled` büyük/küçük harf | kısmen | `Platform\ShellIntegration.cs:114` karşılaştırma `OrdinalIgnoreCase`; StartupApproved durumu okunmuyor |
| 23 Deney örneği gerçek kayıtlara dokunuyor | yapıldı (kısmen günlük) | `WINDROP_INSTANCE` doluysa `ShellIntegration` kullanılmaz (`App.OpenSettings`). Günlük dosyası hâlâ ortak |
| 24 SendTo açıklaması sabit TR | kısmen | `install.ps1` UI diline göre; `ShellIntegration.cs:75` (Platform) hâlâ sabit Türkçe |
| 25 MessageBox / diyalog dili | kısmen | Evet/Hayır onayları `Views\ConfirmDialog.cs` ile uygulama dilinde (varsayılan Hayır). `OpenFileDialog/OpenFolderDialog` düğmeleri hâlâ OS dilinde (Win32 ortak diyalog; başlıkları uygulama dilinde) |
| 26 "Yeniden başlatınca…" iki dilli | **yapılmadı** | `Settings.LangRestartNote` iki dilde de "TR / EN"; kasıtlı olabilir, tasarım kararı |
| 27 Dilbilgisi | kısmen | `Transfer.To/From` "Alıcı: / Gönderen:", tekil/çoğul (`Strings.Plural`, `.One` anahtarları), `Relay.Timeout` ayrıldı. `Err.*` metinlerinde `'` / `’` karışıklığı duruyor (kozmetik) |
| 28 "Sistem" dili | yapıldı | `AppLanguage.DisplayUi` (CurrentUICulture, `Apply` öncesi yakalanır) |
| 30 Küçük resim önbelleği | yapıldı | `null`/hata önbellekten düşer, 200 öğe sınırı |
| 32 Erişilebilirlik | yapıldı | `AutomationProperties` (cihaz, kutular `LabeledBy`, ilerleme, bağlamlı İptal/Klasörde göster/Kaldır, QR, canlı bölgeler), `DeviceViewModel.ToString`, satırlar odaklanabilir, Ctrl+O / Ctrl+Enter. Erişim tuşu (`_`) eklenmedi |
| 33 Kontrast | yapıldı | Tema duyarlı `ErrorBrush` (açık temada `#C42B1C`), yardımcı metin 0.8 opaklık |
| 34 Sürükleme kenarlığı | yapıldı | `MainWindow.xaml.cs` orijinal fırçayı geri yükler |
| 35 Tepsi simgesi | kısmen | Tek tık = Aç yapıldı. Koyu tema menü görünümü ve DPI'ya uyum yapılmadı |
| 36 Tepsideyken bildirim | yapıldı | `App.OnTransfersChanged` → tamamlanınca balon, tıklayınca klasörde göster |
| 37 Teklifte dosya adları | yapıldı | `IncomingOfferWindow`: ilk 5 ad + "+N" (adlar temizlenerek) |
| 38 Zarif kapatma | yapıldı | `--quit` / boru `QUIT` → `App.HandleRequest` → `ExitApp(false)`; betikler önce QUIT, 12 sn bekler, son çare `Kill` + `WaitForExit` (`install.ps1`, `uninstall.ps1`). Canlı denenmedi |
| 39 `-DryRun` kapatıyordu | yapıldı | `Stop-WinDrop` DryRun'da yalnız yazdırır. Geçici kökle denendi |
| 40 Atomik olmayan kopyalama | yapıldı | `<InstallRoot>.new` → `.old` yer değiştirme, hata olursa geri alma |
| 41 `-InstallRoot` gerçek kayıtları siliyordu | yapıldı | Yalnız hedefi bu kök olan kısayol/Run/Uninstall kaydı silinir; başka kurulum varsa güvenlik duvarı atlanır |
| 42 UAC iptali | yapıldı | `try/catch` + uyarı, kurulum/kaldırma devam eder; çıkış kodu denetlenir |
| 43 `-like` | yapıldı | `Test-UnderPath` (`StartsWith` + ayırıcı, `OrdinalIgnoreCase`), `-LiteralPath` |
| 44 `-NoSendTo` / eski Run | yapıldı | Bu kuruluma ait SendTo silinir; var olan Run değeri yeni exe'ye yönlendirilir |
| 45 Güvenlik duvarı Private | kısmen | Kurulum Public ağ profilini uyarır; uygulama içinde "Doğrudan bağlantı engellenmiş olabilir" ipucu yok |
| 46 Uygulamalar ve özellikler | kısmen | HKCU Uninstall kaydı yazılır/silinir. `install.ps1` kurulu klasöre kopyalanmıyor (güncelleme için zip gerekir) |
| 47 BOM / PS 5.1 | yapıldı | `remove-test-rules.ps1`, `make-ico.ps1` BOM'lu |
| 48 publish | kısmen | Önceki `dist` başarısızlıkta korunur, `ZipFile` (`/` ayırıcı, 2 GB sınırı yok), `.sha256`, INSTALL.md pakete girer, ayarlarda sürüm görünür. İmza yok; `dist\` artıkları (kaynak dışı) temizlenmedi |
| 49 ExecutionPolicy / MOTW | yapıldı | `docs\INSTALL.md` ve betik başlıkları `-ExecutionPolicy Bypass -File` komutunu verir, `Unblock-File` notu |
| 50 INSTALL.md eksikleri | yapıldı | Windows 11 22H2+, SmartScreen, güncelleme, Bluetooth/güvenlik duvarı, günlük, port sabit (53600), sorun giderme |

Görsel notlar (V1-V10): V2 (hazırlanan dosya listesi, tek tek çıkarma, Gönder vurgulu), V3 (dosya adı, yüzde, yön ikonu, saat, "Bitenleri temizle"), V4 (rozet kontrastı), V5 (boş durum yönlendirmesi), V7 (onay istenirken "bekleniyor" gizli), V8/V9 (60 sn, `Relay.Timeout`) yapıldı. V1/V6/V10 gözlem; V6 aralıkları 16/6 olarak düzenlendi.

## DECISIONS (App)

- Madde 3: `IncomingOfferWindow.TimeoutSeconds = 60` ve `RelayApprovalWindow.TimeoutSeconds = 60`; süre dolunca ret/iptal (`Answer(false)`).
- Diğer maddeler Core/Platform tarafında (bkz. `windows-core-fixes.md`).

## Bu turda yapılanlar

- `ViewModels\MainViewModel.cs`: hiçbir yerde kullanılmayan `TransferRow.IsSend` kaldırıldı (ölü kod).
- `Views\SettingsWindow.xaml`: sürüm metni opaklığı 0.7 → 0.8 (kontrast, #33 ile tutarlı).
- Gözden geçirmede resx kontrolü: `Strings.resx` ve `Strings.en.resx` 196/196 anahtar, fark yok, yinelenen yok; kodda/XAML'de kullanılıp resx'te olmayan anahtar yok.

## Ertelendi

- **#22** `IsAutoStartEnabled` için StartupApproved durumu (Görev Yöneticisi'nden devre dışı bırakma): `Platform\ShellIntegration.cs`; Platform kapsamı.
- **#24** SendTo kısayol açıklamasının dile göre yerelleştirilmesi: aynı dosya (`ShellIntegration.cs:75`).
- **#25** Dosya/klasör seçici düğmeleri OS dilinde: ortak Win32 diyalog; özel seçici yazmak büyük iş.
- **#26** İki dilli "yeniden başlatınca" notu: kasıtlı olabilir, tasarım kararı.
- **#27** `Err.*` tırnak biçimi birleştirmesi: kozmetik, çok sayıda satır.
- **#15** `OnExit` içindeki 8 sn bekleme ve yeniden başlatmada port çakışması: Platform kapanış akışını değiştirir (riskli).
- **#23** Günlük dosyasının örnek başına ayrılması.
- **#35** Tepsi menüsünün koyu temaya uyumu (WinForms `ContextMenuStrip`) ve DPI'ya göre simge: WPF tabanlı menü gerektirir.
- **#45** Uygulama içi "Doğrudan bağlantı engellenmiş olabilir" ipucu: bağlantı hatası sınıflandırması (Platform) gerekir.
- **#46** `install.ps1`'in kurulu klasöre kopyalanması; **#48** imzalama / `dist\` artıklarının temizliği.
- Erişim tuşları (`_`): çok dilli etiketlerde çakışma kontrolü gerektirir.
- Canlı `QUIT` kapatma denemesi ve gerçek cihazla (BLE) teklif penceresi davranışı (odak çalmama) elle doğrulanmalı.
