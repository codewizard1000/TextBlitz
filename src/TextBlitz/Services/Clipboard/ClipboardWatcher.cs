using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TextBlitz.Models;

namespace TextBlitz.Services.Clipboard;

/// <summary>
/// Monitors the system clipboard for changes using Win32 AddClipboardFormatListener.
/// Creates a hidden HwndSource window to receive WM_CLIPBOARDUPDATE messages.
/// Thread safety: must be created and disposed on an STA thread (the WPF dispatcher thread).
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    // Win32 constants
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    // P/Invoke declarations
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private HwndSource? _hwndSource;
    private bool _disposed;
    private string? _lastPlainText;

    /// <summary>
    /// When set to true, the next clipboard change event will be suppressed.
    /// Used by PasteEngine to avoid re-capturing content that we placed on the clipboard ourselves.
    /// Reset to false after the suppressed event.
    /// </summary>
    public bool SuppressNext { get; set; }

    /// <summary>
    /// Fired when a clipboard change is detected and the content contains text.
    /// </summary>
    public event EventHandler<ClipboardItem>? ClipboardChanged;

    /// <summary>
    /// Creates the hidden message window and begins listening for clipboard changes.
    /// Must be called from an STA thread.
    /// </summary>
    public void Start()
    {
        if (_hwndSource != null)
            return;

        // Create a hidden window to receive clipboard messages
        var parameters = new HwndSourceParameters("TextBlitzClipboardWatcher")
        {
            Width = 0,
            Height = 0,
            PositionX = -1,
            PositionY = -1,
            WindowStyle = 0, // No visible style
            ExtendedWindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        if (!AddClipboardFormatListener(_hwndSource.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
            throw new InvalidOperationException(
                $"Failed to register clipboard format listener. Win32 error: {error}");
        }
    }

    /// <summary>
    /// Stops listening for clipboard changes and destroys the hidden window.
    /// </summary>
    public void Stop()
    {
        if (_hwndSource == null)
            return;

        RemoveClipboardFormatListener(_hwndSource.Handle);
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            OnClipboardUpdate();
        }

        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        if (SuppressNext)
        {
            SuppressNext = false;
            return;
        }

        try
        {
            // We must access the clipboard on the STA thread — we are already on it
            // since HwndSource dispatches on the thread that created it.
            var dataObject = System.Windows.Clipboard.GetDataObject();
            if (dataObject == null)
                return;

            // Plain text is required — skip non-text clipboard updates (images, files, etc.)
            if (!dataObject.GetDataPresent(DataFormats.UnicodeText) &&
                !dataObject.GetDataPresent(DataFormats.Text))
                return;

            string plainText = dataObject.GetDataPresent(DataFormats.UnicodeText)
                ? (dataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty)
                : (dataObject.GetData(DataFormats.Text) as string ?? string.Empty);

            if (string.IsNullOrEmpty(plainText))
                return;

            // Deduplicate: skip if the plain text is identical to the last captured content
            if (string.Equals(plainText, _lastPlainText, StringComparison.Ordinal))
                return;

            _lastPlainText = plainText;

            // Capture RTF if available
            string? rtf = null;
            if (dataObject.GetDataPresent(DataFormats.Rtf))
            {
                rtf = dataObject.GetData(DataFormats.Rtf) as string;
            }

            // Capture HTML if available
            string? html = null;
            if (dataObject.GetDataPresent(DataFormats.Html))
            {
                html = dataObject.GetData(DataFormats.Html) as string;
            }

            // Try to identify the source application
            string? sourceApp = GetForegroundWindowTitle();

            var item = new ClipboardItem
            {
                PlainText = plainText,
                RichText = rtf,
                HtmlText = html,
                SourceApp = sourceApp,
                Timestamp = DateTime.UtcNow
            };

            ClipboardChanged?.Invoke(this, item);
        }
        catch (ExternalException)
        {
            // The clipboard is locked by another process — silently ignore.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClipboardWatcher error: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves the title of the currently focused window.
    /// </summary>
    private static string? GetForegroundWindowTitle()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            char[] buffer = new char[512];
            int length = GetWindowText(hwnd, buffer, buffer.Length);

            if (length > 0)
            {
                string title = new string(buffer, 0, length);
                return string.IsNullOrWhiteSpace(title) ? null : title;
            }
        }
        catch
        {
            // Non-critical — return null if we can't determine the source app.
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
