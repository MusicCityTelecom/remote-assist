using System.Runtime.InteropServices;
using System.Text.Json;

namespace RemoteAssist.Agent;

internal static class InputInjector
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nuint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nuint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);

    private static readonly Dictionary<string, ushort> Keys = BuildKeys();
    private static readonly HashSet<string> ExtendedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArrowLeft","ArrowUp","ArrowRight","ArrowDown","Delete","Insert","Home","End","PageUp","PageDown",
        "ControlRight","AltRight","MetaLeft","MetaRight","NumpadEnter","NumpadDivide","PrintScreen"
    };

    public static void Apply(JsonElement input, int screenIndex)
    {
        if (!input.TryGetProperty("kind", out var kindElement)) return;
        var kind = kindElement.GetString();
        switch (kind)
        {
            case "mouse_move": MouseMove(input, screenIndex); break;
            case "mouse_button": MouseButton(input, screenIndex); break;
            case "mouse_wheel": MouseWheel(input); break;
            case "key": Key(input); break;
        }
    }

    private static void MouseMove(JsonElement input, int screenIndex)
    {
        if (!input.TryGetProperty("x", out var xe) || !input.TryGetProperty("y", out var ye)) return;
        var bounds = ScreenCapture.GetBounds(screenIndex);
        var x = bounds.Left + (int)Math.Round(Math.Clamp(xe.GetDouble(), 0, 1) * Math.Max(1, bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(Math.Clamp(ye.GetDouble(), 0, 1) * Math.Max(1, bounds.Height - 1));
        SetCursorPos(x, y);
    }

    private static void MouseButton(JsonElement input, int screenIndex)
    {
        MouseMove(input, screenIndex);
        var button = input.TryGetProperty("button", out var be) ? be.GetInt32() : 0;
        var down = input.TryGetProperty("down", out var de) && de.GetBoolean();
        var flags = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => 0u
        };
        if (flags == 0) return;
        SendInput(1, [new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags } } }], Marshal.SizeOf<INPUT>());
    }

    private static void MouseWheel(JsonElement input)
    {
        var delta = input.TryGetProperty("delta", out var de) ? de.GetInt32() : 0;
        if (delta == 0) return;
        SendInput(1, [new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL } } }], Marshal.SizeOf<INPUT>());
    }

    private static void Key(JsonElement input)
    {
        if (!input.TryGetProperty("code", out var ce)) return;
        var code = ce.GetString() ?? "";
        if (!Keys.TryGetValue(code, out var vk)) return;

        var down = input.TryGetProperty("down", out var de) && de.GetBoolean();
        var flags = down ? 0u : KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(code)) flags |= KEYEVENTF_EXTENDEDKEY;

        var item = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
        };
        SendInput(1, [item], Marshal.SizeOf<INPUT>());
    }

    private static Dictionary<string, ushort> BuildKeys()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = 0x0D, ["NumpadEnter"] = 0x0D, ["Escape"] = 0x1B, ["Backspace"] = 0x08, ["Tab"] = 0x09,
            ["Space"] = 0x20, ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91, ["PrintScreen"] = 0x2C,
            ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26, ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
            ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["ShiftLeft"] = 0x10, ["ShiftRight"] = 0x10, ["ControlLeft"] = 0x11, ["ControlRight"] = 0x11,
            ["AltLeft"] = 0x12, ["AltRight"] = 0x12, ["MetaLeft"] = 0x5B, ["MetaRight"] = 0x5C,
            ["Backquote"] = 0xC0, ["Minus"] = 0xBD, ["Equal"] = 0xBB, ["BracketLeft"] = 0xDB,
            ["BracketRight"] = 0xDD, ["Backslash"] = 0xDC, ["Semicolon"] = 0xBA, ["Quote"] = 0xDE,
            ["Comma"] = 0xBC, ["Period"] = 0xBE, ["Slash"] = 0xBF,
            ["NumpadMultiply"] = 0x6A, ["NumpadAdd"] = 0x6B, ["NumpadSubtract"] = 0x6D,
            ["NumpadDecimal"] = 0x6E, ["NumpadDivide"] = 0x6F
        };

        for (var c = 'A'; c <= 'Z'; c++) d[$"Key{c}"] = c;
        for (var i = 0; i <= 9; i++)
        {
            d[$"Digit{i}"] = (ushort)('0' + i);
            d[$"Numpad{i}"] = (ushort)(0x60 + i);
        }
        for (var i = 1; i <= 24; i++) d[$"F{i}"] = (ushort)(0x6F + i);

        return d;
    }
}
