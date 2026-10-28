using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Overseer.Login;

/// <summary>
/// Hashing for the user's password. The CLI hashes the passwords it generates with this code and the server
/// verifies with it, so a hash made by any <c>overseer</c> command always verifies in <c>serve</c>.
/// </summary>
public static class Passwords
{
    // ASP.NET Core Identity's hasher (PBKDF2 with HMAC-SHA512, random salt); the user argument is unused.
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object NoUser = new();

    /// <summary>A new random password: 24 letters and digits, about 143 bits, and easy to copy from a terminal.</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 24);

    public static string Hash(string password) => Hasher.HashPassword(NoUser, password);

    /// <summary>Whether <paramref name="hash"/> has the shape of a hash made by <see cref="Hash"/>, as opposed to, say, a password.</summary>
    public static bool IsHash(string hash)
    {
        // Identity's version 3 format: 0x01, then the PRF, iteration count and salt length (big-endian uint32), the salt, the subkey.
        const int headerLength = 13;
        var bytes = new byte[hash.Length];
        return Convert.TryFromBase64String(hash, bytes, out var length)
            && length > headerLength
            && bytes[0] == 0x01
            && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4)) is var saltLength and >= 16
            && headerLength + saltLength < length;
    }

    /// <summary>Whether <paramref name="password"/> is the one <paramref name="hash"/> was made from; false for a malformed hash.</summary>
    public static bool Verify(string hash, string password)
    {
        try
        {
            return Hasher.VerifyHashedPassword(NoUser, hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
