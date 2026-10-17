using WinDrop.Core.Protocol;

namespace WinDrop.Core.Session;

/// <summary>
/// §5 çerçevelerini yollayıp alan kanal: <c>FileSender</c>/<c>FileReceiver</c> yalnız buna dayanır.
/// <see cref="SecureChannel"/> (TCP/aktarma/BLE oturumunun kendisi) ve BLE sinyal oturumu üstünde çerçeve ayıran
/// uyarlayıcı (PROTOCOL §6.4) bunu uygular.
/// </summary>
public interface IFrameChannel
{
    /// <summary>DATA gövdesi üst sınırı (tür baytı düşülmüş). BLE'de 8192 − 16 − 1 = 8175.</summary>
    int MaxFrameBodySize { get; }

    /// <summary>Yanıtlayanda ilk geçerli taşıma mesajı gelene kadar bekler (§1 0-RTT yok).</summary>
    ValueTask EnsureEstablishedAsync(CancellationToken cancellationToken = default);

    /// <summary>Hazır kodlanmış çerçeveyi (<see cref="FrameCodec"/>) yollar.</summary>
    ValueTask SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, CancellationToken cancellationToken = default);

    /// <summary>Sıradaki çerçeve; karşı taraf temiz kapattıysa null. Sinyal gelirse <see cref="ProtocolException"/>.</summary>
    ValueTask<Frame?> ReceiveFrameAsync(CancellationToken cancellationToken = default);
}
