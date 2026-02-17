using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TextBlitz.Services.Hotkeys;

/// <summary>
/// Manages global hotkeys using the Win32 RegisterHotKey API.
/// A hidden HwndSource window receives WM_HOTKEY messages and dispatches registered callbacks.
/// Must be created and disposed on an STA thread (the WPF dispatcher thread).
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    // Win32 constants
    private const int WM_HOTKEY = 0x0312;

    // Modifier flags for RegisterHotKey
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // P/Invoke
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _hwndSource;
    private bool _disposed;
    private int _nextAtomId = 0xC000; // Start in the global atom range

    /// <summary>
    /// Maps a user-defined string id to the registration info.
    /// </summary>
    private readonly Dictionary<string, HotkeyRegistration> _registrations = new();

    /// <summary>
    /// Maps the integer atom id (used with Win32) back to the string id for dispatch.
    /// </summary>
    private readonly Dictionary<int, string> _atomToId = new();

    private record HotkeyRegistration(int AtomId, uint Modifiers, uint VirtualKey, Action Callback);

    /// <summary>
    /// Initializes the hidden message window. Must be called before registering hotkeys.
    /// </summary>
    public void Start()
    {
        if (_hwndSource != null)
            return;

        var parameters = new HwndSourceParameters("TextBlitzHotkeyManager")
        {
            Width = 0,
            Height = 0,
            PositionX = -1,
            PositionY = -1,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    /// <summary>
    /// Registers a global hotkey.
    /// </summary>
    /// <param name="id">A unique string identifier for this hotkey (e.g. "clipboardTray").</param>
    /// <param name="hotkeyString">
    /// Human-readable hotkey string, e.g. "Ctrl+Shift+V", "Alt+F1", "Ctrl+Alt+Delete".
    /// Modifiers: Ctrl, Alt, Shift, Win. Key: any single key name.
    /// </param>
    /// <param name="callback">Action invoked when the hotkey is pressed.</param>
    /// <exception cref="InvalidOperationException">If Start() has not been called.</exception>
    /// <exception cref="ArgumentException">If the hotkey string cannot be parsed.</exception>
    public void RegisterHotkey(string id, string hotkeyString, Action callback)
    {
        if (_hwndSource == null)
            throw new InvalidOperationException("GlobalHotkeyManager has not been started. Call Start() first.");

        if (_registrations.ContainsKey(id))
            UnregisterHotkey(id);

        ParseHotkeyString(hotkeyString, out uint modifiers, out uint vk);

        int atomId = _nextAtomId++;

        // Add MOD_NOREPEAT to prevent repeated WM_HOTKEY while held down
        if (!RegisterHotKey(_hwndSource.Handle, atomId, modifiers | MOD_NOREPEAT, vk))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to register hotkey '{hotkeyString}' (id='{id}'). " +
                $"It may already be registered by another application. Win32 error: {error}");
        }

        var registration = new HotkeyRegistration(atomId, modifiers, vk, callback);
        _registrations[id] = registration;
        _atomToId[atomId] = id;
    }

    /// <summary>
    /// Unregisters a previously registered hotkey by its string id.
    /// </summary>
    public void UnregisterHotkey(string id)
    {
        if (!_registrations.TryGetValue(id, out var reg))
            return;

        if (_hwndSource != null)
        {
            UnregisterHotKey(_hwndSource.Handle, reg.AtomId);
        }

        _atomToId.Remove(reg.AtomId);
        _registrations.Remove(id);
    }

    /// <summary>
    /// Unregisters all currently registered hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        if (_hwndSource == null)
            return;

        foreach (var reg in _registrations.Values)
        {
            UnregisterHotKey(_hwndSource.Handle, reg.AtomId);
        }

        _atomToId.Clear();
        _registrations.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int atomId = wParam.ToInt32();
            if (_atomToId.TryGetValue(atomId, out string? id) &&
                _registrations.TryGetValue(id, out var reg))
            {
                handled = true;
                try
                {
                    reg.Callback();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"GlobalHotkeyManager: callback for '{id}' threw: {ex.Message}");
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses a human-readable hotkey string like "Ctrl+Shift+V" into modifier flags and a virtual key code.
    /// </summary>
    private static void ParseHotkeyString(string hotkeyString, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeyString))
            throw new ArgumentException("Hotkey string cannot be empty.", nameof(hotkeyString));

        string[] parts = hotkeyString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            throw new ArgumentException($"Invalid hotkey string: '{hotkeyString}'", nameof(hotkeyString));

        // The last part is the key; everything before it is a modifier.
        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= ParseModifier(parts[i], hotkeyString);
        }

        virtualKey = ParseKey(parts[^1], hotkeyString);
    }

    private static uint ParseModifier(string modifier, string fullString)
    {
        return modifier.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => MOD_CONTROL,
            "ALT" => MOD_ALT,
            "SHIFT" => MOD_SHIFT,
            "WIN" or "WINDOWS" or "META" or "SUPER" => MOD_WIN,
            _ => throw new ArgumentException(
                $"Unknown modifier '{modifier}' in hotkey string '{fullString}'. " +
                "Supported modifiers: Ctrl, Alt, Shift, Win.")
        };
    }

    /// <summary>
    /// Converts a key name to its Win32 virtual key code.
    /// Supports letter keys (A-Z), digit keys (0-9), function keys (F1-F24),
    /// and common named keys.
    /// </summary>
    private static uint ParseKey(string keyName, string fullString)
    {
        string upper = keyName.ToUpperInvariant();

        // Single letter A-Z
        if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z')
            return (uint)upper[0]; // VK_A through VK_Z match ASCII

        // Single digit 0-9
        if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9')
            return (uint)upper[0]; // VK_0 through VK_9 match ASCII

        // Function keys F1-F24
        if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), out int fNum) && fNum >= 1 && fNum <= 24)
            return (uint)(0x70 + fNum - 1); // VK_F1 = 0x70

        // Named keys
        return upper switch
        {
            "SPACE" or "SPACEBAR" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACKSPACE" or "BACK" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            "SCROLLLOCK" => 0x91,
            "PAUSE" or "BREAK" => 0x13,
            "NUMLOCK" => 0x90,
            "CAPSLOCK" => 0x14,
            "OEM_PLUS" or "PLUS" or "=" => 0xBB,
            "OEM_MINUS" or "MINUS" or "-" => 0xBD,
            "OEM_PERIOD" or "PERIOD" or "." => 0xBE,
            "OEM_COMMA" or "COMMA" or "," => 0xBC,
            "OEM_1" or "SEMICOLON" or ";" => 0xBA,
            "OEM_2" or "SLASH" or "/" => 0xBF,
            "OEM_3" or "TILDE" or "`" => 0xC0,
            "OEM_4" or "[" => 0xDB,
            "OEM_5" or "\\" => 0xDC,
            "OEM_6" or "]" => 0xDD,
            "OEM_7" or "'" => 0xDE,
            _ => throw new ArgumentException(
                $"Unknown key '{keyName}' in hotkey string '{fullString}'. " +
                "Use letter (A-Z), digit (0-9), function key (F1-F24), or named key.")
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnregisterAll();

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
