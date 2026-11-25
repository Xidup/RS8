namespace RS8Control.Ui;

/// <summary>
/// 自绘开关（36×20 圆角胶囊 + 白色滑块）。
///
/// 取代顶栏的「只读」复选框：复选框只表达「勾没勾」，开关能表达「这个状态会改变行为」——
/// 只读打开会直接阻止所有写入，值得用一个更醒目的控件。
///
/// 性能：<c>OnPaint</c> 内零分配（路径与画笔走 <see cref="Theme"/> 缓存）。
/// </summary>
internal sealed class ToggleSwitch : Control
{
    private const int TrackW = 36;
    private const int TrackH = 20;
    private const int Knob = 16;
    private const int Inset = 2;

    private bool _checked;
    private bool _hover;
    private bool _pressed;

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(TrackW, TrackH);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Size ScaledSize() => new(Theme.Scale(this, TrackW), Theme.Scale(this, TrackH));

    public override Size GetPreferredSize(Size proposedSize) => ScaledSize();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Size = ScaledSize();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Size = ScaledSize();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var backdrop = Parent?.BackColor ?? Theme.Bg.Surface;
        g.Clear(backdrop == Color.Empty ? Theme.Bg.Surface : backdrop);

        int trackW = Theme.Scale(this, TrackW);
        int trackH = Theme.Scale(this, TrackH);
        int knob = Theme.Scale(this, Knob);
        int inset = Theme.Scale(this, Inset);

        Color track, knobColor;
        bool drawBorder;
        Color borderColor;

        if (!Enabled)
        {
            if (_checked)
            {
                track = Theme.Blend(Theme.Bg.Sunken, Theme.Accent.Base, 0.35);
                drawBorder = false;
                borderColor = track;
                knobColor = Color.White;
            }
            else
            {
                track = Theme.Bg.Sunken;
                drawBorder = true;
                borderColor = Theme.Line.Subtle;
                knobColor = Theme.Blend(Theme.Ink.Disabled, Theme.Bg.Surface, 0.45);
            }
        }
        else
        {
            // 轨道：关=浅灰底 + 描边；开=主色实心
            track = _checked
                ? (_pressed ? Theme.Accent.Pressed : _hover ? Theme.Accent.Hover : Theme.Accent.Base)
                : (_hover ? Theme.Bg.Hover : Theme.Bg.Sunken);
            drawBorder = !_checked;
            borderColor = Theme.Line.Control;
            knobColor = Color.White;
        }

        var rect = new RectangleF(0.5f, 0.5f, trackW - 1f, trackH - 1f);
        g.FillPath(Theme.Brush(track), Theme.RoundedPath(rect, trackH / 2f));
        if (drawBorder)
            g.DrawPath(Theme.Pen(borderColor), Theme.RoundedPath(rect, trackH / 2f));

        // 滑块
        int kx = _checked ? trackW - knob - inset : inset;
        g.FillEllipse(Theme.Brush(knobColor), kx, inset, knob, knob);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Enabled) return;
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hover && !_pressed) return;
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed) return;
        _pressed = false;
        Invalidate();
        // 在 MouseUp 里切换，和标准按钮的语义一致（按下后移开再松手不应触发）
        if (Enabled && ClientRectangle.Contains(e.Location)) Checked = !Checked;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) { _hover = false; _pressed = false; }
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    /// <summary>空格/回车切换（虽然 TabStop=false，留着以备将来开放键盘操作）。</summary>
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        if (e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
    }
}
