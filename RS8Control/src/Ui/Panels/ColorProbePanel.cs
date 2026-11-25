using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 灯效颜色诊断：把指定灯区依次设为各个色相，让用户盯着键盘观察哪些颜色显示异常。
/// 会临时改动灯光，但**结束或停止时自动还原**（亮度/效果/速度/颜色四个值）。
///
/// 背景：用户反馈「修改侧边灯时某些颜色无法正常显示」。代码里的颜色写入格式
/// （低字节色相、高字节饱和度）已与实机读回核对无误，所以问题大概率在固件或灯珠。
/// 这个工具让用户边扫边看，拿到观察结论后再对症处理。
/// </summary>
public sealed class ColorProbePanel : UserControl
{
    private readonly DeviceSession _session;

    private readonly FlatComboBox _channel = new();
    private readonly FlatButton _start = new("开始色相扫描", ButtonVariant.Primary);
    private readonly FlatButton _stop = new("停止");
    private readonly Label _status = new();
    // 用项目里已有的自绘滑块，替掉原生 TrackBar —— 原生那个是 Win95 观感，和全局不搭
    private readonly LightSlider _hue = new();
    private readonly Label _hueVal = new();
    private readonly LightSlider _sat = new();
    private readonly Label _satVal = new();
    private readonly SwatchButton _previewSwatch = new();
    private readonly FlatButton _apply = new("应用此颜色");
    private readonly RichTextBox _log = new();

    private CancellationTokenSource? _cts;
    private bool _sweeping;
    private byte _sweepChannel;
    // 扫描前备份的四个值（设备域），结束后还原
    private int _bakBrightness, _bakEffect, _bakSpeed, _bakColor;

    private static readonly (byte Ch, string Name)[] Zones =
    {
        (2, "正面 Logo 灯（通道2）"),
        (3, "键帽背光（通道3）"),
        (4, "侧边环绕灯（通道4）"),
    };

    public ColorProbePanel(DeviceSession session)
    {
        _session = session;
        BackColor = Color.White;

        var bar = new Panel { Dock = DockStyle.Top, Height = UiLayout.BarHeightFor(this), BackColor = Color.White };
        var caption = new Label
        {
            Text = "把指定灯区依次设为各色相，盯着键盘看哪些颜色不对。\r\n⚠ 会临时改动灯光，结束/停止时自动还原。",
            AutoSize = true,
            ForeColor = Theme.Ink.Muted,
        };
        bar.Controls.Add(caption);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, Theme.Scale(this, 10), 0) };
        _stop.Enabled = false;
        _start.Click += (_, _) => _ = SweepAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        right.Controls.Add(_stop);
        right.Controls.Add(_start);
        bar.Controls.Add(right);
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        // 两行说明与按钮组同一垂直中心（说明本来就是两行，不再和按钮各偏各的）
        caption.Left = Theme.Scale(this, 12);
        caption.Top = UiLayout.CenterInBar(caption, bar.Height);
        UiLayout.CenterFlowBar(right, bar.Height);

        // ---------- 设置区 ----------
        var setup = new Panel { Dock = DockStyle.Top, Height = Theme.Scale(this, 146), BackColor = Color.White };
        setup.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        var zoneCaption = new Label { Text = "灯区", AutoSize = true, ForeColor = Theme.Ink.Body };
        setup.Controls.Add(zoneCaption);
        _channel.DropDownStyle = ComboBoxStyle.DropDownList;
        _channel.Width = Theme.Scale(this, 220);
        foreach (var z in Zones) _channel.Items.Add(z.Name);
        _channel.SelectedIndex = 2;   // 默认侧边环绕灯 —— 用户反馈的问题灯区
        setup.Controls.Add(_channel);

        _status.AutoSize = true;
        _status.ForeColor = Theme.Ink.Body;
        _status.Text = "未开始";
        setup.Controls.Add(_status);

        // 灯区行：标签 / 下拉框 / 状态文字同一垂直中心
        int rowTop = Theme.Scale(this, 10);
        int rowH = UiLayout.RowHeight(zoneCaption, _channel, _status);
        zoneCaption.Left = Theme.Scale(this, 14);
        zoneCaption.Top = UiLayout.CenterInRow(zoneCaption, rowTop, rowH);
        _channel.Left = zoneCaption.Left + zoneCaption.PreferredSize.Width + Theme.Scale(this, 10);
        _channel.Top = UiLayout.CenterInRow(_channel, rowTop, rowH);
        _status.Left = _channel.Right + Theme.Scale(this, 20);
        _status.Top = UiLayout.CenterInRow(_status, rowTop, rowH);

        // 两条滑块：读数标签与滑块本身同中心
        int sliderGap = Theme.Scale(this, 42);
        int sliderTop = rowTop + rowH + Theme.Scale(this, 8);
        int sliderW = Theme.Scale(this, 300);
        int sliderH = Theme.Scale(this, 42);

        _hue.Minimum = 0; _hue.Maximum = 255; _hue.Value = 128;
        _hue.Size = new Size(sliderW, sliderH);
        _hue.Left = Theme.Scale(this, 14); _hue.Top = sliderTop;
        _hueVal.AutoSize = true; _hueVal.Text = "色相 128";
        _hue.ValueChanged += v => { _hueVal.Text = $"色相 {v}"; UpdatePreviewSwatch(); };
        setup.Controls.Add(_hue); setup.Controls.Add(_hueVal);

        _sat.Minimum = 0; _sat.Maximum = 255; _sat.Value = 255;
        _sat.Size = new Size(sliderW, sliderH);
        _sat.Left = Theme.Scale(this, 14); _sat.Top = sliderTop + sliderGap;
        _satVal.AutoSize = true; _satVal.Text = "饱和 255";
        _sat.ValueChanged += v => { _satVal.Text = $"饱和 {v}"; UpdatePreviewSwatch(); };
        setup.Controls.Add(_sat); setup.Controls.Add(_satVal);

        _hueVal.Left = _hue.Right + Theme.Scale(this, 6);
        _hueVal.Top = UiLayout.CenterInRow(_hueVal, _hue.Top, _hue.Height);
        _satVal.Left = _sat.Right + Theme.Scale(this, 6);
        _satVal.Top = UiLayout.CenterInRow(_satVal, _sat.Top, _sat.Height);

        _previewSwatch.Size = new Size(Theme.Scale(this, 44), Theme.Scale(this, 44));
        _previewSwatch.Cursor = Cursors.Default;
        _previewSwatch.Left = _hue.Right + Theme.Scale(this, 82);
        _previewSwatch.Top = UiLayout.CenterInRow(_previewSwatch, _hue.Top, _sat.Bottom - _hue.Top);
        setup.Controls.Add(_previewSwatch);

        _apply.AutoSize = true;
        _apply.Click += (_, _) => _ = ApplyManualAsync();
        setup.Controls.Add(_apply);
        _apply.Left = _previewSwatch.Right + Theme.Scale(this, 14);
        _apply.Top = UiLayout.CenterInRow(_apply, _hue.Top, _sat.Bottom - _hue.Top);

        // ---------- 日志 ----------
        _log.Dock = DockStyle.Fill;
        _log.Font = Theme.Fonts.Mono;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.WordWrap = false;
        _log.ScrollBars = RichTextBoxScrollBars.Both;
        _log.BackColor = Theme.Bg.Sunken;

        Controls.Add(_log);
        Controls.Add(setup);
        Controls.Add(bar);

        UpdatePreviewSwatch();
        _session.ReadOnlyChanged += OnReadOnlyChanged;
        SyncControls();
    }

    public void OnSessionChanged()
    {
        SyncControls();
        if (!_session.Ready && _sweeping) _cts?.Cancel();
    }

    private void OnReadOnlyChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(OnReadOnlyChanged); } catch { } return; }
        SyncControls();
    }

    private void SyncControls()
    {
        bool ready = _session.Ready;
        bool canWrite = ready && !_session.EffectiveReadOnly;
        _start.Enabled = !_sweeping && canWrite;
        _apply.Enabled = !_sweeping && canWrite;
        _stop.Enabled = _sweeping;
        _channel.Enabled = !_sweeping && ready;
        _hue.Enabled = ready;
        _sat.Enabled = ready;
    }

    private void UpdatePreviewSwatch() =>
        _previewSwatch.SwatchColor = HsToRgb(_hue.Value, _sat.Value);

    private byte SelectedChannel => Zones[Math.Max(0, _channel.SelectedIndex)].Ch;

    private void Log(string s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(s))); } catch { } return; }
        _log.AppendText(s + "\r\n");
    }

    private void SetStatus(string s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => SetStatus(s))); } catch { } return; }
        _status.Text = s;
    }

    private void SetSweeping(bool on)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => SetSweeping(on))); } catch { } return; }
        _sweeping = on;
        SyncControls();
    }

    /// <summary>
    /// 色相扫描。备份 → 把效果临时设为静态（否则循环类效果会覆盖颜色）→ 逐步扫色相 → 还原。
    /// 还原放在 finally 里，保证任何情况下都执行。
    /// </summary>
    private async Task SweepAsync()
    {
        if (_sweeping || !_session.Ready) return;
        if (_session.EffectiveReadOnly)
        {
            Log(_session.UserReadOnly
                ? "⚠ 顶栏「只读」开关已开启，写入会被拒绝。请取消只读后重试。"
                : "⚠ 当前处于只读模式（2.4G 接收器），无法写入。");
            return;
        }

        byte ch = SelectedChannel;
        _sweepChannel = ch;
        _bakBrightness = _bakEffect = _bakSpeed = _bakColor = -1;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetSweeping(true);
        try
        {
            // 1) 备份（放到后台线程，避免阻塞 UI）
            var (bakB, bakFx, bakSp, bakCol) = await Task.Run(() => (
                _session.ReadLight(ch, 1),
                _session.ReadLight(ch, 2),
                _session.ReadLight(ch, 3),
                _session.ReadLight(ch, 4))).ConfigureAwait(true);

            _bakBrightness = bakB;
            _bakEffect = bakFx;
            _bakSpeed = bakSp;
            _bakColor = bakCol;
            Log($"灯区通道 {ch} 原始值：亮度={_bakBrightness} 效果={_bakEffect} 速度={_bakSpeed} 颜色=0x{_bakColor & 0xFFFF:X4}");
            token.ThrowIfCancellationRequested();

            // 2) 临时把效果设为能看清颜色的静态效果
            int staticEffect = ch == 3 ? 1 : 5;   // 背光 46 项表: 1=单色；logo/侧灯 7 项表: 5=常亮
            _session.QueueLightWrite(ch, 2, (ushort)staticEffect);
            await _session.FlushLightsAsync().ConfigureAwait(true);
            Log($"效果已临时设为 {staticEffect}（{StaticName(ch, staticEffect)}）");
            await Task.Delay(600, token).ConfigureAwait(true);

            // 3) 扫色相：0 → 255（最后一步取 255 避免 256 字节溢出回绕到 0），共 17 步
            int[] hues = { 0, 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240, 255 };
            for (int i = 0; i < hues.Length; i++)
            {
                if (token.IsCancellationRequested) { Log("—— 已手动停止 ——"); break; }
                int hue = hues[i];
                int value = (255 << 8) | hue;      // 饱和 255，低字节色相
                _session.QueueLightWrite(ch, 4, (ushort)value);
                await _session.FlushLightsAsync().ConfigureAwait(true);
                int back = await Task.Run(() => _session.ReadLight(ch, 4)).ConfigureAwait(true);
                SetStatus($"色相 {hue}（{i + 1}/{hues.Length}）");
                Log($"色相 {hue,3}  写入 0x{value & 0xFFFF:X4}  回读 0x{back & 0xFFFF:X4}{(back == value ? "" : "  ⚠ 不一致")}");
                try { await Task.Delay(1200, token).ConfigureAwait(true); }
                catch (OperationCanceledException) { Log("—— 已手动停止 ——"); break; }
            }
        }
        catch (OperationCanceledException)
        {
            Log("—— 已停止 ——");
        }
        catch (Exception ex)
        {
            Log("扫描出错：" + ex.Message);
        }
        finally
        {
            await RestoreAsync().ConfigureAwait(true);
            if (!IsDisposed)
            {
                SetSweeping(false);
                SetStatus("已还原");
            }
        }
    }

    private async Task ApplyManualAsync()
    {
        if (!_session.Ready || _sweeping) return;
        if (_session.EffectiveReadOnly)
        {
            Log(_session.UserReadOnly
                ? "⚠ 顶栏「只读」开关已开启，写入会被拒绝。"
                : "⚠ 当前处于只读模式（2.4G 接收器），无法写入。");
            return;
        }
        byte ch = SelectedChannel;
        int hue = _hue.Value;
        int sat = _sat.Value;
        int value = (sat << 8) | hue;
        _session.QueueLightWrite(ch, 4, (ushort)value);
        await _session.FlushLightsAsync().ConfigureAwait(true);
        int back = await Task.Run(() => _session.ReadLight(ch, 4)).ConfigureAwait(true);
        if (IsDisposed) return;
        Log($"手动：通道 {ch} 色相 {hue} 饱和 {sat}  写入 0x{value & 0xFFFF:X4}  回读 0x{back & 0xFFFF:X4}");
        SetStatus($"已应用 色相 {hue}");
    }

    /// <summary>把扫描前的四个值写回去。亮度(id=1)在通道3 有 floor(v×255/160) 的读写换算，这里反解回下发域。</summary>
    private async Task RestoreAsync()
    {
        byte ch = _sweepChannel;
        int bakEffect = _bakEffect, bakSpeed = _bakSpeed, bakColor = _bakColor, bakBrightness = _bakBrightness;
        try
        {
            await Task.Run(() =>
            {
                if (bakEffect >= 0) _session.WriteLight(ch, 2, (ushort)bakEffect);
                if (bakSpeed >= 0) _session.WriteLight(ch, 3, (ushort)bakSpeed);
                if (bakColor >= 0) _session.WriteLight(ch, 4, (ushort)bakColor);
                if (bakBrightness >= 0)
                {
                    int via = bakBrightness;
                    if (ch == 3)
                        via = (int)Math.Ceiling(bakBrightness * 160.0 / 255.0);   // 设备域 → 下发域
                    _session.WriteLight(ch, 1, (ushort)via);
                }
            }).ConfigureAwait(false);
            await _session.FlushLightsAsync().ConfigureAwait(false);
            Log($"已还原：亮度={bakBrightness} 效果={bakEffect} 速度={bakSpeed} 颜色=0x{bakColor & 0xFFFF:X4}");
        }
        catch (Exception ex)
        {
            Log("⚠ 还原失败，请手动在灯效页调回原值：" + ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.ReadOnlyChanged -= OnReadOnlyChanged;
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
        }
        base.Dispose(disposing);
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

    private static string StaticName(byte ch, int effect) => ch switch
    {
        3 => effect == 1 ? "单色" : effect.ToString(),
        _ => effect == 5 ? "常亮" : effect.ToString(),
    };
}
