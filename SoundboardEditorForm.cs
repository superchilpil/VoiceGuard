using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

namespace VoiceGuard;

public sealed class SoundboardEditorForm : Form
{
    private readonly string sourcePath;
    private readonly Action<string>? log;
    private readonly WaveformPanel waveform = new();
    private readonly NumericUpDown start = new();
    private readonly NumericUpDown end = new();
    private readonly Label durationLabel = new();
    private readonly Label positionLabel = new();
    private readonly Button play = new();
    private readonly Button playFull = new();
    private readonly Button stop = new();
    private readonly Button save = new();
    private WaveOutEvent? player;
    private MediaFoundationReader? reader;
    private double duration;

    public string? SavedClipPath { get; private set; }

    public SoundboardEditorForm(string sourcePath, Action<string>? log = null)
    {
        this.sourcePath = sourcePath;
        this.log = log;

        Text = $"Soundboard Clip — {Path.GetFileName(sourcePath)}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ClientSize = new Size(1000, 560);
        MinimumSize = new Size(760, 500);
        BackColor = Color.FromArgb(10, 7, 14);
        ForeColor = Color.FromArgb(238, 234, 245);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(root);

        var info = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Drag across the waveform to select a clip. Use the Start/End fields for exact timing.",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ForeColor
        };
        root.Controls.Add(info, 0, 0);

        waveform.Dock = DockStyle.Fill;
        waveform.BackColor = Color.FromArgb(18, 15, 25);
        waveform.TabStop = true;
        waveform.SelectionChanged += (_, _) => SyncTimesFromWaveform();
        root.Controls.Add(waveform, 0, 1);

        var timing = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 2,
            BackColor = BackColor
        };
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        root.Controls.Add(timing, 0, 2);

        timing.Controls.Add(MakeLabel("Start (sec)"), 0, 0);
        timing.Controls.Add(start, 1, 0);
        timing.Controls.Add(MakeLabel("End (sec)"), 2, 0);
        timing.Controls.Add(end, 3, 0);

        foreach (var n in new[] { start, end })
        {
            n.DecimalPlaces = 3;
            n.Increment = 0.100M;
            n.Minimum = 0;
            n.Maximum = 999999;
            n.Dock = DockStyle.Fill;
            n.BackColor = Color.FromArgb(24, 20, 34);
            n.ForeColor = ForeColor;
        }

        var setFull = new Button { Text = "Full", Dock = DockStyle.Fill };
        Style(setFull);
        timing.Controls.Add(setFull, 4, 0);
        setFull.Click += (_, _) =>
        {
            start.Value = 0;
            end.Value = (decimal)duration;
            waveform.SetSelection(0, duration);
        };

        timing.Controls.Add(positionLabel, 5, 0);
        positionLabel.Dock = DockStyle.Fill;
        positionLabel.TextAlign = ContentAlignment.MiddleCenter;
        positionLabel.Text = "Position: 0.000s";

        durationLabel.Dock = DockStyle.Fill;
        durationLabel.TextAlign = ContentAlignment.MiddleLeft;
        timing.Controls.Add(durationLabel, 0, 1);
        timing.SetColumnSpan(durationLabel, 6);

        start.ValueChanged += (_, _) =>
        {
            if ((double)start.Value > (double)end.Value) end.Value = start.Value;
            waveform.SetSelection((double)start.Value, (double)end.Value);
        };
        end.ValueChanged += (_, _) =>
        {
            if ((double)end.Value < (double)start.Value) start.Value = end.Value;
            waveform.SetSelection((double)start.Value, (double)end.Value);
        };

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = BackColor
        };
        for (int i = 0; i < 5; i++)
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        root.Controls.Add(actions, 0, 3);

        play.Text = "▶ Play Selection";
        playFull.Text = "▶ Play Full";
        stop.Text = "■ Stop";
        save.Text = "Use Selection / Save";

        foreach (var b in new[] { play, playFull, stop, save })
            Style(b);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
        Style(cancel);

        actions.Controls.Add(play, 0, 0);
        actions.Controls.Add(playFull, 1, 0);
        actions.Controls.Add(stop, 2, 0);
        actions.Controls.Add(save, 3, 0);
        actions.Controls.Add(cancel, 4, 0);

        play.Click += async (_, _) => await PlaySelectionAsync();
        playFull.Click += async (_, _) => await PlaySelectionAsync(0, duration);
        stop.Click += (_, _) => StopPlayback();
        save.Click += async (_, _) => await SaveSelectionAsync();

        FormClosing += (_, _) => StopPlayback();
        Load += async (_, _) => await LoadAudioAsync();
    }

    private Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = ForeColor
    };

    private static void Style(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(55, 43, 72);
        b.BackColor = Color.FromArgb(24, 20, 34);
        b.ForeColor = Color.FromArgb(238, 234, 245);
        b.Dock = DockStyle.Fill;
    }

    private async Task LoadAudioAsync()
    {
        try
        {
            using var temp = new MediaFoundationReader(sourcePath);
            duration = Math.Max(0, temp.TotalTime.TotalSeconds);

            start.Maximum = (decimal)duration;
            end.Maximum = (decimal)duration;
            start.Value = 0;
            end.Value = (decimal)duration;

            durationLabel.Text =
                $"Source: {duration:0.000}s    |    Selected: {(double)end.Value - (double)start.Value:0.000}s";
            waveform.Duration = duration;
            waveform.SetSelection(0, duration);
            await waveform.LoadPeaksAsync(sourcePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to load audio",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SyncTimesFromWaveform()
    {
        double s = Math.Clamp(waveform.SelectionStart, 0, duration);
        double e = Math.Clamp(waveform.SelectionEnd, 0, duration);
        if (e < s) (s, e) = (e, s);

        start.Value = (decimal)Math.Clamp(s, (double)start.Minimum, (double)start.Maximum);
        end.Value = (decimal)Math.Clamp(e, (double)end.Minimum, (double)end.Maximum);

        durationLabel.Text =
            $"Source: {duration:0.000}s    |    Selected: {Math.Max(0, e - s):0.000}s";
    }

    private async Task PlaySelectionAsync(double? forcedStart = null, double? forcedEnd = null)
    {
        StopPlayback();

        double s = forcedStart ?? (double)start.Value;
        double e = forcedEnd ?? (double)end.Value;
        if (e <= s) return;

        try
        {
            reader = new MediaFoundationReader(sourcePath);
            reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(s, 0, duration));

            var clip = new ClipSampleProvider(
                reader.ToSampleProvider(),
                Math.Max(0, e - s));

            player = new WaveOutEvent { DeviceNumber = -1 };
            player.Init(clip);
            player.PlaybackStopped += (_, _) =>
            {
                try { BeginInvoke(StopPlayback); } catch { }
            };
            player.Play();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Playback error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        await Task.CompletedTask;
    }

    private async Task SaveSelectionAsync()
    {
        StopPlayback();

        double s = (double)start.Value;
        double e = (double)end.Value;
        if (e <= s)
        {
            MessageBox.Show(this, "The end time must be greater than the start time.",
                "Invalid selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            save.Enabled = false;
            SavedClipPath = await Task.Run(() =>
                SoundboardImporter.ExportSelection(sourcePath, s, e, log));

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to save clip",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            save.Enabled = true;
        }
    }

    private void StopPlayback()
    {
        try { player?.Stop(); } catch { }
        player?.Dispose();
        player = null;
        reader?.Dispose();
        reader = null;
    }

    private sealed class ClipSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly long lengthSamples;
        private long position;

        public ClipSampleProvider(ISampleProvider source, double durationSeconds)
        {
            this.source = source;
            lengthSamples = Math.Max(0,
                (long)(durationSeconds *
                       source.WaveFormat.SampleRate *
                       source.WaveFormat.Channels));
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            long remaining = lengthSamples - position;
            if (remaining <= 0) return 0;

            int want = (int)Math.Min(count, remaining);
            int read = source.Read(buffer, offset, want);
            position += read;
            return read;
        }
    }
}

internal sealed class WaveformPanel : Panel
{
    private float[] peaks = Array.Empty<float>();
    private double duration;
    private bool dragging;
    private int dragMode; // 1=start, 2=end, 3=selection
    private double dragAnchorSeconds;
    private double dragOriginalStart;
    private double dragOriginalEnd;

    public double Duration
    {
        get => duration;
        set
        {
            duration = Math.Max(0, value);
            SelectionStart = Math.Clamp(SelectionStart, 0, duration);
            SelectionEnd = Math.Clamp(SelectionEnd, 0, duration);
            Invalidate();
        }
    }

    public double SelectionStart { get; private set; }
    public double SelectionEnd { get; private set; }
    public double PlayheadSeconds { get; private set; }

    public event EventHandler? SelectionChanged;

    public async Task LoadPeaksAsync(string path)
    {
        peaks = await Task.Run(() => BuildPeaks(path, 1800));
        Invalidate();
    }

    public void SetSelection(double start, double end)
    {
        SelectionStart = Math.Clamp(Math.Min(start, end), 0, duration);
        SelectionEnd = Math.Clamp(Math.Max(start, end), 0, duration);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || duration <= 0) return;

        double seconds = XToSeconds(e.X);
        double startX = SecondsToX(SelectionStart);
        double endX = SecondsToX(SelectionEnd);

        if (Math.Abs(e.X - startX) <= 10)
        {
            dragMode = 1;
        }
        else if (Math.Abs(e.X - endX) <= 10)
        {
            dragMode = 2;
        }
        else if (seconds >= SelectionStart && seconds <= SelectionEnd)
        {
            dragMode = 3;
            dragAnchorSeconds = seconds;
            dragOriginalStart = SelectionStart;
            dragOriginalEnd = SelectionEnd;
        }
        else
        {
            dragMode = 2;
            SelectionEnd = seconds;
            if (SelectionEnd < SelectionStart)
                (SelectionStart, SelectionEnd) = (SelectionEnd, SelectionStart);
        }

        dragging = true;
        UpdateSelectionFromMouse(e.X);
        Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || duration <= 0) return;
        UpdateSelectionFromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        dragMode = 0;
    }

    private void UpdateSelectionFromMouse(int x)
    {
        double seconds = XToSeconds(x);

        switch (dragMode)
        {
            case 1:
                SelectionStart = Math.Min(seconds, SelectionEnd);
                break;

            case 2:
                SelectionEnd = Math.Max(seconds, SelectionStart);
                break;

            case 3:
                double delta = seconds - dragAnchorSeconds;
                double length = dragOriginalEnd - dragOriginalStart;
                double ns = Math.Clamp(dragOriginalStart + delta, 0, Math.Max(0, duration - length));
                SelectionStart = ns;
                SelectionEnd = ns + length;
                break;
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);

        using var wavePen = new Pen(Color.FromArgb(154, 78, 255), 1);
        using var selectedBrush = new SolidBrush(Color.FromArgb(45, 154, 78, 255));
        using var markerBrush = new SolidBrush(Color.FromArgb(210, 170, 255));
        using var borderPen = new Pen(Color.FromArgb(55, 43, 72), 1);

        int mid = Height / 2;

        int sx = SecondsToX(SelectionStart);
        int ex = SecondsToX(SelectionEnd);
        if (ex < sx) (sx, ex) = (ex, sx);
        if (ex > sx)
            e.Graphics.FillRectangle(selectedBrush, sx, 0, ex - sx, Height);

        if (peaks.Length > 0)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (int)((long)x * peaks.Length / Math.Max(1, Width));
                float p = peaks[Math.Min(i, peaks.Length - 1)];
                int h = Math.Max(1, (int)(p * (Height / 2 - 14)));
                e.Graphics.DrawLine(wavePen, x, mid - h, x, mid + h);
            }
        }

        using var startPen = new Pen(Color.FromArgb(225, 180, 255), 2);
        using var endPen = new Pen(Color.FromArgb(225, 180, 255), 2);

        e.Graphics.DrawLine(startPen, sx, 0, sx, Height);
        e.Graphics.DrawLine(endPen, ex, 0, ex, Height);

        var startPoints = new[]
        {
            new Point(sx - 7, 0), new Point(sx + 7, 0), new Point(sx, 10)
        };
        var endPoints = new[]
        {
            new Point(ex - 7, 0), new Point(ex + 7, 0), new Point(ex, 10)
        };
        e.Graphics.FillPolygon(markerBrush, startPoints);
        e.Graphics.FillPolygon(markerBrush, endPoints);

        using var textBrush = new SolidBrush(Color.FromArgb(238, 234, 245));
        e.Graphics.DrawString($"START  {SelectionStart:0.000}s",
            Font, textBrush, Math.Clamp(sx + 6, 4, Math.Max(4, Width - 110)), 12);
        e.Graphics.DrawString($"END  {SelectionEnd:0.000}s",
            Font, textBrush, Math.Clamp(ex + 6, 4, Math.Max(4, Width - 100)), Height - 28);

        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
    }

    private double XToSeconds(int x)
    {
        return Math.Clamp(
            x / (double)Math.Max(1, Width - 1) * duration,
            0, duration);
    }

    private int SecondsToX(double seconds)
    {
        if (duration <= 0) return 0;
        return (int)Math.Round(
            Math.Clamp(seconds / duration, 0, 1) *
            Math.Max(1, Width - 1));
    }

    private static float[] BuildPeaks(string path, int count)
    {
        using var r = new MediaFoundationReader(path);
        var samples = r.ToSampleProvider();
        var values = new float[count];

        long total = (long)(
            r.TotalTime.TotalSeconds *
            samples.WaveFormat.SampleRate *
            samples.WaveFormat.Channels);

        float[] buf = new float[8192];
        long seen = 0;

        while (true)
        {
            int n = samples.Read(buf, 0, buf.Length);
            if (n <= 0) break;

            for (int i = 0; i < n; i++)
            {
                long pos = seen + i;
                int bucket = (int)Math.Min(
                    count - 1,
                    pos * (long)count / Math.Max(1L, total));

                values[bucket] = Math.Max(values[bucket], Math.Abs(buf[i]));
            }

            seen += n;
        }

        return values;
    }
}
