using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlitz.Data;
using TextBlitz.Services.Billing;
using TextBlitz.Services.Clipboard;
using TextBlitz.Services.Firebase;
using TextBlitz.Services.Hotkeys;
using TextBlitz.Services.Snippets;
using TextBlitz.ViewModels;
using TextBlitz.Views;
using Hardcodet.Wpf.TaskbarNotification;

namespace TextBlitz;

public partial class App : Application
{
    private const bool DiagnosticMode = true;
    private TaskbarIcon? _trayIcon;
    private ClipboardWatcher? _clipboardWatcher;
    private GlobalHotkeyManager? _hotkeyManager;
    private SnippetExpansionService? _expansionService;
    private DatabaseService? _databaseService;
    private FirebaseAuthService? _authService;
    private FirestoreSyncService? _syncService;
    private BillingService? _billingService;
    private MainViewModel? _mainViewModel;

    private ClipboardTrayWindow? _trayWindow;
    private SnippetManagerWindow? _snippetWindow;
    private SnippetPickerWindow? _pickerWindow;

    public static App Instance => (App)Current;
    public DatabaseService Database => _databaseService!;
    public FirebaseAuthService Auth => _authService!;
    public FirestoreSyncService Sync => _syncService!;
    public BillingService Billing => _billingService!;
    public ClipboardWatcher ClipboardWatcher => _clipboardWatcher!;
    public GlobalHotkeyManager HotkeyManager => _hotkeyManager!;
    public SnippetExpansionService ExpansionService => _expansionService!;
    public MainViewModel MainVM => _mainViewModel!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            LogDiagnostic("Startup begin");

            // Ensure data directory exists
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TextBlitz");
            Directory.CreateDirectory(dataDir);
            LogDiagnostic($"Data dir ready: {dataDir}");

            // Initialize services
            _databaseService = new DatabaseService();
            await _databaseService.InitializeAsync();
            LogDiagnostic("Database initialized");

            var httpClient = new HttpClient();

            _authService = new FirebaseAuthService(NullLogger<FirebaseAuthService>.Instance);
            _syncService = new FirestoreSyncService(httpClient, _authService, NullLogger<FirestoreSyncService>.Instance);
            _billingService = new BillingService(_authService, _syncService, httpClient, NullLogger<BillingService>.Instance);

            _clipboardWatcher = new ClipboardWatcher();
            _hotkeyManager = new GlobalHotkeyManager();
            _hotkeyManager.Start();
            _expansionService = new SnippetExpansionService();
            LogDiagnostic("Core services initialized");

            // Create main ViewModel
            _mainViewModel = new MainViewModel(
                _databaseService,
                _clipboardWatcher,
                _hotkeyManager,
                _expansionService,
                _authService,
                _syncService,
                _billingService);

            await _mainViewModel.InitializeAsync();
            LogDiagnostic("MainViewModel initialized");

            // Setup system tray
            SetupTrayIcon();

            // Register global hotkeys
            RegisterDefaultHotkeys();

            // Start clipboard watching
            _clipboardWatcher.Start();

            // Start snippet expansion
            _expansionService.Start();
            LogDiagnostic("Watchers started");

            if (DiagnosticMode)
            {
                ShowSnippetManager();
                MessageBox.Show("TextBlitz diagnostic build started.\n\nIf anything fails, log file:\n%LOCALAPPDATA%\\TextBlitz\\diagnostic.log",
                    "TextBlitz Diagnostic", MessageBoxButton.OK, MessageBoxImage.Information);
                LogDiagnostic("Diagnostic UI shown");
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Startup crash: {ex}");
            MessageBox.Show(
                "TextBlitz failed to start.\n\nSee log:\n%LOCALAPPDATA%\\TextBlitz\\diagnostic.log\n\n" + ex.Message,
                "TextBlitz Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static void LogDiagnostic(string message)
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TextBlitz");
            Directory.CreateDirectory(dataDir);
            var logPath = Path.Combine(dataDir, "diagnostic.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // best effort logging
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "TextBlitz"
        };

        // Left click toggles clipboard tray
        _trayIcon.TrayLeftMouseDown += (s, e) => ToggleClipboardTray();

        // Right click context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        var openTrayItem = new System.Windows.Controls.MenuItem { Header = "Open Clipboard Tray" };
        openTrayItem.Click += (s, e) => ShowClipboardTray();
        contextMenu.Items.Add(openTrayItem);

        var openSnippetsItem = new System.Windows.Controls.MenuItem { Header = "Open Snippets" };
        openSnippetsItem.Click += (s, e) => ShowSnippetManager();
        contextMenu.Items.Add(openSnippetsItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => ShowSettings();
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit TextBlitz" };
        quitItem.Click += (s, e) => ShutdownApp();
        contextMenu.Items.Add(quitItem);

        _trayIcon.ContextMenu = contextMenu;
    }

    private void RegisterDefaultHotkeys()
    {
        var settings = _mainViewModel!.SettingsViewModel;

        TryRegisterHotkey("clipboard_tray",
            settings.ClipboardTrayHotkey ?? "Ctrl+Shift+V",
            () => Dispatcher.Invoke(ToggleClipboardTray));

        TryRegisterHotkey("snippet_picker",
            settings.SnippetPickerHotkey ?? "Ctrl+Shift+Space",
            () => Dispatcher.Invoke(ShowSnippetPicker));

        TryRegisterHotkey("paste_last",
            settings.PasteLastHotkey ?? "Ctrl+Shift+L",
            () => Dispatcher.Invoke(PasteLast));
    }

    private void TryRegisterHotkey(string id, string combo, Action callback)
    {
        try
        {
            _hotkeyManager!.RegisterHotkey(id, combo, callback);
            LogDiagnostic($"Hotkey registered: {id} = {combo}");
        }
        catch (Exception ex)
        {
            // Do not crash startup if a global hotkey is already in use by another app.
            LogDiagnostic($"Hotkey registration failed for {id} ({combo}): {ex.Message}");
        }
    }

    public void ReregisterHotkeys()
    {
        _hotkeyManager?.UnregisterAll();
        RegisterDefaultHotkeys();
    }

    private void ToggleClipboardTray()
    {
        if (_trayWindow != null && _trayWindow.IsVisible)
        {
            _trayWindow.Hide();
        }
        else
        {
            ShowClipboardTray();
        }
    }

    private void ShowClipboardTray()
    {
        if (_trayWindow == null)
        {
            _trayWindow = new ClipboardTrayWindow
            {
                DataContext = _mainViewModel!.ClipboardTrayViewModel
            };
            _trayWindow.Closed += (s, e) => _trayWindow = null;
        }

        // Position near system tray (bottom-right)
        var workArea = SystemParameters.WorkArea;
        _trayWindow.Left = workArea.Right - _trayWindow.Width - 10;
        _trayWindow.Top = workArea.Bottom - _trayWindow.Height - 10;
        _trayWindow.Show();
        _trayWindow.Activate();
    }

    public void ShowSnippetManager()
    {
        if (_snippetWindow == null)
        {
            _snippetWindow = new SnippetManagerWindow
            {
                DataContext = _mainViewModel!.SnippetManagerViewModel
            };
            _snippetWindow.Closed += (s, e) => _snippetWindow = null;
        }
        _snippetWindow.Show();
        _snippetWindow.Activate();
    }

    public void ShowSettings()
    {
        var settingsWindow = new SettingsWindow
        {
            DataContext = _mainViewModel!.SettingsViewModel
        };
        settingsWindow.ShowDialog();
    }

    private void ShowSnippetPicker()
    {
        if (_pickerWindow != null && _pickerWindow.IsVisible)
        {
            _pickerWindow.Close();
            _pickerWindow = null;
            return;
        }

        var pickerVM = new SnippetPickerViewModel(_databaseService!);
        _pickerWindow = new SnippetPickerWindow
        {
            DataContext = pickerVM
        };
        pickerVM.SnippetSelected += (s, snippet) =>
        {
            _pickerWindow.Close();
            _pickerWindow = null;
        };
        _pickerWindow.Closed += (s, e) => _pickerWindow = null;

        // Center on screen
        _pickerWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _pickerWindow.Show();
        _pickerWindow.Activate();
    }

    private async void PasteLast()
    {
        var lastItem = (await _databaseService!.GetHistoryAsync(1)).FirstOrDefault();
        if (lastItem != null)
        {
            var settings = _mainViewModel!.SettingsViewModel;
            await PasteEngine.PasteWithFormatting(
                lastItem.PlainText,
                lastItem.RichText,
                lastItem.HtmlText,
                settings.DefaultFormattingMode);
        }
    }

    private void ShutdownApp()
    {
        _clipboardWatcher?.Dispose();
        _hotkeyManager?.Dispose();
        _expansionService?.Dispose();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownApp();
        base.OnExit(e);
    }
}
