using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VoiceGuard;

public sealed class VoiceToTextForm : Form
{
    private static readonly Color Bg = Color.FromArgb(10, 7, 14);
    private static readonly Color Surface = Color.FromArgb(18, 15, 25);
    private static readonly Color Surface2 = Color.FromArgb(24, 20, 34);
    private static readonly Color Border = Color.FromArgb(55, 43, 72);
    private static readonly Color Accent = Color.FromArgb(154, 78, 255);
    private static readonly Color TextMain = Color.FromArgb(238, 234, 245);
    private static readonly Color TextDim = Color.FromArgb(157, 149, 170);

    private readonly TextBox activationKeyBox = new();
    private readonly TextBox chatKeyBox = new();
    private readonly TextBox transcriptBox = new();
    private readonly Label statusLabel = new();
    private readonly Button actionButton = new();
    private readonly Button sendButton = new();
    private readonly Button cancelButton = new();
    private readonly Func<bool> startCapture;
    private readonly Func<bool> stopCapture;
    private readonly Action cancelCapture;
    private readonly Action<Keys, Keys> saveKeys;
    private PttKeyHook? activationHook;
    private PttKeyHook? escapeHook;
    private Keys activationKey;
    private Keys chatKey;
    private bool recording;
    private bool transcribing;
    private IntPtr targetWindow;
    private DateTime lastActivationPressUtc = DateTime.MinValue;
    private bool closing;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

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

    public VoiceToTextForm(
        Keys activationKey,
        Keys chatKey,
        Func<bool> startCapture,
        Func<bool> stopCapture,
        Action cancelCapture,
        Action<Keys, Keys> saveKeys)
    {
        this.activationKey = activationKey;
        this.chatKey = chatKey;
        this.startCapture = startCapture;
        this.stopCapture = stopCapture;
        this.cancelCapture = cancelCapture;
        this.saveKeys = saveKeys;

        Text = "VoiceGuard — Voice-to-Text (Experimental)";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        MinimumSize = new Size(560, 490);
        ClientSize = new Size(650, 560);
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "VOICE-TO-TEXT",
            AutoSize = true,
            Location = new Point(22, 18),
            ForeColor = TextMain,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold)
        };
        Controls.Add(title);
        Controls.Add(new Label
        {
            Text = "Speak into your physical mic; local Whisper prepares a message for game chat.",
            AutoSize = true,
            Location = new Point(24, 52),
            ForeColor = TextDim
        });

        Controls.Add(new Label { Text = "Activation key", AutoSize = true, Location = new Point(24, 91), ForeColor = TextDim });
        activationKeyBox.SetBounds(24, 113, 150, 32);
        activationKeyBox.ReadOnly = true;
        activationKeyBox.TextAlign = HorizontalAlignment.Center;
        StyleInput(activationKeyBox);
        activationKeyBox.Text = activationKey.ToString();
        activationKeyBox.KeyDown += (_, e) =>
        {
            activationKey = e.KeyCode == Keys.Escape ? Keys.F8 : e.KeyCode;
            activationKeyBox.Text = activationKey.ToString();
            e.SuppressKeyPress = true;
            saveKeys(this.activationKey, chatKey);
            RebuildHooks();
        };
        Controls.Add(activationKeyBox);

        Controls.Add(new Label { Text = "Chat-open key", AutoSize = true, Location = new Point(194, 91), ForeColor = TextDim });
        chatKeyBox.SetBounds(194, 113, 150, 32);
        chatKeyBox.ReadOnly = true;
        chatKeyBox.TextAlign = HorizontalAlignment.Center;
        StyleInput(chatKeyBox);
        chatKeyBox.Text = chatKey.ToString();
        chatKeyBox.KeyDown += (_, e) =>
        {
            chatKey = e.KeyCode == Keys.Escape ? Keys.Enter : e.KeyCode;
            chatKeyBox.Text = chatKey.ToString();
            e.SuppressKeyPress = true;
            saveKeys(activationKey, this.chatKey);
            RebuildHooks();
        };
        Controls.Add(chatKeyBox);

        var hint = new Label
        {
            Text = "Hotkey: press once to record, again to transcribe, then again to confirm and send. Escape cancels. A quick double-tap while recording cancels.",
            Location = new Point(24, 157),
            Size = new Size(590, 42),
            ForeColor = TextDim
        };
        Controls.Add(hint);

        statusLabel.SetBounds(24, 205, 590, 34);
        statusLabel.Text = "Ready — start VoiceGuard first.";
        statusLabel.ForeColor = Accent;
        statusLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        Controls.Add(statusLabel);

        transcriptBox.SetBounds(24, 246, 590, 190);
        transcriptBox.Multiline = true;
        transcriptBox.ScrollBars = ScrollBars.Vertical;
        transcriptBox.AcceptsReturn = true;
        transcriptBox.WordWrap = true;
        transcriptBox.Font = new Font("Segoe UI", 11F);
        transcriptBox.BackColor = Surface2;
        transcriptBox.ForeColor = TextMain;
        transcriptBox.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(transcriptBox);

        actionButton.Text = "Start Listening";
        actionButton.SetBounds(24, 454, 170, 40);
        StyleButton(actionButton, true);
        actionButton.Click += (_, _) => HandleActivationPressed();
        Controls.Add(actionButton);

        sendButton.Text = "Send to Game Chat";
        sendButton.SetBounds(204, 454, 190, 40);
        StyleButton(sendButton, true);
        sendButton.Enabled = false;
        sendButton.Click += (_, _) => SendPendingText();
        Controls.Add(sendButton);

        cancelButton.Text = "Cancel / Clear";
        cancelButton.SetBounds(404, 454, 150, 40);
        StyleButton(cancelButton, false);
        cancelButton.Click += (_, _) => CancelSession();
        Controls.Add(cancelButton);

        Controls.Add(new Label
        {
            Text = "VoiceGuard must be running. Voice-to-text records privately and never transmits audio.",
            AutoSize = true,
            Location = new Point(24, 516),
            ForeColor = TextDim
        });

        Resize += (_, _) =>
        {
            int width = Math.Max(300, ClientSize.Width - 48);
            hint.Width = width;
            statusLabel.Width = width;
            transcriptBox.Width = width;
            transcriptBox.Height = Math.Max(100, ClientSize.Height - 370);
            sendButton.Left = Math.Max(204, ClientSize.Width - 24 - sendButton.Width - cancelButton.Width - 10);
            cancelButton.Left = ClientSize.Width - 24 - cancelButton.Width;
            actionButton.Top = transcriptBox.Bottom + 18;
            sendButton.Top = actionButton.Top;
            cancelButton.Top = actionButton.Top;
        };

        Shown += (_, _) => RebuildHooks();
        FormClosing += (_, _) =>
        {
            closing = true;
            if (recording || transcribing || transcriptBox.TextLength > 0)
                cancelCapture();
            recording = false;
            transcribing = false;
            activationHook?.Dispose();
            escapeHook?.Dispose();
            activationHook = null;
            escapeHook = null;
        };
    }

    public void OnTranscriptionReady(string text)
    {
        if (IsDisposed || closing) return;
        transcribing = false;
        transcriptBox.Text = text;
        sendButton.Enabled = !string.IsNullOrWhiteSpace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            statusLabel.Text = "No speech recognized. Try again.";
            actionButton.Text = "Start Listening";
        }
        else
        {
            statusLabel.Text = "Review the text. Press the activation key again to confirm/send.";
            actionButton.Text = "Confirm & Send";
        }
    }

    public void OnTranscriptionFailed(string error)
    {
        if (IsDisposed || closing) return;
        transcribing = false;
        recording = false;
        statusLabel.Text = error;
        actionButton.Text = "Start Listening";
        sendButton.Enabled = transcriptBox.TextLength > 0;
    }

    public void OnEngineStopped()
    {
        if (recording || transcribing || transcriptBox.TextLength > 0)
            CancelSession();
        statusLabel.Text = "VoiceGuard stopped — start it before dictating.";
        actionButton.Text = "Start Listening";
    }

    private void RebuildHooks()
    {
        activationHook?.Dispose();
        escapeHook?.Dispose();
        activationHook = new PttKeyHook(activationKey, down =>
        {
            if (!down || activationKeyBox.Focused || chatKeyBox.Focused || closing) return;
            try { if (!IsDisposed) BeginInvoke(() => HandleActivationPressed()); } catch { }
        });
        escapeHook = new PttKeyHook(Keys.Escape, down =>
        {
            if (!down || closing) return;
            try { if (!IsDisposed) BeginInvoke(() => CancelSession()); } catch { }
        });
        try
        {
            activationHook.Start();
            escapeHook.Start();
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Hotkey unavailable: " + ex.Message;
        }
    }

    private void HandleActivationPressed()
    {
        if (closing || IsDisposed) return;
        var now = DateTime.UtcNow;
        if (recording && (now - lastActivationPressUtc).TotalMilliseconds <= 400)
        {
            lastActivationPressUtc = now;
            CancelSession();
            statusLabel.Text = "Recording canceled by double-tap.";
            return;
        }
        lastActivationPressUtc = now;

        if (recording)
        {
            recording = false;
            transcribing = true;
            actionButton.Text = "Transcribing...";
            actionButton.Enabled = false;
            statusLabel.Text = "Transcribing locally with Whisper...";
            bool queued = stopCapture();
            actionButton.Enabled = true;
            if (!queued)
            {
                transcribing = false;
                actionButton.Text = "Start Listening";
                statusLabel.Text = "No usable audio captured. Try again.";
            }
            return;
        }

        if (transcribing) return;

        if (!string.IsNullOrWhiteSpace(transcriptBox.Text))
        {
            SendPendingText();
            return;
        }

        targetWindow = GetForegroundWindow();
        if (!startCapture())
        {
            statusLabel.Text = "Could not start. Start VoiceGuard and ensure it is idle.";
            return;
        }
        recording = true;
        transcriptBox.Clear();
        sendButton.Enabled = false;
        actionButton.Text = "Finish & Transcribe";
        statusLabel.Text = "Listening — speak, then press the activation key again.";
    }

    private void CancelSession()
    {
        if (recording || transcribing)
            cancelCapture();
        recording = false;
        transcribing = false;
        transcriptBox.Clear();
        sendButton.Enabled = false;
        targetWindow = IntPtr.Zero;
        actionButton.Enabled = true;
        actionButton.Text = "Start Listening";
        statusLabel.Text = "Canceled. Nothing was sent.";
    }

    private void SendPendingText()
    {
        string text = transcriptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            statusLabel.Text = "There is no text to send.";
            return;
        }

        IntPtr target = targetWindow != IntPtr.Zero ? targetWindow : GetForegroundWindow();
        if (target == Handle)
        {
            statusLabel.Text = "Return to the game and start a new dictation there.";
            return;
        }

        try
        {
            if (GetForegroundWindow() != target && !SetForegroundWindow(target))
            {
                statusLabel.Text = "Could not focus the game window. Activate the game and try sending again.";
                return;
            }
            Thread.Sleep(150);
            if (GetForegroundWindow() != target)
            {
                statusLabel.Text = "The game did not regain focus. Nothing was sent.";
                return;
            }
            SendKey(chatKey);
            Thread.Sleep(120);
            SendUnicodeText(text);
            Thread.Sleep(80);
            SendKey(Keys.Enter);
            transcriptBox.Clear();
            sendButton.Enabled = false;
            targetWindow = IntPtr.Zero;
            actionButton.Text = "Start Listening";
            statusLabel.Text = "Confirmed and sent to game chat.";
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Could not send text: " + ex.Message;
        }
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
            throw new InvalidOperationException("Windows did not accept the chat key.");
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
                throw new InvalidOperationException("Windows did not accept the chat text.");
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
