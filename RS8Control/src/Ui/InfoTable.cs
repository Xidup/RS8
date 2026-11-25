namespace RS8Control.Ui;

/// <summary>
/// 只读信息表格：表头浅底 + 仅横向分隔线，取代 <see cref="TableLayoutPanel"/> + <c>CellBorderStyle.Single</c>。
///
/// 背景：快捷键速查页原来用两张 <see cref="TableLayoutPanel"/> 画说明书的表格，
/// 每个单元格一个 <see cref="Label"/> —— 两张表合计约 190 个控件，而且
/// <c>CellBorderStyle.Single</c> 画出来是满格黑线，观感像 Excel 2003。
///
/// 现在整张表就是**一个控件**：自己量换行、自己画线、自己排字。
/// 控件数从约 190 降到 2，也不再需要 TableLayoutPanel 的布局开销。
///
/// 性能：行高按 (文本, 列宽) 缓存，只有数据或列宽变化时才重算；
/// <c>OnPaint</c> 内零分配（画笔走 <see cref="Theme.Pen"/> 缓存）。
/// </summary>
internal sealed class InfoTable : Control
{
    private const TextFormatFlags CellFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    private string[] _headers = Array.Empty<string>();
    private int[] _baseWidths = Array.Empty<int>();
    private int[] _widths = Array.Empty<int>();
    private string[][] _rows = Array.Empty<string[]>();

    /// <summary>每行的高度（含上下内边距）。索引与 <see cref="_rows"/> 对应。</summary>
    private int[] _rowHeights = Array.Empty<int>();

    private int _headerHeight;
    private int _targetWidth;
    private Size _natural;

    /// <summary>换行高度缓存：键 = (文本, 单元格内宽, 字体)。</summary>
    private readonly Dictionary<(string Text, int Width, Font Font), int> _measure = new();

    public InfoTable()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>当目标宽度大于最小列宽之和时，是否按比例拉伸各列填满。</summary>
    public bool StretchToWidth { get; set; } = true;

    /// <summary>内容自然尺寸（宽 = 各列宽之和，高 = 表头 + 各行）。布局时用它算卡片高度。</summary>
    public Size NaturalSize => _natural;

    /// <summary>
    /// 设置表格内容。列宽为 96 DPI 基准值，按控件 DPI 缩放；若设置了目标宽度且允许拉伸则按比例展开。
    /// </summary>
    public void SetData(string[] headers, int[] widths, IReadOnlyList<string[]> rows)
    {
        _headers = headers;
        _baseWidths = (int[])widths.Clone();
        _widths = ComputeWidths(_targetWidth);
        _rows = new string[rows.Count][];
        for (int i = 0; i < rows.Count; i++) _rows[i] = rows[i];

        _measure.Clear();
        Remeasure();
        Invalidate();
    }

    /// <summary>
    /// 设置可用宽度：按当前 DPI 缩放基准列宽得到最小总宽，若 <see cref="StretchToWidth"/> 且可用宽度更大，
    /// 则按基准列宽权重把多余宽度分配到各列。仅在列宽实际变化时才重算行高并重绘。
    /// </summary>
    public void SetTargetWidth(int availableWidth)
    {
        _targetWidth = availableWidth;
        if (_baseWidths.Length == 0) return;

        int[] next = ComputeWidths(availableWidth);
        if (_widths.AsSpan().SequenceEqual(next)) return;

        _widths = next;
        Remeasure();
        Invalidate();
    }

    private int[] ComputeWidths(int availableWidth)
    {
        int[] next = new int[_baseWidths.Length];
        int minTotal = 0;
        for (int i = 0; i < _baseWidths.Length; i++)
        {
            next[i] = Theme.Scale(this, _baseWidths[i]);
            minTotal += next[i];
        }

        if (StretchToWidth && availableWidth > minTotal && next.Length > 0)
        {
            long baseSum = 0;
            for (int i = 0; i < _baseWidths.Length; i++)
                baseSum += Math.Max(0, _baseWidths[i]);

            if (baseSum > 0)
            {
                int extra = availableWidth - minTotal;
                int assigned = 0;
                for (int i = 0; i < next.Length - 1; i++)
                {
                    int add = (int)(extra * Math.Max(0, _baseWidths[i]) / baseSum);
                    next[i] += add;
                    assigned += add;
                }
                next[^1] += extra - assigned;
            }
        }

        return next;
    }

    /// <summary>单元格内宽（扣掉左右内边距）。</summary>
    private int InnerWidth(int col) =>
        Math.Max(Theme.Scale(this, 20), _widths[col] - Theme.Scale(this, Theme.Metrics.TablePadX) * 2);

    /// <summary>重算表头高度与每行高度，并刷新自然尺寸。</summary>
    private void Remeasure()
    {
        if (_widths.Length == 0)
        {
            _headerHeight = 0;
            _rowHeights = Array.Empty<int>();
            _natural = Size.Empty;
            return;
        }

        int padY = Theme.Scale(this, Theme.Metrics.TablePadY);

        _headerHeight = 0;
        for (int c = 0; c < _headers.Length && c < _widths.Length; c++)
            _headerHeight = Math.Max(_headerHeight, WrappedHeight(_headers[c], Theme.Fonts.BodyBold, InnerWidth(c)));
        _headerHeight += padY * 2;

        _rowHeights = new int[_rows.Length];
        for (int r = 0; r < _rows.Length; r++)
        {
            int h = 0;
            for (int c = 0; c < _widths.Length; c++)
            {
                string text = c < _rows[r].Length ? _rows[r][c] : "";
                h = Math.Max(h, WrappedHeight(text, Theme.Fonts.Body, InnerWidth(c)));
            }
            _rowHeights[r] = h + padY * 2;
        }

        int width = 0;
        foreach (int w in _widths) width += w;
        int height = _headerHeight;
        foreach (int h in _rowHeights) height += h;

        _natural = new Size(width, height);
        if (Size != _natural) Size = _natural;
    }

    /// <summary>按单元格内宽与字体量换行后的高度。</summary>
    private int WrappedHeight(string text, Font font, int innerWidth)
    {
        if (text.Length == 0) return font.Height;
        var key = (text, innerWidth, font);
        if (_measure.TryGetValue(key, out int cached)) return cached;

        int h;
        if (IsHandleCreated && !IsDisposed)
        {
            using var g = CreateGraphics();
            h = TextRenderer.MeasureText(g, text, font, new Size(innerWidth, 0), CellFlags).Height;
        }
        else
        {
            h = TextRenderer.MeasureText(text, font, new Size(innerWidth, 0), CellFlags).Height;
        }

        if (_measure.Count > 512) _measure.Clear();
        _measure[key] = h;
        return h;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_baseWidths.Length == 0) return;
        _widths = ComputeWidths(_targetWidth);
        _measure.Clear();
        Remeasure();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (_baseWidths.Length == 0) return;
        _widths = ComputeWidths(_targetWidth);
        _measure.Clear();
        Remeasure();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int padX = Theme.Scale(this, Theme.Metrics.TablePadX);
        int padY = Theme.Scale(this, Theme.Metrics.TablePadY);

        // 铺底：不铺的话控件矩形内会残留双缓冲的旧像素
        var backdrop = Parent?.BackColor ?? Theme.Bg.Surface;
        g.FillRectangle(Theme.Brush(backdrop == Color.Empty ? Theme.Bg.Surface : backdrop),
            new Rectangle(0, 0, Width, Height));

        if (_widths.Length == 0) return;

        int totalWidth = 0;
        foreach (int w in _widths) totalWidth += w;
        totalWidth = Math.Min(totalWidth, Width);

        // ---- 表头：浅底 + 加粗弱化字 ----
        g.FillRectangle(Theme.Brush(Theme.Bg.Sunken), new Rectangle(0, 0, totalWidth, _headerHeight));

        int x = 0;
        for (int c = 0; c < _headers.Length && c < _widths.Length; c++)
        {
            var cell = new Rectangle(x + padX, padY, InnerWidth(c), _headerHeight - padY * 2);
            TextRenderer.DrawText(g, _headers[c], Theme.Fonts.BodyBold, cell, Theme.Ink.Muted, CellFlags);
            x += _widths[c];
        }

        // 表头下沿
        g.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, _headerHeight - 1, totalWidth, _headerHeight - 1);

        // ---- 数据行：只画横向分隔线 ----
        int y = _headerHeight;
        for (int r = 0; r < _rows.Length; r++)
        {
            int h = _rowHeights[r];
            if (y >= Height) break;

            int cx = 0;
            for (int c = 0; c < _widths.Length; c++)
            {
                string text = c < _rows[r].Length ? _rows[r][c] : "";
                if (text.Length > 0)
                {
                    var cell = new Rectangle(cx + padX, y + padY, InnerWidth(c), h - padY * 2);
                    TextRenderer.DrawText(g, text, Theme.Fonts.Body, cell, Theme.Ink.Body, CellFlags);
                }
                cx += _widths[c];
            }

            y += h;
            if (r < _rows.Length - 1)
                g.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, y - 1, totalWidth, y - 1);
        }
    }
}
