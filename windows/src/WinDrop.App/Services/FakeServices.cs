using System.Collections.ObjectModel;
using System.Security.Cryptography;
using WinDrop.App.Infrastructure;

namespace WinDrop.App.Services;

/// <summary>Sahte cihaz servisi (ağ/BLE yok): tasarım ve deneme içindir.</summary>
public sealed class FakeDeviceService : IDeviceService
{
    private readonly SynchronizationContext _ui = UiContext.Capture();
    private readonly List<DeviceInfo> _devices =
    [
        new("fake-1", "Efe'nin iPhone'u", true),
        new("fake-2", "Eski iPhone", false),
    ];

    public IReadOnlyList<DeviceInfo> Devices => _devices.ToArray();
    public IReadOnlyList<DeviceInfo> RegisteredDevices => _devices.ToArray();

    public event EventHandler? DevicesChanged;
    public event EventHandler<PairingRequest>? PairingRequested;
    public event EventHandler<string>? AlreadyPaired;

    public Task<PairingSession> StartPairingAsync()
    {
        var session = new PairingSession(
            "windrop://pair?v=1&k=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), TimeSpan.FromMinutes(2));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6), session.Token);
                _ui.Post(_ => PairingRequested?.Invoke(this, new PairingRequest(Guid.NewGuid().ToString("N"), "Yeni iPhone")), null);
            }
            catch (OperationCanceledException) { }
        });
        return Task.FromResult(session);
    }

    public void RespondToPairing(PairingRequest request, bool accept)
    {
        if (!accept) return;
        if (_devices.Any(d => d.Name == request.DeviceName)) { AlreadyPaired?.Invoke(this, request.DeviceName); return; }
        _devices.Add(new DeviceInfo("fake-" + request.RequestId[..6], request.DeviceName, true));
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveDevice(string deviceId)
    {
        if (_devices.RemoveAll(d => d.Id == deviceId) > 0) DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Sahte aktarım servisi: ilerleme üretir, dosya yazmaz.</summary>
public sealed class FakeTransferService : ITransferService
{
    private readonly SynchronizationContext _ui = UiContext.Capture();
    private readonly Func<string> _downloadFolder;
    private readonly IDeviceService _devices;
    private readonly Dictionary<string, CancellationTokenSource> _running = [];

    public FakeTransferService(IDeviceService devices, Func<string> downloadFolder)
    {
        _devices = devices;
        _downloadFolder = downloadFolder;
    }

    public ObservableCollection<TransferItem> Transfers { get; } = [];

    public event EventHandler<IncomingOffer>? OfferReceived;

    public event EventHandler<RelayApproval>? RelayApprovalRequested;

    /// <summary>Deneme: aktarma sunucusu onayı üretir.</summary>
    public void SimulateRelayApproval(string device = "Efe'nin iPhone'u", long bytes = 48L * 1024 * 1024) =>
        _ui.Post(_ => RelayApprovalRequested?.Invoke(this, new RelayApproval(Guid.NewGuid().ToString("N"), device, bytes)), null);

    public void RespondToRelay(RelayApproval approval, bool allow) =>
        AppLog.Write($"[fake] aktarma yolu {(allow ? "onaylandı" : "reddedildi")}: {approval.DeviceName}");

    public Task SendAsync(string deviceId, IReadOnlyList<string> paths)
    {
        var name = _devices.Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "?";
        long total = 0;
        foreach (var p in paths)
        {
            try { total += File.Exists(p) ? new FileInfo(p).Length : 1024 * 1024; }
            catch (IOException) { total += 1024; }
        }
        var item = new TransferItem(Guid.NewGuid().ToString("N"), TransferDirection.Send, name, paths.Count, total);
        item.PreviewPath = paths.Count > 0 ? paths[0] : null;
        AppLog.Write($"[fake] gönder: {paths.Count} dosya → {name}");
        return Run(item, null);
    }

    /// <summary>Deneme: gelen teklif üretir.</summary>
    public void SimulateIncomingOffer(string sender = "Efe'nin iPhone'u", int count = 3, long bytes = 48L * 1024 * 1024) =>
        _ui.Post(_ => OfferReceived?.Invoke(this, new IncomingOffer(Guid.NewGuid().ToString("N"), sender, count, bytes)), null);

    public void RespondToOffer(IncomingOffer offer, bool accept)
    {
        if (!accept)
        {
            AppLog.Write($"[fake] teklif reddedildi: {offer.SenderName}");
            return;
        }
        var item = new TransferItem(offer.OfferId, TransferDirection.Receive, offer.SenderName, offer.FileCount, offer.TotalBytes);
        item.PreviewPath = Environment.GetEnvironmentVariable("WINDROP_FAKE_PREVIEW");
        _ = Run(item, _downloadFolder());
    }

    public void Cancel(TransferItem transfer)
    {
        lock (_running)
            if (_running.TryGetValue(transfer.Id, out var cts)) cts.Cancel();
    }

    public void ShowInFolder(TransferItem transfer) => Explorer.Reveal(transfer.SavedPath);

    private async Task Run(TransferItem item, string? savedPath)
    {
        using var cts = new CancellationTokenSource();
        lock (_running) _running[item.Id] = cts;
        _ui.Send(_ => Transfers.Insert(0, item), null);
        try
        {
            await Task.Delay(400, cts.Token);
            _ui.Send(_ => item.Status = TransferStatus.Active, null);
            const int steps = 40;
            double stepBytes = Math.Max(item.TotalBytes, 1) / (double)steps;
            for (int i = 1; i <= steps; i++)
            {
                await Task.Delay(100, cts.Token);
                int n = i;
                _ui.Send(_ => { item.Progress = n / (double)steps; item.BytesPerSecond = stepBytes / 0.1; }, null);
            }
            _ui.Send(_ =>
            {
                item.BytesPerSecond = 0;
                item.SavedPath = savedPath;
                item.Status = TransferStatus.Completed;
            }, null);
        }
        catch (OperationCanceledException)
        {
            _ui.Send(_ => { item.BytesPerSecond = 0; item.Status = TransferStatus.Cancelled; }, null);
        }
        finally
        {
            lock (_running) _running.Remove(item.Id);
        }
    }
}
