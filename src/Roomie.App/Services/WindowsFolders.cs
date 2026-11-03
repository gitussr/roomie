using System.Runtime.InteropServices;

namespace Roomie.App.Services;

/// <summary>Folders Windows knows about but .NET has no name for.</summary>
public static partial class WindowsFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder, wherever they moved it, or null if Windows can't say.</summary>
    public static string? Downloads()
    {
        var id = DownloadsId;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
