using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Roomie.App.ViewModels;

namespace Roomie.App.Views;

public partial class ChatsView : UserControl
{
    private ChatViewModel? _chat;

    public ChatsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        CommandManager.AddPreviewExecutedHandler(Composer, OnComposerCommand);
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_chat is not null)
        {
            _chat.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _chat = Main?.Chat;
        if (_chat is not null)
        {
            _chat.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            MessageScroller.ScrollToEnd();
        }
    }

    private void OnFindDevices(object sender, RoutedEventArgs e)
    {
        if (Main is { } main)
        {
            main.Section = AppSection.Devices;
        }
    }

    /// <summary>Files on the clipboard are sent, as when dragged here. Pasting text works as usual.</summary>
    private async void OnComposerCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !Clipboard.ContainsFileDropList() || _chat is null)
        {
            return;
        }

        e.Handled = true;
        var files = Clipboard.GetFileDropList();
        var paths = new List<string>(files.Count);
        foreach (var path in files)
        {
            if (path is not null)
            {
                paths.Add(path);
            }
        }

        await _chat.SendPathsAsync(paths);
    }

    private static string[]? DraggedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDrag(e);

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDrag(e);

    private void UpdateDrag(DragEventArgs e)
    {
        e.Handled = true;
        if (DraggedFiles(e) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropHintText.Text = _chat is { IsGroup: true }
            ? "Files go to one device at a time, not to a group."
            : $"Drop to send to {_chat?.PeerName}";
        DropHint.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (DraggedFiles(e) is { } paths && _chat is not null)
        {
            await _chat.SendPathsAsync(paths);
        }
    }
}
