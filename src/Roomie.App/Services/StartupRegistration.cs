using Microsoft.Win32;

namespace Roomie.App.Services;

/// <summary>Starts Roomie when this user signs in to Windows, through the per-user Run key, which needs no administrator.</summary>
public static class StartupRegistration
{
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Roomie";

    /// <summary>Turns starting with Windows on or off. Also corrects the path if a portable copy was moved.</summary>
    /// <returns>False if Windows refused the change.</returns>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            if (ProgramPath() is not { } path)
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{path}\" {BackgroundArgument}", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// An installed package's folder has its version in its name, so that path stops working at the next update, and
    /// Windows won't start a packaged program from it anyway. The alias in the package manifest stays the same.
    /// </summary>
    private static string? ProgramPath() => PackageInfo.IsPackaged
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "roomie.exe")
        : Environment.ProcessPath;
}
