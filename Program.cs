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
        _ = SetCurrentProcessExplicitAppUserModelID("JackTheGooner.VoiceGuard.6.5.5");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
