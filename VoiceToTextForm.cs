using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VoiceGuard;

public sealed class VoiceToTextForm : Form
{
    private static readonly Color Bg = Color.FromArgb(10, 7, 14);
    private static readonly Color Surface2 = Color.FromArgb(24, 20, 34);
    private static readonly Color Border = Color.FromArgb(55, 43, 72);
    private static readonly Color Accent = Color.FromArgb(154, 78, 255);
    private static readonly Color TextMain = Color.FromArgb(238, 234, 245);
    private static readonly Color TextDim = Color.FromArgb(157, 149, 170);

    private readonly TextBox transcriptBox = new();
    private readonly Label statusLabel = new();
    private readonly Button actionButton = new();
    private readonly Button cancelButton = new();
    private readonly Func<bool> startCapture;
    private readonly Func<bool> stopCapture;
    private readonly Action cancelCapture;
    private readonly Func<IntPtr> getAppWindowHandle;
    private readonly System.Windows.Forms.Timer stopOrCancelTimer = new() { Interval = 375 };
    private PttKeyHook? activationHook;
    private PttKeyHook? escapeHook;
    private Keys activationKey;
    private Keys chatKey;
    private bool recording;
    private bool transcribingFinal;
    private bool closing;
    private IntPtr targetWindow;
    private string typedText = "";
    private bool waitingForSecondTap;
    private bool targetChatOpened;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public VoiceToTextForm(Keys activationKey, Keys chatKey, Func<bool> startCapture, Func<bool> stopCapture,
        Action cancelCapture, Func<IntPtr> getAppWindowHandle)
    {
        this.activationKey = activationKey;
        this.chatKey = chatKey;
        this.startCapture = startCapture;
        this.stopCapture = stopCapture;
        this.cancelCapture = cancelCapture;
        this.getAppWindowHandle = getAppWindowHandle;

        Text = "VoiceGuard — Voice-to-Text (Experimental)";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        MinimumSize = new Size(540, 360);
        ClientSize = new Size(650, 400);
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9F);

        Controls.Add(new Label
        {
            Text = "LIVE GAME-CHAT DICTATION", AutoSize = true, Location = new Point(22, 18),
            ForeColor = TextMain, Font = new Font("Segoe UI", 15F, FontStyle.Bold)
        });
        Controls.Add(new Label
        {
            Text = "Press the hotkey to open chat and dictate. Press once to send, or double-tap to cancel.",
            AutoSize = true, Location = new Point(24, 51), ForeColor = TextDim
        });

        statusLabel.SetBounds(24, 151, 590, 42);
        statusLabel.Text = "Ready — start VoiceGuard and load Whisper first.";
        statusLabel.ForeColor = Accent;
        statusLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        Controls.Add(statusLabel);

        transcriptBox.SetBounds(24, 201, 590, 105);
        transcriptBox.Multiline = true;
        transcriptBox.ReadOnly = true;
        transcriptBox.ScrollBars = ScrollBars.Vertical;
        transcriptBox.WordWrap = true;
        transcriptBox.Font = new Font("Segoe UI", 10.5F);
        transcriptBox.BackColor = Surface2;
        transcriptBox.ForeColor = TextMain;
        transcriptBox.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(transcriptBox);

        actionButton.Text = "Start Dictation";
        actionButton.SetBounds(24, 322, 190, 40);
        StyleButton(actionButton, true);
        actionButton.Click += (_, _) => HandleActivationPressed();
        Controls.Add(actionButton);

        cancelButton.Text = "Cancel / Clear";
        cancelButton.SetBounds(224, 322, 150, 40);
        StyleButton(cancelButton, false);
        cancelButton.Click += (_, _) => CancelSession(true);
        Controls.Add(cancelButton);

        Resize += (_, _) =>
        {
            int width = Math.Max(300, ClientSize.Width - 48);
            statusLabel.Width = width;
            transcriptBox.Width = width;
            transcriptBox.Height = Math.Max(70, ClientSize.Height - 295);
            actionButton.Top = transcriptBox.Bottom + 16;
            cancelButton.Top = actionButton.Top;
            cancelButton.Left = actionButton.Right + 10;
        };

        stopOrCancelTimer.Tick += (_, _) =>
        {
            stopOrCancelTimer.Stop();
            waitingForSecondTap = false;
            FinishAndSend();
        };
        FormClosing += (_, _) =>
        {
            closing = true;
            stopOrCancelTimer.Stop();
            if (recording || transcribingFinal || !string.IsNullOrEmpty(typedText))
                CancelSession(false);
            activationHook?.Dispose();
            escapeHook?.Dispose();
            activationHook = null;
            escapeHook = null;
        };
    }

    public void StartHotkeys()
    {
        if (IsDisposed) return;
        _ = Handle; // Create a native handle so callbacks can be marshaled while this form stays hidden.
        RebuildHooks();
    }

    public void UpdateHotkeys(Keys newActivationKey, Keys newChatKey)
    {
        activationKey = newActivationKey;
        chatKey = newChatKey;
        if (IsHandleCreated && !IsDisposed)
            RebuildHooks();
    }

    public void StopHotkeys()
    {
        if (closing) return;
        closing = true;
        stopOrCancelTimer.Stop();
        if (recording || transcribingFinal || !string.IsNullOrEmpty(typedText))
            CancelSession(false);
        activationHook?.Dispose();
        escapeHook?.Dispose();
        activationHook = null;
        escapeHook = null;
    }

    private void RebuildHooks()
    {
        activationHook?.Dispose();
        escapeHook?.Dispose();
        activationHook = new PttKeyHook(activationKey, down =>
        {
            if (!down || closing) return;
            try { if (!IsDisposed) BeginInvoke(() => HandleActivationPressed()); } catch { }
        });
        escapeHook = new PttKeyHook(Keys.Escape, down =>
        {
            if (!down || closing) return;
            try { if (!IsDisposed) BeginInvoke(() => CancelSession(false, false)); } catch { }
        });
        try
        {
            activationHook.Start();
            escapeHook.Start();
        }
        catch (Exception ex)
        {
            activationHook?.Dispose();
            escapeHook?.Dispose();
            activationHook = null;
            escapeHook = null;
            statusLabel.Text = "Hotkey unavailable: " + ex.Message;
        }
    }

    private void HandleActivationPressed()
    {
        if (closing || IsDisposed) return;

        if (recording)
        {
            // Defer the stop/send action briefly so a double-tap can cancel instead.
            if (waitingForSecondTap)
            {
                stopOrCancelTimer.Stop();
                waitingForSecondTap = false;
                CancelSession(true);
                statusLabel.Text = "Canceled — typed text cleared and chat closed.";
                return;
            }

            waitingForSecondTap = true;
            statusLabel.Text = "Finishing dictation… tap again quickly to cancel.";
            stopOrCancelTimer.Start();
            return;
        }

        if (transcribingFinal) return;

        targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero || targetWindow == Handle || targetWindow == getAppWindowHandle())
        {
            statusLabel.Text = "Activate your game first, then press the dictation hotkey.";
            targetWindow = IntPtr.Zero;
            return;
        }

        // Verify that VoiceGuard is actually ready before touching game chat.
        // Previously Enter was injected first, so a stopped engine could open
        // chat and immediately cancel without ever starting dictation.
        if (!startCapture())
        {
            targetWindow = IntPtr.Zero;
            statusLabel.Text = "Could not start dictation. Start VoiceGuard and ensure Whisper is ready.";
            return;
        }

        try
        {
            EnsureTargetForeground();
            SendKey(chatKey);
            targetChatOpened = true;
        }
        catch (Exception ex)
        {
            cancelCapture();
            targetWindow = IntPtr.Zero;
            targetChatOpened = false;
            statusLabel.Text = "Could not open game chat: " + ex.Message;
            return;
        }

        typedText = "";
        transcriptBox.Clear();
        recording = true;
        statusLabel.Text = "Listening and typing live into game chat… press once to send or double-tap to cancel.";
        actionButton.Text = "Finish & Send";
    }

    private void FinishAndSend()
    {
        if (!recording) return;
        recording = false;
        transcribingFinal = true;
        statusLabel.Text = "Finalizing speech recognition…";
        actionButton.Enabled = false;
        bool queued = stopCapture();
        if (!queued)
        {
            transcribingFinal = false;
            actionButton.Enabled = true;
            CancelSession(true);
            statusLabel.Text = "No usable speech captured; chat was canceled.";
        }
    }

    public void OnLiveTranscription(string text)
    {
        if (IsDisposed || closing || !recording || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            EnsureTargetForeground();
            ReplaceTypedText(text.Trim());
            statusLabel.Text = "Live dictation — press once to send or double-tap to cancel.";
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Could not update game chat: " + ex.Message;
        }
    }

    public void OnTranscriptionReady(string text)
    {
        if (IsDisposed || closing) return;
        if (!transcribingFinal) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                EnsureTargetForeground();
                ReplaceTypedText(text.Trim());
            }

            if (string.IsNullOrWhiteSpace(typedText))
            {
                statusLabel.Text = "No speech recognized; chat was canceled.";
                CancelSession(true);
                return;
            }

            EnsureTargetForeground();
            SendKey(Keys.Enter);
            targetChatOpened = false;
            targetWindow = IntPtr.Zero;
            typedText = "";
            transcriptBox.Text = text;
            transcribingFinal = false;
            actionButton.Enabled = true;
            actionButton.Text = "Start Dictation";
            statusLabel.Text = "Message sent.";
        }
        catch (Exception ex)
        {
            transcribingFinal = false;
            actionButton.Enabled = true;
            statusLabel.Text = "Could not send message: " + ex.Message + " — text may still be in chat.";
        }
    }

    public void OnTranscriptionFailed(string error)
    {
        if (IsDisposed || closing) return;
        transcribingFinal = false;
        recording = false;
        actionButton.Enabled = true;
        actionButton.Text = "Start Dictation";
        statusLabel.Text = error + " Chat canceled for safety.";
        CancelSession(true);
    }

    public void OnEngineStopped()
    {
        if (recording || transcribingFinal || !string.IsNullOrEmpty(typedText))
            CancelSession(true);
        statusLabel.Text = "VoiceGuard stopped — start it before dictating.";
        actionButton.Text = "Start Dictation";
    }

    private void ReplaceTypedText(string newText)
    {
        int common = 0;
        while (common < typedText.Length && common < newText.Length && typedText[common] == newText[common])
            common++;

        int removeCount = typedText.Length - common;
        if (removeCount > 0)
        {
            for (int i = 0; i < removeCount; i++) SendKey(Keys.Back);
        }

        string suffix = newText.Substring(common);
        if (suffix.Length > 0) SendUnicodeText(suffix);
        typedText = newText;
        transcriptBox.Text = typedText;
        transcriptBox.SelectionStart = transcriptBox.TextLength;
        transcriptBox.ScrollToCaret();
    }

    private void CancelSession(bool closeGameChat, bool clearTypedText = true)
    {
        stopOrCancelTimer.Stop();
        waitingForSecondTap = false;
        if (recording || transcribingFinal) cancelCapture();
        recording = false;
        transcribingFinal = false;
        actionButton.Enabled = true;
        actionButton.Text = "Start Dictation";

        try
        {
            if (targetWindow != IntPtr.Zero && (clearTypedText || closeGameChat))
            {
                EnsureTargetForeground();
                if (clearTypedText && typedText.Length > 0)
                    for (int i = 0; i < typedText.Length; i++) SendKey(Keys.Back);
                if (closeGameChat && targetChatOpened) SendKey(Keys.Escape);
            }
        }
        catch { /* Never send text on cancel; best-effort clear and close. */ }

        typedText = "";
        transcriptBox.Clear();
        targetWindow = IntPtr.Zero;
        targetChatOpened = false;
        if (!closing) statusLabel.Text = clearTypedText
            ? "Canceled. Nothing was sent."
            : "Dictation stopped. Escape is closing game chat; no extra keys were sent.";
    }

    private void EnsureTargetForeground()
    {
        if (targetWindow == IntPtr.Zero) throw new InvalidOperationException("Game window is unavailable.");
        if (GetForegroundWindow() != targetWindow && !SetForegroundWindow(targetWindow))
            throw new InvalidOperationException("Could not focus the game window.");
        Thread.Sleep(50);
        if (GetForegroundWindow() != targetWindow)
            throw new InvalidOperationException("The game did not regain focus.");
    }

    private static void SendKey(Keys key)
    {
        ushort vk = (ushort)key;
        INPUT[] inputs =
        [
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } }
        ];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Windows did not accept the key input.");
    }

    private static void SendUnicodeText(string text)
    {
        foreach (char ch in text)
        {
            INPUT[] inputs =
            [
                new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } },
                new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } }
            ];
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
                throw new InvalidOperationException("Windows did not accept the dictated text.");
        }
    }

    private void StyleInput(TextBox box)
    {
        box.BackColor = Surface2;
        box.ForeColor = TextMain;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Segoe UI", 10F);
    }

    private void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = primary ? Accent : Surface2;
        button.ForeColor = TextMain;
        button.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }
}
