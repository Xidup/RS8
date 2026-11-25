using System.Drawing.Drawing2D;

namespace RS8Control.Ui;

/// <summary>
/// 全局设计令牌：颜色 / 字体 / 尺寸 / 圆角。
///
/// 背景：改造前这些值以魔法数字的形式散落在 10 多个文件里，同一个语义有多个值 ——
/// 光是「边框灰」就有 #E2E6EE / #D6DCE7 / #CED5E0 三种，字体有 10 档。
/// 这里收敛成唯一来源，新代码一律走 Theme，不要再写字面量。
///
/// 字体是**共享静态实例**：改造前全程序有 141 个 Font 实例（`ShortcutPanel.Cell()` 每个单元格建一个），
/// 全部靠终结器回收、从不 Dispose。实测确认 WinForms 不会连带释放通过 Font 属性赋入的字体，
/// 所以共享是安全的（见 `_research/memtest/fonttest/`）。
/// 注意：这些静态 Font **绝不能 Dispose**。
/// </summary>
internal static class Theme
{
    // ============================================================
    //  DPI 缩放
    // ============================================================

    public static int Scale(int px, int dpi) => (int)Math.Round(px * (dpi <= 0 ? 96 : dpi) / 96.0);

    public static int Scale(Control c, int px) => Scale(px, c.DeviceDpi);

    public static float ScaleF(float px, int dpi) => px * (dpi <= 0 ? 96 : dpi) / 96f;

    public static float ScaleF(Control c, float px) => ScaleF(px, c.DeviceDpi);

    // ============================================================
    //  颜色
    // ============================================================

    /// <summary>背景层。</summary>
    internal static class Bg
    {
        /// <summary>窗口 / 页面底色。</summary>
        public static readonly Color App = Color.FromArgb(0xF5, 0xF6, 0xF8);
        /// <summary>卡片、顶栏。</summary>
        public static readonly Color Surface = Color.White;
        /// <summary>内嵌区（胶囊网格、日志、表格表头）。</summary>
        public static readonly Color Sunken = Color.FromArgb(0xF7, 0xF8, 0xFA);
        /// <summary>悬停填充。</summary>
        public static readonly Color Hover = Color.FromArgb(0xED, 0xF1, 0xF7);
        /// <summary>选中填充。</summary>
        public static readonly Color Selected = Color.FromArgb(0xE8, 0xF0, 0xFE);
    }

    /// <summary>描边 / 分隔线。</summary>
    internal static class Line
    {
        /// <summary>卡片描边、分隔线。</summary>
        public static readonly Color Subtle = Color.FromArgb(0xE4, 0xE7, 0xEE);
        /// <summary>输入框 / 按钮描边。</summary>
        public static readonly Color Control = Color.FromArgb(0xD5, 0xDA, 0xE5);
        /// <summary>焦点描边。</summary>
        public static readonly Color Focus = Accent.Base;
    }

    /// <summary>文字。</summary>
    internal static class Ink
    {
        /// <summary>标题、数值。</summary>
        public static readonly Color Primary = Color.FromArgb(0x1B, 0x21, 0x30);
        /// <summary>正文。</summary>
        public static readonly Color Body = Color.FromArgb(0x3D, 0x46, 0x57);
        /// <summary>提示、说明。改造前是 #8B95A7，对比度不足，调深。</summary>
        public static readonly Color Muted = Color.FromArgb(0x7C, 0x86, 0x99);
        /// <summary>禁用。</summary>
        public static readonly Color Disabled = Color.FromArgb(0xAF, 0xB6, 0xC2);
    }

    /// <summary>主色。</summary>
    internal static class Accent
    {
        public static readonly Color Base = Color.FromArgb(0x2F, 0x6D, 0xF6);
        public static readonly Color Hover = Color.FromArgb(0x24, 0x5B, 0xD6);
        public static readonly Color Pressed = Color.FromArgb(0x1D, 0x4C, 0xB8);
        /// <summary>选中填充（浅）。</summary>
        public static readonly Color Subtle = Color.FromArgb(0xE8, 0xF0, 0xFE);
    }

    /// <summary>语义状态色。</summary>
    internal static class State
    {
        /// <summary>已连接。</summary>
        public static readonly Color Ok = Color.FromArgb(0x0E, 0x8A, 0x5F);
        /// <summary>已连接浅底。</summary>
        public static readonly Color OkSubtle = Color.FromArgb(0xE6, 0xF7, 0xF0);
        /// <summary>部分可用 / 只读。</summary>
        public static readonly Color Warn = Color.FromArgb(0xB2, 0x6A, 0x00);
        /// <summary>警告浅底。</summary>
        public static readonly Color WarnSubtle = Color.FromArgb(0xFF, 0xF4, 0xE0);
        /// <summary>错误。</summary>
        public static readonly Color Danger = Color.FromArgb(0xC0, 0x39, 0x2B);
        /// <summary>已改键填充。</summary>
        public static readonly Color DangerSubtle = Color.FromArgb(0xFD, 0xEC, 0xEA);
        /// <summary>空闲 / 未检测。</summary>
        public static readonly Color Idle = Color.FromArgb(0x8B, 0x95, 0xA7);
        /// <summary>空闲浅底。</summary>
        public static readonly Color IdleSubtle = Bg.Sunken;
    }

    /// <summary>键帽专用配色（KeyboardCanvas / FnKeyCanvas / PaletteCanvas 三处共用）。</summary>
    internal static class Key
    {
        public static readonly Color Fill = Color.FromArgb(0xF7, 0xF8, 0xFA);
        public static readonly Color Border = Color.FromArgb(0xCE, 0xD5, 0xE0);
        public static readonly Color Text = Color.FromArgb(0x3D, 0x46, 0x57);

        public static readonly Color HoverFill = Color.FromArgb(0xEE, 0xF2, 0xF9);
        public static readonly Color HoverBorder = Color.FromArgb(0x9E, 0xAE, 0xC7);
        public static readonly Color BottomEdge = Color.FromArgb(0xD8, 0xDF, 0xEA);

        public static readonly Color DirtyFill = Color.FromArgb(0xFD, 0xEC, 0xEA);
        public static readonly Color DirtyBorder = Color.FromArgb(0xDC, 0x5A, 0x50);
        public static readonly Color DirtyText = Color.FromArgb(0xA0, 0x28, 0x1E);

        public static readonly Color SelectedFill = Color.FromArgb(0xE8, 0xF0, 0xFE);
        public static readonly Color SelectedBorder = Accent.Base;
        public static readonly Color SelectedText = Color.FromArgb(0x14, 0x3C, 0x96);

        public static readonly Color PressedFill = Color.FromArgb(0x1D, 0x9E, 0x75);
        public static readonly Color PressedBorder = Color.FromArgb(0x12, 0x7A, 0x59);

        /// <summary>无功能键（Fn 图上没有 Fn 功能的键）。</summary>
        public static readonly Color IdleFill = Color.FromArgb(0xF7, 0xF8, 0xFA);
        public static readonly Color IdleBorder = Line.Subtle;
        public static readonly Color IdleText = Color.FromArgb(0xB2, 0xB9, 0xC4);

        /// <summary>禁用键。</summary>
        public static readonly Color DisabledFill = Bg.Sunken;
        public static readonly Color DisabledBorder = Line.Subtle;
        public static readonly Color DisabledText = Ink.Disabled;

        /// <summary>搜索命中。</summary>
        public static readonly Color MatchFill = Color.FromArgb(0xFF, 0xF6, 0xD6);
        public static readonly Color MatchBorder = Color.FromArgb(0xD6, 0x9E, 0x14);
        public static readonly Color MatchText = Color.FromArgb(0x78, 0x52, 0x00);
    }

    /// <summary>胶囊网格专用配色。</summary>
    internal static class Chip
    {
        public static readonly Color DisabledSelectedFill = Color.FromArgb(0xD9, 0xE5, 0xFB);
        public static readonly Color DisabledSelectedBorder = Color.FromArgb(0xA6, 0xC1, 0xF5);
        public static readonly Color DisabledSelectedText = Color.FromArgb(0x47, 0x6B, 0xB8);
    }

    // ============================================================
    //  字体（共享静态，禁止 Dispose）
    // ============================================================

    internal static class Fonts
    {
        private const string Family = "Microsoft YaHei UI";

        /// <summary>卡片标题 11pt SemiBold。</summary>
        public static readonly Font Title = new(Family, 11F, FontStyle.Bold);

        /// <summary>顶栏状态 10pt SemiBold —— 全程序唯一的强调字号。</summary>
        public static readonly Font Emphasis = new(Family, 10F, FontStyle.Bold);

        /// <summary>正文 / 按钮 / 标签 9pt。</summary>
        public static readonly Font Body = new(Family, 9F);

        /// <summary>正文加粗（列表选中项等）。</summary>
        public static readonly Font BodyBold = new(Family, 9F, FontStyle.Bold);

        /// <summary>提示、说明、副标签 8.25pt。</summary>
        public static readonly Font Caption = new(Family, 8.25F);

        /// <summary>日志、十六进制 9pt。</summary>
        public static readonly Font Mono = new("Consolas", 9F);

        /// <summary>键帽文字用的字体族名（画布按缩放动态建字号，不能共享）。</summary>
        public const string KeycapFamily = Family;
    }

    /// <summary>
    /// 按字号取字体（带缓存）。画布的字号是跟着缩放算出来的，没法用固定常量，
    /// 但同一尺寸会被反复用到（每帧、每个键），所以按 (族, 字号, 样式) 缓存。
    /// **返回的对象是共享的，绝不能 Dispose。**
    /// </summary>
    private static readonly Dictionary<(string Family, int SizeQ, FontStyle Style), Font> FontCache = new();
    private static readonly Queue<(string Family, int SizeQ, FontStyle Style)> FontOrder = new();
    private const int FontCacheLimit = 128;

    internal static Font FontAt(string family, float size, FontStyle style = FontStyle.Regular)
    {
        // 字号量化到 0.25pt，避免缩放时产生大量只差零点几磅的条目
        int q = (int)Math.Round(size * 4);
        var key = (family, q, style);
        if (FontCache.TryGetValue(key, out var f)) return f;

        if (FontCache.Count >= FontCacheLimit && FontOrder.Count > 0)
        {
            var oldest = FontOrder.Dequeue();
            if (FontCache.Remove(oldest, out var dead)) dead.Dispose();
        }
        f = new Font(family, q / 4f, style);
        FontCache[key] = f;
        FontOrder.Enqueue(key);
        return f;
    }

    // ============================================================
    //  尺寸
    // ============================================================

    internal static class Metrics
    {
        /// <summary>卡片圆角。</summary>
        public const int CardRadius = 8;

        /// <summary>按钮 / 输入 / 胶囊 / 内嵌区圆角。</summary>
        public const int ControlRadius = 6;

        /// <summary>控件统一高度。</summary>
        public const int ControlHeight = 32;

        /// <summary>卡片内边距。</summary>
        public const int CardPad = 16;

        /// <summary>卡片之间的间距。</summary>
        public const int CardGap = 12;

        /// <summary>标签条高度。</summary>
        public const int TabStripHeight = 36;

        /// <summary>标签之间的水平间距。</summary>
        public const int TabGap = 24;

        /// <summary>按钮左右内边距。</summary>
        public const int ButtonPadX = 14;

        /// <summary>表格行内边距。</summary>
        public const int TablePadX = 8;
        public const int TablePadY = 6;
    }

    // ============================================================
    //  几何：圆角矩形路径（带缓存）
    // ============================================================

    /// <summary>
    /// 圆角矩形路径缓存。
    ///
    /// **键必须包含位置 (X, Y)** —— 同一个 DC 里会画多个不同位置的圆角矩形
    /// （标签条的下划线、键帽画布的 87 个键、胶囊网格的 60 个胶囊）。
    /// 早期版本只按 (宽, 高, 圆角) 做键，导致同尺寸的矩形全部复用**第一个**的坐标，
    /// 表现为「下划线永远停在上一个选中的标签下面」。这个坑很隐蔽，别再踩。
    ///
    /// 尺寸连续变化时会产生大量一次性条目，所以超过上限就整体清空
    /// （与 ShortcutPanel 的 _wrapCache 同一策略）。
    ///
    /// **返回的路径是共享对象，调用方绝对不能 Dispose。**
    /// 这样做的目的是兑现性能红线：OnPaint 内零分配。改造前键盘画布单帧要新建约 90 个 GraphicsPath。
    /// </summary>
    private static readonly Dictionary<(int X, int Y, int W, int H, int R), GraphicsPath> PathCache = new();
    private static readonly Queue<(int X, int Y, int W, int H, int R)> PathOrder = new();
    private const int PathCacheLimit = 512;

    internal static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        int x = (int)Math.Round(r.X);
        int y = (int)Math.Round(r.Y);
        int w = (int)Math.Round(r.Width);
        int h = (int)Math.Round(r.Height);
        int rad = (int)Math.Round(Math.Min(radius, Math.Min(w, h) / 2f));
        if (w <= 0 || h <= 0) rad = 0;

        var key = (x, y, w, h, rad);
        if (PathCache.TryGetValue(key, out var cached)) return cached;

        if (PathCache.Count >= PathCacheLimit && PathOrder.Count > 0)
        {
            var oldest = PathOrder.Dequeue();
            if (PathCache.Remove(oldest, out var dead)) dead.Dispose();
        }

        var p = new GraphicsPath();
        if (rad <= 0)
        {
            p.AddRectangle(new RectangleF(x, y, w, h));
        }
        else
        {
            float d = rad * 2f;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
        }

        PathCache[key] = p;
        PathOrder.Enqueue(key);
        return p;
    }

    /// <summary>填充圆角矩形（用缓存的路径，无分配）。</summary>
    internal static void FillRounded(Graphics g, RectangleF r, float radius, Brush brush) =>
        g.FillPath(brush, RoundedPath(r, radius));

    /// <summary>描边圆角矩形（用缓存的路径，无分配）。</summary>
    internal static void DrawRounded(Graphics g, RectangleF r, float radius, Pen pen) =>
        g.DrawPath(pen, RoundedPath(r, radius));

    // ============================================================
    //  画笔 / 画刷缓存（OnPaint 内零分配）
    // ============================================================

    private static readonly Dictionary<long, Pen> PenCache = new();
    private static readonly Queue<long> PenOrder = new();
    private static readonly Dictionary<int, SolidBrush> BrushCache = new();
    private static readonly Queue<int> BrushOrder = new();
    private const int GdiObjCacheLimit = 256;

    /// <summary>取一支缓存的实线画笔。**不要 Dispose。**</summary>
    internal static Pen Pen(Color c, float width = 1f)
    {
        long key = ((long)c.ToArgb() << 16) | (uint)Math.Round(width * 4);
        if (PenCache.TryGetValue(key, out var p)) return p;
        if (PenCache.Count >= GdiObjCacheLimit && PenOrder.Count > 0)
        {
            var oldest = PenOrder.Dequeue();
            if (PenCache.Remove(oldest, out var dead)) dead.Dispose();
        }
        p = new Pen(c, width);
        PenCache[key] = p;
        PenOrder.Enqueue(key);
        return p;
    }

    /// <summary>取一个缓存的实心画刷。**不要 Dispose。**</summary>
    internal static SolidBrush Brush(Color c)
    {
        int key = c.ToArgb();
        if (BrushCache.TryGetValue(key, out var b)) return b;
        if (BrushCache.Count >= GdiObjCacheLimit && BrushOrder.Count > 0)
        {
            var oldest = BrushOrder.Dequeue();
            if (BrushCache.Remove(oldest, out var dead)) dead.Dispose();
        }
        b = new SolidBrush(c);
        BrushCache[key] = b;
        BrushOrder.Enqueue(key);
        return b;
    }

    /// <summary>按亮度系数压暗颜色（用于脉冲动效）。</summary>
    internal static Color Dim(Color c, double k) => Color.FromArgb(
        Math.Clamp((int)(c.R * k), 0, 255),
        Math.Clamp((int)(c.G * k), 0, 255),
        Math.Clamp((int)(c.B * k), 0, 255));

    /// <summary>两色线性混合（k=0 取 a，k=1 取 b）。</summary>
    internal static Color Blend(Color a, Color b, double k) => Color.FromArgb(
        Math.Clamp((int)Math.Round(a.R + (b.R - a.R) * k), 0, 255),
        Math.Clamp((int)Math.Round(a.G + (b.G - a.G) * k), 0, 255),
        Math.Clamp((int)Math.Round(a.B + (b.B - a.B) * k), 0, 255));
}
