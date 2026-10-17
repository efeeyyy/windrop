using System.Text;
using WinDrop.Core.Protocol;
using Xunit;

namespace WinDrop.Core.Tests.Protocol;

public sealed class SignalMessagesTests
{
    private static readonly TransferId Id = new(Enumerable.Range(0, 16).Select(i => (byte)(i * 16 + 1)).ToArray());

    private static string Json(SignalMessage m) => Encoding.UTF8.GetString(SignalMessages.Encode(m));
    private static SignalMessage Decode(string json) => SignalMessages.Decode(Encoding.UTF8.GetBytes(json));

    public static IEnumerable<object[]> AllMessages() =>
    [
        [new HelloMessage(1, "Efe'nin iPhone'u", ["tcp", "hotspot", "relay"], WifiState.Connected, true), "hello"],
        [new HelloMessage(1, "PC", ["tcp"], null, false), "hello"],
        [new StatusMessage(WifiState.None, false), "status"],
        [new OfferMessage(Id, 3, 12345678901, ["a.txt", "b.png"]), "offer"],
        [new AcceptMessage(Id), "accept"],
        [new RejectMessage(Id, "meşgul"), "reject"],
        [new AddrMessage(Id, [new AddrCandidate("192.168.1.5", 53600), new AddrCandidate("fe80::1", 53600)]), "addr"],
        [new HotspotMessage(Id, "192.168.137.1", 53600), "hotspot"],
        [new HotspotMessage(Id, "192.168.137.1", 53600, "WinDrop-1A2B3C4D", "p@ss"), "hotspot"],
        [new PairInfoMessage("WinDrop-1A2B3C4D", "p@ss"), "pairInfo"],
        [new RelayMessage(Id, "tok123"), "relay"],
        [new PathMessage(Id, PathResult.HotspotOk), "path"],
        [new CancelSignalMessage(Id), "cancel"],
    ];

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void RoundTrip_AndDiscriminator(SignalMessage message, string t)
    {
        var json = Json(message);
        Assert.Contains($"\"t\":\"{t}\"", json);
        Assert.Equal(message.GetType(), Decode(json).GetType());
        // Kayıt eşitliği liste alanlarında referans karşılaştırır; yeniden kodlamanın aynı JSON'u vermesi yeterli.
        Assert.Equal(json, Json(Decode(json)));
    }

    [Fact]
    public void Json_UsesCamelCase_AndTransferIdIsBase64Url16Bytes()
    {
        var json = Json(new OfferMessage(Id, 1, 2, []));
        Assert.Contains("\"transferId\":\"", json);
        var value = json.Split("\"transferId\":\"")[1].Split('"')[0];
        Assert.Equal(22, value.Length);
        Assert.DoesNotContain('=', value);
        Assert.DoesNotContain('+', value);
        Assert.DoesNotContain('/', value);
        Assert.Equal(Id, TransferId.Parse(value));

        Assert.Contains("\"btAudio\":true", Json(new StatusMessage(null, true)));
        Assert.DoesNotContain("wifi", Json(new StatusMessage(null, true)));   // null yazılmaz
    }

    [Fact]
    public void Decode_IgnoresUnknownFields_AndAcceptsAnyFieldOrder()
    {
        var m = (CancelSignalMessage)Decode($$"""{"extra":{"x":1},"transferId":"{{Id}}","zzz":[1,2],"t":"cancel"}""");
        Assert.Equal(Id, m.TransferId);
    }

    [Theory]
    [InlineData("""{"t":"bogus"}""")]
    [InlineData("""{"transferId":"AAAAAAAAAAAAAAAAAAAAAA"}""")]                           // t yok
    [InlineData("""{"t":"cancel"}""")]                                                     // zorunlu alan yok
    [InlineData("""{"t":"cancel","transferId":"kısa"}""")]
    [InlineData("""{"t":"cancel","transferId":"AAAAAAAAAAAAAAAAAAAAAAAA"}""")]            // 24 karakter
    [InlineData("""{"t":"path","transferId":"AAAAAAAAAAAAAAAAAAAAAA","result":"nope"}""")]
    [InlineData("""{"t":"status","wifi":"maybe","btAudio":false}""")]
    [InlineData("""{"t":"hotspot","transferId":"AAAAAAAAAAAAAAAAAAAAAA","ip":"1.2.3.4","port":70000}""")]
    [InlineData("""{"t":"offer","transferId":"AAAAAAAAAAAAAAAAAAAAAA","count":6,"total":6,"preview":["1","2","3","4","5","6"]}""")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("")]
    public void Decode_Invalid_ThrowsProtocolException(string json) =>
        Assert.Throws<ProtocolException>(() => Decode(json));

    [Fact]
    public void PlaintextLimit_8176()
    {
        var name = new string('x', 100);
        var preview = Enumerable.Repeat(name, 5).ToList();
        Assert.True(SignalMessages.Encode(new OfferMessage(Id, 1, 1, preview)).Length < SignalMessages.MaxPlaintext);

        var big = new HelloMessage(1, new string('n', 8200), ["tcp"], null, false);
        Assert.Throws<ProtocolException>(() => SignalMessages.Encode(big));

        var exact = new byte[SignalMessages.MaxPlaintext];
        var json = Encoding.UTF8.GetBytes("{\"t\":\"cancel\",\"transferId\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"pad\":\"");
        json.CopyTo(exact, 0);
        Array.Fill(exact, (byte)'a', json.Length, exact.Length - json.Length - 2);
        exact[^2] = (byte)'"';
        exact[^1] = (byte)'}';
        Assert.IsType<CancelSignalMessage>(SignalMessages.Decode(exact));                 // tam sınırda kabul

        Assert.Throws<ProtocolException>(() => SignalMessages.Decode(new byte[SignalMessages.MaxPlaintext + 1]));
    }

    [Fact]
    public void LooksLikeSignal_DistinguishesFromFrameTypes()
    {
        Assert.True(SignalMessages.LooksLikeSignal("{\"t\":\"x\"}"u8));
        foreach (var t in Enum.GetValues<FrameType>())
            Assert.False(SignalMessages.LooksLikeSignal([(byte)t, 1]));
    }

    [Fact]
    public void TransferId_Parse_Rejects_Garbage()
    {
        Assert.False(TransferId.TryParse(null, out _));
        Assert.False(TransferId.TryParse("", out _));
        Assert.False(TransferId.TryParse(new string('!', 22), out _));
        Assert.Throws<ProtocolException>(() => TransferId.Parse("short"));
        Assert.Equal(22, TransferId.Create().ToString().Length);
        Assert.NotEqual(TransferId.Create(), TransferId.Create());
    }
}
