using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Controls.Primitives;
using TextBlitz.Models;
using TextBlitz.ViewModels;

namespace TextBlitz.Views;

public partial class ClipboardTrayWindow : Window
{
    public ClipboardTrayWindow()
    {
        InitializeComponent();
        PositionNearSystemTray();
    }

    private void PositionNearSystemTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<ButtonBase>(source) is not null)
        {
            return;
        }

        if (DataContext is not ClipboardTrayViewModel viewModel ||
            sender is not System.Windows.Controls.ListBox listBox ||
            listBox.SelectedItem is not ClipboardItem item)
        {
            return;
        }

        if (viewModel.PasteItemCommand.CanExecute(item))
        {
            viewModel.PasteItemCommand.Execute(item);
        }
    }

    private void SavedLists_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ClipboardTrayViewModel viewModel ||
            sender is not System.Windows.Controls.ListBox listBox ||
            listBox.SelectedItem is not SavedList list)
        {
            return;
        }

        if (viewModel.LoadListCommand.CanExecute(list))
        {
            viewModel.LoadListCommand.Execute(list);
        }
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ShowSettings();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Hide();
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

/// <summary>
/// Converts a non-null/non-empty string to Visibility.Visible.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a boolean to Visibility (true = Visible, false = Collapsed).
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}

/// <summary>
/// Converts a boolean to Visibility (true = Collapsed, false = Visible).
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}
