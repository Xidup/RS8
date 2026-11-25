using System.ComponentModel;
using System.Drawing.Drawing2D;
using RS8Control.Hid;
using RS8Control.Model;
using RS8Control.Protocol;
using RS8Control.Ui;

namespace RS8Control.Ui.Panels;

/// <summary>键位映射面板：按官方 VIA 定义渲染键盘，点击改键。</summary>
public sealed class KeymapPanel : UserControl
{
    /// <summary>
    /// 按键高亮是**常驻功能**（不需要用户开关）：连接上键盘后自动开始监听，断开时自动停止。
    /// 这条限制是真的，必须让用户看得到。
    /// </summary>
    private const string HighlightHint =
        "按键高亮已启用：只能高亮当前映射为基本键码的键。被改成媒体键、层功能键、自定义键" +
        "（如 2.4G / 蓝牙切换）的按键，固件不会上报对应的键盘 usage，无法高亮 —— Fn 键即属此类。";

    private const string DefaultHint = "点击任意按键即可改键；改过的按键会标红。";

    private readonly DeviceSession _session;
    private readonly KeyboardCanvas _canvas = new();
    private readonly FlatComboBox _layerBox = new();
    private readonly Label _hint = new();
    private readonly FlatButton _read = new("从键盘读取");
    private readonly FlatButton _write = new("写入键盘", ButtonVariant.Primary);
    private readonly FlatButton _reset = new("恢复出厂键位");

    private readonly Dictionary<int, ushort[]> _edited = new();   // layer → 待写入
    private readonly Dictionary<int, HashSet<int>> _dirty = new();

    // 顶栏排布用的引用（见 LayoutBar）
    private Panel? _bar;
    private Label? _layerCaption;
    private FlowLayoutPanel? _barRight;
    private bool _layingOutBar;
    private bool _suppressLayerChange;

    private KeyboardInputMonitor? _monitor;

    /// <summary>
    /// 读/写进行中。用来替代老代码里的 Application.DoEvents()：
    /// 等待光标需要一次消息泵才显示，但泵消息会让按钮可点 → 重入 ReadLayer。
    /// 改成异步（线程让出，光标自然刷出）+ 这个守卫，重入从源头消失。
    /// </summary>
    private bool _busy;

    /// <summary>
    /// usage → 矩阵下标（当前显示层）。UI 线程整表替换，后台监听线程只读，
    /// 所以不能原地改内容，每次重建都换一个新字典。
    /// </summary>
    private Dictionary<byte, List<int>> _usageToIndex = new();

    public KeymapPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Theme.Bg.Surface;

        // 两行：主行 = 层标签 + 下拉框 + 右侧按钮组；提示行 = 可能换行的限制说明。
        // 主行内所有控件严格同中心，提示行单独居中（见 LayoutBar）。
        var bar = new Panel { Dock = DockStyle.Top, Height = UiLayout.TallBarHeight, BackColor = Theme.Bg.Surface };
        var layerCaption = new Label { Text = "层", AutoSize = true };
        bar.Controls.Add(layerCaption);
        _layerBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _layerBox.Width = Theme.Scale(this, 136);
        _layerBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressLayerChange) return;
            LoadLayer(_layerBox.SelectedIndex);
        };
        bar.Controls.Add(_layerBox);

        _hint.AutoSize = true;
        _hint.ForeColor = Theme.Ink.Muted;
        _hint.Text = DefaultHint;
        bar.Controls.Add(_hint);

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 10, 0) };
        _read.Click += (_, _) => ReadFromDevice();
        _write.Click += (_, _) => WriteToDevice();
        _reset.Click += (_, _) => ResetKeymap();
        right.Controls.Add(_read);
        right.Controls.Add(_write);
        right.Controls.Add(_reset);
        bar.Controls.Add(right);
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });

        // 提示行宽度跟着栏走；高度按两行预留，文字在一行/两行之间切换时栏高不跳。
        _layerCaption = layerCaption;
        _barRight = right;
        bar.Layout += (_, _) => LayoutBar();
        _hint.SizeChanged += (_, _) => LayoutBar();
        _bar = bar;
        LayoutBar();

        _canvas.Dock = DockStyle.Fill;
        _canvas.KeyClicked += OnKeyClicked;

        Controls.Add(_canvas);
        Controls.Add(bar);

        _session.ReadOnlyChanged += OnReadOnlyChanged;
        SyncWriteButtons();
    }

    /// <summary>
    /// 顶栏排布：主行（层标签 / 下拉框 / 按钮组）与提示行两行整体居中，
    /// 主行内四个控件严格同中心；提示行按自己的行高居中。
    /// 提示文字会在「一行」和「两行」之间切换，所以每次布局都要重算一次。
    /// </summary>
    private void LayoutBar()
    {
        if (_layingOutBar || _bar == null || _layerCaption == null || _barRight == null) return;
        _layingOutBar = true;
        try
        {
            // 提示行的可用宽度只能到右侧按钮组的左边 —— 按钮组是 Dock=Right、铺满整栏高
            // 的不透明面板，提示文字一旦越过去就被它盖住。实测：按「整栏宽 - 28」给成 1052 时，
            // 标签宽 1040（右边缘 1054），其中 745 往后的 300px 被按钮组遮掉，文字被硬截。
            int avail = Math.Max(180, _barRight.Left - 14 - 12);

            int mainRow = UiLayout.RowHeight(_layerCaption, _layerBox, _read, _write, _reset);
            // 限制说明最多两行，按两行预留，文字在一行/两行之间切换时栏高与位置都不跳。
            // 一行的高度问 Label 自己（见 UiLayout.LabelLineHeight）—— 实测同一字体下
            // TextRenderer 量到 17，Label 的 AutoSize 高度是 20，差 3px 就会把第二行裁掉。
            int hintRow = UiLayout.LabelLineHeight(_hint.Font) * 2;
            // 宽高一起限：高度上限保证标签不会超过预留的两行，顶不穿栏底。
            var max = new Size(avail, hintRow);
            if (_hint.MaximumSize != max) _hint.MaximumSize = max;

            var rows = UiLayout.TwoRowBar(_bar.Height, mainRow, hintRow);
            _layerCaption.Left = 14;
            _layerCaption.Top = UiLayout.CenterInRow(_layerCaption, rows.MainTop, rows.MainHeight);
            _layerBox.Left = 44;
            _layerBox.Top = UiLayout.CenterInRow(_layerBox, rows.MainTop, rows.MainHeight);
            _hint.Left = 14;
            _hint.Top = UiLayout.CenterInRow(_hint, rows.HintTop, rows.HintHeight);
            UiLayout.CenterFlowRow(_barRight, rows.MainTop, rows.MainHeight);
        }
        finally { _layingOutBar = false; }
    }

    // ---------------- 实时高亮 ----------------

    /// <summary>
    /// 高亮是常驻的：连上就开、断开就停。页面切走也不停 ——
    /// 监听本身几乎不耗资源（集合不变就不通知），换来的是切回来立刻可用、不用等重启监听。
    /// </summary>
    private void SyncHighlight()
    {
        if (_session.Ready) StartHighlight();
        else StopHighlight();
    }

    /// <summary>
    /// 用一层的键码建立 usage → 矩阵下标 的反查表。
    /// QMK 基本键码 0x0004–0x00E7 的低字节就是 HID usage（修饰键 0x00E0–0x00E7 本身即 usage），
    /// 其余（媒体键、层功能键、自定义键、0x0000/0x0001）推不出 usage，只能跳过。
    /// </summary>
    internal static Dictionary<byte, List<int>> BuildUsageMap(ushort[] keycodes)
    {
        var map = new Dictionary<byte, List<int>>();
        for (int i = 0; i < keycodes.Length; i++)
        {
            ushort kc = keycodes[i];
            if (kc < 0x0004 || kc > 0x00E7) continue;
            byte usage = (byte)kc;
            if (!map.TryGetValue(usage, out var list)) map[usage] = list = new List<int>();
            list.Add(i);
        }
        return map;
    }

    /// <summary>反查表跟着"画布上正在显示的键码"重建，保证高亮和键帽上的字一致。</summary>
    private void RebuildUsageMap() => _usageToIndex = BuildUsageMap(_canvas.Keycodes);

    private void StartHighlight()
    {
        if (_monitor != null) return;

        var m = new KeyboardInputMonitor(_session.Device?.VendorId ?? 0, _session.Device?.ProductId ?? 0);
        m.Changed += OnPressedChanged;
        if (!m.Start())
        {
            string err = m.LastError;
            m.Dispose();
            _hint.Text = err;
            return;
        }

        _monitor = m;
        _canvas.Pressed = new HashSet<int>();
        _hint.Text = HighlightHint;
    }

    private void StopHighlight()
    {
        var m = _monitor;
        _monitor = null;
        if (m != null)
        {
            m.Changed -= OnPressedChanged;
            m.Stop();
            m.Dispose();
            _hint.Text = DefaultHint;
        }
        if (_canvas.Pressed.Count > 0)
        {
            _canvas.Pressed = new HashSet<int>();
            _canvas.Invalidate();
        }
    }

    /// <summary>后台线程回调：把 usage 集合翻成矩阵下标集合，再切回 UI 线程更新画布。</summary>
    private void OnPressedChanged(IReadOnlyCollection<byte> usages)
    {
        var map = _usageToIndex;
        var next = new HashSet<int>();
        foreach (byte u in usages)
            if (map.TryGetValue(u, out var list))
                foreach (int i in list) next.Add(i);

        SafeUi(() =>
        {
            if (_monitor == null) return;                  // 已经停掉了，丢弃迟到的回调
            if (next.SetEquals(_canvas.Pressed)) return;   // 集合没变就不重绘
            _canvas.Pressed = next;
            _canvas.Invalidate();
        });
    }

    /// <summary>把回调排到 UI 线程；控件已释放或句柄还没建时静默丢弃。</summary>
    private void SafeUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { /* 页面正在销毁 */ }
        catch (InvalidOperationException) { /* 句柄已消失 */ }
    }

    private void OnReadOnlyChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { SafeUi(OnReadOnlyChanged); return; }
        SyncWriteButtons();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.ReadOnlyChanged -= OnReadOnlyChanged;
            // 这里只停线程，不碰 _hint/_canvas —— 它们可能已经先被释放了
            _monitor?.Dispose();
            _monitor = null;
        }
        base.Dispose(disposing);
    }

    private static string FormatLayerLabel(int layer)
    {
        if (Keycodes.LayerAliases.TryGetValue(layer, out var alias))
            return $"第 {layer} 层 ({alias})";
        return layer == 0 ? "第 0 层 (主层)" : $"第 {layer} 层";
    }

    public void OnSessionChanged()
    {
        var def = _session.Definition;
        if (def == null)
        {
            _hint.Text = "未找到 VIA 定义文件（defs/*.json），无法渲染键盘。";
            SyncWriteButtons();
            return;
        }
        _canvas.KeyLayout = def.Keys;
        _canvas.Cols = def.Cols;
        _canvas.CustomKeycodes = def.CustomKeycodes;
        _canvas.OfflineFallback = false;

        _edited.Clear();
        _dirty.Clear();
        SyncPending();

        _layerBox.Items.Clear();
        for (int i = 0; i < Math.Max(1, _session.LayerCount); i++)
            _layerBox.Items.Add(FormatLayerLabel(i));

        SyncWriteButtons();

        // 重连或切换连接模式时先停掉旧监听以刷新 Raw Input 句柄，再按当前状态决定是否开启
        StopHighlight();
        SyncHighlight();

        if (_layerBox.Items.Count > 0) _layerBox.SelectedIndex = 0;
        RebuildUsageMap();
        _canvas.Invalidate();
    }

    /// <summary>
    /// 取某层的「待写入下标」集合，不存在则补建。
    /// 必须统一走这里：_edited 与 _dirty 是两个独立字典，任何只更新其中一个的地方
    /// 都会让两者失配，之后 _dirty[layer] 就会抛 KeyNotFoundException。
    /// </summary>
    private HashSet<int> DirtyOf(int layer)
    {
        if (!_dirty.TryGetValue(layer, out var s)) _dirty[layer] = s = new HashSet<int>();
        return s;
    }

    /// <summary>
    /// 切层。命中缓存（_edited 已有该层）时**完全同步**走完 —— 没有任何 IO，
    /// 不能因为 await 把一次即时重绘拆成两帧。
    /// 未命中时才异步读键盘。
    /// </summary>
    private void LoadLayer(int layer)
    {
        if (layer < 0 || _session.Definition == null) return;
        _canvas.Layer = layer;

        if (_edited.ContainsKey(layer))
        {
            ShowLayer(layer);
            return;
        }

        if (!_session.Ready)
        {
            if (layer == 0)
            {
                _canvas.OfflineFallback = true;
                _canvas.FallbackLabels = Rs8Defaults.FallbackPhysical;
            }
            else
            {
                _canvas.OfflineFallback = false;
                _canvas.FallbackLabels = Array.Empty<string>();
            }
            _canvas.Keycodes = new ushort[_session.Definition.Rows * _session.Definition.Cols];
            _canvas.Dirty = new HashSet<int>();
            RebuildUsageMap();
            _canvas.Invalidate();
            return;
        }

        _canvas.OfflineFallback = false;
        _ = LoadLayerAsync(layer);
    }

    /// <summary>
    /// 异步读一层并显示。
    /// 这里刻意**不用** DeviceSession.ReadLayerAsync：它的实现在 Task.Run 内部逐段判
    /// _batchWriteState 并可能回退逐键，路径和同步版两条；而 ShortcutPanel 已经确立了
    /// 本项目的既有范式 —— 把同步 ReadLayer 整体丢进 Task.Run。
    /// 好处是读写共用同一条代码路径、行为完全一致，且整段读占用 _ioLock 是原子的。
    /// 注意：同步 ReadLayer 内部是 _ioLock.Wait()（阻塞），所以**必须**在后台线程调用。
    /// </summary>
    private async Task LoadLayerAsync(int layer)
    {
        if (_busy) return;
        _busy = true;
        SetNavEnabled(false);
        Cursor = Cursors.WaitCursor;
        try
        {
            var map = await Task.Run(() =>
            {
                // 后台线程再查一次缓存：等待期间可能已有别的路径把它读回来了
                if (_session.TryGetCachedLayer(layer, out var cached)) return cached;
                return _session.ReadLayer(layer);
            }).ConfigureAwait(true);

            // await 回来时窗口可能已经关了，或者层已经被切走（用户又点了下拉框）
            if (IsDisposed) return;
            if (map == null)
            {
                string why = string.IsNullOrEmpty(_session.LastError) ? "" : $" [{_session.LastError}]";
                _hint.Text = $"读取第 {layer} 层失败（键盘可能休眠，按任意键唤醒后点「从键盘读取」）。{why}";
                return;
            }
            _edited[layer] = (ushort[])map.Clone();
            DirtyOf(layer).Clear();

            // 只在用户仍停在这一层时才刷画布；否则只把数据留进 _edited，等他切回来直接用缓存
            if (_canvas.Layer == layer) ShowLayer(layer);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                SetNavEnabled(true);
                Cursor = Cursors.Default;
            }
        }
    }

    /// <summary>把某层的待写入数据送给画布并重绘。调用方保证 _edited 里已有该层。</summary>
    private void ShowLayer(int layer)
    {
        _canvas.OfflineFallback = false;
        _canvas.Keycodes = _edited[layer];
        _canvas.Dirty = DirtyOf(layer);
        RebuildUsageMap();
        _canvas.Invalidate();
    }

    /// <summary>把"待写入键盘"的改动数上报给会话，供顶栏提示与关窗确认使用。</summary>
    private void SyncPending() => _session.SetPendingEdits(_dirty.Values.Sum(s => s.Count));

    /// <summary>
    /// 从键盘重读第 0 层（按钮回调入口）。
    /// 用 _busy 而不是 DoEvents：读键盘要占 UI 线程几百毫秒，老写法靠 DoEvents 把
    /// 等待光标刷出去，代价是那段时间用户能再点按钮 → 重入 ReadLayer。改成异步 + 停用
    /// 导航控件后，重入被从源头堵死，等待光标也能正常显示（线程已让出）。
    /// </summary>
    public void ReadFromDevice()
    {
        if (!_session.Ready) return;
        _ = ReadFromDeviceAsync();
    }

    private async Task ReadFromDeviceAsync()
    {
        if (_busy) return;
        _busy = true;
        SetNavEnabled(false);
        Cursor = Cursors.WaitCursor;
        try
        {
            _edited.Clear();
            _dirty.Clear();
            SyncPending();
            _session.ClearKeymapCache();

            var map = await Task.Run(() => _session.ReadLayer(0)).ConfigureAwait(true);

            if (IsDisposed) return;
            if (map == null)
            {
                string why = string.IsNullOrEmpty(_session.LastError) ? "" : $" [{_session.LastError}]";
                _hint.Text = $"读取失败（键盘可能休眠，按任意键唤醒后重试）。{why}";
                return;
            }
            _edited[0] = (ushort[])map.Clone();
            DirtyOf(0).Clear();
            _hint.Text = "已从键盘读取第 0 层。切换层时会自动读取该层。";
            _canvas.Layer = 0;
            if (_layerBox.Items.Count > 0)
            {
                _suppressLayerChange = true;
                try { _layerBox.SelectedIndex = 0; }
                finally { _suppressLayerChange = false; }
            }
            ShowLayer(0);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                SetNavEnabled(true);
                Cursor = Cursors.Default;
            }
        }
    }

    /// <summary>
    /// 读/写期间停用导航控件；离线时保持层下拉框可用，只读模式禁用写入/恢复按钮。
    /// 另外只停按钮、不停整个面板：画布还要能点、能看高亮。
    /// </summary>
    private void SetNavEnabled(bool enabled)
    {
        bool canRead = enabled && _session.Ready;
        bool canWrite = enabled && _session.Ready && !_session.EffectiveReadOnly;
        _layerBox.Enabled = enabled;
        _read.Enabled = canRead;
        _write.Enabled = canWrite;
        _reset.Enabled = canWrite;
    }

    private void SyncWriteButtons() => SetNavEnabled(!_busy);

    public void WriteToDevice()
    {
        if (!_session.Ready || _busy) return;
        if (_session.EffectiveReadOnly)
        {
            _hint.Text = "只读模式已开启，不能写入键盘。";
            return;
        }
        var def = _session.Definition;
        if (def == null) return;

        int total = _dirty.Values.Sum(s => s.Count);
        if (total == 0)
        {
            _hint.Text = "没有改动需要写入。";
            return;
        }
        if (MessageBox.Show(this, $"将写入 {total} 个按键改动到键盘，确定吗？", "RS8Control",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        _ = WriteToDeviceAsync(def, total);
    }

    private async Task WriteToDeviceAsync(ViaDefinition def, int total)
    {
        if (_busy) return;
        _busy = true;
        SetNavEnabled(false);
        Cursor = Cursors.WaitCursor;
        _hint.Text = $"正在写入 {total} 个按键…";
        try
        {
            // 快照：写入期间用户仍可点画布改键（只禁了按钮和下拉框，画布故意留着），
            // 所以把每个待写入项快照为不可变值 (Layer, Idx, Code)，写完再按当前值比对清理脏标记。
            var items = new List<(int Layer, int Idx, ushort Code)>();
            foreach (var kv in _dirty)
            {
                if (!_edited.TryGetValue(kv.Key, out var map)) continue;
                foreach (int idx in kv.Value)
                {
                    if (idx >= 0 && idx < map.Length)
                        items.Add((kv.Key, idx, map[idx]));
                }
            }
            int cols = def.Cols;

            var results = await Task.Run(() =>
            {
                var list = new List<(int Layer, int Idx, ushort Code, bool Ok)>(items.Count);
                foreach (var item in items)
                {
                    int row = item.Idx / cols, col = item.Idx % cols;
                    bool ok = _session.WriteKeycode(item.Layer, row, col, item.Code);
                    list.Add((item.Layer, item.Idx, item.Code, ok));
                }
                return list;
            }).ConfigureAwait(true);

            if (IsDisposed) return;

            int done = 0, fail = 0;
            foreach (var r in results)
            {
                if (r.Ok)
                {
                    done++;
                    // 只清理值仍等于本次写入快照的键；若写入期间用户又改了同一个键，保留其新脏标记
                    if (_edited.TryGetValue(r.Layer, out var curMap)
                        && r.Idx >= 0 && r.Idx < curMap.Length
                        && curMap[r.Idx] == r.Code)
                    {
                        DirtyOf(r.Layer).Remove(r.Idx);
                    }
                }
                else
                {
                    fail++;
                }
            }

            SyncPending();
            if (_dirty.Values.All(s => s.Count == 0))
                _session.ClearDirty();

            _canvas.Dirty = DirtyOf(_canvas.Layer);
            _canvas.Invalidate();

            if (fail == 0)
            {
                int remaining = _dirty.Values.Sum(s => s.Count);
                _hint.Text = remaining > 0
                    ? $"已写入 {done} 个按键（写入期间另有 {remaining} 处新改动待写入）。"
                    : $"已写入 {done} 个按键。";
            }
            else
            {
                _hint.Text = $"写入完成：成功 {done}，失败 {fail}。失败通常是键盘休眠，唤醒后重试。";
            }
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                SetNavEnabled(true);
                Cursor = Cursors.Default;
            }
        }
    }

    private void ResetKeymap()
    {
        if (!_session.Ready || _busy) return;
        if (_session.EffectiveReadOnly)
        {
            _hint.Text = "只读模式已开启，不能写入键盘。";
            return;
        }
        if (MessageBox.Show(this, "恢复出厂键位会清空所有自定义改键，确定吗？", "RS8Control",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _ = ResetKeymapAsync();
    }

    private async Task ResetKeymapAsync()
    {
        if (_busy) return;
        _busy = true;
        SetNavEnabled(false);
        Cursor = Cursors.WaitCursor;
        try
        {
            bool ok = await Task.Run(() => _session.ResetKeymap()).ConfigureAwait(true);
            if (IsDisposed) return;
            _hint.Text = ok ? "已恢复出厂键位。" : "恢复失败（键盘可能休眠）。";
            if (!ok) return;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                SetNavEnabled(true);
                Cursor = Cursors.Default;
            }
        }
        // 恢复成功就顺手重读一次，让界面立刻反映出厂状态。
        // 放在 finally 之后调用：_busy 必须先放开，否则 ReadFromDeviceAsync 会被守卫直接挡回来。
        ReadFromDevice();
    }

    private void OnKeyClicked(LayoutKey key)
    {
        if (_session.Definition == null) return;
        int layer = _canvas.Layer;
        if (!_edited.TryGetValue(layer, out var map))
        {
            map = new ushort[_session.Definition.Rows * _session.Definition.Cols];
            _edited[layer] = map;
            DirtyOf(layer).Clear();
        }
        int idx = key.Row * _session.Definition.Cols + key.Col;
        if (idx < 0 || idx >= map.Length) return;
        ushort cur = map[idx];

        // RS8 固件固定 4 层；未连接时 LayerCount 会回退成 1，这里取 4 保证离线也能选到所有层功能键。
        using var dlg = new KeycodePicker(cur, _session.Definition.CustomKeycodes, Math.Max(_session.LayerCount, 4));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        map[idx] = dlg.Selected;
        var dirty = DirtyOf(layer);
        dirty.Add(idx);
        SyncPending();
        _canvas.OfflineFallback = false;
        _canvas.Keycodes = map;
        _canvas.Dirty = dirty;
        RebuildUsageMap();
        _canvas.Invalidate();
        _hint.Text = $"已改 {key.Matrix} → {Keycodes.Describe(dlg.Selected, _session.Definition.CustomKeycodes)}（记得点「写入键盘」）";
    }
}

/// <summary>按 VIA 定义绘制的键盘控件。</summary>
public sealed class KeyboardCanvas : ScrollableControl
{
    private List<LayoutKey> _layout = new();
    private readonly List<LayoutKey> _shown = new();
    private readonly ToolTip _tip = new();
    private LayoutKey? _hoverKey;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Cols { get; set; } = 16;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Layer { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ushort[] Keycodes { get; set; } = Array.Empty<ushort>();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HashSet<int> Dirty { get; set; } = new();

    /// <summary>当前按下的矩阵下标（用于实时高亮）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HashSet<int> Pressed { get; set; } = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> CustomKeycodes { get; set; } = Array.Empty<string>();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] FallbackLabels { get; set; } = Array.Empty<string>();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool OfflineFallback { get; set; }

    public LayoutKey? Selected { get; private set; }

    public event Action<LayoutKey>? KeyClicked;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<LayoutKey> KeyLayout
    {
        get => _layout;
        set
        {
            _layout = value;
            _shown.Clear();
            _hoverKey = null;
            var seen = new HashSet<(int, int)>();
            foreach (var k in value)
                if (seen.Add((k.Row, k.Col))) _shown.Add(k);
            UpdateScrollBounds();
        }
    }

    public KeyboardCanvas()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Theme.Bg.Surface;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    private double _scale = 1;
    private double _ox, _oy;
    private bool _updatingScroll;

    private void UpdateScrollBounds()
    {
        if (_updatingScroll) return;
        _updatingScroll = true;
        try
        {
            if (_shown.Count == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                if (AutoScrollMinSize != Size.Empty) AutoScrollMinSize = Size.Empty;
                return;
            }

            double maxX = _shown.Max(k => k.X + k.W);
            double maxY = _shown.Max(k => k.Y + k.H);
            int pad = Theme.Scale(this, 18);
            double minScale = Theme.Scale(this, 32);

            double sx = (ClientSize.Width - pad * 2) / Math.Max(1, maxX);
            double sy = (ClientSize.Height - pad * 2) / Math.Max(1, maxY);

            int needW = sx < minScale ? (int)Math.Ceiling(maxX * minScale + pad * 2) : 0;
            int needH = sy < minScale ? (int)Math.Ceiling(maxY * minScale + pad * 2) : 0;
            var want = new Size(needW, needH);
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

    private void ComputeTransform()
    {
        if (_shown.Count == 0) return;

        double maxX = _shown.Max(k => k.X + k.W);
        double maxY = _shown.Max(k => k.Y + k.H);
        int pad = Theme.Scale(this, 18);
        double minScale = Theme.Scale(this, 32);
        double maxScale = Theme.Scale(this, 64);

        double sx = (ClientSize.Width - pad * 2) / Math.Max(1, maxX);
        double sy = (ClientSize.Height - pad * 2) / Math.Max(1, maxY);
        _scale = Math.Clamp(Math.Min(sx, sy), minScale, maxScale);

        bool scrollX = AutoScrollMinSize.Width > 0;
        bool scrollY = AutoScrollMinSize.Height > 0;

        _ox = scrollX
            ? pad + AutoScrollPosition.X
            : pad + (ClientSize.Width - pad * 2 - maxX * _scale) / 2;
        _oy = scrollY
            ? pad + AutoScrollPosition.Y
            : pad + (ClientSize.Height - pad * 2 - maxY * _scale) / 2;
    }

    private RectangleF RectOf(LayoutKey k)
    {
        float gap = Theme.ScaleF(this, 3f);
        return new(
            (float)(_ox + k.X * _scale),
            (float)(_oy + k.Y * _scale),
            (float)(k.W * _scale) - gap,
            (float)(k.H * _scale) - gap);
    }

    private LayoutKey? HitTest(Point pt)
    {
        ComputeTransform();
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            if (RectOf(_shown[i]).Contains(pt))
                return _shown[i];
        }
        return null;
    }

    private string KeyLegendOf(int idx, ushort kc)
    {
        if (OfflineFallback)
        {
            string? fb = idx >= 0 && idx < FallbackLabels.Length ? FallbackLabels[idx] : null;
            return string.IsNullOrEmpty(fb) ? "—" : fb;
        }
        return kc == 0 ? "—" : Model.Keycodes.Describe(kc, CustomKeycodes);
    }

    private void UpdateToolTip(LayoutKey? k)
    {
        if (k == null)
        {
            _tip.SetToolTip(this, "");
            return;
        }
        int idx = k.Row * Cols + k.Col;
        if (OfflineFallback)
        {
            string phys = KeyLegendOf(idx, 0);
            _tip.SetToolTip(this, $"矩阵 ({k.Row},{k.Col}) · {phys}（离线默认）");
        }
        else
        {
            ushort kc = idx >= 0 && idx < Keycodes.Length ? Keycodes[idx] : (ushort)0;
            string fullDesc = Model.Keycodes.Describe(kc, CustomKeycodes);
            _tip.SetToolTip(this, $"矩阵 ({k.Row},{k.Col}) · {fullDesc} (0x{kc:X4})");
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(BackColor);

        if (_shown.Count == 0)
        {
            // 空状态画在整幅画布正中，而不是左上角 —— 左上角那点字看起来像渲染残留
            TextRenderer.DrawText(g, "等待读取键位…", Theme.Fonts.Emphasis,
                ClientRectangle, Theme.Ink.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        ComputeTransform();

        // 字号下限提到 8pt：原来 6.5pt 时数字/符号键基本看不清。
        // 字号随缩放走，按尺寸缓存，避免在 OnPaint 内分配字体与 StringFormat。
        var font = Theme.FontAt(Theme.Fonts.KeycapFamily, Math.Max(8f, (float)(_scale * 0.17)));
        var smallFont = Theme.FontAt(Theme.Fonts.KeycapFamily, Math.Max(7f, (float)(_scale * 0.145)));
        float baseRadius = Theme.ScaleF(this, 6f);
        float thinBorder = Theme.ScaleF(this, 1f);
        float thickBorder = Theme.ScaleF(this, 2f);

        foreach (var k in _shown)
        {
            var r = RectOf(k);
            if (r.Width <= 1 || r.Height <= 1) continue;
            int idx = k.Row * Cols + k.Col;
            ushort kc = idx >= 0 && idx < Keycodes.Length ? Keycodes[idx] : (ushort)0;
            bool dirty = Dirty.Contains(idx);
            bool selected = Selected != null && Selected.Row == k.Row && Selected.Col == k.Col;
            bool pressed = Pressed.Contains(idx);
            bool hovered = _hoverKey != null && _hoverKey.Row == k.Row && _hoverKey.Col == k.Col;

            Color fill, border, textColor;
            float borderWidth = thinBorder;
            if (pressed)
            {
                fill = Theme.Key.PressedFill;
                border = Theme.Key.PressedBorder;
                textColor = Theme.Bg.Surface;
                borderWidth = thickBorder;
            }
            else if (selected)
            {
                fill = Theme.Key.SelectedFill;
                border = Theme.Key.SelectedBorder;
                textColor = Theme.Key.SelectedText;
                borderWidth = thickBorder;
            }
            else if (dirty)
            {
                fill = Theme.Key.DirtyFill;
                border = Theme.Key.DirtyBorder;
                textColor = Theme.Key.DirtyText;
            }
            else if (hovered)
            {
                fill = Theme.Key.HoverFill;
                border = Theme.Key.HoverBorder;
                textColor = OfflineFallback || kc != 0 ? Theme.Key.Text : Theme.Key.IdleText;
            }
            else if (OfflineFallback)
            {
                fill = Theme.Key.IdleFill;
                border = Theme.Key.IdleBorder;
                textColor = Theme.Key.IdleText;
            }
            else
            {
                fill = Theme.Key.Fill;
                border = Theme.Key.Border;
                textColor = kc == 0 ? Theme.Key.IdleText : Theme.Key.Text;
            }

            float radius = Math.Min(baseRadius, r.Height / 4f);
            KeycapRenderer.DrawCap(g, r, radius, fill, border, borderWidth, drawBottomEdge: !pressed);

            string text = KeyLegendOf(idx, kc);
            var inner = new RectangleF(r.X + 2f, r.Y + 2f, r.Width - 4f, r.Height - 4f);

            // 数字/符号键按真实键帽印字上下两行（上=Shift 字符，下=基础字符），
            // 单行并排写 "8*" 在 1u 键上很难分辨。
            if (TrySplitLegend(text, out string upper, out string lower) && r.Width >= _scale * 0.8 && r.Height >= _scale * 0.8)
            {
                KeycapRenderer.DrawSplitDigit(g, upper, lower, smallFont, font, r, textColor, textColor);
            }
            else
            {
                KeycapRenderer.DrawCenterText(g, text, font, inner, textColor);
            }
        }
    }

    /// <summary>
    /// 判断键帽印字能否拆成"上=Shift 字符 / 下=基础字符"两行。
    /// 只认恰好两个字符、且都不是字母的组合（"8*" "-_" "\\|" "0)"），
    /// 这样 F1/F2 这类字母+数字不会被误拆。
    /// </summary>
    private static bool TrySplitLegend(string label, out string upper, out string lower)
    {
        upper = lower = "";
        if (label.Length != 2) return false;
        if (char.IsLetter(label[0]) || char.IsLetter(label[1])) return false;
        if (label[0] > 0x7E || label[1] > 0x7E) return false;
        lower = label[0].ToString();
        upper = label[1].ToString();
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (ReferenceEquals(hit, _hoverKey)) return;
        _hoverKey = hit;
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        UpdateToolTip(hit);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverKey == null) return;
        _hoverKey = null;
        Cursor = Cursors.Default;
        _tip.SetToolTip(this, "");
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var hit = HitTest(e.Location);
        if (hit == null) return;
        Selected = hit;
        Invalidate();
        KeyClicked?.Invoke(hit);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _tip.Dispose();
    }
}
