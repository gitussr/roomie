using System.Windows;
using Roomie.App.ViewModels;

namespace Roomie.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
