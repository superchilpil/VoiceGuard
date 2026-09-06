using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceGuard;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\JackTheGooner.VoiceGuard.SingleInstance";
    private static Mutex? singleInstanceMutex;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [STAThread]
    static void Main()
    {
        bool createdNew;
        try
        {
            singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // If Windows will not allow this process to open the existing
            // mutex, fail closed rather than risk launching a second instance.
            MessageBox.Show(
                "VoiceGuard is already running.",
                "VoiceGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (!createdNew)
        {
            MessageBox.Show(
                "VoiceGuard is already running.",
                "VoiceGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // Give Windows a NEW, stable identity for this revision. This prevents
        // the shell/taskbar from treating the application as the old pinned
        // VoiceGuard executable and reusing its cached icon.
        _ = SetCurrentProcessExplicitAppUserModelID("JackTheGooner.VoiceGuard.6.6.2");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
