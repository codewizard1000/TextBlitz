using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using TextBlitz.Models;

namespace TextBlitz.Services.Clipboard;

/// <summary>
/// Provides methods to paste content into the active application by setting the system clipboard
/// and simulating Ctrl+V via Win32 SendInput. All public methods must be called from the
/// WPF dispatcher (STA) thread.
/// </summary>
public static class PasteEngine
{
    // Virtual key codes
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    // SendInput constants
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Delay (ms) between setting clipboard and sending Ctrl+V to allow the clipboard to settle.
    private const int ClipboardSettleDelayMs = 50;

    /// <summary>
    /// Reference to the active ClipboardWatcher instance so we can set SuppressNext before
    /// modifying the clipboard. Set this once at app startup.
    /// </summary>
    public static ClipboardWatcher? Watcher { get; set; }

    // P/Invoke: SendInput
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // INPUT structures for SendInput
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// Pastes plain text into the active application. Strips all formatting.
    /// </summary>
    public static async Task PastePlainText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        SuppressCapture();
        System.Windows.Clipboard.SetText(text, TextDataFormat.UnicodeText);

        await Task.Delay(ClipboardSettleDelayMs);
        SendCtrlV();
    }

    /// <summary>
    /// Pastes content with formatting control based on the specified mode.
    /// </summary>
    /// <param name="plainText">The plain text content (required).</param>
    /// <param name="rtf">Optional RTF content.</param>
    /// <param name="html">Optional HTML content.</param>
    /// <param name="mode">The formatting mode to apply.</param>
    public static async Task PasteWithFormatting(
        string plainText,
        string? rtf,
        string? html,
        FormattingMode mode)
    {
        if (string.IsNullOrEmpty(plainText))
            return;

        SuppressCapture();

        switch (mode)
        {
            case FormattingMode.KeepOriginal:
                PasteKeepOriginal(plainText, rtf, html);
                break;

            case FormattingMode.UseDestination:
            case FormattingMode.MergeFormatting:
                // MergeFormatting: same as UseDestination for MVP (beta)
                System.Windows.Clipboard.SetText(plainText, TextDataFormat.UnicodeText);
                break;

            default:
                System.Windows.Clipboard.SetText(plainText, TextDataFormat.UnicodeText);
                break;
        }

        await Task.Delay(ClipboardSettleDelayMs);
        SendCtrlV();
    }

    /// <summary>
    /// Sets the clipboard with all available formats to preserve original formatting.
    /// </summary>
    private static void PasteKeepOriginal(string plainText, string? rtf, string? html)
    {
        var dataObject = new DataObject();

        dataObject.SetData(DataFormats.UnicodeText, plainText);
        dataObject.SetData(DataFormats.Text, plainText);

        if (!string.IsNullOrEmpty(rtf))
        {
            dataObject.SetData(DataFormats.Rtf, rtf);
        }

        if (!string.IsNullOrEmpty(html))
        {
            dataObject.SetData(DataFormats.Html, html);
        }

        System.Windows.Clipboard.SetDataObject(dataObject, true);
    }

    /// <summary>
    /// Tells the ClipboardWatcher to ignore the next clipboard change event
    /// (the one we are about to cause).
    /// </summary>
    private static void SuppressCapture()
    {
        if (Watcher != null)
        {
            Watcher.SuppressNext = true;
        }
    }

    /// <summary>
    /// Simulates pressing Ctrl+V using Win32 SendInput.
    /// </summary>
    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        int size = Marshal.SizeOf<INPUT>();

        // Key down: Ctrl
        inputs[0] = CreateKeyInput(VK_CONTROL, keyUp: false);
        // Key down: V
        inputs[1] = CreateKeyInput(VK_V, keyUp: false);
        // Key up: V
        inputs[2] = CreateKeyInput(VK_V, keyUp: true);
        // Key up: Ctrl
        inputs[3] = CreateKeyInput(VK_CONTROL, keyUp: true);

        uint sent = SendInput((uint)inputs.Length, inputs, size);
        if (sent != inputs.Length)
        {
            System.Diagnostics.Debug.WriteLine(
                $"PasteEngine.SendCtrlV: SendInput sent {sent}/{inputs.Length} events. " +
                $"Error: {Marshal.GetLastWin32Error()}");
        }
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool keyUp)
    {
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = keyUp ? KEYEVENTF_KEYUP : 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
