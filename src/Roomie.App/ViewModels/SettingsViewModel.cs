using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Roomie.App.Services;
using Roomie.Core.Configuration;
using Roomie.Infrastructure.Storage;
using Roomie.Network.Files;

namespace Roomie.App.ViewModels;

/// <summary>The Settings screen. Every change takes effect and is saved at once.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly JsonSettingsStore _store;
    private readonly FileTransferService _files;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;

    public SettingsViewModel(JsonSettingsStore store, FileTransferService files, ThemeService theme, AppPaths paths)
    {
        _store = store;
        _files = files;
        _theme = theme;
        _paths = paths;

        var current = store.Current;
        _isSystemTheme = current.Theme == ThemeChoice.System;
        _isLightTheme = current.Theme == ThemeChoice.Light;
        _isDarkTheme = current.Theme == ThemeChoice.Dark;
        _closeToTray = current.CloseToTray;
        _startWithWindows = current.StartWithWindows;
        _notifications = current.Notifications;
        _showMessageText = current.ShowMessageText;
        _receiveFolder = files.ReceiveFolder;
        _problem = store.LoadProblem is null ? null : "Your saved settings couldn't be read, so these are the defaults. Changing one replaces the old file.";
    }

    public string DataDirectory => _paths.Root;

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
    private string _receiveFolder;

    [ObservableProperty]
    private string? _problem;

    partial void OnIsSystemThemeChanged(bool value) => SelectTheme(value, ThemeChoice.System);

    partial void OnIsLightThemeChanged(bool value) => SelectTheme(value, ThemeChoice.Light);

    partial void OnIsDarkThemeChanged(bool value) => SelectTheme(value, ThemeChoice.Dark);

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnNotificationsChanged(bool value) => Save(s => s with { Notifications = value });

    partial void OnShowMessageTextChanged(bool value) => Save(s => s with { ShowMessageText = value });

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
