using System.Text.Json;
using System.Text.Json.Serialization;
using Roomie.Core.Configuration;

namespace Roomie.Infrastructure.Storage;

/// <summary>
/// Preferences in <c>config\settings.json</c>, written atomically. Unlike the trust list, a file that can't be
/// read is no reason to refuse to start: Roomie uses the defaults and leaves the file for the user's next change.
/// </summary>
public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public JsonSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Current = Read(out var problem);
        LoadProblem = problem;
    }

    public AppSettings Current { get; private set; }

    /// <summary>Why the saved settings couldn't be used, if they couldn't.</summary>
    public string? LoadProblem { get; }

    /// <summary>Applies a change and saves it. The change holds for this run even if saving fails.</summary>
    /// <returns>False if it couldn't be saved.</returns>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            Current = change(Current);
            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, Current, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private AppSettings Read(out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return new AppSettings();
        }
    }
}
