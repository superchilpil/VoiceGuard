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
        try
        {
            DiagnosticLogger.StartSession();
            DiagnosticLogger.Write("STARTUP", "Entering Program.Main.");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    DiagnosticLogger.WriteException("UNHANDLED EXCEPTION", ex);
                else
                    DiagnosticLogger.Write("UNHANDLED EXCEPTION", e.ExceptionObject?.ToString() ?? "Unknown exception");
            };
            Application.ThreadException += (_, e) =>
                DiagnosticLogger.WriteException("UI THREAD EXCEPTION", e.Exception);

            bool createdNew;
            try
            {
                singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            }
            catch (UnauthorizedAccessException ex)
            {
                DiagnosticLogger.WriteException("SINGLE INSTANCE CHECK", ex);
                MessageBox.Show(
                    "VoiceGuard could not check whether another instance is running.\r\n\r\n" + ex.Message,
                    "VoiceGuard startup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (!createdNew)
            {
                DiagnosticLogger.Write("STARTUP", "Another VoiceGuard instance already owns the single-instance mutex.");
                MessageBox.Show(
                    "VoiceGuard is already running. Check the system tray near the Windows clock.",
                    "VoiceGuard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            _ = SetCurrentProcessExplicitAppUserModelID("JackTheGooner.VoiceGuard.6.7");
            ApplicationConfiguration.Initialize();

            DiagnosticLogger.Write("STARTUP", "Creating MainForm.");
            using var mainForm = new MainForm();
            DiagnosticLogger.Write("STARTUP", "MainForm created; entering WinForms message loop.");
            Application.Run(mainForm);
            DiagnosticLogger.Write("STARTUP", "WinForms message loop exited normally.");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.WriteException("FATAL STARTUP ERROR", ex);
            try
            {
                MessageBox.Show(
                    "VoiceGuard could not start. The error has been recorded in the diagnostic log.\r\n\r\n" +
                    ex.GetType().Name + ": " + ex.Message + "\r\n\r\nLog location:\r\n" +
                    DiagnosticLogger.LogPath,
                    "VoiceGuard startup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // If Windows cannot display the error dialog, preserve the log as the fallback.
            }
        }
        finally
        {
            try { singleInstanceMutex?.ReleaseMutex(); } catch { }
            singleInstanceMutex?.Dispose();
        }
    }
}
