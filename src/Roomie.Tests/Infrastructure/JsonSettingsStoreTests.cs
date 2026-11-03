using Roomie.Core.Configuration;
using Roomie.Infrastructure.Storage;

namespace Roomie.Tests.Infrastructure;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "roomie-tests", Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_folder, "config", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void NoFile_GivesTheDefaults_WithoutCreatingOne()
    {
        var store = new JsonSettingsStore(SettingsFile);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.Null(store.LoadProblem);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void AChange_SurvivesARestart()
    {
        var store = new JsonSettingsStore(SettingsFile);

        Assert.True(store.Update(s => s with { Theme = ThemeChoice.Dark, CloseToTray = false, ReceiveFolder = @"D:\Shared" }));

        var reopened = new JsonSettingsStore(SettingsFile);
        Assert.Equal(ThemeChoice.Dark, reopened.Current.Theme);
        Assert.False(reopened.Current.CloseToTray);
        Assert.Equal(@"D:\Shared", reopened.Current.ReceiveFolder);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"theme\":\"purple\"}")]
    [InlineData("{\"theme\":99}")]
    public void AnUnreadableFile_GivesTheDefaults_AndIsLeftAlone(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, content);

        var store = new JsonSettingsStore(SettingsFile);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.NotNull(store.LoadProblem);
        Assert.Equal(content, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void AFileFromAnOlderVersion_KeepsDefaultsForWhatItLacks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, "{\"theme\":\"light\"}");

        var store = new JsonSettingsStore(SettingsFile);

        Assert.Equal(ThemeChoice.Light, store.Current.Theme);
        Assert.True(store.Current.CloseToTray);
        Assert.True(store.Current.Notifications);
    }

    [Fact]
    public void WhenSavingFails_TheChangeStillHoldsForThisRun()
    {
        Directory.CreateDirectory(_folder);

        // A folder where the file should be makes the final rename fail.
        Directory.CreateDirectory(SettingsFile);
        var store = new JsonSettingsStore(Path.Combine(_folder, "config", "settings.json", "..", "settings.json"));

        Assert.False(store.Update(s => s with { Theme = ThemeChoice.Dark }));
        Assert.Equal(ThemeChoice.Dark, store.Current.Theme);
    }
}
