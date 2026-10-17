using System.Collections.Frozen;

namespace WinDrop.Core.Noise;

/// <summary>Mesaj deseni belirteçleri (Noise rev 34, §7).</summary>
internal enum Token { E, S, EE, ES, SE, SS, Psk }

/// <summary>
/// Noise el sıkışma deseni: ön mesajlar + belirteç listeleriyle mesajlar. Tüm temel (§7.5, §7.6),
/// ertelenmiş (§18.1, 23 desen) ve tek yönlü (N, K, X) desenler ile <c>pskN</c> modifikatörleri
/// (§9) tablodan/metinden okunur. Protokol adı: <c>Noise_{Ad}_25519_ChaChaPoly_SHA256</c>.
/// Mesajlar yönü sabit: 0. mesaj başlatandan, sonra dönüşümlü.
/// </summary>
public sealed class NoisePattern
{
    // Başlatan/yanıtlayan ön mesajları (yalnız "s" desteklenir; "e" yalnız fallback desenlerinde olur).
    internal int PreMessageInitiatorCount { get; }
    internal int PreMessageResponderCount { get; }
    internal bool InitiatorHasPreMessageStatic { get; }
    internal bool ResponderHasPreMessageStatic { get; }
    internal Token[][] Messages { get; }

    /// <summary>Desen adı, ör. "XXpsk3".</summary>
    public string Name { get; }

    public string ProtocolName => $"Noise_{Name}_25519_ChaChaPoly_SHA256";

    /// <summary>Desendeki psk belirteci sayısı (modifikatör sayısı); el sıkışma o kadar psk ister.</summary>
    public int PskCount { get; }

    public int MessageCount => Messages.Length;

    /// <summary>Tek mesajlık (N, K, X) desen: taşıma verisini yalnız başlatan gönderir.</summary>
    public bool IsOneWay => Messages.Length == 1;

    private NoisePattern(string name, bool initiatorPreS, bool responderPreS, Token[][] messages, int pskCount)
    {
        Name = name;
        InitiatorHasPreMessageStatic = initiatorPreS;
        ResponderHasPreMessageStatic = responderPreS;
        Messages = messages;
        PskCount = pskCount;
    }

    // Tablo: ön mesajlar "..." öncesinde; satırlar ';' ile ayrılır.
    private static readonly FrozenDictionary<string, string> BasePatterns = new Dictionary<string, string>
    {
        // Tek yönlü (§7.4)
        ["N"] = "<- s; ...; -> e, es",
        ["K"] = "-> s; <- s; ...; -> e, es, ss",
        ["X"] = "<- s; ...; -> e, es, s, ss",
        // Etkileşimli temel desenler (§7.5)
        ["NN"] = "-> e; <- e, ee",
        ["NK"] = "<- s; ...; -> e, es; <- e, ee",
        ["NX"] = "-> e; <- e, ee, s, es",
        ["KN"] = "-> s; ...; -> e; <- e, ee, se",
        ["KK"] = "-> s; <- s; ...; -> e, es, ss; <- e, ee, se",
        ["KX"] = "-> s; ...; -> e; <- e, ee, se, s, es",
        ["XN"] = "-> e; <- e, ee; -> s, se",
        ["XK"] = "<- s; ...; -> e, es; <- e, ee; -> s, se",
        ["XX"] = "-> e; <- e, ee, s, es; -> s, se",
        ["IN"] = "-> e, s; <- e, ee, se",
        ["IK"] = "<- s; ...; -> e, es, s, ss; <- e, ee, se",
        ["IX"] = "-> e, s; <- e, ee, se, s, es",
        // Ertelenmiş desenler (§18.1)
        ["NK1"] = "<- s; ...; -> e; <- e, ee, es",
        ["NX1"] = "-> e; <- e, ee, s; -> es",
        ["X1N"] = "-> e; <- e, ee; -> s; <- se",
        ["X1K"] = "<- s; ...; -> e, es; <- e, ee; -> s; <- se",
        ["XK1"] = "<- s; ...; -> e; <- e, ee, es; -> s, se",
        ["X1K1"] = "<- s; ...; -> e; <- e, ee, es; -> s; <- se",
        ["X1X"] = "-> e; <- e, ee, s, es; -> s; <- se",
        ["XX1"] = "-> e; <- e, ee, s; -> es, s, se",
        ["X1X1"] = "-> e; <- e, ee, s; -> es, s; <- se",
        ["K1N"] = "-> s; ...; -> e; <- e, ee; -> se",
        ["K1K"] = "-> s; <- s; ...; -> e, es; <- e, ee; -> se",
        ["KK1"] = "-> s; <- s; ...; -> e; <- e, ee, se, es",
        ["K1K1"] = "-> s; <- s; ...; -> e; <- e, ee, es; -> se",
        ["K1X"] = "-> s; ...; -> e; <- e, ee, s, es; -> se",
        ["KX1"] = "-> s; ...; -> e; <- e, ee, se, s; -> es",
        ["K1X1"] = "-> s; ...; -> e; <- e, ee, s; -> se, es",
        ["I1N"] = "-> e, s; <- e, ee; -> se",
        ["I1K"] = "<- s; ...; -> e, es, s; <- e, ee; -> se",
        ["IK1"] = "<- s; ...; -> e, s; <- e, ee, se, es",
        ["I1K1"] = "<- s; ...; -> e, s; <- e, ee, es; -> se",
        ["I1X"] = "-> e, s; <- e, ee, s, es; -> se",
        ["IX1"] = "-> e, s; <- e, ee, se, s; -> es",
        ["I1X1"] = "-> e, s; <- e, ee, s; -> se, es",
    }.ToFrozenDictionary();

    public static NoisePattern NN { get; } = Parse("NN");
    public static NoisePattern NK { get; } = Parse("NK");
    public static NoisePattern XX { get; } = Parse("XX");
    public static NoisePattern IK { get; } = Parse("IK");
    /// <summary>Eşleştirme deseni (PROTOCOL §1): psk, 3. mesajın sonunda.</summary>
    public static NoisePattern XXpsk3 { get; } = Parse("XXpsk3");

    /// <summary>"IK", "XXpsk3", "NNpsk0+psk2" gibi bir adı okur. Tanınmayan ad → <see cref="NoiseErrorKind.UnsupportedPattern"/>.</summary>
    public static NoisePattern Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var pskAt = name.IndexOf("psk", StringComparison.Ordinal);
        var baseName = pskAt < 0 ? name : name[..pskAt];
        if (!BasePatterns.TryGetValue(baseName, out var definition))
            throw new NoiseException(NoiseErrorKind.UnsupportedPattern, $"Bilinmeyen Noise deseni: {name}");

        var (initPre, respPre, messages) = ParseDefinition(definition);

        var pskPositions = new List<int>();
        if (pskAt >= 0)
        {
            foreach (var mod in name[pskAt..].Split('+'))
            {
                if (!mod.StartsWith("psk", StringComparison.Ordinal)
                    || !int.TryParse(mod.AsSpan(3), out var n) || n < 0 || n > messages.Count)
                    throw new NoiseException(NoiseErrorKind.UnsupportedPattern, $"Geçersiz psk modifikatörü: {name}");
                pskPositions.Add(n);
            }
        }

        // pskN: N=0 → 1. mesajın başına "psk"; N>0 → N. mesajın sonuna "psk" (§9.2).
        foreach (var n in pskPositions)
        {
            if (n == 0) messages[0].Insert(0, Token.Psk);
            else messages[n - 1].Add(Token.Psk);
        }

        return new NoisePattern(name, initPre, respPre, messages.Select(m => m.ToArray()).ToArray(), pskPositions.Count);
    }

    private static (bool InitPreS, bool RespPreS, List<List<Token>> Messages) ParseDefinition(string definition)
    {
        bool initPre = false, respPre = false;
        var messages = new List<List<Token>>();
        var pre = true;
        // "..." sonrasına kadar ön mesaj. Tanımda "..." yoksa ön mesaj yoktur.
        if (!definition.Contains("...", StringComparison.Ordinal)) pre = false;

        var lineNo = 0;
        foreach (var raw in definition.Split(';', StringSplitOptions.TrimEntries))
        {
            if (raw == "...") { pre = false; continue; }
            var initiatorSends = raw.StartsWith("->", StringComparison.Ordinal);
            if (!initiatorSends && !raw.StartsWith("<-", StringComparison.Ordinal))
                throw new InvalidOperationException($"Desen tanımı bozuk: {raw}");
            var tokens = raw[2..].Split(',', StringSplitOptions.TrimEntries).Select(ParseToken).ToList();

            if (pre)
            {
                if (tokens.Count != 1 || tokens[0] != Token.S)
                    throw new NoiseException(NoiseErrorKind.UnsupportedPattern, "Yalnız 's' ön mesajı desteklenir.");
                if (initiatorSends) initPre = true; else respPre = true;
            }
            else
            {
                // Yön sabit: çift indeks başlatan. Bozuk tablo varsa derlemede değil ilk kullanımda yakalanır.
                if (initiatorSends != (lineNo % 2 == 0))
                    throw new InvalidOperationException($"Mesaj yönleri dönüşümlü değil: {definition}");
                messages.Add(tokens);
                lineNo++;
            }
        }
        return (initPre, respPre, messages);
    }

    private static Token ParseToken(string t) => t switch
    {
        "e" => Token.E,
        "s" => Token.S,
        "ee" => Token.EE,
        "es" => Token.ES,
        "se" => Token.SE,
        "ss" => Token.SS,
        _ => throw new InvalidOperationException($"Bilinmeyen belirteç: {t}"),
    };

    /// <summary>Yalnız test/tanılama: tüm temel+ertelenmiş desen adları.</summary>
    internal static IEnumerable<string> KnownBaseNames => BasePatterns.Keys;
}
