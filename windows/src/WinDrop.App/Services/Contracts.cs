using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinDrop.App.Services;

/// <summary>Eşli cihaz. <see cref="Name"/> eşleştirme anındaki addır.</summary>
/// <param name="CanPush">PROTOCOL §6.5: yakında değil ama push bildirimiyle uyandırılabilir (pushId kayıtlı + aktarma sunucusu adresi dolu).</param>
public sealed record DeviceInfo(string Id, string Name, bool IsNearby, bool CanPush = false);

/// <summary>Açık eşleştirme oturumu: QR yükü + süre; <see cref="Cancel"/> eşleştirme modunu kapatır.</summary>
public sealed class PairingSession
{
    private readonly CancellationTokenSource _cts = new();

    public PairingSession(string qrPayload, TimeSpan duration)
    {
        QrPayload = qrPayload;
        Duration = duration;
        ExpiresAt = DateTimeOffset.UtcNow + duration;
    }

    public string QrPayload { get; }
    public TimeSpan Duration { get; }
    public DateTimeOffset ExpiresAt { get; }
    public CancellationToken Token => _cts.Token;
    public bool IsCancelled => _cts.IsCancellationRequested;
    public void Cancel() => _cts.Cancel();
}

/// <summary>iPhone'dan gelen eşleştirme isteği (PC'de onaylanır).</summary>
public sealed record PairingRequest(string RequestId, string DeviceName, string? VerificationCode = null);

public interface IDeviceService
{
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>Liste veya yakınlık değişti.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Eşleştirme modunu açar (QR ekrandayken). İptal/zaman aşımı: <see cref="PairingSession.Cancel"/>.</summary>
    Task<PairingSession> StartPairingAsync();

    event EventHandler<PairingRequest>? PairingRequested;

    void RespondToPairing(PairingRequest request, bool accept);

    void RemoveDevice(string deviceId);
}

public enum TransferDirection { Send, Receive }

public enum TransferStatus { Waiting, Active, Completed, Failed, Cancelled, Rejected }

/// <summary>Bir aktarımın arayüze yansıyan durumu; servis günceller (arayüz iş parçacığında).</summary>
public sealed partial class TransferItem : ObservableObject
{
    public TransferItem(string id, TransferDirection direction, string deviceName, int fileCount, long totalBytes)
    {
        Id = id;
        Direction = direction;
        DeviceName = deviceName;
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }

    public string Id { get; }
    public TransferDirection Direction { get; }
    public string DeviceName { get; }
    public int FileCount { get; }
    public long TotalBytes { get; }

    /// <summary>0..1</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial double BytesPerSecond { get; set; }
    [ObservableProperty] public partial TransferStatus Status { get; set; } = TransferStatus.Waiting;

    /// <summary>Satır önizlemesi için ilk dosya/klasör yolu (gönderen: seçilen ilk öğe; alıcı: kaydedilen ilk dosya).</summary>
    [ObservableProperty] public partial string? PreviewPath { get; set; }

    /// <summary>Alınan dosya/klasör yolu (tamamlanınca; "Klasörde göster" için).</summary>
    [ObservableProperty] public partial string? SavedPath { get; set; }

    /// <summary>Başarısız/iptal/reddedilen aktarımın anlaşılır nedeni (Türkçe).</summary>
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    /// <summary>Telefona push bildirimi gönderildi, telefonun bağlanması bekleniyor (PROTOCOL §6.5).</summary>
    [ObservableProperty] public partial bool PushPending { get; set; }
}

/// <summary>Eşli cihazdan gelen teklif; gönderen adı eşleştirme kaydından gelir.</summary>
/// <param name="VerificationCode">Eşleştirmesiz (XX) oturumda 4 haneli doğrulama kodu (PROTOCOL §2b); yoksa null.</param>
public sealed record IncomingOffer(string OfferId, string SenderName, int FileCount, long TotalBytes, string? VerificationCode = null,
    IReadOnlyList<string>? Preview = null);

/// <summary>Aktarma sunucusu (internet/hücresel veri) yolu için kullanıcı onayı (PROTOCOL §6.3).</summary>
public sealed record RelayApproval(string TransferId, string DeviceName, long TotalBytes);

public interface ITransferService
{
    ObservableCollection<TransferItem> Transfers { get; }

    Task SendAsync(string deviceId, IReadOnlyList<string> paths);

    void Cancel(TransferItem transfer);

    event EventHandler<IncomingOffer>? OfferReceived;

    void RespondToOffer(IncomingOffer offer, bool accept);

    /// <summary>Doğrudan yol/etkin nokta/Bluetooth yok; aktarma sunucusu kullanılacak — onay bekler.</summary>
    event EventHandler<RelayApproval>? RelayApprovalRequested;

    void RespondToRelay(RelayApproval approval, bool allow);

    void ShowInFolder(TransferItem transfer);
}
