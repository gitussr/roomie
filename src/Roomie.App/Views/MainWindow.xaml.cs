using System.Collections.Specialized;
using System.Windows;
using Roomie.App.ViewModels;

namespace Roomie.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.AttentionRequested += (_, _) => RequestAttention();
        viewModel.Chat.Messages.CollectionChanged += OnMessagesChanged;
        Activated += (_, _) => viewModel.OnWindowActiveChanged(true);
        Deactivated += (_, _) => viewModel.OnWindowActiveChanged(false);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            MessageScroller.ScrollToEnd();
        }
    }

    /// <summary>
    /// Brings the window up for an incoming pairing request. When another app has focus, Windows flashes
    /// the taskbar button instead of switching to it.
    /// </summary>
    private void RequestAttention()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
