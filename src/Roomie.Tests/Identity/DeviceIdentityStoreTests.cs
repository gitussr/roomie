using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Roomie.Core.Identity;
using Roomie.Infrastructure.Identity;

namespace Roomie.Tests.Identity;

public sealed class DeviceIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RoomieTests", Guid.NewGuid().ToString("N"), "identity");

    private string MetadataPath => Path.Combine(_directory, DeviceIdentityStore.MetadataFileName);
    private string KeyPath => Path.Combine(_directory, DeviceIdentityStore.PrivateKeyFileName);
    private string CertificatePath => Path.Combine(_directory, DeviceIdentityStore.CertificateFileName);

    [Fact]
    public void LoadOrCreate_OnFirstLaunch_CreatesIdentityFiles()
    {
        using var store = new DeviceIdentityStore(_directory);

        var result = store.LoadOrCreate("Alice");

        Assert.Equal(IdentityLoadOutcome.Created, result.Outcome);
        Assert.Equal("Alice", result.Identity.Username);
        Assert.NotEqual(Guid.Empty, result.Identity.DeviceId);
        Assert.Matches("^SHA256:[0-9A-F]{64}$", result.Identity.CertificateFingerprint);
        Assert.Equal(DeviceIdentity.CurrentVersion, result.Identity.IdentityVersion);
        Assert.True(File.Exists(MetadataPath));
        Assert.True(File.Exists(KeyPath));
        Assert.True(File.Exists(CertificatePath));
    }

    [Fact]
    public void LoadOrCreate_CertificateIsSelfSignedP256WithUsablePrivateKey()
    {
        using var store = new DeviceIdentityStore(_directory);
        var identity = store.LoadOrCreate("Alice").Identity;

        var certificate = store.Certificate;
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(certificate.Subject, certificate.Issuer);

        using var privateKey = certificate.GetECDsaPrivateKey();
        Assert.NotNull(privateKey);
        Assert.Equal(256, privateKey.KeySize);

        var data = Encoding.UTF8.GetBytes("pairing transcript");
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256);
        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey), out _);
        Assert.True(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void LoadOrCreate_AfterRestart_ReusesSameIdentity()
    {
        DeviceIdentity first;
        using (var store = new DeviceIdentityStore(_directory))
        {
            first = store.LoadOrCreate("Alice").Identity;
        }

        using var restarted = new DeviceIdentityStore(_directory);
        var result = restarted.LoadOrCreate("Someone else");

        Assert.Equal(IdentityLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(first, result.Identity);
        Assert.True(restarted.Certificate.HasPrivateKey);
    }

    [Fact]
    public void ChangeUsername_PersistsNameAndPreservesDeviceIdentity()
    {
        DeviceIdentity original;
        using (var store = new DeviceIdentityStore(_directory))
        {
            original = store.LoadOrCreate("Alice").Identity;
            store.ChangeUsername("  Office PC  ");
        }

        using var restarted = new DeviceIdentityStore(_directory);
        var reloaded = restarted.LoadOrCreate("ignored").Identity;

        Assert.Equal("Office PC", reloaded.Username);
        Assert.Equal(original.DeviceId, reloaded.DeviceId);
        Assert.Equal(original.CertificateFingerprint, reloaded.CertificateFingerprint);
    }

    [Fact]
    public void ChangeUsername_RejectsInvalidName()
    {
        using var store = new DeviceIdentityStore(_directory);
        store.LoadOrCreate("Alice");

        Assert.Throws<ArgumentException>(() => store.ChangeUsername("   "));
        Assert.Equal("Alice", store.Identity.Username);
    }

    [Fact]
    public void LoadOrCreate_WhenKeyCannotBeDecrypted_MovesIdentityAsideAndRegenerates()
    {
        DeviceIdentity original;
        using (var store = new DeviceIdentityStore(_directory))
        {
            original = store.LoadOrCreate("Alice").Identity;
        }

        // A copied identity folder fails DPAPI decryption the same way a damaged blob does.
        var blob = File.ReadAllBytes(KeyPath);
        blob[^1] ^= 0xFF;
        blob[blob.Length / 2] ^= 0xFF;
        File.WriteAllBytes(KeyPath, blob);

        using var restarted = new DeviceIdentityStore(_directory);
        var result = restarted.LoadOrCreate("Alice");

        Assert.Equal(IdentityLoadOutcome.RegeneratedForeignIdentity, result.Outcome);
        Assert.NotEqual(original.DeviceId, result.Identity.DeviceId);
        Assert.NotEqual(original.CertificateFingerprint, result.Identity.CertificateFingerprint);
        Assert.NotNull(result.OrphanedDirectory);
        Assert.True(File.Exists(Path.Combine(result.OrphanedDirectory, DeviceIdentityStore.PrivateKeyFileName)));
        Assert.True(File.Exists(Path.Combine(result.OrphanedDirectory, DeviceIdentityStore.MetadataFileName)));
    }

    [Fact]
    public void LoadOrCreate_WhenKeyFileIsLocked_ThrowsWithoutRegenerating()
    {
        DeviceIdentity original;
        using (var store = new DeviceIdentityStore(_directory))
        {
            original = store.LoadOrCreate("Alice").Identity;
        }

        using (new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var restarted = new DeviceIdentityStore(_directory);
            Assert.Throws<IOException>(() => restarted.LoadOrCreate("Alice"));
        }

        using var afterUnlock = new DeviceIdentityStore(_directory);
        Assert.Equal(original.DeviceId, afterUnlock.LoadOrCreate("Alice").Identity.DeviceId);
    }

    [Fact]
    public void LoadOrCreate_WhenKeyFileIsMissing_ThrowsInsteadOfRegenerating()
    {
        using (var store = new DeviceIdentityStore(_directory))
        {
            store.LoadOrCreate("Alice");
        }

        File.Delete(KeyPath);

        using var restarted = new DeviceIdentityStore(_directory);
        Assert.Throws<IdentityStoreException>(() => restarted.LoadOrCreate("Alice"));
        Assert.True(File.Exists(MetadataPath));
    }

    [Fact]
    public void LoadOrCreate_WhenFingerprintIsTampered_Throws()
    {
        using (var store = new DeviceIdentityStore(_directory))
        {
            store.LoadOrCreate("Alice");
        }

        var json = JsonNode.Parse(File.ReadAllText(MetadataPath))!;
        json["certificateFingerprint"] = "SHA256:" + new string('A', 64);
        File.WriteAllText(MetadataPath, json.ToJsonString());

        using var restarted = new DeviceIdentityStore(_directory);
        Assert.Throws<IdentityStoreException>(() => restarted.LoadOrCreate("Alice"));
    }

    [Fact]
    public void LoadOrCreate_WhenFirstLaunchWasInterrupted_ReplacesLeftovers()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(KeyPath, [1, 2, 3]);

        using var store = new DeviceIdentityStore(_directory);
        var result = store.LoadOrCreate("Alice");

        Assert.Equal(IdentityLoadOutcome.Created, result.Outcome);
        Assert.True(File.Exists(MetadataPath));
    }

    [Fact]
    public void PersistedFiles_ContainNoPlaintextPrivateKey()
    {
        using var store = new DeviceIdentityStore(_directory);
        store.LoadOrCreate("Alice");

        using var key = store.Certificate.GetECDsaPrivateKey()!;
        var privateScalar = key.ExportParameters(includePrivateParameters: true).D!;

        foreach (var file in Directory.GetFiles(_directory))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan().IndexOf(privateScalar) < 0, $"{Path.GetFileName(file)} contains the private key.");
        }

        var json = JsonNode.Parse(File.ReadAllText(MetadataPath))!.AsObject();
        Assert.Equal(
            ["certificateFingerprint", "createdAt", "deviceId", "identityVersion", "publicKey", "username"],
            json.Select(property => property.Key).Order());
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_directory)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
