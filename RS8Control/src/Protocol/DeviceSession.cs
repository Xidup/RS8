using System.Diagnostics;
using RS8Control.Hid;
using RS8Control.Model;

namespace RS8Control.Protocol;

public enum DeviceLinkState { NotConnected, DeviceFound, ViaReady, ReadOnly, Error }

/// <summary>设备会话：负责发现、连接、以及所有对键盘的读写。</summary>
public sealed class DeviceSession : IDisposable
{
    /// <summary>2.4G 接收器的 PID —— 它的 0xFF60 写入通道永远不完成传输，只能跳过。</summary>
    private const ushort PidReceiver24G = 0x3002;

    /// <summary>灯效下发的合并窗口（毫秒）：窗口内同一 (channel,valueId) 的连续写入只保留最后一次。</summary>
    private const int LightMergeWindowMs = 30;

    private ViaClient? _via;

    /// <summary>HID 是单通道，所有收发都必须串行化（UI 线程与后台线程共用）。</summary>
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>线程安全的键位缓存：layer → [row*cols+col] → keycode。</summary>
    private readonly object _keymapGate = new();
    private readonly Dictionary<int, ushort[]> _keymap = new();

    // ---- 0x13 批量写入能力：0=未自检 1=可用 2=不可用（永久回退逐键） ----
    private int _batchWriteState;

    // ---- 灯效下发队列 ----
    private readonly object _lightGate = new();
    private readonly Dictionary<(byte Channel, byte ValueId), ushort> _lightPending = new();
    private readonly SemaphoreSlim _lightSignal = new(0, int.MaxValue);
    private Task? _lightTask;
    private CancellationTokenSource? _lightCts;
    private int _lightInFlight;

    private volatile bool _disposed;
    private bool _userReadOnly;

    public DeviceSet? Device { get; private set; }
    public DeviceLinkState State { get; private set; } = DeviceLinkState.NotConnected;
    public string StatusText { get; private set; } = "未连接";
    public string DetailText { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public byte ProtocolVersion { get; private set; }
    public int LayerCount { get; private set; } = 1;
    public ViaDefinition? Definition { get; private set; }

    /// <summary>固件声明的宏数量（0x0C），连上后读取并缓存。</summary>
    public int MacroCount { get; private set; }

    /// <summary>固件声明的宏缓冲字节数（0x0D，RS8 上是 338），连上后读取并缓存。</summary>
    public int MacroBufferSize { get; private set; }

    /// <summary>已下发到设备但尚未保存（0x09 写 EEPROM）的改动计数。</summary>
    public int DirtyCount { get; private set; }

    /// <summary>用户手动开启的只读开关：为 true 时一切写入都直接拒绝，用于防止误改键盘。</summary>
    public bool UserReadOnly
    {
        get => _userReadOnly;
        set
        {
            if (_userReadOnly == value) return;
            _userReadOnly = value;
            ReadOnlyChanged?.Invoke();
        }
    }

    /// <summary>有效只读态：用户手动只读 或 2.4G 只读。</summary>
    public bool EffectiveReadOnly => _userReadOnly || ReadOnlyMode;

    public event Action? Changed;
    public event Action? DirtyChanged;
    public event Action? ReadOnlyChanged;

    public DeviceSession()
    {
        try { Definition = ViaDefinition.AutoLoad(); }
        catch (Exception ex) { DetailText = "读取 VIA 定义失败: " + ex.Message; }
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>2.4G 接收器连读都做不到，只能算"看到了设备"。</summary>
    public bool ReadOnlyMode => State == DeviceLinkState.ReadOnly;

    // ---------------- 键位缓存（线程安全） ----------------

    public bool TryGetCachedLayer(int layer, out ushort[] map)
    {
        lock (_keymapGate)
        {
            if (_keymap.TryGetValue(layer, out var existing))
            {
                map = (ushort[])existing.Clone();
                return true;
            }
        }
        map = Array.Empty<ushort>();
        return false;
    }

    public void ClearKeymapCache()
    {
        lock (_keymapGate) _keymap.Clear();
    }

    private void SetCachedLayer(int layer, ushort[] buf)
    {
        lock (_keymapGate) _keymap[layer] = (ushort[])buf.Clone();
    }

    // ---------------- 发现 / 握手 ----------------

    public void Refresh()
    {
        if (_disposed) return;

        bool shouldRaise;
        _ioLock.Wait();
        try { shouldRaise = RefreshLocked(); }
        finally { _ioLock.Release(); }

        // 绝不在持有 _ioLock 时触发事件：订阅者可能在回调里读写设备或触发阻塞操作
        if (shouldRaise && !_disposed) Raise();
    }

    private bool RefreshLocked()
    {
        if (_disposed) return false;
        DisposeClient();
        Device = DeviceDiscovery.Discover();

        if (Device.Others.Count == 0 && !Device.Any)
        {
            State = DeviceLinkState.NotConnected;
            StatusText = "未检测到设备";
            DetailText = "请插上 USB 线，或确认 2.4G 接收器已插入。";
            return true;
        }

        if (Device.ViaCandidates.Count == 0)
        {
            State = DeviceLinkState.DeviceFound;
            StatusText = $"已找到设备但无 VIA 通道  (VID_{Device.VendorId:X4}/PID_{Device.ProductId:X4})";
            DetailText = "该固件未提供 VIA Raw HID（0xFF60/0x61）接口。";
            return true;
        }

        bool only24G = Device.ViaCandidates.All(c => c.ProductId == PidReceiver24G);

        var failures = new List<string>();
        foreach (var cand in Device.ViaCandidates)
        {
            if (cand.ProductId == PidReceiver24G)
            {
                failures.Add($"MI={cand.InterfaceNumber}/PID_{cand.ProductId:X4} 2.4G 接收器（只读，跳过握手）");
                continue;
            }

            var via = new ViaClient(cand);
            via.WriteTimeoutMs = 300;
            via.WriteRetries = 2;
            if (!via.Open())
            {
                failures.Add($"MI={cand.InterfaceNumber}/PID_{cand.ProductId:X4} 打开失败(Win32 {via.LastWin32Error})");
                via.Dispose();
                continue;
            }
            if (!via.Handshake(out string msg))
            {
                failures.Add($"MI={cand.InterfaceNumber}/PID_{cand.ProductId:X4} {msg}");
                via.Dispose();
                continue;
            }

            via.WriteTimeoutMs = 500;
            via.WriteRetries = 3;

            _via = via;
            Device.ViaChannel = cand;
            // 同步当前真正握手成功的接口信息（避免多设备时 VID/PID 仍指向首位候选）
            Device.VendorId = cand.VendorId;
            Device.ProductId = cand.ProductId;
            if (!string.IsNullOrWhiteSpace(cand.Product)) Device.ProductName = cand.Product;
            if (!string.IsNullOrWhiteSpace(cand.Manufacturer)) Device.Manufacturer = cand.Manufacturer;
            if (!string.IsNullOrWhiteSpace(cand.SerialNumber)) Device.SerialNumber = cand.SerialNumber;

            ProtocolVersion = via.ProtocolVersion;
            LayerCount = Math.Max(1, via.GetLayerCount());
            MacroCount = Math.Max(0, via.GetMacroCount());
            MacroBufferSize = Math.Max(0, via.GetMacroBufferSize());
            State = DeviceLinkState.ViaReady;
            string mode = cand.ProductId switch
            {
                PidReceiver24G => "2.4G 接收器",
                0x3005 => "USB 有线",
                _ => $"PID {cand.ProductId:X4}",
            };
            StatusText = $"已连接 — {mode}，VIA 协议 v{ProtocolVersion}，{LayerCount} 层";
            DetailText = "";
            return true;
        }

        if (only24G)
        {
            Device.ViaChannel ??= Device.ViaCandidates.FirstOrDefault(c => c.ProductId == PidReceiver24G);
            State = DeviceLinkState.ReadOnly;
            StatusText = "2.4G 只读 — 无法与键盘通信";
            DetailText = "2.4G 接收器的写入通道不完成传输，协议层面读不到也写不了，请插 USB 数据线。";
            return true;
        }

        State = DeviceLinkState.Error;
        StatusText = $"VIA 握手失败（试了 {Device.ViaCandidates.Count} 个候选接口）";
        DetailText = string.Join("；", failures);
        return true;
    }

    public bool Ready => !_disposed && State == DeviceLinkState.ViaReady && _via != null;

    // ---------------- HID 串行化 ----------------

    /// <summary>在锁保护下使用当前连接。未就绪时返回 false 且不执行 action。</summary>
    private bool TryUse(Action<ViaClient> action)
    {
        if (_disposed) return false;
        _ioLock.Wait();
        try
        {
            var v = _via;
            if (_disposed || v == null || State != DeviceLinkState.ViaReady) return false;
            action(v);
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>在锁保护下执行带写入前置校验的原子操作。</summary>
    private bool TryWrite(Func<ViaClient, bool> action)
    {
        if (_disposed || EffectiveReadOnly) return false;
        bool ok = false;
        TryUse(v =>
        {
            if (EffectiveReadOnly) return;
            ok = action(v);
        });
        return ok;
    }

    // ---------------- 键位 ----------------

    private int MatrixBytes => Definition == null ? 0 : Definition.Rows * Definition.Cols * 2;

    /// <summary>用 0x12 / 0x0E 这类"分帧读缓冲"命令在单次锁内原子读完原始字节。失败返回 null。</summary>
    private static byte[]? ReadRawBufferCore(ViaClient v, byte cmd, int offset, int total)
    {
        var raw = new byte[total];
        for (int off = 0; off < total; off += ViaClient.MaxPayload)
        {
            int n = Math.Min(ViaClient.MaxPayload, total - off);
            int abs = offset + off;
            var resp = v.Cmd(cmd, (byte)(abs >> 8), (byte)(abs & 0xFF), (byte)n);
            if (resp == null || resp.Length < ViaClient.ReportSize || resp[0] != cmd)
                return null;
            Array.Copy(resp, 4, raw, off, n);
        }
        return raw;
    }

    private byte[]? ReadRawBuffer(byte cmd, int offset, int total)
    {
        byte[]? raw = null;
        if (!TryUse(v => raw = ReadRawBufferCore(v, cmd, offset, total)))
            return null;
        return raw;
    }

    private static ushort[] ParseBigEndian(byte[] raw, int count)
    {
        var buf = new ushort[count];
        for (int i = 0; i < count; i++) buf[i] = (ushort)((raw[i * 2] << 8) | raw[i * 2 + 1]);
        return buf;
    }

    private static ushort[]? ReadLayerByKeycodeCore(ViaClient v, int layer, int rows, int cols)
    {
        var buf = new ushort[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                ushort kc = v.GetKeycode((byte)layer, (byte)r, (byte)c);
                if (kc == 0xFFFF) return null;
                buf[r * cols + c] = kc;
            }
        }
        return buf;
    }

    /// <summary>
    /// 原子读取整层：在单次 _ioLock 保护下先试批量 0x12，失败则同一锁内回退逐键 0x04。
    /// </summary>
    public ushort[]? ReadLayer(int layer)
    {
        LastError = "";
        var def = Definition;
        if (!Ready) { LastError = "设备未就绪"; return null; }
        if (def == null) { LastError = "未加载 VIA 定义文件 (defs/RS8_via.json)"; return null; }

        ushort[]? buf = null;
        int expected = def.Rows * def.Cols;
        int total = expected * 2;
        if (!TryUse(v =>
        {
            var raw = ReadRawBufferCore(v, ViaClient.CmdKeymapGetBuffer, layer * total, total);
            buf = raw != null
                ? ParseBigEndian(raw, expected)
                : ReadLayerByKeycodeCore(v, layer, def.Rows, def.Cols);
            if (buf == null)
                LastError = string.IsNullOrEmpty(v.LastError) ? "读取键位无应答" : v.LastError;
        }))
        {
            if (string.IsNullOrEmpty(LastError)) LastError = "设备未就绪";
            return null;
        }
        if (buf == null) return null;

        SetCachedLayer(layer, buf);
        return (ushort[])buf.Clone();
    }

    /// <summary>异步批量读一层，progress 报告 0..100 百分比（在单次 _ioLock 内原子完成）。</summary>
    public Task<ushort[]?> ReadLayerAsync(int layer, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        return Task.Run<ushort[]?>(() =>
        {
            LastError = "";
            var def = Definition;
            if (!Ready) { LastError = "设备未就绪"; return null; }
            if (def == null) { LastError = "未加载 VIA 定义文件 (defs/RS8_via.json)"; return null; }

            int expected = def.Rows * def.Cols;
            int total = expected * 2;
            ushort[]? buf = null;

            if (!TryUse(v =>
            {
                var raw = new byte[total];
                int done = 0;
                bool batchOk = true;
                for (int off = 0; off < total; off += ViaClient.MaxPayload)
                {
                    if (ct.IsCancellationRequested) { batchOk = false; break; }
                    int n = Math.Min(ViaClient.MaxPayload, total - off);
                    int abs = layer * total + off;
                    var resp = v.Cmd(ViaClient.CmdKeymapGetBuffer,
                        (byte)(abs >> 8), (byte)(abs & 0xFF), (byte)n);
                    if (resp == null || resp.Length < ViaClient.ReportSize || resp[0] != ViaClient.CmdKeymapGetBuffer)
                    {
                        batchOk = false;
                        break;
                    }
                    Array.Copy(resp, 4, raw, off, n);
                    done += n;
                    progress?.Report(done * 100 / total);
                }

                if (ct.IsCancellationRequested) return;
                buf = batchOk
                    ? ParseBigEndian(raw, expected)
                    : ReadLayerByKeycodeCore(v, layer, def.Rows, def.Cols);
                if (buf == null)
                    LastError = string.IsNullOrEmpty(v.LastError) ? "读取键位无应答" : v.LastError;
            }))
            {
                if (string.IsNullOrEmpty(LastError)) LastError = "设备未就绪";
                return null;
            }

            if (buf == null) return null;
            SetCachedLayer(layer, buf);
            return (ushort[])buf.Clone();
        }, ct);
    }

    public bool WriteKeycode(int layer, int row, int col, ushort kc)
    {
        if (EffectiveReadOnly || !Ready) return false;
        bool ok = TryWrite(v => v.SetKeycode((byte)layer, (byte)row, (byte)col, kc));
        if (ok)
        {
            var def = Definition;
            if (def != null)
            {
                lock (_keymapGate)
                {
                    if (_keymap.TryGetValue(layer, out var map))
                    {
                        int idx = row * def.Cols + col;
                        if (idx >= 0 && idx < map.Length) map[idx] = kc;
                    }
                }
            }
            DirtyCount++;
            DirtyChanged?.Invoke();
        }
        return ok;
    }

    /// <summary>
    /// 整层写入。在单次 _ioLock 内同步执行 0x13 幂等自检（若未自检过），
    /// 通过后用 0x13 批量写入，否则永久回退逐键 0x05 —— 消除并发后台自检覆盖新键位的竞态。
    /// </summary>
    public bool WriteLayer(int layer, ushort[] map)
    {
        var def = Definition;
        if (EffectiveReadOnly || !Ready || def == null) return false;
        int expected = def.Rows * def.Cols;
        if (map.Length < expected) return false;

        bool ok = TryWrite(v =>
        {
            if (_batchWriteState == 0)
            {
                _batchWriteState = VerifyBatchWriteCore(v, MatrixBytes) ? 1 : 2;
            }

            if (_batchWriteState == 1)
            {
                var data = new byte[expected * 2];
                for (int i = 0; i < expected; i++)
                {
                    data[i * 2] = (byte)(map[i] >> 8);
                    data[i * 2 + 1] = (byte)(map[i] & 0xFF);
                }
                return v.SetKeymapBuffer(layer * expected * 2, data);
            }

            for (int r = 0; r < def.Rows; r++)
                for (int c = 0; c < def.Cols; c++)
                    if (!v.SetKeycode((byte)layer, (byte)r, (byte)c, map[r * def.Cols + c])) return false;
            return true;
        });

        if (ok) MarkLayerWritten(layer, map, expected);
        return ok;
    }

    private static bool VerifyBatchWriteCore(ViaClient v, int total)
    {
        if (total <= 0) return false;
        try
        {
            var before = ReadRawBufferCore(v, ViaClient.CmdKeymapGetBuffer, 0, total);
            if (before == null) return false;
            if (!v.SetKeymapBuffer(0, before)) return false;
            Thread.Sleep(400);
            var after = ReadRawBufferCore(v, ViaClient.CmdKeymapGetBuffer, 0, total);
            return after != null && after.SequenceEqual(before);
        }
        catch
        {
            return false;
        }
    }

    private void MarkLayerWritten(int layer, ushort[] map, int count)
    {
        int changed = count;
        lock (_keymapGate)
        {
            if (_keymap.TryGetValue(layer, out var cached))
            {
                changed = 0;
                for (int i = 0; i < count && i < cached.Length; i++)
                    if (cached[i] != map[i]) changed++;
                Array.Copy(map, cached, Math.Min(count, cached.Length));
            }
        }
        if (changed > 0)
        {
            DirtyCount += changed;
            DirtyChanged?.Invoke();
        }
    }

    public bool ResetKeymap()
    {
        if (EffectiveReadOnly || !Ready) return false;
        bool ok = TryWrite(v => v.ResetKeymap());
        if (ok) ClearKeymapCache();
        return ok;
    }

    // ---------------- 宏 ----------------

    /// <summary>0x0E 原子读完整个宏缓冲（338 字节 = 13 帧）。未连接/固件没报告大小时返回 null。</summary>
    public byte[]? ReadMacroBuffer()
    {
        int size = MacroBufferSize;
        if (!Ready || size <= 0) return null;
        return ReadRawBuffer(ViaClient.CmdMacroGetBuffer, 0, size);
    }

    /// <summary>
    /// 0x0F 在单次锁内分帧写入整个宏缓冲，写完等固件落盘再回读校验一次，返回"写入并校验一致"。
    /// 只读开关打开、未连接、长度与固件声明不符都直接拒绝（一个字节都不写）。
    /// 绝不调用 0x10（清空宏缓冲）—— 那是破坏性命令，全项目不碰。
    /// </summary>
    public bool WriteMacroBuffer(byte[] buffer)
    {
        if (EffectiveReadOnly || !Ready) return false;
        int size = MacroBufferSize;
        if (buffer == null || size <= 0 || buffer.Length != size) return false;

        bool match = false;
        TryWrite(v =>
        {
            if (!v.SetMacroBuffer(0, buffer)) return false;
            Thread.Sleep(300);
            var back = ReadRawBufferCore(v, ViaClient.CmdMacroGetBuffer, 0, size);
            match = back != null && back.SequenceEqual(buffer);
            return match;
        });
        return match;
    }

    // ---------------- 脏数据计数（聚合键位 / 宏 / 灯效三类未保存状态） ----------------

    /// <summary>界面上已改但尚未写入键盘的键位改动数。</summary>
    public int PendingEdits { get; private set; }

    /// <summary>宏编辑器是否有尚未写入键盘的改动。</summary>
    public bool HasUnsavedMacros { get; private set; }

    /// <summary>灯效页是否有尚未保存到 EEPROM 的改动。</summary>
    public bool HasUnsavedLights { get; private set; }

    /// <summary>是否存在任意未写入/未保存的改动。</summary>
    public bool HasAnyUnsaved => PendingEdits > 0 || HasUnsavedMacros || HasUnsavedLights;

    public void SetPendingEdits(int count)
    {
        if (count < 0) count = 0;
        if (PendingEdits == count) return;
        PendingEdits = count;
        DirtyChanged?.Invoke();
    }

    public void SetUnsavedMacros(bool dirty)
    {
        if (HasUnsavedMacros == dirty) return;
        HasUnsavedMacros = dirty;
        DirtyChanged?.Invoke();
    }

    public void SetUnsavedLights(bool dirty)
    {
        if (HasUnsavedLights == dirty) return;
        HasUnsavedLights = dirty;
        DirtyChanged?.Invoke();
    }

    /// <summary>生成顶栏与关窗确认用的未保存状态摘要；无改动时返回空串。</summary>
    public string BuildUnsavedSummary()
    {
        var parts = new List<string>();
        if (PendingEdits > 0) parts.Add($"键位 {PendingEdits} 处未写入");
        if (HasUnsavedMacros) parts.Add("宏有未写入改动");
        if (HasUnsavedLights) parts.Add("灯效尚未保存到键盘");
        return string.Join(" · ", parts);
    }

    public void ClearDirty()
    {
        DirtyCount = 0;
        DirtyChanged?.Invoke();
    }

    // ---------------- 灯效 ----------------

    public int ReadLight(byte channel, byte valueId)
    {
        if (!Ready) return -1;
        int v = -1;
        TryUse(c => v = c.GetCustomValue(channel, valueId));
        return v;
    }

    public bool WriteLight(byte channel, byte valueId, ushort value)
    {
        if (EffectiveReadOnly || !Ready) return false;
        return TryWrite(c => c.SetCustomValue(channel, valueId, value));
    }

    /// <summary>0x09 —— 写 EEPROM，只能由 UI 显式调用，绝不由灯效队列触发。</summary>
    public bool SaveLights()
    {
        if (EffectiveReadOnly || !Ready) return false;
        return TryWrite(c => c.CustomSave());
    }

    /// <summary>
    /// 实时下发一个灯效值（只写设备内存 0x07）。同一个 (channel,valueId) 的连续写入
    /// 在 30ms 窗口内合并，只保留最后一次；实际发送走单条后台串行队列，不阻塞 UI。
    /// 只读模式下直接丢弃。
    /// </summary>
    public void QueueLightWrite(byte channel, byte valueId, ushort value)
    {
        if (_disposed || EffectiveReadOnly || !Ready) return;

        lock (_lightGate)
        {
            if (_disposed) return;
            _lightPending[(channel, valueId)] = value;
            if (_lightTask == null)
            {
                _lightCts?.Dispose();
                _lightCts = new CancellationTokenSource();
                var token = _lightCts.Token;
                _lightTask = Task.Run(() => LightWorker(token), token);
            }
        }
        _lightSignal.Release();
    }

    private void LightWorker(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { _lightSignal.Wait(ct); }
                catch (OperationCanceledException) { break; }
                if (ct.IsCancellationRequested) break;

                while (_lightSignal.Wait(0)) { }

                if (ct.WaitHandle.WaitOne(LightMergeWindowMs)) break;

                List<KeyValuePair<(byte Channel, byte ValueId), ushort>> batch;
                lock (_lightGate)
                {
                    if (_lightPending.Count == 0)
                    {
                        Monitor.PulseAll(_lightGate);
                        continue;
                    }
                    batch = _lightPending.ToList();
                    _lightPending.Clear();
                    _lightInFlight += batch.Count;
                }

                try
                {
                    foreach (var kv in batch)
                    {
                        if (ct.IsCancellationRequested) break;
                        TryWrite(v => v.SetCustomValue(kv.Key.Channel, kv.Key.ValueId, kv.Value));
                    }
                }
                finally
                {
                    lock (_lightGate)
                    {
                        _lightInFlight = Math.Max(0, _lightInFlight - batch.Count);
                        if (_lightPending.Count == 0 && _lightInFlight == 0)
                            Monitor.PulseAll(_lightGate);
                    }
                }
            }
        }
        finally
        {
            lock (_lightGate)
            {
                _lightTask = null;
                _lightInFlight = 0;
                Monitor.PulseAll(_lightGate);
            }
        }
    }

    /// <summary>等待灯效队列排空（松手 / 保存时调用）。</summary>
    public async Task FlushLightsAsync()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            bool idle;
            lock (_lightGate) idle = _lightPending.Count == 0 && _lightInFlight == 0;
            if (idle) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    // ---------------- 生命周期 ----------------

    public void DisposeClient()
    {
        lock (_lightGate)
        {
            _lightPending.Clear();
            Monitor.PulseAll(_lightGate);
        }

        _via?.Dispose();
        _via = null;
        _batchWriteState = 0;
        ClearKeymapCache();
        MacroCount = 0;
        MacroBufferSize = 0;
        if (HasUnsavedLights)
        {
            HasUnsavedLights = false;
            DirtyChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Task? worker;
        CancellationTokenSource? cts;
        lock (_lightGate)
        {
            _lightPending.Clear();
            cts = _lightCts;
            _lightCts = null;
            worker = _lightTask;
        }

        try { cts?.Cancel(); } catch { /* 忽略 */ }
        // 在锁外等待后台工作线程退出，避免它正等 _ioLock 时死锁
        if (worker != null)
        {
            try { worker.Wait(1000); } catch { /* 忽略 */ }
        }
        cts?.Dispose();

        _ioLock.Wait();
        try { DisposeClient(); }
        finally { _ioLock.Release(); }

        _ioLock.Dispose();
        _lightSignal.Dispose();
    }
}
