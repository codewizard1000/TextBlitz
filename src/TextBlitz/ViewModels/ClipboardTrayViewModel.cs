using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextBlitz.Data;
using TextBlitz.Models;
using TextBlitz.Services.Clipboard;

namespace TextBlitz.ViewModels;

/// <summary>
/// ViewModel for the clipboard tray panel. Manages clipboard history, pinned items,
/// saved lists, search filtering, and paste operations.
/// </summary>
public partial class ClipboardTrayViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly ClipboardWatcher _clipboardWatcher;
    private int _historyLimit = 500;

    public ObservableCollection<ClipboardItem> HistoryItems { get; } = new();
    public ObservableCollection<ClipboardItem> PinnedItems { get; } = new();
    public ObservableCollection<SavedList> SavedLists { get; } = new();
    public ObservableCollection<ClipboardItem> CurrentListItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredItems))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private FormattingMode _selectedFormattingMode = FormattingMode.KeepOriginal;

    [ObservableProperty]
    private bool _isViewingList;

    [ObservableProperty]
    private string _currentListName = string.Empty;

    [ObservableProperty]
    private ClipboardItem? _selectedItem;

    [ObservableProperty]
    private SavedList? _selectedList;

    /// <summary>
    /// Returns the currently active item collection filtered by <see cref="SearchText"/>.
    /// When viewing a saved list, filters <see cref="CurrentListItems"/>;
    /// otherwise filters <see cref="HistoryItems"/>.
    /// </summary>
    public ObservableCollection<ClipboardItem> FilteredItems
    {
        get
        {
            var source = IsViewingList ? CurrentListItems : HistoryItems;

            if (string.IsNullOrWhiteSpace(SearchText))
                return source;

            var filtered = source
                .Where(item => item.PlainText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new ObservableCollection<ClipboardItem>(filtered);
        }
    }

    public ClipboardTrayViewModel(DatabaseService databaseService, ClipboardWatcher clipboardWatcher)
    {
        _databaseService = databaseService;
        _clipboardWatcher = clipboardWatcher;

        _clipboardWatcher.ClipboardChanged += (_, item) => OnClipboardChanged(item);
    }

    /// <summary>
    /// Loads the persisted formatting mode from the given settings.
    /// </summary>
    public Task LoadFormattingModeAsync(AppSettings settings)
    {
        SelectedFormattingMode = settings.FormattingMode;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads clipboard history, pinned items, and saved lists from the database.
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        var settings = await _databaseService.GetSettingsAsync();
        _historyLimit = settings.HistoryLimit;

        var history = await _databaseService.GetHistoryAsync(_historyLimit);
        HistoryItems.Clear();
        foreach (var item in history.Where(i => !i.IsPinned))
        {
            HistoryItems.Add(item);
        }

        var pinned = await _databaseService.GetPinnedItemsAsync();
        PinnedItems.Clear();
        foreach (var item in pinned)
        {
            PinnedItems.Add(item);
        }

        var lists = await _databaseService.GetListsAsync();
        SavedLists.Clear();
        foreach (var list in lists)
        {
            SavedLists.Add(list);
        }

        OnPropertyChanged(nameof(FilteredItems));
    }

    /// <summary>
    /// Handles a new clipboard item captured by the <see cref="ClipboardWatcher"/>.
    /// Adds the item to the history, persists it, and trims history to the configured limit.
    /// </summary>
    public async void OnClipboardChanged(ClipboardItem item)
    {
        try
        {
            await _databaseService.SaveClipboardItemAsync(item);

            // Insert at the top of history (reverse chronological)
            HistoryItems.Insert(0, item);

            // Trim history to the configured limit
            while (HistoryItems.Count > _historyLimit)
            {
                var oldest = HistoryItems[HistoryItems.Count - 1];
                HistoryItems.RemoveAt(HistoryItems.Count - 1);
                await _databaseService.DeleteClipboardItemAsync(oldest.Id);
            }

            OnPropertyChanged(nameof(FilteredItems));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClipboardTrayViewModel.OnClipboardChanged error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PasteItemAsync(ClipboardItem? item)
    {
        if (item is null)
            return;

        await PasteEngine.PasteWithFormatting(
            item.PlainText,
            item.RichText,
            item.HtmlText,
            SelectedFormattingMode);
    }

    [RelayCommand]
    private async Task TogglePinAsync(ClipboardItem? item)
    {
        if (item is null)
            return;

        item.IsPinned = !item.IsPinned;

        if (item.IsPinned)
        {
            item.PinOrder = PinnedItems.Count;
            HistoryItems.Remove(item);
            PinnedItems.Add(item);
        }
        else
        {
            item.PinOrder = 0;
            PinnedItems.Remove(item);
            // Re-insert into history at the correct chronological position
            var insertIndex = 0;
            while (insertIndex < HistoryItems.Count &&
                   HistoryItems[insertIndex].Timestamp > item.Timestamp)
            {
                insertIndex++;
            }
            HistoryItems.Insert(insertIndex, item);
        }

        await _databaseService.UpdatePinAsync(item.Id, item.IsPinned, item.PinOrder);
        OnPropertyChanged(nameof(FilteredItems));
    }

    [RelayCommand]
    private async Task DeleteItemAsync(ClipboardItem? item)
    {
        if (item is null)
            return;

        HistoryItems.Remove(item);
        PinnedItems.Remove(item);
        CurrentListItems.Remove(item);

        await _databaseService.DeleteClipboardItemAsync(item.Id);
        OnPropertyChanged(nameof(FilteredItems));
    }

    [RelayCommand]
    private async Task SaveAsListAsync()
    {
        // Prompt for a list name is handled by the View; this method receives the name
        // after the dialog completes. For now, default to a timestamped name.
        var listName = $"List {DateTime.Now:yyyy-MM-dd HH:mm}";
        await SaveAsListWithNameAsync(listName);
    }

    /// <summary>
    /// Creates a new saved list with the given name and adds all pinned items to it.
    /// </summary>
    public async Task SaveAsListWithNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var listId = await _databaseService.CreateListAsync(name);
        var savedList = new SavedList { Id = listId, Name = name, CreatedAt = DateTime.UtcNow };
        SavedLists.Add(savedList);

        foreach (var item in PinnedItems)
        {
            await _databaseService.AddItemToListAsync(item.Id, listId);
        }
    }

    [RelayCommand]
    private async Task LoadListAsync(SavedList? list)
    {
        if (list is null)
            return;

        SelectedList = list;
        CurrentListName = list.Name;
        IsViewingList = true;

        var items = await _databaseService.GetListItemsAsync(list.Id);
        CurrentListItems.Clear();
        foreach (var item in items)
        {
            CurrentListItems.Add(item);
        }

        OnPropertyChanged(nameof(FilteredItems));
    }

    [RelayCommand]
    private void BackToHistory()
    {
        IsViewingList = false;
        CurrentListName = string.Empty;
        SelectedList = null;
        CurrentListItems.Clear();
        OnPropertyChanged(nameof(FilteredItems));
    }

    [RelayCommand]
    private async Task MoveItemAsync(ClipboardItem? item)
    {
        // This command is used for drag-and-drop reordering of pinned items.
        // The View is responsible for updating the item's position in PinnedItems
        // and passing the item after the move. We persist the new order here.
        if (item is null || !item.IsPinned)
            return;

        for (int i = 0; i < PinnedItems.Count; i++)
        {
            PinnedItems[i].PinOrder = i;
            await _databaseService.UpdatePinAsync(PinnedItems[i].Id, true, i);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredItems));
    }

    partial void OnIsViewingListChanged(bool value)
    {
        OnPropertyChanged(nameof(FilteredItems));
    }

    partial void OnSelectedFormattingModeChanged(FormattingMode value)
    {
        // Persist the formatting mode selection asynchronously
        _ = PersistFormattingModeAsync(value);
    }

    private async Task PersistFormattingModeAsync(FormattingMode mode)
    {
        try
        {
            var settings = await _databaseService.GetSettingsAsync();
            settings.FormattingMode = mode;
            await _databaseService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"ClipboardTrayViewModel.PersistFormattingModeAsync error: {ex.Message}");
        }
    }
}
