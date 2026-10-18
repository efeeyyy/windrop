using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;

namespace WinDrop.App.ViewModels;

/// <summary>Ana pencerenin kabuğa (pencere açma, dosya seçici) ihtiyaç duyduğu işlemler.</summary>
public interface IAppShell
{
    void OpenPairing();
    void OpenSettings();
    IReadOnlyList<string> PickFiles();
    IReadOnlyList<string> PickFolder();
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
        var img = await Thumbnails.GetAsync(path);
        if (!string.Equals(_thumbPath, path, StringComparison.Ordinal) || img is null) return;
        Thumbnail = img;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public void Detach() => Item.PropertyChanged -= OnItemChanged;

    public string Title => Strings.Format(Item.Direction == TransferDirection.Send ? "Transfer.To" : "Transfer.From", Item.DeviceName);

    public string Detail
    {
        get
        {
            var text = Strings.Format("Transfer.Detail", Item.FileCount, ByteFormat.Size(Item.TotalBytes));
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
    public double Percent => Item.Progress * 100;
    public bool IsSend => Item.Direction == TransferDirection.Send;
    public bool CanCancel => Item.Status is TransferStatus.Waiting or TransferStatus.Active;
    public bool CanShow => Item.Status == TransferStatus.Completed && !string.IsNullOrEmpty(Item.SavedPath);
    public bool ShowProgress => Item.Status is TransferStatus.Waiting or TransferStatus.Active;

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

    public MainViewModel(IDeviceService devices, ITransferService transfers, IAppShell shell)
    {
        _devices = devices;
        _transfers = transfers;
        _shell = shell;

        _devices.DevicesChanged += (_, _) => RefreshDevices();
        _transfers.Transfers.CollectionChanged += OnTransfersChanged;
        StagedPaths.CollectionChanged += (_, _) => OnStagedChanged();
        RefreshDevices();
        OnTransfersChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<TransferRow> Transfers { get; } = [];
    public ObservableCollection<string> StagedPaths { get; } = [];

    [ObservableProperty] public partial DeviceViewModel? SelectedDevice { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public bool HasNoDevices => Devices.Count == 0;
    public bool HasNoTransfers => Transfers.Count == 0;
    public bool HasStaged => StagedPaths.Count > 0;
    public string StagedText => Strings.Format("Main.StagedFormat", StagedPaths.Count);
    public bool ShowSelectHint => HasStaged && SelectedDevice is not { IsReachable: true };

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        OnPropertyChanged(nameof(ShowSelectHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Sürükle-bırak, dosya seçici, <c>--send</c> ve tepsiden gelen yollar.</summary>
    public void StageFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!string.IsNullOrWhiteSpace(p) && !StagedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                StagedPaths.Add(p);
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
        var selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var d in _devices.Devices) Devices.Add(new DeviceViewModel(d));
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId);
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(ShowSelectHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in Transfers) row.Detach();
        Transfers.Clear();
        foreach (var item in _transfers.Transfers) Transfers.Add(new TransferRow(item));
        OnPropertyChanged(nameof(HasNoTransfers));
    }

    private bool CanSend() => HasStaged && SelectedDevice is { IsReachable: true };

    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var device = SelectedDevice;
        if (device == null) return;
        var paths = StagedPaths.ToArray();
        StagedPaths.Clear();
        ErrorMessage = null;
        try { await _transfers.SendAsync(device.Id, paths); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorMessage = Strings.Format("Main.SendError", ex.Message);
            AppLog.Write($"gönderme hatası: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PickFiles() => StageFiles(_shell.PickFiles());

    [RelayCommand]
    private void PickFolder() => StageFiles(_shell.PickFolder());

    [RelayCommand]
    private void ClearStaged() => StagedPaths.Clear();

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
}
