using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Roomie.Core.Identity;

namespace Roomie.Infrastructure.Identity;

/// <summary>
/// Creates, loads and persists this computer's cryptographic identity (architecture.md §1.1).
/// </summary>
/// <remarks>
/// Layout of the identity directory:
/// <list type="bullet">
/// <item><c>device.crt</c>: the self-signed device certificate (DER, public).</item>
/// <item><c>device.key</c>: the PKCS#8 private key encrypted with DPAPI for the current Windows user.</item>
/// <item><c>identity.json</c>: public metadata. Written last, so its presence marks a completed identity.</item>
/// </list>
/// </remarks>
public sealed class DeviceIdentityStore : IDisposable
{
    public const string MetadataFileName = "identity.json";
    public const string CertificateFileName = "device.crt";
    public const string PrivateKeyFileName = "device.key";

    // Binds the DPAPI blob to this purpose so another application running as the same user can't
    // simply call Unprotect on it without knowing the entropy.
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("Roomie.DeviceKey.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private DeviceIdentity? _identity;
    private X509Certificate2? _certificate;

    public DeviceIdentityStore(string identityDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDirectory);

        _directory = Path.GetFullPath(identityDirectory);
        _time = timeProvider ?? TimeProvider.System;
    }

    private string MetadataPath => Path.Combine(_directory, MetadataFileName);
    private string CertificatePath => Path.Combine(_directory, CertificateFileName);
    private string PrivateKeyPath => Path.Combine(_directory, PrivateKeyFileName);

    /// <summary>The loaded identity. Throws until <see cref="LoadOrCreate"/> has succeeded.</summary>
    public DeviceIdentity Identity
    {
        get
        {
            lock (_gate)
            {
                return _identity ?? throw new InvalidOperationException("The device identity has not been loaded.");
            }
        }
    }

    /// <summary>
    /// The device certificate with its private key attached, for TLS and signing. Owned by the store;
    /// callers must not dispose it.
    /// </summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (_gate)
            {
                return _certificate ?? throw new InvalidOperationException("The device identity has not been loaded.");
            }
        }
    }

    /// <summary>
    /// Loads the identity, creating one on first launch.
    /// </summary>
    /// <param name="defaultUsername">Used only when a new identity is generated; it is sanitized first.</param>
    /// <exception cref="IdentityStoreException">The identity on disk is incomplete or inconsistent.</exception>
    /// <exception cref="IOException">The identity files couldn't be read (for example, locked). Never repaired by regenerating.</exception>
    public IdentityLoadResult LoadOrCreate(string defaultUsername)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);

            if (!File.Exists(MetadataPath))
            {
                // identity.json is written last, so leftover key or certificate files without it come from
                // an interrupted first launch. That identity was never used, so it is safe to replace.
                DeleteIfExists(CertificatePath);
                DeleteIfExists(PrivateKeyPath);
                return Create(defaultUsername, IdentityLoadOutcome.Created, orphanedDirectory: null);
            }

            if (!File.Exists(CertificatePath) || !File.Exists(PrivateKeyPath))
            {
                throw new IdentityStoreException(
                    $"The device identity in '{_directory}' is incomplete: the certificate or private key file is missing.");
            }

            var metadata = ReadMetadata();
            var certificateBytes = File.ReadAllBytes(CertificatePath);
            var protectedKey = File.ReadAllBytes(PrivateKeyPath);

            byte[] pkcs8;
            try
            {
                pkcs8 = ProtectedData.Unprotect(protectedKey, KeyEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Only DPAPI failing to decrypt means "foreign identity" (architecture.md §1.1). Every other
                // failure above or below is surfaced instead of silently replacing the identity.
                var orphaned = MoveToOrphanedDirectory();
                return Create(defaultUsername, IdentityLoadOutcome.RegeneratedForeignIdentity, orphaned);
            }

            try
            {
                var (identity, certificate) = Validate(metadata, certificateBytes, pkcs8);
                SetCurrent(identity, certificate);
                return new IdentityLoadResult(identity, IdentityLoadOutcome.Loaded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
    }

    /// <summary>Changes the display name. The device ID and keys are unaffected.</summary>
    /// <exception cref="ArgumentException">The username breaks the rules in <see cref="Username"/>.</exception>
    public DeviceIdentity ChangeUsername(string username)
    {
        if (!Username.TryNormalize(username, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(username));
        }

        lock (_gate)
        {
            var current = _identity ?? throw new InvalidOperationException("The device identity has not been loaded.");
            if (current.Username == normalized)
            {
                return current;
            }

            var updated = current with { Username = normalized };
            WriteMetadata(updated);
            _identity = updated;
            return updated;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
            _identity = null;
        }
    }

    private IdentityLoadResult Create(string defaultUsername, IdentityLoadOutcome outcome, string? orphanedDirectory)
    {
        var deviceId = Guid.NewGuid();
        var now = _time.GetUtcNow();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // CreateSelfSigned returns the certificate with the private key already attached.
        var certificate = DeviceCertificateFactory.Create(deviceId, key, now);

        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        var identity = new DeviceIdentity(
            deviceId,
            Username.Sanitize(defaultUsername),
            CertificateFingerprint.FromSubjectPublicKeyInfo(spki),
            Convert.ToBase64String(spki),
            new DateTimeOffset(now.UtcDateTime, TimeSpan.Zero),
            DeviceIdentity.CurrentVersion);

        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            var protectedKey = ProtectedData.Protect(pkcs8, KeyEntropy, DataProtectionScope.CurrentUser);
            WriteAtomically(CertificatePath, certificate.RawData);
            WriteAtomically(PrivateKeyPath, protectedKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        WriteMetadata(identity);

        // TODO(phase 3): SChannel can refuse ephemeral keys as a TLS server credential. If SslStream rejects
        // this certificate, re-import it through a PKCS#12 round trip when building the TLS options.
        SetCurrent(identity, certificate);
        return new IdentityLoadResult(identity, outcome, orphanedDirectory);
    }

    private static (DeviceIdentity Identity, X509Certificate2 Certificate) Validate(
        IdentityFile metadata, byte[] certificateBytes, byte[] pkcs8)
    {
        X509Certificate2 publicCertificate;
        try
        {
            publicCertificate = X509CertificateLoader.LoadCertificate(certificateBytes);
        }
        catch (CryptographicException ex)
        {
            throw new IdentityStoreException("The device certificate is not a valid X.509 certificate.", ex);
        }

        using (publicCertificate)
        {
            using var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(pkcs8, out _);
            }
            catch (CryptographicException ex)
            {
                throw new IdentityStoreException("The device private key decrypted but is not a valid ECDSA key.", ex);
            }

            if (key.KeySize != 256)
            {
                throw new IdentityStoreException("The device private key is not an ECDSA P-256 key.");
            }

            var spki = publicCertificate.PublicKey.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(spki, key.ExportSubjectPublicKeyInfo()))
            {
                throw new IdentityStoreException("The device private key does not match the device certificate.");
            }

            var fingerprint = CertificateFingerprint.FromSubjectPublicKeyInfo(spki);
            if (!CertificateFingerprint.AreEqual(fingerprint, metadata.CertificateFingerprint)
                || metadata.PublicKey != Convert.ToBase64String(spki))
            {
                throw new IdentityStoreException("The identity metadata does not match the device certificate.");
            }

            if (DeviceCertificateFactory.ReadDeviceId(publicCertificate) != metadata.DeviceId)
            {
                throw new IdentityStoreException("The device ID in the identity metadata does not match the device certificate.");
            }

            var identity = new DeviceIdentity(
                metadata.DeviceId,
                Username.Sanitize(metadata.Username),
                fingerprint,
                metadata.PublicKey,
                metadata.CreatedAt,
                metadata.IdentityVersion);

            return (identity, publicCertificate.CopyWithPrivateKey(key));
        }
    }

    private IdentityFile ReadMetadata()
    {
        IdentityFile? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<IdentityFile>(File.ReadAllBytes(MetadataPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new IdentityStoreException($"'{MetadataFileName}' is not valid JSON.", ex);
        }

        if (metadata is null
            || metadata.DeviceId == Guid.Empty
            || string.IsNullOrEmpty(metadata.CertificateFingerprint)
            || string.IsNullOrEmpty(metadata.PublicKey))
        {
            throw new IdentityStoreException($"'{MetadataFileName}' is missing required fields.");
        }

        if (metadata.IdentityVersion != DeviceIdentity.CurrentVersion)
        {
            throw new IdentityStoreException(
                $"Unsupported identity version {metadata.IdentityVersion}; this build supports version {DeviceIdentity.CurrentVersion}.");
        }

        return metadata;
    }

    private void WriteMetadata(DeviceIdentity identity)
    {
        var file = new IdentityFile(
            identity.DeviceId,
            identity.Username,
            identity.CertificateFingerprint,
            identity.PublicKey,
            identity.CreatedAt,
            identity.IdentityVersion);

        WriteAtomically(MetadataPath, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
    }

    private string MoveToOrphanedDirectory()
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(_directory, $"orphaned-{stamp}");
        for (var suffix = 2; Directory.Exists(target); suffix++)
        {
            target = Path.Combine(_directory, $"orphaned-{stamp}-{suffix}");
        }

        Directory.CreateDirectory(target);
        foreach (var path in new[] { PrivateKeyPath, CertificatePath, MetadataPath })
        {
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(target, Path.GetFileName(path)));
            }
        }

        return target;
    }

    private void SetCurrent(DeviceIdentity identity, X509Certificate2 certificate)
    {
        _certificate?.Dispose();
        _certificate = certificate;
        _identity = identity;
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>On-disk shape of <c>identity.json</c>. Contains no secret material.</summary>
    private sealed record IdentityFile(
        Guid DeviceId,
        string Username,
        string CertificateFingerprint,
        string PublicKey,
        DateTimeOffset CreatedAt,
        int IdentityVersion);
}
