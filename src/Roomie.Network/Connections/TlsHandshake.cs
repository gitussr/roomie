using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Roomie.Network.Connections;

/// <summary>
/// Mutually authenticated TLS between device certificates (architecture.md §10). TLS 1.3 when both
/// computers support it; Windows 10 only has TLS 1.2, which is accepted with ECDHE and AES-GCM only.
/// </summary>
internal static class TlsHandshake
{
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    // Forward-secret, authenticated-encryption suites. Device keys are ECDSA, so no RSA suites can apply.
    private static readonly HashSet<TlsCipherSuite> AllowedTls12Suites =
    [
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
    ];

    public static async Task<(SslStream Stream, RemoteDeviceCertificate Peer)> AuthenticateAsServerAsync(
        Stream transport, SslStreamCertificateContext credentials, TimeProvider time, CancellationToken cancellationToken)
    {
        var validation = new ValidationState(time);
        var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = credentials,
                ClientCertificateRequired = true,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false,
                RemoteCertificateValidationCallback = validation.Validate,
            }, cancellationToken).ConfigureAwait(false);

            return (ssl, Verify(ssl, validation));
        }
        catch (AuthenticationException ex) when (validation.Failure is not null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException(validation.Failure, ex);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<(SslStream Stream, RemoteDeviceCertificate Peer)> AuthenticateAsClientAsync(
        Stream transport, SslStreamCertificateContext credentials, Guid expectedDeviceId, TimeProvider time, CancellationToken cancellationToken)
    {
        var validation = new ValidationState(time);
        var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = expectedDeviceId.ToString("D"),
                // Not ClientCertificateContext: with it SChannel only sends a client certificate issued by a CA
                // the server lists, which a self-signed device certificate never is.
                ClientCertificates = [credentials.TargetCertificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => credentials.TargetCertificate,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false,
                RemoteCertificateValidationCallback = validation.Validate,
            }, cancellationToken).ConfigureAwait(false);

            var peer = Verify(ssl, validation);
            if (peer.DeviceId != expectedDeviceId)
            {
                throw new AuthenticationException("A different device answered at that address.");
            }

            return (ssl, peer);
        }
        catch (AuthenticationException ex) when (validation.Failure is not null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException(validation.Failure, ex);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static string Describe(SslStream ssl)
    {
        var protocol = ssl.SslProtocol switch
        {
            SslProtocols.Tls13 => "TLS 1.3",
            SslProtocols.Tls12 => "TLS 1.2",
            _ => ssl.SslProtocol.ToString(),
        };

        return $"{protocol}, {ssl.NegotiatedCipherSuite}";
    }

    private static RemoteDeviceCertificate Verify(SslStream ssl, ValidationState validation)
    {
        if (validation.Result is not { } peer)
        {
            throw new AuthenticationException("The peer did not present a valid device certificate.");
        }

        if (!ssl.IsMutuallyAuthenticated)
        {
            throw new AuthenticationException("The TLS session is not mutually authenticated.");
        }

        var acceptable = ssl.SslProtocol == SslProtocols.Tls13
            || (ssl.SslProtocol == SslProtocols.Tls12 && AllowedTls12Suites.Contains(ssl.NegotiatedCipherSuite));
        if (!acceptable)
        {
            throw new AuthenticationException($"The negotiated security ({Describe(ssl)}) is not allowed.");
        }

        return peer;
    }

    private sealed class ValidationState(TimeProvider time)
    {
        public RemoteDeviceCertificate? Result { get; private set; }

        public string? Failure { get; private set; }

        public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                Failure = "The peer did not present a certificate.";
                return false;
            }

            // Chain errors are expected: device certificates are self-signed and trusted by pinning, never
            // through a CA. Windows also reports a name mismatch, because the device ID sits in the subject
            // rather than a DNS name; the device ID is checked explicitly instead (caller and TryValidate).
            var certificate2 = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            if (!DeviceCertificateValidator.TryValidate(certificate2, time.GetUtcNow(), out var result, out var failure))
            {
                Failure = failure;
                return false;
            }

            Result = result;
            return true;
        }
    }
}
