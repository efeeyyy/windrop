using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinDrop.Core.Protocol;

/// <summary>
/// Veri kanalı çerçeve türü (PROTOCOL §5). PROTOCOL §9a: Spesifikasyon tür baytının sayısal değerlerini vermiyor;
/// aşağıdaki değerler 1'den başlayıp §5'teki sıra ile atandı. iOS tarafı aynı tabloyu kullanmalı.
/// </summary>
public enum FrameType : byte
{
    Manifest = 1,
    Resume = 2,
    FileStart = 3,
    Data = 4,
    FileEnd = 5,
    Ack = 6,
    Cancel = 7,
    Done = 8,
}

// JSON kontrol gövdeleri (§5). PROTOCOL §9a: alan tipleri belirtilmedi — id: tamsayı (int), size: bayt (long),
// mtime: Unix epoch milisaniye (long; yeniden başlatmada "mtime değişti mi" karşılaştırması için
// alt saniye çözünürlüğü korunur), sha256: 64 karakterlik küçük harf onaltılık dize, offset: bayt (long).
// Alan adları spesifikasyondaki gibi küçük harf/camelCase. Spesifikasyondaki alanlar zorunludur.

/// <summary>MANIFEST.files[] öğesi.</summary>
public sealed record ManifestFile(
    [property: JsonRequired] int Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] long Size,
    [property: JsonRequired] long Mtime);

/// <summary>MANIFEST (gönderen, ilk çerçeve; sayfalıysa <c>more=true</c> sayfalar ardışık MANIFEST çerçeveleridir).</summary>
public sealed record ManifestBody(
    [property: JsonRequired] IReadOnlyList<ManifestFile> Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool More = false);

/// <summary>RESUME {id, offset} (alıcı → gönderen).</summary>
public sealed record ResumeBody([property: JsonRequired] int Id, [property: JsonRequired] long Offset);

/// <summary>FILE_START {id, offset} (gönderen).</summary>
public sealed record FileStartBody([property: JsonRequired] int Id, [property: JsonRequired] long Offset);

/// <summary>FILE_END {id, sha256} (gönderen; 0. bayttan itibaren tüm dosyanın SHA-256'sı).</summary>
public sealed record FileEndBody([property: JsonRequired] int Id, [property: JsonRequired] string Sha256);

/// <summary>ACK {id, offset} (alıcı).</summary>
public sealed record AckBody([property: JsonRequired] int Id, [property: JsonRequired] long Offset);

/// <summary>CANCEL. PROTOCOL §9a: alan listelenmedi; gövde boş nesne <c>{}</c> olabilir, isteğe bağlı <c>reason</c> taşır.</summary>
public sealed record CancelBody(string? Reason = null);

/// <summary>DONE {ok, failed: [id]} (alıcı → gönderen, sonuç).</summary>
public sealed record DoneBody([property: JsonRequired] bool Ok, [property: JsonRequired] IReadOnlyList<int> Failed);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ManifestBody))]
[JsonSerializable(typeof(ManifestFile))]
[JsonSerializable(typeof(ResumeBody))]
[JsonSerializable(typeof(FileStartBody))]
[JsonSerializable(typeof(FileEndBody))]
[JsonSerializable(typeof(AckBody))]
[JsonSerializable(typeof(CancelBody))]
[JsonSerializable(typeof(DoneBody))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;

/// <summary>Çözülmüş düz metin çerçeve: tür + gövde.</summary>
public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Body);

/// <summary>
/// §5 düz metin çerçeve kodlaması: <c>tür (1 B) || gövde</c>, en çok 65519 B; Noise şifrelemesi
/// çağıranda (etiket +16 → en çok 65535). Kontrol gövdeleri UTF-8 JSON, DATA ham bayttır.
/// </summary>
public static class FrameCodec
{
    public const int MaxPlaintext = 65519;
    public const int MaxBody = MaxPlaintext - 1;   // 65518: DATA ve kontrol gövdesi üst sınırı

    public static byte[] Encode(FrameType type, ReadOnlySpan<byte> body)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentException("Bilinmeyen çerçeve türü.", nameof(type));
        if (body.Length > MaxBody)
            throw new ProtocolException($"Çerçeve gövdesi {MaxBody} baytı aşıyor ({body.Length}).");
        var frame = new byte[1 + body.Length];
        frame[0] = (byte)type;
        body.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] EncodeData(ReadOnlySpan<byte> data) => Encode(FrameType.Data, data);

    public static byte[] Encode(ManifestBody body) => EncodeControl(FrameType.Manifest, body, ControlJsonContext.Default.ManifestBody);
    public static byte[] Encode(ResumeBody body) => EncodeControl(FrameType.Resume, body, ControlJsonContext.Default.ResumeBody);
    public static byte[] Encode(FileStartBody body) => EncodeControl(FrameType.FileStart, body, ControlJsonContext.Default.FileStartBody);
    public static byte[] Encode(FileEndBody body) => EncodeControl(FrameType.FileEnd, body, ControlJsonContext.Default.FileEndBody);
    public static byte[] Encode(AckBody body) => EncodeControl(FrameType.Ack, body, ControlJsonContext.Default.AckBody);
    public static byte[] Encode(CancelBody body) => EncodeControl(FrameType.Cancel, body, ControlJsonContext.Default.CancelBody);
    public static byte[] Encode(DoneBody body) => EncodeControl(FrameType.Done, body, ControlJsonContext.Default.DoneBody);

    /// <summary>Düz metin çerçeveyi türüne ve gövdesine ayırır. Boş, bilinmeyen tür veya aşırı büyük → <see cref="ProtocolException"/>.</summary>
    public static Frame Decode(ReadOnlyMemory<byte> plaintext)
    {
        if (plaintext.Length == 0) throw new ProtocolException("Boş çerçeve.");
        if (plaintext.Length > MaxPlaintext)
            throw new ProtocolException($"Çerçeve {MaxPlaintext} baytı aşıyor ({plaintext.Length}).");
        var type = (FrameType)plaintext.Span[0];
        if (!Enum.IsDefined(type)) throw new ProtocolException($"Bilinmeyen çerçeve türü: {plaintext.Span[0]}.");
        return new Frame(type, plaintext[1..]);
    }

    public static ManifestBody DecodeManifest(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.ManifestBody);
    public static ResumeBody DecodeResume(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.ResumeBody);
    public static FileStartBody DecodeFileStart(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.FileStartBody);
    public static FileEndBody DecodeFileEnd(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.FileEndBody);
    public static AckBody DecodeAck(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.AckBody);
    public static CancelBody DecodeCancel(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.CancelBody);
    public static DoneBody DecodeDone(ReadOnlySpan<byte> body) => DecodeControl(body, ControlJsonContext.Default.DoneBody);

    /// <summary>Bir dosya girdisinin MANIFEST JSON'undaki bayt boyutu (sayfalama ölçümü için).</summary>
    public static int MeasureManifestFile(ManifestFile file) =>
        JsonSerializer.SerializeToUtf8Bytes(file, ControlJsonContext.Default.ManifestFile).Length;

    private static byte[] EncodeControl<T>(FrameType type, T body, JsonTypeInfo<T> info)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, info);
        return Encode(type, json);
    }

    private static T DecodeControl<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info)
                ?? throw new ProtocolException("Kontrol gövdesi null.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Kontrol gövdesi geçersiz JSON: {ex.Message}");
        }
    }
}
