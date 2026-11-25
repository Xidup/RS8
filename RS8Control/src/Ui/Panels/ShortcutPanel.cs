using System.ComponentModel;
using RS8Control.Model;
using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 「快捷键速查」页：上半部分用键盘配列图展示 Fn 层每个键做什么（主标签 = 层 0 的物理键名，
/// 副标签 = Fn 功能中文名，按功能分组着色），下半部分是说明书原文的两张表格。
///
/// 数据来源分两种，顶栏会明确标出来：
///   有线连接且读到层数据 → 「来自键盘（第 n 层）」，配列图用实机数据；
///   未连接 / 读取失败     → 「出厂默认（离线）」，配列图退化成只有物理键名的底图，
///                           Fn 功能以下方说明书表格为准（绝不白屏）。
/// </summary>
public sealed class ShortcutPanel : UserControl
{
    private static readonly Color ColOk = Theme.State.Ok;
    private static readonly Color ColWarn = Theme.State.Warn;
    private static readonly Color ColMuted = Theme.Ink.Muted;

    private static readonly int[] FnBaseWidths = { 88, 404, 100, 108 };
    private static readonly int[] LightBaseWidths = { 120, 250 };

    private readonly DeviceSession _session;
    private readonly ShortcutGuide? _guide;

    private readonly Panel _dot = new();
    private readonly Label _source = new();
    private readonly Label _hint = new();
    private readonly FlatButton _reload = new("重新读取");

    private readonly Panel _host = new BufferedPanel();
    private readonly CardPanel _layoutCard = new();
    private readonly Label _layoutTitle = CardTitle("Fn 层配列图 — 每个键在 Fn 层上的功能");
    private readonly FnKeyCanvas _canvas = new();
    private readonly CardPanel _fnCard = new();
    private readonly Label _fnTitle = CardTitle("说明书原文 · 多媒体快捷组合键");
    private readonly CardPanel _lightCard = new();
    private readonly Label _lightTitle = CardTitle("说明书原文 · 灯光控制");
    private readonly Label _lightNote = new();
    private readonly Label _discrepancy = new();

    // 顶栏排布用（见 LayoutTopBar）
    private Panel? _bar;
    private FlowLayoutPanel? _barRight;

    private InfoTable _fnTable = new();
    private InfoTable _lightTable = new();

    private Color _dotColor = ColWarn;
    private bool _reading;
    private bool _loaded;

    /// <summary>已登记一次重排请求（Resize 合并窗口）。</summary>
    private bool _layoutPending;

    /// <summary>
    /// 正在执行重排。重排本身会改子控件尺寸，可能让 _host 的滚动条出现/消失并再次触发 Resize，
    /// 那些 Resize 一律丢弃 —— 否则「改尺寸 → 滚动条切换 → 又改尺寸」会同步递归下去，这是卡顿主因。
    /// </summary>
    private bool _layingOut;

    /// <summary>换行高度缓存：键 = (文本, 可用宽度, 字号)。拖动窗口时宽度不变就不必重跑 MeasureText。</summary>
    private readonly Dictionary<(string Text, int Width, float Size), int> _wrapCache = new();

    public ShortcutPanel(DeviceSession session)
    {
        _session = session;
        try { _guide = ShortcutGuide.AutoLoad(); }
        catch { _guide = null; }

        BackColor = Theme.Bg.App;

        // ---------- 顶栏：数据来源 ----------
        // 主行 = 圆点 + 来源文字 + 「重新读取」；提示行 = 补充说明。两行整体居中，
        // 主行内三个元素严格同中心，圆点单独承载状态色。
        var bar = new Panel { Dock = DockStyle.Top, Height = UiLayout.BarHeightFor(this), BackColor = Theme.Bg.Surface };
        _dot.BackColor = Theme.Bg.Surface;
        _dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(Theme.Brush(_dotColor), 0, 0, _dot.Width - 1, _dot.Height - 1);
        };
        bar.Controls.Add(_dot);

        _source.AutoSize = true;
        _source.Font = Theme.Fonts.Emphasis;
        _source.ForeColor = Theme.Ink.Primary;
        bar.Controls.Add(_source);

        _hint.AutoSize = true;
        _hint.ForeColor = ColMuted;
        bar.Controls.Add(_hint);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Bg.Surface,
        };
        _reload.Click += (_, _) => _ = ReadAsync();
        right.Controls.Add(_reload);
        bar.Controls.Add(right);
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });
        _bar = bar;
        _barRight = right;

        // ---------- 滚动容器 ----------
        _host.Dock = DockStyle.Fill;
        _host.AutoScroll = true;
        _host.BackColor = Theme.Bg.App;
        _host.Resize += (_, _) => RequestLayout();

        BuildLayoutCard();
        BuildManualCards();
        BuildDiscrepancyLine();

        _host.Controls.Add(_layoutCard);
        _host.Controls.Add(_fnCard);
        _host.Controls.Add(_lightCard);
        _host.Controls.Add(_discrepancy);

        Controls.Add(_host);
        Controls.Add(bar);

        ApplyGuideStatic();
        ShowOffline("尚未读取键盘。");
        LayoutTopBar();   // 文字填好之后再算行高（AutoSize 标签的 Height 这时才是最终值）

        // 切到本页时自动读一次（TabControl 切换会改变子控件 Visible）
        VisibleChanged += (_, _) =>
        {
            if (Visible && !_loaded && !_reading) _ = ReadAsync();
        };
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _wrapCache.Clear();
        LayoutTopBar();
        RequestLayout();
    }

    // ============================================================
    //  构建界面
    // ============================================================

    /// <summary>
    /// 顶栏排布：主行（圆点 + 来源文字 + 重新读取）与提示行两行整体居中，
    /// 主行内三个控件严格同中心，提示行单独居中。
    /// </summary>
    private void LayoutTopBar()
    {
        if (_bar == null || _barRight == null) return;

        _bar.Height = UiLayout.BarHeightFor(this);
        _barRight.Padding = new Padding(0, 0, Theme.Scale(this, 10), 0);
        int dotSize = Theme.Scale(this, 12);
        if (_dot.Size.Width != dotSize || _dot.Size.Height != dotSize)
            _dot.Size = new Size(dotSize, dotSize);

        var rows = UiLayout.TwoRowBar(_bar.Height, UiLayout.RowHeight(_source, _dot, _reload),
                                      _hint.PreferredSize.Height, Theme.Scale(this, 2));
        _dot.Left = Theme.Scale(this, 16);
        _dot.Top = UiLayout.CenterInRow(_dot, rows.MainTop, rows.MainHeight);
        int textLeft = Theme.Scale(this, 36);
        _source.Left = textLeft;
        _source.Top = UiLayout.CenterInRow(_source, rows.MainTop, rows.MainHeight);
        _hint.Left = textLeft;
        _hint.Top = UiLayout.CenterInRow(_hint, rows.HintTop, rows.HintHeight);
        UiLayout.CenterFlowRow(_barRight, rows.MainTop, rows.MainHeight);
    }

    private void BuildLayoutCard()
    {
        _layoutCard.Controls.Add(_layoutTitle);

        _canvas.BackColor = Theme.Bg.Surface;
        _canvas.Names = _guide?.KeycodeNames ?? new Dictionary<ushort, ShortcutEntry>();
        _canvas.Cols = _session.Definition?.Cols ?? 16;
        _canvas.KeyLayout = _session.Definition?.Keys ?? new List<LayoutKey>();
        _layoutCard.Controls.Add(_canvas);
    }

    private void BuildManualCards()
    {
        // ---- 多媒体快捷组合键（说明书原文） ----
        _fnCard.Controls.Add(_fnTitle);
        _fnTable = BuildTable(
            new[] { "按键", "Windows 系统", "MAC 系统", "备注" },
            FnBaseWidths,
            (_guide?.ManualFnTable ?? new List<ManualFnRow>())
                .Select(r => new[] { r.Keys, r.Win, r.Mac, r.Note }).ToList());
        _fnCard.Controls.Add(_fnTable);

        // ---- 灯光控制（说明书原文） ----
        _lightCard.Controls.Add(_lightTitle);
        _lightTable = BuildTable(
            new[] { "按键", "功能" },
            LightBaseWidths,
            (_guide?.ManualLightTable ?? new List<ManualLightRow>())
                .Select(r => new[] { r.Keys, r.Desc }).ToList());
        _lightCard.Controls.Add(_lightTable);

        _lightNote.AutoSize = false;
        _lightNote.ForeColor = ColMuted;
        _lightNote.Text = _guide?.ManualLightNote ?? "";
        _lightCard.Controls.Add(_lightNote);
    }

    private void BuildDiscrepancyLine()
    {
        _discrepancy.AutoSize = false;
        _discrepancy.ForeColor = ColWarn;
        _discrepancy.Font = Theme.Fonts.Caption;
        _discrepancy.Text = "说明书部分灯光按键说明与实机固件存在差异，上方配列图以键盘实际数据为准。";
    }

    private static Label CardTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Fonts.Title,
        ForeColor = Theme.Ink.Primary,
    };

    /// <summary>
    /// 建一张只读信息表。整张表是一个自绘控件（<see cref="InfoTable"/>），
    /// 不再用 TableLayoutPanel + 每个单元格一个 Label —— 两张表原本合计约 190 个控件。
    /// </summary>
    private static InfoTable BuildTable(string[] headers, int[] widths, List<string[]> rows)
    {
        var table = new InfoTable();
        table.SetData(headers, widths, rows);
        return table;
    }

    /// <summary>配列图画布的高度（96 DPI 基准）。够高才能让中文副标签排到两行以上。</summary>
    private const int CanvasHeight = 400;

    /// <summary>卡片外边距（左右各 12，96 DPI 基准）。</summary>
    private const int HostMargin = 12;

    /// <summary>卡片内边距（96 DPI 基准）。</summary>
    private const int CardPadding = 18;

    /// <summary>两表并排时的宽度配比：左（多媒体表）62% / 右（灯光表）38%。</summary>
    private const double FnShare = 0.62;

    /// <summary>内容最小宽度的兜底下限（96 DPI 基准）；真实下限还会参考表格最小宽度。</summary>
    private const int MinContentWidth = 640;

    /// <summary>
    /// 把重排请求合并到消息循环。拖动窗口时 Resize 每秒触发几十次，
    /// 这里只登记一个标志并用 BeginInvoke 排到消息循环，保证**每个消息循环周期最多真正重排一次**。
    /// 重排期间到达的 Resize 直接丢弃（见 <see cref="_layingOut"/>）。
    /// </summary>
    private void RequestLayout()
    {
        if (IsDisposed) return;
        if (_layingOut || _layoutPending) return;   // 已有请求在排队 → 合并掉

        if (!IsHandleCreated)
        {
            // 构造期还没有句柄，BeginInvoke 会抛异常，直接同步做一次。
            // 必须**同样打上 _layingOut 标记**：LayoutCards 末尾的 ResumeLayout(true)
            // 会让 _host 重新布局，可能再触发 Resize → 又回到这里。
            // 少了这个标记就会重入 LayoutCards，形成布局递归（表现为 DrawToBitmap 卡死）。
            _layingOut = true;
            try { LayoutCards(); }
            finally { _layingOut = false; }
            return;
        }

        _layoutPending = true;
        try
        {
            BeginInvoke(new Action(FlushLayout));
        }
        catch (InvalidOperationException) { _layoutPending = false; }   // 句柄在排队后被销毁（ObjectDisposedException 也是它的子类）
    }

    private void FlushLayout()
    {
        if (IsDisposed) { _layoutPending = false; return; }
        _layoutPending = false;
        _layingOut = true;
        try { LayoutCards(); }
        finally { _layingOut = false; }
    }

    /// <summary>
    /// 卡片位置全部手算：FlowLayoutPanel 在「两栏并排 + 各自自适应高度」这种布局上不好用。
    /// 宽度先定，再算换行后的高度。整段重排期间挂起布局，最后只做一次布局，而不是每改一个子控件就重排一次。
    /// </summary>
    private void LayoutCards()
    {
        _host.SuspendLayout();
        try { LayoutCardsCore(); }
        finally { _host.ResumeLayout(true); }
    }

    private int MinTableWidth(int[] baseWidths)
    {
        int sum = 0;
        for (int i = 0; i < baseWidths.Length; i++)
            sum += Theme.Scale(this, baseWidths[i]);
        return sum;
    }

    private void LayoutCardsCore()
    {
        int margin = Theme.Scale(this, HostMargin);
        int pad = Theme.Scale(this, CardPadding);
        int titleTop = Theme.Scale(this, 14);
        int tableTop = Theme.Scale(this, 46);
        int canvasPadX = Theme.Scale(this, 14);
        int canvasH = Theme.Scale(this, CanvasHeight);
        int gap = Theme.Scale(this, 14);
        int noteGap = Theme.Scale(this, 8);
        int bottomPad = Theme.Scale(this, 16);
        int tailGap = Theme.Scale(this, 12);

        int sx = _host.AutoScrollPosition.X;
        int sy = _host.AutoScrollPosition.Y;

        // 可用宽度：扣掉左右外边距与竖向滚动条宽度。滚动条宽度无条件预留，
        // 这样滚动条出现/消失时算出来的内容宽度不会跳变，也就不会来回触发重排。
        int avail = _host.ClientSize.Width - margin * 2 - SystemInformation.VerticalScrollBarWidth;

        // 最小总宽按基准列宽算（而不是已被拉伸过的 NaturalSize.Width），避免窗口缩窄时无法回落。
        int fnMinW = MinTableWidth(FnBaseWidths);
        int lightMinW = MinTableWidth(LightBaseWidths);

        int minW = Math.Max(Theme.Scale(this, MinContentWidth), fnMinW + pad * 2);
        int w = Math.Max(minW, avail);

        _layoutCard.Width = w;
        _layoutCard.Location = new Point(margin + sx, margin + sy);
        _layoutTitle.Location = new Point(pad, titleTop);
        _canvas.Location = new Point(canvasPadX, tableTop);
        _canvas.Size = new Size(Math.Max(100, w - canvasPadX * 2), canvasH);
        _layoutCard.Height = tableTop + canvasH + canvasPadX;

        int fnNeed = fnMinW + pad * 2;
        int lightNeed = lightMinW + pad * 2;
        bool sideBySide = (w - gap) * FnShare >= fnNeed && (w - gap) * (1 - FnShare) >= lightNeed;

        if (sideBySide)
        {
            int leftW = (int)((w - gap) * FnShare);
            _fnCard.Width = leftW;
            _lightCard.Width = w - gap - leftW;
        }
        else
        {
            // 窄窗口：两表上下堆叠，各自占满整行宽度
            _fnCard.Width = w;
            _lightCard.Width = w;
        }

        // 先按卡片可用内宽让表格按比例撑满，再读取重排后的 NaturalSize，消除右侧空白
        _fnTitle.Location = new Point(pad, titleTop);
        _lightTitle.Location = new Point(pad, titleTop);
        _fnTable.Location = new Point(pad, tableTop);
        _lightTable.Location = new Point(pad, tableTop);
        _fnTable.SetTargetWidth(Math.Max(1, _fnCard.Width - pad * 2));
        _lightTable.SetTargetWidth(Math.Max(1, _lightCard.Width - pad * 2));

        // 宽度定下来之后，表格与注释的换行高度才算得准
        _lightNote.Location = new Point(pad, tableTop + TableSize(_lightTable).Height + noteGap);
        _lightNote.Width = Math.Max(40, _lightCard.Width - pad * 2);
        _lightNote.Height = WrappedHeight(_lightNote.Text, _lightNote.Font, _lightNote.Width);

        _fnCard.Height = tableTop + TableSize(_fnTable).Height + bottomPad;
        _lightCard.Height = _lightNote.Bottom + bottomPad;

        int y = _layoutCard.Bottom + gap;
        _fnCard.Location = new Point(margin + sx, y);
        _lightCard.Location = sideBySide
            ? new Point(margin + sx + _fnCard.Width + gap, y)
            : new Point(margin + sx, y + _fnCard.Height + gap);

        y += (sideBySide ? Math.Max(_fnCard.Height, _lightCard.Height)
                         : _fnCard.Height + gap + _lightCard.Height) + tailGap;

        _discrepancy.Location = new Point(margin + sx, y);
        _discrepancy.Width = w;
        _discrepancy.Height = WrappedHeight(_discrepancy.Text, _discrepancy.Font, w);
    }

    /// <summary>表格自然尺寸（列宽在 <see cref="InfoTable.SetTargetWidth"/> 后已更新）。</summary>
    private static Size TableSize(InfoTable table) => table.NaturalSize;

    private int WrappedHeight(string text, Font font, int width)
    {
        var key = (text, width, font.Size);
        if (_wrapCache.TryGetValue(key, out int cached)) return cached;

        int h = TextRenderer.MeasureText(text, font, new Size(Math.Max(40, width), 0),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height
            + Theme.Scale(this, 4);

        // 拖动窗口时宽度一直在变，缓存里会有很多一次性条目；到上限就整体清掉，避免无界增长
        if (_wrapCache.Count > 64) _wrapCache.Clear();
        _wrapCache[key] = h;
        return h;
    }

    /// <summary>把说明书里与界面无关的静态部分填好（表格在构造时已建好，这里只管离线兜底底图）。</summary>
    private void ApplyGuideStatic()
    {
        _canvas.Offline = true;
        _canvas.Physical = Rs8Defaults.FallbackPhysical;
        _canvas.FnKeycodes = Array.Empty<ushort>();
        _canvas.Layer0Keycodes = Array.Empty<ushort>();
        _canvas.CustomKeycodes = _session.Definition?.CustomKeycodes;
        _canvas.OfflineFn = BuildOfflineFnMap(_guide);
    }

    // ============================================================
    //  读取
    // ============================================================

    public void OnSessionChanged()
    {
        _loaded = false;
        _reading = false;
        if (_session.Ready && Visible) _ = ReadAsync();
        else ShowOffline(_session.Ready ? "切到本页时自动读取。" : "键盘未就绪（2.4G 只读模式或未连接）。");
    }

    private async Task ReadAsync()
    {
        if (_reading) return;
        _reading = true;
        _reload.Enabled = false;

        if (!_session.Ready)
        {
            _reading = false;
            _reload.Enabled = true;
            ShowOffline("键盘未就绪（2.4G 只读模式或未连接）。");
            return;
        }

        _hint.ForeColor = ColMuted;
        _hint.Text = "正在从键盘读取第 0 层与 Fn 层…";

        try
        {
            var result = await Task.Run(() =>
            {
                var l0 = _session.ReadLayer(0);
                if (l0 == null) return ((ushort[]?, ushort[]?, int))default;
                int fnLayer = DetectFnLayer(l0);
                var fn = _session.ReadLayer(fnLayer);
                return (l0, fn, fnLayer);
            }).ConfigureAwait(true);

            if (IsDisposed) return;

            if (result.Item1 == null || result.Item2 == null)
            {
                ShowOffline("读取失败（键盘可能休眠，按任意键唤醒后点「重新读取」）。");
                return;
            }

            ApplyDeviceLayers(result.Item1, result.Item2, result.Item3);
            _loaded = true;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) ShowOffline("读取出错：" + ex.Message);
        }
        finally
        {
            _reading = false;
            if (!IsDisposed) _reload.Enabled = true;
        }
    }

    /// <summary>
    /// Fn 层号不硬编码：先在层 0 里找键码为 MO(n)（0x5220 + n）的键，用它的 n；
    /// 找不到再退回说明书里的 fnLayerIndex。
    /// </summary>
    private int DetectFnLayer(ushort[] layer0)
    {
        foreach (ushort kc in layer0)
            if (kc >= 0x5220 && kc < 0x5240) return kc - 0x5220;
        return _guide?.FnLayerIndex ?? 2;
    }

    private void ApplyDeviceLayers(ushort[] layer0, ushort[] fn, int fnLayer)
    {
        _canvas.Offline = false;
        _canvas.Physical = PhysicalLabels(layer0, _session.Definition?.CustomKeycodes);
        _canvas.Layer0Keycodes = layer0;
        _canvas.CustomKeycodes = _session.Definition?.CustomKeycodes;
        _canvas.FnKeycodes = fn;

        _dotColor = ColOk;
        _dot.Invalidate();
        _source.Text = $"数据来源：来自键盘（第 {fnLayer} 层）";
        _hint.ForeColor = ColMuted;
        _hint.Text = $"层 0 提供物理键名，第 {fnLayer} 层提供 Fn 功能；共 {_canvas.KeyLayout.Count} 个按键。";

        LayoutCards();
        _canvas.Invalidate();
    }

    private void ShowOffline(string why)
    {
        _canvas.Offline = true;
        _canvas.FnKeycodes = Array.Empty<ushort>();
        _canvas.Layer0Keycodes = Array.Empty<ushort>();
        _dotColor = ColWarn;
        _dot.Invalidate();
        _source.Text = "数据来源：出厂默认（离线）";
        _hint.ForeColor = ColMuted;
        _hint.Text = why + " 配列图仅显示物理键名，Fn 功能以下方说明书表格为准。";

        LayoutCards();
        _canvas.Invalidate();
    }

    // ============================================================
    //  键名解析
    // ============================================================

    /// <summary>层 0 的键码 → 物理键名；索引 = row * cols + col。</summary>
    public static string[] PhysicalLabels(ushort[] layer0, IReadOnlyList<string>? custom)
    {
        var labels = new string[layer0.Length];
        for (int i = 0; i < layer0.Length; i++) labels[i] = Keycodes.Describe(layer0[i], custom);
        return labels;
    }

    /// <summary>
    /// 离线兜底：从说明书的「多媒体快捷组合键」表里挑出「按键」列就是单个键名的行
    /// （F1–F12、End、Win、BackSpace、N），直接用作配列图的副标签。
    /// 说明书写成整句的行（如 1/2/3、4）放不进键帽，留给下方表格原文。
    /// </summary>
    private static Dictionary<string, string> BuildOfflineFnMap(ShortcutGuide? guide)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (guide == null) return map;

        // 说明书用的是印刷写法，配列图上的键名来自 QMK 键码表，这里显式对齐两处差异
        var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Win"] = "左 Win",
            ["BackSpace"] = "Backspace",
        };
        // 过长说明改用简短键帽标签（完整说明保留在下方表格）
        var shortLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["N"] = "全/六键切换",
            ["Win"] = "锁 Win 键",
            ["BackSpace"] = "电池电量",
        };

        foreach (var row in guide.ManualFnTable)
        {
            string key = row.Keys.Trim();
            if (key.Length == 0 || key.Contains('/') || key.Contains(' ')) continue;
            string phys = alias.TryGetValue(key, out var mapped) ? mapped : key;
            string label = shortLabel.TryGetValue(key, out var concise) ? concise : row.Win;
            map[phys] = label;
        }
        return map;
    }
}

/// <summary>
/// 配列图控件：位置完全按 <see cref="LayoutKey"/> 的 X/Y/W/H 算（与键位映射页同一套思路），
/// 每个键上面一行是物理键名，下面一行是 Fn 功能中文名，键帽底色按功能分组。
/// </summary>
public sealed class FnKeyCanvas : Control
{
    private const int Pad = 18;
    private const int LegendHeight = 30;

    /// <summary>功能分组 → 键帽配色（底色 / 边框 / 文字）。分组名取自 shortcuts.json 的 group 字段。</summary>
    internal static readonly Dictionary<string, (Color Fill, Color Border, Color Text)> GroupColors = new()
    {
        ["多媒体"] = (Color.FromArgb(233, 241, 255), Theme.Accent.Base, Color.FromArgb(28, 62, 140)),
        ["背光"] = (Color.FromArgb(255, 244, 224), Color.FromArgb(201, 122, 0), Color.FromArgb(122, 74, 0)),
        ["Logo 灯"] = (Color.FromArgb(243, 235, 255), Color.FromArgb(123, 79, 209), Color.FromArgb(74, 42, 138)),
        ["侧灯"] = (Color.FromArgb(230, 247, 244), Theme.State.Ok, Color.FromArgb(11, 84, 58)),
        ["环绕灯"] = (Color.FromArgb(230, 244, 251), Color.FromArgb(31, 127, 168), Color.FromArgb(19, 76, 101)),
        ["模式"] = (Color.FromArgb(237, 235, 255), Color.FromArgb(75, 69, 198), Color.FromArgb(45, 41, 124)),
        ["系统"] = (Color.FromArgb(253, 236, 234), Theme.State.Danger, Color.FromArgb(122, 34, 25)),
        ["层"] = (Color.FromArgb(238, 241, 246), Color.FromArgb(90, 100, 120), Theme.Ink.Body),
        ["自定义"] = (Color.FromArgb(235, 240, 248), Color.FromArgb(84, 104, 140), Color.FromArgb(42, 58, 86)),
    };

    /// <summary>图例里「无功能」那一项的配色，也是所有没有 Fn 功能的键的配色。</summary>
    internal static readonly (Color Fill, Color Border, Color Text) NoFnColor =
        (Color.FromArgb(247, 248, 250), Theme.Line.Subtle, Theme.Ink.Disabled);

    private List<LayoutKey> _layout = new();
    private readonly List<LayoutKey> _shown = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Cols { get; set; } = 16;

    /// <summary>索引 = row * Cols + col 的物理键名（来自层 0）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Physical { get; set; } = Array.Empty<string>();

    /// <summary>索引 = row * Cols + col 的 Fn 层键码。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ushort[] FnKeycodes { get; set; } = Array.Empty<ushort>();

    /// <summary>索引 = row * Cols + col 的层 0 键码（用于过滤层 2 直接复制的基础键）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ushort[] Layer0Keycodes { get; set; } = Array.Empty<ushort>();

    /// <summary>自定义键码名称列表（来自 VIA 定义）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string>? CustomKeycodes { get; set; }

    /// <summary>键码 → 中文名 + 分组（shortcuts.json 的 keycodeNames）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyDictionary<ushort, ShortcutEntry> Names { get; set; } = new Dictionary<ushort, ShortcutEntry>();

    /// <summary>离线模式：不画 Fn 副标签，改用 <see cref="OfflineFn"/> 里的说明书内容。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Offline { get; set; } = true;

    /// <summary>离线时按物理键名查的 Fn 功能名（来自说明书表格）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Dictionary<string, string> OfflineFn { get; set; } = new();

    public int DrawnCount => _shown.Count;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<LayoutKey> KeyLayout
    {
        get => _layout;
        set
        {
            _layout = value;
            _shown.Clear();
            var seen = new HashSet<(int, int)>();
            foreach (var k in value)
                if (seen.Add((k.Row, k.Col))) _shown.Add(k);
        }
    }

    public FnKeyCanvas()
    {
        DoubleBuffered = true;
        BackColor = Theme.Bg.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>一个键的 Fn 功能中文名；没有功能返回空串。</summary>
    public string FnLabelOf(int index) => Offline
        ? (index >= 0 && index < Physical.Length && OfflineFn.TryGetValue(Physical[index], out var s) ? s : "")
        : Resolve(index, FnKeycodes, Names, CustomKeycodes, Layer0Keycodes).Fn;

    /// <summary>一个键的功能分组；没有功能返回空串。</summary>
    public string GroupOf(int index) => Offline
        ? ""
        : Resolve(index, FnKeycodes, Names, CustomKeycodes, Layer0Keycodes).Group;

    /// <summary>
    /// 配列图与自检共用的解析规则：
    /// 优先查 keycodeNames；若未连接或仅查字典无果，在连接状态下对于用户自定义映射到 Fn 层的
    /// 扩展/复合键码（如宏、组合键、媒体键或与层 0 不同的改键）归入「自定义」分组。
    /// </summary>
    public static (string Fn, string Group) Resolve(
        int index,
        ushort[] fnKeycodes,
        IReadOnlyDictionary<ushort, ShortcutEntry> names,
        IReadOnlyList<string>? customKeycodes = null,
        ushort[]? layer0Keycodes = null)
    {
        if (index < 0 || index >= fnKeycodes.Length) return ("", "");
        ushort kc = fnKeycodes[index];
        if (names.TryGetValue(kc, out var e)) return (e.Zh, e.Group);

        if (kc == Keycodes.KC_NO || kc == Keycodes.KC_TRNS) return ("", "");
        if (layer0Keycodes != null && index < layer0Keycodes.Length && kc == layer0Keycodes[index])
            return ("", "");

        bool isCustomOrExtended = kc >= 0x0100 || (kc >= 0x00A5 && kc <= 0x00DF);
        if (!isCustomOrExtended && (layer0Keycodes == null || index >= layer0Keycodes.Length))
            return ("", "");

        string desc = Keycodes.Describe(kc, customKeycodes);
        return string.IsNullOrEmpty(desc) || desc == "—" ? ("", "") : (desc, "自定义");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (_shown.Count == 0)
        {
            TextRenderer.DrawText(g, "等待读取键位…", Theme.Fonts.Emphasis,
                new Rectangle(0, 0, Width, Height), Theme.Ink.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int pad = Theme.Scale(this, Pad);
        int legendH = Theme.Scale(this, LegendHeight);
        float keyGap = Theme.ScaleF(this, 3f);
        float maxRadius = Theme.ScaleF(this, 6f);

        double maxX = _shown.Max(k => k.X + k.W);
        double maxY = _shown.Max(k => k.Y + k.H);
        int availH = Math.Max(40, Height - pad * 2 - legendH);
        double scale = Math.Min(Math.Max(10, Width - pad * 2) / Math.Max(1, maxX), availH / Math.Max(1, maxY));
        scale = Math.Min(scale, Theme.ScaleF(this, 64f));
        double ox = pad + (Width - pad * 2 - maxX * scale) / 2;
        double oy = pad + (availH - maxY * scale) / 2;

        float dpiScale = Math.Max(1f, DeviceDpi / 96f);
        var nameFont = Theme.FontAt(Theme.Fonts.KeycapFamily, Math.Max(6.5f, (float)(scale * 0.15 / dpiScale)));
        var fnFont = Theme.FontAt(Theme.Fonts.KeycapFamily, Math.Max(6.0f, (float)(scale * 0.115 / dpiScale)));

        foreach (var k in _shown)
        {
            var r = new RectangleF(
                (float)(ox + k.X * scale), (float)(oy + k.Y * scale),
                (float)(k.W * scale) - keyGap, (float)(k.H * scale) - keyGap);
            if (r.Width <= 1f || r.Height <= 1f) continue;

            int idx = k.Row * Cols + k.Col;
            string phys = idx >= 0 && idx < Physical.Length ? Physical[idx] : "";

            string fn;
            string group;
            if (Offline)
            {
                fn = idx >= 0 && idx < Physical.Length && OfflineFn.TryGetValue(Physical[idx], out var s) ? s : "";
                group = "";
            }
            else
            {
                (fn, group) = Resolve(idx, FnKeycodes, Names, CustomKeycodes, Layer0Keycodes);
            }

            var col = group.Length > 0 && GroupColors.TryGetValue(group, out var c) ? c : NoFnColor;
            float radius = Math.Min(maxRadius, r.Height / 5f);

            KeycapRenderer.DrawCap(g, r, radius, col.Fill, col.Border, 1f, drawBottomEdge: false);

            if (fn.Length == 0)
            {
                KeycapRenderer.DrawCenterText(g, phys, nameFont,
                    new RectangleF(r.X + 2f, r.Y + 2f, r.Width - 4f, r.Height - 4f), NoFnColor.Text);
                continue;
            }

            KeycapRenderer.DrawFnKeyText(g, phys, fn, nameFont, fnFont, r, Theme.Ink.Body, col.Text);
        }

        DrawLegend(g, Theme.Fonts.Caption, pad, legendH);
    }

    /// <summary>图例：每个功能分组一个色块 + 名称，末尾补上「无功能」。</summary>
    private void DrawLegend(Graphics g, Font font, int pad, int legendH)
    {
        int x = pad;
        int y = Height - legendH + Theme.Scale(this, 6);
        int limit = Width - pad;

        foreach (var kv in GroupColors)
        {
            var (fill, border, text) = kv.Value;
            x = DrawLegendItem(g, font, x, y, fill, border, text, kv.Key, limit);
            if (x >= limit) return;
        }

        DrawLegendItem(g, font, x, y, NoFnColor.Fill, NoFnColor.Border, NoFnColor.Text, "无功能", limit);
    }

    private int DrawLegendItem(Graphics g, Font font, int x, int y,
        Color fill, Color border, Color text, string label, int limit)
    {
        int swatchSize = Theme.Scale(this, 12);
        int textGap = Theme.Scale(this, 5);
        int itemGap = Theme.Scale(this, 10);
        Size textSize = TextRenderer.MeasureText(g, label, font, Size.Empty, TextFormatFlags.NoPrefix);
        int totalW = swatchSize + textGap + textSize.Width;
        if (x + totalW > limit && x > Theme.Scale(this, Pad)) return limit;

        int textH = Math.Max(swatchSize, textSize.Height);
        var swatch = new RectangleF(x, y + (textH - swatchSize) / 2f, swatchSize, swatchSize);
        var path = Theme.RoundedPath(swatch, Theme.ScaleF(this, 3f));
        g.FillPath(Theme.Brush(fill), path);
        g.DrawPath(Theme.Pen(border), path);

        TextRenderer.DrawText(
            g,
            label,
            font,
            new Rectangle(x + swatchSize + textGap, y, textSize.Width + 2, textH),
            text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        return x + totalW + itemGap;
    }
}
