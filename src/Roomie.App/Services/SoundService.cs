using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using Roomie.Core.Sounds;
using Roomie.Infrastructure.Storage;

namespace Roomie.App.Services;

/// <summary>Plays Roomie's sounds, when they're turned on and Windows isn't being kept quiet. Call on the UI thread.</summary>
public sealed partial class SoundService(JsonSettingsStore settings) : IDisposable
{
    private const long MinimumGapMs = 400;
    private const int NotificationsWelcome = 5;

    /// <summary>The active Focus Assist mode: 0 off, 1 priority only, 2 alarms only. Not a documented name, see <see cref="IsFocusAssistOn"/>.</summary>
    private const ulong FocusAssistState = 0x0D83063EA3BF5075;

    private readonly Dictionary<SoundEffect, SoundPlayer> _players = [];
    private long _lastPlayed = -MinimumGapMs;

    public void Play(SoundEffect effect)
    {
        var now = Environment.TickCount64;
        if (!settings.Current.Sounds || now - _lastPlayed < MinimumGapMs || IsWindowsQuiet())
        {
            return;
        }

        _lastPlayed = now;
        PlayNow(effect);
    }

    /// <summary>For someone asking to hear it, so nothing that keeps Roomie quiet applies.</summary>
    public void PlayTest(SoundEffect effect)
    {
        _lastPlayed = Environment.TickCount64;
        PlayNow(effect);
    }

    private void PlayNow(SoundEffect effect)
    {
        try
        {
            if (!_players.TryGetValue(effect, out var player))
            {
                player = new SoundPlayer(new MemoryStream(SoundBank.Render(effect)));
                player.Load();
                _players[effect] = player;
            }

            // Doesn't wait for it to finish.
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or ExternalException)
        {
            // A sound that can't play must never get in the way of the message it announces.
        }
    }

    public void Dispose()
    {
        foreach (var player in _players.Values)
        {
            player.Dispose();
        }

        _players.Clear();
    }

    private static bool IsWindowsQuiet() => !DoesWindowsAcceptNotifications() || IsFocusAssistOn();

    /// <summary>False for a full-screen game or video, a presentation, or a locked computer nobody is at.</summary>
    private static bool DoesWindowsAcceptNotifications()
    {
        try
        {
            return NativeMethods.SHQueryUserNotificationState(out var state) != 0 || state == NotificationsWelcome;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    /// <summary>
    /// Windows 10 has no documented way to read this, so this uses the state its own shell publishes. If a later Windows
    /// changes it, the read fails or says off, and the only cost is a sound during Focus Assist.
    /// </summary>
    private static bool IsFocusAssistOn()
    {
        try
        {
            var name = FocusAssistState;
            var size = (uint)sizeof(int);
            return NativeMethods.NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, out var mode, ref size) == 0
                && size == sizeof(int)
                && mode != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("shell32.dll")]
        public static partial int SHQueryUserNotificationState(out int state);

        [LibraryImport("ntdll.dll")]
        public static partial int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, out int buffer, ref uint bufferSize);
    }
}
