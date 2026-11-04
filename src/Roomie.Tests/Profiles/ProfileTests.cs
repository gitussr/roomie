using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.Core.Connections;
using Roomie.Core.Discovery;
using Roomie.Core.Profiles;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Profiles;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Profiles;

namespace Roomie.Tests.Profiles;

public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));

    public ProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Png(int size = ProfileProtocol.PictureSize)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill<byte>(pixels, 200);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static JsonElement Payload(int? preset, string? picture) => SessionPayloads.ToElement(new ProfileUpdatePayload(preset, picture));

    // ---- What may arrive from another device ----

    [Fact]
    public void AcceptablePicture_IsExactlyASmallSquarePng()
    {
        Assert.True(ProfileProtocol.IsAcceptablePicture(Png()));
        Assert.False(ProfileProtocol.IsAcceptablePicture(Png(97)));
        Assert.False(ProfileProtocol.IsAcceptablePicture(new byte[100]));
        Assert.False(ProfileProtocol.IsAcceptablePicture([]));

        var tooLong = new byte[ProfileProtocol.MaxPictureBytes + 1];
        Png().CopyTo(tooLong, 0);
        Assert.False(ProfileProtocol.IsAcceptablePicture(tooLong));
    }

    [Fact]
    public void WorstCasePicture_FitsTheLimit_AndAControlFrame()
    {
        // Noise doesn't compress, so this is about as large as a picture of this size can be.
        var pixels = new byte[ProfileProtocol.PictureSize * ProfileProtocol.PictureSize * 4];
        new Random(1).NextBytes(pixels);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(96, 96, 96, 96, PixelFormats.Bgra32, null, pixels, 96 * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        Assert.True(ProfileProtocol.IsAcceptablePicture(stream.ToArray()));
        var payload = SessionPayloads.ToElement(ProfileProtocol.ToPayload(AvatarChoice.FromPicture(stream.ToArray())));
        Assert.True(payload.GetRawText().Length < RmpFraming.MaxControlPayload - 1024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProfileProtocol.PresetCount + 1)]
    public void APresetThatDoesntExist_IsRefused(int preset) =>
        Assert.Equal(RmpPayloadCheck.Malformed, ProfileProtocol.CheckUpdate(Payload(preset, null)));

    [Fact]
    public void BothAPresetAndAPicture_IsRefused() =>
        Assert.Equal(RmpPayloadCheck.Malformed, ProfileProtocol.CheckUpdate(Payload(1, Convert.ToBase64String(Png()))));

    [Fact]
    public void WhatIsNotAnAcceptablePicture_IsRefused()
    {
        Assert.Equal(RmpPayloadCheck.Malformed, ProfileProtocol.CheckUpdate(Payload(null, "not base64!")));
        Assert.Equal(RmpPayloadCheck.Malformed, ProfileProtocol.CheckUpdate(Payload(null, Convert.ToBase64String(Png(200)))));
        Assert.Equal(RmpPayloadCheck.TooLarge, ProfileProtocol.CheckUpdate(Payload(null, new string('A', 60_000))));
    }

    [Fact]
    public void ValidUpdates_AreAccepted()
    {
        Assert.Equal(RmpPayloadCheck.Valid, ProfileProtocol.CheckUpdate(Payload(null, null)));
        Assert.Equal(RmpPayloadCheck.Valid, ProfileProtocol.CheckUpdate(Payload(ProfileProtocol.PresetCount, null)));
        Assert.Equal(RmpPayloadCheck.Valid, ProfileProtocol.CheckUpdate(Payload(null, Convert.ToBase64String(Png()))));
    }

    [Fact]
    public void AnUnpairedDevice_CantSendAPicture()
    {
        var rejection = RmpMessagePolicy.Check(RmpMessageTypes.ProfileUpdate, Payload(1, null), isTrusted: false, new HashSet<string> { RmpCapabilities.Profile });

        Assert.Equal(RmpErrorCodes.PeerNotTrusted, rejection?.Code);
    }

    [Fact]
    public void WithoutTheCapability_APictureIsRefused()
    {
        var rejection = RmpMessagePolicy.Check(RmpMessageTypes.ProfileUpdate, Payload(1, null), isTrusted: true, new HashSet<string>());

        Assert.Equal(RmpErrorCodes.UnsupportedMessage, rejection?.Code);
    }

    // ---- Saving ----

    [Fact]
    public void Store_KeepsOneChoicePerDevice_AndClearsIt()
    {
        var store = new FileAvatarStore(Path.Combine(_root, "avatars"));
        var id = Guid.NewGuid();

        Assert.True(store.Load(id).IsSameAs(AvatarChoice.None));

        store.Save(id, AvatarChoice.FromPreset(3));
        Assert.Equal(3, store.Load(id).Preset);

        store.Save(id, AvatarChoice.FromPicture(Png()));
        Assert.Null(store.Load(id).Preset);
        Assert.True(store.Load(id).IsSameAs(AvatarChoice.FromPicture(Png())));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "avatars")));

        store.Save(id, AvatarChoice.None);
        Assert.True(store.Load(id).IsSameAs(AvatarChoice.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "avatars")));
    }

    [Theory]
    [InlineData("png", "not a picture")]
    [InlineData("preset", "99")]
    [InlineData("preset", "-1")]
    [InlineData("preset", "fox")]
    public void Store_TreatsADamagedFileAsNoPicture(string extension, string content)
    {
        var folder = Path.Combine(_root, "avatars");
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(folder, $"{id:N}.{extension}"), content);

        Assert.True(new FileAvatarStore(folder).Load(id).IsSameAs(AvatarChoice.None));
    }

    [Fact]
    public void Store_RemovesOtherDevices_ButNotOtherFiles()
    {
        var folder = Path.Combine(_root, "avatars");
        var store = new FileAvatarStore(folder);
        var (keep, drop) = (Guid.NewGuid(), Guid.NewGuid());
        store.Save(keep, AvatarChoice.FromPreset(1));
        store.Save(drop, AvatarChoice.FromPreset(2));
        var other = Path.Combine(folder, "notes.txt");
        File.WriteAllText(other, "x");

        store.RemoveAllExcept(new HashSet<Guid> { keep });

        Assert.Equal(1, store.Load(keep).Preset);
        Assert.Null(store.Load(drop).Preset);
        Assert.True(File.Exists(other));
    }

    // ---- Preparing an uploaded picture ----

    private string SaveTwoColours(int width, int height, BitmapEncoder encoder, BitmapMetadata? metadata = null, string name = "photo.png")
    {
        // The top half red and the bottom half blue.
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 4;
                pixels[at] = (byte)(y < height / 2 ? 0 : 255);
                pixels[at + 2] = (byte)(y < height / 2 ? 255 : 0);
                pixels[at + 3] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapSource frame = encoder is JpegBitmapEncoder ? new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0) : source;
        encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static Color PixelAt(byte[] png, int x, int y)
    {
        using var stream = new MemoryStream(png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var pixel = new byte[4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static bool IsRed(Color c) => c is { R: > 200, B: < 60 };

    private static bool IsBlue(Color c) => c is { B: > 200, R: < 60 };

    [Theory]
    [InlineData(1000, 500)]
    [InlineData(500, 1000)]
    [InlineData(96, 96)]
    [InlineData(40, 40)]
    public void Normalize_GivesTheAgreedSquare_FromAnySize(int width, int height)
    {
        var png = AvatarImage.Normalize(SaveTwoColours(width, height, new PngBitmapEncoder()));

        Assert.True(ProfileProtocol.IsAcceptablePicture(png));
        Assert.True(IsRed(PixelAt(png, 48, 10)));
        Assert.True(IsBlue(PixelAt(png, 48, 85)));
    }

    [Theory]
    [InlineData((ushort)1, true, false)]
    [InlineData((ushort)3, false, false)]
    [InlineData((ushort)6, false, true)]
    public void Normalize_TurnsAPhotoUpright(ushort orientation, bool redOnTop, bool redOnRight)
    {
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);

        var png = AvatarImage.Normalize(SaveTwoColours(200, 200, new JpegBitmapEncoder { QualityLevel = 100 }, metadata, "photo.jpg"));

        if (redOnRight)
        {
            Assert.True(IsRed(PixelAt(png, 85, 48)));
            Assert.True(IsBlue(PixelAt(png, 10, 48)));
        }
        else
        {
            Assert.Equal(redOnTop, IsRed(PixelAt(png, 48, 10)));
            Assert.Equal(!redOnTop, IsRed(PixelAt(png, 48, 85)));
        }
    }

    [Fact]
    public void Normalize_LeavesOtherDetailsOfAPhotoBehind()
    {
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=270}", "SECRET-LOCATION");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)1);
        var path = SaveTwoColours(200, 200, new JpegBitmapEncoder(), metadata, "photo.jpg");
        Assert.Contains("SECRET-LOCATION", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);

        var png = AvatarImage.Normalize(path);

        Assert.DoesNotContain("SECRET-LOCATION", System.Text.Encoding.ASCII.GetString(png), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_ExplainsAFileThatIsntAPicture()
    {
        var path = Path.Combine(_root, "notes.png");
        File.WriteAllText(path, "this is not a picture");

        Assert.Throws<AvatarImageException>(() => AvatarImage.Normalize(path));
        Assert.Throws<AvatarImageException>(() => AvatarImage.Normalize(Path.Combine(_root, "missing.png")));
    }

    // ---- Between real nodes ----

    [Fact]
    public async Task APicture_ReachesAPairedDevice_WhenItConnects_AndChangesFollow()
    {
        await using var alice = await StartAsync("Alice");
        await using var bob = await StartAsync("Bob");
        var png = Png();
        Assert.True(alice.Profiles.SetOwn(AvatarChoice.FromPicture(png)));
        Pair(alice, bob);

        await ConnectAsync(alice, bob);
        await WaitUntil(() => bob.Store.Load(alice.Id).IsSameAs(AvatarChoice.FromPicture(png)));

        Assert.True(alice.Profiles.SetOwn(AvatarChoice.FromPreset(5)));
        await WaitUntil(() => bob.Store.Load(alice.Id).Preset == 5);

        Assert.True(alice.Profiles.SetOwn(AvatarChoice.None));
        await WaitUntil(() => bob.Store.Load(alice.Id).IsSameAs(AvatarChoice.None));
    }

    [Fact]
    public async Task ChangedEvent_IsRaisedOnlyWhenSomethingChanged()
    {
        await using var alice = await StartAsync("Alice");
        await using var bob = await StartAsync("Bob");
        Pair(alice, bob);
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(2));
        var heard = 0;
        bob.Profiles.Changed += (_, _) => Interlocked.Increment(ref heard);

        await ConnectAsync(alice, bob);
        await WaitUntil(() => bob.Store.Load(alice.Id).Preset == 2);
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(2));
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(3));
        await WaitUntil(() => bob.Store.Load(alice.Id).Preset == 3);

        Assert.Equal(2, Volatile.Read(ref heard));
    }

    [Fact]
    public async Task PairingOnALiveSession_SendsThePicture()
    {
        await using var alice = await StartAsync("Alice");
        await using var bob = await StartAsync("Bob");
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(4));
        await ConnectAsync(alice, bob, PeerConnectionState.ConnectedUntrusted);
        Assert.Equal(0, bob.Store.Load(alice.Id).Preset ?? 0);

        Pair(alice, bob);
        Assert.True(alice.Connections.TryGetSession(bob.Id, out var fromAlice));
        Assert.True(bob.Connections.TryGetSession(alice.Id, out var fromBob));
        bob.Connections.MarkTrusted(fromBob);
        alice.Connections.MarkTrusted(fromAlice);

        await WaitUntil(() => bob.Store.Load(alice.Id).Preset == 4);
    }

    [Fact]
    public async Task ADeviceWithoutTheFeature_IsSentNothing_AndChatStaysUp()
    {
        await using var alice = await StartAsync("Alice");
        await using var old = await StartAsync("Old", capabilities: [RmpCapabilities.Text]);
        Pair(alice, old);
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(6));

        await ConnectAsync(alice, old);
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(7));
        await Task.Delay(300);

        Assert.Null(old.Store.Load(alice.Id).Preset);
        Assert.Equal(PeerConnectionState.ConnectedTrusted, alice.Connections.GetInfo(old.Id).State);
    }

    [Fact]
    public async Task ForgettingADevice_RemovesItsPicture_ButNotOurOwn()
    {
        await using var alice = await StartAsync("Alice");
        await using var bob = await StartAsync("Bob");
        Pair(alice, bob);
        alice.Profiles.SetOwn(AvatarChoice.FromPreset(8));
        bob.Profiles.SetOwn(AvatarChoice.FromPreset(9));
        await ConnectAsync(alice, bob);
        await WaitUntil(() => bob.Store.Load(alice.Id).Preset == 8);

        bob.Trust.Remove(alice.Id);

        Assert.Null(bob.Store.Load(alice.Id).Preset);
        Assert.Equal(9, bob.Store.Load(bob.Id).Preset);
    }

    private static void Pair(ProfileNode a, ProfileNode b)
    {
        a.Trust.Trust(new TrustedPeer(b.Id, "B", b.Identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
        b.Trust.Trust(new TrustedPeer(a.Id, "A", a.Identity.Identity.CertificateFingerprint, DateTimeOffset.UtcNow));
    }

    private static async Task ConnectAsync(ProfileNode a, ProfileNode b, PeerConnectionState expected = PeerConnectionState.ConnectedTrusted)
    {
        a.Sees(b);
        b.Sees(a);
        await a.Connections.ConnectAsync(b.Id);
        await WaitUntil(() => a.Connections.GetInfo(b.Id).State == expected && b.Connections.GetInfo(a.Id).State == expected);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(20);
        }
    }

    private async Task<ProfileNode> StartAsync(string name, IReadOnlyList<string>? capabilities = null)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var identity = new DeviceIdentityStore(Path.Combine(folder, "identity"));
        identity.LoadOrCreate(name);
        var registry = new PeerRegistry(TimeSpan.FromMinutes(1));
        var trust = new InMemoryTrustStore();
        var connections = new ConnectionManager(identity, identity, new StaticInterfaces(), registry, trust,
            new ConnectionOptions
            {
                Port = 0,
                AppVersion = "0.11.0",
                Capabilities = capabilities ?? [RmpCapabilities.Text, RmpCapabilities.Profile],
                MaintenanceInterval = TimeSpan.FromMilliseconds(20),
                ReconnectDelays = [TimeSpan.FromHours(1)],
                LargerIdDialDelay = TimeSpan.FromHours(1),
            },
            TimeProvider.System, NullLogger<ConnectionManager>.Instance);
        var store = new FileAvatarStore(Path.Combine(folder, "avatars"));
        var profiles = new ProfileService(connections, store, identity, trust, TimeProvider.System, NullLogger<ProfileService>.Instance);
        await connections.StartAsync(CancellationToken.None);
        await WaitUntil(() => connections.ListeningEndPoints.Count == 1);
        return new ProfileNode(identity, registry, trust, connections, store, profiles);
    }

    private sealed class StaticInterfaces : ILanInterfaceProvider
    {
        public event EventHandler? InterfacesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LanInterface> GetEligibleInterfaces() => [new LanInterface("lo", "Loopback", new Ipv4Subnet(IPAddress.Loopback, 8))];
    }

    private sealed class ProfileNode(DeviceIdentityStore identity, PeerRegistry registry, InMemoryTrustStore trust, ConnectionManager connections, FileAvatarStore store, ProfileService profiles) : IAsyncDisposable
    {
        public DeviceIdentityStore Identity { get; } = identity;

        public InMemoryTrustStore Trust { get; } = trust;

        public ConnectionManager Connections { get; } = connections;

        public FileAvatarStore Store { get; } = store;

        public ProfileService Profiles { get; } = profiles;

        public Guid Id => Identity.Identity.DeviceId;

        public void Sees(ProfileNode other) => registry.Observe(
            new DiscoveryPacket(DiscoveryPacketType.Announce, other.Id, other.Identity.Identity.Username, other.Connections.ListeningEndPoints[0].Port, "0.11.0"),
            IPAddress.Loopback,
            DateTimeOffset.UtcNow);

        public async ValueTask DisposeAsync()
        {
            Profiles.Dispose();
            await Connections.StopAsync(CancellationToken.None);
            Connections.Dispose();
            Identity.Dispose();
        }
    }
}
