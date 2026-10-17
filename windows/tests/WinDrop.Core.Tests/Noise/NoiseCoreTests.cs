using System.Security.Cryptography;
using NSec.Cryptography;
using WinDrop.Core.Noise;
using Xunit;

namespace WinDrop.Core.Tests.Noise;

public sealed class NoiseCoreTests
{
    private static readonly byte[] Prologue = "prologue"u8.ToArray();

    // ---- HKDF: NSec (ham bayt), NSec (SharedSecret) ve BCL aynı sonucu vermeli ----

    [Fact]
    public void Hkdf_Rfc5869_TestCase3_MatchesNSecAndBcl()
    {
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var expected = Convert.FromHexString("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8");

        var nsec = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(ikm, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, 42);
        var bcl = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt: [], info: []);

        Assert.Equal(expected, nsec);
        Assert.Equal(expected, bcl);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1)]
    [InlineData(48)]
    public void Hkdf_NoiseShape_RawNSec_SharedSecretNSec_Bcl_Agree(int ikmLength)
    {
        var ikm = RandomNumberGenerator.GetBytes(ikmLength);
        var ck = RandomNumberGenerator.GetBytes(32);

        var raw = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(ikm, ck, ReadOnlySpan<byte>.Empty, 96);
        using var secret = SharedSecret.Import(ikm, SharedSecretBlobFormat.RawSharedSecret);
        var viaSecret = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(secret, ck, ReadOnlySpan<byte>.Empty, 96);
        var bcl = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 96, ck, []);

        Assert.Equal(bcl, raw);
        Assert.Equal(bcl, viaSecret);
    }

    [Fact]
    public void Hkdf_EmptyIkm_BclWorks_ForSplit_NSecRejects()
    {
        // Gerekçe: SymmetricState.Split boş IKM ile HKDF ister; NSec reddeder, BCL kabul eder.
        Assert.Throws<ArgumentException>(() =>
            KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(ReadOnlySpan<byte>.Empty, new byte[32], ReadOnlySpan<byte>.Empty, 64));
        var output = HKDF.DeriveKey(HashAlgorithmName.SHA256, [], 64, new byte[32], []);
        Assert.Equal(64, output.Length);
    }

    // ---- CipherState ----

    [Fact]
    public void CipherState_Nonce_IsFourZeroBytesPlusLittleEndianCounter()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var cs = new CipherState();
        cs.InitializeKey(key);
        cs.SetNonceForTest(0x0102030405060708UL);

        var ct = cs.Encrypt("merhaba"u8);

        var nonce = new byte[12];
        new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }.CopyTo(nonce, 4);
        using var bcl = new System.Security.Cryptography.ChaCha20Poly1305(key);
        var plain = new byte[ct.Length - 16];
        bcl.Decrypt(nonce, ct.AsSpan(0, plain.Length), ct.AsSpan(plain.Length), plain);
        Assert.Equal("merhaba"u8.ToArray(), plain);
        Assert.Equal(0x0102030405060709UL, cs.Nonce);
    }

    [Fact]
    public void CipherState_NonceExhausted_Throws()
    {
        using var cs = new CipherState();
        cs.InitializeKey(new byte[32]);
        cs.SetNonceForTest(ulong.MaxValue);
        var ex = Assert.Throws<NoiseException>(() => cs.Encrypt([1]));
        Assert.Equal(NoiseErrorKind.NonceExhausted, ex.Kind);
    }

    [Fact]
    public void CipherState_TagFailure_DoesNotAdvanceNonce_AndShortOrHugeInputsAreTyped()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var a = new CipherState();
        using var b = new CipherState();
        a.InitializeKey(key);
        b.InitializeKey(key);

        var ct = a.Encrypt([1, 2, 3]);
        var tampered = (byte[])ct.Clone();
        tampered[0] ^= 1;
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, Assert.Throws<NoiseException>(() => b.Decrypt(tampered)).Kind);
        Assert.Equal(0UL, b.Nonce);
        Assert.Equal(new byte[] { 1, 2, 3 }, b.Decrypt(ct));   // sayaç ilerlemediği için hâlâ çözülür

        Assert.Equal(NoiseErrorKind.InvalidMessageLength, Assert.Throws<NoiseException>(() => b.Decrypt(new byte[15])).Kind);
        Assert.Equal(NoiseErrorKind.MessageTooLarge, Assert.Throws<NoiseException>(() => b.Decrypt(new byte[65536])).Kind);
        Assert.Equal(NoiseErrorKind.MessageTooLarge, Assert.Throws<NoiseException>(() => a.Encrypt(new byte[65520])).Kind);
        Assert.Equal(65535, a.Encrypt(new byte[65519]).Length);
    }

    // ---- Anahtar çifti / dışa aktarma politikası ----

    [Fact]
    public void KeyPair_DefaultIsNotExportable_ExportableRoundTrips()
    {
        using var closed = NoiseKeyPair.Generate();
        Assert.Throws<InvalidOperationException>(() => closed.ExportPrivateKey());
        Assert.Equal(32, closed.PublicKey.Length);

        using var open = NoiseKeyPair.Generate(exportable: true);
        var raw = open.ExportPrivateKey();
        using var again = NoiseKeyPair.FromPrivateKey(raw);
        Assert.Equal(open.PublicKey, again.PublicKey);
        Assert.Throws<InvalidOperationException>(() => again.ExportPrivateKey());   // içe aktarma varsayılanı kapalı
    }

    // ---- Desen ayrıştırma ----

    [Theory]
    [InlineData("NN", 2, 0)]
    [InlineData("IK", 2, 0)]
    [InlineData("XXpsk3", 3, 1)]
    [InlineData("NNpsk0+psk2", 2, 2)]
    [InlineData("N", 1, 0)]
    public void Pattern_Parse_Basics(string name, int messages, int psks)
    {
        var p = NoisePattern.Parse(name);
        Assert.Equal(messages, p.MessageCount);
        Assert.Equal(psks, p.PskCount);
        Assert.Equal($"Noise_{name}_25519_ChaChaPoly_SHA256", p.ProtocolName);
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("")]
    [InlineData("XXpsk9")]
    [InlineData("XXpskx")]
    public void Pattern_Parse_Unsupported_Throws(string name)
    {
        var ex = Assert.Throws<NoiseException>(() => NoisePattern.Parse(name));
        Assert.Equal(NoiseErrorKind.UnsupportedPattern, ex.Kind);
    }

    [Fact]
    public void Pattern_AllKnownBasePatterns_ParseWithRepresentativePsk()
    {
        foreach (var name in NoisePattern.KnownBaseNames)
        {
            Assert.Equal(0, NoisePattern.Parse(name).PskCount);
            Assert.Equal(1, NoisePattern.Parse(name + "psk0").PskCount);
        }
    }

    // ---- Argüman ve durum denetimleri ----

    [Fact]
    public void Ctor_RequiresPsksAndStatics()
    {
        using var s = NoiseKeyPair.Generate();
        Assert.Throws<ArgumentException>(() => new HandshakeState(NoisePattern.XXpsk3, true, Prologue, s));            // psk eksik
        Assert.Throws<ArgumentException>(() => new HandshakeState(NoisePattern.XXpsk3, true, Prologue, s, default, [new byte[16]]));
        Assert.Throws<ArgumentException>(() => new HandshakeState(NoisePattern.XX, true, Prologue));                    // yerel statik eksik
        Assert.Throws<ArgumentException>(() => new HandshakeState(NoisePattern.IK, true, Prologue, s));                 // uzak statik (ön mesaj) eksik
    }

    [Fact]
    public void WrongTurn_AndAfterFailure_AndSplitEarly_ThrowInvalidState()
    {
        using var init = new HandshakeState(NoisePattern.NN, true, Prologue);
        using var resp = new HandshakeState(NoisePattern.NN, false, Prologue);

        Assert.Equal(NoiseErrorKind.InvalidState, Assert.Throws<NoiseException>(() => resp.WriteMessage([])).Kind);
        Assert.Equal(NoiseErrorKind.InvalidState, Assert.Throws<NoiseException>(() => init.ReadMessage(new byte[32])).Kind);
        Assert.Equal(NoiseErrorKind.InvalidState, Assert.Throws<NoiseException>(() => init.Split()).Kind);
        Assert.Equal(NoiseErrorKind.InvalidState, Assert.Throws<NoiseException>(() => _ = init.HandshakeHash).Kind);

        var m1 = init.WriteMessage([]);
        resp.ReadMessage(m1);
        var m2 = resp.WriteMessage([]);
        var tampered = (byte[])m2.Clone();
        tampered[^1] ^= 1;
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, Assert.Throws<NoiseException>(() => init.ReadMessage(tampered)).Kind);
        // Hata sonrası el sıkışma ölüdür, doğru mesajı bile kabul etmez.
        Assert.Equal(NoiseErrorKind.InvalidState, Assert.Throws<NoiseException>(() => init.ReadMessage(m2)).Kind);
    }

    [Fact]
    public void ReadMessage_TooShort_And_TooLarge_AreTyped()
    {
        using var shortInit = new HandshakeState(NoisePattern.NN, false, Prologue);
        Assert.Equal(NoiseErrorKind.InvalidMessageLength, Assert.Throws<NoiseException>(() => shortInit.ReadMessage(new byte[31])).Kind);

        using var bigInit = new HandshakeState(NoisePattern.NN, false, Prologue);
        Assert.Equal(NoiseErrorKind.MessageTooLarge, Assert.Throws<NoiseException>(() => bigInit.ReadMessage(new byte[65536])).Kind);

        // XX 2. mesajı: e(32) + şifreli s(48) + yük etiketi(16) = 96 ; eksik s
        using var xxI = new HandshakeState(NoisePattern.XX, true, Prologue, NoiseKeyPair.Generate());
        using var xxR = new HandshakeState(NoisePattern.XX, false, Prologue, NoiseKeyPair.Generate());
        xxR.ReadMessage(xxI.WriteMessage([]));
        var m2 = xxR.WriteMessage([]);
        Assert.Equal(96, m2.Length);
        Assert.Equal(NoiseErrorKind.InvalidMessageLength, Assert.Throws<NoiseException>(() => xxI.ReadMessage(m2.AsSpan(0, 70))).Kind);
    }

    [Fact]
    public void WriteMessage_65535Boundary_IsExact_AndRejectionHasNoSideEffect()
    {
        using var init = new HandshakeState(NoisePattern.NN, true, Prologue);
        using var resp = new HandshakeState(NoisePattern.NN, false, Prologue);

        // NN 1. mesaj: e(32) + açık yük (anahtar yok → etiket yok) = 65535 en çok.
        var tooBig = Assert.Throws<NoiseException>(() => init.WriteMessage(new byte[65504]));
        Assert.Equal(NoiseErrorKind.MessageTooLarge, tooBig.Kind);

        var ok = init.WriteMessage(new byte[65503]);        // durum bozulmadı, sınırda geçer
        Assert.Equal(65535, ok.Length);
        Assert.Equal(65503, resp.ReadMessage(ok).Length);

        // 2. mesajda anahtar var: e(32) + yük + etiket(16) ≤ 65535 → yük en çok 65487
        Assert.Equal(NoiseErrorKind.MessageTooLarge, Assert.Throws<NoiseException>(() => resp.WriteMessage(new byte[65488])).Kind);
        Assert.Equal(65535, resp.WriteMessage(new byte[65487]).Length);
    }

    [Fact]
    public void LowOrderRemoteStatic_IsRejectedAsInvalidKey()
    {
        using var s = NoiseKeyPair.Generate();
        using var init = new HandshakeState(NoisePattern.IK, true, Prologue, s, new byte[32]);   // sıfır noktası: DH çıktısı sıfır
        var ex = Assert.Throws<NoiseException>(() => init.WriteMessage([]));
        Assert.Equal(NoiseErrorKind.InvalidKey, ex.Kind);
    }

    [Fact]
    public void XXpsk3_WrongPsk_FailsAuthentication_RightPskWorks()
    {
        static (HandshakeState I, HandshakeState R) Make(byte[] pskI, byte[] pskR) => (
            new HandshakeState(NoisePattern.XXpsk3, true, Prologue, NoiseKeyPair.Generate(), default, [pskI]),
            new HandshakeState(NoisePattern.XXpsk3, false, Prologue, NoiseKeyPair.Generate(), default, [pskR]));

        var psk = RandomNumberGenerator.GetBytes(32);
        var other = RandomNumberGenerator.GetBytes(32);

        var (i, r) = Make(psk, psk);
        r.ReadMessage(i.WriteMessage([]));
        i.ReadMessage(r.WriteMessage([]));
        r.ReadMessage(i.WriteMessage([]));
        Assert.True(i.IsComplete && r.IsComplete);
        Assert.Equal(i.HandshakeHash, r.HandshakeHash);

        var (i2, r2) = Make(psk, other);
        r2.ReadMessage(i2.WriteMessage([]));
        i2.ReadMessage(r2.WriteMessage([]));   // psk3 yalnız 3. mesajın sonunda devreye girer
        var m3 = i2.WriteMessage([]);
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, Assert.Throws<NoiseException>(() => r2.ReadMessage(m3)).Kind);
    }
}
