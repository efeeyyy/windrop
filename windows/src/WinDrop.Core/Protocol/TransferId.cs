using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDrop.Core.Protocol;

/// <summary>
/// 16 baytlık aktarım kimliği (PROTOCOL §1 ön ek, §4 JSON'da 16 B base64url, dolgusuz).
/// Değişmezdir; sözlük anahtarı olarak kullanılabilir.
/// </summary>
[JsonConverter(typeof(TransferIdJsonConverter))]
public sealed class TransferId : IEquatable<TransferId>
{
    public const int Length = 16;

    private readonly byte[] _bytes;

    public TransferId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length) throw new ArgumentException("transferId 16 bayt olmalı.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public static TransferId Create() => new(RandomNumberGenerator.GetBytes(Length));

    public ReadOnlySpan<byte> Span => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    /// <summary>base64url (dolgusuz), 22 karakter.</summary>
    public override string ToString() => Base64Url.EncodeToString(_bytes);

    public static TransferId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new ProtocolException("Geçersiz transferId (16 B base64url bekleniyor).");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TransferId? id)
    {
        id = null;
        if (text is null || text.Length != 22) return false;   // 16 bayt → 22 base64url karakter
        Span<byte> buffer = stackalloc byte[Length + 2];
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, buffer, out var written) || written != Length) return false;
            id = new TransferId(buffer[..Length]);
            return true;
        }
        catch (FormatException)
        {
            return false;   // geçersiz karakter (BCL "Try" yine de fırlatabiliyor)
        }
    }

    public bool Equals(TransferId? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => Equals(obj as TransferId);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(_bytes);
        return h.ToHashCode();
    }
}

internal sealed class TransferIdJsonConverter : JsonConverter<TransferId>
{
    public override TransferId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !TransferId.TryParse(reader.GetString(), out var id))
            throw new JsonException("transferId 16 B base64url olmalı.");
        return id;
    }

    public override void Write(Utf8JsonWriter writer, TransferId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
