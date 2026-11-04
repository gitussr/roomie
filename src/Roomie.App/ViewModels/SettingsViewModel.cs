using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Roomie.App.Services;
using Roomie.Core.Configuration;
using Roomie.Core.Identity;
using Roomie.Core.Profiles;
using Roomie.Core.Sounds;
using Roomie.Infrastructure.Profiles;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Files;
using Roomie.Network.Profiles;

namespace Roomie.App.ViewModels;

/// <summary>The Settings screen. Every change takes effect and is saved at once.</summary>
/// <summary>A built-in avatar to choose from.</summary>
public sealed record AvatarOption(int Id, string Name, AvatarVisual Visual, bool IsSelected);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly JsonSettingsStore _store;
    private readonly FileTransferService _files;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;
    private readonly ProfileService _profiles;
    private readonly AvatarCatalog _avatars;
    private readonly ILocalIdentityProvider _identity;
    private readonly SoundService _sounds;

    public SettingsViewModel(JsonSettingsStore store, FileTransferService files, ThemeService theme, AppPaths paths, ProfileService profiles, AvatarCatalog avatars, ILocalIdentityProvider identity, SoundService sounds)
    {
        _sounds = sounds;
        _store = store;
        _files = files;
        _theme = theme;
        _paths = paths;
        _profiles = profiles;
        _avatars = avatars;
        _identity = identity;
        RefreshOwnAvatar();

        var current = store.Current;
        _isSystemTheme = current.Theme == ThemeChoice.System;
        _isLightTheme = current.Theme == ThemeChoice.Light;
        _isDarkTheme = current.Theme == ThemeChoice.Dark;
        _closeToTray = current.CloseToTray;
        _startWithWindows = current.StartWithWindows;
        _notifications = current.Notifications;
        _showMessageText = current.ShowMessageText;
        _playSounds = current.Sounds;
        _receiveFolder = files.ReceiveFolder;
        _problem = store.LoadProblem is null ? null : "Your saved settings couldn't be read, so these are the defaults. Changing one replaces the old file.";
    }

    public string DataDirectory => _paths.Root;

    public System.Collections.ObjectModel.ObservableCollection<AvatarOption> AvatarOptions { get; } = [];

    [ObservableProperty]
    private AvatarVisual? _ownAvatar;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemovePictureCommand))]
    private bool _hasPicture;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadPictureCommand))]
    private bool _isPreparingPicture;

    [ObservableProperty]
    private string? _pictureProblem;

    [ObservableProperty]
    private bool _isSystemTheme;

    [ObservableProperty]
    private bool _isLightTheme;

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _notifications;

    [ObservableProperty]
    private bool _showMessageText;

    [ObservableProperty]
    private bool _playSounds;

    [ObservableProperty]
    private string _receiveFolder;

    [ObservableProperty]
    private string? _problem;

    partial void OnIsSystemThemeChanged(bool value) => SelectTheme(value, ThemeChoice.System);

    partial void OnIsLightThemeChanged(bool value) => SelectTheme(value, ThemeChoice.Light);

    partial void OnIsDarkThemeChanged(bool value) => SelectTheme(value, ThemeChoice.Dark);

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnNotificationsChanged(bool value) => Save(s => s with { Notifications = value });

    partial void OnShowMessageTextChanged(bool value) => Save(s => s with { ShowMessageText = value });

    partial void OnPlaySoundsChanged(bool value)
    {
        Save(s => s with { Sounds = value });
        if (value)
        {
            // So the user hears what they just turned on.
            _sounds.Play(SoundEffect.Message);
        }
    }

    [RelayCommand]
    private void TestSound() => _sounds.PlayTest(SoundEffect.Message);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (StartupRegistration.Apply(value))
        {
            Save(s => s with { StartWithWindows = value });
        }
        else
        {
            Problem = "Windows didn't allow changing whether Roomie starts by itself.";
        }
    }

    /// <summary>Draws this device's avatar again. The first letter depends on the name, so renaming needs this too.</summary>
    public void RefreshOwnAvatar()
    {
        var self = _identity.Identity;
        OwnAvatar = _avatars.For(self.DeviceId, self.Username);
        var own = _profiles.Own;
        HasPicture = !own.IsSameAs(AvatarChoice.None);

        // Only what changed is replaced, so the button just used keeps the keyboard focus when it can.
        for (var i = 0; i < _avatars.Presets.Count; i++)
        {
            var preset = _avatars.Presets[i];
            var option = new AvatarOption(preset.Id, preset.Name, AvatarCatalog.ForPreset(preset), own.Preset == preset.Id);
            if (i >= AvatarOptions.Count)
            {
                AvatarOptions.Add(option);
            }
            else if (AvatarOptions[i] != option)
            {
                AvatarOptions[i] = option;
            }
        }
    }

    [RelayCommand]
    private void ChooseAvatar(AvatarOption option) => SetPicture(AvatarChoice.FromPreset(option.Id));

    private bool CanUploadPicture() => !IsPreparingPicture;

    [RelayCommand(CanExecute = nameof(CanUploadPicture))]
    private async Task UploadPictureAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a picture",
            CheckFileExists = true,
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsPreparingPicture = true;
        try
        {
            var png = await Task.Run(() => AvatarImage.Normalize(dialog.FileName));
            SetPicture(AvatarChoice.FromPicture(png));
        }
        catch (AvatarImageException ex)
        {
            PictureProblem = ex.Message;
        }
        finally
        {
            IsPreparingPicture = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasPicture))]
    private void RemovePicture() => SetPicture(AvatarChoice.None);

    private void SetPicture(AvatarChoice choice)
    {
        PictureProblem = _profiles.SetOwn(choice) ? null : "Roomie couldn't save your picture.";
        RefreshOwnAvatar();
    }

    [RelayCommand]
    private void ChooseReceiveFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where received files are saved", InitialDirectory = ReceiveFolder };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_files.TrySetReceiveFolder(dialog.FolderName) is { } error)
        {
            Problem = error;
            return;
        }

        ReceiveFolder = _files.ReceiveFolder;
        Save(s => s with { ReceiveFolder = ReceiveFolder });
    }

    [RelayCommand]
    private void OpenReceiveFolder() => OpenFolder(ReceiveFolder);

    [RelayCommand]
    private void OpenLogs() => OpenFolder(_paths.Logs);

    private void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Problem = $"That folder isn't there right now: {folder}";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
    }

    private void SelectTheme(bool isSelected, ThemeChoice choice)
    {
        if (isSelected)
        {
            _theme.Apply(choice);
            Save(s => s with { Theme = choice });
        }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        Problem = _store.Update(change) ? null : "This change works for now, but Roomie couldn't save it for next time.";
    }
}
