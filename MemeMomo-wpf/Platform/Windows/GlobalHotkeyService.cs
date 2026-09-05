using System.Runtime.InteropServices;
using System.Windows.Forms;
using MemeMomo.Models;

namespace MemeMomo.Platform.Windows;

/// <summary>Registers the five production hotkey groups on one message-only HWND.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private readonly NativeMessageWindow _window;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;
    private int _disposed;

    public GlobalHotkeyService()
    {
        _window = new NativeMessageWindow();
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    public event Action? RestoreRequested;

    internal int RegisteredCount => _actions.Count;
    internal nint Handle => _window.Handle;

    public void Apply(
        AppSettings settings,
        MainWindow mainWindow,
        Action toggleTopmostTarget,
        Action toggleMemoTaskbarTarget,
        Action quickMemoFromClipboard)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(toggleTopmostTarget);
        ArgumentNullException.ThrowIfNull(toggleMemoTaskbarTarget);
        ArgumentNullException.ThrowIfNull(quickMemoFromClipboard);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        UnregisterAll();
        TryRegister(settings.ToggleTopmostHotkey, toggleTopmostTarget);
        TryRegister(settings.MinimizeHotkey, mainWindow.HideToTrayWithTransition);
        TryRegister(settings.ShowWindowHotkey, mainWindow.ShowWithTransition);
        if (settings.QuickMemoEnabled)
        {
            TryRegister(settings.QuickMemoHotkey, quickMemoFromClipboard);
        }

        if (settings.ShowMemoWindowTaskbarIcon)
        {
            TryRegister(settings.ToggleMemoTaskbarHotkey, toggleMemoTaskbarTarget);
        }

        _window.RestoreRequested -= OnRestoreRequested;
        _window.RestoreRequested += OnRestoreRequested;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UnregisterAll();
        _window.HotkeyPressed -= OnHotkeyPressed;
        _window.RestoreRequested -= OnRestoreRequested;
        _window.Dispose();
        RestoreRequested = null;
    }

    private void OnHotkeyPressed(int id)
    {
        if (_actions.TryGetValue(id, out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] action failed: {exception}");
            }
        }
    }

    private void OnRestoreRequested() => RestoreRequested?.Invoke();

    private void TryRegister(HotkeySetting hotkey, Action action)
    {
        if (hotkey.IsEmpty || !TryMapKey(hotkey.Key, out Keys key))
        {
            return;
        }

        uint modifiers = 0;
        if (hotkey.Alt) modifiers |= ModAlt;
        if (hotkey.Ctrl) modifiers |= ModControl;
        if (hotkey.Shift) modifiers |= ModShift;
        if (hotkey.Win) modifiers |= ModWin;

        int id = _nextId++;
        if (RegisterHotKey(_window.Handle, id, modifiers, (uint)key))
        {
            _actions[id] = action;
        }
    }

    private void UnregisterAll()
    {
        foreach (int id in _actions.Keys.ToArray())
        {
            _ = UnregisterHotKey(_window.Handle, id);
        }

        _actions.Clear();
    }

    internal static bool TryMapKeyForTest(string value, out Keys key) => TryMapKey(value, out key);

    private static bool TryMapKey(string value, out Keys key)
    {
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (value.Length == 1)
        {
            char character = value[0];
            if (char.IsDigit(character))
            {
                key = Keys.D0 + (character - '0');
                return true;
            }

            if (char.IsLetter(character))
            {
                key = Keys.A + (char.ToUpperInvariant(character) - 'A');
                return true;
            }
        }

        key = value switch
        {
            "Ctrl" => Keys.ControlKey,
            "Alt" => Keys.Menu,
            "Shift" => Keys.ShiftKey,
            "Win" => Keys.LWin,
            "Tab" => Keys.Tab,
            "CapsLock" => Keys.CapsLock,
            "Space" => Keys.Space,
            "`" => Keys.Oemtilde,
            "-" => Keys.OemMinus,
            "=" => Keys.Oemplus,
            "[" => Keys.OemOpenBrackets,
            "]" => Keys.OemCloseBrackets,
            "\\" => Keys.OemPipe,
            ";" => Keys.OemSemicolon,
            "'" => Keys.OemQuotes,
            "," => Keys.Oemcomma,
            "." => Keys.OemPeriod,
            "/" => Keys.OemQuestion,
            "NumPad+" => Keys.Add,
            "NumPad-" => Keys.Subtract,
            "NumPad*" => Keys.Multiply,
            "NumPad/" => Keys.Divide,
            "NumPad." => Keys.Decimal,
            _ => Keys.None
        };
        if (key != Keys.None) return true;

        if (value.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && value.Length == 7
            && char.IsDigit(value[^1]))
        {
            key = Keys.NumPad0 + (value[^1] - '0');
            return true;
        }

        return Enum.TryParse(value, true, out key) && key != Keys.None;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
