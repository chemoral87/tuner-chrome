using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Tuner;

/// <summary>
/// Manages global (system-wide) keyboard hotkeys using Win32 RegisterHotKey/UnregisterHotKey APIs.
/// Works even when the application window is not focused or minimized to tray.
/// </summary>
public class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_ALT = 0x0001;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private bool _disposed;

    public GlobalHotkeyManager()
    {
        var helper = new WindowInteropHelper(App.Current.MainWindow!);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source.AddHook(HwndHook);
    }

    /// <summary>
    /// Registers a global hotkey combination.
    /// </summary>
    /// <param name="id">Unique ID for this hotkey (use different IDs for different hotkeys).</param>
    /// <param name="modifiers">Key modifiers (MOD_CONTROL, MOD_ALT, MOD_SHIFT, MOD_WIN).</param>
    /// <param name="key">Virtual key code (e.g., KeyInterop.VirtualKeyFromKey).</param>
    /// <param name="action">Action to invoke when the hotkey is pressed.</param>
    public bool Register(int id, int modifiers, int key, Action action)
    {
        bool success = RegisterHotKey(IntPtr.Zero, id, modifiers, key);
        if (success)
            _hotkeyActions[id] = action;
        return success;
    }

    /// <summary>
    /// Unregisters a specific hotkey.
    /// </summary>
    public void Unregister(int id)
    {
        UnregisterHotKey(IntPtr.Zero, id);
        _hotkeyActions.Remove(id);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int hotkeyId = wParam.ToInt32();
            if (_hotkeyActions.TryGetValue(hotkeyId, out var action))
            {
                action();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _hotkeyActions.Keys.ToList())
            UnregisterHotKey(IntPtr.Zero, id);

        _source.RemoveHook(HwndHook);
        _hotkeyActions.Clear();
    }
}
