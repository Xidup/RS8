using RS8Control.Diag;
using RS8Control.Hid;
using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

// ============================================================
//  诊断面板
// ============================================================
public sealed class DiagPanel : UserControl
{
    private readonly DeviceSession _session;
    private readonly RichTextBox _box = new();
    private readonly FlatButton _refresh = new("刷新诊断", ButtonVariant.Primary);
    private readonly FlatButton _scan = new("深度扫描");
    private readonly FlatButton _save = new("保存报告");
    private readonly FlatButton _copy = new("复制");

    /// <summary>诊断/扫描进行中。既用于防止重复启动，也用于驱动 SetBusy。</summary>
    private bool _running;

    public DiagPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Color.White;

        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.BarHeightFor(this),
            BackColor = Theme.Bg.Surface,
            Padding = new Padding(0, 0, 0, 1),
        };
        bar.Paint += (_, e) => e.Graphics.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, bar.Height - 1, bar.Width, bar.Height - 1);
        bar.Resize += (_, _) => bar.Invalidate();

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, Theme.Scale(this, 8), 0) };
        _refresh.Click += (_, _) => Run();
        _scan.Click += (_, _) => RunScan();
        _save.Click += (_, _) => Save();
        _copy.Click += (_, _) => { if (_box.Text.Length > 0) { Clipboard.SetText(_box.Text); } };
        right.Controls.Add(_copy);
        right.Controls.Add(_save);
        right.Controls.Add(_scan);
        right.Controls.Add(_refresh);
        bar.Controls.Add(right);
        var note = new Label
        {
            Text = "「深度扫描」会逐个试探报文结构，约需 1 分钟，用于定位协议问题。",
            AutoSize = true,
            ForeColor = Theme.Ink.Muted,
        };
        bar.Controls.Add(note);
        note.Left = Theme.Scale(this, 12);
        note.Top = UiLayout.CenterInBar(note, bar.Height);
        UiLayout.CenterFlowBar(right, bar.Height);

        _box.Dock = DockStyle.Fill;
        _box.Font = Theme.Fonts.Mono;
        _box.ReadOnly = true;
        _box.BorderStyle = BorderStyle.None;
        _box.WordWrap = false;
        _box.ScrollBars = RichTextBoxScrollBars.Both;
        _box.BackColor = Theme.Bg.Sunken;
        _box.Text = "点击右上角「刷新诊断」生成完整设备与 VIA 协议报告。\r\n如正在排查未识别的报文结构，可点击「深度扫描」（约需 1 分钟，仅发送只读命令）。";

        Controls.Add(_box);
        Controls.Add(bar);
    }

    /// <summary>
    /// 刷新诊断。Probe.Run 是纯函数（只读 HID + 拼字符串），所以整段丢后台线程跑。
    /// 老写法是「设等待光标 → DoEvents → 同步跑」：DoEvents 只为把光标刷出去，
    /// 代价是这几百毫秒里按钮可点、能重入再跑一次诊断。
    /// </summary>
    public void Run()
    {
        if (_running) return;
        _running = true;
        SetBusy(true, isScan: false);
        Cursor = Cursors.WaitCursor;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            string report = await Task.Run(() => Probe.Run()).ConfigureAwait(true);
            if (IsDisposed) return;
            _box.Text = report;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _box.Text = "诊断出错：\r\n" + ex;
        }
        finally
        {
            _running = false;
            if (!IsDisposed)
            {
                SetBusy(false, isScan: false);
                Cursor = Cursors.Default;
            }
        }
    }

    private void RunScan()
    {
        if (_running) return;
        _running = true;
        SetBusy(true, isScan: true);
        Cursor = Cursors.WaitCursor;
        _box.Text = "深度扫描进行中，约需 1 分钟，请勿操作键盘…\r\n";
        _ = RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        try
        {
            string text = await Task.Run(() =>
            {
                using var sw = new StringWriter();
                Interactive.Scan(sw);
                return sw.ToString();
            }).ConfigureAwait(true);

            if (IsDisposed) return;
            _box.Text = "【深度扫描结果】\r\n" + text;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _box.Text = "扫描出错：\r\n" + ex;
        }
        finally
        {
            _running = false;
            if (!IsDisposed)
            {
                SetBusy(false, isScan: true);
                Cursor = Cursors.Default;
            }
        }
    }

    /// <summary>
    /// 诊断或扫描期间停用按钮，防止与后台 HID 操作冲突；仅在对应操作下修改按钮文案。
    /// </summary>
    private void SetBusy(bool busy, bool isScan)
    {
        _refresh.Enabled = !busy;
        _scan.Enabled = !busy;
        _save.Enabled = !busy;
        _scan.Text = busy && isScan ? "扫描中…" : "深度扫描";
        _refresh.Text = busy && !isScan ? "诊断中…" : "刷新诊断";
    }

    private void Save()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "文本文件|*.txt",
            FileName = $"RS8_diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, _box.Text, new System.Text.UTF8Encoding(false));
    }
}

// ============================================================
//  链路测试面板：实时显示按键，验证键盘与接收器是否真的在通信
// ============================================================
public sealed class LinkPanel : UserControl
{
    private readonly DeviceSession _session;
    private readonly RichTextBox _log = new();
    private readonly FlatButton _start = new("开始监听", ButtonVariant.Primary);
    private readonly FlatButton _clear = new("清空");
    private readonly Label _hint = new();
    private KeyboardInputMonitor? _monitor;

    public LinkPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Theme.Bg.Surface;

        var bar = new Panel { Dock = DockStyle.Top, Height = UiLayout.BarHeight, BackColor = Theme.Bg.Surface };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 8, 0) };
        _start.Click += (_, _) => Toggle();
        _clear.Click += (_, _) => _log.Clear();
        right.Controls.Add(_clear);
        right.Controls.Add(_start);
        bar.Controls.Add(right);

        _hint.Text = "点击「开始监听」，然后在键盘上按键 —— 能显示按键说明键盘与接收器链路正常。\r\n" +
                     "若无任何反应，请先按一下键盘唤醒它（无线模式下键盘会自动休眠）。";
        _hint.AutoSize = true;
        _hint.ForeColor = Theme.Ink.Body;
        bar.Controls.Add(_hint);
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        // 两行说明与按钮组同一垂直中心
        _hint.Left = 12;
        _hint.Top = UiLayout.CenterInBar(_hint, bar.Height);
        UiLayout.CenterFlowBar(right, bar.Height);

        _log.Dock = DockStyle.Fill;
        _log.Font = Theme.Fonts.Mono;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.WordWrap = false;
        _log.BackColor = Theme.Bg.Sunken;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;

        Controls.Add(_log);
        Controls.Add(bar);
    }

    public void OnSessionChanged()
    {
        StopMonitor("--- 设备状态变更，监听已停止 ---");
    }

    private void StopMonitor(string logLine)
    {
        if (_monitor == null) return;
        _monitor.Changed -= OnPressed;
        _monitor.Stop();
        _monitor.Dispose();
        _monitor = null;
        _start.Text = "开始监听";
        Append(logLine);
    }

    private void Toggle()
    {
        if (_monitor != null)
        {
            StopMonitor("--- 已停止 ---");
            return;
        }

        var m = new KeyboardInputMonitor(_session.Device?.VendorId ?? 0, _session.Device?.ProductId ?? 0);
        m.Changed += OnPressed;
        if (!m.Start())
        {
            Append(m.LastError);
            m.Dispose();
            return;
        }

        var iface = m.Interface!;
        Append($"监听接口 MI={iface.InterfaceNumber}（{iface.InputReportByteLength} 字节）");
        Append(">>> 现在请在键盘上按键 <<<");
        _monitor = m;
        _start.Text = "停止监听";
    }

    /// <summary>后台线程回调：按键集合变化时才来一次，按住不放不会重复刷屏。</summary>
    private void OnPressed(IReadOnlyCollection<byte> pressed)
    {
        if (pressed.Count == 0) return;   // 全部松开，与原实现一样不打印
        SafeAppend(Format(ToReport(pressed)));
    }

    /// <summary>
    /// 把 usage 集合还原成 boot 键盘报告的字节布局，
    /// 这样日志里的十六进制和原来的原始报告长得一样。
    /// </summary>
    private static byte[] ToReport(IReadOnlyCollection<byte> pressed)
    {
        var d = new byte[8];
        int slot = 2;
        foreach (byte u in pressed)
        {
            if (u is >= 0xE0 and <= 0xE7) { d[0] |= (byte)(1 << (u - 0xE0)); continue; }
            if (slot < d.Length) d[slot++] = u;
        }
        return d;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _monitor != null)
        {
            _monitor.Changed -= OnPressed;
            _monitor.Stop();
            _monitor.Dispose();
            _monitor = null;
        }
        base.Dispose(disposing);
    }

    private static string Format(byte[] d)
    {
        string hex = string.Join(" ", d.Select(b => b.ToString("X2")));
        var parts = new List<string>();
        string[] mods = { "左Ctrl", "左Shift", "左Alt", "左Win", "右Ctrl", "右Shift", "右Alt", "右Win" };
        if (d.Length > 0)
            for (int i = 0; i < 8; i++)
                if ((d[0] & (1 << i)) != 0) parts.Add(mods[i]);
        for (int i = 2; i < d.Length; i++)
            if (d[i] != 0) parts.Add(Model.Keycodes.Describe(d[i]));
        return $"[{DateTime.Now:HH:mm:ss.fff}]  {hex}   {(parts.Count > 0 ? "→ " + string.Join(" + ", parts) : "")}";
    }

    private void Append(string s) => _log.AppendText(s + "\r\n");
    private void SafeAppend(string s)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _log.AppendText(s + "\r\n");
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            });
        }
        catch { /* 窗体已关闭 */ }
    }
}

// ============================================================
//  原始 HID 调试台
// ============================================================
public sealed class RawPanel : UserControl
{
    private readonly DeviceSession _session;
    private readonly FlatComboBox _ifaceBox = new();
    private readonly FlatTextBox _input = new();
    private readonly FlatNumeric _reportId = new();
    private readonly RichTextBox _log = new();
    private readonly FlatButton _send = new("发送", ButtonVariant.Primary);
    private readonly FlatButton _read = new("读取");
    private List<HidInterfaceInfo> _ifaces = new();
    private HidConnection? _conn;
    private bool _suppressOpen;
    private bool _ioBusy;

    public RawPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Theme.Bg.Surface;

        // 两行栏：第 1 行选接口，第 2 行填报文；每行内所有控件同中心
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.TallBarHeightFor(this),
            BackColor = Theme.Bg.Surface,
            Padding = new Padding(0, 0, 0, 1),
        };
        bar.Paint += (_, e) => e.Graphics.DrawLine(Theme.Pen(Theme.Line.Subtle), 0, bar.Height - 1, bar.Width, bar.Height - 1);
        bar.Resize += (_, _) => bar.Invalidate();

        var ifaceCaption = new Label { Text = "接口", AutoSize = true };
        bar.Controls.Add(ifaceCaption);
        _ifaceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _ifaceBox.Width = Theme.Scale(this, 620);
        _ifaceBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressOpen) OpenSelected();
        };
        bar.Controls.Add(_ifaceBox);

        var sendCaption = new Label { Text = "发送(十六进制)", AutoSize = true };
        bar.Controls.Add(sendCaption);
        _input.Width = Theme.Scale(this, 470);
        _input.Font = Theme.Fonts.Mono;
        _input.Text = "01";
        bar.Controls.Add(_input);

        var reportCaption = new Label { Text = "报告ID", AutoSize = true };
        bar.Controls.Add(reportCaption);
        _reportId.Width = Theme.Scale(this, 68);
        _reportId.Minimum = 0;
        _reportId.Maximum = 255;
        _reportId.Value = 0;
        bar.Controls.Add(_reportId);

        _send.Click += (_, _) => _ = SendHexAsync();
        bar.Controls.Add(_send);

        _read.Click += (_, _) => _ = ReadOnceAsync();
        bar.Controls.Add(_read);

        int row1H = UiLayout.RowHeight(ifaceCaption, _ifaceBox);
        int row2H = UiLayout.RowHeight(sendCaption, _input, reportCaption, _reportId, _send, _read);
        var rows = UiLayout.TwoRowBar(bar.Height, row1H, row2H);

        ifaceCaption.Left = Theme.Scale(this, 12);
        ifaceCaption.Top = UiLayout.CenterInRow(ifaceCaption, rows.MainTop, rows.MainHeight);
        _ifaceBox.Left = ifaceCaption.Left + ifaceCaption.PreferredSize.Width + Theme.Scale(this, 10);
        _ifaceBox.Top = UiLayout.CenterInRow(_ifaceBox, rows.MainTop, rows.MainHeight);

        sendCaption.Left = Theme.Scale(this, 12);
        sendCaption.Top = UiLayout.CenterInRow(sendCaption, rows.HintTop, rows.HintHeight);
        _input.Left = sendCaption.Left + sendCaption.PreferredSize.Width + Theme.Scale(this, 8);
        _input.Top = UiLayout.CenterInRow(_input, rows.HintTop, rows.HintHeight);
        reportCaption.Left = _input.Right + Theme.Scale(this, 10);
        reportCaption.Top = UiLayout.CenterInRow(reportCaption, rows.HintTop, rows.HintHeight);
        _reportId.Left = reportCaption.Left + reportCaption.PreferredSize.Width + Theme.Scale(this, 8);
        _reportId.Top = UiLayout.CenterInRow(_reportId, rows.HintTop, rows.HintHeight);
        _send.Left = _reportId.Right + Theme.Scale(this, 10);
        _send.Top = UiLayout.CenterInRow(_send, rows.HintTop, rows.HintHeight);
        _read.Left = _send.Right + Theme.Scale(this, 10);
        _read.Top = UiLayout.CenterInRow(_read, rows.HintTop, rows.HintHeight);

        _log.Dock = DockStyle.Fill;
        _log.Font = Theme.Fonts.Mono;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Theme.Bg.Sunken;

        Controls.Add(_log);
        Controls.Add(bar);

        _session.ReadOnlyChanged += OnReadOnlyChanged;
        SyncControls();
    }

    public void OnSessionChanged()
    {
        _conn?.Dispose();
        _conn = null;

        _ifaces = _session.Device == null
            ? new List<HidInterfaceInfo>()
            : Win32Hid.Enumerate()
                .Where(a => a.VendorId == _session.Device.VendorId && a.ProductId == _session.Device.ProductId)
                .ToList();

        _suppressOpen = true;
        try
        {
            _ifaceBox.Items.Clear();
            foreach (var a in _ifaces)
                _ifaceBox.Items.Add($"MI={a.InterfaceNumber,-3} UP=0x{a.UsagePage:X4} U=0x{a.Usage:X4}  IN={a.InputReportByteLength} OUT={a.OutputReportByteLength} FEAT={a.FeatureReportByteLength}");

            if (_ifaceBox.Items.Count > 0)
            {
                int viaIdx = _ifaces.FindIndex(a => a.UsagePage == 0xFF60);
                _ifaceBox.SelectedIndex = viaIdx >= 0 ? viaIdx : 0;
            }
        }
        finally
        {
            _suppressOpen = false;
        }
        SyncControls();
    }

    private void OnReadOnlyChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(OnReadOnlyChanged); } catch { } return; }
        SyncControls();
    }

    private void SyncControls()
    {
        _send.Enabled = !_ioBusy && !_session.EffectiveReadOnly;
        _read.Enabled = !_ioBusy;
        _ifaceBox.Enabled = !_ioBusy;
    }

    private bool EnsureOpen()
    {
        if (_conn != null) return true;
        int i = _ifaceBox.SelectedIndex;
        if (i < 0 || i >= _ifaces.Count)
        {
            _log.AppendText("未选择可用接口\r\n");
            return false;
        }
        OpenSelected();
        return _conn != null;
    }

    private void OpenSelected()
    {
        _conn?.Dispose();
        _conn = null;
        int i = _ifaceBox.SelectedIndex;
        if (i < 0 || i >= _ifaces.Count) return;
        var conn = new HidConnection(_ifaces[i]);
        bool ok = conn.Open();
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] 打开 MI={_ifaces[i].InterfaceNumber}: {(ok ? "成功" : $"失败 (Win32 {conn.LastWin32Error})")}  可写={conn.OpenedForWrite}\r\n");
        if (ok)
        {
            _conn = conn;
        }
        else
        {
            conn.Dispose();
        }
    }

    private async Task SendHexAsync()
    {
        if (_ioBusy) return;
        if (_session.EffectiveReadOnly)
        {
            _log.AppendText("⚠ 只读模式已开启，拒绝发送原始报文。\r\n");
            return;
        }
        byte[] bytes;
        try { bytes = ParseHex(_input.Text); }
        catch { _log.AppendText("十六进制格式错误\r\n"); return; }

        if (!EnsureOpen()) return;
        var conn = _conn!;
        byte reportId = (byte)_reportId.Value;

        _ioBusy = true;
        SyncControls();
        try
        {
            var (n, elapsedMs, err, replies) = await Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int written = conn.Write(bytes, reportId);
                sw.Stop();
                int win32 = conn.LastWin32Error;
                var list = new List<byte[]>();
                for (int k = 0; k < 3; k++)
                {
                    var resp = conn.Read(300);
                    if (resp == null || resp.Length == 0) break;
                    list.Add(resp);
                }
                return (written, sw.ElapsedMilliseconds, win32, list);
            }).ConfigureAwait(true);

            if (IsDisposed) return;
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] → [id={reportId:X2}] {Convert.ToHexString(bytes)}   结果={(n > 0 ? $"{n} 字节" : "失败")}  耗时={elapsedMs}ms  Win32={err}\r\n");
            foreach (var resp in replies)
                _log.AppendText($"[{DateTime.Now:HH:mm:ss}] ← {Convert.ToHexString(resp)}\r\n");
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _log.AppendText($"[{DateTime.Now:HH:mm:ss}] 发送异常：{ex.Message}\r\n");
        }
        finally
        {
            _ioBusy = false;
            if (!IsDisposed) SyncControls();
        }
    }

    private async Task ReadOnceAsync()
    {
        if (_ioBusy) return;
        if (!EnsureOpen()) return;
        var conn = _conn!;

        _ioBusy = true;
        SyncControls();
        try
        {
            var resp = await Task.Run(() => conn.Read(1000)).ConfigureAwait(true);
            if (IsDisposed) return;
            _log.AppendText(resp == null
                ? $"[{DateTime.Now:HH:mm:ss}] ← 无数据（超时）\r\n"
                : $"[{DateTime.Now:HH:mm:ss}] ← {Convert.ToHexString(resp)}\r\n");
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _log.AppendText($"[{DateTime.Now:HH:mm:ss}] 读取异常：{ex.Message}\r\n");
        }
        finally
        {
            _ioBusy = false;
            if (!IsDisposed) SyncControls();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.ReadOnlyChanged -= OnReadOnlyChanged;
            _conn?.Dispose();
            _conn = null;
        }
        base.Dispose(disposing);
    }

    private static byte[] ParseHex(string s)
    {
        s = new string(s.Where(c => Uri.IsHexDigit(c)).ToArray());
        if (s.Length % 2 != 0) throw new FormatException();
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }
}

// ============================================================
//  开了双缓冲的滚动容器
// ============================================================

/// <summary>
/// 开了双缓冲的 Panel。<c>Control.DoubleBuffered</c> 是 protected，只能靠子类打开；
/// 快捷键页的滚动容器里子控件多、拖动窗口时反复重排，不开双缓冲会明显闪。
/// </summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel() => DoubleBuffered = true;
}

/// <summary>开了双缓冲的 FlowLayoutPanel，供灯效页的卡片容器使用。</summary>
internal sealed class BufferedFlowPanel : FlowLayoutPanel
{
    public BufferedFlowPanel() => DoubleBuffered = true;
}
