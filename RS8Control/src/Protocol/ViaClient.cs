using System.Diagnostics;
using RS8Control.Hid;

namespace RS8Control.Protocol;

/// <summary>
/// VIA / QMK Raw HID 协议客户端。
/// 通道：Usage Page 0xFF60 / Usage 0x61，32 字节定长报文（含 1 字节报告 ID，实际载荷 32）。
///
/// 报文布局（由 RS8 出厂固件实测确定，非教科书版 VIA）：
///   请求 = [cmd][arg0][arg1]...            其余补 0
///   响应 = [cmd][请求参数原样回显...][结果...]
/// 例如 get_keycode(layer,row,col) 的响应：
///   [0x04][layer][row][col][kc_hi][kc_lo]
/// 结果字节的偏移量按命令不同，见各方法注释。
///
/// 两个实测坑：
///   1. 打开接口后的**第一条命令会失败**，必须先发一条无害命令"热身"。
///   2. 参数非法时首字节返回 0xFF（不是"命令未实现"）。
/// </summary>
public sealed class ViaClient : IDisposable
{
    // ---- 命令字（对应 QMK quantum/via.h） ----
    public const byte CmdGetProtocolVersion = 0x01;
    public const byte CmdGetKeyboardValue = 0x02;
    public const byte CmdSetKeyboardValue = 0x03;
    public const byte CmdKeymapGetKeycode = 0x04;
    public const byte CmdKeymapSetKeycode = 0x05;
    public const byte CmdKeymapReset = 0x06;
    public const byte CmdCustomSetValue = 0x07;
    public const byte CmdCustomGetValue = 0x08;
    public const byte CmdCustomSave = 0x09;
    public const byte CmdEepromReset = 0x0A;
    public const byte CmdBootloaderJump = 0x0B;
    public const byte CmdMacroGetCount = 0x0C;
    public const byte CmdMacroGetBufferSize = 0x0D;
    public const byte CmdMacroGetBuffer = 0x0E;
    public const byte CmdMacroSetBuffer = 0x0F;
    public const byte CmdMacroReset = 0x10;
    public const byte CmdGetLayerCount = 0x11;
    public const byte CmdKeymapGetBuffer = 0x12;
    public const byte CmdKeymapSetBuffer = 0x13;
    public const byte CmdKeymapGetEncoder = 0x14;

    // ---- 键盘信息子命令 ----
    public const byte KvUptime = 0x01;
    public const byte KvLayoutOptions = 0x02;
    public const byte KvSwitchMatrixState = 0x03;
    public const byte KvFirmwareVersion = 0x04;

    /// <summary>参数非法/命令不识别时首字节的值。</summary>
    public const byte ErrorMarker = 0xFF;

    public const int ReportSize = 32;

    /// <summary>单帧可携带的数据字节数（32 字节报文减去 4 字节命令头）。</summary>
    public const int MaxPayload = ReportSize - 4;

    private readonly HidConnection _conn;

    /// <summary>上一轮交换是否失败/超时 —— 只有这种情况下才需要清残留响应。</summary>
    private bool _drainPending;
    public HidInterfaceInfo Interface => _conn.Info;

    public byte ProtocolVersion { get; private set; }
    public bool Connected { get; private set; }
    public string LastError { get; private set; } = "";
    public int LastWin32Error => _conn.LastWin32Error;

    // ---- 收发超时与重试（可配置）----
    /// <summary>单次写入等待完成的超时（毫秒）。</summary>
    public int WriteTimeoutMs { get; set; } = 500;

    /// <summary>写入失败时的重试次数。</summary>
    public int WriteRetries { get; set; } = 3;

    /// <summary>等待响应回包的总超时（毫秒）。</summary>
    public int ResponseTimeoutMs { get; set; } = 800;

    public ViaClient(HidInterfaceInfo info) => _conn = new HidConnection(info);

    public bool Open()
    {
        if (!_conn.Open())
        {
            LastError = $"无法打开 HID 接口 (Win32 {_conn.LastWin32Error})";
            return false;
        }
        Connected = true;
        return true;
    }

    /// <summary>
    /// 热身：打开接口后第一条命令会失败，先发几条无害的只读命令把链路激活。
    /// </summary>
    public void WarmUp()
    {
        for (int i = 0; i < 3; i++)
        {
            var r = Cmd(CmdGetLayerCount);
            if (r != null && r[0] == CmdGetLayerCount) return;
            Thread.Sleep(60);
        }
    }

    /// <summary>握手：读协议版本。失败返回 false，message 说明原因。</summary>
    public bool Handshake(out string message)
    {
        WarmUp();

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var r = Cmd(CmdGetProtocolVersion);
            if (r != null && r[0] == CmdGetProtocolVersion)
            {
                ProtocolVersion = (byte)((r[1] << 8) | r[2]);
                if (ProtocolVersion != 0)
                {
                    message = $"VIA 协议版本 {ProtocolVersion}";
                    return true;
                }
            }
            Thread.Sleep(120);
        }

        ProtocolVersion = 0;
        message = LastError.Length > 0 ? LastError : "设备未按 VIA 协议应答";
        return false;
    }

    /// <summary>发送命令，返回完整 32 字节响应。失败返回 null。</summary>
    public byte[]? Cmd(byte cmd, params byte[] args)
    {
        LastError = "";
        var frame = new byte[ReportSize];
        frame[0] = cmd;
        Array.Copy(args, 0, frame, 1, Math.Min(args.Length, ReportSize - 1));

        // 只在上一轮交换失败/超时时清一次残留响应。正常路径不做任何排空读，
        // 否则每条命令都要白等一次 Read 超时（实测每条 ~20ms，逐键读一层要多花 2 秒）。
        if (_drainPending)
        {
            _drainPending = false;
            for (int k = 0; k < 8; k++)
                if (_conn.Read(5) == null) break;
        }

        for (int attempt = 0; attempt < WriteRetries; attempt++)
        {
            if (_conn.Write(frame, 0, WriteTimeoutMs) >= 0) break;
            if (attempt == WriteRetries - 1)
            {
                LastError = _conn.OpenedForWrite
                    ? $"写入失败 (Win32 {_conn.LastWin32Error})"
                    : $"接口不可写 (Win32 {_conn.LastWin32Error})";
                if (_conn.LastWin32Error == 31)
                    LastError += " —— 无线链路未就绪，请改用 USB 线或唤醒键盘";
                _drainPending = true;
                return null;
            }
            Thread.Sleep(150);
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ResponseTimeoutMs)
        {
            int remain = (int)(ResponseTimeoutMs - sw.ElapsedMilliseconds);
            var resp = _conn.Read(Math.Max(1, Math.Min(200, remain)));
            if (resp == null || resp.Length < ReportSize) continue;
            if (resp[0] == ErrorMarker) return resp;
            if (resp[0] == cmd && MatchesEcho(cmd, args, resp)) return resp;
        }
        LastError = "等待响应超时";
        _drainPending = true;
        return null;
    }

    /// <summary>
    /// 校验响应的请求参数回显，过滤掉上一轮超时后迟到的同命令字残留包。
    /// 仅对已实机核实过回显格式的命令做校验。
    /// </summary>
    private static bool MatchesEcho(byte cmd, byte[] args, byte[] resp)
    {
        return cmd switch
        {
            CmdGetKeyboardValue => args.Length < 1 || resp[1] == args[0],
            CmdCustomGetValue or CmdCustomSetValue =>
                args.Length < 2 || (resp[1] == args[0] && resp[2] == args[1]),
            CmdKeymapGetKeycode or CmdKeymapSetKeycode
                or CmdKeymapGetBuffer or CmdKeymapSetBuffer
                or CmdMacroGetBuffer or CmdMacroSetBuffer =>
                args.Length < 3 || (resp[1] == args[0] && resp[2] == args[1] && resp[3] == args[2]),
            _ => true,
        };
    }

    /// <summary>响应是否表示成功（首字节不是 0xFF）。</summary>
    private static bool Ok(byte[]? r) => r != null && r[0] != ErrorMarker;

    // ---------------- 高层封装（结果偏移量按实测布局） ----------------

    /// <summary>0x11 → 响应 [11][count]</summary>
    public int GetLayerCount()
    {
        var r = Cmd(CmdGetLayerCount);
        return Ok(r) ? r![1] : 1;
    }

    /// <summary>0x0C → 响应 [0C][count]</summary>
    public int GetMacroCount()
    {
        var r = Cmd(CmdMacroGetCount);
        return Ok(r) ? r![1] : 0;
    }

    /// <summary>0x0D → 响应 [0D][hi][lo]</summary>
    public int GetMacroBufferSize()
    {
        var r = Cmd(CmdMacroGetBufferSize);
        return Ok(r) ? (r![1] << 8) | r[2] : 0;
    }

    /// <summary>0x02 → 响应 [02][id][b3][b2][b1][b0]，返回 32 位大端值</summary>
    public int GetKeyboardValue(byte valueId)
    {
        var r = Cmd(CmdGetKeyboardValue, valueId);
        if (!Ok(r)) return -1;
        return (r![2] << 24) | (r[3] << 16) | (r[4] << 8) | r[5];
    }

    public int GetUptime() => GetKeyboardValue(KvUptime);
    public int GetFirmwareVersion() => GetKeyboardValue(KvFirmwareVersion);
    public int GetLayoutOptions() => GetKeyboardValue(KvLayoutOptions);

    /// <summary>0x04 → 响应 [04][layer][row][col][kc_hi][kc_lo]</summary>
    public ushort GetKeycode(byte layer, byte row, byte col)
    {
        var r = Cmd(CmdKeymapGetKeycode, layer, row, col);
        if (!Ok(r)) return 0xFFFF;
        return (ushort)((r![4] << 8) | r[5]);
    }

    /// <summary>0x05 → 请求 [05][layer][row][col][kc_hi][kc_lo]，响应为原样回显</summary>
    public bool SetKeycode(byte layer, byte row, byte col, ushort keycode)
    {
        var r = Cmd(CmdKeymapSetKeycode, layer, row, col, (byte)(keycode >> 8), (byte)(keycode & 0xFF));
        return r != null && r[0] == CmdKeymapSetKeycode;
    }

    /// <summary>0x06 恢复出厂键位</summary>
    public bool ResetKeymap()
    {
        var r = Cmd(CmdKeymapReset);
        return Ok(r);
    }

    /// <summary>0x08 → 响应 [08][channel][id][val_lo][val_hi]（实测为小端）</summary>
    public int GetCustomValue(byte channel, byte valueId)
    {
        var r = Cmd(CmdCustomGetValue, channel, valueId);
        if (!Ok(r)) return -1;
        return (r![4] << 8) | r[3];
    }

    /// <summary>0x07 → 请求 [07][channel][id][val_lo][val_hi]（小端）</summary>
    public bool SetCustomValue(byte channel, byte valueId, ushort value)
    {
        var r = Cmd(CmdCustomSetValue, channel, valueId, (byte)(value & 0xFF), (byte)(value >> 8));
        return r != null && r[0] == CmdCustomSetValue;
    }

    /// <summary>0x09 把灯效设置写入 EEPROM</summary>
    public bool CustomSave()
    {
        var r = Cmd(CmdCustomSave);
        return Ok(r);
    }

    /// <summary>0x12 → 响应 [12][off_hi][off_lo][size][data...]，数据从索引 4 开始。size 上限 28 字节。</summary>
    public byte[]? GetKeymapBuffer(int offset, int size)
    {
        int n = Math.Clamp(size, 0, MaxPayload);
        var r = Cmd(CmdKeymapGetBuffer, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)n);
        if (r == null || r[0] != CmdKeymapGetBuffer) return null;
        var data = new byte[n];
        Array.Copy(r, 4, data, 0, n);
        return data;
    }

    /// <summary>
    /// 0x13 分段写入键位缓冲。请求 = [13][off_hi][off_lo][size][data…]，每段最多 28 字节。
    /// 全部段成功才返回 true。
    /// </summary>
    public bool SetKeymapBuffer(int offset, byte[] data)
    {
        for (int off = 0; off < data.Length; off += MaxPayload)
        {
            int n = Math.Min(MaxPayload, data.Length - off);
            int abs = offset + off;
            var args = new byte[3 + n];
            args[0] = (byte)(abs >> 8);
            args[1] = (byte)(abs & 0xFF);
            args[2] = (byte)n;
            Array.Copy(data, off, args, 3, n);

            var r = Cmd(CmdKeymapSetBuffer, args);
            if (r == null || r[0] != CmdKeymapSetBuffer) return false;
        }
        return true;
    }

    /// <summary>0x0E → 响应 [0E][off_hi][off_lo][size][data...]，数据从索引 4 开始。size 上限 28 字节。</summary>
    public byte[]? GetMacroBuffer(int offset, int size)
    {
        int n = Math.Clamp(size, 0, MaxPayload);
        var r = Cmd(CmdMacroGetBuffer, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)n);
        if (r == null || r[0] != CmdMacroGetBuffer) return null;
        var data = new byte[n];
        Array.Copy(r, 4, data, 0, n);
        return data;
    }

    /// <summary>
    /// 0x0F 分段写入宏缓冲。请求 = [0F][off_hi][off_lo][size][data…]，每段最多 28 字节。
    /// 全部段成功才返回 true。**绝不发送 0x10（清空宏缓冲）**。
    /// </summary>
    public bool SetMacroBuffer(int offset, byte[] data)
    {
        for (int off = 0; off < data.Length; off += MaxPayload)
        {
            int n = Math.Min(MaxPayload, data.Length - off);
            int abs = offset + off;
            var args = new byte[3 + n];
            args[0] = (byte)(abs >> 8);
            args[1] = (byte)(abs & 0xFF);
            args[2] = (byte)n;
            Array.Copy(data, off, args, 3, n);

            var r = Cmd(CmdMacroSetBuffer, args);
            if (r == null || r[0] != CmdMacroSetBuffer) return false;
        }
        return true;
    }

    public void Dispose() => _conn.Dispose();
}