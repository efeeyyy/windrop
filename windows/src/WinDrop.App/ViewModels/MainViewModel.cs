using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;
using WinDrop.Platform;

namespace WinDrop.App.ViewModels;

/// <summary>Ana pencerenin kabuğa (pencere açma, dosya seçici) ihtiyaç duyduğu işlemler.</summary>
public interface IAppShell
{
    void OpenPairing();
    void OpenSettings();
    IReadOnlyList<string> PickFiles();
    IReadOnlyList<string> PickFolder();
    void OpenLog();
    void RestartApp();
}

public sealed class DeviceViewModel
{
    public DeviceViewModel(DeviceInfo info) => Info = info;

    public DeviceInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public bool IsNearby => Info.IsNearby;
    public bool CanPush => Info.CanPush;
    /// <summary>Gönderilebilir: yakında ya da push bildirimiyle ulaşılabilir (§6.5).</summary>
    public bool IsReachable => Info.IsNearby || Info.CanPush;
    public string BadgeText => Strings.Get(Info.IsNearby ? "Device.Nearby" : Info.CanPush ? "Device.Push" : "Device.Away");

    /// <summary>Ekran okuyucu adı ("Efe'nin iPhone'u, Yakında").</summary>
    public string AutomationName => Name + ", " + BadgeText;
    public override string ToString() => AutomationName;
}

/// <summary>Gönderilmeye hazırlanan dosya/klasör.</summary>
public sealed record StagedFile(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public string RemoveLabel => Strings.Format("Main.RemoveStagedNamed", Name);
    public override string ToString() => Name;
}

/// <summary><see cref="TransferItem"/> için görüntü satırı (metinler, düğme durumları).</summary>
public sealed class TransferRow : ObservableObject
{
    public TransferRow(TransferItem item)
    {
        Item = item;
        item.PropertyChanged += OnItemChanged;
        UpdateThumbnail();
    }

    public TransferItem Item { get; }

    private string? _thumbPath;

    /// <summary>Önizleme; aktarım sürerken (yol yokken) null → yer tutucu simge.</summary>
    public System.Windows.Media.ImageSource? Thumbnail { get; private set; }

    private async void UpdateThumbnail()
    {
        var path = Item.PreviewPath;
        if (string.IsNullOrEmpty(path) || string.Equals(path, _thumbPath, StringComparison.Ordinal)) return;
        _thumbPath = path;
        try
        {
            var img = await Thumbnails.GetAsync(path);
            if (!string.Equals(_thumbPath, path, StringComparison.Ordinal) || img is null) return;
            Thumbnail = img;
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write($"küçük resim satıra uygulanamadı ({ex.GetType().Name})");   // async void: istisna dispatcher'a sızmasın
        }
    }

    public void Detach() => Item.PropertyChanged -= OnItemChanged;

    public string Title => Strings.Format(Item.Direction == TransferDirection.Send ? "Transfer.To" : "Transfer.From", Item.DeviceName);

    /// <summary>İlk dosya/klasör adı (biliniyorsa).</summary>
    public string? FirstName
    {
        get
        {
            var path = Item.PreviewPath;
            if (string.IsNullOrEmpty(path)) return null;
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return name.Length > 0 ? name : path;
        }
    }

    public string Detail
    {
        get
        {
            var size = ByteFormat.Size(Item.TotalBytes);
            string text = FirstName is { } name
                ? (Item.FileCount > 1 ? Strings.Format("Transfer.NameMore", name, Item.FileCount - 1) : name) + " · " + size
                : Strings.Plural("Transfer.Detail", Item.FileCount, Item.FileCount, size);
            if (!string.IsNullOrEmpty(Item.ErrorMessage) && Item.Status is TransferStatus.Failed or TransferStatus.Cancelled or TransferStatus.Rejected)
                return text + "\n" + Item.ErrorMessage;
            return Item.Status == TransferStatus.Active && Item.BytesPerSecond > 0
                ? text + " · " + ByteFormat.Speed(Item.BytesPerSecond)
                : text;
        }
    }

    public string StatusText => Item.PushPending && Item.Status == TransferStatus.Waiting
        ? Strings.Get("Status.PushSent")
        : Strings.Get("Status." + Item.Status);

    /// <summary>Durum · yüzde (sürerken) · saat.</summary>
    public string StatusLine
    {
        get
        {
            var text = StatusText;
            if (Item.Status == TransferStatus.Active) text += " · " + Strings.Format("Transfer.Percent", Percent);
            var started = Item.StartedAt;
            return text + " · " + started.ToString(started.Date == DateTimeOffset.Now.Date ? "t" : "g", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Yön simgesi (Segoe Fluent Icons): yükleme / indirme oku.</summary>
    public string DirectionGlyph => Item.Direction == TransferDirection.Send ? "" : "";
    public string DirectionName => Strings.Get(Item.Direction == TransferDirection.Send ? "Transfer.Sent" : "Transfer.Received");
    public double Percent => Item.Progress * 100;
    public bool CanCancel => Item.Status is TransferStatus.Waiting or TransferStatus.Active;
    public bool CanShow => Item.Status == TransferStatus.Completed && !string.IsNullOrEmpty(Item.SavedPath);
    public bool ShowProgress => Item.Status is TransferStatus.Waiting or TransferStatus.Active;
    public bool IsFinished => Item.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled or TransferStatus.Rejected;

    // Ekran okuyucu: satır, ilerleme ve aynı adlı düğmeler bağlamlı adlar taşır.
    public string AutomationName => DirectionName + ": " + Title + ". " + Detail.Replace('\n', ' ') + ". " + StatusLine;
    public string ProgressName => Strings.Format("Main.ProgressNamed", Title);
    public string CancelName => Strings.Format("Main.CancelNamed", Title);
    public string ShowName => Strings.Format("Main.ShowNamed", Title);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(string.Empty);
        UpdateThumbnail();
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDeviceService _devices;
    private readonly ITransferService _transfers;
    private readonly IAppShell _shell;
    private bool _hadFinished;

    public MainViewModel(IDeviceService devices, ITransferService transfers, IAppShell shell)
    {
        _devices = devices;
        _transfers = transfers;
        _shell = shell;

        _devices.DevicesChanged += (_, _) => RefreshDevices();
        _transfers.Transfers.CollectionChanged += OnTransfersChanged;
        Staged.CollectionChanged += (_, _) => OnStagedChanged();
        RefreshDevices();
        OnTransfersChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<TransferRow> Transfers { get; } = [];
    public ObservableCollection<StagedFile> Staged { get; } = [];

    [ObservableProperty] public partial DeviceViewModel? SelectedDevice { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    /// <summary>Kalıcı durum bandı: servisler başlamadı, ayar dosyası bozuktu, beklenmeyen hata. Kullanıcı kapatana kadar kalır.</summary>
    [ObservableProperty] public partial string? NoticeMessage { get; set; }
    [ObservableProperty] public partial bool NoticeCanRestart { get; set; }

    public bool HasNotice => !string.IsNullOrEmpty(NoticeMessage);
    public bool HasNoDevices => Devices.Count == 0;
    public bool HasNoTransfers => Transfers.Count == 0;
    public bool HasStaged => Staged.Count > 0;
    public bool HasFinished => Transfers.Any(t => t.IsFinished);
    public string StagedText => Strings.Plural("Main.StagedFormat", Staged.Count, Staged.Count);
    public bool ShowSelectHint => HasStaged && SelectedDevice is not { IsReachable: true };

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        OnPropertyChanged(nameof(ShowSelectHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnNoticeMessageChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    /// <summary>Durum bandını gösterir (kapatılana kadar).</summary>
    public void ShowNotice(string message, bool canRestart = false)
    {
        NoticeCanRestart = canRestart;
        NoticeMessage = message;
    }

    /// <summary>Sürükle-bırak, dosya seçici, <c>--send</c> ve tepsiden gelen yollar.</summary>
    public void StageFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!string.IsNullOrWhiteSpace(p) && !Staged.Any(s => string.Equals(s.Path, p, StringComparison.OrdinalIgnoreCase)))
                Staged.Add(new StagedFile(p));
        if (SelectedDevice is not { IsReachable: true })
            SelectedDevice = Devices.FirstOrDefault(d => d.IsNearby) ?? Devices.FirstOrDefault(d => d.IsReachable) ?? SelectedDevice;
    }

    private void OnStagedChanged()
    {
        OnPropertyChanged(nameof(HasStaged));
        OnPropertyChanged(nameof(StagedText));
        OnPropertyChanged(nameof(ShowSelectHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDevices()
    {
        var infos = _devices.Devices;
        // Değişmediyse listeyi yeniden kurma (BLE sık tetikler: seçim/kaydırma titremesin).
        if (infos.Count == Devices.Count && infos.Zip(Devices).All(p => p.First == p.Second.Info)) return;
        var selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var d in infos) Devices.Add(new DeviceViewModel(d));
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId);
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(ShowSelectHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0 && e.NewStartingIndex <= Transfers.Count:
                int at = e.NewStartingIndex;
                foreach (TransferItem item in e.NewItems) InsertRow(at++, item);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0
                                                          && e.OldStartingIndex + e.OldItems.Count <= Transfers.Count:
                for (int i = 0; i < e.OldItems.Count; i++) RemoveRow(e.OldStartingIndex);
                break;
            default:
                while (Transfers.Count > 0) RemoveRow(0);
                foreach (var item in _transfers.Transfers) InsertRow(Transfers.Count, item);
                break;
        }
        OnPropertyChanged(nameof(HasNoTransfers));
        UpdateHasFinished();
    }

    private void InsertRow(int index, TransferItem item)
    {
        var row = new TransferRow(item);
        row.PropertyChanged += OnRowChanged;
        Transfers.Insert(index, row);
    }

    private void RemoveRow(int index)
    {
        var row = Transfers[index];
        row.PropertyChanged -= OnRowChanged;
        row.Detach();
        Transfers.RemoveAt(index);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateHasFinished();

    private void UpdateHasFinished()
    {
        var now = HasFinished;
        if (now == _hadFinished) return;
        _hadFinished = now;
        OnPropertyChanged(nameof(HasFinished));
    }

    private bool CanSend() => HasStaged && SelectedDevice is { IsReachable: true };

    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var device = SelectedDevice;
        if (device == null) return;
        var paths = Staged.Select(s => s.Path).ToArray();
        Staged.Clear();
        ErrorMessage = null;
        try { await _transfers.SendAsync(device.Id, paths); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Hata: seçim kaybolmasın, geri koy ve nedenini göster (yalnız bilinen türler değil, her istisna).
            foreach (var p in paths.Reverse()) if (!Staged.Any(s => s.Path == p)) Staged.Insert(0, new StagedFile(p));
            ErrorMessage = Strings.Format("Main.SendError", ex is TransferException te ? ErrorTexts.Format(te.Error) : ex.Message);
            AppLog.Write($"gönderme hatası: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PickFiles() => StageFiles(_shell.PickFiles());

    [RelayCommand]
    private void PickFolder() => StageFiles(_shell.PickFolder());

    [RelayCommand]
    private void ClearStaged() => Staged.Clear();

    [RelayCommand]
    private void RemoveStaged(StagedFile? file)
    {
        if (file != null) Staged.Remove(file);
    }

    [RelayCommand]
    private void AddDevice() => _shell.OpenPairing();

    [RelayCommand]
    private void OpenSettings() => _shell.OpenSettings();

    [RelayCommand]
    private void CancelTransfer(TransferRow? row)
    {
        if (row != null) _transfers.Cancel(row.Item);
    }

    [RelayCommand]
    private void ShowInFolder(TransferRow? row)
    {
        if (row != null) _transfers.ShowInFolder(row.Item);
    }

    /// <summary>Biten (tamamlanan/başarısız/iptal/reddedilen) satırları listeden kaldırır; sürenleri bırakır.</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var row in Transfers.Where(t => t.IsFinished).ToArray()) _transfers.Transfers.Remove(row.Item);
    }

    [RelayCommand]
    private void DismissNotice() => NoticeMessage = null;

    [RelayCommand]
    private void OpenLog() => _shell.OpenLog();

    [RelayCommand]
    private void RestartApp() => _shell.RestartApp();
}
