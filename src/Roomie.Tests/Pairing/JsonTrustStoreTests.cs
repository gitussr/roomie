using Roomie.Core.Trust;
using Roomie.Infrastructure.Trust;

namespace Roomie.Tests.Pairing;

public sealed class JsonTrustStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "trusted-devices.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TrustAndBlocks_SurviveARestart()
    {
        var paired = new TrustedPeer(Guid.NewGuid(), "Office PC", "SHA256:" + new string('A', 64), DateTimeOffset.UtcNow);
        var blocked = Guid.NewGuid();

        var store = new JsonTrustStore(FilePath);
        store.Trust(paired);
        store.Block(blocked);

        var reloaded = new JsonTrustStore(FilePath);
        Assert.Equal(paired, reloaded.FindTrusted(paired.DeviceId));
        Assert.True(reloaded.IsBlocked(blocked));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void BlockingRevokesTrust_AndTrustingUnblocks()
    {
        var store = new JsonTrustStore(FilePath);
        var peer = new TrustedPeer(Guid.NewGuid(), "Laptop", "SHA256:" + new string('B', 64), DateTimeOffset.UtcNow);

        store.Trust(peer);
        store.Block(peer.DeviceId);
        Assert.Null(store.FindTrusted(peer.DeviceId));

        store.Trust(peer);
        Assert.False(store.IsBlocked(peer.DeviceId));
        Assert.NotNull(new JsonTrustStore(FilePath).FindTrusted(peer.DeviceId));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":2,"trusted":[],"blocked":[]}""")]
    [InlineData("""{"version":1,"trusted":[{"deviceId":"7f92a31c-8d20-4b71-a6e3-0123456789ab","username":"x","certificateFingerprint":"MD5:00","trustedAt":"2026-10-31T00:00:00Z"}],"blocked":[]}""")]
    public void CorruptFile_IsReportedAndLeftAlone(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, contents);

        Assert.Throws<TrustStoreException>(() => new JsonTrustStore(FilePath));
        Assert.Equal(contents, File.ReadAllText(FilePath));
    }
}
