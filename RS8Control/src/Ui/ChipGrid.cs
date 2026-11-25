namespace RS8Control.Ui;

/// <summary>
/// 可换行的胶囊选择网格（灯效模式用）。整块是**一个自绘控件**，
/// 取代原来的 <c>FlowLayoutPanel + 60 个 Button</c>。
///
/// 换掉的两个理由：
///   1. 控件数：三个灯区合计 60 个胶囊 Button → 3 个 ChipGrid。
///   2. 交互：原来网格自己 <c>AutoScroll</c>、高度锁在 172px，
///      46 项的主键区灯效要在 4 行的小框里滚 —— 卡片内嵌滚动条很别扭。
///      现在网格**自动撑高**，卡片跟着长，由页面统一滚动。
///
/// 性能：胶囊宽度按文字实测并缓存（文字不变就不重量）；
/// <c>OnPaint</c> 内零分配（路径与画笔走 <see cref="Theme"/> 缓存）。
/// </summary>
internal sealed class ChipGrid : Control
{
    private const int ChipHeight = 26;
    private const int ChipPadX = 12;
    private const int GapX = 6;
    private const int GapY = 6;

    /// <summary>网格四周内边距。原来是 Sunken 内嵌面板，留 8px 让胶囊不贴着边缘。</summary>
    private const int Pad = 8;

    private readonly List<(string Text, int Value, int Width)> _items = new();
    private readonly List<(Rectangle Rect, int Value)> _hits = new();

    private int _selected;
    private int _hover = -1;
    private int _downValue = -1;
    private bool _interactive = true;

    /// <summary>有效可用性：自身开关 且 继承来的 Enabled（父容器被禁用时也要变灰）。</summary>
    private bool EffectiveInteractive => _interactive && Enabled;

    /// <summary>点了某个胶囊（参数是该胶囊的 Value）。</summary>
    public event Action<int>? Chosen;

    public ChipGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    /// <summary>false 时变灰且不响应鼠标。</summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            if (!_interactive) { _hover = -1; _downValue = -1; }
            Cursor = EffectiveInteractive ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>当前选中的 Value。</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    /// <summary>设置胶囊列表与初始选中值。</summary>
    public void SetItems(IEnumerable<(string Text, int Value)> items, int selected)
    {
        _items.Clear();
        foreach (var (text, value) in items)
            _items.Add((text, value, 0));

        RemeasureWidths();
        _selected = selected;
        _downValue = -1;
        _hits.Clear();
        Reflow();
        Invalidate();
    }

    private void RemeasureWidths()
    {
        if (_items.Count == 0) return;
        int padX = Theme.Scale(this, ChipPadX);

        if (IsHandleCreated && !IsDisposed)
        {
            using var g = CreateGraphics();
            for (int i = 0; i < _items.Count; i++)
            {
                var (text, value, _) = _items[i];
                int w = TextRenderer.MeasureText(g, text, Theme.Fonts.Body).Width + padX * 2;
                _items[i] = (text, value, w);
            }
        }
        else
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var (text, value, _) = _items[i];
                int w = TextRenderer.MeasureText(text, Theme.Fonts.Body).Width + padX * 2;
                _items[i] = (text, value, w);
            }
        }
    }

    /// <summary>按当前宽度算需要几行，返回内容总高（含上下内边距）。</summary>
    public int PreferredHeight
    {
        get
        {
            int pad = Theme.Scale(this, Pad);
            int chipH = Theme.Scale(this, ChipHeight);
            int gapX = Theme.Scale(this, GapX);
            int gapY = Theme.Scale(this, GapY);

            int avail = Math.Max(Theme.Scale(this, 40), Width - pad * 2);
            int rows = 1, line = 0;
            foreach (var (_, _, w) in _items)
            {
                if (line > 0 && line + w > avail) { rows++; line = 0; }
                line += w + gapX;
            }
            return rows * chipH + (rows - 1) * gapY + pad * 2;
        }
    }

    /// <summary>宽度变化后重算自身高度（高度变了才改，避免布局抖动）。</summary>
    private void Reflow()
    {
        int h = PreferredHeight;
        if (Height != h) Height = h;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RemeasureWidths();
        Reflow();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RemeasureWidths();
        Reflow();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Reflow();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var backdrop = Parent?.BackColor ?? Theme.Bg.Surface;
        g.Clear(backdrop == Color.Empty ? Theme.Bg.Surface : backdrop);

        _hits.Clear();
        if (_items.Count == 0) return;

        int pad = Theme.Scale(this, Pad);
        int chipH = Theme.Scale(this, ChipHeight);
        int gapX = Theme.Scale(this, GapX);
        int gapY = Theme.Scale(this, GapY);
        float radius = Theme.Scale(this, Theme.Metrics.ControlRadius);

        int avail = Math.Max(Theme.Scale(this, 40), Width - pad * 2);
        int x = pad, y = pad, line = 0;

        foreach (var (text, value, w) in _items)
        {
            if (line > 0 && line + w > avail) { x = pad; y += chipH + gapY; line = 0; }

            var r = new RectangleF(x, y, w, chipH);
            _hits.Add((new Rectangle(x, y, w, chipH), value));

            bool selected = value == _selected;
            bool hover = EffectiveInteractive && value == _hover;

            Color fill, border, ink;
            if (!EffectiveInteractive)
            {
                if (selected)
                {
                    fill = Theme.Chip.DisabledSelectedFill;
                    border = Theme.Chip.DisabledSelectedBorder;
                    ink = Theme.Chip.DisabledSelectedText;
                }
                else
                {
                    fill = Theme.Bg.Sunken;
                    border = Theme.Line.Subtle;
                    ink = Theme.Ink.Disabled;
                }
            }
            else if (selected)
            {
                fill = Theme.Accent.Base;
                border = Theme.Accent.Base;
                ink = Color.White;
            }
            else
            {
                fill = hover ? Theme.Bg.Hover : Theme.Bg.Surface;
                border = Theme.Line.Control;
                ink = Theme.Ink.Body;
            }

            g.FillPath(Theme.Brush(fill), Theme.RoundedPath(r, radius));
            g.DrawPath(Theme.Pen(border), Theme.RoundedPath(r, radius));

            TextRenderer.DrawText(g, text, Theme.Fonts.Body,
                new Rectangle(x, y, w, chipH), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            int step = w + gapX;
            x += step;
            line += step;
        }
    }

    private int HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i].Value;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!EffectiveInteractive) return;
        int v = HitTest(e.Location);
        if (v == _hover) return;
        _hover = v;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _downValue = -1;
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !EffectiveInteractive) return;
        _downValue = HitTest(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int down = _downValue;
        _downValue = -1;
        if (e.Button != MouseButtons.Left || !EffectiveInteractive) return;
        if (down >= 0 && HitTest(e.Location) == down) Chosen?.Invoke(down);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _hover = -1;
        _downValue = -1;
        Cursor = EffectiveInteractive ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }
}
