namespace WinDrop.Core.Protocol;

/// <summary>
/// PROTOCOL §1 sürüm uyuşmazlığı: yanıtlayan <c>0xFF || desteklenen sürüm</c> gönderip kapatır;
/// kullanıcıya "güncelleme gerekli" gösterilebilsin diye hem başlatan hem yanıtlayan tarafta fırlatılır.
/// </summary>
public sealed class VersionMismatchException : ProtocolException
{
    /// <summary>Yerel sürüm (taşıma katmanı bilmiyorsa 0: üst katman doldurur).</summary>
    public byte LocalVersion { get; }
    /// <summary>Karşı tarafın sürümü: başlatanda yanıtlayanın desteklediği, yanıtlayanda başlatanın sunduğu.</summary>
    public byte PeerVersion { get; }

    public VersionMismatchException(byte localVersion, byte peerVersion)
        : base($"Protokol sürümü uyuşmuyor (yerel {localVersion}, karşı {peerVersion}).")
    {
        LocalVersion = localVersion;
        PeerVersion = peerVersion;
    }
}
