using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Roomie.Infrastructure.Identity;

/// <summary>Builds the self-signed X.509 device certificate (architecture.md §1.1).</summary>
internal static class DeviceCertificateFactory
{
    private const string SubjectPrefix = "CN=Roomie Device ";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Long-lived on purpose: peers pin the public-key fingerprint, not the certificate, so a renewal
    /// can be added later without changing the device's identity.
    /// </summary>
    private static readonly TimeSpan Validity = TimeSpan.FromDays(365 * 20);

    public static X509Certificate2 Create(Guid deviceId, ECDsa key, DateTimeOffset now)
    {
        var request = new CertificateRequest(SubjectPrefix + deviceId.ToString("D"), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ServerAuthOid), new Oid(ClientAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // Back-date slightly so a peer whose clock runs a little behind still sees a valid certificate.
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Validity));
    }

    /// <summary>Reads the device ID back from the certificate subject, or null if the subject isn't ours.</summary>
    public static Guid? ReadDeviceId(X509Certificate2 certificate)
    {
        var subject = certificate.Subject;
        if (!subject.StartsWith(SubjectPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParseExact(subject[SubjectPrefix.Length..], "D", out var deviceId) ? deviceId : null;
    }
}
