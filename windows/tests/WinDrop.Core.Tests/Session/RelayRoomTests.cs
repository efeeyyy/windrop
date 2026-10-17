using System.Text.Json;
using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Core.Tests.Session;

/// <summary>PROTOCOL §6.3: token, oda kimliği (ortak vektör protocol/vectors/relay.json) ve aktarma adresi.</summary>
public class RelayRoomTests
{
    private static JsonElement[] Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "relay.json")));
        return [.. doc.RootElement.GetProperty("vectors").EnumerateArray().Select(e => e.Clone())];
    }

    [Fact]
    public void RoomId_MatchesSharedVectors()
    {
        var vectors = Vectors();
        Assert.True(vectors.Length >= 3);
        foreach (var v in vectors)
        {
            var token = Convert.FromHexString(v.GetProperty("token_hex").GetString()!);
            var expected = v.GetProperty("room_id").GetString()!;
            Assert.Equal(32, expected.Length);
            Assert.Equal(expected, RelayRoom.ComputeRoomId(token));

            // token_b64url aynı baytlardır (relay sinyalindeki biçim).
            Assert.Equal(v.GetProperty("token_b64url").GetString(), RelayRoom.EncodeToken(token));
            Assert.True(RelayRoom.TryDecodeToken(v.GetProperty("token_b64url").GetString(), out var decoded));
            Assert.Equal(token, decoded);
        }
    }

    [Fact]
    public void NewToken_Is32RandomBytes_AndRoomIdIs32LowerHex()
    {
        var a = RelayRoom.NewToken();
        var b = RelayRoom.NewToken();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
        var id = RelayRoom.ComputeRoomId(a);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.NotEqual(id, RelayRoom.ComputeRoomId(b));
        Assert.Equal(43, RelayRoom.EncodeToken(a).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void ComputeRoomId_RejectsWrongTokenLength(int length) =>
        Assert.Throws<ArgumentException>(() => RelayRoom.ComputeRoomId(new byte[length]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")]   // dolgulu
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh+")]    // base64url dışı karakter
    public void TryDecodeToken_RejectsMalformed(string? text) => Assert.False(RelayRoom.TryDecodeToken(text, out _));

    private const string Room = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("windrop-relay.efe.workers.dev", "wss://windrop-relay.efe.workers.dev/v1/room/" + Room + "?role=pc")]
    [InlineData("wss://windrop-relay.efe.workers.dev", "wss://windrop-relay.efe.workers.dev/v1/room/" + Room + "?role=pc")]
    [InlineData("  https://relay.example.com:8443/ignored/path  ", "wss://relay.example.com:8443/v1/room/" + Room + "?role=pc")]
    [InlineData("ws://127.0.0.1:8787", "ws://127.0.0.1:8787/v1/room/" + Room + "?role=pc")]
    [InlineData("http://localhost:8787", "ws://localhost:8787/v1/room/" + Room + "?role=pc")]
    public void BuildRoomUri_Normalizes(string input, string expected) =>
        Assert.Equal(expected, RelayRoom.BuildRoomUri(input, Room, "pc")!.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ws://relay.example.com")]       // şifresiz yalnız yerel döngü adresi
    [InlineData("http://relay.example.com")]
    [InlineData("ftp://relay.example.com")]
    [InlineData("wss://")]
    public void BuildRoomUri_RejectsEmptyOrInsecure(string? input) => Assert.Null(RelayRoom.BuildRoomUri(input, Room, "pc"));
}
