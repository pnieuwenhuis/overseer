using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Overseer.Sandboxes;

/// <summary>A fresh RSA key pair in the formats OpenSSH reads, for a Sandbox's user or host.</summary>
/// <param name="PrivateKey">PEM-encoded (PKCS #1).</param>
/// <param name="PublicKey">One line, as in <c>authorized_keys</c>: <c>ssh-rsa</c> and the base64-encoded key.</param>
/// <param name="Fingerprint">The SHA256 fingerprint as OpenSSH shows it: <c>SHA256:</c> and unpadded base64.</param>
public sealed record SshKeyPair(string PrivateKey, string PublicKey, string Fingerprint)
{
    public static SshKeyPair Generate()
    {
        using var rsa = RSA.Create(3072);
        var blob = PublicKeyBlob(rsa.ExportParameters(includePrivateParameters: false));
        return new SshKeyPair(
            rsa.ExportRSAPrivateKeyPem(),
            $"ssh-rsa {Convert.ToBase64String(blob)}",
            $"SHA256:{Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=')}");
    }

    // Leaves the private key out, should a key pair ever be logged.
    public override string ToString() => Fingerprint;

    /// <summary>The key as SSH sends it (RFC 4253, section 6.6): the algorithm name, the exponent and the modulus.</summary>
    private static byte[] PublicKeyBlob(RSAParameters key)
    {
        using var blob = new MemoryStream();
        WriteString(blob, "ssh-rsa"u8);
        WriteMpint(blob, key.Exponent!);
        WriteMpint(blob, key.Modulus!);
        return blob.ToArray();
    }

    private static void WriteString(Stream blob, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        blob.Write(length);
        blob.Write(value);
    }

    /// <summary>An mpint is a two's complement number, so a positive one whose top bit is set gets a leading zero byte.</summary>
    private static void WriteMpint(Stream blob, byte[] unsignedBigEndian)
    {
        var value = unsignedBigEndian.AsSpan().TrimStart((byte)0);
        WriteString(blob, value.Length > 0 && value[0] >= 0x80 ? [0, .. value] : value);
    }
}
