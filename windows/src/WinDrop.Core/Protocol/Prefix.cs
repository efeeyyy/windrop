namespace WinDrop.Core.Protocol;

/// <summary>Protokol katmanı (ön ek, çerçeveleme, parçalama, dosya adı) hatası.</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>Taşıma türü (PROTOCOL §1 ön ek, 4. bayt).</summary>
public enum TransportKind : byte
{
    Ble = 1,
    Tcp = 2,
    Relay = 3,
}

/// <summary>
/// PROTOCOL §1 açık ön ek (20 bayt, şifresiz): <c>"WD" || sürüm (1) || taşıma (1) || transferId (16)</c>.
/// Aynı 20 bayt Noise prologue olarak da kullanılır (<see cref="Bytes"/>). BLE'de transferId 16 sıfır bayttır.
/// <para>PROTOCOL §9a: Ön ek BLE'de parçalanmaz, <c>c2p</c>'ye tek (20 bayt = en küçük ATT yükü, MTU 23) ham yazma
/// olarak gider; spesifikasyon "parçalama birimi tek Noise mesajıdır" der, ön eki açıkça dışarıda bırakmaz.</para>
/// <para>PROTOCOL §9a: Sürüm uyuşmazlığı yanıtı <c>0xFF || sürüm</c> iki bayttır ("tek bayt 0xFF || sürüm" ifadesi
/// 0xFF'in tek bayt başlık olduğu biçiminde okundu). Alıcı bunu TCP'de ilk baytın 0xFF olmasından
/// (gerçek bir el sıkışma yanıtının uzunluk üst baytı en çok 0x00'dır) ve BLE'de ilk parça başlığının
/// 0x80 olmamasından (ilk mesajın sıra no 0 + son parça = 0x80/0x00) ayırt eder.</para>
/// </summary>
public readonly struct Prefix : IEquatable<Prefix>
{
    public const int Length = 20;
    public const int TransferIdLength = 16;
    /// <summary>Bu uygulamanın konuştuğu protokol sürümü.</summary>
    public const byte CurrentVersion = 1;
    /// <summary>Sürüm uyuşmazlığı yanıtının ilk baytı.</summary>
    public const byte VersionMismatchMarker = 0xFF;

    private readonly byte[]? _transferId;

    public byte Version { get; }
    public TransportKind Transport { get; }

    /// <summary>16 baytlık transferId (kopya).</summary>
    public byte[] TransferId => _transferId is null ? new byte[TransferIdLength] : (byte[])_transferId.Clone();

    public Prefix(byte version, TransportKind transport, ReadOnlySpan<byte> transferId)
    {
        if (!Enum.IsDefined(transport))
            throw new ArgumentException("Bilinmeyen taşıma türü.", nameof(transport));
        if (transferId.Length != TransferIdLength)
            throw new ArgumentException("transferId 16 bayt olmalı.", nameof(transferId));
        if (transport == TransportKind.Ble && transferId.ContainsAnyExcept((byte)0))
            throw new ArgumentException("BLE ön ekinde transferId 16 sıfır bayt olmalı.", nameof(transferId));
        Version = version;
        Transport = transport;
        _transferId = transferId.ToArray();
    }

    /// <summary>Şu anki sürümle ön ek.</summary>
    public static Prefix Create(TransportKind transport, ReadOnlySpan<byte> transferId) =>
        new(CurrentVersion, transport, transferId);

    public static Prefix Create(TransportKind transport, TransferId transferId) =>
        new(CurrentVersion, transport, transferId.Span);

    /// <summary>BLE ön eki (transferId = 16 sıfır).</summary>
    public static Prefix CreateBle() => new(CurrentVersion, TransportKind.Ble, new byte[TransferIdLength]);

    /// <summary>20 baytlık kodlama; aynı bayt dizisi prologue'dur.</summary>
    public byte[] Bytes
    {
        get
        {
            var b = new byte[Length];
            b[0] = (byte)'W';
            b[1] = (byte)'D';
            b[2] = Version;
            b[3] = (byte)Transport;
            (_transferId ?? new byte[TransferIdLength]).CopyTo(b, 4);
            return b;
        }
    }

    /// <summary>Ön eki çözer. Bozuk (uzunluk, "WD", taşıma türü, BLE'de sıfır olmayan transferId) → <see cref="ProtocolException"/>.
    /// Sürümü <b>denetlemez</b>: uyuşmazlığı çağıran <see cref="IsVersionSupported"/> ile yönetir.</summary>
    public static Prefix Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != Length)
            throw new ProtocolException($"Ön ek {Length} bayt olmalı ({data.Length} geldi).");
        if (data[0] != (byte)'W' || data[1] != (byte)'D')
            throw new ProtocolException("Ön ek 'WD' ile başlamıyor.");
        var transport = (TransportKind)data[3];
        if (!Enum.IsDefined(transport))
            throw new ProtocolException($"Bilinmeyen taşıma türü: {data[3]}.");
        if (transport == TransportKind.Ble && data[4..].ContainsAnyExcept((byte)0))
            throw new ProtocolException("BLE ön ekinde transferId sıfır olmalı.");
        return new Prefix(data[2], transport, data[4..]);
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out Prefix prefix)
    {
        try
        {
            prefix = Decode(data);
            return true;
        }
        catch (ProtocolException)
        {
            prefix = default;
            return false;
        }
    }

    public bool IsVersionSupported(byte supportedVersion) => Version == supportedVersion;

    public bool Equals(Prefix other) => Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is Prefix p && Equals(p);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public static bool operator ==(Prefix left, Prefix right) => left.Equals(right);

    public static bool operator !=(Prefix left, Prefix right) => !left.Equals(right);

    /// <summary>Yanıtlayanın sürüm uyuşmazlığında el sıkışmadan önce gönderdiği 2 bayt: <c>0xFF || desteklenen sürüm</c>.</summary>
    public static byte[] EncodeVersionMismatch(byte supportedVersion = CurrentVersion) =>
        [VersionMismatchMarker, supportedVersion];

    /// <summary>Başlatan tarafı: gelen ilk baytlar sürüm uyuşmazlığı yanıtı mı? Uzunluk &lt; 2 veya ilk bayt 0xFF değilse false.</summary>
    public static bool TryDecodeVersionMismatch(ReadOnlySpan<byte> data, out byte supportedVersion)
    {
        if (data.Length == 2 && data[0] == VersionMismatchMarker)
        {
            supportedVersion = data[1];
            return true;
        }
        supportedVersion = 0;
        return false;
    }
}
