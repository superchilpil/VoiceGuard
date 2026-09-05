using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
    private readonly TextBox log = new();
    private readonly Label mode = new();
    private readonly Label status = new();
    private readonly Button start = new();

    private AudioEngine? engine;
    private PttKeyHook? hook;
    private SpeechDetector? detector;
    private bool loadingPersistence;
    private string? loadedInputDeviceName;
    private string? loadedOutputDeviceName;

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
        public decimal DelaySeconds { get; set; } = 3M;
        public string PttKey { get; set; } = Keys.Z.ToString();
        public string? InputDevice { get; set; }
        public string? OutputDevice { get; set; }
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

        Text = "VoiceGuard — Stage 6.5.5";
        Width = 1180;
        Height = 720;
        MinimumSize = new Size(1000, 620);
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
            Text = "VOICE CHAT PROFANITY FILTER  •  LOCAL PROCESSING",
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

        // The three-column working area is confined to row 2 of root.
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 14, 18, 18),
            Margin = new Padding(0),
            BackColor = Bg
        };
        // Fixed semantic layout:
        //   0 = controls
        //   1 = blocked words
        //   2 = logs (RIGHT)
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        root.Controls.Add(main, 0, 1);

        // LEFT: controls are explicitly arranged top-to-bottom in the
        // requested order instead of relying on DockStyle.Top z-order.
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 12,
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
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // model heading
                left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); // start/stop
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // settings heading
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // delay
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // ptt
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // mode
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); // status
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // branding
        main.Controls.Add(left, 0, 0);

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

        left.Controls.Add(MakeSectionTitle("MODEL & CONTROL"), 0, 4);

        start.Text = "Start VoiceGuard";
        StyleButton(start, true);
        start.Dock = DockStyle.Fill;
        start.Margin = new Padding(0, 2, 0, 6);
        start.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        start.Click += async (_, _) => await ToggleEngineAsync();
        left.Controls.Add(start, 0, 5);

        left.Controls.Add(MakeSectionTitle("SETTINGS"), 0, 6);

        var delayPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        delayPanel.Controls.Add(MakeFieldLabel("Delay"));
        delay.Dock = DockStyle.Bottom;
        delay.Height = 28;
        delay.Minimum = 2;
        delay.Maximum = 5;
        delay.DecimalPlaces = 1;
        StyleInput(delay);
        delay.Increment = 0.5M;
        delay.Value = 3;
        delayPanel.Controls.Add(delay);
        left.Controls.Add(delayPanel, 0, 7);

        var pttPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        pttPanel.Controls.Add(MakeFieldLabel("PTT key"));
        ptt.Dock = DockStyle.Bottom;
        ptt.Height = 28;
        ptt.Text = "Z";
        StyleInput(ptt);
        ptt.Tag = Keys.Z;
        ptt.ReadOnly = true;
        ptt.KeyDown += (_, e) =>
        {
            ptt.Text = e.KeyCode.ToString();
            ptt.Tag = e.KeyCode;
            e.SuppressKeyPress = true;
        };
        pttPanel.Controls.Add(ptt);
        left.Controls.Add(pttPanel, 0, 8);

        // A compact status area lives below the fixed controls if there is
        // room; it does not participate in the three primary control order.
        status.Text = "Whisper loads automatically when VoiceGuard starts.";
        status.AutoSize = false;
        status.Dock = DockStyle.Bottom;
        status.Height = 44;
        status.ForeColor = TextDim;
        mode.Text = "MODE: STOPPED";
        mode.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        mode.AutoSize = false;
        mode.Dock = DockStyle.Fill;
        mode.Height = 30;
        mode.Margin = new Padding(0);
        left.Controls.Add(mode, 0, 9);
        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0);
        left.Controls.Add(status, 0, 10);

        var jackBrand = new Label
        {
            Text = "Jack The Gooner",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(190, 120, 255),
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 0, 4),
            Margin = new Padding(0)
        };
        left.Controls.Add(jackBrand, 0, 11);

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
        words.ItemHeight = 32;
        words.IntegralHeight = false;
        words.HorizontalScrollbar = true;
        words.ScrollAlwaysVisible = false;
        words.DrawItem += DrawBlockedWordItem;
        words.MouseUp += WordsMouseUp;
        wordPanel.Controls.Add(words, 0, 1);

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
        wordsMenu.Items.Add(new ToolStripSeparator());
        wordsMenu.Items.Add("Remove word", null, (_, _) => RemoveBlockedWord());
        words.ContextMenuStrip = wordsMenu;

        // RIGHT COLUMN ONLY:
        // Column 0 = controls
        // Column 1 = blocked words
        // Column 2 = LOGS
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
        main.Controls.Add(logSection, 2, 0);

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

        Load += (_, _) =>
        {
            LoadPersistence();
            LoadDevices();
        };
        FormClosing += (_, _) =>
        {
            SavePersistence();
            StopEngine();
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
                return;

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<VoiceGuardConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config == null)
                return;

            words.Items.Clear();
            blockedWordAliases.Clear();
            replacementSounds.Clear();

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

            if (config.DelaySeconds >= delay.Minimum && config.DelaySeconds <= delay.Maximum)
                delay.Value = config.DelaySeconds;

            if (Enum.TryParse<Keys>(config.PttKey, true, out var savedKey))
            {
                ptt.Tag = savedKey;
                ptt.Text = savedKey.ToString();
            }

            loadedInputDeviceName = config.InputDevice;
            loadedOutputDeviceName = config.OutputDevice;

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
                DelaySeconds = delay.Value,
                PttKey = (ptt.Tag is Keys key ? key : Keys.Z).ToString(),
                InputDevice = input.SelectedItem is AudioDeviceInfo inputDevice ? inputDevice.Name : null,
                OutputDevice = output.SelectedItem is AudioDeviceInfo outputDevice ? outputDevice.Name : null
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

    private void DrawBlockedWordItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();

        if (e.Index >= 0 && e.Index < words.Items.Count)
        {
            string value = words.Items[e.Index]?.ToString() ?? "";
            string displayValue = value;
            if (replacementSounds.TryGetValue(value, out var sound) && !string.IsNullOrWhiteSpace(sound))
                displayValue += "  [SOUND: " + System.IO.Path.GetFileName(sound) + "]";

            var outer = new Rectangle(
                e.Bounds.Left + 4,
                e.Bounds.Top + 3,
                e.Bounds.Width - 8,
                e.Bounds.Height - 6);

            using var pen = new Pen(Border);
            using var brush = new SolidBrush(
                e.State.HasFlag(DrawItemState.Selected)
                    ? Color.FromArgb(65, 34, 92)
                    : Surface2);

            e.Graphics.FillRectangle(brush, outer);
            e.Graphics.DrawRectangle(pen, outer);

            TextRenderer.DrawText(
                e.Graphics,
                displayValue,
                words.Font,
                new Rectangle(
                    outer.Left + 8,
                    outer.Top,
                    outer.Width - 16,
                    outer.Height),
                e.State.HasFlag(DrawItemState.Selected)
                    ? Color.White
                    : TextMain,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        e.DrawFocusRectangle();
    }

    private void WordsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        int index = words.IndexFromPoint(e.Location);

        // Do not show the menu when the user right-clicks empty space.
        if (index < 0 || index >= words.Items.Count)
            return;

        words.SelectedIndex = index;

        // Explicitly show the menu. This avoids relying on the
        // ListBox/ContextMenuStrip automatic routing, which was unreliable
        // in the previous build.
        wordsMenu.Show(words, e.Location);
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
            SyncAliasesToDetector();
            words.Invalidate();
            SavePersistence();
        }
    }

    private void SetReplacementSoundForSelectedWord()
    {
        if (words.SelectedItem is null) return;
        string word = words.SelectedItem.ToString() ?? "";

        using var dialog = new OpenFileDialog
        {
            Title = $"Choose replacement sound — {word}",
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        replacementSounds[word] = dialog.FileName;
        words.Invalidate();
        SavePersistence();
        AddLog($"Replacement sound set: \"{word}\" → {dialog.FileName}");
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
                    detector.AddPcm48k(pcm, count, absoluteStartSeconds),
                AddLog,
                seconds => detector.BeginPttSegment(seconds),
                seconds => detector.EndPttSegment(seconds),
                () => detector.CompletedThroughSeconds,
                () => detector.HasPendingAnalysis,
                () => detector.AnalysisSafeThroughSeconds,
                GetReplacementSound);

            detector.SetOutputCursorProvider(() => newEngine.CurrentSourceSeconds);

            detector.SetCensorCallback(
                (startSeconds, endSeconds, word) =>
                {
                    AddLog($"CENSOR CALLBACK RECEIVED — {word} {startSeconds:0.000}s→{endSeconds:0.000}s");
                    try
                    {
                        newEngine.AddCensorRegion(startSeconds, endSeconds, word, GetReplacementSound(word));
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

            start.Text = "Stop VoiceGuard";
            start.Enabled = true;
            AddLog("VoiceGuard started.");
            SetMode("LIVE");
        }
        catch (Exception ex)
        {
            StopEngine();
            MessageBox.Show(ex.ToString(), "VoiceGuard startup error");
        }
    }

    private void AddLog(string text)
    {
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
        hook?.Dispose();
        hook = null;

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
