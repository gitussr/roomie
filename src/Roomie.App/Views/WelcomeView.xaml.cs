using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Roomie.App.ViewModels;

namespace Roomie.App.Views;

public partial class WelcomeView : UserControl
{
    private WelcomeViewModel? _welcome;

    public WelcomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_welcome is not null)
            {
                _welcome.PropertyChanged -= OnWelcomeChanged;
            }

            _welcome = (DataContext as MainViewModel)?.Welcome;
            if (_welcome is not null)
            {
                _welcome.PropertyChanged += OnWelcomeChanged;
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                FocusStep();
            }
        };
    }

    /// <summary>
    /// Puts the keyboard on the current step. On pairing it goes to the step itself and not a button,
    /// so a key already on its way can't choose anything.
    /// </summary>
    public void FocusStep()
    {
        // After layout: a step that has only just become visible can't take the focus yet.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            switch (_welcome?.Step)
            {
                case WelcomeStep.Name:
                    NameBox.Focus();
                    NameBox.SelectAll();
                    break;
                case WelcomeStep.Pair:
                    PairStep.Focus();
                    break;
                case WelcomeStep.Done:
                    StartButton.Focus();
                    break;
            }
        });
    }

    private void OnWelcomeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WelcomeViewModel.Step) && IsVisible)
        {
            FocusStep();
        }
    }
}
