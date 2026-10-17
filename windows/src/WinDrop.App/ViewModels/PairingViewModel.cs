using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;

namespace WinDrop.App.ViewModels;

public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _devices;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairingSession? _session;

    public PairingViewModel(IDeviceService devices)
    {
        _devices = devices;
        _devices.PairingRequested += OnPairingRequested;
        _timer.Tick += (_, _) => UpdateRemaining();
        StatusText = Strings.Get("Pair.Waiting");
    }

    /// <summary>Eşleşme tamamlandı veya kullanıcı kapattı: pencere kapanabilir.</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty] public partial ImageSource? QrImage { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string RemainingText { get; set; } = "";
    [ObservableProperty] public partial bool IsExpired { get; set; }
    [ObservableProperty] public partial PairingRequest? PendingRequest { get; set; }

    public bool HasPending => PendingRequest != null;
    public string ConfirmText => PendingRequest == null ? ""
        : Strings.Format("Pair.Confirm", PendingRequest.DeviceName)
          + (PendingRequest.VerificationCode is { } code ? "\n" + Strings.Format("Pair.Code", code) : "");

    partial void OnPendingRequestChanged(PairingRequest? value)
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ConfirmText));
    }

    public async Task StartAsync()
    {
        IsExpired = false;
        _session?.Cancel();
        _session = await _devices.StartPairingAsync();
        QrImage = RenderQr(_session.QrPayload);
        StatusText = Strings.Get("Pair.Waiting");
        UpdateRemaining();
        _timer.Start();
    }

    private void UpdateRemaining()
    {
        if (_session == null) return;
        var left = _session.ExpiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero || _session.IsCancelled)
        {
            _timer.Stop();
            _session.Cancel();
            if (PendingRequest == null)
            {
                IsExpired = true;
                QrImage = null;
                StatusText = Strings.Get("Pair.Expired");
                RemainingText = "";
            }
            return;
        }
        RemainingText = Strings.Format("Pair.Remaining", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    private void OnPairingRequested(object? sender, PairingRequest request) => PendingRequest = request;

    [RelayCommand]
    private void Accept()
    {
        if (PendingRequest == null) return;
        _devices.RespondToPairing(PendingRequest, true);
        PendingRequest = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Reject()
    {
        if (PendingRequest == null) return;
        _devices.RespondToPairing(PendingRequest, false);
        PendingRequest = null;
    }

    [RelayCommand]
    private async Task RestartAsync() => await StartAsync();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static BitmapImage RenderQr(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var image = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _timer.Stop();
        _devices.PairingRequested -= OnPairingRequested;
        _session?.Cancel();
    }
}
