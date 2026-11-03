namespace Roomie.Infrastructure.Storage;

/// <summary>
/// Local data layout (pre-architecture.md §9). Identity lives apart from chat data so that
/// clearing history can never delete the device's cryptographic identity.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        Root = Path.GetFullPath(rootDirectory);
        Database = Path.Combine(Root, "database");
        Downloads = Path.Combine(Root, "files", "downloads");
        Attachments = Path.Combine(Root, "files", "attachments");
        Incoming = Path.Combine(Root, "files", "incoming");
        Logs = Path.Combine(Root, "logs");
        Config = Path.Combine(Root, "config");
        Identity = Path.Combine(Root, "identity");
    }

    public string Root { get; }
    public string Database { get; }
    public string Downloads { get; }
    public string Attachments { get; }

    /// <summary>Files still arriving. Nothing here has been verified, and Roomie empties it when it starts.</summary>
    public string Incoming { get; }
    public string Logs { get; }
    public string Config { get; }
    public string Identity { get; }

    public string DatabaseFile => Path.Combine(Database, "messenger.db");
    public string SettingsFile => Path.Combine(Config, "settings.json");

    /// <summary>Paired and blocked devices. Kept out of the database folder, so clearing chat data never unpairs.</summary>
    public string TrustFile => Path.Combine(Config, "trusted-devices.json");
    public string LogFile => Path.Combine(Logs, "application-.log");

    /// <summary>%LOCALAPPDATA%\Roomie</summary>
    public static AppPaths CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roomie"));

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Database, Downloads, Attachments, Incoming, Logs, Config, Identity })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
