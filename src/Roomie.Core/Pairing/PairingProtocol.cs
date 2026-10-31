using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Roomie.Core.Identity;

namespace Roomie.Core.Pairing;

/// <summary>
/// The commit-then-reveal pairing computations of architecture.md §3.1. Pure functions, so both sides
/// and the tests compute exactly the same bytes.
/// </summary>
/// <remarks>
/// Encodings: an ephemeral key <c>pub(e)</c> is the 91-byte DER SubjectPublicKeyInfo of a P-256 key; a
/// nonce is 32 random bytes; <c>fp</c> is the 32-byte SHA-256 of the device certificate's
/// SubjectPublicKeyInfo; a device ID is its 16 bytes in RFC 4122 (big-endian) order. Every transcript
/// field, including the leading label, is prefixed with its length as a 32-bit big-endian integer.
/// Device signatures are ECDSA P-256 / SHA-256 in IEEE P1363 form (64 bytes).
/// </remarks>
public static class PairingProtocol
{
    public const int NonceLength = 32;
    public const int CommitmentLength = 32;
    public const int EphemeralKeyLength = 91;
    public const int SignatureLength = 64;

    private static readonly byte[] CommitLabel = "RMP-PAIR-COMMIT"u8.ToArray();
    private static readonly byte[] TranscriptLabel = "RMP-PAIR-v1"u8.ToArray();
    private static readonly byte[] SasLabel = "RMP-PAIR-SAS-v1"u8.ToArray();

    // DER prefix of every P-256 SubjectPublicKeyInfo: id-ecPublicKey, prime256v1, uncompressed point.
    private static readonly byte[] P256SpkiPrefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D03010703420004");

    public static ECDiffieHellman CreateEphemeralKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary><c>commitA = SHA-256("RMP-PAIR-COMMIT" ‖ pub(eA) ‖ nA)</c>.</summary>
    public static byte[] Commit(ReadOnlySpan<byte> ephemeralKey, ReadOnlySpan<byte> nonce)
    {
        RequireLength(ephemeralKey, EphemeralKeyLength, nameof(ephemeralKey));
        RequireLength(nonce, NonceLength, nameof(nonce));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(CommitLabel);
        hash.AppendData(ephemeralKey);
        hash.AppendData(nonce);
        return hash.GetHashAndReset();
    }

    /// <summary>Constant-time check that a revealed key and nonce match the earlier commitment.</summary>
    public static bool MatchesCommitment(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> ephemeralKey, ReadOnlySpan<byte> nonce) =>
        commitment.Length == CommitmentLength
        && ephemeralKey.Length == EphemeralKeyLength
        && nonce.Length == NonceLength
        && CryptographicOperations.FixedTimeEquals(commitment, Commit(ephemeralKey, nonce));

    /// <summary>
    /// <c>transcript = "RMP-PAIR-v1" ‖ fp(certA) ‖ fp(certB) ‖ deviceIdA ‖ deviceIdB ‖ commitA ‖ pub(eB) ‖ nB</c>,
    /// where A is the initiator (sender of <c>PAIR_REQUEST</c>) and B the responder.
    /// </summary>
    public static byte[] Transcript(
        string fingerprintA,
        string fingerprintB,
        Guid deviceIdA,
        Guid deviceIdB,
        ReadOnlySpan<byte> commitA,
        ReadOnlySpan<byte> ephemeralKeyB,
        ReadOnlySpan<byte> nonceB)
    {
        RequireLength(commitA, CommitmentLength, nameof(commitA));
        RequireLength(ephemeralKeyB, EphemeralKeyLength, nameof(ephemeralKeyB));
        RequireLength(nonceB, NonceLength, nameof(nonceB));

        using var buffer = new MemoryStream(512);
        Append(buffer, TranscriptLabel);
        Append(buffer, CertificateFingerprint.ToBytes(fingerprintA));
        Append(buffer, CertificateFingerprint.ToBytes(fingerprintB));
        Append(buffer, deviceIdA.ToByteArray(bigEndian: true));
        Append(buffer, deviceIdB.ToByteArray(bigEndian: true));
        Append(buffer, commitA);
        Append(buffer, ephemeralKeyB);
        Append(buffer, nonceB);
        return buffer.ToArray();
    }

    /// <summary>What the initiator signs: the transcript extended with <c>pub(eA) ‖ nA</c>.</summary>
    public static byte[] InitiatorSignedData(ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> ephemeralKeyA, ReadOnlySpan<byte> nonceA)
    {
        RequireLength(ephemeralKeyA, EphemeralKeyLength, nameof(ephemeralKeyA));
        RequireLength(nonceA, NonceLength, nameof(nonceA));

        using var buffer = new MemoryStream(transcript.Length + 140);
        buffer.Write(transcript);
        Append(buffer, ephemeralKeyA);
        Append(buffer, nonceA);
        return buffer.ToArray();
    }

    /// <summary>Verifies a device signature against the SubjectPublicKeyInfo from the peer's TLS certificate.</summary>
    public static bool VerifySignature(ReadOnlySpan<byte> devicePublicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(devicePublicKey, out var read);
            return read == devicePublicKey.Length
                && key.KeySize == 256
                && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Imports a peer's ephemeral key, accepting only a P-256 SubjectPublicKeyInfo with a valid point.</summary>
    public static bool TryImportEphemeralKey(ReadOnlySpan<byte> spki, [NotNullWhen(true)] out ECDiffieHellman? key)
    {
        key = null;
        if (spki.Length != EphemeralKeyLength || !spki.StartsWith(P256SpkiPrefix))
        {
            return false;
        }

        var candidate = ECDiffieHellman.Create();
        try
        {
            candidate.ImportSubjectPublicKeyInfo(spki, out _);
            candidate.ExportParameters(false).Validate();
            key = candidate;
            return true;
        }
        catch (CryptographicException)
        {
            candidate.Dispose();
            return false;
        }
    }

    /// <summary>
    /// <c>Z = ECDH(own, peer)</c>, then <c>sas = HKDF-SHA256(Z, salt = nA ‖ nB, info = "RMP-PAIR-SAS-v1" ‖ transcript, 4)</c>,
    /// shown as <c>uint32_be(sas) mod 1 000 000</c>, e.g. <c>482 193</c>. Z is wiped afterwards.
    /// </summary>
    public static string ComputeCode(ECDiffieHellman ownKey, ECDiffieHellman peerKey, ReadOnlySpan<byte> nonceA, ReadOnlySpan<byte> nonceB, ReadOnlySpan<byte> transcript)
    {
        ArgumentNullException.ThrowIfNull(ownKey);
        ArgumentNullException.ThrowIfNull(peerKey);
        RequireLength(nonceA, NonceLength, nameof(nonceA));
        RequireLength(nonceB, NonceLength, nameof(nonceB));

        var shared = ownKey.DeriveRawSecretAgreement(peerKey.PublicKey);
        try
        {
            var salt = new byte[NonceLength * 2];
            nonceA.CopyTo(salt);
            nonceB.CopyTo(salt.AsSpan(NonceLength));

            var info = new byte[SasLabel.Length + transcript.Length];
            SasLabel.CopyTo(info, 0);
            transcript.CopyTo(info.AsSpan(SasLabel.Length));

            Span<byte> sas = stackalloc byte[4];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, sas, salt, info);
            return FormatCode(BinaryPrimitives.ReadUInt32BigEndian(sas) % 1_000_000);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public static string FormatCode(uint value)
    {
        var digits = value.ToString("D6", CultureInfo.InvariantCulture);
        return $"{digits[..3]} {digits[3..]}";
    }

    private static void Append(MemoryStream buffer, ReadOnlySpan<byte> field)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
        buffer.Write(length);
        buffer.Write(field);
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"Expected {length} bytes.", name);
        }
    }
}
