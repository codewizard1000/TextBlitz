using System.Windows;

namespace TextBlitz.Services.Snippets;

/// <summary>
/// A simple modal dialog that prompts the user for a text value.
/// Used by SnippetExpansionService to resolve {prompt:FieldName} tokens.
/// </summary>
public partial class PromptDialog : Window
{
    /// <summary>
    /// The value entered by the user, or null if the dialog was cancelled.
    /// </summary>
    public string? EnteredValue { get; private set; }

    /// <summary>
    /// Creates a new prompt dialog.
    /// </summary>
    /// <param name="fieldName">The name of the field to display in the prompt label.</param>
    public PromptDialog(string fieldName)
    {
        InitializeComponent();
        PromptLabel.Text = $"Enter value for {fieldName}:";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        EnteredValue = ValueTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        EnteredValue = null;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Shows the prompt dialog modally and returns the entered value, or null if cancelled.
    /// Must be called from the STA (dispatcher) thread.
    /// </summary>
    /// <param name="fieldName">The field name to display.</param>
    /// <returns>The entered value, or null if cancelled.</returns>
    public static string? Prompt(string fieldName)
    {
        var dialog = new PromptDialog(fieldName);
        bool? result = dialog.ShowDialog();
        return result == true ? dialog.EnteredValue : null;
    }
}
