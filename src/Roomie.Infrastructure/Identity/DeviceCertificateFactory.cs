using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roomie.Core.Identity;

namespace Roomie.Infrastructure.Identity;

/// <summary>Builds the self-signed X.509 device certificate (architecture.md §1.1).</summary>
internal static class DeviceCertificateFactory
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Long-lived on purpose: peers pin the public-key fingerprint, not the certificate, so a renewal
    /// can be added later without changing the device's identity.
    /// </summary>
    private static readonly TimeSpan Validity = TimeSpan.FromDays(365 * 20);

    public static X509Certificate2 Create(Guid deviceId, ECDsa key, DateTimeOffset now)
    {
        var request = new CertificateRequest(DeviceCertificateNames.SubjectFor(deviceId), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ServerAuthOid), new Oid(ClientAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // Back-date slightly so a peer whose clock runs a little behind still sees a valid certificate.
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Validity));
    }
}
