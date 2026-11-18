namespace WinDrop.Core.Protocol;

/// <summary>
/// PROTOCOL §4.2 BLE parçalama: bir Noise mesajı (en çok 8192 bayt) GATT yazma/bildirim yüklerine bölünür.
/// Yük = <c>1 B başlık || parça</c>; başlık bit7 = son parça, bit0–6 = mesaj sıra no (mod 128).
/// <para>PROTOCOL §4.2: "mesaj sıra no" harfi harfine okundu: <b>bir mesajın tüm parçaları aynı sıra no'yu taşır</b>,
/// no mesaj başına bir artar (parça başına değil). Alternatif okuma (her parça için artan sayaç) iOS ile
/// uyuşmazlık demektir; netleştirilmeli. Parça kaybı bu okumada sıra no ile değil Noise etiketi/uzunluğuyla
/// yakalanır, tüm bir mesajın kaybı sıra no atlamasıyla.</para>
/// <para>PROTOCOL §4.2: <c>mtu</c> ATT MTU'dur (en az 23); tek GATT yükü en çok <c>mtu − 3</c> bayt, dolayısıyla parça
/// verisi en çok <c>mtu − 4</c> bayttır. Her yönün sıra sayacı 0'dan başlar ve her yeni oturumda (yeni
/// el sıkışma) sıfırlanır; 20 baytlık ön ek parçalanmaz ve sayılmaz.</para>
/// </summary>
public static class BleFragmenter
{
    public const int MaxMessageSize = 8192;
    public const int MinMtu = 23;
    public const int AttOverhead = 3;
    public const int HeaderSize = 1;
    public const int SequenceModulo = 128;
    private const byte LastFragmentFlag = 0x80;
    private const byte SequenceMask = 0x7F;

    /// <summary>MTU'ya göre tek parçadaki en çok mesaj baytı.</summary>
    public static int MaxFragmentData(int mtu)
    {
        if (mtu < MinMtu) throw new ArgumentOutOfRangeException(nameof(mtu), $"ATT MTU en az {MinMtu} olmalı.");
        return mtu - AttOverhead - HeaderSize;
    }

    /// <summary>Mesajı GATT yüklerine böler. Boş veya 8192'den büyük mesaj hatadır.</summary>
    public static IReadOnlyList<byte[]> Fragment(ReadOnlySpan<byte> message, int mtu, int sequence)
    {
        var chunk = MaxFragmentData(mtu);
        if (message.Length == 0) throw new ProtocolException("Boş mesaj parçalanamaz.");
        if (message.Length > MaxMessageSize)
            throw new ProtocolException($"Mesaj {MaxMessageSize} baytı aşıyor ({message.Length}).");
        var seq = ((sequence % SequenceModulo) + SequenceModulo) % SequenceModulo;

        var count = (message.Length + chunk - 1) / chunk;
        var result = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var part = message.Slice(i * chunk, Math.Min(chunk, message.Length - i * chunk));
            var payload = new byte[HeaderSize + part.Length];
            payload[0] = (byte)(seq | (i == count - 1 ? LastFragmentFlag : 0));
            part.CopyTo(payload.AsSpan(HeaderSize));
            result.Add(payload);
        }
        return result;
    }

    /// <summary>Tek bir mesajın tüm parçalarını birleştirir (saf yardımcı); eksik/fazla parça veya sıra hatasında <see cref="ProtocolException"/>.</summary>
    public static byte[] Reassemble(IEnumerable<byte[]> fragments, int expectedSequence = 0)
    {
        var r = new BleReassembler(expectedSequence);
        byte[]? message = null;
        foreach (var f in fragments)
        {
            if (message is not null) throw new ProtocolException("Son parçadan sonra fazladan parça.");
            message = r.Push(f);
        }
        return message ?? throw new ProtocolException("Mesaj tamamlanmadı (son parça yok).");
    }

    internal static (bool Last, int Sequence) ParseHeader(byte header) =>
        ((header & LastFragmentFlag) != 0, header & SequenceMask);
}

/// <summary>
/// Bir yöndeki GATT yüklerinden Noise mesajlarını birleştiren durum makinesi (G/Ç yok).
/// Herhangi bir hata sonrası nesne kalıcı olarak bozulur: PROTOCOL §4.2 gereği oturum düşer, yeni el sıkışma gerekir.
/// </summary>
public sealed class BleReassembler
{
    private readonly List<byte> _buffer = [];
    private int _expectedSequence;
    private int _currentSequence = -1;
    private bool _faulted;

    public BleReassembler(int firstSequence = 0)
    {
        _expectedSequence = ((firstSequence % BleFragmenter.SequenceModulo) + BleFragmenter.SequenceModulo) % BleFragmenter.SequenceModulo;
    }

    /// <summary>Sıradaki mesajdan beklenen sıra no.</summary>
    public int ExpectedSequence => _expectedSequence;

    /// <summary>Bir GATT yükü verir; mesaj tamamlandıysa birleşmiş mesajı, değilse null döndürür.</summary>
    public byte[]? Push(ReadOnlySpan<byte> gattPayload)
    {
        if (_faulted) throw new ProtocolException("Parça birleştirici daha önce hata verdi; oturum düşürülmeli.");
        try
        {
            return PushCore(gattPayload);
        }
        catch (ProtocolException)
        {
            _faulted = true;
            throw;
        }
    }

    private byte[]? PushCore(ReadOnlySpan<byte> gattPayload)
    {
        if (gattPayload.Length <= BleFragmenter.HeaderSize)
            throw new ProtocolException("Parça boş (yalnız başlık veya hiç bayt yok).");
        var (last, seq) = BleFragmenter.ParseHeader(gattPayload[0]);

        if (_currentSequence < 0)
        {
            if (seq != _expectedSequence)
                throw new ProtocolException($"Sıra no atlaması: beklenen {_expectedSequence}, gelen {seq}.");
            _currentSequence = seq;
        }
        else if (seq != _currentSequence)
        {
            throw new ProtocolException($"Mesaj ortasında sıra no değişti: {_currentSequence} → {seq}.");
        }

        if (_buffer.Count + gattPayload.Length - BleFragmenter.HeaderSize > BleFragmenter.MaxMessageSize)
            throw new ProtocolException($"Mesaj {BleFragmenter.MaxMessageSize} baytı aşıyor.");
        _buffer.AddRange(gattPayload[BleFragmenter.HeaderSize..].ToArray());

        if (!last) return null;
        var message = _buffer.ToArray();
        _buffer.Clear();
        _currentSequence = -1;
        _expectedSequence = (seq + 1) % BleFragmenter.SequenceModulo;
        return message;
    }
}
