using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceGuard;

public sealed class PttKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly Keys key;
    private readonly Action<bool> callback;
    private IntPtr hookId;
    private LowLevelKeyboardProc? proc;
    private bool isDown;

    public PttKeyHook(Keys key, Action<bool> callback)
    {
        this.key = key;
        this.callback = callback;
    }

    public void Start()
    {
        proc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc,
            GetModuleHandle(module.ModuleName), 0);

        if (hookId == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (Keys)Marshal.ReadInt32(lParam);
            if (k == key)
            {
                bool down = wParam == (IntPtr)WM_KEYDOWN ||
                            wParam == (IntPtr)WM_SYSKEYDOWN;
                bool up = wParam == (IntPtr)WM_KEYUP ||
                          wParam == (IntPtr)WM_SYSKEYUP;

                if (down && !isDown)
                {
                    isDown = true;
                    callback(true);
                }
                else if (up && isDown)
                {
                    isDown = false;
                    callback(false);
                }
            }
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
