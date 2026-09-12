using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceGuard;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(10, 7, 14);
    private static readonly Color Surface = Color.FromArgb(18, 15, 25);
    private static readonly Color Surface2 = Color.FromArgb(24, 20, 34);
    private static readonly Color Border = Color.FromArgb(55, 43, 72);
    private static readonly Color Accent = Color.FromArgb(154, 78, 255);
    private static readonly Color AccentBright = Color.FromArgb(190, 108, 255);
    private static readonly Color TextMain = Color.FromArgb(238, 234, 245);
    private static readonly Color TextDim = Color.FromArgb(157, 149, 170);
    private static readonly Color Success = Color.FromArgb(117, 235, 171);

    private readonly ComboBox input = new();
    private readonly ComboBox output = new();
    private readonly NumericUpDown delay = new();
    private readonly TextBox ptt = new();
    private readonly ListBox words = new();
    private readonly ContextMenuStrip wordsMenu = new();
    private readonly Button addWord = new();
    private readonly Button removeWord = new();
    private readonly Dictionary<string, List<string>> blockedWordAliases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> replacementSounds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReplacementPlaybackSettings> replacementPlaybackSettings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> replacementVolumes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> soundboardSounds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> soundboardVolumes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> soundboardAliases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox soundboard = new();
    private readonly Button addSoundboard = new();
    private readonly Button removeSoundboard = new();
    private readonly Button listenSoundboard = new();
    private bool soundboardListenInProgress;
    private readonly TextBox soundboardListenKey = new();
    private Thread? soundboardListenKeyThread;
    private CancellationTokenSource? soundboardListenKeyCts;
    private int soundboardListenVirtualKey = (int)Keys.OemPeriod;
    private int soundboardListenKeyDown;
    private readonly Button editSoundboard = new();
    private readonly TrackBar outputVolume = new();
    private readonly Label outputVolumeValue = new();
    private bool draggingReplacementVolume;
    private int draggingReplacementIndex = -1;
    private readonly TextBox log = new();
    private readonly Label mode = new();
    private readonly Label status = new();
    private readonly Button start = new();
    private readonly CheckBox startWithWindows = new();
    private readonly CheckBox minimizeToTray = new();
    private readonly TextBox startStopHotkey = new();
    private readonly NotifyIcon trayIcon = new();
    private readonly ContextMenuStrip trayMenu = new();

    private AudioEngine? engine;
    private PttKeyHook? hook;
    private SpeechDetector? detector;
    private bool loadingPersistence;
    private bool exitingApplication;
    private bool launchedMinimized;
    private string? loadedInputDeviceName;
    private string? loadedOutputDeviceName;

    private const int WM_HOTKEY = 0x0312;
    private const int StartStopHotkeyId = 0x5647;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Keep both icon sizes alive for the lifetime of the window. Windows uses
    // the small icon for the taskbar and the large icon for the title bar / shell.
    private Icon? taskbarBigIcon;
    private Icon? taskbarSmallIcon;

    private const int WM_SETICON = 0x0080;
    private static readonly IntPtr ICON_SMALL = new(0);
    private static readonly IntPtr ICON_BIG = new(1);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceGuard");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    private sealed class VoiceGuardConfig
    {
        public List<string> BlockedWords { get; set; } = new();
        public Dictionary<string, List<string>> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> ReplacementSounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReplacementPlaybackSettings> ReplacementPlayback { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ReplacementVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SoundboardEntryConfig> SoundboardPhrases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> SoundboardAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string SoundboardListenKey { get; set; } = Keys.OemPeriod.ToString();
        public decimal OutputVolumePercent { get; set; } = 100M;
        public decimal DelaySeconds { get; set; } = 3M;
        public string PttKey { get; set; } = Keys.Z.ToString();
        public string? InputDevice { get; set; }
        public string? OutputDevice { get; set; }
        public bool StartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; }
        public string StartStopHotkey { get; set; } = "Control, Alt, V";
    }

    private sealed class SoundboardEntryConfig
    {
        public string SoundPath { get; set; } = "";
        public double Volume { get; set; } = 1.0;
    }

    private void ApplyWindowAndTaskbarIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "VoiceGuard.ico");
        if (!File.Exists(iconPath))
        {
            ShowIcon = true;
            return;
        }

        // Select actual entries from the multi-resolution ICO rather than
        // letting WinForms/Windows guess which frame to use.
        taskbarBigIcon = new Icon(iconPath, new Size(256, 256));
        taskbarSmallIcon = new Icon(iconPath, new Size(32, 32));

        Icon = taskbarBigIcon;
        ShowIcon = true;

        // Explicitly set both shell icon slots. This prevents the taskbar from
        // falling back to a generic/default icon while the window is created.
        _ = Handle; // force the native window handle to exist
        SendMessage(Handle, WM_SETICON, ICON_BIG, taskbarBigIcon.Handle);
        SendMessage(Handle, WM_SETICON, ICON_SMALL, taskbarSmallIcon.Handle);
    }

    public MainForm()
    {
        ApplyWindowAndTaskbarIcon();

        Text = "VoiceGuard — Stage 6.6.5";
        Width = 1600;
        Height = 900;
        MinimumSize = new Size(1000, 620);
        WindowState = FormWindowState.Normal;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Bg;
        ForeColor = TextMain;

        // Hard two-row root: the header owns the entire top row and the
        // working UI can never dock underneath it or clip its controls.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0),
            Margin = new Padding(0),
            BackColor = Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(24, 10, 24, 8),
            BackColor = Bg
        };
        root.Controls.Add(header, 0, 0);

        var logo = new LogoPanel { Location = new Point(0, 6), Size = new Size(54, 54) };
        header.Controls.Add(logo);

        header.Controls.Add(new Label
        {
            Text = "VoiceGuard",
            Font = new Font("Segoe UI", 21F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(68, 4),
            ForeColor = TextMain
        });

        header.Controls.Add(new Label
        {
            Text = "VOICE CHAT PROFANITY FILTER  -  SOUNDBOARD",
            AutoSize = true,
            Location = new Point(70, 39),
            ForeColor = AccentBright,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold)
        });

        var headerLine = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Border
        };
        header.Controls.Add(headerLine);

        // The four-section working area is confined to row 2 of root.
        var mainHost = new Panel
        { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Bg, Padding = new Padding(0) };
        root.Controls.Add(mainHost, 0, 1);

        var main = new TableLayoutPanel
        {
            Location = new Point(0, 0),
            Width = 1680,
            Height = 1,
            AutoSize = false,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(18, 14, 18, 18),
            Margin = new Padding(0),
            BackColor = Bg
        };
        // Four primary sections: controls, blocked words, soundboard phrases, logs.
        // The host is horizontally scrollable whenever the window is narrower than
        // the complete working surface. Maximized mode normally shows the full layout.
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 510));
        mainHost.Controls.Add(main);
        mainHost.Resize += (_, _) => main.Height = Math.Max(1, mainHost.ClientSize.Height);

        // LEFT: controls are explicitly arranged top-to-bottom in the
        // requested order instead of relying on DockStyle.Top z-order.
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 16,
            Margin = new Padding(0, 0, 14, 0),
            Padding = new Padding(0),
            BackColor = Bg,
            AutoScroll = true
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // input label
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // input
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // output label
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // output
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // master output volume
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // model heading
                left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); // start/stop
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // settings heading
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // delay
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // ptt
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // soundboard listen key
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // start/stop hotkey
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); // mode
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); // status
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); // branding
        main.Controls.Add(left, 0, 0);
        ApplyDarkScrollbarTheme(left);

        var inputLabel = MakeFieldLabel("Input");
        left.Controls.Add(inputLabel, 0, 0);
        input.Dock = DockStyle.Fill;
        input.DropDownStyle = ComboBoxStyle.DropDownList;
        StyleInput(input);
        input.Margin = new Padding(0, 0, 0, 4);
        left.Controls.Add(input, 0, 1);

        var outputLabel = MakeFieldLabel("Output");
        left.Controls.Add(outputLabel, 0, 2);
        output.Dock = DockStyle.Fill;
        output.DropDownStyle = ComboBoxStyle.DropDownList;
        StyleInput(output);
        output.Margin = new Padding(0, 0, 0, 4);
        left.Controls.Add(output, 0, 3);

        var outputVolumePanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var outputVolumeLabel = MakeFieldLabel("Headset Output Volume");
        outputVolumePanel.Controls.Add(outputVolumeLabel);
        outputVolume.Dock = DockStyle.Bottom;
        outputVolume.Height = 28;
        outputVolume.Minimum = 0;
        outputVolume.Maximum = 150;
        outputVolume.TickFrequency = 25;
        outputVolume.SmallChange = 5;
        outputVolume.LargeChange = 10;
        outputVolume.Value = 100;
        outputVolume.Margin = new Padding(0, 0, 48, 0);
        outputVolumePanel.Controls.Add(outputVolume);
        outputVolumeValue.Text = "100%";
        outputVolumeValue.Width = 45;
        outputVolumeValue.Height = 28;
        outputVolumeValue.TextAlign = ContentAlignment.MiddleRight;
        outputVolumeValue.ForeColor = TextDim;
        outputVolumeValue.Dock = DockStyle.Right;
        outputVolumePanel.Controls.Add(outputVolumeValue);
        outputVolume.ValueChanged += (_, _) =>
        {
            outputVolumeValue.Text = $"{outputVolume.Value}%";
            engine?.SetOutputVolume(outputVolume.Value / 100.0);
            if (!loadingPersistence) SavePersistence();
        };
        left.Controls.Add(outputVolumePanel, 0, 4);

        left.Controls.Add(MakeSectionTitle("MODEL & CONTROL"), 0, 5);

        start.Text = "Start VoiceGuard";
        StyleButton(start, true);
        start.Dock = DockStyle.Fill;
        start.Margin = new Padding(0, 2, 0, 6);
        start.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        start.Click += async (_, _) => await ToggleEngineAsync();
        left.Controls.Add(start, 0, 6);

        left.Controls.Add(MakeSectionTitle("SETTINGS"), 0, 7);

        var delayPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var delayLabel = MakeFieldLabel("Delay");
        delayLabel.Dock = DockStyle.Top;
        delayLabel.Height = 24;
        delayPanel.Controls.Add(delayLabel);
        var delayBox = MakeInputBox(delay, 70);
        delayBox.Location = new Point(0, 24);
        delayPanel.Controls.Add(delayBox);
        delayBox.BringToFront();
        delay.Minimum = 2;
        delay.Maximum = 5;
        delay.DecimalPlaces = 1;
        delay.Increment = 0.5M;
        delay.Value = 3;
        left.Controls.Add(delayPanel, 0, 8);

        var pttPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var pttLabel = MakeFieldLabel("PTT key");
        pttLabel.Dock = DockStyle.Top;
        pttLabel.Height = 24;
        pttPanel.Controls.Add(pttLabel);
        var pttBox = MakeInputBox(ptt, 70);
        pttBox.Location = new Point(0, 24);
        pttPanel.Controls.Add(pttBox);
        pttBox.BringToFront();
        ptt.Text = "Z";
        ptt.Tag = Keys.Z;
        ptt.ReadOnly = true;
        ptt.TextAlign = HorizontalAlignment.Center;
        ptt.KeyDown += (_, e) =>
        {
            ptt.Text = e.KeyCode.ToString();
            ptt.Tag = e.KeyCode;
            e.SuppressKeyPress = true;
            SavePersistence();
        };
        left.Controls.Add(pttPanel, 0, 9);

        var soundboardListenKeyPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var soundboardListenKeyLabel = MakeFieldLabel("Soundboard listen key");
        soundboardListenKeyLabel.Dock = DockStyle.Top;
        soundboardListenKeyLabel.Height = 24;
        soundboardListenKeyPanel.Controls.Add(soundboardListenKeyLabel);
        var soundboardKeyBox = MakeInputBox(soundboardListenKey, 70);
        soundboardKeyBox.Location = new Point(0, 24);
        soundboardListenKeyPanel.Controls.Add(soundboardKeyBox);
        soundboardKeyBox.BringToFront();
        soundboardListenKey.Text = ">";
        soundboardListenKey.Tag = Keys.OemPeriod;
        soundboardListenKey.TextAlign = HorizontalAlignment.Center;
        soundboardListenKey.ReadOnly = true;
        soundboardListenKey.KeyDown += (_, e) =>
        {
            soundboardListenKey.Tag = e.KeyCode;
            soundboardListenVirtualKey = (int)e.KeyCode;
            soundboardListenKey.Text = e.KeyCode == Keys.OemPeriod ? ">" : e.KeyCode.ToString();
            e.SuppressKeyPress = true;
            SavePersistence();
        };
        left.Controls.Add(soundboardListenKeyPanel, 0, 10);

        var hotkeyPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var hotkeyLabel = MakeFieldLabel("Start/Stop Hotkey");
        hotkeyLabel.Dock = DockStyle.Top;
        hotkeyLabel.Height = 24;
        hotkeyPanel.Controls.Add(hotkeyLabel);
        startStopHotkey.Dock = DockStyle.None;
        startStopHotkey.Location = new Point(0, 24);
        startStopHotkey.Width = 140;
        startStopHotkey.Height = 28;
        startStopHotkey.ReadOnly = true;
        startStopHotkey.Text = "Ctrl + Alt + V";
        startStopHotkey.Tag = Keys.Control | Keys.Alt | Keys.V;
        StyleInput(startStopHotkey);
        startStopHotkey.TextAlign = HorizontalAlignment.Center;
        startStopHotkey.KeyDown += StartStopHotkey_KeyDown;
        hotkeyPanel.Controls.Add(startStopHotkey);
        left.Controls.Add(hotkeyPanel, 0, 11);

        var startupPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0), Padding = new Padding(0), BackColor = Bg
        };
        startupPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        startupPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        startWithWindows.Text = "Start with Windows";
        startWithWindows.AutoSize = true;
        startWithWindows.ForeColor = TextMain;
        startWithWindows.BackColor = Bg;
        startWithWindows.Margin = new Padding(0);
        startWithWindows.CheckedChanged += (_, _) =>
        {
            if (!loadingPersistence)
            {
                SetStartWithWindows(startWithWindows.Checked);
                SavePersistence();
            }
        };
        startupPanel.Controls.Add(startWithWindows, 0, 0);

        minimizeToTray.Text = "Minimize to system tray";
        minimizeToTray.AutoSize = true;
        minimizeToTray.ForeColor = TextMain;
        minimizeToTray.BackColor = Bg;
        minimizeToTray.Margin = new Padding(0);
        minimizeToTray.CheckedChanged += (_, _) =>
        {
            if (!loadingPersistence) SavePersistence();
        };
        startupPanel.Controls.Add(minimizeToTray, 0, 1);

        left.Controls.Add(startupPanel, 0, 12);

        // A compact status area lives below the fixed controls if there is
        // room; it does not participate in the three primary control order.
        status.Text = "Whisper loads automatically when VoiceGuard starts.";
        status.AutoSize = false;
        status.Dock = DockStyle.Bottom;
        status.Height = 44;
        status.ForeColor = TextDim;
        mode.Text = "MODE: STOPPED";
        mode.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        mode.AutoSize = false;
        mode.Dock = DockStyle.Fill;
        mode.Height = 42;
        mode.TextAlign = ContentAlignment.MiddleCenter;
        mode.Margin = new Padding(0);
        left.Controls.Add(mode, 0, 13);
        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0);
        left.Controls.Add(status, 0, 14);

        var jackBrand = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4),
            // Keep the branding compact while preserving the existing row size.
            // Keep the branding noticeably smaller so it does not dominate the controls.
            Padding = new Padding(53, 14, 53, 14)
        };

        var brandPath = Path.Combine(AppContext.BaseDirectory, "JackTheGooner_Purple.png");
        if (File.Exists(brandPath))
        {
            try
            {
                using var stream = File.OpenRead(brandPath);
                using var sourceImage = Image.FromStream(stream);
                jackBrand.Image = new Bitmap(sourceImage);
            }
            catch
            {
                // Branding is optional; the rest of VoiceGuard must still load.
            }
        }

        left.Controls.Add(jackBrand, 0, 13);

        // MIDDLE: a dedicated three-row layout makes the ListBox bounds
        // unambiguous: title, list (fills), controls/help.
        var wordPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 7, 0),
            Padding = new Padding(8, 0, 7, 0),
            BackColor = Bg
        };
        wordPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        wordPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wordPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        main.Controls.Add(wordPanel, 1, 0);

        var wordTitle = new Label
        {
            Text = "BLOCKED WORDS",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        wordPanel.Controls.Add(wordTitle, 0, 0);

        words.Dock = DockStyle.Fill;
        words.Margin = new Padding(0);
        words.SelectionMode = SelectionMode.One;
        words.BorderStyle = BorderStyle.FixedSingle;
        words.BackColor = Surface;
        words.ForeColor = TextMain;
        words.DrawMode = DrawMode.OwnerDrawFixed;
        words.ItemHeight = 70;
        words.IntegralHeight = false;
        words.HorizontalScrollbar = true;
        words.ScrollAlwaysVisible = false;
        words.DrawItem += DrawBlockedWordItem;
        words.MouseDown += WordsMouseDown;
        words.MouseMove += WordsMouseMove;
        words.MouseUp += WordsMouseUp;
        wordPanel.Controls.Add(words, 0, 1);
        ApplyDarkScrollbarTheme(words);

        var wordBottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0)
        };
        wordBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        wordBottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wordPanel.Controls.Add(wordBottom, 0, 2);

        var wordButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        addWord.Text = "Add word";
        StyleButton(addWord, false);
        addWord.Width = 105;
        addWord.Height = 30;
        addWord.Margin = new Padding(0, 0, 7, 0);
        addWord.Click += (_, _) => AddBlockedWord();
        removeWord.Text = "Remove";
        StyleButton(removeWord, false);
        removeWord.Width = 105;
        removeWord.Height = 30;
        removeWord.Margin = new Padding(0);
        removeWord.Click += (_, _) => RemoveBlockedWord();
        wordButtons.Controls.Add(addWord);
        wordButtons.Controls.Add(removeWord);
        wordBottom.Controls.Add(wordButtons, 0, 0);

        var hint = new Label
        {
            Text = "Right-click a word to add/manage aliases or set a replacement sound.",
            Dock = DockStyle.Fill,
            ForeColor = TextDim,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 4, 0, 0),
            Margin = new Padding(0)
        };
        wordBottom.Controls.Add(hint, 0, 1);

        wordsMenu.Items.Clear();
        wordsMenu.Items.Add("Add alias...", null, (_, _) => AddAliasToSelectedWord());
        wordsMenu.Items.Add("Manage aliases...", null, (_, _) => ManageAliasesForSelectedWord());
        wordsMenu.Items.Add("Set replacement sound...", null, (_, _) => SetReplacementSoundForSelectedWord());
        wordsMenu.Items.Add("Clear replacement sound", null, (_, _) => ClearReplacementSoundForSelectedWord());
        wordsMenu.Items.Add("Replacement playback...", null, (_, _) => ConfigureReplacementPlaybackForSelectedWord());
        wordsMenu.Items.Add(new ToolStripSeparator());
        wordsMenu.Items.Add("Remove word", null, (_, _) => RemoveBlockedWord());
        words.ContextMenuStrip = wordsMenu;

        var soundboardPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = new Padding(0, 0, 7, 0), Padding = new Padding(8, 0, 7, 0), BackColor = Bg
        };
        soundboardPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        soundboardPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        soundboardPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        main.Controls.Add(soundboardPanel, 2, 0);

        soundboardPanel.Controls.Add(new Label
        { Text = "SOUNDBOARD PHRASES", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10F, FontStyle.Bold),
          TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);

        soundboard.Dock = DockStyle.Fill;
        soundboard.Margin = new Padding(0);
        soundboard.SelectionMode = SelectionMode.One;
        soundboard.BorderStyle = BorderStyle.FixedSingle;
        soundboard.BackColor = Surface;
        soundboard.ForeColor = TextMain;
        soundboard.DrawMode = DrawMode.OwnerDrawFixed;
        soundboard.ItemHeight = 88;
        soundboard.IntegralHeight = false;
        soundboard.HorizontalScrollbar = true;
        soundboard.DrawItem += DrawSoundboardItem;
        soundboard.DoubleClick += (_, _) => EditSelectedSoundboard();
        var soundboardMenu = new ContextMenuStrip();
        soundboardMenu.Items.Add("Add alias...", null, (_, _) => AddAliasToSelectedSoundboard());
        soundboardMenu.Items.Add("Manage aliases...", null, (_, _) => ManageAliasesForSelectedSoundboard());
        soundboardMenu.Items.Add("Replace / edit WAV...", null, (_, _) => EditSelectedSoundboard());
        soundboard.ContextMenuStrip = soundboardMenu;
        soundboard.MouseDown += SoundboardMouseDown;
        soundboard.MouseMove += SoundboardMouseMove;
        soundboard.MouseUp += SoundboardMouseUp;
        soundboardPanel.Controls.Add(soundboard, 0, 1);
        ApplyDarkScrollbarTheme(soundboard);

        var soundboardBottom = new TableLayoutPanel
        { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0, 4, 0, 0) };
        soundboardBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        soundboardBottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        soundboardPanel.Controls.Add(soundboardBottom, 0, 2);

        var sbButtons = new TableLayoutPanel
        { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 6, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
        sbButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        sbButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        sbButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        sbButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        sbButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        sbButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        sbButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        sbButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        addSoundboard.Text = "Add phrase"; StyleButton(addSoundboard, false); addSoundboard.Margin = new Padding(0,0,6,2); addSoundboard.Click += (_,_) => AddSoundboardPhrase();
        editSoundboard.Text = "Edit"; StyleButton(editSoundboard, false); editSoundboard.Margin = new Padding(0,0,6,2); editSoundboard.Click += (_,_) => EditSelectedSoundboard();
        removeSoundboard.Text = "Remove"; StyleButton(removeSoundboard, false); removeSoundboard.Margin = new Padding(0,0,6,2); removeSoundboard.Click += (_,_) => RemoveSelectedSoundboard();
        listenSoundboard.Text = "Listen & Trigger"; StyleButton(listenSoundboard, true); listenSoundboard.Margin = new Padding(0,0,6,2); listenSoundboard.Click += (_,_) => BeginSoundboardListenFromKey();
        sbButtons.Controls.Add(addSoundboard,0,0); sbButtons.Controls.Add(editSoundboard,1,0); sbButtons.Controls.Add(removeSoundboard,2,0); sbButtons.Controls.Add(listenSoundboard,3,0);
        soundboardBottom.Controls.Add(sbButtons,0,0);
        soundboardBottom.Controls.Add(new Label
        { Text = "Listen & Trigger captures a longer phrase privately, then holds your configured PTT key while the soundboard clip is transmitted.", Dock = DockStyle.Fill, ForeColor = TextDim, Padding = new Padding(0,4,0,0), Margin = new Padding(0) },0,1);

        // RIGHT SIDE:
        // Column 0 = controls
        // Column 1 = blocked words
        // Column 2 = soundboard phrases
        // Column 3 = LOGS
        //
        // The log is deliberately hosted in its own right-column panel.
        // It is never added to the controls column or blocked-words column.
        var logSection = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 0),
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Bg
        };
        main.Controls.Add(logSection, 3, 0);

        var logTitle = new Label
        {
            Text = "LOGS",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            ForeColor = TextMain
        };
        logSection.Controls.Add(logTitle);

        var logHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(10),
            BackColor = Color.FromArgb(18, 15, 25),
            BorderStyle = BorderStyle.FixedSingle
        };
        logSection.Controls.Add(logHost);
        logHost.BringToFront();

        log.Multiline = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.ReadOnly = true;
        log.Dock = DockStyle.Fill;
        log.Margin = new Padding(0);
        log.WordWrap = true;
        log.BackColor = Color.FromArgb(24, 24, 24);
        log.ForeColor = Color.Gainsboro;
        log.Font = new Font("Consolas", 9F);
        logHost.Controls.Add(log);

        delay.ValueChanged += (_, _) => SavePersistence();
        ptt.KeyDown += (_, _) => SavePersistence();
        input.SelectedIndexChanged += (_, _) => SavePersistence();
        output.SelectedIndexChanged += (_, _) => SavePersistence();

        InitializeTrayIcon();
        FormClosing += MainForm_FormClosing;
        SizeChanged += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && minimizeToTray.Checked)
                HideToTray();
        };
        Load += (_, _) =>
        {
            LoadPersistence();
            LoadDevices();
            ApplyStartWithWindowsSetting();
            RegisterStartStopHotkey();
            if (launchedMinimized && minimizeToTray.Checked)
                BeginInvoke(HideToTray);
        };
    }

    private static Label MakeSectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        ForeColor = TextDim,
        TextAlign = ContentAlignment.BottomLeft,
        Padding = new Padding(0, 0, 0, 6),
        Margin = new Padding(0)
    };

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.BottomLeft,
        Padding = new Padding(0, 0, 0, 4),
        Margin = new Padding(0)
    };

    private static Panel MakeInputBox(Control control, int width)
    {
        // Explicit bordered container.  Keep the editor itself inside the
        // container so the compact Delay/PTT/Soundboard fields remain visibly
        // configurable instead of looking like plain labels.
        var box = new Panel
        {
            Width = width,
            Height = 30,
            BackColor = Border,
            Padding = new Padding(1),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0),
            TabStop = false
        };

        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0);
        StyleInput(control);
        box.Controls.Add(control);
        box.BringToFront();
        return box;
    }

    private static void StyleInput(Control control)
    {
        control.BackColor = Surface;
        control.ForeColor = TextMain;
        if (control is TextBoxBase tb) tb.BorderStyle = BorderStyle.FixedSingle;
        if (control is NumericUpDown nud)
        {
            nud.BackColor = Surface;
            nud.ForeColor = TextMain;
        }
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.BackColor = primary ? Color.FromArgb(72, 31, 112) : Surface2;
        button.ForeColor = TextMain;
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(91, 39, 140) : Color.FromArgb(37, 29, 49);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(56, 24, 82);
    }

    private sealed class LogoPanel : Panel
    {
        public LogoPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(6, 4, Width - 12, Height - 8);

            using var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                r, AccentBright, Accent, 90f);
            var shield = new PointF[]
            {
                new(r.Left + r.Width * .50f, r.Top),
                new(r.Right, r.Top + r.Height * .18f),
                new(r.Right - 2, r.Top + r.Height * .57f),
                new(r.Left + r.Width * .50f, r.Bottom),
                new(r.Left + 2, r.Top + r.Height * .57f),
                new(r.Left, r.Top + r.Height * .18f)
            };
            e.Graphics.FillPolygon(glow, shield);

            using var inner = new Pen(Color.FromArgb(235, 255, 255, 255), 1.5f);
            e.Graphics.DrawPolygon(inner, shield);

            using var wave = new Pen(Color.White, 2.4f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            float x = r.Left + 11;
            float mid = r.Top + r.Height * .52f;
            e.Graphics.DrawLines(wave, new[]
            {
                new PointF(x, mid), new PointF(x + 5, mid), new PointF(x + 8, mid - 8),
                new PointF(x + 12, mid + 10), new PointF(x + 16, mid - 13),
                new PointF(x + 20, mid + 5), new PointF(x + 24, mid)
            });
        }
    }

    private void LoadDevices()
    {
        input.Items.Clear();
        output.Items.Clear();

        foreach (var d in AudioDeviceEnumerator.GetInputs())
            input.Items.Add(d);

        foreach (var d in AudioDeviceEnumerator.GetOutputs())
            output.Items.Add(d);

        string? savedInput = loadedInputDeviceName;
        string? savedOutput = loadedOutputDeviceName;

        if (!string.IsNullOrWhiteSpace(savedInput))
        {
            var match = input.Items.Cast<AudioDeviceInfo>()
                .FirstOrDefault(d => string.Equals(d.Name, savedInput, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                input.SelectedItem = match;
        }

        if (input.SelectedIndex < 0 && input.Items.Count > 0)
            input.SelectedIndex = 0;

        var cable = output.Items.Cast<AudioDeviceInfo>()
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(savedOutput)
                && string.Equals(d.Name, savedOutput, StringComparison.OrdinalIgnoreCase));

        cable ??= output.Items.Cast<AudioDeviceInfo>()
            .FirstOrDefault(d => d.Name.Contains(
                "CABLE Input", StringComparison.OrdinalIgnoreCase));

        if (cable != null)
            output.SelectedItem = cable;
        else if (output.Items.Count > 0)
            output.SelectedIndex = 0;

        loadingPersistence = false;
    }

    private void LoadPersistence()
    {
        loadingPersistence = true;

        try
        {
            if (!File.Exists(ConfigPath))
            {
                loadingPersistence = false;
                ApplyStartWithWindowsSetting();
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<VoiceGuardConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config == null)
            {
                loadingPersistence = false;
                return;
            }

            words.Items.Clear();
            blockedWordAliases.Clear();
            replacementSounds.Clear();
            replacementPlaybackSettings.Clear();
            replacementVolumes.Clear();
            soundboardSounds.Clear();
            soundboardVolumes.Clear();
            soundboardAliases.Clear();
            soundboard.Items.Clear();

            foreach (var word in config.BlockedWords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                words.Items.Add(word);
                blockedWordAliases[word] = new List<string>();
            }

            foreach (var pair in config.Aliases ?? new Dictionary<string, List<string>>())
            {
                var actualWord = words.Items.Cast<object>()
                    .Select(x => x?.ToString() ?? "")
                    .FirstOrDefault(x => string.Equals(x, pair.Key, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(actualWord))
                    continue;

                blockedWordAliases[actualWord] = (pair.Value ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            foreach (var pair in config.ReplacementSounds ?? new Dictionary<string, string?>())
            {
                var actualWord = words.Items.Cast<object>()
                    .Select(x => x?.ToString() ?? "")
                    .FirstOrDefault(x => string.Equals(x, pair.Key, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(actualWord) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    replacementSounds[actualWord] = pair.Value;
                }
            }

            foreach (var pair in config.ReplacementPlayback ?? new Dictionary<string, ReplacementPlaybackSettings>())
            {
                var actualWord = words.Items.Cast<object>()
                    .Select(x => x?.ToString() ?? "")
                    .FirstOrDefault(x => string.Equals(x, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(actualWord))
                {
                    var value = pair.Value;
                    replacementPlaybackSettings[actualWord] = new ReplacementPlaybackSettings(
                        value.MatchWordLength,
                        Math.Clamp(value.DurationSeconds, 0.1, 5.0));
                }
            }

            foreach (var pair in config.ReplacementVolumes ?? new Dictionary<string, double>())
            {
                var actualWord = words.Items.Cast<object>()
                    .Select(x => x?.ToString() ?? "")
                    .FirstOrDefault(x => string.Equals(x, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(actualWord))
                    replacementVolumes[actualWord] = Math.Clamp(pair.Value, 0.0, 1.5);
            }

            foreach (var pair in config.SoundboardPhrases ?? new Dictionary<string, SoundboardEntryConfig>())
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.SoundPath)) continue;
                if (!File.Exists(pair.Value.SoundPath)) continue;
                string trigger = pair.Key.Trim();
                soundboardSounds[trigger] = pair.Value.SoundPath;
                soundboardVolumes[trigger] = Math.Clamp(pair.Value.Volume, 0.0, 1.5);
                soundboard.Items.Add(trigger);
            }

            if (config.OutputVolumePercent >= 0 && config.OutputVolumePercent <= 150)
                outputVolume.Value = (int)Math.Round(config.OutputVolumePercent);
            outputVolumeValue.Text = $"{outputVolume.Value}%";

            if (config.DelaySeconds >= delay.Minimum && config.DelaySeconds <= delay.Maximum)
                delay.Value = config.DelaySeconds;

            if (Enum.TryParse<Keys>(config.PttKey, true, out var savedKey))
            {
                ptt.Tag = savedKey;
                ptt.Text = savedKey.ToString();
            }

             if (Enum.TryParse<Keys>(config.SoundboardListenKey, true, out var savedListenKey))
             {
                 soundboardListenKey.Tag = savedListenKey;
                 soundboardListenVirtualKey = (int)savedListenKey;
                 soundboardListenKey.Text = savedListenKey == Keys.OemPeriod ? ">" : savedListenKey.ToString();
             }

            loadedInputDeviceName = config.InputDevice;
            loadedOutputDeviceName = config.OutputDevice;

            startWithWindows.Checked = config.StartWithWindows;
            minimizeToTray.Checked = config.MinimizeToTray;
            if (TryParseHotkey(config.StartStopHotkey, out var savedHotkey))
            {
                startStopHotkey.Tag = savedHotkey;
                startStopHotkey.Text = FormatHotkey(savedHotkey);
            }
            launchedMinimized = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (words.Items.Count > 0)
                words.SelectedIndex = 0;

            AddLog($"Configuration loaded — {words.Items.Count} blocked word(s), {blockedWordAliases.Values.Sum(x => x.Count)} alias(es).");
        }
        catch (Exception ex)
        {
            AddLog($"CONFIG LOAD ERROR — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SavePersistence()
    {
        if (loadingPersistence || IsDisposed)
            return;

        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            var config = new VoiceGuardConfig
            {
                BlockedWords = GetWords().ToList(),
                Aliases = blockedWordAliases.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                ReplacementSounds = replacementSounds.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                ReplacementPlayback = replacementPlaybackSettings.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                ReplacementVolumes = replacementVolumes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                SoundboardPhrases = soundboardSounds.ToDictionary(
                    pair => pair.Key,
                    pair => new SoundboardEntryConfig { SoundPath = pair.Value, Volume = GetSoundboardVolume(pair.Key) },
                    StringComparer.OrdinalIgnoreCase),
                SoundboardAliases = soundboardAliases.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
                SoundboardListenKey = (soundboardListenKey.Tag is Keys listenKey ? listenKey : Keys.OemPeriod).ToString(),
                OutputVolumePercent = outputVolume.Value,
                DelaySeconds = delay.Value,
                PttKey = (ptt.Tag is Keys key ? key : Keys.Z).ToString(),
                InputDevice = input.SelectedItem is AudioDeviceInfo inputDevice ? inputDevice.Name : null,
                OutputDevice = output.SelectedItem is AudioDeviceInfo outputDevice ? outputDevice.Name : null,
                StartWithWindows = startWithWindows.Checked,
                MinimizeToTray = minimizeToTray.Checked,
                StartStopHotkey = FormatHotkeyForConfig(startStopHotkey.Tag is Keys startStopKey ? startStopKey : Keys.Control | Keys.Alt | Keys.V)
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, true);
        }
        catch (Exception ex)
        {
            AddLog($"CONFIG SAVE ERROR — {ex.GetType().Name}: {ex.Message}");
        }
    }


    private void InitializeTrayIcon()
    {
        trayMenu.Items.Clear();

        var showItem = trayMenu.Items.Add("Show VoiceGuard");
        showItem.Click += (_, _) => RestoreFromTray();

        var toggleItem = trayMenu.Items.Add("Start / Stop VoiceGuard");
        toggleItem.Click += async (_, _) => await ToggleEngineAsync();

        trayMenu.Items.Add(new ToolStripSeparator());

        var exitItem = trayMenu.Items.Add("Exit VoiceGuard");
        exitItem.Click += (_, _) =>
        {
            exitingApplication = true;
            trayIcon.Visible = false;
            Close();
        };

        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Text = "VoiceGuard";
        trayIcon.Icon = taskbarSmallIcon ?? SystemIcons.Application;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        if (!minimizeToTray.Checked)
            return;

        Hide();
        WindowState = FormWindowState.Minimized;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!exitingApplication && minimizeToTray.Checked)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        UnregisterStartStopHotkey();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        SavePersistence();
        StopEngine();
    }

    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null)
                return;

            const string valueName = "VoiceGuard";
            if (enabled)
            {
                string args = minimizeToTray.Checked ? " --minimized" : "";
                key.SetValue(valueName, $"\"{Application.ExecutablePath}\"{args}");
            }
            else
            {
                key.DeleteValue(valueName, false);
            }
        }
        catch (Exception ex)
        {
            AddLog($"STARTUP ERROR: {ex.Message}");
        }
    }

    private void ApplyStartWithWindowsSetting()
    {
        SetStartWithWindows(startWithWindows.Checked);
    }

    private static uint GetHotkeyModifiers(Keys keyData)
    {
        uint modifiers = 0;
        if ((keyData & Keys.Control) == Keys.Control) modifiers |= MOD_CONTROL;
        if ((keyData & Keys.Alt) == Keys.Alt) modifiers |= MOD_ALT;
        if ((keyData & Keys.Shift) == Keys.Shift) modifiers |= MOD_SHIFT;
        if ((keyData & Keys.LWin) == Keys.LWin || (keyData & Keys.RWin) == Keys.RWin)
            modifiers |= MOD_WIN;
        return modifiers;
    }

    private static Keys GetHotkeyKey(Keys keyData) => keyData & Keys.KeyCode;

    private static string FormatHotkey(Keys keyData)
    {
        var parts = new List<string>();
        if ((keyData & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((keyData & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        if ((keyData & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if ((keyData & Keys.LWin) == Keys.LWin || (keyData & Keys.RWin) == Keys.RWin) parts.Add("Win");

        var key = GetHotkeyKey(keyData);
        if (key != Keys.None && !key.ToString().Equals("ControlKey", StringComparison.OrdinalIgnoreCase)
            && !key.ToString().Equals("Menu", StringComparison.OrdinalIgnoreCase)
            && !key.ToString().Equals("ShiftKey", StringComparison.OrdinalIgnoreCase))
            parts.Add(key.ToString());

        return string.Join(" + ", parts);
    }

    private static string FormatHotkeyForConfig(Keys keyData)
    {
        var parts = new List<string>();
        if ((keyData & Keys.Control) == Keys.Control) parts.Add("Control");
        if ((keyData & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        if ((keyData & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if ((keyData & Keys.LWin) == Keys.LWin || (keyData & Keys.RWin) == Keys.RWin) parts.Add("Win");
        var key = GetHotkeyKey(keyData);
        if (key != Keys.None) parts.Add(key.ToString());
        return string.Join(", ", parts);
    }

    private static bool TryParseHotkey(string? value, out Keys hotkey)
    {
        hotkey = Keys.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
                hotkey |= Keys.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                hotkey |= Keys.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                hotkey |= Keys.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase))
                hotkey |= Keys.LWin;
            else if (Enum.TryParse<Keys>(part, true, out var parsed) && parsed != Keys.None)
                hotkey |= parsed & Keys.KeyCode;
        }

        return GetHotkeyKey(hotkey) != Keys.None;
    }

    private void StartStopHotkey_KeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.KeyCode;
        if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey)
            return;

        Keys keyData = e.KeyData;
        if (GetHotkeyKey(keyData) == Keys.None)
            return;

        startStopHotkey.Tag = keyData;
        startStopHotkey.Text = FormatHotkey(keyData);
        SavePersistence();
        RegisterStartStopHotkey();
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void RegisterStartStopHotkey()
    {
        if (!IsHandleCreated)
            return;

        UnregisterStartStopHotkey();

        var hotkey = startStopHotkey.Tag is Keys key
            ? key
            : Keys.Control | Keys.Alt | Keys.V;

        var modifiers = GetHotkeyModifiers(hotkey) | MOD_NOREPEAT;
        var keyCode = GetHotkeyKey(hotkey);
        if (keyCode == Keys.None)
            return;

        if (!RegisterHotKey(Handle, StartStopHotkeyId, modifiers, (uint)keyCode))
        {
            AddLog("START/STOP HOTKEY: unable to register selected hotkey.");
        }
    }

    private void UnregisterStartStopHotkey()
    {
        if (IsHandleCreated)
            _ = UnregisterHotKey(Handle, StartStopHotkeyId);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == StartStopHotkeyId)
        {
            BeginInvoke(async () => await ToggleEngineAsync());
        }

        base.WndProc(ref m);
    }

    private async Task PrepareModelAsync()
    {
        try
        {
            SetStatus("Preparing Whisper base.en model...");

            detector?.SetWords(GetWords());

            if (detector == null)
            {
                detector = new SpeechDetector(GetWords(), AddLog);
            }

            SyncAliasesToDetector();
            detector.SetSoundboardPhrases(soundboardSounds.Keys);
            detector.SetSoundboardAliases(BuildSoundboardAliasMap());

            AddLog($"Process architecture: {Environment.Is64BitProcess} (64-bit=True)");
            AddLog($"OS: {Environment.OSVersion}");
            AddLog($"Model folder: {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceGuard", "Models")}");
            await detector.DownloadModelAsync();

            SetStatus("Whisper model ready.");
        }
        catch (Exception ex)
        {
            SetStatus("Model error.");
            AddLog("MODEL ERROR: " + ex);
            MessageBox.Show(ex.ToString(), "VoiceGuard model error");
        }
        finally
        {
        }
    }

    private string[] GetWords()
    {
        return words.Items.Cast<object>()
            .Select(x => x?.ToString() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    private static void ApplyDarkScrollbarTheme(Control control)
    {
        try
        {
            _ = control.Handle;
            // Windows exposes a dark scrollbar theme through the UxTheme API.
            // If unavailable on an older Windows build, the normal scrollbar remains.
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch
        {
            // Scrollbar theming is cosmetic only.
        }
    }

    private void DrawBlockedWordItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= words.Items.Count) { e.DrawFocusRectangle(); return; }
        string value = words.Items[e.Index]?.ToString() ?? "";
        bool hasSound = replacementSounds.TryGetValue(value, out var sound) && !string.IsNullOrWhiteSpace(sound);
        var outer = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, e.Bounds.Width - 8, e.Bounds.Height - 6);
        using var pen = new Pen(Border);
        using var brush = new SolidBrush(e.State.HasFlag(DrawItemState.Selected) ? Color.FromArgb(65, 34, 92) : Surface2);
        e.Graphics.FillRectangle(brush, outer); e.Graphics.DrawRectangle(pen, outer);
        var wordRect = new Rectangle(outer.Left + 8, outer.Top + 4, Math.Max(80, outer.Width - 150), 24);
        TextRenderer.DrawText(e.Graphics, value, words.Font, wordRect, TextMain, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        var replacementRect = new Rectangle(outer.Right - 128, outer.Top + 4, 118, 25);
        using (var replacementBrush = new SolidBrush(hasSound ? Color.FromArgb(72, 31, 112) : Surface))
        using (var replacementPen = new Pen(hasSound ? Accent : Border))
        { e.Graphics.FillRectangle(replacementBrush, replacementRect); e.Graphics.DrawRectangle(replacementPen, replacementRect); }
        string replacementName = hasSound ? $"🔊 {Path.GetFileName(sound!)}" : "Replacement";
        TextRenderer.DrawText(e.Graphics, replacementName, words.Font, replacementRect, TextMain, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (hasSound)
        {
            DrawVolumeSlider(e.Graphics, GetReplacementSliderRect(index: e.Index), GetReplacementVolume(value));
            var testRect = GetReplacementTestRect(e.Index);
            using var testBrush = new SolidBrush(Color.FromArgb(37, 29, 49));
            using var testPen = new Pen(Border);
            e.Graphics.FillRectangle(testBrush, testRect); e.Graphics.DrawRectangle(testPen, testRect);
            TextRenderer.DrawText(e.Graphics, "Test", words.Font, testRect, TextMain, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        e.DrawFocusRectangle();
    }

    private void DrawVolumeSlider(Graphics g, Rectangle rect, double volume)
    {
        int y = rect.Top + rect.Height / 2, left = rect.Left + 4, right = rect.Right - 4;
        using var trackPen = new Pen(Border, 4); g.DrawLine(trackPen, left, y, right, y);
        int x = left + (int)Math.Round((right - left) * Math.Clamp(volume / 1.5, 0.0, 1.0));
        using var fillPen = new Pen(Accent, 4); g.DrawLine(fillPen, left, y, x, y);
        using var knobBrush = new SolidBrush(AccentBright); g.FillEllipse(knobBrush, x - 6, y - 6, 12, 12);
        TextRenderer.DrawText(g, $"{Math.Round(volume * 100):0}%", words.Font, new Rectangle(rect.Right + 6, rect.Top - 5, 48, 28), TextDim, TextFormatFlags.VerticalCenter);
    }

    private Rectangle GetReplacementSliderRect(int index)
    {
        var b = words.GetItemRectangle(index); var o = new Rectangle(b.Left + 4, b.Top + 3, b.Width - 8, b.Height - 6);
        return new Rectangle(o.Left + 8, o.Top + 38, Math.Max(80, o.Width - 170), 18);
    }
    private Rectangle GetReplacementTestRect(int index)
    {
        var b = words.GetItemRectangle(index); var o = new Rectangle(b.Left + 4, b.Top + 3, b.Width - 8, b.Height - 6);
        return new Rectangle(o.Right - 88, o.Top + 34, 80, 27);
    }
    private Rectangle GetReplacementButtonRect(int index)
    {
        var b = words.GetItemRectangle(index); var o = new Rectangle(b.Left + 4, b.Top + 3, b.Width - 8, b.Height - 6);
        return new Rectangle(o.Right - 128, o.Top + 4, 118, 25);
    }
    private void SetReplacementVolumeFromPoint(int index, int x)
    {
        if (index < 0 || index >= words.Items.Count) return;
        string word = words.Items[index]?.ToString() ?? ""; if (!replacementSounds.ContainsKey(word)) return;
        var r = GetReplacementSliderRect(index); int left = r.Left + 4, right = r.Right - 4;
        double normalized = right <= left ? 1.0 : (x - left) / (double)(right - left);
        replacementVolumes[word] = Math.Clamp(normalized, 0.0, 1.0) * 1.5;
        words.Invalidate(words.GetItemRectangle(index)); SavePersistence();
    }
    private void WordsMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return; int index = words.IndexFromPoint(e.Location);
        if (index < 0 || index >= words.Items.Count) return; words.SelectedIndex = index;
        string word = words.Items[index]?.ToString() ?? ""; if (!replacementSounds.ContainsKey(word)) return;
        if (GetReplacementSliderRect(index).Contains(e.Location)) { draggingReplacementVolume = true; draggingReplacementIndex = index; SetReplacementVolumeFromPoint(index, e.X); }
        else if (GetReplacementTestRect(index).Contains(e.Location)) TestReplacementSound(word);
        else if (GetReplacementButtonRect(index).Contains(e.Location)) SetReplacementSoundForSelectedWord();
    }
    private void WordsMouseMove(object? sender, MouseEventArgs e)
    {
        if (draggingReplacementVolume && draggingReplacementIndex >= 0 && e.Button == MouseButtons.Left) SetReplacementVolumeFromPoint(draggingReplacementIndex, e.X);
    }
    private void WordsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { draggingReplacementVolume = false; draggingReplacementIndex = -1; return; }
        if (e.Button != MouseButtons.Right) return; int index = words.IndexFromPoint(e.Location);
        if (index < 0 || index >= words.Items.Count) return; words.SelectedIndex = index; wordsMenu.Show(words, e.Location);
    }

    private void DrawSoundboardItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= soundboard.Items.Count) { e.DrawFocusRectangle(); return; }
        string trigger = soundboard.Items[e.Index]?.ToString() ?? "";
        soundboardSounds.TryGetValue(trigger, out var path);
        double volume = GetSoundboardVolume(trigger);
        var outer = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, e.Bounds.Width - 8, e.Bounds.Height - 6);
        using var pen = new Pen(Border);
        using var brush = new SolidBrush(e.State.HasFlag(DrawItemState.Selected) ? Color.FromArgb(65, 34, 92) : Surface2);
        e.Graphics.FillRectangle(brush, outer); e.Graphics.DrawRectangle(pen, outer);
        TextRenderer.DrawText(e.Graphics, trigger, soundboard.Font, new Rectangle(outer.Left + 8, outer.Top + 4, outer.Width - 16, 24), TextMain, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        string name = string.IsNullOrWhiteSpace(path) ? "No sound" : Path.GetFileName(path);
        TextRenderer.DrawText(e.Graphics, "🔊  " + name, soundboard.Font, new Rectangle(outer.Left + 8, outer.Top + 27, Math.Max(120, outer.Width - 90), 22), TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        string aliases = soundboardAliases.TryGetValue(trigger, out var a) && a.Count > 0 ? "Aliases: " + string.Join(", ", a) : "Aliases: none";
        TextRenderer.DrawText(e.Graphics, aliases, soundboard.Font, new Rectangle(outer.Left + 8, outer.Top + 45, Math.Max(120, outer.Width - 90), 18), TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        var sliderRect = new Rectangle(outer.Left + 8, outer.Bottom - 25, Math.Max(120, outer.Width - 155), 18);
        DrawSoundboardVolumeSlider(e.Graphics, sliderRect, volume);
        var testRect = new Rectangle(outer.Right - 62, outer.Bottom - 30, 58, 28);
        using var testBrush = new SolidBrush(Surface);
        using var testPen = new Pen(Border);
        e.Graphics.FillRectangle(testBrush, testRect);
        e.Graphics.DrawRectangle(testPen, testRect);
        TextRenderer.DrawText(e.Graphics, "Test", soundboard.Font, testRect, TextMain, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        e.DrawFocusRectangle();
    }

    private void DrawSoundboardVolumeSlider(Graphics g, Rectangle rect, double volume)
    {
        int y = rect.Top + rect.Height / 2, left = rect.Left + 4, right = rect.Right - 4;
        using var track = new Pen(Border, 4); g.DrawLine(track, left, y, right, y);
        int x = left + (int)Math.Round((right-left) * Math.Clamp(volume/1.5,0,1));
        using var fill = new Pen(Accent,4); g.DrawLine(fill,left,y,x,y);
        using var knob = new SolidBrush(AccentBright); g.FillEllipse(knob,x-6,y-6,12,12);
        TextRenderer.DrawText(g,$"{Math.Round(volume*100):0}%",soundboard.Font,new Rectangle(rect.Right+4,rect.Top-5,48,28),TextDim,TextFormatFlags.VerticalCenter);
    }

    private Rectangle GetSoundboardSliderRect(int index) { var b=soundboard.GetItemRectangle(index); var o=new Rectangle(b.Left+4,b.Top+3,b.Width-8,b.Height-6); return new Rectangle(o.Left+8,o.Bottom-28,Math.Max(120,o.Width-155),18); }
    private Rectangle GetSoundboardTestRect(int index) { var b=soundboard.GetItemRectangle(index); var o=new Rectangle(b.Left+4,b.Top+3,b.Width-8,b.Height-6); return new Rectangle(o.Right-62,o.Bottom-30,58,28); }
    private void SetSoundboardVolumeFromPoint(int index,int x)
    {
        if(index<0||index>=soundboard.Items.Count)return; string trigger=soundboard.Items[index]?.ToString()??""; if(!soundboardSounds.ContainsKey(trigger))return;
        var r=GetSoundboardSliderRect(index); int left=r.Left+4,right=r.Right-4; double n=right<=left?1:(x-left)/(double)(right-left); soundboardVolumes[trigger]=Math.Clamp(n,0,1)*1.5; soundboard.Invalidate(soundboard.GetItemRectangle(index)); SavePersistence();
    }
    private bool draggingSoundboardVolume;
    private int draggingSoundboardIndex=-1;
    private void SoundboardMouseDown(object? sender,MouseEventArgs e)
    {
        int index=soundboard.IndexFromPoint(e.Location); if(index<0||index>=soundboard.Items.Count)return; soundboard.SelectedIndex=index; string trigger=soundboard.Items[index]?.ToString()??"";
        if(e.Button==MouseButtons.Left){ if(GetSoundboardSliderRect(index).Contains(e.Location)){draggingSoundboardVolume=true;draggingSoundboardIndex=index;SetSoundboardVolumeFromPoint(index,e.X);} else if(GetSoundboardTestRect(index).Contains(e.Location)) TestSoundboardSound(trigger); }
    }
    private void SoundboardMouseMove(object? sender,MouseEventArgs e){if(draggingSoundboardVolume&&draggingSoundboardIndex>=0&&e.Button==MouseButtons.Left)SetSoundboardVolumeFromPoint(draggingSoundboardIndex,e.X);}
    private void SoundboardMouseUp(object? sender,MouseEventArgs e){if(e.Button==MouseButtons.Left){draggingSoundboardVolume=false;draggingSoundboardIndex=-1;}}

    private string? GetSoundboardSound(string phrase) => soundboardSounds.TryGetValue(phrase, out var path) ? path : null;
    private double GetSoundboardVolume(string phrase) => soundboardVolumes.TryGetValue(phrase, out var value) ? Math.Clamp(value,0,1.5) : 1.0;

    private void StartSoundboardListen()
    {
        if (engine == null || detector == null || !detector.IsReady)
        {
            MessageBox.Show(this, "Start VoiceGuard first so Whisper is ready.", "VoiceGuard");
            return;
        }

        if (soundboardSounds.Count == 0)
        {
            MessageBox.Show(this, "Add at least one soundboard phrase first.", "VoiceGuard");
            return;
        }

        if (soundboardListenInProgress) return;

        soundboardListenInProgress = true;
        listenSoundboard.Enabled = false;
        SetStatus("LISTENING — say your soundboard phrase...");
        SetMode("SOUNDBOARD LISTEN");

        double start = engine.CapturePcmSeconds;
        if (!engine.SetSoundboardListening(true))
        {
            soundboardListenInProgress = false;
            listenSoundboard.Enabled = true;
            SetMode("LIVE");
            SetStatus("Unable to enter soundboard listening mode.");
            return;
        }
        AddLog("SOUNDBOARD LISTEN CAPTURE STARTED");
        detector.StartSoundboardListen(start, 2.5);
    }

    private void BeginSoundboardListenFromKey()
    {
        if (engine == null || detector == null || !detector.IsReady || soundboardListenInProgress) return;
        soundboardListenInProgress = true;
        listenSoundboard.Enabled = false;
        SetMode("SOUNDBOARD LISTENING");
        SetStatus("Listening for soundboard phrase...");
        // The keyboard-triggered path must explicitly put AudioEngine into
        // soundboard-listening mode. Without this, Capture_DataAvailable keeps
        // treating the microphone as normal live passthrough and the dedicated
        // phrase buffer never receives any audio.
        if (!engine.SetSoundboardListening(true))
        {
            soundboardListenInProgress = false;
            listenSoundboard.Enabled = true;
            SetMode("LIVE");
            SetStatus("Unable to enter soundboard listening mode.");
            return;
        }
        AddLog("SOUNDBOARD LISTEN TRIGGERED");
        AddLog("SOUNDBOARD LISTEN CAPTURE STARTED");
        detector.StartSoundboardListen(engine.CapturePcmSeconds, 2.5);
    }

    private void TriggerSoundboardFromListen(string phrase)
    {
        var currentEngine = engine;
        if (currentEngine == null) return;

        currentEngine.SetSoundboardListening(false);
        detector?.StopSoundboardListen();
        soundboardListenInProgress = false;

        try
        {
            var key = ptt.Tag is Keys k ? k : Keys.Z;

            // Drive VoiceGuard's own PTT state directly as well as injecting the
            // physical key for the game. This makes the soundboard path independent
            // of whether the low-level keyboard hook receives injected events.
            currentEngine.SetPtt(true);
            PttKeyInjector.KeyDown(key);

            double duration = currentEngine.TriggerSoundboardNow(phrase);
            if (duration <= 0.0)
            {
                currentEngine.SetPtt(false);
                PttKeyInjector.KeyUp(key);
                listenSoundboard.Enabled = true;
                SetMode("LIVE");
                SetStatus("Soundboard trigger failed — no playable clip was found.");
                return;
            }

            // Keep the real game PTT key held until the delayed soundboard clip
            // has actually reached the game. The extra 350 ms gives the output
            // device a small safety margin before releasing the game PTT.
            double holdSeconds = Math.Max(0.5, currentEngine.DelaySeconds + duration + 0.35);
            AddLog($"SOUNDBOARD PTT INJECTED — key={key} | hold={holdSeconds:0.000}s | phrase=\"{phrase}\"");
            SetStatus($"SOUNDBOARD TRANSMITTING — {phrase}");
            SetMode("SOUNDBOARD TRANSMIT");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
                }
                finally
                {
                    try { currentEngine.SetPtt(false); } catch { }
                    try { PttKeyInjector.KeyUp(key); } catch { }
                    if (!IsDisposed)
                    {
                        BeginInvoke(() =>
                        {
                            listenSoundboard.Enabled = true;
                            SetMode("LIVE");
                            SetStatus("READY — PTT-gated delayed output is active.");
                        });
                    }
                }
            });
        }
        catch (Exception ex)
        {
            try { PttKeyInjector.KeyUp(ptt.Tag is Keys k ? k : Keys.Z); } catch { }
            listenSoundboard.Enabled = true;
            SetMode("LIVE");
            SetStatus("Soundboard trigger failed.");
            AddLog($"SOUNDBOARD PTT INJECTION ERROR — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void AddSoundboardPhrase()
    {
        string? phrase = PromptForText("Add soundboard phrase", "Phrase to trigger:");
        if (string.IsNullOrWhiteSpace(phrase)) return;
        phrase = phrase.Trim();
        if (soundboardSounds.ContainsKey(phrase)) { MessageBox.Show(this, "That soundboard phrase already exists.", "VoiceGuard"); return; }
        ChooseAndEditSoundboard(phrase, null);
    }

    private void EditSelectedSoundboard()
    {
        if (soundboard.SelectedItem is not object item) return;
        string oldPhrase = item.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(oldPhrase) || !soundboardSounds.TryGetValue(oldPhrase, out var oldPath)) return;
        string? phrase = PromptForText("Edit soundboard phrase", "Phrase to trigger:");
        if (string.IsNullOrWhiteSpace(phrase)) return;
        phrase = phrase.Trim();
        if (!string.Equals(phrase, oldPhrase, StringComparison.OrdinalIgnoreCase) && soundboardSounds.ContainsKey(phrase))
        {
            MessageBox.Show(this, "That soundboard phrase already exists.", "VoiceGuard");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"Choose soundboard audio — {phrase}",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = oldPath
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        using var editor = new SoundboardEditorForm(dialog.FileName, AddLog);
        if (editor.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(editor.SavedClipPath)) return;

        double oldVolume = GetSoundboardVolume(oldPhrase);
        soundboardAliases.TryGetValue(oldPhrase, out var oldAliases);
        soundboardSounds.Remove(oldPhrase);
        soundboardVolumes.Remove(oldPhrase);
        soundboardSounds[phrase] = editor.SavedClipPath;
        soundboardVolumes[phrase] = oldVolume;
        soundboardAliases.Remove(oldPhrase);
        soundboardAliases[phrase] = oldAliases ?? new List<string>();
        int oldIndex = soundboard.Items.IndexOf(item);
        soundboard.Items[oldIndex] = phrase;
        soundboard.SelectedIndex = oldIndex;
        SyncSoundboardAliases();
        SavePersistence();
        soundboard.Invalidate();
        SetStatus("Soundboard clip updated.");
    }

    private void ChooseAndEditSoundboard(string phrase, string? existingPath)
    {
        string sourcePath;
        using (var dialog = new OpenFileDialog
        {
            Title = $"Choose soundboard audio — {phrase}",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = existingPath ?? ""
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            sourcePath = dialog.FileName;
        }

        using var editor = new SoundboardEditorForm(sourcePath, AddLog);
        if (editor.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(editor.SavedClipPath)) return;

        soundboardSounds[phrase] = editor.SavedClipPath;
        if (!soundboardVolumes.ContainsKey(phrase)) soundboardVolumes[phrase] = 1.0;
        if (!soundboard.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), phrase, StringComparison.OrdinalIgnoreCase)))
            soundboard.Items.Add(phrase);
        soundboard.SelectedIndex = soundboard.Items.Count - 1;
        soundboard.Invalidate();
        SyncSoundboardAliases();
        SavePersistence();
        SetStatus("Soundboard clip ready.");
    }

    private string? GetSelectedSoundboardPhrase()
    {
        return soundboard.SelectedItem?.ToString();
    }

    private void AddAliasToSelectedSoundboard()
    {
        string? phrase = GetSelectedSoundboardPhrase();
        if (string.IsNullOrWhiteSpace(phrase)) return;
        string? alias = PromptForText($"Add soundboard alias — {phrase}", "Alias:");
        if (string.IsNullOrWhiteSpace(alias)) return;
        alias = alias.Trim();
        if (string.Equals(alias, phrase, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var pair in soundboardAliases)
            if (pair.Value.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)) || string.Equals(pair.Key, alias, StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "That alias is already assigned.", "VoiceGuard"); return; }
        if (!soundboardAliases.TryGetValue(phrase, out var list)) soundboardAliases[phrase] = list = new List<string>();
        list.Add(alias);
        SyncSoundboardAliases();
        SavePersistence();
        soundboard.Invalidate();
    }

    private void ManageAliasesForSelectedSoundboard()
    {
        string? phrase = GetSelectedSoundboardPhrase();
        if (string.IsNullOrWhiteSpace(phrase)) return;
        if (!soundboardAliases.TryGetValue(phrase, out var stored)) soundboardAliases[phrase] = stored = new List<string>();
        using var form = new Form { Text = $"Aliases — {phrase}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(430, 320), BackColor = Bg, ForeColor = TextMain, Font = Font, MinimizeBox = false, MaximizeBox = false };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
        foreach (var a in stored) list.Items.Add(a);
        var add = new Button { Text = "Add", Width = 80, Height = 30 }; StyleButton(add, false);
        var remove = new Button { Text = "Remove", Width = 80, Height = 30 }; StyleButton(remove, false);
        var ok = new Button { Text = "Save", Width = 90, Height = 30, DialogResult = DialogResult.OK }; StyleButton(ok, true);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, BackColor = Bg, FlowDirection = FlowDirection.LeftToRight };
        bottom.Controls.Add(add); bottom.Controls.Add(remove); bottom.Controls.Add(ok);
        form.Controls.Add(list); form.Controls.Add(bottom);
        add.Click += (_, _) => { var a = PromptForText("Add alias", "Alias:"); if (!string.IsNullOrWhiteSpace(a)) list.Items.Add(a.Trim()); };
        remove.Click += (_, _) => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };
        if (form.ShowDialog(this) != DialogResult.OK) return;
        soundboardAliases[phrase] = list.Items.Cast<object>().Select(x => x?.ToString()?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SyncSoundboardAliases(); SavePersistence(); soundboard.Invalidate();
    }

    private Dictionary<string,string> BuildSoundboardAliasMap()
    {
        var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in soundboardAliases)
            foreach (var alias in pair.Value)
                if (!string.IsNullOrWhiteSpace(alias)) map[alias] = pair.Key;
        return map;
    }

    private void SyncSoundboardAliases()
    {
        detector?.SetSoundboardPhrases(soundboardSounds.Keys);
        detector?.SetSoundboardAliases(BuildSoundboardAliasMap());
    }

    private void RemoveSelectedSoundboard()
    {
        if (soundboard.SelectedItem is not object item) return;
        string phrase = item.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(phrase)) return;
        soundboard.Items.Remove(item);
        soundboardSounds.Remove(phrase);
        soundboardVolumes.Remove(phrase);
        soundboardAliases.Remove(phrase);
        SyncSoundboardAliases();
        SavePersistence();
    }

    private async void TestSoundboardSound(string phrase)
    {
        var path=GetSoundboardSound(phrase); if(string.IsNullOrWhiteSpace(path)||!File.Exists(path)){MessageBox.Show(this,"No soundboard clip is assigned.","VoiceGuard");return;}
        try { SetStatus("Testing soundboard clip..."); double volume=Math.Clamp(GetSoundboardVolume(phrase)*(outputVolume.Value/100.0),0,1.5); await Task.Run(()=>PlayReplacementOnDefaultDevice(path,volume)); SetStatus("Soundboard test complete."); } catch(Exception ex){AddLog($"SOUNDBOARD TEST ERROR — {Path.GetFileName(path)} — {ex.Message}");}
    }

    private void AddBlockedWord()
    {
        string? value = PromptForText("Add blocked word", "Word:");
        if (string.IsNullOrWhiteSpace(value)) return;
        value = value.Trim();

        if (!words.Items.Cast<object>().Any(x =>
            string.Equals(x?.ToString(), value, StringComparison.OrdinalIgnoreCase)))
        {
            words.Items.Add(value);
            blockedWordAliases.TryAdd(value, new List<string>());
            words.SelectedIndex = words.Items.Count - 1;
            words.Invalidate();
            SyncAliasesToDetector();
            SavePersistence();
        }
    }

    private void RemoveBlockedWord()
    {
        if (words.SelectedIndex >= 0)
        {
            string word = words.SelectedItem?.ToString() ?? "";
            words.Items.RemoveAt(words.SelectedIndex);
            blockedWordAliases.Remove(word);
            replacementSounds.Remove(word);
            replacementPlaybackSettings.Remove(word);
            replacementVolumes.Remove(word);
            SyncAliasesToDetector();
            words.Invalidate();
            SavePersistence();
        }
    }

    private async void SetReplacementSoundForSelectedWord()
    {
        if (words.SelectedItem is null) return;
        string word = words.SelectedItem.ToString() ?? "";

        using var dialog = new OpenFileDialog
        {
            Title = $"Choose replacement sound — {word}",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg|WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string sourcePath = dialog.FileName;
        string selectedWord = word;

        try
        {
            SetStatus("Preparing replacement sound...");
            string importedPath = await Task.Run(() => ReplacementSoundImporter.Import(sourcePath, AddLog));

            if (IsDisposed) return;

            replacementSounds[selectedWord] = importedPath;
            words.Invalidate();
            SavePersistence();
            SetStatus("Replacement sound ready.");
        }
        catch (Exception ex)
        {
            AddLog($"REPLACEMENT ERROR: {Path.GetFileName(sourcePath)} — {ex.Message}");
            SetStatus("Replacement sound could not be loaded.");
        }
    }

    private void ClearReplacementSoundForSelectedWord()
    {
        if (words.SelectedItem is null) return;
        string word = words.SelectedItem.ToString() ?? "";
        if (replacementSounds.Remove(word))
        {
            words.Invalidate();
            SavePersistence();
            AddLog($"Replacement sound cleared: \"{word}\"");
        }
    }

    private string? GetReplacementSound(string word)
    {
        return replacementSounds.TryGetValue(word, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : null;
    }

    private double GetReplacementVolume(string word)
    {
        return replacementVolumes.TryGetValue(word, out var volume) ? Math.Clamp(volume, 0.0, 1.5) : 1.0;
    }

    private async void TestReplacementSound(string word)
    {
        var path = GetReplacementSound(word);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "No replacement sound is assigned to this word.", "VoiceGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            double volume = Math.Clamp(GetReplacementVolume(word) * (outputVolume.Value / 100.0), 0.0, 1.5);
            await Task.Run(() => PlayReplacementOnDefaultDevice(path, volume));
        }
        catch (Exception ex)
        {
            AddLog($"REPLACEMENT TEST ERROR: {Path.GetFileName(path)} — {ex.Message}");
        }
    }

    private sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly float gain;
        public GainSampleProvider(ISampleProvider source, double gain)
        {
            this.source = source;
            this.gain = (float)Math.Clamp(gain, 0.0, 1.5);
        }
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (Math.Abs(gain - 1f) < 0.0001f) return read;
            if (gain <= 1f)
            {
                for (int i = offset; i < offset + read; i++) buffer[i] *= gain;
                return read;
            }
            double denominator = Math.Tanh(gain);
            for (int i = offset; i < offset + read; i++)
                buffer[i] = (float)(Math.Tanh(buffer[i] * gain) / denominator);
            return read;
        }
    }

    private static void PlayReplacementOnDefaultDevice(string path, double volume)
    {
        using var reader = new AudioFileReader(path);
        var volumeProvider = new GainSampleProvider(reader, Math.Clamp(volume, 0.0, 1.5));
        using var player = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 80, NumberOfBuffers = 3 };
        player.Init(volumeProvider);
        player.Play();
        while (player.PlaybackState == PlaybackState.Playing)
            System.Threading.Thread.Sleep(20);
    }

    private ReplacementPlaybackSettings GetReplacementPlaybackSettings(string word)
    {
        return replacementPlaybackSettings.TryGetValue(word, out var settings)
            ? settings
            : new ReplacementPlaybackSettings(true, 1.0);
    }

    private void ConfigureReplacementPlaybackForSelectedWord()
    {
        if (words.SelectedItem is null) return;
        string word = words.SelectedItem.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(word)) return;

        var current = GetReplacementPlaybackSettings(word);

        using var dialog = new Form
        {
            Text = $"Replacement playback — {word}",
            Width = 390,
            Height = 205,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Bg,
            ForeColor = TextMain,
            Font = Font
        };

        var match = new CheckBox
        {
            Text = "Limit playback to detected word length",
            Checked = current.MatchWordLength,
            AutoSize = true,
            Location = new Point(18, 18),
            ForeColor = TextMain,
            BackColor = Color.Transparent
        };

        var label = new Label
        {
            Text = "Custom playback length (0.1–5.0 seconds):",
            AutoSize = true,
            Location = new Point(18, 58),
            ForeColor = TextDim
        };

        var duration = new NumericUpDown
        {
            Minimum = 0.1M,
            Maximum = 5.0M,
            Increment = 0.1M,
            DecimalPlaces = 1,
            Value = (decimal)Math.Clamp(current.DurationSeconds, 0.1, 5.0),
            Location = new Point(21, 82),
            Width = 90,
            BackColor = Surface2,
            ForeColor = TextMain
        };
        duration.Enabled = !match.Checked;
        match.CheckedChanged += (_, _) => duration.Enabled = !match.Checked;

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90, Height = 30, Location = new Point(185, 120) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30, Location = new Point(283, 120) };
        dialog.Controls.AddRange(new Control[] { match, label, duration, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        replacementPlaybackSettings[word] = new ReplacementPlaybackSettings(
            match.Checked,
            Math.Clamp((double)duration.Value, 0.1, 5.0));
        words.Invalidate();
        SavePersistence();
        AddLog(match.Checked
            ? $"REPLACEMENT PLAYBACK: {word} — detected word length"
            : $"REPLACEMENT PLAYBACK: {word} — {duration.Value:0.0}s");
    }

    private void AddAliasToSelectedWord()
    {
        if (words.SelectedItem is null)
            return;

        string word = words.SelectedItem.ToString() ?? "";
        string? alias = PromptForText(
            $"Add alias — {word}",
            "Whisper may hear:");

        if (string.IsNullOrWhiteSpace(alias))
            return;

        alias = alias.Trim();

        if (!blockedWordAliases.TryGetValue(word, out var list))
        {
            list = new List<string>();
            blockedWordAliases[word] = list;
        }

        if (!list.Any(x => string.Equals(x, alias, StringComparison.OrdinalIgnoreCase)))
            list.Add(alias);

        SyncAliasesToDetector();
        SavePersistence();

        AddLog($"Alias added: \"{alias}\" → \"{word}\"");
    }

    private void ManageAliasesForSelectedWord()
    {
        if (words.SelectedItem is null)
            return;

        string word = words.SelectedItem.ToString() ?? "";

        if (!blockedWordAliases.TryGetValue(word, out var stored))
        {
            stored = new List<string>();
            blockedWordAliases[word] = stored;
        }

        var working = new List<string>(stored);

        using var dialog = new Form
        {
            Text = $"Aliases — {word}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(400, 280),
            MinimizeBox = false,
            MaximizeBox = false
        };

        var list = new ListBox
        {
            Location = new Point(15, 15),
            Width = 370,
            Height = 170
        };

        foreach (var alias in working)
            list.Items.Add(alias);

        var add = new Button
        {
            Text = "Add",
            Location = new Point(15, 200),
            Width = 80
        };

        var remove = new Button
        {
            Text = "Remove",
            Location = new Point(105, 200),
            Width = 80
        };

        var close = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(310, 235),
            Width = 75
        };

        add.Click += (_, _) =>
        {
            string? alias = PromptForText(
                $"Add alias — {word}",
                "Whisper may hear:");

            if (string.IsNullOrWhiteSpace(alias))
                return;

            alias = alias.Trim();

            if (!list.Items.Cast<object>().Any(x =>
                string.Equals(x?.ToString(), alias,
                    StringComparison.OrdinalIgnoreCase)))
            {
                list.Items.Add(alias);
            }
        };

        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
                list.Items.RemoveAt(list.SelectedIndex);
        };

        close.Click += (_, _) =>
        {
            working.Clear();

            foreach (var item in list.Items)
            {
                string alias = item?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(alias))
                    working.Add(alias);
            }

            blockedWordAliases[word] = working;
            SyncAliasesToDetector();
            SavePersistence();

            AddLog(
                $"Aliases saved for \"{word}\": " +
                $"{(working.Count == 0 ? "none" : string.Join(", ", working))}");
        };

        dialog.Controls.AddRange(new Control[] { list, add, remove, close });
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private void SyncAliasesToDetector()
    {
        if (detector == null)
            return;

        detector.ClearBlockedWordAliases();

        foreach (var pair in blockedWordAliases)
        {
            foreach (var alias in pair.Value)
                detector.AddBlockedWordAlias(pair.Key, alias);
        }
    }

    private static string? PromptForText(string title, string labelText)
    {
        using var dialog = new Form {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(360, 125),
            MinimizeBox = false,
            MaximizeBox = false
        };

        var label = new Label { Text = labelText, AutoSize = true, Location = new Point(15, 15) };
        var box = new TextBox { Location = new Point(15, 40), Width = 330 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(180, 70), Width = 70 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(270, 70), Width = 70 };
        dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    private async Task ToggleEngineAsync()
    {
        if (engine != null)
        {
            StopEngine();
            return;
        }

        await StartEngineAsync();
    }

    private async Task StartEngineAsync()
    {
        if (input.SelectedItem is not AudioDeviceInfo inDev ||
            output.SelectedItem is not AudioDeviceInfo outDev)
        {
            MessageBox.Show("Select your physical microphone and VB-CABLE CABLE Input.");
            return;
        }

        try
        {
            start.Enabled = false;
            AddLog("WHISPER STARTING: checking model...");
            await PrepareModelAsync();
        }
        catch
        {
            start.Enabled = true;
            return;
        }

        if (detector == null || !detector.IsReady)
        {
            start.Enabled = true;
            return;
        }

        detector.SetWords(GetWords());
        SyncAliasesToDetector();
        detector.SetSoundboardPhrases(soundboardSounds.Keys);
            detector.SetSoundboardAliases(BuildSoundboardAliasMap());
        detector.Reset();

        if (!outDev.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
        {
            if (MessageBox.Show(
                "The selected output is not named CABLE Input.\r\nContinue anyway?",
                "Routing warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }

        var key = ptt.Tag is Keys k ? k : Keys.F13;

        try
        {
            // Keep a stable engine reference inside the asynchronous censor callback.
            // The detector can deliver callbacks from a worker thread after the UI
            // field has been replaced/stopped; capturing the instance prevents a
            // valid censor event from being lost because the field is temporarily null.
            var newEngine = new AudioEngine(
                inDev.DeviceNumber,
                outDev.DeviceNumber,
                (double)delay.Value,
                SetStatus,
                (pcm, count, absoluteStartSeconds) =>
                {
                    if (detector.IsSoundboardListening)
                        detector.AddSoundboardListenPcm(pcm, count, absoluteStartSeconds);
                    else
                        detector.AddPcm48k(pcm, count, absoluteStartSeconds);
                },
                AddLog,
                seconds => detector.BeginPttSegment(seconds),
                seconds => detector.EndPttSegment(seconds),
                () => detector.CompletedThroughSeconds,
                () => detector.HasPendingAnalysis,
                () => detector.AnalysisSafeThroughSeconds,
                GetReplacementSound,
                GetReplacementPlaybackSettings,
                GetReplacementVolume,
                () => outputVolume.Value / 100.0,
                GetSoundboardSound,
                GetSoundboardVolume);

            detector.SetOutputCursorProvider(() => newEngine.CurrentSourceSeconds);

            detector.SetCensorCallback(
                (startSeconds, endSeconds, word) =>
                {
                    AddLog($"CENSOR CALLBACK RECEIVED — {word} {startSeconds:0.000}s→{endSeconds:0.000}s");
                    try
                    {
                        newEngine.AddCensorRegion(startSeconds, endSeconds, word, GetReplacementSound(word), GetReplacementPlaybackSettings(word));
                    }
                    catch (ObjectDisposedException)
                    {
                        AddLog($"CENSOR CALLBACK IGNORED — engine disposed — {word}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"CENSOR CALLBACK ERROR — {ex.GetType().Name}: {ex.Message}");
                    }
                });

            detector.SetSoundboardCallback(
                (startSeconds, endSeconds, phrase) =>
                {
                    string? soundPath = GetSoundboardSound(phrase);
                    if (string.IsNullOrWhiteSpace(soundPath)) return;
                    AddLog($"SOUNDBOARD CALLBACK RECEIVED — {phrase} {startSeconds:0.000}s→{endSeconds:0.000}s");
                    try { newEngine.AddSoundboardRegion(startSeconds, endSeconds, phrase, soundPath); }
                    catch (ObjectDisposedException) { AddLog($"SOUNDBOARD CALLBACK IGNORED — engine disposed — {phrase}"); }
                    catch (Exception ex) { AddLog($"SOUNDBOARD CALLBACK ERROR — {ex.GetType().Name}: {ex.Message}"); }
                });

            detector.SetSoundboardListenCallback(TriggerSoundboardFromListen);
            detector.SetSoundboardListenNoMatchCallback(() =>
            {
                // Detector and AudioEngine have separate listener flags. Always
                // clear BOTH so a no-match cannot leave the microphone feeding
                // the listener path forever or block subsequent triggers.
                newEngine.SetSoundboardListening(false);
                soundboardListenInProgress = false;
                if (!IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        listenSoundboard.Enabled = true;
                        SetMode("LIVE");
                        SetStatus("No configured soundboard phrase was recognized.");
                    });
                }
            });

            engine = newEngine;
            AddLog("Censor callback attached.");

            newEngine.Start();

            AddLog($"PTT HOOK STARTING — key={key}");
            hook = new PttKeyHook(key, down =>
            {
                AddLog($"PTT {(down ? "DOWN" : "UP")} — key={key}");
                engine?.SetPtt(down);
                // Do not reset the detector on PTT release. AudioEngine now
                // supplies the global capture timestamp whenever a PTT segment
                // begins; resetting here would send Whisper timestamps back to
                // zero and break censor-region alignment.
                SetMode(down ? "DELAY / ANALYZING" : "DELAYED / DRAINING");
                // Queued Whisper work is intentionally preserved.
            });

            hook.Start();

            var listenKey = soundboardListenKey.Tag is Keys lk ? lk : Keys.OemPeriod;
            soundboardListenVirtualKey = (int)listenKey;
            AddLog($"SOUNDBOARD LISTEN KEY MONITOR STARTING — key={listenKey}");
            soundboardListenKeyCts?.Cancel();
            soundboardListenKeyCts?.Dispose();
            soundboardListenKeyCts = new CancellationTokenSource();
            var listenToken = soundboardListenKeyCts.Token;
            Interlocked.Exchange(ref soundboardListenKeyDown, 0);
            soundboardListenKeyThread = new Thread(() =>
            {
                while (!listenToken.IsCancellationRequested)
                {
                    int vk = Volatile.Read(ref soundboardListenVirtualKey);
                    bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                    int wasDown = Volatile.Read(ref soundboardListenKeyDown);
                    if (down)
                    {
                        if (wasDown == 0 && Interlocked.Exchange(ref soundboardListenKeyDown, 1) == 0)
                        {
                            try
                            {
                                if (!IsDisposed) BeginInvoke(BeginSoundboardListenFromKey);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref soundboardListenKeyDown, 0);
                    }
                    Thread.Sleep(20);
                }
            })
            { IsBackground = true, Name = "VoiceGuard Soundboard Listen Key" };
            soundboardListenKeyThread.Start();

            start.Text = "Stop VoiceGuard";
            start.Enabled = true;
            AddLog("VoiceGuard started.");
            SetMode("LIVE");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.WriteException("STARTUP ERROR", ex);
            StopEngine();
            MessageBox.Show(ex.ToString(), "VoiceGuard startup error");
        }
    }

    private void AddLog(string text)
    {
        DiagnosticLogger.Write("APP", text);
        if (IsDisposed) return;

        // Keep the on-screen log human-readable. The detector produces many
        // internal timing/queue/audio diagnostics that are useful while
        // developing VoiceGuard but are not useful to a normal user.
        //
        // HEARD    = exactly what Whisper transcribed
        // FILTERED = a blocked word was successfully scheduled
        // MISSED   = a blocked word was detected too late to censor
        //
        // Keeping the raw Whisper phrase makes it easy to discover
        // transcription mistakes that may need an alias.
        string? userLog = null;

        if (text.StartsWith("HEARD: ", StringComparison.OrdinalIgnoreCase))
        {
            // SpeechDetector emits this marker directly from the raw Whisper
            // result. Do not alter it: this is the text users need when
            // creating transcription aliases for misheard words.
            userLog = text.Trim();
        }
        else if (text.StartsWith("Speech recognition READY", StringComparison.OrdinalIgnoreCase))
        {
            userLog = "WHISPER READY: " + text["Speech recognition READY".Length..].Trim().TrimStart('—', '-').Trim();
        }
        else if (text.StartsWith("WHISPER STARTING:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("WHISPER RUNTIME:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("WHISPER ACCELERATION:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("Whisper warm-up starting", StringComparison.OrdinalIgnoreCase))
        {
            userLog = "WHISPER STARTING: warm-up...";
        }
        else if (text.StartsWith("Whisper warm-up complete", StringComparison.OrdinalIgnoreCase))
        {
            userLog = "WHISPER READY: warm-up complete.";
        }
        else if (text.StartsWith("CENSOR MISSED — ", StringComparison.OrdinalIgnoreCase))
        {
            userLog = "MISSED: " + text["CENSOR MISSED — ".Length..].Trim();
        }
        else if (text.StartsWith("CONVERTING:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("TRIMMING:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("READY:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("REPLACEMENT ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            userLog = text.Trim();
        }
        else if (text.StartsWith("CENSOR SCHEDULED — ", StringComparison.OrdinalIgnoreCase))
        {
            string details = text["CENSOR SCHEDULED — ".Length..].Trim();
            int separator = details.IndexOf(" PCM=", StringComparison.Ordinal);
            userLog = "FILTERED: " + (separator >= 0 ? details[..separator].Trim() : details);
        }

        if (userLog == null)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(() => AddLog(userLog));
            return;
        }

        log.AppendText($"[{DateTime.Now:HH:mm:ss}] {userLog}{Environment.NewLine}");
    }

    private void SetMode(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetMode(text));
            return;
        }
        mode.Text = "MODE: " + text;
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text));
            return;
        }
        status.Text = text;
    }

    private void StopEngine()
    {
        soundboardListenInProgress = false;
        listenSoundboard.Enabled = true;
        try { PttKeyInjector.KeyUp(ptt.Tag is Keys k ? k : Keys.Z); } catch { }
        detector?.StopSoundboardListen();
        hook?.Dispose();
        hook = null;
        soundboardListenKeyCts?.Cancel();
        soundboardListenKeyCts?.Dispose();
        soundboardListenKeyCts = null;
        soundboardListenKeyThread = null;
        Interlocked.Exchange(ref soundboardListenKeyDown, 0);

        engine?.Stop();
        engine?.Dispose();
        engine = null;

        detector?.Reset();

        start.Text = "Start VoiceGuard";
        start.Enabled = true;
        SetMode("STOPPED");
        SetStatus("Stopped.");
    }
}
