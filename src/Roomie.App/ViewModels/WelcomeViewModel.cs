using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Storage;

namespace Roomie.App.ViewModels;

public enum WelcomeStep
{
    Name,
    Pair,
    Done,
}

/// <summary>
/// The first-run walkthrough: name this computer, then pair with another. Every step can be skipped, and pairing
/// itself stays in the usual pairing panel, so the code comparison is the same here as anywhere else.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly JsonSettingsStore _settings;
    private readonly Func<string, string?> _rename;
    private readonly Action<Guid> _openChat;
    private readonly HashSet<Guid> _alreadyPaired = [];
    private Guid? _pairedDevice;

    /// <param name="settings">Remembers that the walkthrough was seen.</param>
    /// <param name="rename">Renames this computer, and returns what's wrong with the name if it can't.</param>
    /// <param name="openChat">Opens the conversation with a device.</param>
    public WelcomeViewModel(JsonSettingsStore settings, Func<string, string?> rename, Action<Guid> openChat)
    {
        _settings = settings;
        _rename = rename;
        _openChat = openChat;
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameStep), nameof(IsPairStep), nameof(IsDoneStep))]
    private WelcomeStep _step;

    [ObservableProperty]
    private string _nameDraft = string.Empty;

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    private string _pairedName = string.Empty;

    [ObservableProperty]
    private bool _hasNoDevices = true;

    public bool IsNameStep => Step == WelcomeStep.Name;

    public bool IsPairStep => Step == WelcomeStep.Pair;

    public bool IsDoneStep => Step == WelcomeStep.Done;

    /// <summary>Devices that could still be paired with. Not paired and not blocked.</summary>
    public ObservableCollection<NearbyDevice> Devices { get; } = [];

    /// <summary>Starts from the first step. Only a device paired from now on counts as this walkthrough's success.</summary>
    public void Show(string currentName, IEnumerable<TrustedPeer> trusted)
    {
        _alreadyPaired.Clear();
        _alreadyPaired.UnionWith(trusted.Select(p => p.DeviceId));
        _pairedDevice = null;
        NameDraft = currentName;
        NameError = null;
        Step = WelcomeStep.Name;
        IsVisible = true;
    }

    /// <summary>Follows the network. Pairing can finish on any step, because the other computer may have started it.</summary>
    public void Update(IReadOnlyList<NearbyDevice> devices, IEnumerable<TrustedPeer> trusted)
    {
        if (!IsVisible)
        {
            return;
        }

        var wanted = devices.Where(d => !d.CanForget && !d.CanUnblock).ToList();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= Devices.Count)
            {
                Devices.Add(wanted[i]);
            }
            else if (Devices[i] != wanted[i])
            {
                // Only what changed, so a button stays under the mouse.
                Devices[i] = wanted[i];
            }
        }

        while (Devices.Count > wanted.Count)
        {
            Devices.RemoveAt(Devices.Count - 1);
        }

        HasNoDevices = Devices.Count == 0;

        if (Step != WelcomeStep.Done && trusted.FirstOrDefault(p => !_alreadyPaired.Contains(p.DeviceId)) is { } paired)
        {
            _pairedDevice = paired.DeviceId;
            PairedName = paired.Username;
            Step = WelcomeStep.Done;
            MarkSeen();
        }
    }

    /// <summary>Puts it away without opening anything, because something else now needs the screen.</summary>
    public void Dismiss()
    {
        if (IsVisible)
        {
            Finish();
        }
    }

    [RelayCommand]
    private void ContinueToPairing()
    {
        NameError = _rename(NameDraft);
        if (NameError is null)
        {
            Step = WelcomeStep.Pair;
        }
    }

    [RelayCommand]
    private void BackToName() => Step = WelcomeStep.Name;

    [RelayCommand]
    private void Finish()
    {
        MarkSeen();
        IsVisible = false;
    }

    [RelayCommand]
    private void StartChatting()
    {
        var device = _pairedDevice;
        Finish();
        if (device is { } id)
        {
            _openChat(id);
        }
    }

    // A failed save only means seeing this once more next time.
    private void MarkSeen() => _settings.Update(s => s with { HasSeenWelcome = true });
}
