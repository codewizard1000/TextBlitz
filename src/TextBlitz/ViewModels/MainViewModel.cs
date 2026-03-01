using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextBlitz.Data;
using TextBlitz.Models;
using TextBlitz.Services.Clipboard;
using TextBlitz.Services.Hotkeys;
using TextBlitz.Services.Snippets;
using TextBlitz.Services.Firebase;
using TextBlitz.Services.Billing;

namespace TextBlitz.ViewModels;

/// <summary>
/// Central ViewModel that coordinates the application lifecycle, manages authentication state,
/// and holds references to all child ViewModels.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly ClipboardWatcher _clipboardWatcher;
    private readonly GlobalHotkeyManager _hotkeyManager;
    private readonly SnippetExpansionService _snippetExpansionService;
    private readonly FirebaseAuthService _authService;
    private readonly FirestoreSyncService _syncService;
    private readonly BillingService _billingService;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private string _userEmail = string.Empty;

    [ObservableProperty]
    private int _trialDaysRemaining;

    [ObservableProperty]
    private bool _isPro;

    [ObservableProperty]
    private SubscriptionStatus _subscriptionStatus;

    [ObservableProperty]
    private ClipboardTrayViewModel _clipboardTrayViewModel;

    [ObservableProperty]
    private SnippetManagerViewModel _snippetManagerViewModel;

    [ObservableProperty]
    private SettingsViewModel _settingsViewModel;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isSnippetsOpen;

    public MainViewModel(
        DatabaseService databaseService,
        ClipboardWatcher clipboardWatcher,
        GlobalHotkeyManager hotkeyManager,
        SnippetExpansionService snippetExpansionService,
        FirebaseAuthService authService,
        FirestoreSyncService syncService,
        BillingService billingService)
    {
        _databaseService = databaseService;
        _clipboardWatcher = clipboardWatcher;
        _hotkeyManager = hotkeyManager;
        _snippetExpansionService = snippetExpansionService;
        _authService = authService;
        _syncService = syncService;
        _billingService = billingService;

        _clipboardTrayViewModel = new ClipboardTrayViewModel(databaseService, clipboardWatcher);
        _snippetManagerViewModel = new SnippetManagerViewModel(
            databaseService, snippetExpansionService, syncService, authService);
        _settingsViewModel = new SettingsViewModel(
            databaseService, hotkeyManager, syncService, authService);

        _authService.AuthStateChanged += OnAuthStateChanged;
        _billingService.SubscriptionChanged += OnSubscriptionChanged;
    }

    /// <summary>
    /// Initializes all services and loads persisted state. Call once at application startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _databaseService.InitializeAsync();

        var settings = await _databaseService.GetSettingsAsync();

        // Initialize clipboard monitoring
        _clipboardWatcher.Start();
        PasteEngine.Watcher = _clipboardWatcher;

        // Load child ViewModels
        await ClipboardTrayViewModel.LoadHistoryAsync();
        await ClipboardTrayViewModel.LoadFormattingModeAsync(settings);
        await SnippetManagerViewModel.LoadSnippetsAsync();
        await SettingsViewModel.LoadAsync();

        // Register global hotkeys from settings
        RegisterHotkeys(settings);

        // Start snippet expansion monitoring
        var snippets = await _databaseService.GetAllSnippetsAsync();
        _snippetExpansionService.UpdateSnippets(snippets);
        _snippetExpansionService.Start(settings.DelimiterTriggers);

        // Load auth state
        await RefreshAuthStateAsync();
    }

    private void RegisterHotkeys(AppSettings settings)
    {
        _hotkeyManager.UnregisterAll();

        if (!string.IsNullOrWhiteSpace(settings.ClipboardTrayHotkey))
        {
            _hotkeyManager.Register(settings.ClipboardTrayHotkey, OnClipboardTrayHotkeyPressed);
        }

        if (!string.IsNullOrWhiteSpace(settings.SnippetPickerHotkey))
        {
            _hotkeyManager.Register(settings.SnippetPickerHotkey, OnSnippetPickerHotkeyPressed);
        }

        if (!string.IsNullOrWhiteSpace(settings.PasteLastHotkey))
        {
            _hotkeyManager.Register(settings.PasteLastHotkey, OnPasteLastHotkeyPressed);
        }
    }

    private async Task RefreshAuthStateAsync()
    {
        IsAuthenticated = _authService.IsAuthenticated;

        if (IsAuthenticated && _authService.CurrentUser is { } user)
        {
            UserEmail = user.Email;
            IsPro = _billingService.IsPro;
            SubscriptionStatus = user.SubscriptionStatus;

            if (user.TrialEnd.HasValue)
            {
                var remaining = (user.TrialEnd.Value - DateTime.UtcNow).Days;
                TrialDaysRemaining = Math.Max(0, remaining);
            }
            else
            {
                TrialDaysRemaining = 0;
            }
        }
        else
        {
            UserEmail = string.Empty;
            IsPro = false;
            TrialDaysRemaining = 0;
            SubscriptionStatus = SubscriptionStatus.None;
        }
    }

    private async void OnAuthStateChanged(object? sender, EventArgs e)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await RefreshAuthStateAsync();
        });
    }

    private void OnSubscriptionChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsPro = _billingService.IsPro;
        });
    }

    /// <summary>
    /// Invoked when the clipboard tray global hotkey is pressed.
    /// </summary>
    public event EventHandler? ClipboardTrayHotkeyPressed;

    /// <summary>
    /// Invoked when the snippet picker global hotkey is pressed.
    /// </summary>
    public event EventHandler? SnippetPickerHotkeyPressed;

    private void OnClipboardTrayHotkeyPressed()
    {
        ClipboardTrayHotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSnippetPickerHotkeyPressed()
    {
        SnippetPickerHotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPasteLastHotkeyPressed()
    {
        if (ClipboardTrayViewModel.HistoryItems.Count > 0)
        {
            var lastItem = ClipboardTrayViewModel.HistoryItems[0];
            await PasteEngine.PasteWithFormatting(
                lastItem.PlainText,
                lastItem.RichText,
                lastItem.HtmlText,
                ClipboardTrayViewModel.SelectedFormattingMode);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _authService.SignOutAsync();
        await RefreshAuthStateAsync();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        IsSnippetsOpen = false;
    }

    [RelayCommand]
    private void OpenSnippets()
    {
        IsSnippetsOpen = true;
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void Quit()
    {
        _clipboardWatcher.Stop();
        _hotkeyManager.UnregisterAll();
        _snippetExpansionService.Stop();
        _databaseService.Dispose();
        Application.Current.Shutdown();
    }
}
