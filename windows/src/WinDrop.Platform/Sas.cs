using System.Security.Cryptography;
using System.Text;

namespace WinDrop.Platform;

/// <summary>PROTOCOL §3.1 doğrulama kodu: <c>SHA-256("WinDrop-SAS" || h)</c>'nin ilk 4 baytı (big-endian uint32) mod 10000, 4 hane.</summary>
public static class Sas
{
    public static string Compute(ReadOnlySpan<byte> handshakeHash)
    {
        var label = Encoding.ASCII.GetBytes("WinDrop-SAS");
        var input = new byte[label.Length + handshakeHash.Length];
        label.CopyTo(input, 0);
        handshakeHash.CopyTo(input.AsSpan(label.Length));
        var hash = SHA256.HashData(input);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
        return (value % 10000u).ToString("D4");
    }
}
