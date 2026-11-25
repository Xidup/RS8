using System.Drawing.Drawing2D;

namespace RS8Control.Ui;

/// <summary>
/// 侧灯预设色块条：以单控件自绘替代多个独立 <see cref="Panel"/>，
/// 支持 DPI 缩放、悬停提示、选中高亮环与交互开关。
/// </summary>
internal sealed class ColorPresetStrip : Control
{
    private const int SwatchW = 24;
    private const int SwatchH = 20;
    private const int SwatchPitch = 28;
    private const int StripPad = 4;

    private readonly ToolTip _tip = new();
    private bool _tipDisposed;
    private (string Name, int Hue, int Sat, Color Preview)[] _presets =
        Array.Empty<(string Name, int Hue, int Sat, Color Preview)>();

    private bool _interactive = true;
    private int _hover = -1;
    private int _pressed = -1;

    public event Action<int, int>? PresetChosen;

    public ColorPresetStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        TabStop = false;
        Cursor = Cursors.Hand;
        UpdateDimensions();
    }

    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            if (!EffectiveInteractive)
            {
                _hover = -1;
                _pressed = -1;
                if (!_tipDisposed && !IsDisposed) _tip.SetToolTip(this, "");
            }
            UpdateCursor();
            Invalidate();
        }
    }

    public int SelectedHue { get; private set; } = -1;

    public int SelectedSat { get; private set; } = -1;

    private bool EffectiveInteractive => _interactive && Enabled;

    public void SetPresets((string Name, int Hue, int Sat, Color Preview)[] presets)
    {
        _presets = presets ?? Array.Empty<(string Name, int Hue, int Sat, Color Preview)>();
        _hover = -1;
        _pressed = -1;
        UpdateDimensions();
        Invalidate();
    }

    public void SetSelectedHs(int hue, int sat)
    {
        if (SelectedHue == hue && SelectedSat == sat) return;
        SelectedHue = hue;
        SelectedSat = sat;
        Invalidate();
    }

    private void UpdateDimensions()
    {
        int pad = Theme.Scale(this, StripPad);
        int sw = Theme.Scale(this, SwatchW);
        int sh = Theme.Scale(this, SwatchH);
        int pitch = Theme.Scale(this, SwatchPitch);

        int w = _presets.Length == 0
            ? pad * 2
            : pad * 2 + (_presets.Length - 1) * pitch + sw;
        int h = pad * 2 + sh;
        Size = new Size(w, h);
    }

    private void UpdateCursor() =>
        Cursor = EffectiveInteractive && _hover >= 0 ? Cursors.Hand : Cursors.Default;

    private int HitTest(Point p)
    {
        if (_presets.Length == 0) return -1;
        int pad = Theme.Scale(this, StripPad);
        int sw = Theme.Scale(this, SwatchW);
        int sh = Theme.Scale(this, SwatchH);
        int pitch = Theme.Scale(this, SwatchPitch);

        if (p.Y < pad || p.Y >= pad + sh) return -1;
        int relX = p.X - pad;
        if (relX < 0) return -1;
        int idx = relX / pitch;
        if (idx >= _presets.Length) return -1;
        return relX - idx * pitch < sw ? idx : -1;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateDimensions();
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!EffectiveInteractive)
        {
            _hover = -1;
            _pressed = -1;
            if (!_tipDisposed && !IsDisposed) _tip.SetToolTip(this, "");
        }
        UpdateCursor();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = HitTest(e.Location);
        if (_hover == idx) return;
        _hover = idx;
        UpdateCursor();

        if (idx >= 0)
        {
            var p = _presets[idx];
            _tip.SetToolTip(this, $"{p.Name}（色相 {p.Hue} · 饱和 {p.Sat}）");
        }
        else
        {
            _tip.SetToolTip(this, "");
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0 && _pressed < 0) return;
        _hover = -1;
        _pressed = -1;
        _tip.SetToolTip(this, "");
        UpdateCursor();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !EffectiveInteractive) return;
        _pressed = HitTest(e.Location);
        if (_pressed >= 0) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int down = _pressed;
        _pressed = -1;
        Invalidate();

        if (!EffectiveInteractive || down < 0) return;
        int up = HitTest(e.Location);
        if (up == down)
        {
            var p = _presets[up];
            PresetChosen?.Invoke(p.Hue, p.Sat);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var parent = Parent;
        Color backdrop = parent == null || parent.BackColor == Color.Empty ? Theme.Bg.Surface : parent.BackColor;
        g.Clear(backdrop);

        if (_presets.Length == 0) return;

        int pad = Theme.Scale(this, StripPad);
        int sw = Theme.Scale(this, SwatchW);
        int sh = Theme.Scale(this, SwatchH);
        int pitch = Theme.Scale(this, SwatchPitch);
        float radius = Theme.ScaleF(this, 4f);

        for (int i = 0; i < _presets.Length; i++)
        {
            var (_, hue, sat, preview) = _presets[i];
            float x = pad + i * pitch;
            float y = pad;
            var rect = new RectangleF(x, y, sw, sh);

            bool selected = SelectedHue == hue && SelectedSat == sat;
            bool hovered = i == _hover && EffectiveInteractive;

            g.FillPath(Theme.Brush(preview), Theme.RoundedPath(rect, radius));
            g.DrawPath(Theme.Pen(Theme.Line.Control), Theme.RoundedPath(rect, radius));

            if (selected)
            {
                var ring = new RectangleF(x - 2f, y - 2f, sw + 4f, sh + 4f);
                g.DrawPath(Theme.Pen(Theme.Accent.Base, Theme.ScaleF(this, 2f)), Theme.RoundedPath(ring, radius + 2f));
            }
            else if (hovered)
            {
                var ring = new RectangleF(x - 1.5f, y - 1.5f, sw + 3f, sh + 3f);
                g.DrawPath(Theme.Pen(Theme.Ink.Muted, Theme.ScaleF(this, 1.4f)), Theme.RoundedPath(ring, radius + 1.5f));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tipDisposed = true;
        base.Dispose(disposing);
        if (disposing) _tip.Dispose();
    }
}
