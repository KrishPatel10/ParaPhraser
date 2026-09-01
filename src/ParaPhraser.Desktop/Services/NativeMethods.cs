using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ParaPhraser.Desktop.Services;

internal static class NativeMethods
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyShift = 0x10;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(
        nint windowHandle,
        int id,
        HotkeyModifiers modifiers,
        VirtualKey virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint numberOfInputs,
        Input[] inputs,
        int sizeOfInputStructure);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(
        nint windowHandle,
        StringBuilder text,
        int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    internal static int GetLastWin32Error() => Marshal.GetLastWin32Error();

    internal static NativePoint GetCursorPosition()
    {
        return GetCursorPos(out var point) ? point : default;
    }

    internal static string? GetWindowTitle(nint windowHandle)
    {
        var length = GetWindowTextLengthW(windowHandle);
        if (length <= 0)
        {
            return null;
        }

        var title = new StringBuilder(length + 1);
        return GetWindowTextW(windowHandle, title, title.Capacity) > 0
            ? title.ToString()
            : null;
    }

    internal static bool AreHotkeyModifiersPressed()
    {
        return IsKeyPressed(VirtualKeyControl)
            || IsKeyPressed(VirtualKeyMenu)
            || IsKeyPressed(VirtualKeyShift);
    }

    internal static void SendControlChord(ushort virtualKey)
    {
        var inputs = new[]
        {
            CreateKeyboardInput(VirtualKeyControl, keyUp: false),
            CreateKeyboardInput(virtualKey, keyUp: false),
            CreateKeyboardInput(virtualKey, keyUp: true),
            CreateKeyboardInput(VirtualKeyControl, keyUp: true)
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != (uint)inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not send the keyboard shortcut.");
        }
    }

    private static bool IsKeyPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static Input CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;

        [FieldOffset(0)]
        internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }
}
