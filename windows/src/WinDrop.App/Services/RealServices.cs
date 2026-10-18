using System.Collections.ObjectModel;
using WinDrop.App.Infrastructure;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Platform;

namespace WinDrop.App.Services;

/// <summary>Gerçek cihaz servisi: eşli cihazlar, yakınlık (BLE oturumu), eşleştirme (<see cref="PairingService"/>).</summary>
public sealed class DeviceService : IDeviceService
{
    private readonly WinDropRuntime _rt;
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();

    public DeviceService(WinDropRuntime runtime)
    {
        _rt = runtime;
        _rt.Coordinator.PeersChanged += RaiseChanged;
        _rt.Pairing.DeviceTrusted += _ => RaiseChanged();
        _rt.Pairing.PairingRequested += r => _ui.Post(_ => PairingRequested?.Invoke(this, new PairingRequest(r.RequestId, r.DeviceName, r.VerificationCode)), null);
        _rt.Pairing.AlreadyPaired += n => _ui.Post(_ => AlreadyPaired?.Invoke(this, n), null);
    }

    /// <summary>Eşli + tanıdık cihazlar ve (PROTOCOL §2b) yakındaki bilinmeyen iPhone'lar (iPhone'un kurduğu BLE bağlantısı + hello).</summary>
    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            var list = _rt.Trusted.List()
                .Select(d =>
                {
                    var id = DeviceId.ToHex(d.DeviceId);
                    return new DeviceInfo(id, d.Name, _rt.Coordinator.IsNearby(id), _rt.Coordinator.CanPush(id));
                })
                .ToList();
            foreach (var n in _rt.Coordinator.NearbyPeers)
            {
                if (list.All(d => d.Id != n.PeerId)) list.Add(new DeviceInfo(n.PeerId, n.Name, true, IsTrusted: false));
            }
            return list;
        }
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<PairingRequest>? PairingRequested;
    public event EventHandler<string>? AlreadyPaired;

    private void RaiseChanged() => _ui.Post(_ => DevicesChanged?.Invoke(this, EventArgs.Empty), null);

    public Task<PairingSession> StartPairingAsync()
    {
        var mode = _rt.Pairing.Start();
        var session = new PairingSession(mode.QrPayload, mode.Duration);
        session.Token.Register(mode.Cancel);
        return Task.FromResult(session);
    }

    public void RespondToPairing(PairingRequest request, bool accept) => _rt.Pairing.Respond(request.RequestId, accept);

    public void RemoveDevice(string deviceId)
    {
        var device = _rt.Trusted.List().FirstOrDefault(d => DeviceId.ToHex(d.DeviceId) == deviceId);
        if (device is null) return;
        _rt.Trusted.Remove(device.PublicKey);
        _rt.Coordinator.DropPeer(deviceId);
        AppLog.Write($"eşleştirme kaldırıldı: {device.Name}");
        RaiseChanged();
    }
}

/// <summary>Gerçek aktarım servisi: <see cref="TransferCoordinator"/> olaylarını arayüz modeline (UI iş parçacığında) yansıtır.</summary>
public sealed class TransferService : ITransferService
{
    private readonly TransferCoordinator _coord;
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly Dictionary<string, TransferItem> _items = [];   // yalnız UI iş parçacığında

    public TransferService(WinDropRuntime runtime)
    {
        _coord = runtime.Coordinator;
        _coord.TransferUpdated += snap => _ui.Post(_ => Apply(snap), null);
        _coord.OfferReceived += o => _ui.Post(_ =>
            OfferReceived?.Invoke(this, new IncomingOffer(o.Id.ToString(), o.PeerName, o.FileCount, o.TotalBytes, o.VerificationCode, o.Preview)), null);
        _coord.RelayApprovalRequested += r => _ui.Post(_ =>
            RelayApprovalRequested?.Invoke(this, new RelayApproval(r.Id.ToString(), r.PeerName, r.TotalBytes)), null);
    }

    public ObservableCollection<TransferItem> Transfers { get; } = [];

    public event EventHandler<IncomingOffer>? OfferReceived;

    public event EventHandler<RelayApproval>? RelayApprovalRequested;

    /// <summary>Hemen döner; ilerleme/sonuç satırda görünür. Eşzamanlı sorunlar (cihaz yakında değil, dosya yok) istisna olur.</summary>
    public Task SendAsync(string deviceId, IReadOnlyList<string> paths)
    {
        var snap = _coord.Send(deviceId, paths);
        AppLog.Write($"gönder: {paths.Count} dosya → {snap.PeerName} ({snap.Id})");
        Apply(snap);
        if (paths.Count > 0 && _items.TryGetValue(snap.Id.ToString(), out var sent)) sent.PreviewPath = paths[0];
        return Task.CompletedTask;
    }

    public void Cancel(TransferItem transfer)
    {
        if (TransferId.TryParse(transfer.Id, out var id)) _coord.Cancel(id);
    }

    public void RespondToOffer(IncomingOffer offer, bool accept)
    {
        if (TransferId.TryParse(offer.OfferId, out var id)) _coord.RespondToOffer(id, accept);
    }

    public void RespondToRelay(RelayApproval approval, bool allow)
    {
        if (TransferId.TryParse(approval.TransferId, out var id)) _coord.RespondToRelay(id, allow);
    }

    public void ShowInFolder(TransferItem transfer) => Explorer.Reveal(transfer.SavedPath);

    private void Apply(TransferSnapshot s)
    {
        var key = s.Id.ToString();
        if (!_items.TryGetValue(key, out var item))
        {
            // Alıcı tarafta kullanıcı onayı beklerken (veya reddettikten sonra) listeye satır eklenmez.
            if (s.Role == TransferRole.Receiver && s.Phase is TransferPhase.Negotiating or TransferPhase.AwaitingApproval or TransferPhase.Rejected)
                return;
            item = new TransferItem(key,
                s.Role == TransferRole.Sender ? TransferDirection.Send : TransferDirection.Receive,
                s.PeerName, s.FileCount, s.TotalBytes);
            _items[key] = item;
            Transfers.Insert(0, item);
        }
        item.Progress = s.Progress;
        item.BytesPerSecond = s.BytesPerSecond;
        item.ErrorMessage = s.Error;
        item.PushPending = s.Note == TransferNote.PushSent;
        if (s.SavedPath is not null) item.SavedPath = s.SavedPath;
        if (s.FirstFilePath is not null) item.PreviewPath = s.FirstFilePath;
        else if (item.PreviewPath is null && s.SavedPath is not null) item.PreviewPath = s.SavedPath;
        item.Status = s.Phase switch
        {
            TransferPhase.Active => TransferStatus.Active,
            TransferPhase.Completed => TransferStatus.Completed,
            TransferPhase.Failed => TransferStatus.Failed,
            TransferPhase.Canceled => TransferStatus.Cancelled,
            TransferPhase.Rejected => TransferStatus.Rejected,
            _ => TransferStatus.Waiting,
        };
        if (s.IsTerminal) AppLog.Write($"aktarım {key}: {s.Phase}{(s.Error is null ? "" : " — " + s.Error)}");
    }
}
