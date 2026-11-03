using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Roomie.App.ViewModels;

namespace Roomie.App.Views;

/// <summary>Binds a rail button to the section it opens: checked when it is the current one, and choosing it selects it.</summary>
public sealed class SectionToCheckedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppSection section && section.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && Enum.TryParse<AppSection>(parameter as string, out var section) ? section : Binding.DoNothing;
}

/// <summary>Shows a screen only while its section is the current one.</summary>
public sealed class SectionToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppSection section && section.ToString() == parameter as string ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows a badge only when there is something to count.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
