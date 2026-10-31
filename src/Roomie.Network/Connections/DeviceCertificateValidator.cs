using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roomie.Core.Identity;

namespace Roomie.Network.Connections;

/// <summary>The identity a peer proved in the TLS handshake.</summary>
public sealed record RemoteDeviceCertificate(Guid DeviceId, string Fingerprint);

/// <summary>
/// Checks that a peer certificate is a well-formed Roomie device certificate (architecture.md §10,
/// "first contact"): self-signed with a valid signature, ECDSA P-256 with SHA-256, currently valid, with
/// the expected extensions and a device ID in the subject. Whether the device is trusted is decided
/// separately, by comparing the fingerprint with the pinned one.
/// </summary>
public static class DeviceCertificateValidator
{
    private const string EcPublicKeyOid = "1.2.840.10045.2.1";
    private const string EcdsaWithSha256Oid = "1.2.840.10045.4.3.2";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    // DER encoding of the OID 1.2.840.10045.3.1.7 (secp256r1 / P-256).
    private static readonly byte[] P256CurveParameters = [0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07];

    public static bool TryValidate(
        X509Certificate2 certificate,
        DateTimeOffset now,
        [NotNullWhen(true)] out RemoteDeviceCertificate? result,
        [NotNullWhen(false)] out string? failure)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        result = null;

        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
        {
            failure = "The certificate is not self-signed.";
            return false;
        }

        if (DeviceCertificateNames.ReadDeviceId(certificate) is not { } deviceId)
        {
            failure = "The certificate subject is not a Roomie device ID.";
            return false;
        }

        if (certificate.SignatureAlgorithm.Value != EcdsaWithSha256Oid)
        {
            failure = "The certificate is not signed with ECDSA and SHA-256.";
            return false;
        }

        if (certificate.PublicKey.Oid.Value != EcPublicKeyOid
            || certificate.PublicKey.EncodedParameters?.RawData is not { } curve
            || !curve.AsSpan().SequenceEqual(P256CurveParameters))
        {
            failure = "The certificate key is not ECDSA P-256.";
            return false;
        }

        if (now < new DateTimeOffset(certificate.NotBefore) || now > new DateTimeOffset(certificate.NotAfter))
        {
            failure = "The certificate is not currently valid. Check both computers' clocks.";
            return false;
        }

        if (!HasRequiredExtensions(certificate))
        {
            failure = "The certificate does not have the extensions of a Roomie device certificate.";
            return false;
        }

        if (!HasValidSelfSignature(certificate))
        {
            failure = "The certificate's signature is invalid.";
            return false;
        }

        var fingerprint = CertificateFingerprint.FromSubjectPublicKeyInfo(certificate.PublicKey.ExportSubjectPublicKeyInfo());
        result = new RemoteDeviceCertificate(deviceId, fingerprint);
        failure = null;
        return true;
    }

    private static bool HasRequiredExtensions(X509Certificate2 certificate)
    {
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var enhanced = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();

        if (basic is null || basic.CertificateAuthority || usage is null || enhanced is null)
        {
            return false;
        }

        var purposes = enhanced.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value).ToHashSet();
        return usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)
            && purposes.Contains(ServerAuthOid)
            && purposes.Contains(ClientAuthOid);
    }

    // TLS already proves the peer holds the private key; this proves the certificate itself wasn't altered.
    private static bool HasValidSelfSignature(X509Certificate2 certificate)
    {
        try
        {
            var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var toBeSigned = sequence.ReadEncodedValue();
            sequence.ReadEncodedValue(); // signatureAlgorithm, already checked
            var signature = sequence.ReadBitString(out var unusedBits);
            if (unusedBits != 0 || sequence.HasData || reader.HasData)
            {
                return false;
            }

            using var key = certificate.GetECDsaPublicKey();
            return key is not null
                && key.VerifyData(toBeSigned.Span, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return false;
        }
    }
}
