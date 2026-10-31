using System.Security.Cryptography;

namespace Roomie.Core.Identity;

/// <summary>
/// SPKI fingerprints (architecture.md §10). Hashing the public key rather than the whole
/// certificate lets a device renew its certificate later without changing its identity.
/// </summary>
public static class CertificateFingerprint
{
    public const string Prefix = "SHA256:";

    /// <summary>Returns <c>SHA256:</c> followed by the upper-case hex SHA-256 of the DER SubjectPublicKeyInfo.</summary>
    public static string FromSubjectPublicKeyInfo(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        if (subjectPublicKeyInfo.IsEmpty)
        {
            throw new ArgumentException("SubjectPublicKeyInfo is empty.", nameof(subjectPublicKeyInfo));
        }

        return Prefix + Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));
    }

    /// <summary>Constant-time comparison of two fingerprints in canonical form.</summary>
    public static bool AreEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));
    }

    /// <summary>Groups the hex digits in fours for reading aloud during manual verification.</summary>
    public static string ToDisplayString(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        var hex = fingerprint.StartsWith(Prefix, StringComparison.Ordinal) ? fingerprint[Prefix.Length..] : fingerprint;
        return string.Join(' ', hex.Chunk(4).Select(chunk => new string(chunk)));
    }
}
