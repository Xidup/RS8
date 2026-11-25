namespace RS8Control.Ui;

/// <summary>
/// 自绘标签宿主：上方一条下划线式标签条，下方是内容区。
///
/// 取代原生 <see cref="TabControl"/>。原生标签条的问题很具体：
/// 灰色渐变标签带、激活页一圈蚀刻边框、页面区底色和页面底色撞色 ——
/// 全程序「显旧」的观感有一大半来自它。
///
/// 视觉规格：
///   · 标签条高 36px，底部 1px <see cref="Theme.Line.Subtle"/> 通栏线
///   · 选中：<see cref="Theme.Ink.Primary"/> + 2px <see cref="Theme.Accent.Base"/> 下划线
///   · 悬停（未选中）：<see cref="Theme.Ink.Body"/>
///   · 常态：<see cref="Theme.Ink.Muted"/>
///   · 内容区无边框，直接铺页面底色
///
/// 性能：标签文字宽度只在标题变化时重算并缓存；<c>OnPaint</c> 内零分配
/// （画笔走 <see cref="Theme.Pen"/> 缓存，无 new）。
/// 只 <c>Invalidate()</c> 标签条自己，不触发内容区重排。
/// </summary>
internal sealed class TabHost : UserControl
{
    /// <summary>标签之间的水平间距。</summary>
    private const int TabGap = 16;

    /// <summary>每个标签左右各留的内边距（也是下划线的伸出量）。</summary>
    private const int TabPadX = 8;

    /// <summary>标签条左边距，与原来 <c>_tabs.Padding = new Point(18, 6)</c> 的观感对齐。</summary>
    private const int StripPadLeft = 18;

    private sealed class Page
    {
        public required string Title;
        public Control? Content;
        public Func<Control>? Factory;
        public Action<Control>? OnCreated;
        public int TextWidth = -1;          // 缓存：-1 表示需要重算
    }

    private readonly List<Page> _pages = new();
    private readonly Panel _content;
    private readonly Strip _strip;

    private int _selected = -1;

    /// <summary>选中页变化。<c>sender</c> 是本控件。</summary>
    public event EventHandler? SelectedIndexChanged;

    public TabHost()
    {
        // 标签条铺白底（和顶栏连成一条），内容区铺页面底色 —— 两者之间由那条 1px 底线分开。
        BackColor = Theme.Bg.Surface;

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg.App,
        };

        _strip = new Strip(this) { Dock = DockStyle.Top };

        Controls.Add(_content);
        Controls.Add(_strip);
    }

    /// <summary>标签条控件（内部使用）。</summary>
    private sealed class Strip : Control
    {
        private readonly TabHost _owner;
        private int _hover = -1;

        public Strip(TabHost owner)
        {
            _owner = owner;
            Height = Theme.Metrics.TabStripHeight;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg.Surface);

            // 先画通栏底线，再画标签 —— 顺序不能反：选中态的下划线要压在底线上，
            // 否则那条 1px 的底线会把下划线盖掉一半，看起来像没有下划线。
            g.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, Height - 1, Width, Height - 1);

            int tabGap = Theme.Scale(this, TabGap);
            int tabPadX = Theme.Scale(this, TabPadX);
            int stripPadLeft = Theme.Scale(this, StripPadLeft);
            int underlineH = Math.Max(2, Theme.Scale(this, 2));

            int n = _owner._pages.Count;
            int x = stripPadLeft;
            int underlineY = Height - underlineH;

            for (int i = 0; i < n; i++)
            {
                var page = _owner._pages[i];
                int tw = _owner.MeasureTitle(i);
                var rect = new Rectangle(x, 0, tw + tabPadX * 2, Height);

                bool selected = i == _owner._selected;
                Color ink = selected ? Theme.Ink.Primary
                    : i == _hover ? Theme.Ink.Body
                    : Theme.Ink.Muted;
                Font font = selected ? Theme.Fonts.BodyBold : Theme.Fonts.Body;

                // 选中态的下划线：2px，贴标签条底边
                if (selected)
                {
                    var bar = new RectangleF(x, underlineY, tw + tabPadX * 2, underlineH);
                    g.FillPath(Theme.Brush(Theme.Accent.Base), Theme.RoundedPath(bar, 1f));

                    if (Focused)
                    {
                        var focusRect = new RectangleF(x + 2, 3, rect.Width - 4, Math.Max(4, Height - underlineH - 5));
                        g.DrawPath(Theme.Pen(Color.FromArgb(102, Theme.Line.Focus)),
                            Theme.RoundedPath(focusRect, Theme.Scale(this, 4)));
                    }
                }

                TextRenderer.DrawText(g, page.Title, font,
                    new Rectangle(rect.X, 0, rect.Width, Height - underlineH), ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                x += rect.Width + tabGap;
            }
        }

        private int HitTest(int px)
        {
            int tabGap = Theme.Scale(this, TabGap);
            int tabPadX = Theme.Scale(this, TabPadX);
            int x = Theme.Scale(this, StripPadLeft);
            for (int i = 0; i < _owner._pages.Count; i++)
            {
                int w = _owner.MeasureTitle(i) + tabPadX * 2;
                if (px >= x && px < x + w) return i;
                x += w + tabGap;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.X);
            if (h == _hover) return;
            _hover = h;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0) return;
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.X);
            if (i >= 0) _owner.SelectedIndex = i;
        }

        /// <summary>左右方向键切页（标签条获得焦点时）。</summary>
        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) _owner.SelectedIndex = Math.Max(0, _owner._selected - 1);
            else if (e.KeyCode == Keys.Right) _owner.SelectedIndex = Math.Min(_owner._pages.Count - 1, _owner._selected + 1);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }
    }

    // ============================================================
    //  页管理
    // ============================================================

    /// <summary>标签页数量。</summary>
    public int PageCount => _pages.Count;

    /// <summary>当前选中页下标；无页时为 -1。</summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_pages.Count == 0) return;
            int v = Math.Clamp(value, 0, _pages.Count - 1);
            if (v == _selected) return;
            _selected = v;
            EnsurePageCreated(v);
            ShowSelectedPage();
            _strip.Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>追加一页（立即创建内容控件）。</summary>
    public void AddPage(string title, Control content)
    {
        var page = new Page { Title = title, Content = content };
        _pages.Add(page);

        content.Dock = DockStyle.Fill;
        content.Visible = false;
        _content.Controls.Add(content);

        if (_selected < 0) { _selected = 0; ShowSelectedPage(); }
        _strip.Invalidate();
    }

    /// <summary>追加一页（首次切换到该页或显式获取时再延迟创建内容控件）。</summary>
    public void AddLazyPage(string title, Func<Control> factory, Action<Control>? onCreated = null)
    {
        var page = new Page { Title = title, Factory = factory, OnCreated = onCreated };
        _pages.Add(page);

        if (_selected < 0)
        {
            _selected = 0;
            EnsurePageCreated(0);
            ShowSelectedPage();
        }
        _strip.Invalidate();
    }

    /// <summary>取某页内容控件；若是延迟页则按需创建。</summary>
    public Control? GetOrCreatePage(int index)
    {
        if (index < 0 || index >= _pages.Count) return null;
        EnsurePageCreated(index);
        return _pages[index].Content;
    }

    /// <summary>所有已创建的页面内容控件。</summary>
    public IEnumerable<Control> CreatedPages
    {
        get
        {
            foreach (var p in _pages)
                if (p.Content != null) yield return p.Content;
        }
    }

    private void EnsurePageCreated(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        var page = _pages[index];
        if (page.Content != null || page.Factory == null) return;

        var c = page.Factory();
        page.Content = c;
        page.Factory = null;
        c.Dock = DockStyle.Fill;
        c.Visible = false;
        _content.Controls.Add(c);
        page.OnCreated?.Invoke(c);
        page.OnCreated = null;
    }

    /// <summary>取某页的标题。</summary>
    public string PageTitle(int index) =>
        index >= 0 && index < _pages.Count ? _pages[index].Title : "";

    /// <summary>
    /// 改某页标题（键码选择器用它把匹配数写进标签，如「基本键 (3)」）。
    /// 会作废该页缓存的文字宽度。
    /// </summary>
    public void SetPageTitle(int index, string title)
    {
        if (index < 0 || index >= _pages.Count) return;
        var page = _pages[index];
        if (page.Title == title) return;
        page.Title = title;
        page.TextWidth = -1;
        _strip.Invalidate();
    }

    /// <summary>显示选中页、隐藏其它页。</summary>
    private void ShowSelectedPage()
    {
        for (int i = 0; i < _pages.Count; i++)
            if (_pages[i].Content != null)
                _pages[i].Content!.Visible = i == _selected;
    }

    /// <summary>标签文字宽度，按需重算并缓存。</summary>
    internal int MeasureTitle(int index)
    {
        var page = _pages[index];
        if (page.TextWidth < 0)
        {
            // 选中态用加粗字体，宽度会略大；取两种字体的较大值，
            // 避免切换选中时标签左右抖动。
            int normal, bold;
            if (_strip.IsHandleCreated && !_strip.IsDisposed)
            {
                using var g = _strip.CreateGraphics();
                normal = TextRenderer.MeasureText(g, page.Title, Theme.Fonts.Body).Width;
                bold = TextRenderer.MeasureText(g, page.Title, Theme.Fonts.BodyBold).Width;
            }
            else
            {
                normal = TextRenderer.MeasureText(page.Title, Theme.Fonts.Body).Width;
                bold = TextRenderer.MeasureText(page.Title, Theme.Fonts.BodyBold).Width;
            }
            page.TextWidth = Math.Max(normal, bold);
        }
        return page.TextWidth;
    }

    /// <summary>标签条本身（供需要精确定位的场景）。</summary>
    internal Control StripControl => _strip;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _strip.Height = Theme.Scale(this, Theme.Metrics.TabStripHeight);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        foreach (var p in _pages) p.TextWidth = -1;
        _strip.Height = Theme.Scale(this, Theme.Metrics.TabStripHeight);
        _strip.Invalidate();
    }
}
