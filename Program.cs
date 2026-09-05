using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceGuard;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [STAThread]
    static void Main()
    {
        // Give Windows a NEW, stable identity for this revision. This prevents
        // the shell/taskbar from treating the application as the old pinned
        // VoiceGuard executable and reusing its cached icon.
        _ = SetCurrentProcessExplicitAppUserModelID("JackTheGooner.VoiceGuard.6.5.5");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
