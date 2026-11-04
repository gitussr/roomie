namespace Roomie.Core.Configuration;

public enum ThemeChoice
{
    /// <summary>Follows the Windows setting, including when it changes.</summary>
    System,

    Light,

    Dark,
}

/// <summary>What the user chose in Settings. Everything has a working default, so a lost file costs only preferences.</summary>
/// <param name="ReceiveFolder">Where received files go. Null means the Windows Downloads folder.</param>
/// <param name="HasSeenTrayHint">The user has been told once that closing the window leaves Roomie running.</param>
/// <param name="HasSeenWelcome">The user finished or skipped the first-run welcome.</param>
public sealed record AppSettings(
    ThemeChoice Theme = ThemeChoice.System,
    bool CloseToTray = true,
    bool StartWithWindows = false,
    bool Notifications = true,
    bool ShowMessageText = true,
    string? ReceiveFolder = null,
    bool HasSeenTrayHint = false,
    bool HasSeenWelcome = false)
{
    /// <summary>
    /// Whether to walk this user through naming and pairing. Someone with a paired device already knows how, which is
    /// what keeps it away from people updating from a version that had no welcome.
    /// </summary>
    public bool NeedsWelcome(int pairedDevices) => !HasSeenWelcome && pairedDevices == 0;
}
