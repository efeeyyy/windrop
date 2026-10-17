using System.Text.Json;
using WinDrop.Core.Noise;
using Xunit;
using Xunit.Abstractions;

namespace WinDrop.Core.Tests.Noise;

/// <summary>
/// Resmi cacophony vektörleri (protocol/vectors/cacophony.txt): <c>25519_ChaChaPoly_SHA256</c> olan tüm
/// girdiler (psk'lılar dahil) iki rol de çalıştırılarak doğrulanır: her mesajın şifreli metni, el sıkışma
/// özeti ve el sıkışma sonrası taşıma mesajları.
/// </summary>
public sealed class CacophonyVectorTests(ITestOutputHelper output)
{
    private const string Suffix = "_25519_ChaChaPoly_SHA256";

    private sealed record Vector(string ProtocolName, JsonElement Json)
    {
        public string PatternName => ProtocolName["Noise_".Length..^Suffix.Length];
    }

    private static readonly Lazy<IReadOnlyList<Vector>> All = new(Load);

    private static IReadOnlyList<Vector> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "cacophony.txt");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(v => new Vector(v.GetProperty("protocol_name").GetString()!, v.Clone()))
            .Where(v => v.ProtocolName.EndsWith(Suffix, StringComparison.Ordinal))
            .ToList();
    }

    public static IEnumerable<object[]> Cases() =>
        All.Value.Select((v, i) => new object[] { i, v.PatternName });

    [Fact]
    public void AllBaseVectorsAreSupported_AndIkAndXXpsk3ArePresent()
    {
        var total = All.Value.Count;
        var unsupported = All.Value.Where(v => !Supported(v.PatternName)).Select(v => v.PatternName).ToList();
        var ik = All.Value.Count(v => v.PatternName == "IK");
        var xxpsk3 = All.Value.Count(v => v.PatternName == "XXpsk3");
        output.WriteLine($"25519_ChaChaPoly_SHA256 vektörleri: {total}; desteklenen: {total - unsupported.Count}; " +
                         $"atlanan: {unsupported.Count} [{string.Join(",", unsupported)}]; IK={ik}; XXpsk3={xxpsk3}");
        Assert.Empty(unsupported);
        Assert.True(total >= 59, $"beklenenden az vektör: {total}");
        Assert.True(ik >= 1, "IK vektörü yok");
        Assert.True(xxpsk3 >= 1, "XXpsk3 vektörü yok");
    }

    private static bool Supported(string name)
    {
        try { NoisePattern.Parse(name); return true; }
        catch (NoiseException) { return false; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_Passes(int index, string patternName)
    {
        var v = All.Value[index];
        Assert.Equal(patternName, v.PatternName);
        var j = v.Json;
        var pattern = NoisePattern.Parse(v.PatternName);

        byte[] Hex(string name) => Convert.FromHexString(j.GetProperty(name).GetString()!);
        byte[]? OptHex(string name) => j.TryGetProperty(name, out var e) ? Convert.FromHexString(e.GetString()!) : null;
        List<byte[]> Psks(string name) => j.TryGetProperty(name, out var e)
            ? e.EnumerateArray().Select(x => Convert.FromHexString(x.GetString()!)).ToList()
            : [];

        NoiseKeyPair? Key(string name) => OptHex(name) is { } k ? NoiseKeyPair.FromPrivateKey(k) : null;

        using var initiator = new HandshakeState(
            pattern, true, Hex("init_prologue"), Key("init_static"),
            OptHex("init_remote_static") ?? default, Psks("init_psks"), Key("init_ephemeral"));
        using var responder = new HandshakeState(
            pattern, false, Hex("resp_prologue"), Key("resp_static"),
            OptHex("resp_remote_static") ?? default, Psks("resp_psks"), Key("resp_ephemeral"));

        var messages = j.GetProperty("messages").EnumerateArray().ToList();
        var i = 0;
        for (; !initiator.IsComplete; i++)
        {
            var (sender, receiver) = i % 2 == 0 ? (initiator, responder) : (responder, initiator);
            var payload = Convert.FromHexString(messages[i].GetProperty("payload").GetString()!);
            var expected = Convert.FromHexString(messages[i].GetProperty("ciphertext").GetString()!);

            var wire = sender.WriteMessage(payload);
            Assert.Equal(expected, wire);
            Assert.Equal(payload, receiver.ReadMessage(wire));
        }
        Assert.True(responder.IsComplete);
        Assert.Equal(Hex("handshake_hash"), initiator.HandshakeHash);
        Assert.Equal(Hex("handshake_hash"), responder.HandshakeHash);

        // El sıkışma sonrası taşıma mesajları: tek yönlü desenlerde hep başlatan, diğerlerinde dönüşümlü.
        using var tInit = initiator.Split();
        using var tResp = responder.Split();
        for (; i < messages.Count; i++)
        {
            var (send, recv) = pattern.IsOneWay || i % 2 == 0
                ? (tInit.Send, tResp.Receive)
                : (tResp.Send, tInit.Receive);
            var payload = Convert.FromHexString(messages[i].GetProperty("payload").GetString()!);
            var expected = Convert.FromHexString(messages[i].GetProperty("ciphertext").GetString()!);

            var wire = send.Encrypt(payload);
            Assert.Equal(expected, wire);
            Assert.Equal(payload, recv.Decrypt(wire));
        }
    }
}
