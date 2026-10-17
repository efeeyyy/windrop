namespace WinDrop.Core.Protocol;

/// <summary>
/// PROTOCOL §5 TCP çerçevesi: <c>uzunluk (2 B big-endian) || Noise mesajı</c> (1..65535 bayt).
/// Hem el sıkışma mesajları hem şifreli taşıma mesajları bu biçimle gider; ön ek (20 bayt) çerçevesizdir.
/// <para>PROTOCOL §9a: Sıfır uzunluklu çerçeve geçersiz sayılır (en küçük Noise mesajı bir etiket/anahtar taşır).</para>
/// </summary>
public static class TcpFraming
{
    public const int LengthPrefixSize = 2;
    public const int MaxNoiseMessage = 65535;

    /// <summary>Tek bir Noise mesajını çerçeveleyip yazar (uzunluk + mesaj tek yazmada).</summary>
    public static async ValueTask WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> noiseMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (noiseMessage.Length == 0) throw new ProtocolException("Boş Noise mesajı gönderilemez.");
        if (noiseMessage.Length > MaxNoiseMessage)
            throw new ProtocolException($"Noise mesajı {MaxNoiseMessage} baytı aşıyor ({noiseMessage.Length}).");

        var buffer = new byte[LengthPrefixSize + noiseMessage.Length];
        buffer[0] = (byte)(noiseMessage.Length >> 8);
        buffer[1] = (byte)noiseMessage.Length;
        noiseMessage.CopyTo(buffer.AsMemory(LengthPrefixSize));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bir Noise mesajı okur. Akış çerçeve sınırında temiz biçimde bittiyse <c>null</c> döner;
    /// çerçeve ortasında bitmişse (uzunluk önekinin 1 baytı veya gövdenin eksik kalması) veya uzunluk 0 ise
    /// <see cref="ProtocolException"/> fırlatır. Kısa okumalar (parça parça gelen TCP verisi) döngüyle tamamlanır.
    /// </summary>
    public static ValueTask<byte[]?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(stream, detectVersionMismatch: false, cancellationToken);

    /// <summary>
    /// El sıkışma aşamasındaki okuma: ilk bayt 0xFF ise bu bir uzunluk öneki değil, §1 sürüm uyuşmazlığı yanıtıdır
    /// (<c>0xFF || sürüm</c>; el sıkışma mesajları küçüktür, uzunluk üst baytı 0x00'dır) →
    /// <see cref="VersionMismatchException"/> (yerel sürüm 0 = bilinmiyor).
    /// </summary>
    public static ValueTask<byte[]?> ReadHandshakeMessageAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(stream, detectVersionMismatch: true, cancellationToken);

    private static async ValueTask<byte[]?> ReadCoreAsync(Stream stream, bool detectVersionMismatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[LengthPrefixSize];
        var got = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < LengthPrefixSize) throw new ProtocolException("Akış uzunluk öneki ortasında bitti.");
        if (detectVersionMismatch && header[0] == Prefix.VersionMismatchMarker)
            throw new VersionMismatchException(0, header[1]);

        var length = (header[0] << 8) | header[1];
        if (length == 0) throw new ProtocolException("Sıfır uzunluklu çerçeve.");

        var message = new byte[length];
        got = await ReadFullyAsync(stream, message, cancellationToken).ConfigureAwait(false);
        if (got < length) throw new ProtocolException($"Akış çerçeve gövdesi ortasında bitti ({got}/{length}).");
        return message;
    }

    /// <summary>Tam <paramref name="buffer"/> kadar okur; akış erken biterse okunan bayt sayısını döndürür.</summary>
    internal static async ValueTask<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Tam <paramref name="count"/> bayt okur (ör. 20 baytlık ön ek). Eksik biterse <see cref="ProtocolException"/>.</summary>
    public static async ValueTask<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[count];
        var got = await ReadFullyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (got < count) throw new ProtocolException($"Akış erken bitti ({got}/{count}).");
        return buffer;
    }
}
