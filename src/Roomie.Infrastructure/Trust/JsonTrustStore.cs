using System.Text.Json;
using Roomie.Core.Identity;
using Roomie.Core.Trust;

namespace Roomie.Infrastructure.Trust;

/// <summary>
/// Trusted and blocked devices in <c>config\trusted-devices.json</c>, written atomically on every change.
/// Kept out of the message database on purpose: clearing or losing history must never unpair anything, and a
/// damaged database must not take the list of trusted keys with it.
/// </summary>
public sealed class JsonTrustStore : InMemoryTrustStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    /// <exception cref="TrustStoreException">The file exists but can't be understood. It is left untouched.</exception>
    public JsonTrustStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);

        if (!File.Exists(_path))
        {
            return;
        }

        TrustFile? file;
        try
        {
            file = JsonSerializer.Deserialize<TrustFile>(File.ReadAllBytes(_path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new TrustStoreException($"'{Path.GetFileName(_path)}' is not valid JSON.", ex);
        }

        if (file is null || file.Version != CurrentVersion || file.Trusted is null || file.Blocked is null)
        {
            throw new TrustStoreException($"'{Path.GetFileName(_path)}' has an unsupported format.");
        }

        foreach (var peer in file.Trusted)
        {
            if (peer.DeviceId == Guid.Empty || !IsFingerprint(peer.CertificateFingerprint) || !Username.TryNormalize(peer.Username, out _, out _))
            {
                throw new TrustStoreException($"'{Path.GetFileName(_path)}' contains an invalid trusted device.");
            }
        }

        Load(file.Trusted, file.Blocked);
    }

    protected override void OnChanged(IReadOnlyList<TrustedPeer> trusted, IReadOnlyCollection<Guid> blocked)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TrustFile(CurrentVersion, [.. trusted], [.. blocked]), JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private static bool IsFingerprint(string? value)
    {
        try
        {
            return value is not null && CertificateFingerprint.ToBytes(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record TrustFile(int Version, List<TrustedPeer> Trusted, List<Guid> Blocked);
}

public sealed class TrustStoreException : Exception
{
    public TrustStoreException()
    {
    }

    public TrustStoreException(string message)
        : base(message)
    {
    }

    public TrustStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
