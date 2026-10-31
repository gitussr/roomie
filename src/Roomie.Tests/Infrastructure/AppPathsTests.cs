using Roomie.Infrastructure.Storage;

namespace Roomie.Tests.Infrastructure;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RoomieTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureCreated_CreatesEveryDataDirectory()
    {
        var paths = new AppPaths(_root);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Database));
        Assert.True(Directory.Exists(paths.Downloads));
        Assert.True(Directory.Exists(paths.Attachments));
        Assert.True(Directory.Exists(paths.Logs));
        Assert.True(Directory.Exists(paths.Config));
        Assert.True(Directory.Exists(paths.Identity));
    }

    [Fact]
    public void Identity_IsNotInsideChatDataDirectories()
    {
        var paths = new AppPaths(_root);

        foreach (var chatDirectory in new[] { paths.Database, paths.Downloads, paths.Attachments })
        {
            Assert.False(paths.Identity.StartsWith(chatDirectory, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Constructor_RejectsBlankRoot()
    {
        Assert.Throws<ArgumentException>(() => new AppPaths(" "));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
