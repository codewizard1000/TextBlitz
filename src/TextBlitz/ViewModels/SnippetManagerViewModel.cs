using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextBlitz.Data;
using TextBlitz.Models;
using TextBlitz.Services.Snippets;
using TextBlitz.Services.Firebase;

namespace TextBlitz.ViewModels;

/// <summary>
/// ViewModel for the snippet management panel. Supports creating, editing, deleting,
/// and syncing snippets with conflict detection for shortcuts and hotkeys.
/// </summary>
public partial class SnippetManagerViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly SnippetExpansionService _snippetExpansionService;
    private readonly FirestoreSyncService _syncService;
    private readonly FirebaseAuthService _authService;

    public ObservableCollection<Snippet> Snippets { get; } = new();

    [ObservableProperty]
    private Snippet? _selectedSnippet;

    [ObservableProperty]
    private Snippet? _editingSnippet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredSnippets))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string? _conflictWarning;

    [ObservableProperty]
    private bool _isRecordingHotkey;

    /// <summary>
    /// Returns snippets filtered by <see cref="SearchText"/> across name, content, and text shortcut.
    /// </summary>
    public ObservableCollection<Snippet> FilteredSnippets
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return Snippets;

            var filtered = Snippets
                .Where(s =>
                    s.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    s.Content.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    s.TextShortcut.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new ObservableCollection<Snippet>(filtered);
        }
    }

    public SnippetManagerViewModel(
        DatabaseService databaseService,
        SnippetExpansionService snippetExpansionService,
        FirestoreSyncService syncService,
        FirebaseAuthService authService)
    {
        _databaseService = databaseService;
        _snippetExpansionService = snippetExpansionService;
        _syncService = syncService;
        _authService = authService;
    }

    /// <summary>
    /// Loads all snippets from the database.
    /// </summary>
    public async Task LoadSnippetsAsync()
    {
        var snippets = await _databaseService.GetAllSnippetsAsync();
        Snippets.Clear();
        foreach (var snippet in snippets)
        {
            Snippets.Add(snippet);
        }
        OnPropertyChanged(nameof(FilteredSnippets));
    }

    [RelayCommand]
    private void NewSnippet()
    {
        EditingSnippet = new Snippet
        {
            Id = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ConflictWarning = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditSnippet(Snippet? snippet)
    {
        if (snippet is null)
            return;

        // Create a copy for editing so we can cancel without mutating the original
        EditingSnippet = new Snippet
        {
            Id = snippet.Id,
            Name = snippet.Name,
            Content = snippet.Content,
            TextShortcut = snippet.TextShortcut,
            Hotkey = snippet.Hotkey,
            IsEnabled = snippet.IsEnabled,
            CreatedAt = snippet.CreatedAt,
            UpdatedAt = snippet.UpdatedAt,
            SyncId = snippet.SyncId
        };

        SelectedSnippet = snippet;
        ConflictWarning = null;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveSnippetAsync()
    {
        if (EditingSnippet is null)
            return;

        // Validate required fields
        if (string.IsNullOrWhiteSpace(EditingSnippet.Name))
        {
            ConflictWarning = "Snippet name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingSnippet.Content))
        {
            ConflictWarning = "Snippet content is required.";
            return;
        }

        // Check for conflicts
        var conflict = CheckConflicts(EditingSnippet);
        if (conflict is not null)
        {
            ConflictWarning = conflict;
            return;
        }

        EditingSnippet.UpdatedAt = DateTime.UtcNow;

        await _databaseService.SaveSnippetAsync(EditingSnippet);

        // Update the collection: replace existing or add new
        var existing = Snippets.FirstOrDefault(s => s.Id == EditingSnippet.Id);
        if (existing is not null)
        {
            var index = Snippets.IndexOf(existing);
            Snippets[index] = EditingSnippet;
        }
        else
        {
            Snippets.Add(EditingSnippet);
        }

        // Refresh snippet expansion service with updated snippet list
        _snippetExpansionService.UpdateSnippets(Snippets.ToList());

        IsEditing = false;
        EditingSnippet = null;
        ConflictWarning = null;
        OnPropertyChanged(nameof(FilteredSnippets));

        await SyncSnippetsAsync();
    }

    [RelayCommand]
    private async Task DeleteSnippetAsync(Snippet? snippet)
    {
        if (snippet is null)
            return;

        await _databaseService.DeleteSnippetAsync(snippet.Id);
        Snippets.Remove(snippet);

        if (SelectedSnippet?.Id == snippet.Id)
        {
            SelectedSnippet = null;
        }

        if (IsEditing && EditingSnippet?.Id == snippet.Id)
        {
            IsEditing = false;
            EditingSnippet = null;
        }

        // Refresh snippet expansion service
        _snippetExpansionService.UpdateSnippets(Snippets.ToList());
        OnPropertyChanged(nameof(FilteredSnippets));

        await SyncSnippetsAsync();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditingSnippet = null;
        ConflictWarning = null;
    }

    [RelayCommand]
    private void RecordHotkey()
    {
        // Set flag so the View can intercept the next key combination
        // and assign it to EditingSnippet.Hotkey
        IsRecordingHotkey = true;
    }

    /// <summary>
    /// Called by the View after a hotkey has been captured. Assigns the key combo
    /// to the snippet being edited and re-validates for conflicts.
    /// </summary>
    public void SetRecordedHotkey(string hotkey)
    {
        IsRecordingHotkey = false;

        if (EditingSnippet is not null)
        {
            EditingSnippet.Hotkey = hotkey;
            var conflict = CheckConflicts(EditingSnippet);
            ConflictWarning = conflict;
        }
    }

    /// <summary>
    /// Checks whether the given snippet's text shortcut or hotkey conflicts
    /// with another existing snippet. Returns a warning message if a conflict
    /// is found, or null if no conflicts exist.
    /// </summary>
    public string? CheckConflicts(Snippet snippet)
    {
        // Check text shortcut conflicts
        if (!string.IsNullOrWhiteSpace(snippet.TextShortcut))
        {
            var shortcutConflict = Snippets.FirstOrDefault(s =>
                s.Id != snippet.Id &&
                string.Equals(s.TextShortcut, snippet.TextShortcut, StringComparison.OrdinalIgnoreCase));

            if (shortcutConflict is not null)
            {
                return $"Text shortcut \"{snippet.TextShortcut}\" is already used by snippet \"{shortcutConflict.Name}\".";
            }
        }

        // Check hotkey conflicts
        if (!string.IsNullOrWhiteSpace(snippet.Hotkey))
        {
            var hotkeyConflict = Snippets.FirstOrDefault(s =>
                s.Id != snippet.Id &&
                string.Equals(s.Hotkey, snippet.Hotkey, StringComparison.OrdinalIgnoreCase));

            if (hotkeyConflict is not null)
            {
                return $"Hotkey \"{snippet.Hotkey}\" is already used by snippet \"{hotkeyConflict.Name}\".";
            }
        }

        return null;
    }

    /// <summary>
    /// Syncs all snippets to Firebase if the user is authenticated.
    /// </summary>
    public async Task SyncSnippetsAsync()
    {
        if (!_authService.IsAuthenticated)
            return;

        try
        {
            await _syncService.SyncSnippetsAsync(Snippets.ToList());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"SnippetManagerViewModel.SyncSnippetsAsync error: {ex.Message}");
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredSnippets));
    }
}
