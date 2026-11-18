using System.Text.Json;
using WinDrop.Core.Noise;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>
/// PROTOCOL §3.1 ortak vektörü (protocol/vectors/sas.json; Swift üretti, bağımsız Node referansıyla doğrulandı):
/// sabit anahtarlarla Noise_XX → el sıkışma özeti → 4 haneli doğrulama kodu. C# aynı özeti ve aynı kodu üretmelidir.
/// </summary>
public class SasVectorTests
{
    [Fact]
    public void SharedSasVector_ProducesSameHandshakeHashAndCode()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "sas.json")));
        var j = doc.RootElement;
        byte[] Hex(string name) => Convert.FromHexString(j.GetProperty(name).GetString()!);

        var name = j.GetProperty("protocol_name").GetString()!;
        Assert.Equal("Noise_XX_25519_ChaChaPoly_SHA256", name);
        var pattern = NoisePattern.Parse("XX");

        using var initiator = new HandshakeState(pattern, true, Hex("prologue"),
            NoiseKeyPair.FromPrivateKey(Hex("init_static")), default, [], NoiseKeyPair.FromPrivateKey(Hex("init_ephemeral")));
        using var responder = new HandshakeState(pattern, false, Hex("prologue"),
            NoiseKeyPair.FromPrivateKey(Hex("resp_static")), default, [], NoiseKeyPair.FromPrivateKey(Hex("resp_ephemeral")));

        for (var i = 0; i < 3; i++)
        {
            var (w, r) = i % 2 == 0 ? (initiator, responder) : (responder, initiator);
            r.ReadMessage(w.WriteMessage([]));
        }
        Assert.True(initiator.IsComplete);
        Assert.True(responder.IsComplete);
        Assert.Equal(Hex("handshake_hash"), initiator.HandshakeHash);
        Assert.Equal(Hex("handshake_hash"), responder.HandshakeHash);
        Assert.Equal(j.GetProperty("sas").GetString(), Sas.Compute(initiator.HandshakeHash));
        Assert.Equal(j.GetProperty("sas").GetString(), Sas.Compute(responder.HandshakeHash));
    }
}
