using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VoiceGuard;

/// <summary>Compact click-through status indicator for VoiceGuard.</summary>
internal sealed class ModeOverlayForm : Form
{
    private readonly Label textLabel = new();
    private string currentMode = "LIVE";
    private string positionName = "TopCenter";
    private string styleName = "Text";
    private string monitorDeviceName = "";
    private Color modeColor = Color.LimeGreen;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public ModeOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Size = new Size(230, 38);
        BackColor = Color.Black; TransparencyKey = Color.Black; Opacity = .52;
        textLabel.Dock = DockStyle.Fill; textLabel.TextAlign = ContentAlignment.MiddleCenter;
        textLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        textLabel.ForeColor = Color.White; textLabel.BackColor = Color.Transparent;
        Controls.Add(textLabel);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Configure(positionName, styleName, monitorDeviceName);
        SetMode(currentMode);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; } }
    protected override void OnShown(EventArgs e) { base.OnShown(e); PositionOverlay(); }

    public void Configure(string position, string style, string? monitor = null)
    {
        positionName = position ?? "TopCenter";
        styleName = style ?? "Text";
        monitorDeviceName = monitor ?? "";
        bool isLed = string.Equals(styleName, "LED", StringComparison.OrdinalIgnoreCase);
        textLabel.Visible = !isLed;
        Size = isLed ? new Size(33, 33) : new Size(230, 38);
        Region?.Dispose();
        if (isLed)
        {
            using var circle = new GraphicsPath(); circle.AddEllipse(0, 0, Width, Height);
            Region = new Region(circle);
        }
        else Region = null;
        PositionOverlay(); Invalidate(); SetMode(currentMode);
    }
    private void PositionOverlay()
    {
        var screens = Screen.AllScreens;
        var targetScreen = screens.FirstOrDefault(s => string.Equals(s.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase));
        if (targetScreen == null) targetScreen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        var area = targetScreen.WorkingArea;
        int margin = 18, x = area.Left + (area.Width - Width) / 2, y = area.Top + margin;
        switch (positionName)
        {
            case "TopLeft": x = area.Left + margin; y = area.Top + margin; break;
            case "TopRight": x = area.Right - Width - margin; y = area.Top + margin; break;
            case "MiddleLeft": x = area.Left + margin; y = area.Top + (area.Height - Height) / 2; break;
            case "MiddleRight": x = area.Right - Width - margin; y = area.Top + (area.Height - Height) / 2; break;
            case "BottomLeft": x = area.Left + margin; y = area.Bottom - Height - margin; break;
            case "BottomCenter": x = area.Left + (area.Width - Width) / 2; y = area.Bottom - Height - margin; break;
            case "BottomRight": x = area.Right - Width - margin; y = area.Bottom - Height - margin; break;
            default: x = area.Left + (area.Width - Width) / 2; y = area.Top + margin; break;
        }
        Location = new Point(x, y);
    }
    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(PositionOverlay)); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        base.OnFormClosed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        PositionOverlay();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!string.Equals(styleName, "LED", StringComparison.OrdinalIgnoreCase)) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        var bounds = new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f);
        using var path = new GraphicsPath(); path.AddEllipse(bounds);
        e.Graphics.SetClip(path);
        // Keep the center fully saturated and fade only the outer portion.
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(255, modeColor),
            SurroundColors = new[] { Color.FromArgb(0, modeColor) },
            FocusScales = new PointF(.28f, .28f)
        };
        e.Graphics.FillEllipse(brush, bounds);
        e.Graphics.ResetClip();
        // Intentionally no outline: a transparency-key fringe can tint the LED pink.

    }
    public void SetMode(string mode)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => SetMode(mode))); return; }
        currentMode = mode ?? "LIVE";
        modeColor =             currentMode.Contains("BYPASS", StringComparison.OrdinalIgnoreCase) ? Color.Gold :
            currentMode.Contains("SOUNDBOARD", StringComparison.OrdinalIgnoreCase) ? Color.MediumPurple :
            currentMode.Contains("DRAIN", StringComparison.OrdinalIgnoreCase) || currentMode.Contains("ANALYZ", StringComparison.OrdinalIgnoreCase) || currentMode.Contains("DELAY", StringComparison.OrdinalIgnoreCase) ? Color.IndianRed :
            currentMode.Contains("STOP", StringComparison.OrdinalIgnoreCase) ? Color.Gray : Color.LimeGreen;
        textLabel.Text = "VOICEGUARD  •  " + currentMode;
        textLabel.ForeColor = Color.FromArgb(190, modeColor);
        if (!string.Equals(styleName, "LED", StringComparison.OrdinalIgnoreCase))
        {
            // Expand the text overlay for longer soundboard phrases instead of clipping them.
            int textWidth = TextRenderer.MeasureText(textLabel.Text, textLabel.Font).Width + 20;
            Size = new Size(Math.Clamp(textWidth, 230, 700), 38);
            PositionOverlay();
        }
        Invalidate();
    }
}
