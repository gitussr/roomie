using System.Runtime.InteropServices;

namespace Roomie.App.Services;

/// <summary>Whether Roomie is running from its installed MSIX package rather than a portable copy.</summary>
public static partial class PackageInfo
{
    private const int NoPackage = 15700;

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int length, IntPtr name);
}
