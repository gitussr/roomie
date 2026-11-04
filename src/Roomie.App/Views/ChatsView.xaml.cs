using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Roomie.App.Services;
using Roomie.App.ViewModels;

namespace Roomie.App.Views;

public partial class ChatsView : UserControl
{
    private const long EmojiReopenDelayMs = 250;

    /// <summary>Dragging across the parts of a conversation leaves one and enters the next, which isn't really leaving.</summary>
    private const long DropReappearMs = 150;

    private readonly HashSet<Guid> _justSent = [];
    private readonly Dictionary<string, int> _shownUnread = [];
    private ChatViewModel? _chat;
    private long _emojiClosedAt = -EmojiReopenDelayMs - 1;
    private long _dropHiddenAt = -DropReappearMs - 1;

    public ChatsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        CommandManager.AddPreviewExecutedHandler(Composer, OnComposerCommand);
        EmojiList.ItemsSource = EmojiCatalog.All;
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    /// <summary>Puts the cursor in the message box, if a conversation is open.</summary>
    public void FocusComposer()
    {
        if (_chat is { IsOpen: true })
        {
            Composer.Focus();
        }
    }

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

            // Not while a conversation opens, when its whole history counts as added.
            if (_chat is { HasLoadedMessages: true } && e.NewItems?[0] is ChatBubble { IsMine: true } sent)
            {
                _justSent.Add(sent.MessageId);
            }
        }
    }

    /// <summary>
    /// A bubble is drawn again each time its status changes, so this goes by the message and only the first time.
    /// It's done here because this runs just before the bubble is first drawn, so it never flashes in its final place.
    /// </summary>
    private void OnBubbleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatBubble bubble } element && _justSent.Remove(bubble.MessageId))
        {
            Motion.SlideUp(element);
        }
    }

    /// <summary>A row is rebuilt whenever anything about it changes, so the count it last showed is kept here.</summary>
    private void OnRowBadgeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationItem row } badge)
        {
            return;
        }

        var key = row.GroupId ?? row.PeerId?.ToString() ?? string.Empty;
        if (_shownUnread.TryGetValue(key, out var shown) && row.Unread > shown)
        {
            Motion.Pop(badge);
        }

        _shownUnread[key] = row.Unread;
    }

    private void OnFindDevices(object sender, RoutedEventArgs e)
    {
        if (Main is { } main)
        {
            main.Section = AppSection.Devices;
        }
    }

    private void OnEmojiButton(object sender, RoutedEventArgs e)
    {
        // A click on this button while the list is open has just closed it, and shouldn't open it straight back up.
        if (Environment.TickCount64 - _emojiClosedAt > EmojiReopenDelayMs)
        {
            EmojiPopup.IsOpen = true;
        }
    }

    private void OnEmojiOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => EmojiList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));

    private void OnEmojiClosed(object? sender, EventArgs e) => _emojiClosedAt = Environment.TickCount64;

    private void OnEmojiKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EmojiPopup.IsOpen = false;
            Composer.Focus();
        }
    }

    /// <summary>Goes where the cursor is, replacing any selected text, as typing it would.</summary>
    private void OnEmojiChosen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EmojiOption emoji)
        {
            return;
        }

        EmojiPopup.IsOpen = false;
        var at = Composer.SelectionStart;
        Composer.SelectedText = emoji.Glyph;
        Composer.Select(at + emoji.Glyph.Length, 0);
        Composer.Focus();
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
        if (DropHint.Visibility != Visibility.Visible)
        {
            DropHint.Visibility = Visibility.Visible;
            if (Environment.TickCount64 - _dropHiddenAt > DropReappearMs)
            {
                Motion.Appear(DropHint);
            }
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        _dropHiddenAt = Environment.TickCount64;
    }

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
