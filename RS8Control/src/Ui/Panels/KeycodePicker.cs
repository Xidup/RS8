using RS8Control.Model;
using RS8Control.Ui;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 图形化键码选择器：左侧分类标签 + 键盘样式的可点击键位图 + 搜索高亮。
/// 取代原来的列表框版本（<c>KeycodePickerForm</c>）。
/// </summary>
public sealed class KeycodePicker : Form
{
    private readonly List<KeycodeCategory> _categories;
    private readonly TabHost _tabs = new();
    private readonly FlatTextBox _search = new();
    private readonly Label _current = new();
    private readonly Label _matchInfo = new();
    private readonly List<PaletteCanvas> _canvases = new();

    /// <summary>选中的键码（点「确定」或双击某个键后生效）。</summary>
    public ushort Selected { get; private set; }

    public KeycodePicker(ushort current, IReadOnlyList<string>? customKeycodes, int layerCount)
    {
        _categories = KeycodePalette.Build(layerCount, customKeycodes);
        Selected = current;

        Text = "选择键码";
        // 基本键现在是完整 104 配列（22.5 单位宽），窗口相应放宽，否则 1u 键上的字全被省略号截掉
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Fonts.Body;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        ForeColor = Theme.Ink.Primary;

        // ---- 顶部：搜索 ----
        var top = new Panel { Dock = DockStyle.Top, Height = UiLayout.BarHeightFor(this), BackColor = Color.White };
        var searchCaption = new Label { Text = "搜索", AutoSize = true, ForeColor = Theme.Ink.Body };
        top.Controls.Add(searchCaption);
        _search.Width = Theme.Scale(this, 260);
        _search.PlaceholderText = "键名或键码，如 F1 / Enter / 层 / 0x003A";
        _search.TextChanged += (_, _) => ApplySearch();
        top.Controls.Add(_search);

        _matchInfo.AutoSize = true;
        _matchInfo.ForeColor = Theme.Ink.Muted;
        _matchInfo.Text = "点任一键即选中，双击可直接确定。";
        top.Controls.Add(_matchInfo);
        top.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        // 顶栏三个元素同一垂直中心
        searchCaption.Left = Theme.Scale(this, 12);
        searchCaption.Top = UiLayout.CenterInBar(searchCaption, top.Height);
        _search.Left = searchCaption.Left + searchCaption.PreferredSize.Width + Theme.Scale(this, 8);
        _search.Top = UiLayout.CenterInBar(_search, top.Height);
        _matchInfo.Left = _search.Right + Theme.Scale(this, 14);
        _matchInfo.Top = UiLayout.CenterInBar(_matchInfo, top.Height);

        // ---- 中部：分类标签 + 键位图 ----
        _tabs.Dock = DockStyle.Fill;
        foreach (var cat in _categories)
        {
            var canvas = new PaletteCanvas(cat) { Selected = current };
            canvas.KeyChosen += OnKeyChosen;
            canvas.KeyActivated += OnKeyActivated;
            canvas.ActionClicked += OnActionClicked;
            _canvases.Add(canvas);

            _tabs.AddPage(cat.Name, canvas);
        }
        _tabs.SelectedIndexChanged += (_, _) => UpdateCurrentLabel();

        // ---- 底部：当前选择 + 按钮 ----
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = UiLayout.BarHeightFor(this),
            BackColor = Theme.Bg.Surface,
            Padding = new Padding(0, 1, 0, 0),
        };
        bottom.Paint += (_, e) => e.Graphics.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, 0, bottom.Width, 0);
        bottom.Resize += (_, _) => bottom.Invalidate();

        _current.AutoSize = true;
        _current.Font = Theme.Fonts.Emphasis;
        bottom.Controls.Add(_current);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 0, Theme.Scale(this, 12), 0),
        };
        var clear = new FlatButton("清除");
        var ok = new FlatButton("确定", ButtonVariant.Primary);
        var cancel = new FlatButton("取消") { DialogResult = DialogResult.Cancel };
        clear.Click += (_, _) => Select(Keycodes.KC_NO);
        ok.Click += (_, _) => Accept();
        right.Controls.Add(clear);
        right.Controls.Add(ok);
        right.Controls.Add(cancel);
        bottom.Controls.Add(right);

        // 底栏：分隔线由 Padding.Top=1 留出，right 已从 y=1 起停靠，_current 坐标相对 bottom 客户区
        _current.Left = Theme.Scale(this, 14);
        _current.Top = UiLayout.CenterInRow(_current, UiLayout.Separator, bottom.Height - UiLayout.Separator);
        UiLayout.CenterFlowRow(right, 0, bottom.Height - UiLayout.Separator);

        Controls.Add(_tabs);
        Controls.Add(top);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;

        UpdateCurrentLabel();
    }

    private void OnKeyChosen(PaletteKey key) => Select(key.Code);

    /// <summary>「＋ 多键组合…」入口：弹小对话框，确定后把组合键码作为结果并直接关闭选择器。</summary>
    private void OnActionClicked(PaletteKey key)
    {
        if (key.Code != KeycodePalette.ComboActionCode) return;
        using var dlg = new ComboDialog(Selected);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Select(dlg.Selected);
        Accept();
    }

    private void OnKeyActivated(PaletteKey key)
    {
        Select(key.Code);
        Accept();
    }

    private void Select(ushort code)
    {
        Selected = code;
        foreach (var c in _canvases) c.Selected = code;
        UpdateCurrentLabel();
    }

    private void Accept()
    {
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>底部「当前选择」行：优先用调色板里的名字，其次回退到键码表。</summary>
    private void UpdateCurrentLabel()
    {
        string name = KeycodePalette.LabelOf(_categories, Selected)
                      ?? Keycodes.Describe(Selected);
        _current.Text = $"当前选择：{name}  (0x{Selected:X4})";
    }

    /// <summary>输入搜索词后：各分类高亮匹配项，当前分类没有匹配就自动跳到第一个有匹配的分类。</summary>
    private void ApplySearch()
    {
        string q = _search.Text.Trim();
        var counts = new int[_canvases.Count];
        for (int i = 0; i < _canvases.Count; i++)
        {
            _canvases[i].Search = q;
            counts[i] = _canvases[i].MatchCount;
        }

        int total = counts.Sum();
        for (int i = 0; i < _tabs.PageCount && i < counts.Length; i++)
            _tabs.SetPageTitle(i, counts[i] > 0 && q.Length > 0
                ? $"{_categories[i].Name} ({counts[i]})"
                : _categories[i].Name);

        if (q.Length == 0)
        {
            _matchInfo.Text = "点任一键即选中，双击可直接确定。";
            _matchInfo.ForeColor = Theme.Ink.Muted;
            return;
        }

        _matchInfo.Text = total > 0 ? $"匹配 {total} 项（高亮显示）" : "没有匹配的键";
        _matchInfo.ForeColor = total > 0 ? Theme.State.Ok : Theme.State.Danger;

        int cur = _tabs.SelectedIndex;
        if (total > 0 && (cur < 0 || counts[cur] == 0))
        {
            int first = Array.FindIndex(counts, c => c > 0);
            if (first >= 0) _tabs.SelectedIndex = first;
        }
    }
}

/// <summary>
/// 把一组键位表画成键盘样子的可点击画布：按相对宽度等比缩放，行数多时竖向滚动。
/// </summary>
internal sealed class PaletteCanvas : ScrollableControl
{
    private const int Pad = 12;
    private const int RowGap = 6;
    // 键帽单元上限（96 DPI 基准 48px，保证 10 行的「层功能」页完整展示不裁剪、不出现滚动条）。
    private const float MaxUnit = 48f;

    private readonly KeycodeCategory _category;
    private readonly List<(RectangleF Rect, PaletteKey Key)> _hits = new();
    private readonly ToolTip _tip = new();
    private ushort _selected;
    private string _search = "";
    private PaletteKey? _lastHit;
    private PaletteKey? _hoverKey;
    private bool _updatingScroll;

    public event Action<PaletteKey>? KeyChosen;
    public event Action<PaletteKey>? KeyActivated;

    /// <summary>点到了 <see cref="PaletteKey.Action"/> 的入口键（不是键码，交给窗体处理）。</summary>
    public event Action<PaletteKey>? ActionClicked;

    public PaletteCanvas(KeycodeCategory category)
    {
        _category = category;
        BackColor = Color.White;
        AutoScroll = true;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public ushort Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public string Search
    {
        get => _search;
        set
        {
            _search = value ?? "";
            Invalidate();
        }
    }

    /// <summary>当前搜索词下本分类的匹配项数。</summary>
    public int MatchCount => _search.Length == 0 ? 0 : _category.AllKeys().Count(k => k.Enabled && IsMatch(k));

    private bool IsMatch(PaletteKey k)
    {
        if (_search.Length == 0) return false;
        if (k.Label.Length == 0) return false;   // 占位键不参与搜索
        string q = _search;
        if (k.Label.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        string hex = $"0x{k.Code:X4}";
        if (hex.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (k.Code.ToString("X4").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        // 也允许用键码表的规范名搜索（调色板名字与它不一定一致）
        return Keycodes.Describe(k.Code).Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateScrollBounds()
    {
        if (_updatingScroll) return;
        _updatingScroll = true;
        try
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || _category.Rows.Count == 0)
            {
                if (AutoScrollMinSize != Size.Empty) AutoScrollMinSize = Size.Empty;
                return;
            }

            float pad = Theme.ScaleF(this, Pad);
            float rowGap = Theme.ScaleF(this, RowGap);
            double maxUnit = Theme.ScaleF(this, MaxUnit);
            double minUnit = Theme.ScaleF(this, 18f);

            double maxW = Math.Max(1, _category.MaxRowWidth());
            double unit = Math.Min(maxUnit, Math.Max(minUnit, (ClientSize.Width - pad * 2 - Theme.ScaleF(this, 4f)) / maxW));
            float keyH = (float)Math.Max(Theme.ScaleF(this, 24f), unit * 0.92);

            float y = pad;
            for (int i = 0; i < _category.Rows.Count; i++)
                y += keyH + rowGap;

            int contentH = (int)Math.Ceiling(y + pad);
            var want = contentH > ClientSize.Height ? new Size(0, contentH) : Size.Empty;
            if (AutoScrollMinSize != want) AutoScrollMinSize = want;
        }
        finally
        {
            _updatingScroll = false;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateScrollBounds();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBounds();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateScrollBounds();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        _hits.Clear();

        float pad = Theme.ScaleF(this, Pad);
        float rowGap = Theme.ScaleF(this, RowGap);
        float keyGap = Theme.ScaleF(this, 3f);
        double maxUnit = Theme.ScaleF(this, MaxUnit);
        double minUnit = Theme.ScaleF(this, 18f);

        double maxW = Math.Max(1, _category.MaxRowWidth());
        double unit = Math.Min(maxUnit, Math.Max(minUnit, (ClientSize.Width - pad * 2 - Theme.ScaleF(this, 4f)) / maxW));
        float keyH = (float)Math.Max(Theme.ScaleF(this, 24f), unit * 0.92);
        // 字号按逻辑（96 DPI）单位计算，TextRenderer 会自动按实际 DPI 缩放点数
        float dpiScale = Math.Max(1f, DeviceDpi / 96f);
        double logicalUnit = unit / dpiScale;
        float font = (float)Math.Clamp(Math.Min(logicalUnit * 0.27, (logicalUnit - 7) / 5.3), 7.0, 10.5);
        int oy = AutoScrollPosition.Y;

        var f = Theme.FontAt(Theme.Fonts.KeycapFamily, font);
        float maxRadius = Theme.ScaleF(this, 6f);

        float y = pad + oy;
        foreach (var row in _category.Rows)
        {
            float x = pad;
            foreach (var key in row)
            {
                float w = (float)(key.Width * unit);
                // 占位键：Label 为空串，只推进 x、不画也不参与命中测试（用来撑出键盘上的空隙）
                if (key.Label.Length == 0) { x += w; continue; }

                var r = new RectangleF(x, y, w - keyGap, keyH - keyGap);
                _hits.Add((r, key));

                bool selected = key.Enabled && key.Code == _selected && _selected != Keycodes.KC_NO;
                bool matched = key.Enabled && IsMatch(key);
                bool dimmed = _search.Length > 0 && key.Enabled && !matched;
                bool hovered = key.Enabled && ReferenceEquals(key, _hoverKey);

                Color fill, border, text;
                float borderWidth = 1f;
                bool drawBottomEdge = true;
                if (!key.Enabled)
                {
                    fill = Theme.Key.DisabledFill;
                    border = Theme.Key.DisabledBorder;
                    text = Theme.Key.DisabledText;
                    drawBottomEdge = false;
                }
                else if (selected)
                {
                    fill = Theme.Key.SelectedFill;
                    border = Theme.Key.SelectedBorder;
                    text = Theme.Key.SelectedText;
                    borderWidth = 2f;
                }
                else if (matched)
                {
                    fill = Theme.Key.MatchFill;
                    border = Theme.Key.MatchBorder;
                    text = Theme.Key.MatchText;
                    borderWidth = 2f;
                }
                else if (dimmed)
                {
                    fill = Theme.Key.DisabledFill;
                    border = Theme.Key.DisabledBorder;
                    text = Theme.Key.DisabledText;
                    drawBottomEdge = false;
                }
                else if (hovered)
                {
                    fill = Theme.Key.HoverFill;
                    border = Theme.Key.HoverBorder;
                    text = Theme.Key.Text;
                }
                else
                {
                    fill = Theme.Key.Fill;
                    border = Theme.Key.Border;
                    text = Theme.Key.Text;
                }

                float radius = Math.Min(maxRadius, r.Height / 4f);
                KeycapRenderer.DrawCap(g, r, radius, fill, border, borderWidth, drawBottomEdge);
                KeycapRenderer.DrawCenterText(g, key.Label, f,
                    new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), text);

                x += w;
            }
            y += keyH + rowGap;
        }
    }

    private PaletteKey? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i].Key;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (ReferenceEquals(hit, _hoverKey)) return;
        _hoverKey = hit;
        Cursor = hit != null && hit.Enabled ? Cursors.Hand : Cursors.Default;
        if (hit != null)
        {
            string tip = !string.IsNullOrEmpty(hit.Tooltip)
                ? hit.Tooltip
                : $"{Keycodes.Describe(hit.Code)} (0x{hit.Code:X4})";
            _tip.SetToolTip(this, tip);
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
        if (_hoverKey == null) return;
        _hoverKey = null;
        _tip.SetToolTip(this, "");
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _tip.Dispose();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var key = HitTest(e.Location);
        _lastHit = key;
        if (key == null || !key.Enabled) return;
        // 入口键不是键码：抛给窗体去开对话框，不走「选中键码」这条路
        if (key.Action) { ActionClicked?.Invoke(key); return; }
        KeyChosen?.Invoke(key);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var key = _lastHit ?? HitTest(e.Location);
        if (key == null || !key.Enabled || key.Action) return;
        KeyActivated?.Invoke(key);
    }
}
