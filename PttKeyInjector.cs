using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceGuard;

public static class PttKeyInjector
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static void KeyDown(Keys key) => SendKey(key, false);

    public static void KeyUp(Keys key) => SendKey(key, true);

    private static void SendKey(Keys key, bool keyUp)
    {
        byte vk = (byte)((int)key & 0xFF);
        byte scan = (byte)MapVirtualKey(vk, 0);
        if (scan == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not map PTT key {key} to a keyboard scan code.");

        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtended(key)) flags |= KEYEVENTF_EXTENDEDKEY;

        // keybd_event is intentionally used here for compatibility with games
        // that do not recognize the scan-code-only SendInput path. This is the
        // same legacy Windows keyboard injection API used by the known-good
        // VoiceGuard soundboard build.
        keybd_event(vk, scan, flags, UIntPtr.Zero);
    }

    private static bool IsExtended(Keys key) => key is
        Keys.Right or Keys.Left or Keys.Up or Keys.Down or
        Keys.Insert or Keys.Delete or Keys.Home or Keys.End or
        Keys.PageUp or Keys.PageDown or Keys.NumLock or Keys.Divide;
}
