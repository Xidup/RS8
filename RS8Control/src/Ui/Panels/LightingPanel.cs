using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using RS8Control.Model;
using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 灯效面板：每个灯区一张卡片（效果胶囊网格 + 带气泡的滑块 + 色块），
/// 卡片全部从 <see cref="ViaDefinition.LightGroups"/> 动态生成，不硬编码灯区数量。
///
/// 写入策略（与 <see cref="DeviceSession"/> 的灯效队列配合）：
///   拖动滑块 / 点效果胶囊 → <c>QueueLightWrite</c>（30ms 合并窗口，只写设备内存）
///   松手 / 点保存         → <c>FlushLightsAsync</c> 等队列排空后回读校验
///   点「保存到键盘」      → Flush 之后才 <c>SaveLights</c>（0x09 写 EEPROM，全项目只此一处）
/// </summary>
public sealed class LightingPanel : UserControl
{
    private readonly DeviceSession _session;
    private readonly FlowLayoutPanel _host = new BufferedFlowPanel();
    private readonly Label _hint = new();
    private readonly FlatButton _read = new("从键盘读取");
    private readonly FlatButton _save = new("保存到键盘", ButtonVariant.Primary);
    private readonly List<Card> _cards = new();

    /// <summary>上次同步后的卡片宽度；宽度没变就整体跳过，省掉一次整卡重排。</summary>
    private int _cardWidth = -1;

    /// <summary>每次重建卡片就 +1；后台读取返回时若代次变了就丢弃结果。</summary>
    private int _generation;

    private bool _reading;
    private bool _loaded;

    // ---- 重排合并（见 RequestLayout）----
    private bool _layoutPending;
    private bool _layingOut;

    private const int CardMinWidth = 620;
    private const int RowHeight = 46;

    /// <summary>
    /// 灯珠混色能力有限的灯区：给一排「纯色」快捷块 + 提示，别再引导用户去选复合色。
    /// 依据是用户用「颜色诊断」页做的实机观察：侧边环绕灯在复合色相下
    /// 会呈现「白光 + 浅主色」，即混色基本出不来。
    /// </summary>
    private static readonly HashSet<byte> LimitedMixingChannels = new() { 4 };

    /// <summary>
    /// 混色受限灯区的预设色。
    /// 用户用「颜色诊断」实测：色相 0–240（步长 16）在**全饱和**下都能正常显示，
    /// 只有偏白的低饱和混色还原不出来。所以这里给一整排全饱和色相 + 一个白色，
    /// 而不是只给三原色 —— 之前那样过于保守，把能用的颜色也挡在外面了。
    /// </summary>
    private static readonly (string Name, int Hue, int Sat, Color Preview)[] PureColors = BuildPresets();

    private static (string Name, int Hue, int Sat, Color Preview)[] BuildPresets()
    {
        var list = new List<(string, int, int, Color)>();
        for (int h = 0; h < 256; h += 16)
            list.Add(($"色相 {h}", h, 255, HsToRgb(h, 255)));
        list.Add(("白", 0, 0, Color.FromArgb(236, 236, 236)));
        return list.ToArray();
    }

    /// <summary>
    /// 亮度（valueId=1）的**设备真实上限** —— 实机实测：把 255 写进去再回读。
    ///   ch2 正面 Logo 灯：写 255 回读 160（固件把值夹在 160）
    ///   ch3 主键区背光  ：写 255 回读 255（固件按 floor(v × 255 / 160) 换算，实测写 144 回读 229）
    ///   ch4 侧边灯      ：写 255 回读 160
    /// 官方 JSON 三个通道的亮度都写 [0,160]，对主键区背光偏小 —— 设备能到 255，
    /// 滑块 Maximum 必须跟上，否则回读到 229 会被夹成 160 显示。
    /// </summary>
    /// <summary>与 <see cref="BrightnessCeiling"/> 同源，供颜色诊断页做设备域 → 下发域的反解。</summary>
    internal static readonly Dictionary<byte, int> BrightnessCeiling = new()
    {
        [2] = 160,
        [3] = 255,
        [4] = 160,
    };

    // ============================================================
    //  卡片模型
    // ============================================================

    private sealed class Card
    {
        public required LightGroup Def;
        public required CardPanel Host;

        /// <summary>效果控件（type=dropdown，valueId=2）。</summary>
        public LightControl? Effect;

        /// <summary>颜色控件（type=color，valueId=4）。</summary>
        public LightControl? Color;

        /// <summary>范围控件定义（亮度 valueId=1、速度 valueId=3），保持定义顺序。</summary>
        public readonly List<LightControl> RangeDefs = new();

        public readonly List<RangeRow> Ranges = new();

        public FlatButton Toggle = null!;

        /// <summary>灯效模式胶囊网格。整块一个自绘控件，取代原来的 46 个 Button。</summary>
        public ChipGrid? EffectGrid;

        public SwatchButton? Swatch;
        public ColorPresetStrip? PresetStrip;
        public Label? ColorReadout;
        public Label? ColorCaption;

        /// <summary>网格之后的所有控件及其「网格高度为初始值时的 y」—— 网格换行数变化时按差值整体下移/上移。</summary>
        public readonly List<(Control Ctl, int Top)> Below = new();

        /// <summary>建卡时的网格高度与卡片高度，作为重排的基准。</summary>
        public int BaseGridHeight;
        public int BaseCardHeight;

        /// <summary>上次重排用过的差值，避免宽度微调时反复平移。</summary>
        public int LastDelta;

        public int CurrentEffect;
        public int LastNonZeroEffect;
        public int OpSeq;

        public IEnumerable<LightControl> AllControls()
        {
            if (Effect != null) yield return Effect;
            foreach (var r in RangeDefs) yield return r;
            if (Color != null) yield return Color;
        }
    }

    private sealed class RangeRow
    {
        public required LightControl Def;
        public required LightSlider Slider;
        public required Label Caption;
        public required Label Value;
        public int OpSeq;

        /// <summary>用设备回读值刷新界面：滑块会夹到定义区间，右侧数字与滑块保持一致。</summary>
        public void SetFromDevice(int raw)
        {
            Slider.Value = raw;                    // setter 内部按 [min,max] 夹取
            Value.Text = Slider.Value.ToString();
        }

        /// <summary>按 showIf 结果启用/禁用，并在视觉上明显变灰。</summary>
        public void SetEnabled(bool on)
        {
            Slider.Interactive = on;
            Slider.Invalidate();
            Caption.ForeColor = on ? Theme.Ink.Body : Theme.Ink.Disabled;
            Value.ForeColor = on ? Theme.Ink.Primary : Theme.Ink.Disabled;
        }
    }

    public LightingPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Theme.Bg.App;

        var bar = new Panel { Dock = DockStyle.Top, Height = UiLayout.BarHeight, BackColor = Theme.Bg.Surface };
        _hint.AutoSize = true;
        _hint.ForeColor = Theme.Ink.Muted;
        _hint.Text = "拖动即时生效（只写键盘内存）；点「保存到键盘」才会写入 flash。";
        bar.Controls.Add(_hint);

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 10, 0) };
        _read.Click += (_, _) => _ = ReadAsync();
        _save.Click += (_, _) => SaveToDevice();
        right.Controls.Add(_read);
        right.Controls.Add(_save);
        bar.Controls.Add(right);
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        // 单行栏：提示文字与按钮组同一垂直中心
        _hint.Left = 14;
        _hint.Top = UiLayout.CenterInBar(_hint, bar.Height);
        UiLayout.CenterFlowBar(right, bar.Height);

        _host.Dock = DockStyle.Fill;
        _host.FlowDirection = FlowDirection.TopDown;
        _host.WrapContents = false;
        _host.AutoScroll = true;
        _host.Padding = new Padding(12);
        _host.Resize += (_, _) => RequestLayout();

        Controls.Add(_host);
        Controls.Add(bar);

        _session.ReadOnlyChanged += OnReadOnlyChanged;

        // 切到本页时若还没读过就自动读一次。TabControl 会切换子控件的 Visible，
        // 这样不用改 MainForm 也能感知「切到灯效页」。
        VisibleChanged += (_, _) =>
        {
            if (Visible && !_loaded && !_reading && _session.Ready) _ = ReadAsync();
        };
    }

    private void OnReadOnlyChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(OnReadOnlyChanged); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }
        foreach (var card in _cards)
            UpdateEnabledStates(card);
        _save.Enabled = !_reading && _session.Ready && !_session.EffectiveReadOnly;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _session.ReadOnlyChanged -= OnReadOnlyChanged;
        base.Dispose(disposing);
    }

    // ============================================================
    //  构建界面
    // ============================================================

    public void OnSessionChanged()
    {
        _generation++;
        _loaded = false;
        _reading = false;
        _cardWidth = -1;
        _cards.Clear();

        _host.SuspendLayout();
        try
        {
            while (_host.Controls.Count > 0)
            {
                var c = _host.Controls[0];
                _host.Controls.RemoveAt(0);
                c.Dispose();
            }
            _host.Controls.Clear();
        }
        finally { _host.ResumeLayout(false); }

        var def = _session.Definition;
        if (def == null || def.LightGroups.Count == 0)
        {
            _host.Controls.Add(new Label
            {
                Text = "没有可用的灯效定义。",
                AutoSize = true,
                ForeColor = Theme.Ink.Muted,
                Margin = new Padding(4),
            });
            SetBusy(false);
            _read.Enabled = _save.Enabled = false;
            return;
        }

        foreach (var grp in def.LightGroups)
        {
            var card = BuildCard(grp);
            _cards.Add(card);
            _host.Controls.Add(card.Host);
        }

        ResizeCards();
        SetBusy(false);   // 顺便按 Ready 与只读态恢复两个按钮

        if (_session.Ready && Visible) _ = ReadAsync();
    }

    private Card BuildCard(LightGroup grp)
    {
        var host = new CardPanel
        {
            Width = Math.Max(CardMinWidth, _host.ClientSize.Width - 24),
            Margin = new Padding(0, 0, 0, 14),
        };

        var card = new Card { Def = grp, Host = host };

        // 按类型分拣控件；范围控件保持定义顺序（亮度在前、速度在后）
        foreach (var c in grp.Controls)
        {
            switch (c.Type)
            {
                case "dropdown":
                    card.Effect ??= c;
                    break;
                case "range":
                    card.RangeDefs.Add(c);
                    break;
                case "color":
                    card.Color ??= c;
                    break;
            }
        }

        int y = 12;

        // ---- 标题 + 灯区开关（同一行，三个元素同中心）----
        const int headRowH = 28;
        var title = new Label
        {
            Text = TranslateGroup(grp.Label),
            AutoSize = true,
            Font = Theme.Fonts.Title,
            ForeColor = Theme.Ink.Primary,
        };
        host.Controls.Add(title);

        var toggleHint = new Label
        {
            Text = "灯效开关",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = Theme.Ink.Muted,
        };
        host.Controls.Add(toggleHint);

        card.Toggle = new FlatButton("关")
        {
            AutoSize = false,
            Size = new Size(64, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        card.Toggle.Click += (_, _) => ToggleCard(card);
        host.Controls.Add(card.Toggle);

        title.Location = new Point(18, UiLayout.CenterInRow(title, y, headRowH));
        toggleHint.Location = new Point(host.Width - 64 - 18 - toggleHint.Width - 8,
                                        UiLayout.CenterInRow(toggleHint, y, headRowH));
        card.Toggle.Location = new Point(host.Width - 64 - 18,
                                         UiLayout.CenterInRow(card.Toggle, y, headRowH));

        y += 42;

        // ---- 效果胶囊网格（自动换行 + 自动撑高，不再内嵌滚动条）----
        if (card.Effect != null)
        {
            host.Controls.Add(new Label
            {
                Text = "灯效模式",
                Location = new Point(18, y),
                AutoSize = true,
                ForeColor = Theme.Ink.Body,
            });
            y += 24;

            card.EffectGrid = new ChipGrid
            {
                Location = new Point(18, y),
                Width = host.Width - 36,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Theme.Bg.Surface,
            };
            var options = new List<(string Text, int Value)>();
            foreach (var (name, value) in card.Effect.Options)
                options.Add((EffectNames.Translate(name), value));
            card.EffectGrid.SetItems(options, 0);
            card.EffectGrid.Chosen += value => ChooseEffect(card, value);
            host.Controls.Add(card.EffectGrid);

            // 网格会按宽度自动算高度：46 项在窄卡片里排更多行，卡片跟着长。
            // 这里只记下基准，实际高度由 RelayoutCards 在宽度确定后统一刷新。
            card.BaseGridHeight = card.EffectGrid.Height;
            y += card.EffectGrid.Height + 8;
        }

        // ---- 亮度 / 速度等范围控件 ----
        foreach (var rangeDef in card.RangeDefs)
        {
            card.Ranges.Add(BuildRangeRow(host, rangeDef, y));
            y += RowHeight;
        }

        // ---- 颜色 ----
        if (card.Color != null)
        {
            const int colorRowH = 28;
            card.ColorCaption = new Label
            {
                Text = TranslateControl(card.Color.Label),
                Width = 48,
                AutoSize = false,
                ForeColor = Theme.Ink.Body,
            };
            host.Controls.Add(card.ColorCaption);

            card.Swatch = new SwatchButton { Size = new Size(56, 28) };
            card.Swatch.Click += (_, _) => PickColor(card);
            host.Controls.Add(card.Swatch);

            card.ColorReadout = new Label
            {
                AutoSize = true,
                ForeColor = Theme.Ink.Muted,
                Text = "色相 — · 饱和 —",
            };
            host.Controls.Add(card.ColorReadout);

            // 颜色行的三个元素同中心
            card.ColorCaption.Location = new Point(18, UiLayout.CenterInRow(card.ColorCaption, y, colorRowH));
            card.Swatch.Location = new Point(70, UiLayout.CenterInRow(card.Swatch, y, colorRowH));
            card.ColorReadout.Location = new Point(136, UiLayout.CenterInRow(card.ColorReadout, y, colorRowH));

            y += RowHeight;

            // 混色受限的灯区：补一排全饱和色相快捷块 + 一句说明
            if (LimitedMixingChannels.Contains(card.Color.Channel))
            {
                host.Controls.Add(new Label
                {
                    Location = new Point(70, y),
                    AutoSize = true,
                    ForeColor = Theme.State.Warn,
                    Text = "该灯区对偏白的低饱和色还原较差（实测全饱和色相都能正常显示），建议用下面这些：",
                });
                y += 24;

                var strip = new ColorPresetStrip { Location = new Point(Theme.Scale(this, 70), y) };
                strip.SetPresets(PureColors);
                strip.PresetChosen += (h, s) => ApplyPreset(card, h, s);
                host.Controls.Add(strip);
                card.PresetStrip = strip;
                y += strip.Height + Theme.Scale(this, 8);
            }
        }

        host.Height = y + 8;
        card.BaseCardHeight = host.Height;
        CaptureBelow(card);
        UpdateEnabledStates(card);
        return card;
    }

    /// <summary>
    /// 记下网格「之后」的控件及其 y。网格换行数变化时，这些控件整体下移/上移同一个差值即可，
    /// 不需要重排整个卡片。
    /// </summary>
    private static void CaptureBelow(Card card)
    {
        card.Below.Clear();
        if (card.EffectGrid == null) return;

        int gridBottom = card.EffectGrid.Bottom;
        foreach (Control c in card.Host.Controls)
            if (!ReferenceEquals(c, card.EffectGrid) && c.Top >= gridBottom)
                card.Below.Add((c, c.Top));
    }

    /// <summary>
    /// 按当前宽度刷新网格高度，并把网格之后的控件整体平移差值、同步卡片高度。
    ///
    /// 背景：胶囊网格改成自动撑高之后，46 项的主键区灯效在窄卡片里会多排几行，
    /// 卡片必须跟着长，否则下面的亮度/颜色行会被压住。
    /// </summary>
    private static void RelayoutCard(Card card)
    {
        if (card.EffectGrid == null) return;

        int w = Math.Max(120, card.Host.Width - 36);
        if (card.EffectGrid.Width != w) card.EffectGrid.Width = w;   // 触发 ChipGrid 自己重算高度

        int delta = card.EffectGrid.Height - card.BaseGridHeight;
        if (delta == card.LastDelta) return;   // 行数没变就别折腾
        card.LastDelta = delta;

        foreach (var (ctl, top) in card.Below) ctl.Top = top + delta;
        card.Host.Height = card.BaseCardHeight + delta;
    }

    private RangeRow BuildRangeRow(CardPanel host, LightControl def, int y)
    {
        // 亮度/速度行：标题、滑块、右侧数值三个元素同中心
        int rangeRowH = Theme.Scale(host, 42);
        var caption = new Label
        {
            Text = TranslateControl(def.Label),
            Width = 48,
            AutoSize = false,
            ForeColor = Theme.Ink.Body,
        };
        host.Controls.Add(caption);

        var slider = new LightSlider
        {
            Minimum = def.Min,
            Maximum = SliderMax(def),
            Size = new Size(host.Width - 70 - 78, rangeRowH),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        host.Controls.Add(slider);

        var value = new Label
        {
            Size = new Size(52, 20),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = Theme.Ink.Primary,
            Text = "—",
        };
        host.Controls.Add(value);

        caption.Location = new Point(18, UiLayout.CenterInRow(caption, y, rangeRowH));
        slider.Location = new Point(70, y);
        value.Location = new Point(host.Width - 68, UiLayout.CenterInRow(value, y, rangeRowH));

        var row = new RangeRow { Def = def, Slider = slider, Caption = caption, Value = value };
        slider.ValueChanged += v =>
        {
            value.Text = v.ToString();
            if (_session.Ready && !_session.EffectiveReadOnly)
            {
                _session.QueueLightWrite(row.Def.Channel, row.Def.ValueId, ToViaValue(row, v));
                _session.SetUnsavedLights(true);
            }
        };
        slider.DragCompleted += () => _ = CommitRangeAsync(row);
        return row;
    }

    /// <summary>
    /// 滑块的取值范围。只有亮度（valueId=1）用实测的设备上限覆盖定义值，
    /// 其余控件（灯效、速度、颜色）照官方定义走。
    /// </summary>
    private static int SliderMax(LightControl def) =>
        def.ValueId == 1 && BrightnessCeiling.TryGetValue(def.Channel, out int max)
            ? Math.Max(def.Min + 1, max)
            : Math.Max(def.Min + 1, def.Max);

    /// <summary>
    /// 设备域 → 下发域。主键区背光的设备上限（255）大于 VIA 定义上限（160），
    /// 固件按 floor(v × 255 / 160) 放大，这里取上取整反解，保证读回值落在目标上。
    /// </summary>
    private static ushort ToViaValue(RangeRow row, int deviceValue)
    {
        int viaMax = row.Def.Max;
        int sliderMax = row.Slider.Maximum;
        if (sliderMax <= viaMax) return (ushort)deviceValue;
        return (ushort)Math.Clamp((int)Math.Ceiling(deviceValue * (double)viaMax / sliderMax), 0, viaMax);
    }

    /// <summary>卡片宽度跟随容器；卡片内的子控件靠 Anchor 自适应。</summary>
    private void ResizeCards()
    {
        int w = Math.Max(CardMinWidth, _host.ClientSize.Width - 24);
        if (w == _cardWidth) return;    // 宽度没变就整体跳过，省掉整卡重排
        _cardWidth = w;
        _host.SuspendLayout();
        try
        {
            foreach (var card in _cards)
            {
                card.Host.Width = w;
                RelayoutCard(card);   // 网格换行数随宽度变化，卡片高度要跟着走
            }
        }
        finally { _host.ResumeLayout(true); }
    }

    /// <summary>
    /// 把重排请求合并到消息循环。拖动窗口时 Resize 每秒触发几十次，
    /// 直接在里面给 46 个胶囊按钮重排会严重掉帧。这里只登记标志并 BeginInvoke，
    /// 保证**每个消息循环周期最多真正重排一次**；重排期间到达的请求直接丢弃。
    /// </summary>
    private void RequestLayout()
    {
        if (IsDisposed) return;
        if (_layingOut || _layoutPending) return;   // 已有请求在排队 → 合并掉

        if (!IsHandleCreated)
        {
            // 构造期还没有句柄，BeginInvoke 会抛异常，直接同步做一次。
            // 同样要打上 _layingOut 标记，否则 ResumeLayout 引发的 Resize 会重入（见 ShortcutPanel 的同类说明）。
            _layingOut = true;
            try { ResizeCards(); }
            finally { _layingOut = false; }
            return;
        }

        _layoutPending = true;
        try { BeginInvoke(new Action(FlushLayout)); }
        catch (InvalidOperationException) { _layoutPending = false; }   // 句柄在排队后被销毁
    }

    private void FlushLayout()
    {
        if (IsDisposed) { _layoutPending = false; return; }
        _layoutPending = false;
        _layingOut = true;
        try { ResizeCards(); }
        finally { _layingOut = false; }
    }

    // ============================================================
    //  读取（后台线程，不阻塞 UI）
    // ============================================================

    private async Task ReadAsync()
    {
        if (_reading || !_session.Ready || _cards.Count == 0) return;

        _reading = true;
        int gen = _generation;
        SetBusy(true, lockHost: false);   // 读取不锁卡片区，只灰掉两个按钮
        _hint.ForeColor = Theme.Ink.Muted;
        _hint.Text = "正在读取灯效…";

        try
        {
            // DeviceSession 内部对 HID 收发做了串行化，后台线程调用是安全的
            var targets = _cards.SelectMany(c => c.AllControls()).ToList();
            var values = await Task.Run(() =>
            {
                var map = new Dictionary<(byte Channel, byte ValueId), int>();
                foreach (var c in targets)
                {
                    int v = _session.ReadLight(c.Channel, c.ValueId);
                    if (v >= 0) map[(c.Channel, c.ValueId)] = v;
                }
                return map;
            }).ConfigureAwait(true);

            if (IsDisposed || gen != _generation) return;

            ApplyValues(values);
            if (values.Count > 0)
            {
                _loaded = true;
                _session.SetUnsavedLights(false);
                _hint.Text = $"已读取 {values.Count} 项灯效设置。";
            }
            else
            {
                _hint.Text = "读取失败（键盘可能休眠，按任意键唤醒后重试）。";
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && gen == _generation) _hint.Text = "读取灯效出错：" + ex.Message;
        }
        finally
        {
            _reading = false;
            if (!IsDisposed && gen == _generation) SetBusy(false, lockHost: false);
        }
    }

    private void ApplyValues(Dictionary<(byte Channel, byte ValueId), int> values)
    {
        foreach (var card in _cards)
        {
            foreach (var row in card.Ranges)
            {
                if (values.TryGetValue((row.Def.Channel, row.Def.ValueId), out int v))
                    row.SetFromDevice(v);
            }

            if (card.Effect != null && values.TryGetValue((card.Effect.Channel, card.Effect.ValueId), out int eff))
            {
                card.CurrentEffect = eff;
                if (!IsCardOff(card, eff)) card.LastNonZeroEffect = eff;
                PaintChips(card, eff);
            }

            if (card.Color != null && values.TryGetValue((card.Color.Channel, card.Color.ValueId), out int col))
                ShowColor(card, col);

            UpdateEnabledStates(card);
        }
    }

    // ============================================================
    //  写入
    // ============================================================

    /// <summary>滑块松手：等队列排空后回读校验，不一致就把界面回滚到设备实际值。</summary>
    private async Task CommitRangeAsync(RangeRow row)
    {
        int gen = _generation;
        int seq = ++row.OpSeq;
        try
        {
            int wanted = row.Slider.Value;                          // 设备域
            if (_session.EffectiveReadOnly)
            {
                _hint.Text = "只读模式：改动只在界面预览，不会写入键盘。";
                return;
            }

            _session.QueueLightWrite(row.Def.Channel, row.Def.ValueId, ToViaValue(row, wanted));
            _session.SetUnsavedLights(true);

            await _session.FlushLightsAsync().ConfigureAwait(true);
            if (IsDisposed || gen != _generation || seq != row.OpSeq) return;

            int actual = await Task.Run(() => _session.ReadLight(row.Def.Channel, row.Def.ValueId)).ConfigureAwait(true);
            if (IsDisposed || gen != _generation || seq != row.OpSeq) return;

            if (actual < 0)
            {
                _hint.Text = "下发后回读失败（键盘可能休眠），界面值可能与设备不一致。";
                return;
            }
            if (!MatchesWrite(row, wanted, actual))
            {
                row.SetFromDevice(actual);
                _hint.Text = $"{TranslateControl(row.Def.Label)}下发未生效（设备实际 {actual}），界面已回滚。";
                return;
            }
            // 固件有量化（下发域比设备域窄），把界面吸附到设备真实值，避免显示一个设备上不存在的数
            row.SetFromDevice(actual);
            _hint.Text = $"{TranslateControl(row.Def.Label)} = {actual}（尚未保存到 flash）。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && gen == _generation && seq == row.OpSeq)
                _hint.Text = "下发失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 判断设备回读值是否说明刚写入的值生效了。
    ///
    /// 实测 RS8 固件对**主键区背光亮度**（ch=3, valueId=1）做了 0..160 → 0..255 的线性换算：
    /// 写 80 回读 127、写 144 回读 229、写 160 回读 255（floor(v × 255 / 160)）；而 logo/side 的
    /// 亮度是原样存储（上限 160）。设备域比下发域宽，量化不可避免，所以除了相等还要容忍一个量化步长，
    /// 否则每次拖背光亮度都会被误判成"下发失败"并回滚。
    /// </summary>
    private static bool MatchesWrite(RangeRow row, int written, int readBack)
    {
        if (readBack == written) return true;

        int sliderMax = row.Slider.Maximum;
        int viaMax = Math.Max(1, row.Def.Max);
        if (sliderMax <= viaMax) return false;   // 没有换算，必须精确相等

        int step = (int)Math.Ceiling(sliderMax / (double)viaMax);
        return Math.Abs(readBack - written) <= step;
    }

    /// <summary>点效果胶囊 / 灯区开关：切换选中态并下发；失败则回滚到设备实际值。</summary>
    private async void ChooseEffect(Card card, int value)
    {
        int gen = _generation;
        int seq = ++card.OpSeq;
        try
        {
            SelectEffect(card, value);
            var eff = card.Effect;
            if (eff == null) return;

            if (_session.EffectiveReadOnly)
            {
                _hint.Text = "只读模式：改动只在界面预览，不会写入键盘。";
                return;
            }

            _session.QueueLightWrite(eff.Channel, eff.ValueId, (ushort)value);
            _session.SetUnsavedLights(true);

            await _session.FlushLightsAsync().ConfigureAwait(true);
            if (IsDisposed || gen != _generation || seq != card.OpSeq) return;

            int actual = await Task.Run(() => _session.ReadLight(eff.Channel, eff.ValueId)).ConfigureAwait(true);
            if (IsDisposed || gen != _generation || seq != card.OpSeq) return;

            if (actual < 0)
            {
                _hint.Text = "切换灯效后回读失败（键盘可能休眠）。";
                return;
            }
            if (actual != value)
            {
                SelectEffect(card, actual);
                _hint.Text = value == 0
                    ? $"{TranslateGroup(card.Def.Label)}的固件忽略效果 0，无法从这里关闭；已回滚为「{EffectNameOf(card, actual)}」。"
                    : $"灯效切换未生效（设备实际 {actual}），界面已回滚。";
                return;
            }
            _hint.Text = $"灯效已切换为「{EffectNameOf(card, actual)}」（尚未保存到 flash）。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && gen == _generation && seq == card.OpSeq)
                _hint.Text = "切换灯效失败：" + ex.Message;
        }
    }

    /// <summary>灯区关闭对应的效果值：选项中有非 0 的 "shutdown"（ch2/ch4 为 6）时用它，否则用 0。</summary>
    private static int OffEffectOf(Card card)
    {
        if (card.Effect != null)
        {
            foreach (var (name, val) in card.Effect.Options)
            {
                if (val != 0 && name.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                    return val;
            }
        }
        return 0;
    }

    private static bool IsCardOff(Card card, int effect)
    {
        int off = OffEffectOf(card);
        return effect == off || effect == 0;
    }

    /// <summary>灯区开关：关 → 切到关闭效果（0 或 shutdown=6）；开 → 回到上次有效效果。</summary>
    private void ToggleCard(Card card)
    {
        if (card.Effect == null) return;

        int off = OffEffectOf(card);
        int target;
        if (!IsCardOff(card, card.CurrentEffect))
        {
            card.LastNonZeroEffect = card.CurrentEffect;
            target = off;
        }
        else
        {
            target = !IsCardOff(card, card.LastNonZeroEffect)
                ? card.LastNonZeroEffect
                : card.Effect.Options.FirstOrDefault(o => o.Value != 0 && o.Value != off).Value;
            if (IsCardOff(card, target)) target = 1;
        }
        ChooseEffect(card, target);
    }

    /// <summary>「保存到键盘」：先排空下发队列，再写一次 EEPROM。绝不随拖动触发。</summary>
    private async void SaveToDevice()
    {
        if (!_session.Ready) return;
        if (_session.EffectiveReadOnly)
        {
            _hint.Text = "只读模式：不会写入键盘。";
            return;
        }

        try
        {
            SetBusy(true);
            _hint.ForeColor = Theme.Ink.Muted;
            _hint.Text = "正在等待下发完成…";
            await _session.FlushLightsAsync().ConfigureAwait(true);

            bool ok = await Task.Run(() => _session.SaveLights()).ConfigureAwait(true);
            if (IsDisposed) return;
            if (ok) _session.SetUnsavedLights(false);
            _hint.Text = ok ? "已保存到键盘（写入 EEPROM）。" : "保存失败（键盘可能休眠，按任意键唤醒后重试）。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _hint.Text = "保存失败：" + ex.Message;
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    // ============================================================
    //  界面状态
    // ============================================================

    private void SelectEffect(Card card, int value)
    {
        card.CurrentEffect = value;
        if (!IsCardOff(card, value)) card.LastNonZeroEffect = value;
        PaintChips(card, value);
        UpdateEnabledStates(card);
    }

    private static void PaintChips(Card card, int selected)
    {
        if (card.EffectGrid != null) card.EffectGrid.Selected = selected;

        bool isOff = IsCardOff(card, selected);
        card.Toggle.Text = isOff ? "关" : "开";
        card.Toggle.Variant = isOff ? ButtonVariant.Secondary : ButtonVariant.Primary;
    }

    /// <summary>
    /// 按连接/只读状态、灯区开关与 showIf 决定卡片内各控件是否可用：
    /// logo/side 的 shutdown=6 时整区停用；其余按定义原文求值，不写死。
    /// </summary>
    private void UpdateEnabledStates(Card card)
    {
        bool panelWritable = _session.Ready && !_session.EffectiveReadOnly;
        bool zoneOn = !IsCardOff(card, card.CurrentEffect);

        foreach (var row in card.Ranges)
            row.SetEnabled(panelWritable && zoneOn && ShowIfSatisfied(row.Def.ShowIf, card.CurrentEffect));

        if (card.EffectGrid != null)
            card.EffectGrid.Interactive = panelWritable;
        card.Toggle.Enabled = panelWritable && card.Effect != null;

        if (card.Color != null && card.Swatch != null)
        {
            bool on = panelWritable && zoneOn && ShowIfSatisfied(card.Color.ShowIf, card.CurrentEffect);
            card.Swatch.Interactive = on;
            card.Swatch.Invalidate();
            if (card.PresetStrip != null)
                card.PresetStrip.Interactive = on;
            if (card.ColorCaption != null)
                card.ColorCaption.ForeColor = on ? Theme.Ink.Body : Theme.Ink.Disabled;
            if (card.ColorReadout != null)
                card.ColorReadout.ForeColor = on ? Theme.Ink.Muted : Theme.Ink.Disabled;
        }
    }

    private static void DisplayColor(Card card, int hue, int sat)
    {
        if (card.Swatch != null) card.Swatch.SwatchColor = HsToRgb(hue, sat);
        if (card.ColorReadout != null) card.ColorReadout.Text = $"色相 {hue} · 饱和 {sat}";
        card.PresetStrip?.SetSelectedHs(hue, sat);
    }

    private void ShowColor(Card card, int value)
    {
        // 实测：低字节 = 色相，高字节 = 饱和度（即 (r[4]<<8)|r[3]）
        DisplayColor(card, value & 0xFF, (value >> 8) & 0xFF);
    }

    private void PickColor(Card card)
    {
        if (card.Color == null || card.Swatch == null || !card.Swatch.Interactive) return;

        using var dlg = new ColorDialog { Color = card.Swatch.SwatchColor, FullOpen = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var (hue, sat) = RgbToHs(dlg.Color);
        int value = (sat << 8) | hue;
        DisplayColor(card, hue, sat);

        _ = PushColorAsync(card, value);
    }

    /// <summary>点纯色快捷块：直接下发该色相/饱和，不走系统调色板。</summary>
    private void ApplyPreset(Card card, int hue, int sat)
    {
        if (card.Color == null || card.Swatch == null || !card.Swatch.Interactive) return;
        int value = (sat << 8) | hue;
        DisplayColor(card, hue, sat);
        _ = PushColorAsync(card, value);
    }

    private async Task PushColorAsync(Card card, int value)
    {
        int gen = _generation;
        int seq = ++card.OpSeq;
        var col = card.Color;
        if (col == null) return;
        try
        {
            if (_session.EffectiveReadOnly)
            {
                _hint.Text = "只读模式：改动只在界面预览，不会写入键盘。";
                return;
            }

            _session.QueueLightWrite(col.Channel, col.ValueId, (ushort)value);
            _session.SetUnsavedLights(true);

            await _session.FlushLightsAsync().ConfigureAwait(true);
            if (IsDisposed || gen != _generation || seq != card.OpSeq) return;

            int actual = await Task.Run(() => _session.ReadLight(col.Channel, col.ValueId)).ConfigureAwait(true);
            if (IsDisposed || gen != _generation || seq != card.OpSeq) return;

            if (actual < 0)
            {
                _hint.Text = "颜色下发后回读失败（键盘可能休眠）。";
                return;
            }
            if (actual != value)
            {
                ShowColor(card, actual);
                _hint.Text = $"颜色下发未生效（设备实际 0x{actual:X4}），界面已回滚。";
                return;
            }
            _hint.Text = $"颜色已更新（色相 {value & 0xFF} · 饱和 {(value >> 8) & 0xFF}，尚未保存到 flash）。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && gen == _generation && seq == card.OpSeq)
                _hint.Text = "颜色下发失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 忙碌态开关。
    /// <paramref name="lockHost"/> = true 时连卡片区一起锁（写入 / 保存 EEPROM 时用，
    /// 避免用户在写入过程中再改值）；读取时传 false —— 读是非破坏性的，
    /// 锁住整个卡片区只会让 60 个胶囊白白闪一下灰，没有任何收益。
    /// </summary>
    private void SetBusy(bool busy, bool lockHost = true)
    {
        if (lockHost) _host.Enabled = !busy;
        _read.Enabled = !busy && _session.Ready;
        _save.Enabled = !busy && _session.Ready && !_session.EffectiveReadOnly;
    }

    private string EffectNameOf(Card card, int value)
    {
        if (card.Effect != null)
            foreach (var (name, v) in card.Effect.Options)
                if (v == value) return EffectNames.Translate(name);
        return $"效果 {value}";
    }

    // ============================================================
    //  showIf 求值
    // ============================================================

    /// <summary>
    /// 求值官方定义里的 showIf 表达式，形如 <c>{id_xxx} != 0 &amp;&amp; {id_xxx} != 1</c>。
    /// 只支持 <c>!= / ==</c> 与 <c>&amp;&amp; / ||</c>（RS8 定义里只用到这些）；
    /// 解析不了的子句一律当作「满足」，宁可让控件可用也不要把用户锁死。
    /// </summary>
    internal static bool ShowIfSatisfied(string expr, int effect)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;

        foreach (var orPart in expr.Split("||"))
        {
            bool all = true;
            foreach (var clause in orPart.Split("&&"))
            {
                if (!EvalClause(clause, effect)) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    private static bool EvalClause(string clause, int effect)
    {
        var m = Regex.Match(clause, @"\{[^}]*\}\s*(==|!=)\s*(-?\d+)");
        if (!m.Success) return true;
        if (!int.TryParse(m.Groups[2].Value, out int rhs)) return true;
        bool equal = effect == rhs;
        return m.Groups[1].Value == "==" ? equal : !equal;
    }

    // ============================================================
    //  文案
    // ============================================================

    private static string TranslateGroup(string s) => s switch
    {
        "logo" => "正面 Logo 灯",
        "Backlight" => "主键区背光",
        "side" => "侧边灯",
        _ => s,
    };

    private static string TranslateControl(string s) => s switch
    {
        "Brightness" => "亮度",
        "Effect" => "灯效模式",
        "Effect Speed" => "速度",
        "Color" => "颜色",
        _ => s,
    };

    // ============================================================
    //  RGB <-> VIA 的 HSV8（色相/饱和度各 8 位，明度固定取满）
    // ============================================================

    private static (int Hue, int Sat) RgbToHs(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        double s = max <= 0 ? 0 : d / max;
        return ((int)Math.Round(h / 360.0 * 255) & 0xFF, (int)Math.Round(s * 255) & 0xFF);
    }

    private static Color HsToRgb(int hue255, int sat255)
    {
        double h = hue255 / 255.0 * 360.0, s = sat255 / 255.0, v = 1.0;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromArgb(
            Math.Clamp((int)Math.Round((r + m) * 255), 0, 255),
            Math.Clamp((int)Math.Round((g + m) * 255), 0, 255),
            Math.Clamp((int)Math.Round((b + m) * 255), 0, 255));
    }
}

// ============================================================
//  卡片容器：圆角白底 + 淡边框
// ============================================================
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        // 卡片自身设成**白色**而不是页面底色：WinForms 的 BackColor 是环境属性，
        // 子控件（Label、自绘控件取 Parent.BackColor）会继承它。
        // 早先这里设的是页面灰，导致卡片内的 Label 全都盖出一块浅灰底 —— 很隐蔽。
        // 圆角之外的四个角改为在 OnPaint 里显式用父容器底色铺出来。
        BackColor = Theme.Bg.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 先把整块铺成页面底色，圆角之外那四角就露成页面底色（而不是白块）
        var outer = Parent?.BackColor ?? Theme.Bg.App;
        g.FillRectangle(Theme.Brush(outer == Color.Empty ? Theme.Bg.App : outer),
            new Rectangle(0, 0, Width, Height));

        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        g.FillPath(Theme.Brush(Theme.Bg.Surface), Theme.RoundedPath(r, Theme.Metrics.CardRadius));
        g.DrawPath(Theme.Pen(Theme.Line.Subtle), Theme.RoundedPath(r, Theme.Metrics.CardRadius));
    }
}

// ============================================================
//  带数值气泡的滑块
// ============================================================
internal sealed class LightSlider : Control
{
    private const int PadX = 11;

    private int _min;
    private int _max = 255;
    private int _value;
    private bool _interactive = true;
    private bool _hover;
    private bool _dragging;

    /// <summary>false 时变灰且不响应鼠标（对应 showIf 不满足）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            if (!EffectiveInteractive)
            {
                _hover = false;
                _dragging = false;
            }
            Cursor = EffectiveInteractive ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>有效可用性：自身开关 且 继承来的 Enabled（父容器被禁用时也要变灰）。</summary>
    private bool EffectiveInteractive => _interactive && Enabled;

    /// <summary>用户拖动导致的值变化（程序化设值不会触发）。</summary>
    public event Action<int>? ValueChanged;

    /// <summary>松手 / 拖动结束。</summary>
    public event Action? DragCompleted;

    public LightSlider()
    {
        DoubleBuffered = true;
        Height = Theme.Scale(this, 42);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum
    {
        get => _min;
        set { _min = value; if (_value < _min) _value = _min; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _max;
        set { _max = Math.Max(value, _min + 1); if (_value > _max) _value = _max; Invalidate(); }
    }

    /// <summary>程序化设值，不会触发 <see cref="ValueChanged"/>（避免回读时反向写回设备）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    private double Ratio => _max <= _min ? 0 : (_value - _min) / (double)(_max - _min);

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = Theme.Scale(this, 42);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!EffectiveInteractive)
        {
            _hover = false;
            _dragging = false;
        }
        Cursor = EffectiveInteractive ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        Color backdrop = Parent?.BackColor is Color pc && pc != Color.Empty ? pc : Theme.Bg.Surface;
        g.Clear(backdrop);

        float padX = Theme.ScaleF(this, PadX);
        float left = padX;
        float right = Width - padX;
        if (right <= left) return;

        float trackH = Theme.ScaleF(this, 4f);
        float thumbD = Theme.ScaleF(this, 14f);
        float dotD = Theme.ScaleF(this, 5f);
        float trackY = Height - Theme.ScaleF(this, 14f);
        float trackRadius = trackH / 2f;
        float cx = (float)(left + (right - left) * Ratio);

        bool on = EffectiveInteractive;
        Color trackBase = on ? Theme.Line.Subtle : Theme.Bg.Sunken;
        Color trackFill = on ? Theme.Accent.Base : Theme.Ink.Disabled;
        Color thumbColor = !on ? Theme.Ink.Disabled
            : _dragging ? Theme.Accent.Pressed
            : _hover ? Theme.Accent.Hover
            : Theme.Accent.Base;

        g.FillPath(Theme.Brush(trackBase),
            Theme.RoundedPath(new RectangleF(left, trackY - trackH / 2f, right - left, trackH), trackRadius));

        float fillW = cx - left;
        if (fillW > 0.5f)
        {
            g.FillPath(Theme.Brush(trackFill),
                Theme.RoundedPath(new RectangleF(left, trackY - trackH / 2f, Math.Max(trackH, fillW), trackH), trackRadius));
        }

        g.FillEllipse(Theme.Brush(thumbColor), cx - thumbD / 2f, trackY - thumbD / 2f, thumbD, thumbD);
        g.FillEllipse(Theme.Brush(Theme.Bg.Surface), cx - dotD / 2f, trackY - dotD / 2f, dotD, dotD);

        if ((_hover || _dragging) && on)
        {
            var f = Theme.FontAt(Theme.Fonts.KeycapFamily, 8F);
            string text = _value.ToString();
            var textSize = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int bw = textSize.Width + Theme.Scale(this, 8);
            int bh = textSize.Height + Theme.Scale(this, 3);
            float bx = Math.Clamp(cx - bw / 2f, 0f, Math.Max(0f, Width - bw));
            var br = new RectangleF(bx, 0f, bw, bh);

            g.FillPath(Theme.Brush(Theme.Accent.Base), Theme.RoundedPath(br, Theme.ScaleF(this, 5f)));
            TextRenderer.DrawText(
                g,
                text,
                f,
                Rectangle.Round(br),
                Theme.Bg.Surface,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    private void SetFromMouse(int x)
    {
        int padX = Theme.Scale(this, PadX);
        double t = Math.Clamp((x - padX) / (double)Math.Max(1, Width - padX * 2), 0, 1);
        int v = (int)Math.Round(_min + t * (_max - _min));
        if (v == _value) return;
        _value = v;
        Invalidate();
        ValueChanged?.Invoke(v);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!EffectiveInteractive) return;
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hover) return;
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!EffectiveInteractive || e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        Invalidate();
        SetFromMouse(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        _hover = EffectiveInteractive && ClientRectangle.Contains(e.Location);
        Invalidate();
        DragCompleted?.Invoke();
    }
}

// ============================================================
//  颜色色块按钮
// ============================================================
internal sealed class SwatchButton : Control
{
    private Color _swatch = Color.FromArgb(255, 80, 80);
    private bool _interactive = true;
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SwatchColor
    {
        get => _swatch;
        set { _swatch = value; Invalidate(); }
    }

    /// <summary>false 时画成灰化色块（对应 showIf 不满足）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            if (!EffectiveInteractive) _hover = false;
            Cursor = EffectiveInteractive ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private bool EffectiveInteractive => _interactive && Enabled;

    public SwatchButton()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!EffectiveInteractive) _hover = false;
        Cursor = EffectiveInteractive ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!EffectiveInteractive) return;
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hover) return;
        _hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        Color backdrop = Parent?.BackColor is Color pc && pc != Color.Empty ? pc : Theme.Bg.Surface;
        g.Clear(backdrop);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = Theme.ScaleF(this, 4f);
        var path = Theme.RoundedPath(r, radius);

        bool on = EffectiveInteractive;
        Color fill = on ? _swatch : Theme.Blend(_swatch, Theme.Bg.Sunken, 0.75);
        Color border = !on ? Theme.Line.Subtle
            : _hover ? Theme.Accent.Base
            : Theme.Line.Control;
        float borderW = on && _hover ? Theme.ScaleF(this, 1.5f) : 1f;

        g.FillPath(Theme.Brush(fill), path);
        g.DrawPath(Theme.Pen(border, borderW), path);
    }
}
