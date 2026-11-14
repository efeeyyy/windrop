using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;
using WinDrop.App.Views;
using WinDrop.Platform;

namespace WinDrop.App.ViewModels;

/// <summary>Dil seçici öğesi: <paramref name="Code"/> ayara yazılır, <paramref name="Label"/> gösterilir.</summary>
public sealed record LanguageOption(string Code, string Label);

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Otomatik kabul üst sınırı (MB) için kabul edilen en büyük değer (1 TB); taşmayı önler.</summary>
    public const long MaxAutoAcceptMb = 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly ShellIntegration? _shell;
    private readonly IDeviceService _devices;
    private readonly Action? _restart;
    private readonly EventHandler _devicesChanged;

    /// <param name="shell">Kabuk entegrasyonu (SendTo/Run); <c>null</c> = uygulanmaz (ayrı deneme örneği gerçek kısayol/kayıt anahtarına dokunmasın).</param>
    public SettingsViewModel(AppSettings settings, ShellIntegration? shell, IDeviceService devices, Action? restart = null)
    {
        _restart = restart;
        SelectedLanguage = AppLanguage.Normalize(settings.Language);
        _settings = settings;
        _shell = shell;
        _devices = devices;

        DownloadFolder = settings.DownloadFolder;
        AutoAccept = settings.AutoAccept;
        AutoAcceptMaxText = Math.Max(1, settings.AutoAcceptMaxBytes / (1024 * 1024)).ToString(CultureInfo.CurrentCulture);
        RelayUrl = settings.RelayUrl;
        // Gerçek durumu göster (ayardaki "istenen" değil).
        SendToEnabled = shell?.IsSendToInstalled ?? settings.SendToEnabled;
        AutoStartEnabled = shell?.IsAutoStartEnabled ?? settings.AutoStartEnabled;
        _devicesChanged = (_, _) => RefreshDevices();
        _devices.DevicesChanged += _devicesChanged;
        RefreshDevices();
    }

    public event EventHandler? CloseRequested;

    public string VersionText => Strings.Format("Settings.Version",
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

    [ObservableProperty] public partial string DownloadFolder { get; set; }
    [ObservableProperty] public partial bool AutoAccept { get; set; }
    [ObservableProperty] public partial string AutoAcceptMaxText { get; set; }
    [ObservableProperty] public partial string RelayUrl { get; set; }
    [ObservableProperty] public partial bool SendToEnabled { get; set; }
    [ObservableProperty] public partial bool AutoStartEnabled { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    // Satır içi doğrulama hataları (ilgili kutunun altında).
    [ObservableProperty] public partial string? FolderError { get; set; }
    [ObservableProperty] public partial string? MaxError { get; set; }
    [ObservableProperty] public partial string? RelayError { get; set; }

    /// <summary>Seçilebilir diller; adlar kendi dillerinde (çeviri gerektirmez), "Sistem" hariç.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(AppLanguage.Auto, Strings.Get("Settings.LangSystem")),
        new(AppLanguage.Turkish, "Türkçe"),
        new(AppLanguage.English, "English"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartNeeded))]
    public partial string SelectedLanguage { get; set; }

    /// <summary>Seçilen dil, ÇALIŞAN dilden (açılışta uygulanan) farklı: yeniden başlatınca uygulanır.
    /// Kayıtlı ayarla değil çalışan dille karşılaştırılır; "Kaydet"ten sonra yeniden açılsa da uyarı kaybolmaz.</summary>
    public bool RestartNeeded => SelectedLanguage != AppLanguage.Applied;

    public ObservableCollection<DeviceInfo> PairedDevices { get; } = [];
    public bool HasNoDevices => PairedDevices.Count == 0;
    public bool HasDevices => PairedDevices.Count > 0;

    private void RefreshDevices()
    {
        PairedDevices.Clear();
        // Yalnız kayıtlı (eşli/tanıdık) cihazlar kaldırılabilir; yakındaki bilinmeyen iPhone'lar burada listelenmez.
        foreach (var d in _devices.RegisteredDevices.Where(d => d.IsTrusted)) PairedDevices.Add(d);
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>Pencere kapanınca servis olayından çıkar (kapalı pencerenin koleksiyonuna yazılmasın, sızıntı olmasın).</summary>
    public void Dispose() => _devices.DevicesChanged -= _devicesChanged;

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = Strings.Get("Dialog.PickFolder"), InitialDirectory = DownloadFolder };
        if (dialog.ShowDialog() == true) DownloadFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void RemoveDevice(DeviceInfo? device)
    {
        if (device == null) return;
        if (Confirm(Strings.Format("Settings.RemoveConfirm", device.Name)))
            _devices.RemoveDevice(device.Id);
    }

    [RelayCommand]
    private void RemoveAllDevices()
    {
        if (!Confirm(Strings.Get("Settings.RemoveAllConfirm"))) return;
        foreach (var d in _devices.RegisteredDevices.ToArray()) _devices.RemoveDevice(d.Id);
    }

    /// <summary>Uygulama dilinde evet/hayır (varsayılan: Hayır).</summary>
    private static bool Confirm(string message) =>
        ConfirmDialog.Ask(System.Windows.Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault(),
            message, Strings.Get("Dialog.Yes"), Strings.Get("Dialog.No"));

    [RelayCommand]
    private void Save()
    {
        if (TryApply()) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ayarları kaydedip dili uygulamak için uygulamayı yeniden başlatır.</summary>
    [RelayCommand]
    private void RestartNow()
    {
        if (TryApply()) _restart?.Invoke();
    }

    /// <summary>Alanları doğrular; hata varsa ilgili satır içi mesajı doldurur ve false döner. Değerler <paramref name="folder"/>/<paramref name="maxMb"/>/<paramref name="relay"/> ile verilir.</summary>
    private bool Validate(out string folder, out long maxMb, out string relay)
    {
        FolderError = MaxError = RelayError = null;
        ErrorMessage = null;
        folder = string.IsNullOrWhiteSpace(DownloadFolder) ? AppSettings.DefaultDownloadFolder() : DownloadFolder.Trim();
        relay = (RelayUrl ?? "").Trim();
        maxMb = 0;
        bool ok = true;

        try
        {
            folder = Path.GetFullPath(folder);
            if (!Path.IsPathRooted(folder) || folder.StartsWith(@"\\.\", StringComparison.Ordinal) || folder.StartsWith(@"\\?\", StringComparison.Ordinal))
                throw new ArgumentException("yol kök değil");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            FolderError = Strings.Get("Settings.FolderInvalid");
            ok = false;
        }

        var text = (AutoAcceptMaxText ?? "").Trim();
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out maxMb) || maxMb < 1 || maxMb > MaxAutoAcceptMb)
        {
            MaxError = Strings.Format("Settings.MaxInvalid", MaxAutoAcceptMb.ToString("N0", CultureInfo.CurrentCulture));
            ok = false;
        }

        if (relay.Length > 0 && !IsValidRelay(relay))
        {
            RelayError = Strings.Get("Settings.RelayInvalid");
            ok = false;
        }
        return ok;
    }

    /// <summary><c>host</c>, <c>host:port</c> ya da <c>wss://host[:port][/yol]</c> (ws:// kabul edilir ama önerilmez).</summary>
    internal static bool IsValidRelay(string value)
    {
        if (value.Any(char.IsWhiteSpace)) return false;
        string candidate = value.Contains("://", StringComparison.Ordinal) ? value : "wss://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme is "wss" or "ws") && uri.Host.Length > 0 && Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown;
    }

    private bool TryApply()
    {
        if (!Validate(out var folder, out var maxMb, out var relay)) return false;

        // Klasör yoksa oluştur (yazılamıyorsa satır içi hata).
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Write($"indirme klasörü oluşturulamadı: {ex.Message}");
            FolderError = Strings.Format("Settings.FolderCreateFailed", ex.Message);
            return false;
        }

        // Canlı ayar nesnesi yalnız kayıt başarılıysa kalıcı değişir; hata olursa eski değerler geri yüklenir.
        var old = (_settings.Language, _settings.DownloadFolder, _settings.AutoAccept, _settings.AutoAcceptMaxBytes,
                   _settings.RelayUrl, _settings.SendToEnabled, _settings.AutoStartEnabled);
        _settings.Language = SelectedLanguage;
        _settings.DownloadFolder = folder;
        _settings.AutoAccept = AutoAccept;
        _settings.AutoAcceptMaxBytes = checked(maxMb * 1024 * 1024);
        _settings.RelayUrl = relay;
        _settings.SendToEnabled = SendToEnabled;
        _settings.AutoStartEnabled = AutoStartEnabled;
        try
        {
            _settings.Save();
            if (_shell is not null) _shell.Apply(_settings);
            else AppLog.Write("ayrı deneme örneği (WINDROP_INSTANCE): SendTo/otomatik başlatma kısayolları değiştirilmedi.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or PlatformNotSupportedException or System.Security.SecurityException)
        {
            (_settings.Language, _settings.DownloadFolder, _settings.AutoAccept, _settings.AutoAcceptMaxBytes,
                _settings.RelayUrl, _settings.SendToEnabled, _settings.AutoStartEnabled) = old;
            AppLog.Write($"ayarlar uygulanamadı: {ex.Message}");
            ErrorMessage = Strings.Format("Settings.SaveError", ex.Message);
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
