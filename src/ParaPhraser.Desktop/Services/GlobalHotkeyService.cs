using System.ComponentModel;
using System.Windows.Interop;

namespace ParaPhraser.Desktop.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly HwndSource _source;
    private readonly HashSet<int> _registeredIds = [];
    private bool _disposed;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("ParaPhraser.HotkeySink")
        {
            ParentWindow = MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WindowProcedure);
    }

    public event EventHandler<int>? HotkeyPressed;

    public void Register(int id, HotkeyModifiers modifiers, VirtualKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveModifiers = modifiers | HotkeyModifiers.NoRepeat;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, effectiveModifiers, key))
        {
            throw new Win32Exception(
                NativeMethods.GetLastWin32Error(),
                $"Could not register shortcut {modifiers}+{key}. It may already be used by another app.");
        }

        _registeredIds.Add(id);
    }

    private nint WindowProcedure(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmHotkey)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, wParam.ToInt32());
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var id in _registeredIds)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _registeredIds.Clear();
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
        _disposed = true;
    }
}

