using System.Windows;
using System.Windows.Input;

namespace TextBlitz.Views;

public partial class SnippetPickerWindow : Window
{
    public SnippetPickerWindow()
    {
        InitializeComponent();
        Loaded += SnippetPickerWindow_Loaded;
    }

    private void SnippetPickerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                if (SnippetList.SelectedItem != null)
                {
                    DialogResult = true;
                    Close();
                }
                e.Handled = true;
                break;

            case Key.Down:
                if (!SnippetList.IsFocused && SnippetList.Items.Count > 0)
                {
                    SnippetList.Focus();
                    if (SnippetList.SelectedIndex < 0)
                        SnippetList.SelectedIndex = 0;
                }
                break;

            case Key.Up:
                if (SnippetList.SelectedIndex == 0)
                {
                    SearchBox.Focus();
                    e.Handled = true;
                }
                break;
        }
    }

    private void SnippetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SnippetList.SelectedItem != null)
        {
            DialogResult = true;
            Close();
        }
    }
}
