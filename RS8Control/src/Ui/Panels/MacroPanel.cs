using RS8Control.Model;
using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 宏编辑器：左侧 16 个宏，右侧步骤列表 + 编码预览。
///
/// 宏只是存在键盘的宏缓冲里，<b>不会自动触发</b> —— 要真正用起来还得去「键位映射」页
/// 把某个键设成对应的「宏 N」键码（0x7700–0x770F）。界面上写明了这一点，避免误解。
///
/// 写入安全策略（宏缓冲是全项目唯一会整块覆盖的写入，必须保守）：
///   1. 写之前先 0x0E 读出当前缓冲做备份（读不出来就放弃写入）
///   2. 弹确认框说明会覆盖键盘上现有的宏
///   3. 0x0F 分帧写入 → 等固件落盘 → 回读校验
///   4. 校验不一致时提示，并把「恢复备份」的入口直接摆到按钮栏上
///   5. <b>绝不调用 0x10（清空宏缓冲）</b>
/// </summary>
public sealed class MacroPanel : UserControl
{
    private static readonly Color ColOk = Theme.State.Ok;
    private static readonly Color ColErr = Theme.State.Danger;
    private static readonly Color ColIdle = Theme.Ink.Muted;

    private readonly DeviceSession _session;

    private readonly Panel _bar = new();
    private readonly FlowLayoutPanel _actions = new();
    private readonly FlatButton _read = new("从键盘读取宏");
    private readonly FlatButton _write = new("写入键盘", ButtonVariant.Primary);
    private readonly FlatButton _restore = new("恢复备份");
    private readonly Label _status = new();

    private readonly Panel _body = new();
    private readonly CardPanel _leftCard = new();
    private readonly Panel _cardGap = new();
    private readonly CardPanel _rightCard = new();

    private readonly Label _leftTitle = new();
    private readonly Label _rightTitle = new();
    private readonly ListBox _macroList = new();
    private readonly Panel _stepHost = new();
    private readonly ListBox _stepList = new();
    private readonly Label _emptyStepsHint = new();
    private readonly FlowLayoutPanel _stepBar = new();
    private readonly Label _preview = new();
    private readonly BufferUsageBar _usageBar = new();
    private readonly Label _footNote = new();

    private readonly FlatButton _addText = new("＋打字");
    private readonly FlatButton _addKey = new("＋按键");
    private readonly FlatButton _addDelay = new("＋延时");
    private readonly FlatButton _editStep = new("编辑");
    private readonly FlatButton _remove = new("删除");
    private readonly FlatButton _up = new("上移");
    private readonly FlatButton _down = new("下移");

    private readonly ToolTip _tip = new();
    private bool _tipDisposed;
    private string _writeTip = "";

    /// <summary>悬停行（-1 = 无）。ListBox 不会自己给悬停态，只能靠 MouseMove 自己记。</summary>
    private int _hoverMacro = -1;
    private int _hoverStep = -1;

    /// <summary>当前显示的宏个数（连上时取固件声明的，否则按 RS8 的 16 个预置）。</summary>
    private int _count = MacroCodec.MaxMacros;

    /// <summary>每个宏的步骤序列，索引即宏号-1。这是编辑器的唯一数据源。</summary>
    private List<MacroStep>[] _steps = Array.Empty<List<MacroStep>>();

    private int _index;
    private bool _busy;
    private bool _reading;
    private bool _loaded;
    private bool _dirty;

    /// <summary>写入前读出的原始缓冲，供「恢复备份」使用。</summary>
    private byte[]? _backup;

    /// <summary>全部宏的编码/容量错误（非空表示不可写入）。</summary>
    private string _encodeError = "";

    public MacroPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Theme.Bg.App;
        EnsureCount(MacroCodec.MaxMacros);

        // ---------- 顶部操作条 ----------
        _bar.Dock = DockStyle.Top;
        _bar.BackColor = Theme.Bg.Surface;

        _status.Dock = DockStyle.Fill;
        _status.AutoSize = false;
        _status.TextAlign = ContentAlignment.MiddleRight;   // 整栏高度内垂直居中
        _status.ForeColor = ColIdle;
        _status.Text = "尚未读取。";
        _bar.Controls.Add(_status);

        _actions.Dock = DockStyle.Left;
        _actions.FlowDirection = FlowDirection.LeftToRight;
        _actions.AutoSize = true;
        _actions.WrapContents = false;
        _actions.BackColor = Theme.Bg.Surface;
        _read.Click += (_, _) => _ = ReadAsync();
        _write.Click += (_, _) => WriteToDevice();
        _restore.Click += (_, _) => RestoreBackup();
        _actions.Controls.Add(_read);
        _actions.Controls.Add(_write);
        _actions.Controls.Add(_restore);
        _bar.Controls.Add(_actions);
        _bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        // ---------- 主体：左列表 + 间距 + 右编辑区 ----------
        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.Bg.App;

        _rightCard.Dock = DockStyle.Fill;
        _cardGap.Dock = DockStyle.Left;
        _cardGap.BackColor = Theme.Bg.App;
        _leftCard.Dock = DockStyle.Left;

        BuildLeft(_leftCard);
        BuildRight(_rightCard);

        // WinForms Dock 按控件集合逆序停靠：最后加入的最先停靠，Fill 必须最先加入
        _body.Controls.Add(_rightCard);
        _body.Controls.Add(_cardGap);
        _body.Controls.Add(_leftCard);

        Controls.Add(_body);
        Controls.Add(_bar);

        ApplyDpiMetrics();

        // 先铺满 16 行再选第一行：ListBox 在空列表上设 SelectedIndex 会抛越界
        RefreshMacroList();
        LoadMacro(0);

        _session.ReadOnlyChanged += OnReadOnlyChanged;

        // 切到本页时若还没读过（且没有离线编辑的脏数据）就自动读一次
        VisibleChanged += (_, _) =>
        {
            if (IsDisposed || Disposing) return;
            SyncWriteState();
            if (Visible && !_loaded && !_reading && !_dirty && _session.Ready) _ = ReadAsync();
        };
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.ReadOnlyChanged -= OnReadOnlyChanged;
            _tipDisposed = true;
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _tip.Dispose();
        }
    }

    private void ApplyDpiMetrics()
    {
        _bar.Height = UiLayout.BarHeightFor(this);
        _status.Padding = new Padding(0, 0, Theme.Scale(this, 16), 0);
        _actions.Padding = new Padding(Theme.Scale(this, 12), 0, 0, 0);
        UiLayout.CenterFlowBar(_actions, _bar.Height);

        int gap = Theme.Scale(this, Theme.Metrics.CardGap);
        _body.Padding = new Padding(gap);
        _cardGap.Width = gap;
        _leftCard.Width = Theme.Scale(this, 210);
        _leftCard.Padding = new Padding(Theme.Scale(this, 12), Theme.Scale(this, 10), Theme.Scale(this, 12), Theme.Scale(this, 10));
        _rightCard.Padding = new Padding(Theme.Scale(this, 14), Theme.Scale(this, 10), Theme.Scale(this, 14), Theme.Scale(this, 10));

        _leftTitle.Height = Theme.Scale(this, 26);
        _rightTitle.Height = Theme.Scale(this, 26);

        int itemH = Theme.Scale(this, 28);
        _macroList.ItemHeight = itemH;
        _stepList.ItemHeight = itemH;

        _stepBar.Padding = new Padding(0, Theme.Scale(this, 8), 0, 0);
        _stepBar.Height = Math.Max(Theme.Scale(this, 46), _addText.PreferredSize.Height + Theme.Scale(this, 14));

        _preview.Height = Theme.Scale(this, 46);
        _preview.Padding = new Padding(Theme.Scale(this, 10), Theme.Scale(this, 6), Theme.Scale(this, 10), Theme.Scale(this, 6));

        _usageBar.Height = Theme.Scale(this, 36);

        _footNote.Padding = new Padding(Theme.Scale(this, 10), Theme.Scale(this, 6), Theme.Scale(this, 10), 0);
        _footNote.Height = Theme.Scale(this, 58);
    }

    // ============================================================
    //  ListBox 自绘
    // ============================================================

    /// <summary>
    /// 把 ListBox 改成 owner-draw：选中项用 Accent.Subtle 填充 + 左侧 2px 竖条，
    /// 悬停项用 Bg.Hover，替代系统那个和全局主色对不上的经典蓝。
    /// 保留 ListBox 本体是为了白拿键盘导航与滚动条 —— 自己实现这些不划算。
    /// </summary>
    private void StyleList(ListBox list, bool isMacroList)
    {
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = Theme.Scale(this, 28);
        list.DrawItem += (_, e) => DrawRow(list, e, isMacroList);
        list.MouseMove += (_, e) =>
        {
            int idx = list.IndexFromPoint(e.Location);
            ref int hover = ref (isMacroList ? ref _hoverMacro : ref _hoverStep);
            if (hover == idx) return;
            int old = hover;
            hover = idx;
            if (old >= 0 && old < list.Items.Count) list.Invalidate(list.GetItemRectangle(old));
            if (idx >= 0 && idx < list.Items.Count) list.Invalidate(list.GetItemRectangle(idx));
        };
        list.MouseLeave += (_, _) =>
        {
            ref int hover = ref (isMacroList ? ref _hoverMacro : ref _hoverStep);
            int old = hover;
            if (old < 0) return;
            hover = -1;
            if (old < list.Items.Count) list.Invalidate(list.GetItemRectangle(old));
        };
    }

    private void DrawRow(ListBox list, DrawItemEventArgs e, bool isMacroList)
    {
        if (e.Index < 0 || e.Index >= list.Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // 只能擦自己这一行 —— DrawItem 拿到的 Graphics 是**整个列表**的 DC，
        // 用 g.Clear() 会把已画好的其它行一起抹掉（表现为只剩最后一行）。
        g.FillRectangle(Theme.Brush(Theme.Bg.Surface), e.Bounds);

        bool selected = (e.State & DrawItemState.Selected) != 0;
        int hover = isMacroList ? _hoverMacro : _hoverStep;

        int pad = Theme.Scale(list, 2);
        float radius = Theme.ScaleF(list, Theme.Metrics.ControlRadius);
        var row = new RectangleF(pad, e.Bounds.Y + pad, Math.Max(0, list.ClientSize.Width - pad * 2), Math.Max(0, e.Bounds.Height - pad * 2));
        if (row.Width > 2f && row.Height > 2f)
        {
            if (selected)
            {
                g.FillPath(Theme.Brush(Theme.Accent.Subtle), Theme.RoundedPath(row, radius));
                float barW = Math.Max(2f, Theme.ScaleF(list, 2f));
                float barPad = Theme.ScaleF(list, 4f);
                var bar = new RectangleF(row.X, row.Y + barPad, barW, Math.Max(2f, row.Height - barPad * 2));
                g.FillPath(Theme.Brush(Theme.Accent.Base), Theme.RoundedPath(bar, 1f));
            }
            else if (e.Index == hover)
            {
                g.FillPath(Theme.Brush(Theme.Bg.Hover), Theme.RoundedPath(row, radius));
            }
        }

        int textPadL = Theme.Scale(list, 10);
        int textPadR = Theme.Scale(list, 10);

        if (isMacroList)
        {
            int stepCount = e.Index < _count ? _steps[e.Index].Count : 0;
            string leftText = $"宏 {e.Index + 1}";
            string rightText = stepCount == 0 ? "空" : $"● {stepCount} 步";

            Color leftInk = !list.Enabled ? Theme.Ink.Disabled
                : selected ? Theme.Ink.Primary
                : Theme.Ink.Body;
            Color rightInk = !list.Enabled ? Theme.Ink.Disabled
                : stepCount == 0 ? Theme.Ink.Muted
                : Theme.Accent.Base;

            int rightW = TextRenderer.MeasureText(g, rightText, Theme.Fonts.Caption, Size.Empty, TextFormatFlags.NoPrefix).Width
                       + Theme.Scale(list, 6);
            var leftRect = new Rectangle((int)row.X + textPadL, e.Bounds.Y,
                Math.Max(0, (int)row.Width - textPadL - textPadR - rightW), e.Bounds.Height);
            var rightRect = new Rectangle((int)row.Right - textPadR - rightW, e.Bounds.Y, rightW, e.Bounds.Height);

            TextRenderer.DrawText(g, leftText, selected ? Theme.Fonts.BodyBold : list.Font, leftRect, leftInk,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, rightText, Theme.Fonts.Caption, rightRect, rightInk,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        string text = list.Items[e.Index]?.ToString() ?? "";
        Color ink = !list.Enabled ? Theme.Ink.Disabled
            : selected ? Theme.Ink.Primary
            : Theme.Ink.Body;

        TextRenderer.DrawText(g, text, list.Font,
            new Rectangle((int)row.X + textPadL, e.Bounds.Y, Math.Max(0, (int)row.Width - textPadL - textPadR), e.Bounds.Height), ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    // ============================================================
    //  构建界面
    // ============================================================

    private void BuildLeft(CardPanel card)
    {
        _macroList.Dock = DockStyle.Fill;
        _macroList.BorderStyle = BorderStyle.None;
        _macroList.BackColor = Theme.Bg.Surface;
        _macroList.IntegralHeight = false;
        _macroList.Font = Theme.Fonts.Body;
        StyleList(_macroList, isMacroList: true);
        _macroList.SelectedIndexChanged += (_, _) => LoadMacro(_macroList.SelectedIndex);
        card.Controls.Add(_macroList);

        _leftTitle.Dock = DockStyle.Top;
        _leftTitle.AutoSize = false;
        _leftTitle.Text = "宏列表";
        _leftTitle.Font = Theme.Fonts.Title;
        _leftTitle.ForeColor = Theme.Ink.Primary;
        card.Controls.Add(_leftTitle);
    }

    private void BuildRight(CardPanel card)
    {
        _stepHost.Dock = DockStyle.Fill;
        _stepHost.BackColor = Theme.Bg.Surface;

        _stepList.Dock = DockStyle.Fill;
        _stepList.BorderStyle = BorderStyle.None;
        _stepList.BackColor = Theme.Bg.Surface;
        _stepList.IntegralHeight = false;
        _stepList.Font = Theme.Fonts.Body;
        StyleList(_stepList, isMacroList: false);
        _stepList.SelectedIndexChanged += (_, _) => UpdateEditButtons();
        _stepList.DoubleClick += (_, _) => EditStep();

        _emptyStepsHint.Dock = DockStyle.Fill;
        _emptyStepsHint.AutoSize = false;
        _emptyStepsHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyStepsHint.BackColor = Theme.Bg.Surface;
        _emptyStepsHint.ForeColor = Theme.Ink.Muted;
        _emptyStepsHint.Font = Theme.Fonts.Body;
        _emptyStepsHint.Text = "当前宏为空 — 点击下方「＋打字 / ＋按键 / ＋延时」添加步骤";

        _stepHost.Controls.Add(_stepList);
        _stepHost.Controls.Add(_emptyStepsHint);
        card.Controls.Add(_stepHost);

        _stepBar.Dock = DockStyle.Bottom;
        _stepBar.FlowDirection = FlowDirection.LeftToRight;
        _stepBar.WrapContents = false;
        _stepBar.BackColor = Theme.Bg.Surface;
        _addText.Click += (_, _) => AddText();
        _addKey.Click += (_, _) => AddKey();
        _addDelay.Click += (_, _) => AddDelay();
        _editStep.Click += (_, _) => EditStep();
        _remove.Click += (_, _) => RemoveStep();
        _up.Click += (_, _) => MoveStep(-1);
        _down.Click += (_, _) => MoveStep(1);
        _stepBar.Controls.Add(_addText);
        _stepBar.Controls.Add(_addKey);
        _stepBar.Controls.Add(_addDelay);
        _stepBar.Controls.Add(_editStep);
        _stepBar.Controls.Add(_remove);
        _stepBar.Controls.Add(_up);
        _stepBar.Controls.Add(_down);
        card.Controls.Add(_stepBar);

        _preview.Dock = DockStyle.Bottom;
        _preview.AutoSize = false;
        _preview.BackColor = Theme.Bg.Sunken;
        _preview.TextAlign = ContentAlignment.MiddleLeft;
        _preview.AutoEllipsis = true;
        _preview.ForeColor = ColIdle;
        _preview.Text = "预览：（未选中宏）";
        card.Controls.Add(_preview);

        _usageBar.Dock = DockStyle.Bottom;
        card.Controls.Add(_usageBar);

        _footNote.Dock = DockStyle.Bottom;
        _footNote.AutoSize = false;
        _footNote.BackColor = Theme.Bg.Surface;
        _footNote.ForeColor = ColIdle;
        _footNote.Font = Theme.Fonts.Caption;
        _footNote.Text = "宏只是存在键盘的宏缓冲区里，不会自动触发 —— 到「键位映射」页把某个键设成"
                       + "「宏 N」键码（0x7700–0x770F），按下它就会播放对应的宏。\r\n"
                       + "写入会覆盖键盘上现有的全部宏：写入前自动备份、写入后回读校验，校验不一致可用「恢复备份」退回。";
        card.Controls.Add(_footNote);

        _rightTitle.Dock = DockStyle.Top;
        _rightTitle.AutoSize = false;
        _rightTitle.Text = "步骤列表（按顺序执行）";
        _rightTitle.Font = Theme.Fonts.Emphasis;
        _rightTitle.ForeColor = Theme.Ink.Primary;
        card.Controls.Add(_rightTitle);
    }

    // ============================================================
    //  会话状态
    // ============================================================

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _session.SetUnsavedMacros(dirty);
    }

    private void OnReadOnlyChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(SyncWriteState)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }
        SyncWriteState();
    }

    /// <summary>由 MainForm 在重新检测后调用：若没有离线改动则回到"未读取"；若已有离线编辑则保留步骤。</summary>
    public void OnSessionChanged()
    {
        _loaded = false;
        _reading = false;
        _busy = false;
        _backup = null;
        EnsureCount(CountFromSession());

        if (_dirty)
        {
            RefreshMacroList();
            LoadMacro(_index);
            _status.ForeColor = _session.Ready ? Theme.State.Warn : ColIdle;
            _status.Text = _session.Ready
                ? "已连接 — 当前有离线编排的宏，可点「写入键盘」覆盖或「从键盘读取宏」放弃。"
                : "未连接键盘：当前有未写入的宏修改，连接后可写入。";
            SyncWriteState();
            return;
        }

        SetDirty(false);
        RefreshMacroList();
        LoadMacro(_index);
        _status.ForeColor = ColIdle;
        _status.Text = _session.Ready ? "尚未读取。" : "未连接键盘：可以离线编排宏，连接后才能读写。";
        SyncWriteState();
    }

    /// <summary>刷新按钮可用状态（只读开关或连接状态变化时调用）。</summary>
    public void SyncWriteState()
    {
        if (IsDisposed || Disposing) return;
        bool ro = _session.EffectiveReadOnly;
        bool ready = _session.Ready;

        _read.Enabled = !_busy && ready;
        _write.Enabled = !_busy && ready && !ro && _encodeError.Length == 0;
        _restore.Enabled = !_busy && ready && !ro && _backup != null;

        string tip;
        if (ro)
        {
            tip = _session.ReadOnlyMode
                ? "2.4G 只读模式下无法写入，请使用 USB 有线连接。"
                : "只读模式已开启：不会写入键盘。取消右上角的「只读」后可用。";
        }
        else if (!ready)
        {
            tip = "未连接键盘，无法写入。";
        }
        else if (_encodeError.Length > 0)
        {
            tip = "当前宏无法编码：" + _encodeError;
        }
        else
        {
            tip = "把当前全部宏写入键盘（会覆盖键盘上现有的宏，写入前自动备份）。";
        }

        if (!_tipDisposed && !_write.IsDisposed && _writeTip != tip)
        {
            _writeTip = tip;
            _tip.SetToolTip(_write, tip);
        }
    }

    private int CountFromSession() =>
        _session.MacroCount > 0 ? _session.MacroCount : MacroCodec.MaxMacros;

    private int BufferSizeFromSession() =>
        _session.MacroBufferSize > 0 ? _session.MacroBufferSize : MacroCodec.DefaultBufferSize;

    private void EnsureCount(int count)
    {
        count = Math.Max(1, count);
        if (_steps.Length == count) return;
        var old = _steps;
        _steps = new List<MacroStep>[count];
        for (int i = 0; i < count; i++)
            _steps[i] = i < old.Length ? old[i] : new List<MacroStep>();
        _count = count;
        _index = Math.Clamp(_index, 0, count - 1);
    }

    // ============================================================
    //  读取
    // ============================================================

    private async Task ReadAsync()
    {
        if (_reading || !_session.Ready) return;

        _reading = true;
        SetBusy(true);
        _status.ForeColor = ColIdle;
        _status.Text = "正在读取宏缓冲…";
        try
        {
            // DeviceSession 内部对 HID 收发做了串行化，后台线程调用是安全的
            var buf = await Task.Run(() => _session.ReadMacroBuffer()).ConfigureAwait(true);
            if (IsDisposed) return;

            if (buf == null)
            {
                _status.ForeColor = ColErr;
                _status.Text = "读取失败（键盘可能休眠，按任意键唤醒后重试）。";
                return;
            }
            ApplyBuffer(buf, out int nonEmpty);
            _status.ForeColor = nonEmpty > 0 ? ColOk : ColIdle;
            _status.Text = nonEmpty > 0
                ? $"已读取 {_count} 个宏（{nonEmpty} 个有内容，{_count - nonEmpty} 个为空）。"
                : $"已读取 {_count} 个宏（全为空）。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "读取宏出错：" + ex.Message;
        }
        finally
        {
            _reading = false;
            if (!IsDisposed) SetBusy(false);
        }
    }

    /// <summary>把一整块缓冲铺进编辑器。不负责状态文案（由调用方决定）。</summary>
    private void ApplyBuffer(byte[] buffer, out int nonEmpty)
    {
        EnsureCount(CountFromSession());
        var parts = MacroCodec.SplitBuffer(buffer, _count);
        nonEmpty = 0;
        for (int i = 0; i < _count; i++)
        {
            _steps[i] = MacroCodec.Decode(parts[i]);
            if (parts[i].Length > 0) nonEmpty++;
        }
        _loaded = true;
        SetDirty(false);
        RefreshMacroList();
        LoadMacro(_index);
    }

    // ============================================================
    //  写入
    // ============================================================

    private void WriteToDevice()
    {
        if (!_session.Ready)
        {
            MessageBox.Show(this, "未连接键盘，无法写入。", "RS8Control",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_session.EffectiveReadOnly)
        {
            MessageBox.Show(this,
                _session.ReadOnlyMode
                    ? "当前处于 2.4G 只读模式，无法写入键盘。请改用 USB 有线连接。"
                    : "已开启只读模式，不会写入键盘。\r\n请先取消右上角的「只读」。",
                "RS8Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!TryBuildFromSteps(out byte[] buffer, out string error))
        {
            MessageBox.Show(this, "当前宏无法编码，没有写入：\r\n\r\n" + error, "RS8Control",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            $"写入会覆盖键盘上现有的全部 {_count} 个宏。\r\n"
            + "写入前会自动读出当前缓冲做备份，写入后回读校验；校验不一致可用「恢复备份」退回。\r\n\r\n"
            + "确定要写入吗？",
            "写入宏", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;

        _ = WriteAsync(buffer);
    }

    private async Task WriteAsync(byte[] buffer)
    {
        SetBusy(true);
        try
        {
            _status.ForeColor = ColIdle;
            _status.Text = "正在备份键盘上的宏…";

            var backup = await Task.Run(() => _session.ReadMacroBuffer()).ConfigureAwait(true);
            if (IsDisposed) return;
            if (backup == null)
            {
                // 读不出备份就不敢写：万一写坏没有任何退路
                _status.ForeColor = ColErr;
                _status.Text = "读不出当前宏缓冲，为安全起见已放弃写入。";
                return;
            }
            _backup = backup;
            SyncWriteState();

            _status.Text = "正在写入并回读校验…";
            bool ok = await Task.Run(() => _session.WriteMacroBuffer(buffer)).ConfigureAwait(true);
            if (IsDisposed) return;

            if (ok)
            {
                ApplyBuffer(buffer, out _);
                _status.ForeColor = ColOk;
                _status.Text = $"已写入 {_count} 个宏，回读校验一致。";
                return;
            }

            _status.ForeColor = ColErr;
            _status.Text = "写入后回读校验不一致，键盘上的宏可能只写了一半。";
            var r = MessageBox.Show(this,
                "写入后回读校验不一致，键盘上的宏可能处于半写入状态。\r\n\r\n"
                + "是否立刻用刚读出的备份恢复？",
                "写入失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) await RestoreCoreAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "写入宏出错：" + ex.Message;
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void RestoreBackup()
    {
        if (_backup == null) return;
        var answer = MessageBox.Show(this,
            "用写入前读出的备份覆盖键盘上当前的宏缓冲？",
            "恢复备份", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        _ = RestoreWithBusyAsync();
    }

    private async Task RestoreWithBusyAsync()
    {
        SetBusy(true);
        try { await RestoreCoreAsync(); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task<bool> RestoreCoreAsync()
    {
        var backup = _backup;
        if (backup == null)
        {
            _status.ForeColor = ColErr;
            _status.Text = "没有可用的备份。";
            return false;
        }

        _status.ForeColor = ColIdle;
        _status.Text = "正在用备份恢复…";
        bool ok = await Task.Run(() => _session.WriteMacroBuffer(backup)).ConfigureAwait(true);
        if (IsDisposed) return false;

        if (ok)
        {
            ApplyBuffer(backup, out _);
            _status.ForeColor = ColOk;
            _status.Text = "已用备份恢复，回读校验一致。";
        }
        else
        {
            _status.ForeColor = ColErr;
            _status.Text = "恢复失败，键盘上的宏可能仍不完整。";
        }
        return ok;
    }

    /// <summary>把编辑器里的全部宏编码成完整缓冲（含每个宏的结束符与补 0）。</summary>
    private bool TryBuildFromSteps(out byte[] buffer, out string error)
    {
        buffer = Array.Empty<byte>();
        error = "";

        int size = BufferSizeFromSession();
        var encoded = new byte[_count][];
        for (int i = 0; i < _count; i++)
        {
            if (!MacroCodec.TryEncode(_steps[i], out encoded[i], out string e))
            {
                error = $"宏 {i + 1}：{e}";
                return false;
            }
        }
        if (!MacroCodec.TryBuildBuffer(encoded, size, out buffer, out string e2))
        {
            error = e2;
            return false;
        }
        return true;
    }

    // ============================================================
    //  编辑
    // ============================================================

    private List<MacroStep>? Current() =>
        _index >= 0 && _index < _count ? _steps[_index] : null;

    private bool TryPromptText(string title, string initial, out string text)
    {
        text = "";
        string? input = InputDialog.AskText(this, title,
            "要打出来的文字。只支持可打印 ASCII（字母、数字、符号、空格），不支持中文。", initial);
        if (input == null) return false;
        if (input.Length == 0)
        {
            MessageBox.Show(this, "文字是空的，没有保存。", "RS8Control",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        // 先按正式编码器验一遍：编码器拒绝什么，这里就拒绝什么，两处判据不会走偏
        if (!MacroCodec.TryEncode(new MacroStep[] { new MacroText(input) }, out _, out string error))
        {
            MessageBox.Show(this, "这段文字放不进宏：\r\n\r\n" + error, "RS8Control",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        text = input;
        return true;
    }

    private void AddText()
    {
        var steps = Current();
        if (steps == null) return;
        if (!TryPromptText("添加「打字」步骤", "", out string text)) return;

        steps.Add(new MacroText(text));
        AfterEdit();
    }

    private void AddKey()
    {
        var steps = Current();
        if (steps == null) return;

        using var dlg = new MacroKeyStepDialog(Keycodes.KC_NO, MacroKeyKind.Tap,
            _session.Definition?.CustomKeycodes, Math.Max(_session.LayerCount, 4), "添加「按键」步骤");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        steps.Add(new MacroKey(dlg.Keycode, dlg.Kind));
        AfterEdit();
    }

    private void AddDelay()
    {
        var steps = Current();
        if (steps == null) return;

        int? ms = InputDialog.AskNumber(this, "添加「延时」步骤",
            $"延时的毫秒数（{MacroCodec.MinDelayMs}–{MacroCodec.MaxDelayMs}）：", 100,
            MacroCodec.MinDelayMs, MacroCodec.MaxDelayMs);
        if (ms == null) return;

        steps.Add(new MacroDelay(ms.Value));
        AfterEdit();
    }

    private void EditStep()
    {
        if (_busy) return;
        var steps = Current();
        if (steps == null) return;
        int i = _stepList.SelectedIndex;
        if (i < 0 || i >= steps.Count) return;

        MacroStep updated;
        switch (steps[i])
        {
            case MacroText t:
            {
                if (!TryPromptText("编辑「打字」步骤", t.Text, out string text)) return;
                updated = new MacroText(text);
                break;
            }
            case MacroDelay d:
            {
                int? ms = InputDialog.AskNumber(this, "编辑「延时」步骤",
                    $"延时的毫秒数（{MacroCodec.MinDelayMs}–{MacroCodec.MaxDelayMs}）：", d.Ms,
                    MacroCodec.MinDelayMs, MacroCodec.MaxDelayMs);
                if (ms == null) return;
                updated = new MacroDelay(ms.Value);
                break;
            }
            case MacroKey k:
            {
                using var dlg = new MacroKeyStepDialog(k.Keycode, k.Kind,
                    _session.Definition?.CustomKeycodes, Math.Max(_session.LayerCount, 4), "编辑「按键」步骤");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                updated = new MacroKey(dlg.Keycode, dlg.Kind);
                break;
            }
            default:
                return;
        }

        steps[i] = updated;
        AfterEdit();
        _stepList.SelectedIndex = i;
    }

    private void RemoveStep()
    {
        var steps = Current();
        if (steps == null) return;
        int i = _stepList.SelectedIndex;
        if (i < 0 || i >= steps.Count) return;

        steps.RemoveAt(i);
        AfterEdit();
        if (steps.Count > 0) _stepList.SelectedIndex = Math.Min(i, steps.Count - 1);
    }

    private void MoveStep(int delta)
    {
        var steps = Current();
        if (steps == null) return;
        int i = _stepList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= steps.Count) return;

        (steps[i], steps[j]) = (steps[j], steps[i]);
        AfterEdit();
        _stepList.SelectedIndex = j;
    }

    /// <summary>任何一次编辑之后：标记未保存、刷新列表、预览与按钮状态。</summary>
    private void AfterEdit()
    {
        SetDirty(true);
        RefreshMacroList();
        RefreshStepList();
        UpdatePreview();
        UpdateEditButtons();
        SyncWriteState();
    }

    private void LoadMacro(int i)
    {
        if (i < 0 || i >= _count)
        {
            _stepList.Items.Clear();
            _stepList.Visible = false;
            _emptyStepsHint.Visible = true;
            UpdatePreview();
            UpdateEditButtons();
            SyncWriteState();
            return;
        }
        _index = i;
        RefreshStepList();
        UpdatePreview();
        UpdateEditButtons();
        SyncWriteState();
    }

    private void RefreshMacroList()
    {
        int keep = _macroList.SelectedIndex;
        _macroList.BeginUpdate();
        try
        {
            _macroList.Items.Clear();
            for (int i = 0; i < _count; i++)
            {
                int n = _steps[i].Count;
                _macroList.Items.Add(n == 0 ? $"宏 {i + 1} — 空" : $"宏 {i + 1} ● {n} 步");
            }
        }
        finally { _macroList.EndUpdate(); }

        _macroList.SelectedIndex = Math.Clamp(keep, 0, _count - 1);
    }

    private void RefreshStepList()
    {
        int keep = _stepList.SelectedIndex;
        var steps = Current();

        _stepList.BeginUpdate();
        try
        {
            _stepList.Items.Clear();
            if (steps != null)
                for (int i = 0; i < steps.Count; i++)
                    _stepList.Items.Add($"{i + 1}. {MacroCodec.DescribeStep(steps[i])}");
        }
        finally { _stepList.EndUpdate(); }

        bool empty = steps == null || steps.Count == 0;
        _stepList.Visible = !empty;
        _emptyStepsHint.Visible = empty;

        if (keep >= 0 && keep < _stepList.Items.Count) _stepList.SelectedIndex = keep;
        else if (_stepList.Items.Count > 0) _stepList.SelectedIndex = _stepList.Items.Count - 1;
    }

    private void UpdatePreview()
    {
        int bufferSize = BufferSizeFromSession();
        int totalBytes = 0;
        string allError = "";

        for (int i = 0; i < _count; i++)
        {
            if (!MacroCodec.TryEncode(_steps[i], out byte[] encodedMacro, out string err))
            {
                if (allError.Length == 0) allError = $"宏 {i + 1}：{err}";
            }
            else
            {
                totalBytes += encodedMacro.Length + 1;   // 每个宏末尾含 1 字节 0x00 结束符
            }
        }
        if (allError.Length == 0 && totalBytes > bufferSize)
            allError = $"全部宏共需 {totalBytes} 字节（含结束符），超过缓冲上限 {bufferSize} 字节。";

        _encodeError = allError;
        _usageBar.SetUsage(totalBytes, bufferSize, allError.Length > 0);

        string prefix = _dirty ? "预览（有未写入的改动）：" : "预览：";
        var steps = Current();
        if (steps == null)
        {
            _preview.ForeColor = ColIdle;
            _preview.Text = prefix + "（未选中宏）";
            return;
        }

        if (MacroCodec.TryEncode(steps, out byte[] curEncoded, out string curErr))
        {
            if (allError.Length > 0)
            {
                _preview.ForeColor = ColErr;
                _preview.Text = $"{prefix}{MacroCodec.Describe(steps)}（{curEncoded.Length} 字节）  |  {allError}";
            }
            else
            {
                _preview.ForeColor = Theme.Ink.Primary;
                _preview.Text = $"{prefix}{MacroCodec.Describe(steps)}      编码 {curEncoded.Length} 字节";
            }
        }
        else
        {
            _preview.ForeColor = ColErr;
            _preview.Text = "无法编码：" + curErr;
        }
    }

    private void UpdateEditButtons()
    {
        var steps = Current();
        bool has = !_busy && steps != null;
        _addText.Enabled = has;
        _addKey.Enabled = has;
        _addDelay.Enabled = has;

        int sel = _stepList.SelectedIndex;
        bool onStep = has && sel >= 0 && sel < (steps?.Count ?? 0);
        _editStep.Enabled = onStep;
        _remove.Enabled = onStep;
        _up.Enabled = onStep && sel > 0;
        _down.Enabled = onStep && steps != null && sel < steps.Count - 1;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _macroList.Enabled = !busy;
        _stepList.Enabled = !busy;
        UpdateEditButtons();
        SyncWriteState();
    }
}

/// <summary>宏缓冲总占用进度条：显示已用字节 / 总容量及状态色（正常 / &gt;80% 警告 / &gt;100% 或错误危险）。</summary>
internal sealed class BufferUsageBar : Control
{
    private int _used;
    private int _total = MacroCodec.DefaultBufferSize;
    private bool _hasError;

    public BufferUsageBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        BackColor = Theme.Bg.Surface;
        Height = Theme.Scale(this, 36);
    }

    public void SetUsage(int usedBytes, int totalBytes, bool hasError)
    {
        int safeTotal = Math.Max(1, totalBytes);
        if (_used == usedBytes && _total == safeTotal && _hasError == hasError) return;
        _used = usedBytes;
        _total = safeTotal;
        _hasError = hasError;
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = Theme.Scale(this, 36);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        Color backdrop = Parent?.BackColor ?? Theme.Bg.Surface;
        if (backdrop == Color.Empty) backdrop = Theme.Bg.Surface;
        g.Clear(backdrop);

        int padX = Theme.Scale(this, 2);
        int topPad = Theme.Scale(this, 4);
        int trackH = Math.Max(4, Theme.Scale(this, 6));
        int textH = Math.Max(Theme.Fonts.Caption.Height, Theme.Scale(this, 16));

        double ratio = (double)_used / _total;
        bool over = _used > _total;
        bool warn = !over && !_hasError && ratio > 0.8;

        Color barColor = (_hasError || over) ? Theme.State.Danger
            : warn ? Theme.State.Warn
            : Theme.Accent.Base;
        Color inkColor = (_hasError || over) ? Theme.State.Danger
            : warn ? Theme.State.Warn
            : Theme.Ink.Body;

        string leftLabel = $"宏缓冲占用：{_used} / {_total} 字节";
        if (over) leftLabel += $"（超出 {_used - _total} 字节）";
        else if (_hasError) leftLabel += "（存在编码错误）";

        string rightLabel = $"{(int)Math.Round(ratio * 100.0)}%";

        int innerW = Math.Max(0, Width - padX * 2);
        TextRenderer.DrawText(g, leftLabel, Theme.Fonts.Caption,
            new Rectangle(padX, topPad, innerW, textH), inkColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, rightLabel, Theme.Fonts.Caption,
            new Rectangle(padX, topPad, innerW, textH), inkColor,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int trackY = topPad + textH + Theme.Scale(this, 4);
        if (innerW > 4 && trackY + trackH <= Height)
        {
            float radius = trackH / 2f;
            var trackRect = new RectangleF(padX, trackY, innerW, trackH);
            g.FillPath(Theme.Brush(Theme.Bg.Sunken), Theme.RoundedPath(trackRect, radius));

            float clamped = (float)Math.Clamp(ratio, 0.0, 1.0);
            float fillW = innerW * clamped;
            if (fillW > 0.5f)
            {
                fillW = Math.Clamp(fillW, trackH, innerW);
                var fillRect = new RectangleF(padX, trackY, fillW, trackH);
                g.FillPath(Theme.Brush(barColor), Theme.RoundedPath(fillRect, radius));
            }
        }
    }
}

/// <summary>宏「按键」步骤编辑对话框：选择基本键码与动作类型（点按 / 按下 / 松开）。</summary>
internal sealed class MacroKeyStepDialog : Form
{
    private readonly IReadOnlyList<string>? _customKeycodes;
    private readonly int _layerCount;
    private ushort _rawKeycode;
    private MacroKeyKind _kind;

    private readonly Label _keyCaption = new() { Text = "按  键：", AutoSize = true };
    private readonly Label _keyDesc = new() { AutoSize = false };
    private readonly FlatButton _pick = new("选择按键…");

    private readonly Label _kindCaption = new() { Text = "动  作：", AutoSize = true };
    private readonly FlatButton _tap = new("点按 (Tap)");
    private readonly FlatButton _down = new("按下 (Down)");
    private readonly FlatButton _up = new("松开 (Up)");

    private readonly Label _hint = new() { AutoSize = false };
    private readonly FlatButton _ok = new("确定", ButtonVariant.Primary);
    private readonly FlatButton _cancel = new("取消");

    public byte Keycode { get; private set; }
    public MacroKeyKind Kind { get; private set; }

    public MacroKeyStepDialog(
        ushort initialKeycode,
        MacroKeyKind initialKind,
        IReadOnlyList<string>? customKeycodes,
        int layerCount,
        string title = "按键步骤")
    {
        _rawKeycode = initialKeycode;
        _kind = initialKind;
        _customKeycodes = customKeycodes;
        _layerCount = layerCount;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = Theme.Fonts.Body;
        BackColor = Theme.Bg.Surface;
        ForeColor = Theme.Ink.Primary;

        _keyCaption.ForeColor = Theme.Ink.Body;
        _keyDesc.TextAlign = ContentAlignment.MiddleLeft;
        _keyDesc.AutoEllipsis = true;
        _keyDesc.BackColor = Theme.Bg.Sunken;

        _pick.Click += (_, _) => PickKey();

        _kindCaption.ForeColor = Theme.Ink.Body;
        _tap.Click += (_, _) => SetKind(MacroKeyKind.Tap);
        _down.Click += (_, _) => SetKind(MacroKeyKind.Down);
        _up.Click += (_, _) => SetKind(MacroKeyKind.Up);

        _hint.Font = Theme.Fonts.Caption;
        _hint.ForeColor = Theme.Ink.Muted;
        _hint.Text = "仅支持基本键码（0x02–0xFF）；使用「按下」时请记得配对「松开」。";

        _cancel.DialogResult = DialogResult.Cancel;
        _ok.Click += (_, _) => OnAccept();

        Controls.AddRange(new Control[]
        {
            _keyCaption, _keyDesc, _pick,
            _kindCaption, _tap, _down, _up,
            _hint, _ok, _cancel,
        });

        AcceptButton = _ok;
        CancelButton = _cancel;

        SetKind(_kind);
        UpdateKeyDescription();
        LayoutControls();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutControls();
    }

    private void LayoutControls()
    {
        int w = Theme.Scale(this, 470);
        int h = Theme.Scale(this, 206);
        ClientSize = new Size(w, h);

        int pad = Theme.Scale(this, 16);
        int gap = Theme.Scale(this, 8);
        int fieldX = Theme.Scale(this, 80);

        int row1Y = Theme.Scale(this, 18);
        int row1H = Math.Max(_pick.PreferredSize.Height, Theme.Scale(this, Theme.Metrics.ControlHeight));
        _keyCaption.Location = new Point(pad, UiLayout.CenterInRow(_keyCaption, row1Y, row1H));
        _pick.Location = new Point(w - pad - _pick.Width, UiLayout.CenterInRow(_pick, row1Y, row1H));
        _keyDesc.SetBounds(fieldX, row1Y, Math.Max(Theme.Scale(this, 120), _pick.Left - gap - fieldX), row1H);
        _keyDesc.Padding = new Padding(Theme.Scale(this, 8), 0, Theme.Scale(this, 8), 0);

        int row2Y = row1Y + row1H + Theme.Scale(this, 16);
        int row2H = UiLayout.RowHeight(_kindCaption, _tap, _down, _up);
        _kindCaption.Location = new Point(pad, UiLayout.CenterInRow(_kindCaption, row2Y, row2H));
        _tap.Location = new Point(fieldX, UiLayout.CenterInRow(_tap, row2Y, row2H));
        _down.Location = new Point(_tap.Right + gap, UiLayout.CenterInRow(_down, row2Y, row2H));
        _up.Location = new Point(_down.Right + gap, UiLayout.CenterInRow(_up, row2Y, row2H));

        int hintY = row2Y + row2H + Theme.Scale(this, 14);
        _hint.SetBounds(pad, hintY, w - pad * 2, Theme.Scale(this, 32));

        int btnY = h - pad - _ok.Height;
        _cancel.Location = new Point(w - pad - _cancel.Width, btnY);
        _ok.Location = new Point(_cancel.Left - gap - _ok.Width, btnY);
    }

    private void SetKind(MacroKeyKind kind)
    {
        _kind = kind;
        _tap.Variant = kind == MacroKeyKind.Tap ? ButtonVariant.Primary : ButtonVariant.Secondary;
        _down.Variant = kind == MacroKeyKind.Down ? ButtonVariant.Primary : ButtonVariant.Secondary;
        _up.Variant = kind == MacroKeyKind.Up ? ButtonVariant.Primary : ButtonVariant.Secondary;
    }

    private void UpdateKeyDescription()
    {
        if (_rawKeycode > 0x01 && _rawKeycode <= 0xFF)
        {
            _keyDesc.ForeColor = Theme.Ink.Primary;
            _keyDesc.Text = $"{Keycodes.Describe(_rawKeycode, _customKeycodes)}  (0x{_rawKeycode:X2})";
        }
        else
        {
            _keyDesc.ForeColor = Theme.Ink.Muted;
            _keyDesc.Text = "（未选择 — 请点击右侧「选择按键…」）";
        }
    }

    private void PickKey()
    {
        using var dlg = new KeycodePicker(_rawKeycode, _customKeycodes, _layerCount);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (!ValidateKeycode(dlg.Selected, out string msg))
        {
            MessageBox.Show(this, msg, "RS8Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _rawKeycode = dlg.Selected;
        UpdateKeyDescription();
    }

    private void OnAccept()
    {
        if (!ValidateKeycode(_rawKeycode, out string msg))
        {
            MessageBox.Show(this, msg, "RS8Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Keycode = (byte)_rawKeycode;
        Kind = _kind;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool ValidateKeycode(ushort kc, out string message)
    {
        if (kc > 0x01 && kc <= 0xFF)
        {
            message = "";
            return true;
        }

        message = "宏里只能放基本键码（单字节，0x02–0xFF：字母、修饰键、媒体键等）。\r\n\r\n"
                + "不支持 0x00（无键码）和 0x01（▽ 透传）；"
                + "层功能、组合键、宏键码这类 16 位复合键码无法由宏发送，请改到「键位映射」页设置。";
        return false;
    }
}

/// <summary>宏编辑用的极简输入框（单行/多行文本、数字各一个入口）。</summary>
internal sealed class InputDialog : Form
{
    private readonly bool _numeric;
    private readonly Label _prompt = new();
    private readonly FlatTextBox _text = new();
    private readonly FlatNumeric _num = new();
    private readonly FlatButton _ok = new("确定", ButtonVariant.Primary);
    private readonly FlatButton _cancel = new("取消");

    private InputDialog(string title, string prompt, string initial, int min, int max, bool numeric)
    {
        _numeric = numeric;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = Theme.Fonts.Body;
        BackColor = Theme.Bg.Surface;
        ForeColor = Theme.Ink.Primary;

        _prompt.Text = prompt;
        _prompt.AutoSize = false;
        _prompt.ForeColor = Theme.Ink.Body;
        Controls.Add(_prompt);

        if (numeric)
        {
            _num.Minimum = min;
            _num.Maximum = max;
            _num.Value = Math.Clamp(int.TryParse(initial, out int v) ? v : min, min, max);
            Controls.Add(_num);
        }
        else
        {
            _text.Multiline = true;
            _text.ScrollBars = ScrollBars.Vertical;
            _text.AcceptsReturn = false;   // 回车＝确定，换行本身宏也发不出去
            _text.Font = Theme.Fonts.Mono;
            _text.Text = initial;
            Controls.Add(_text);
        }

        _ok.DialogResult = DialogResult.OK;
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_ok);
        Controls.Add(_cancel);
        AcceptButton = _ok;
        CancelButton = _cancel;

        LayoutControls();
        Shown += (_, _) =>
        {
            if (_numeric) _num.Focus();
            else _text.Focus();
        };
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutControls();
    }

    private void LayoutControls()
    {
        int w = Theme.Scale(this, 440);
        int h = Theme.Scale(this, _numeric ? 164 : 244);
        ClientSize = new Size(w, h);

        int pad = Theme.Scale(this, 14);
        int gap = Theme.Scale(this, 8);
        int promptH = Theme.Scale(this, 40);
        _prompt.SetBounds(pad, Theme.Scale(this, 12), w - pad * 2, promptH);

        int inputY = Theme.Scale(this, 56);
        if (_numeric)
        {
            _num.SetBounds(pad, inputY, Theme.Scale(this, 132), Theme.Scale(this, Theme.Metrics.ControlHeight));
        }
        else
        {
            _text.SetBounds(pad, inputY, w - pad * 2, Theme.Scale(this, 126));
        }

        int btnY = h - pad - _ok.Height;
        _cancel.Location = new Point(w - pad - _cancel.Width, btnY);
        _ok.Location = new Point(_cancel.Left - gap - _ok.Width, btnY);
    }

    /// <summary>取消返回 null。</summary>
    public static string? AskText(IWin32Window owner, string title, string prompt, string initial)
    {
        using var dlg = new InputDialog(title, prompt, initial, 0, 0, false);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._text.Text : null;
    }

    /// <summary>取消返回 null。</summary>
    public static int? AskNumber(IWin32Window owner, string title, string prompt, int initial, int min, int max)
    {
        using var dlg = new InputDialog(title, prompt, initial.ToString(), min, max, true);
        return dlg.ShowDialog(owner) == DialogResult.OK ? (int)dlg._num.Value : null;
    }
}
