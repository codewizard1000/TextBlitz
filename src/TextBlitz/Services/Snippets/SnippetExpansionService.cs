using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TextBlitz.Models;
using TextBlitz.Services.Clipboard;

namespace TextBlitz.Services.Snippets;

/// <summary>
/// Monitors keystrokes using a low-level keyboard hook (WH_KEYBOARD_LL) and expands
/// text snippets when a trigger shortcut followed by a delimiter is typed.
///
/// Template tokens supported in snippet content:
///   {date}              - current date formatted per settings
///   {time}              - current time formatted per settings
///   {clipboard}         - current clipboard plain text
///   {prompt:FieldName}  - shows a dialog asking the user for a value
///
/// Must be created and used on the WPF dispatcher (STA) thread because the hook
/// callback is dispatched via the message loop.
/// </summary>
public sealed class SnippetExpansionService : IDisposable
{
    // Win32 constants
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    // Virtual key codes for backspace simulation
    private const ushort VK_BACK = 0x08;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // P/Invoke: low-level keyboard hook
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // P/Invoke: SendInput for backspace and key simulation
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc; // prevent GC of delegate
    private bool _disposed;
    private bool _suppressingInput;

    /// <summary>
    /// The typing buffer holds the most recent keystrokes (as characters).
    /// We keep it bounded so it doesn't grow unbounded.
    /// </summary>
    private readonly StringBuilder _typingBuffer = new(128);
    private const int MaxBufferLength = 128;

    /// <summary>
    /// Active snippets to match against. Updated externally when snippets change.
    /// </summary>
    public IReadOnlyList<Snippet> ActiveSnippets { get; set; } = Array.Empty<Snippet>();

    /// <summary>
    /// Date format used for {date} token. Defaults to "yyyy-MM-dd".
    /// </summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Time format used for {time} token. Defaults to "HH:mm:ss".
    /// </summary>
    public string TimeFormat { get; set; } = "HH:mm:ss";

    /// <summary>
    /// Characters that act as delimiters to trigger snippet matching.
    /// Defaults to space, tab, newline, and common punctuation.
    /// </summary>
    public string DelimiterTriggers { get; set; } = " \t\n.,;:!?()[]{}";

    /// <summary>
    /// The WPF Dispatcher to use for UI operations (prompt dialogs).
    /// </summary>
    private readonly Dispatcher _dispatcher;

    public SnippetExpansionService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// Installs the low-level keyboard hook and begins monitoring keystrokes.
    /// Must be called on the STA thread.
    /// </summary>
    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        _hookProc = HookCallback;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        IntPtr moduleHandle = GetModuleHandle(curModule.ModuleName);

        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _hookProc = null;
            throw new InvalidOperationException(
                $"Failed to install low-level keyboard hook. Win32 error: {error}");
        }
    }

    /// <summary>
    /// Removes the keyboard hook and stops monitoring.
    /// </summary>
    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hookProc = null;
        }

        _typingBuffer.Clear();
    }

    /// <summary>
    /// Backward-compatible overload used by older startup code.
    /// </summary>
    public void Start(string delimiterTriggers)
    {
        DelimiterTriggers = delimiterTriggers;
        Start();
    }

    /// <summary>
    /// Backward-compatible helper used by view models.
    /// </summary>
    public void UpdateSnippets(List<Snippet> snippets)
    {
        ActiveSnippets = snippets ?? Array.Empty<Snippet>();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_suppressingInput)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vkCode = hookStruct.vkCode;

                char? character = VirtualKeyToChar(vkCode);

                if (character.HasValue)
                {
                    char ch = character.Value;

                    // Check if this character is a delimiter
                    if (DelimiterTriggers.Contains(ch))
                    {
                        // Check if buffer ends with any snippet shortcut
                        var match = FindMatchingSnippet();
                        if (match != null)
                        {
                            // Suppress the delimiter keystroke — we handle expansion
                            // Fire-and-forget the expansion on the dispatcher
                            string shortcut = match.TextShortcut;
                            string content = match.Content;

                            _typingBuffer.Clear();

                            _dispatcher.BeginInvoke(async () =>
                            {
                                await ExpandSnippetAsync(shortcut, content, ch);
                            });

                            // Suppress the delimiter key
                            return (IntPtr)1;
                        }

                        // Delimiter typed but no match — clear buffer
                        _typingBuffer.Clear();
                    }
                    else
                    {
                        AppendToBuffer(ch);
                    }
                }
                else if (vkCode == VK_BACK)
                {
                    // Backspace: remove last character from buffer
                    if (_typingBuffer.Length > 0)
                        _typingBuffer.Length--;
                }
                else if (IsModifierKey(vkCode))
                {
                    // Modifier keys don't affect the buffer
                }
                else
                {
                    // Non-character key (arrows, function keys, etc.) — clear buffer
                    _typingBuffer.Clear();
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void AppendToBuffer(char ch)
    {
        if (_typingBuffer.Length >= MaxBufferLength)
        {
            // Remove the oldest half of the buffer to keep it bounded
            _typingBuffer.Remove(0, MaxBufferLength / 2);
        }

        _typingBuffer.Append(ch);
    }

    /// <summary>
    /// Checks if the current typing buffer ends with any active snippet's text shortcut.
    /// Returns the matching snippet or null.
    /// </summary>
    private Snippet? FindMatchingSnippet()
    {
        if (_typingBuffer.Length == 0 || ActiveSnippets.Count == 0)
            return null;

        string buffer = _typingBuffer.ToString();

        foreach (var snippet in ActiveSnippets)
        {
            if (!snippet.IsEnabled || string.IsNullOrEmpty(snippet.TextShortcut))
                continue;

            string shortcut = snippet.TextShortcut;

            if (buffer.Length >= shortcut.Length &&
                buffer.EndsWith(shortcut, StringComparison.Ordinal))
            {
                // Ensure the shortcut is at a word boundary (either start of buffer
                // or preceded by a delimiter/space)
                int startIndex = buffer.Length - shortcut.Length;
                if (startIndex == 0 || DelimiterTriggers.Contains(buffer[startIndex - 1]))
                {
                    return snippet;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Performs snippet expansion: sends backspaces to erase the shortcut, processes tokens,
    /// and pastes the result.
    /// </summary>
    private async Task ExpandSnippetAsync(string shortcut, string content, char delimiter)
    {
        _suppressingInput = true;

        try
        {
            // Send backspaces to erase the shortcut text that was already typed
            SendBackspaces(shortcut.Length);

            // Small delay for backspaces to be processed by the target application
            await Task.Delay(30);

            // Process template tokens
            string expanded = ProcessTokens(content);

            // Paste the expanded text
            await PasteEngine.PastePlainText(expanded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SnippetExpansionService.ExpandSnippetAsync error: {ex.Message}");
        }
        finally
        {
            _suppressingInput = false;
        }
    }

    /// <summary>
    /// Sends the specified number of backspace key presses via SendInput.
    /// </summary>
    private static void SendBackspaces(int count)
    {
        if (count <= 0)
            return;

        var inputs = new INPUT[count * 2];
        int size = Marshal.SizeOf<INPUT>();

        for (int i = 0; i < count; i++)
        {
            // Key down
            inputs[i * 2] = new INPUT
            {
                Type = INPUT_KEYBOARD,
                Union = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT
                    {
                        VirtualKey = VK_BACK,
                        ScanCode = 0,
                        Flags = 0,
                        Time = 0,
                        ExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Key up
            inputs[i * 2 + 1] = new INPUT
            {
                Type = INPUT_KEYBOARD,
                Union = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT
                    {
                        VirtualKey = VK_BACK,
                        ScanCode = 0,
                        Flags = KEYEVENTF_KEYUP,
                        Time = 0,
                        ExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        SendInput((uint)inputs.Length, inputs, size);
    }

    /// <summary>
    /// Processes template tokens in snippet content and returns the expanded string.
    /// Supported tokens: {date}, {time}, {clipboard}, {prompt:FieldName}
    /// </summary>
    private string ProcessTokens(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Use regex to find and replace all tokens in a single pass
        return Regex.Replace(content, @"\{(date|time|clipboard|prompt:([^}]+))\}", match =>
        {
            string token = match.Groups[1].Value;

            if (token.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.Now.ToString(DateFormat);
            }

            if (token.Equals("time", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.Now.ToString(TimeFormat);
            }

            if (token.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                        return System.Windows.Clipboard.GetText() ?? string.Empty;
                }
                catch (ExternalException)
                {
                    Debug.WriteLine("SnippetExpansionService: clipboard locked during token processing.");
                }

                return string.Empty;
            }

            if (token.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
            {
                string fieldName = match.Groups[2].Value;
                // Show prompt dialog on the dispatcher thread (we are already on it)
                string? value = PromptDialog.Prompt(fieldName);
                return value ?? string.Empty;
            }

            // Unknown token — leave as-is
            return match.Value;
        }, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Converts a virtual key code to its character representation.
    /// Returns null for non-character keys.
    /// </summary>
    private static char? VirtualKeyToChar(uint vkCode)
    {
        // Letters A-Z (0x41-0x5A)
        if (vkCode >= 0x41 && vkCode <= 0x5A)
        {
            // Return lowercase — snippet shortcuts are case-sensitive by convention
            // but we match case-sensitively from the buffer. This simplification
            // returns lowercase; Shift state is not tracked for the typing buffer
            // since snippet shortcuts are typically lowercase identifiers.
            return (char)('a' + (vkCode - 0x41));
        }

        // Digits 0-9 (0x30-0x39)
        if (vkCode >= 0x30 && vkCode <= 0x39)
            return (char)vkCode;

        // Numpad 0-9 (0x60-0x69)
        if (vkCode >= 0x60 && vkCode <= 0x69)
            return (char)('0' + (vkCode - 0x60));

        // Common punctuation / symbol keys (unshifted US layout)
        return vkCode switch
        {
            0xBA => ';',  // OEM_1
            0xBB => '=',  // OEM_PLUS
            0xBC => ',',  // OEM_COMMA
            0xBD => '-',  // OEM_MINUS
            0xBE => '.',  // OEM_PERIOD
            0xBF => '/',  // OEM_2
            0xC0 => '`',  // OEM_3
            0xDB => '[',  // OEM_4
            0xDC => '\\', // OEM_5
            0xDD => ']',  // OEM_6
            0xDE => '\'', // OEM_7
            0x20 => ' ',  // SPACE
            0x0D => '\n', // ENTER
            0x09 => '\t', // TAB
            0x6A => '*',  // MULTIPLY
            0x6B => '+',  // ADD
            0x6D => '-',  // SUBTRACT
            0x6E => '.',  // DECIMAL
            0x6F => '/',  // DIVIDE
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the given virtual key code is a modifier key (Shift, Ctrl, Alt, Win).
    /// </summary>
    private static bool IsModifierKey(uint vkCode)
    {
        return vkCode switch
        {
            0x10 or 0x11 or 0x12 => true, // SHIFT, CTRL, ALT
            0xA0 or 0xA1 => true, // LSHIFT, RSHIFT
            0xA2 or 0xA3 => true, // LCTRL, RCTRL
            0xA4 or 0xA5 => true, // LALT, RALT
            0x5B or 0x5C => true, // LWIN, RWIN
            _ => false
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
