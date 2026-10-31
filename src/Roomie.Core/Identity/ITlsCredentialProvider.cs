using System.Security.Cryptography.X509Certificates;

namespace Roomie.Core.Identity;

public interface ITlsCredentialProvider
{
    /// <summary>
    /// The device certificate with a private key that Windows' TLS stack (SChannel) can use. Owned by the
    /// provider; callers must not dispose it.
    /// </summary>
    X509Certificate2 TlsCertificate { get; }
}
