using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roomie.Core.Identity;
using Roomie.Infrastructure.Identity;
using Roomie.Network.Connections;

namespace Roomie.Tests.Connections;

public sealed class DeviceCertificateValidatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));
    private readonly DeviceIdentityStore _store;

    public DeviceCertificateValidatorTests()
    {
        _store = new DeviceIdentityStore(_directory);
        _store.LoadOrCreate("Alice");
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RealDeviceCertificate_IsAccepted()
    {
        using var certificate = X509CertificateLoader.LoadCertificate(_store.Certificate.RawData);

        Assert.True(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow, out var result, out var failure), failure);
        Assert.Equal(_store.Identity.DeviceId, result.DeviceId);
        Assert.Equal(_store.Identity.CertificateFingerprint, result.Fingerprint);
    }

    [Fact]
    public void TlsCredential_IsTheSameCertificate()
    {
        Assert.True(_store.TlsCertificate.HasPrivateKey);
        Assert.Equal(_store.Certificate.RawData, _store.TlsCertificate.RawData);
    }

    [Fact]
    public void ExpiredCertificate_IsRejected()
    {
        using var certificate = X509CertificateLoader.LoadCertificate(_store.Certificate.RawData);

        Assert.False(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow.AddYears(30), out _, out var failure));
        Assert.Contains("not currently valid", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongSubject_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = DeviceLike("CN=Someone Else", key, key);

        Assert.False(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow, out _, out var failure));
        Assert.Contains("not a Roomie device ID", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgedSelfSignature_IsRejected()
    {
        // Subject and issuer match, but the signature was made with a different key than the one inside.
        using var subjectKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = DeviceLike(DeviceCertificateNames.SubjectFor(Guid.NewGuid()), subjectKey, otherKey);

        Assert.False(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow, out _, out var failure));
        Assert.Contains("signature is invalid", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongCurve_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var certificate = DeviceLike(DeviceCertificateNames.SubjectFor(Guid.NewGuid()), key, key, HashAlgorithmName.SHA256);

        Assert.False(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow, out _, out var failure));
        Assert.Contains("P-256", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void RsaCertificate_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(DeviceCertificateNames.SubjectFor(Guid.NewGuid()), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        Assert.False(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow, out _, out _));
    }

    [Fact]
    public void MissingExtensions_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(DeviceCertificateNames.SubjectFor(Guid.NewGuid()), key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        Assert.False(DeviceCertificateValidator.TryValidate(certificate, DateTimeOffset.UtcNow, out _, out var failure));
        Assert.Contains("extensions", failure, StringComparison.Ordinal);
    }

    private static X509Certificate2 DeviceLike(string subject, ECDsa subjectKey, ECDsa signingKey, HashAlgorithmName? hash = null)
    {
        var request = new CertificateRequest(subject, subjectKey, hash ?? HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));

        var serial = RandomNumberGenerator.GetBytes(8);
        return request.Create(
            new X500DistinguishedName(subject),
            X509SignatureGenerator.CreateForECDsa(signingKey),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            serial);
    }
}
