using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextBlitz.Data;
using TextBlitz.Models;
using TextBlitz.Services.Hotkeys;
using TextBlitz.Services.Firebase;

namespace TextBlitz.ViewModels;

/// <summary>
/// ViewModel for the settings panel. Exposes editable settings properties,
/// hotkey recording, snippet import/export, and persists changes to the database.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly GlobalHotkeyManager _hotkeyManager;
    private readonly FirestoreSyncService _syncService;
    private readonly FirebaseAuthService _authService;

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [ObservableProperty]
    private int _historyLimit = 500;

    [ObservableProperty]
    private bool _startupOnBoot;

    [ObservableProperty]
    private FormattingMode _defaultFormattingMode = FormattingMode.KeepOriginal;

    [ObservableProperty]
    private string _clipboardTrayHotkey = "Ctrl+Shift+V";

    [ObservableProperty]
    private string _snippetPickerHotkey = "Ctrl+Shift+S";

    [ObservableProperty]
    private string _pasteLastHotkey = "Ctrl+Shift+Z";

    [ObservableProperty]
    private string _dateFormat = "yyyy-MM-dd";

    [ObservableProperty]
    private string _timeFormat = "HH:mm:ss";

    [ObservableProperty]
    private string _delimiterTriggers = " \t\n.,;:!?()[]{}";

    [ObservableProperty]
    private bool _isRecordingHotkey;

    [ObservableProperty]
    private string _recordingHotkeyTarget = string.Empty;

    public SettingsViewModel(
        DatabaseService databaseService,
        GlobalHotkeyManager hotkeyManager,
        FirestoreSyncService syncService,
        FirebaseAuthService authService)
    {
        _databaseService = databaseService;
        _hotkeyManager = hotkeyManager;
        _syncService = syncService;
        _authService = authService;
    }

    /// <summary>
    /// Loads settings from the database and populates all properties.
    /// </summary>
    public async Task LoadAsync()
    {
        var settings = await _databaseService.GetSettingsAsync();
        ApplyFromSettings(settings);
    }

    private void ApplyFromSettings(AppSettings settings)
    {
        HistoryLimit = settings.HistoryLimit;
        StartupOnBoot = settings.StartupOnBoot;
        DefaultFormattingMode = settings.FormattingMode;
        ClipboardTrayHotkey = settings.ClipboardTrayHotkey;
        SnippetPickerHotkey = settings.SnippetPickerHotkey;
        PasteLastHotkey = settings.PasteLastHotkey;
        DateFormat = settings.DateFormat;
        TimeFormat = settings.TimeFormat;
        DelimiterTriggers = settings.DelimiterTriggers;
    }

    private AppSettings ToSettings()
    {
        return new AppSettings
        {
            HistoryLimit = HistoryLimit,
            StartupOnBoot = StartupOnBoot,
            FormattingMode = DefaultFormattingMode,
            ClipboardTrayHotkey = ClipboardTrayHotkey,
            SnippetPickerHotkey = SnippetPickerHotkey,
            PasteLastHotkey = PasteLastHotkey,
            DateFormat = DateFormat,
            TimeFormat = TimeFormat,
            DelimiterTriggers = DelimiterTriggers
        };
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var settings = ToSettings();
        await _databaseService.SaveSettingsAsync(settings);

        // Re-register hotkeys with updated key combinations
        _hotkeyManager.UnregisterAll();

        if (!string.IsNullOrWhiteSpace(ClipboardTrayHotkey))
        {
            _hotkeyManager.Register(ClipboardTrayHotkey, () => { });
        }

        if (!string.IsNullOrWhiteSpace(SnippetPickerHotkey))
        {
            _hotkeyManager.Register(SnippetPickerHotkey, () => { });
        }

        if (!string.IsNullOrWhiteSpace(PasteLastHotkey))
        {
            _hotkeyManager.Register(PasteLastHotkey, () => { });
        }

        // Sync settings to the cloud if authenticated
        if (_authService.IsAuthenticated)
        {
            try
            {
                await _syncService.SyncSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"SettingsViewModel.SaveSettingsAsync sync error: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        var defaults = new AppSettings();
        ApplyFromSettings(defaults);
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task ExportSnippetsAsync()
    {
        try
        {
            var snippets = await _databaseService.GetAllSnippetsAsync();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                FileName = $"TextBlitz_Snippets_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                var json = JsonSerializer.Serialize(snippets, ExportJsonOptions);
                await File.WriteAllTextAsync(dialog.FileName, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"SettingsViewModel.ExportSnippetsAsync error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ImportSnippetsAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog() != true)
                return;

            var json = await File.ReadAllTextAsync(dialog.FileName);
            var importedSnippets = JsonSerializer.Deserialize<List<Snippet>>(json, ExportJsonOptions);

            if (importedSnippets is null || importedSnippets.Count == 0)
                return;

            foreach (var snippet in importedSnippets)
            {
                // Assign a new ID to avoid collisions with existing snippets
                snippet.Id = Guid.NewGuid().ToString();
                snippet.UpdatedAt = DateTime.UtcNow;
                snippet.SyncId = null;

                await _databaseService.SaveSnippetAsync(snippet);
            }

            // Sync imported snippets if authenticated
            if (_authService.IsAuthenticated)
            {
                try
                {
                    var allSnippets = await _databaseService.GetAllSnippetsAsync();
                    await _syncService.SyncSnippetsAsync(allSnippets.ToList());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"SettingsViewModel.ImportSnippetsAsync sync error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"SettingsViewModel.ImportSnippetsAsync error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RecordHotkey(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        RecordingHotkeyTarget = target;
        IsRecordingHotkey = true;
    }

    /// <summary>
    /// Called by the View after a hotkey combination has been captured during recording.
    /// Assigns the recorded hotkey to the appropriate setting property.
    /// </summary>
    public void SetRecordedHotkey(string hotkey)
    {
        IsRecordingHotkey = false;

        switch (RecordingHotkeyTarget)
        {
            case nameof(ClipboardTrayHotkey):
                ClipboardTrayHotkey = hotkey;
                break;
            case nameof(SnippetPickerHotkey):
                SnippetPickerHotkey = hotkey;
                break;
            case nameof(PasteLastHotkey):
                PasteLastHotkey = hotkey;
                break;
        }

        RecordingHotkeyTarget = string.Empty;
    }
}
