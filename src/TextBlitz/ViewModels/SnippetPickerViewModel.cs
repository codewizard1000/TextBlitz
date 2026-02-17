using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextBlitz.Data;
using TextBlitz.Models;
using TextBlitz.Services.Clipboard;

namespace TextBlitz.ViewModels;

/// <summary>
/// ViewModel for the global snippet picker popup. Provides a searchable palette
/// for quickly finding and inserting snippets via hotkey.
/// </summary>
public partial class SnippetPickerViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;

    public ObservableCollection<Snippet> AllSnippets { get; } = new();
    public ObservableCollection<Snippet> FilteredSnippets { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Snippet? _selectedSnippet;

    /// <summary>
    /// Raised when a snippet is selected for insertion. The View should close
    /// the picker popup and initiate the paste.
    /// </summary>
    public event EventHandler<Snippet>? SnippetSelected;

    /// <summary>
    /// Raised when the picker should be closed without inserting a snippet.
    /// </summary>
    public event EventHandler? CloseRequested;

    public SnippetPickerViewModel(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    /// <summary>
    /// Loads all enabled snippets from the database and populates the filtered list.
    /// Call this each time the picker popup is opened.
    /// </summary>
    public async Task LoadSnippetsAsync()
    {
        var snippets = await _databaseService.GetAllSnippetsAsync();

        AllSnippets.Clear();
        FilteredSnippets.Clear();

        foreach (var snippet in snippets.Where(s => s.IsEnabled))
        {
            AllSnippets.Add(snippet);
            FilteredSnippets.Add(snippet);
        }

        SearchText = string.Empty;
        SelectedSnippet = FilteredSnippets.FirstOrDefault();
    }

    [RelayCommand]
    private async Task InsertSnippetAsync(Snippet? snippet)
    {
        if (snippet is null)
            return;

        SnippetSelected?.Invoke(this, snippet);

        // Paste the snippet content as plain text
        await PasteEngine.PastePlainText(snippet.Content);
    }

    [RelayCommand]
    private void Close()
    {
        SearchText = string.Empty;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter(value);
    }

    private void ApplyFilter(string query)
    {
        FilteredSnippets.Clear();

        var source = string.IsNullOrWhiteSpace(query)
            ? AllSnippets
            : new ObservableCollection<Snippet>(
                AllSnippets.Where(s =>
                    s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    s.TextShortcut.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    s.Content.Contains(query, StringComparison.OrdinalIgnoreCase)));

        foreach (var snippet in source)
        {
            FilteredSnippets.Add(snippet);
        }

        // Auto-select the first match
        SelectedSnippet = FilteredSnippets.FirstOrDefault();
    }
}
