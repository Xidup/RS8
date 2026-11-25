using System.Reflection;
using RS8Control.Hid;
using RS8Control.Protocol;
using RS8Control.Ui.Panels;

namespace RS8Control.Ui;

public sealed class MainForm : Form
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeFallback = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly DeviceSession _session = new();
    private readonly TabHost _tabs = new();
    private readonly Panel _top = new();
    private readonly StatusBadge _badge = new();
    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly FlatButton _refresh = new("重新检测");
    private readonly Label _readOnlyCaption = new();
    private readonly ToggleSwitch _readOnly = new();
    private readonly ToolTip _tip = new();

    private Icon? _appIcon;

    /// <summary>是否正在后台检测设备（避免重复触发 Refresh）。</summary>
    private bool _detecting;
    private bool _syncingReadOnly;

    /// <summary>检测期间的状态圆点脉冲（只改亮度，不引入额外依赖）。</summary>
    private readonly System.Windows.Forms.Timer _pulse = new();
    private int _pulsePhase;

    /// <summary>USB 热插拔防抖定时器与上次 RS8 接口路径快照。</summary>
    private readonly System.Windows.Forms.Timer _hotplugDebounce = new();
    private string[] _lastHidPaths = Array.Empty<string>();
    private bool _checkingHotplug;

    private readonly KeymapPanel _keymapPanel;

    public MainForm()
    {
        Text = "RS8Control — RICKS RS8 键盘配置工具";
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Fonts.Body;
        BackColor = Theme.Bg.App;
        ForeColor = Theme.Ink.Primary;

        _appIcon = LoadAppIcon();
        if (_appIcon != null) Icon = _appIcon;

        _keymapPanel = new KeymapPanel(_session);

        // ---------- 顶部状态条（纯状态显示，读写按钮留在各自的页里） ----------
        _top.Dock = DockStyle.Top;
        _top.BackColor = Theme.Bg.Surface;
        _top.Padding = new Padding(0, 0, 0, 1);
        _top.Paint += (_, e) =>
            e.Graphics.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, _top.Height - 1, _top.Width, _top.Height - 1);
        _top.Resize += (_, _) => LayoutTopBar();

        _badge.SetState(Theme.State.Idle, Theme.State.IdleSubtle, Theme.Ink.Body, "检测中");
        _top.Controls.Add(_badge);

        _status.Text = "正在检测 RS8 键盘…";
        _status.AutoSize = true;
        _status.Font = Theme.Fonts.Emphasis;
        _status.ForeColor = Theme.Ink.Primary;
        _top.Controls.Add(_status);

        _detail.Text = "";
        _detail.AutoSize = true;
        _detail.Font = Theme.Fonts.Caption;
        _detail.ForeColor = Theme.Ink.Muted;
        _top.Controls.Add(_detail);

        _readOnlyCaption.Text = "只读";
        _readOnlyCaption.AutoSize = true;
        _readOnlyCaption.ForeColor = Theme.Ink.Body;
        _top.Controls.Add(_readOnlyCaption);

        _readOnly.CheckedChanged += (_, _) =>
        {
            if (_syncingReadOnly) return;
            _session.UserReadOnly = _readOnly.Checked;
        };
        _top.Controls.Add(_readOnly);

        const string roTip = "开启后禁止一切向键盘写入的操作（改键 / 灯效 / 宏 / 原始调试），防止误改配置。";
        _tip.SetToolTip(_readOnly, roTip);
        _tip.SetToolTip(_readOnlyCaption, roTip);

        _refresh.Click += (_, _) => Connect();
        _top.Controls.Add(_refresh);

        LayoutTopBar();

        // ---------- 标签页（除首屏键位映射外全部懒加载） ----------
        _tabs.Dock = DockStyle.Fill;
        _tabs.AddPage("键位映射", _keymapPanel);
        _tabs.AddLazyPage("灯效", () => new LightingPanel(_session), c => ((LightingPanel)c).OnSessionChanged());
        _tabs.AddLazyPage("宏", () => new MacroPanel(_session), c => ((MacroPanel)c).OnSessionChanged());
        _tabs.AddLazyPage("快捷键速查", () => new ShortcutPanel(_session), c => ((ShortcutPanel)c).OnSessionChanged());
        _tabs.AddLazyPage("使用说明", () => new GuidePanel(_session));
        _tabs.AddLazyPage("诊断与调试", () => new DebugHubPanel(_session), c => ((DebugHubPanel)c).OnSessionChanged());

        Controls.Add(_tabs);
        Controls.Add(_top);

        _pulse.Interval = 90;
        _pulse.Tick += (_, _) =>
        {
            _pulsePhase = (_pulsePhase + 1) % 24;
            double t = (1 - Math.Cos(_pulsePhase / 24.0 * Math.PI * 2)) / 2;
            _badge.SetDotColor(Theme.Dim(Theme.State.Idle, 0.35 + 0.65 * t));
        };

        _hotplugDebounce.Interval = 450;
        _hotplugDebounce.Tick += (_, _) =>
        {
            _hotplugDebounce.Stop();
            _ = CheckHotplugAndReconnectAsync();
        };

        _session.Changed += OnSessionChanged;
        _session.DirtyChanged += OnDirtyChanged;
        _session.ReadOnlyChanged += OnReadOnlyChanged;
        Shown += (_, _) => Connect();
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RS8Control.app.ico");
            return s == null ? null : new Icon(s);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 强制浅色标题栏，避免 Windows 深色模式下出现黑标题栏配浅色界面
        int off = 0;
        try
        {
            if (DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref off, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkModeFallback, ref off, sizeof(int));
        }
        catch { /* 非 Win10/11 环境忽略 */ }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutTopBar();
    }

    private void LayoutTopBar()
    {
        _top.Height = UiLayout.MainBarHeightFor(this);
        int padX = Theme.Scale(this, 18);
        int rightPad = Theme.Scale(this, 16);
        int gap = Theme.Scale(this, 10);

        int mainRowH = Math.Max(UiLayout.RowHeight(_badge, _status, _readOnly, _refresh), Theme.Scale(this, 32));
        int hintRowH = Math.Max(_detail.PreferredSize.Height, UiLayout.LabelLineHeight(_detail.Font));
        var rows = UiLayout.TwoRowBar(_top.Height, mainRowH, hintRowH, Theme.Scale(this, 2));

        // 右侧操作区显式右对齐（不使用 Dock=Right + AutoSize=true 避免缩放错位）
        int rx = Math.Max(padX + Theme.Scale(this, 260), _top.ClientSize.Width - rightPad);
        _refresh.Left = rx - _refresh.Width;
        _refresh.Top = UiLayout.CenterInRow(_refresh, rows.MainTop, rows.MainHeight);

        _readOnly.Left = _refresh.Left - Theme.Scale(this, 14) - _readOnly.Width;
        _readOnly.Top = UiLayout.CenterInRow(_readOnly, rows.MainTop, rows.MainHeight);

        _readOnlyCaption.Left = _readOnly.Left - Theme.Scale(this, 6) - _readOnlyCaption.Width;
        _readOnlyCaption.Top = UiLayout.CenterInRow(_readOnlyCaption, rows.MainTop, rows.MainHeight);

        // 左侧徽章 + 状态文字 + 明细行
        _badge.Left = padX;
        _badge.Top = UiLayout.CenterInRow(_badge, rows.MainTop, rows.MainHeight);

        _status.Left = _badge.Right + gap;
        _status.Top = UiLayout.CenterInRow(_status, rows.MainTop, rows.MainHeight);
        int maxTextW = Math.Max(Theme.Scale(this, 140), _readOnlyCaption.Left - _status.Left - Theme.Scale(this, 12));
        _status.MaximumSize = new Size(maxTextW, mainRowH);

        _detail.Left = padX;
        _detail.Top = UiLayout.CenterInRow(_detail, rows.HintTop, rows.HintHeight);
        _detail.MaximumSize = new Size(Math.Max(Theme.Scale(this, 200), _readOnlyCaption.Left - padX - Theme.Scale(this, 12)), hintRowH);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange)
        {
            int ev = unchecked((int)m.WParam.ToInt64());
            if (ev is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemoveComplete)
            {
                _hotplugDebounce.Stop();
                _hotplugDebounce.Start();
            }
        }
        base.WndProc(ref m);
    }

    private async Task CheckHotplugAndReconnectAsync()
    {
        if (_checkingHotplug || _detecting || IsDisposed) return;
        _checkingHotplug = true;
        try
        {
            var next = await Task.Run(() => Win32Hid.Enumerate(DeviceDiscovery.KnownVid)
                .Select(a => a.DevicePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray()).ConfigureAwait(true);

            if (IsDisposed) return;
            if (!next.SequenceEqual(_lastHidPaths, StringComparer.OrdinalIgnoreCase))
            {
                _lastHidPaths = next;
                Connect();
            }
        }
        catch { /* 热插拔探测失败忽略 */ }
        finally { _checkingHotplug = false; }
    }

    /// <summary>
    /// 设备检测放到后台线程：握手可能耗时（无线链路下每条命令都可能超时），
    /// 绝不能占着 UI 线程。检测期间窗口保持可交互，不用 WaitCursor。
    /// </summary>
    private void Connect()
    {
        if (_detecting) return;
        _detecting = true;
        _refresh.Enabled = false;
        _badge.SetState(Theme.State.Idle, Theme.State.IdleSubtle, Theme.Ink.Body, "检测中");
        _status.Text = "正在检测 RS8 键盘…";
        _detail.Text = "正在枚举 HID 接口并握手，请稍候。";
        _detail.ForeColor = Theme.Ink.Muted;
        LayoutTopBar();
        _pulsePhase = 0;
        _pulse.Start();

        Task.Run(() =>
        {
            try
            {
                _session.Refresh();
            }
            catch (Exception ex)
            {
                SafeUi(() =>
                {
                    EndDetect();
                    _badge.SetState(Theme.State.Danger, Theme.State.DangerSubtle, Theme.State.Danger, "异常");
                    _status.Text = "检测失败";
                    _detail.Text = ex.Message;
                    _detail.ForeColor = Theme.Ink.Muted;
                    LayoutTopBar();
                });
            }
        });
    }

    private void EndDetect()
    {
        _pulse.Stop();
        _detecting = false;
        _refresh.Enabled = true;
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { /* 窗体正在关闭 */ }
    }

    private void OnSessionChanged()
    {
        if (InvokeRequired)
        {
            SafeUi(OnSessionChanged);
            return;
        }

        EndDetect();
        SnapshotSessionPaths();

        (Color dot, Color pillBg, Color pillFg, string tag) = _session.State switch
        {
            DeviceLinkState.ViaReady => (Theme.State.Ok, Theme.State.OkSubtle, Theme.State.Ok, "已连接"),
            DeviceLinkState.DeviceFound => (Theme.State.Warn, Theme.State.WarnSubtle, Theme.State.Warn, "仅发现"),
            DeviceLinkState.ReadOnly => (Theme.State.Warn, Theme.State.WarnSubtle, Theme.State.Warn, "2.4G 只读"),
            DeviceLinkState.Error => (Theme.State.Danger, Theme.State.DangerSubtle, Theme.State.Danger, "握手失败"),
            _ => (Theme.State.Idle, Theme.State.IdleSubtle, Theme.Ink.Muted, "未连接"),
        };
        _badge.SetState(dot, pillBg, pillFg, tag);
        _status.Text = _session.StatusText;

        SyncReadOnlyToggleUi();
        UpdateDetailLine();
        LayoutTopBar();

        foreach (var page in _tabs.CreatedPages)
        {
            switch (page)
            {
                case KeymapPanel kp: kp.OnSessionChanged(); break;
                case LightingPanel lp: lp.OnSessionChanged(); break;
                case MacroPanel mp: mp.OnSessionChanged(); break;
                case ShortcutPanel sp: sp.OnSessionChanged(); break;
                case GuidePanel gp: gp.OnSessionChanged(); break;
                case DebugHubPanel dp: dp.OnSessionChanged(); break;
            }
        }
    }

    private void SnapshotSessionPaths()
    {
        var d = _session.Device;
        if (d == null)
        {
            _lastHidPaths = Array.Empty<string>();
            return;
        }
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in d.ViaCandidates) set.Add(c.DevicePath);
        if (d.PrivateChannel != null) set.Add(d.PrivateChannel.DevicePath);
        foreach (var o in d.Others) set.Add(o.DevicePath);
        _lastHidPaths = set.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void OnReadOnlyChanged()
    {
        if (InvokeRequired) { SafeUi(OnReadOnlyChanged); return; }
        SyncReadOnlyToggleUi();
    }

    private void SyncReadOnlyToggleUi()
    {
        _syncingReadOnly = true;
        try
        {
            _readOnly.Enabled = !_session.ReadOnlyMode;
            _readOnly.Checked = _session.ReadOnlyMode || _session.UserReadOnly;
        }
        finally { _syncingReadOnly = false; }
    }

    private void OnDirtyChanged()
    {
        if (InvokeRequired) { SafeUi(OnDirtyChanged); return; }
        UpdateDetailLine();
        LayoutTopBar();
    }

    /// <summary>第二行：有未保存改动时优先提示（汇总键位/宏/灯效），否则显示设备详情。</summary>
    private void UpdateDetailLine()
    {
        if (_session.HasAnyUnsaved)
        {
            _detail.Text = $"有未保存改动：{_session.BuildUnsavedSummary()}";
            _detail.ForeColor = Theme.State.Warn;
        }
        else
        {
            _detail.Text = _session.DetailText;
            _detail.ForeColor = Theme.Ink.Muted;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.WindowsShutDown && _session.HasAnyUnsaved)
        {
            var r = MessageBox.Show(this,
                $"以下改动尚未写入或保存到键盘：\r\n{_session.BuildUnsavedSummary()}\r\n\r\n仍要退出吗？",
                "RS8Control", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) { e.Cancel = true; return; }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _session.Changed -= OnSessionChanged;
        _session.DirtyChanged -= OnDirtyChanged;
        _session.ReadOnlyChanged -= OnReadOnlyChanged;
        _pulse.Dispose();
        _hotplugDebounce.Dispose();
        _tip.Dispose();
        _appIcon?.Dispose();
        _session.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>顶部状态胶囊（圆角浅底 + 状态圆点 + 状态短词），零分配自绘。</summary>
    private sealed class StatusBadge : Control
    {
        private Color _dot = Theme.State.Idle;
        private Color _bg = Theme.State.IdleSubtle;
        private Color _fg = Theme.Ink.Body;
        private string _text = "检测中";

        public StatusBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Font = Theme.Fonts.Caption;
            TabStop = false;
            UpdateBoundsSize();
        }

        public void SetState(Color dot, Color bg, Color fg, string text)
        {
            _dot = dot;
            _bg = bg;
            _fg = fg;
            _text = text;
            UpdateBoundsSize();
            Invalidate();
        }

        public void SetDotColor(Color dot)
        {
            if (_dot == dot) return;
            _dot = dot;
            Invalidate();
        }

        private void UpdateBoundsSize()
        {
            Size ts = TextRenderer.MeasureText(_text, Font);
            int padX = Theme.Scale(this, 10);
            int dotW = Theme.Scale(this, 8);
            int gap = Theme.Scale(this, 6);
            int h = Theme.Scale(this, 24);
            Size = new Size(padX * 2 + dotW + gap + ts.Width, h);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateBoundsSize();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.Clear(Parent?.BackColor ?? Theme.Bg.Surface);

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            g.FillPath(Theme.Brush(_bg), Theme.RoundedPath(r, Height / 2f));

            int padX = Theme.Scale(this, 10);
            float dotSize = Theme.ScaleF(this, 8f);
            float dy = (Height - dotSize) / 2f;
            g.FillEllipse(Theme.Brush(_dot), padX, dy, dotSize, dotSize);

            int textX = padX + (int)Math.Round(dotSize) + Theme.Scale(this, 6);
            TextRenderer.DrawText(g, _text, Font,
                new Rectangle(textX, 0, Math.Max(1, Width - textX - padX / 2), Height),
                _fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
