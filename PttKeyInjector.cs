using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceGuard;

public static class PttKeyInjector
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void KeyDown(Keys key)
    {
        byte vk = (byte)key;
        byte scan = (byte)MapVirtualKey(vk, 0);
        uint flags = IsExtended(key) ? KEYEVENTF_EXTENDEDKEY : 0;
        keybd_event(vk, scan, flags, UIntPtr.Zero);
    }

    public static void KeyUp(Keys key)
    {
        byte vk = (byte)key;
        byte scan = (byte)MapVirtualKey(vk, 0);
        uint flags = KEYEVENTF_KEYUP | (IsExtended(key) ? KEYEVENTF_EXTENDEDKEY : 0);
        keybd_event(vk, scan, flags, UIntPtr.Zero);
    }

    private static bool IsExtended(Keys key) => key is
        Keys.Right or Keys.Left or Keys.Up or Keys.Down or
        Keys.Insert or Keys.Delete or Keys.Home or Keys.End or
        Keys.PageUp or Keys.PageDown or Keys.NumLock or Keys.Divide;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
