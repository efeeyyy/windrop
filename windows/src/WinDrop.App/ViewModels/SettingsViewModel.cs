using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;
using WinDrop.Platform;

namespace WinDrop.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ShellIntegration _shell;
    private readonly IDeviceService _devices;

    public SettingsViewModel(AppSettings settings, ShellIntegration shell, IDeviceService devices)
    {
        _settings = settings;
        _shell = shell;
        _devices = devices;

        DownloadFolder = settings.DownloadFolder;
        AutoAccept = settings.AutoAccept;
        AutoAcceptMaxMb = Math.Max(1, settings.AutoAcceptMaxBytes / (1024 * 1024));
        RelayUrl = settings.RelayUrl;
        // Gerçek durumu göster (ayardaki "istenen" değil).
        SendToEnabled = shell.IsSendToInstalled;
        AutoStartEnabled = shell.IsAutoStartEnabled;
        _devices.DevicesChanged += (_, _) => RefreshDevices();
        RefreshDevices();
    }

    public event EventHandler? CloseRequested;

    [ObservableProperty] public partial string DownloadFolder { get; set; }
    [ObservableProperty] public partial bool AutoAccept { get; set; }
    [ObservableProperty] public partial long AutoAcceptMaxMb { get; set; }
    [ObservableProperty] public partial string RelayUrl { get; set; }
    [ObservableProperty] public partial bool SendToEnabled { get; set; }
    [ObservableProperty] public partial bool AutoStartEnabled { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public ObservableCollection<DeviceInfo> PairedDevices { get; } = [];
    public bool HasNoDevices => PairedDevices.Count == 0;
    public bool HasDevices => PairedDevices.Count > 0;

    private void RefreshDevices()
    {
        PairedDevices.Clear();
        foreach (var d in _devices.Devices) PairedDevices.Add(d);
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(HasDevices));
    }

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
        if (MessageBox.Show(Strings.Format("Settings.RemoveConfirm", device.Name), Strings.Get("App.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _devices.RemoveDevice(device.Id);
    }

    [RelayCommand]
    private void RemoveAllDevices()
    {
        if (MessageBox.Show(Strings.Get("Settings.RemoveAllConfirm"), Strings.Get("App.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var d in _devices.Devices.ToArray()) _devices.RemoveDevice(d.Id);
    }

    [RelayCommand]
    private void Save()
    {
        _settings.DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? AppSettings.DefaultDownloadFolder() : DownloadFolder;
        _settings.AutoAccept = AutoAccept;
        _settings.AutoAcceptMaxBytes = Math.Max(1, AutoAcceptMaxMb) * 1024 * 1024;
        _settings.RelayUrl = (RelayUrl ?? "").Trim();
        _settings.SendToEnabled = SendToEnabled;
        _settings.AutoStartEnabled = AutoStartEnabled;
        try
        {
            _settings.Save();
            _shell.Apply(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or PlatformNotSupportedException or System.Security.SecurityException)
        {
            AppLog.Write($"ayarlar uygulanamadı: {ex.Message}");
            ErrorMessage = Strings.Format("Settings.SaveError", ex.Message);
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
