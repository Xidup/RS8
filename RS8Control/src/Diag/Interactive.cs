using RS8Control.Hid;
using RS8Control.Protocol;

namespace RS8Control.Diag;

/// <summary>需要人工配合的交互式测试。</summary>
public static class Interactive
{
    /// <summary>实时监听键盘输入，用于验证链路是否活着。</summary>
    public static void Watch(int seconds = 12)
    {
        var dev = DeviceDiscovery.Discover();
        if (!dev.Any && dev.Others.Count == 0)
        {
            Console.WriteLine("未检测到设备。");
            return;
        }

        Console.WriteLine($"设备: VID_{dev.VendorId:X4}/PID_{dev.ProductId:X4}  \"{dev.ProductName}\"");
        Console.WriteLine();
        Console.WriteLine($">>> 请在 {seconds} 秒内按几下键盘上的任意按键（例如 A、空格）<<<");
        Console.WriteLine();

        int count = 0;
        using var monitor = new KeyboardInputMonitor(dev.VendorId, dev.ProductId);
        monitor.Changed += pressed =>
        {
            Interlocked.Increment(ref count);
            string text = pressed.Count == 0
                ? "（全部松开）"
                : string.Join(" + ", pressed.Select(b => $"{Model.Keycodes.Describe(b)}(0x{b:X2})"));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
        };

        if (!monitor.Start())
        {
            Console.WriteLine(monitor.LastError);
            return;
        }

        var kbd = monitor.Interface!;
        Console.WriteLine($"监听接口: MI={kbd.InterfaceNumber}  (boot 键盘接口 {kbd.InputReportByteLength} 字节)");
        Console.WriteLine("已开始监听，等待按键…");

        Thread.Sleep(seconds * 1000);
        Console.WriteLine();
        Console.WriteLine(count == 0
            ? $"{seconds} 秒内没有收到任何按键 —— 键盘可能处于休眠，或链路未建立。"
            : $"共收到 {count} 次按键变化，链路正常。");
    }

    /// <summary>
    /// 自动扫描：把每个接口的报文结构彻底问清楚。
    /// 1) 报告 ID 扫描 —— 找出该接口接受哪个报告 ID（解决 Win32 87 参数错误）
    /// 2) 命令扫描 —— 逐个发命令字，把完整响应打出来（判断是回显 / 私有协议 / 真 VIA）
    /// </summary>
    public static void Scan(TextWriter? output = null)
    {
        var w = output ?? Console.Out;
        w.WriteLine("注意：本扫描只发送**只读**命令，不会修改键盘的键位、灯效或 EEPROM。");
        w.WriteLine();

        var all = Win32Hid.Enumerate().Where(a => a.VendorId == DeviceDiscovery.KnownVid).ToList();
        if (all.Count == 0)
        {
            w.WriteLine($"未找到 VID_{DeviceDiscovery.KnownVid:X4} 的设备。请插上 USB 线后重试。");
            return;
        }

        foreach (var pid in all.Select(a => a.ProductId).Distinct())
        {
            var set = all.Where(a => a.ProductId == pid).ToList();
            w.WriteLine("################################################################");
            w.WriteLine($"#  PID_{pid:X4}   共 {set.Count} 个接口");
            w.WriteLine("################################################################");
            w.WriteLine();

            foreach (var iface in set.Where(a => a.OutputReportByteLength > 0))
            {
                w.WriteLine("================================================================");
                w.WriteLine($"接口 MI={iface.InterfaceNumber}  UP=0x{iface.UsagePage:X4} U=0x{iface.Usage:X4}");
                w.WriteLine($"      IN={iface.InputReportByteLength} OUT={iface.OutputReportByteLength} FEAT={iface.FeatureReportByteLength}");
                w.WriteLine("================================================================");

                using var conn = new HidConnection(iface);
                if (!conn.Open())
                {
                    w.WriteLine($"  打开失败 (Win32 {conn.LastWin32Error})");
                    w.WriteLine();
                    continue;
                }
                w.WriteLine($"  打开成功  可写={conn.OpenedForWrite}");

                int outLen = Math.Max(1, (int)iface.OutputReportByteLength);
                int dataLen = Math.Max(1, outLen - 1);

                // ---- 1. 报告 ID 扫描 ----
                w.WriteLine();
                w.WriteLine("  [1] 报告 ID 扫描（发送全 0 载荷，看哪个 ID 被接受）");
                int goodId = -1;
                for (byte id = 0; id <= 7; id++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int n = conn.Write(new byte[dataLen], id);
                    sw.Stop();
                    string verdict = n > 0 ? "接受" : $"拒绝(Win32 {conn.LastWin32Error})";
                    w.WriteLine($"      id=0x{id:X2}  →  {verdict}   耗时 {sw.ElapsedMilliseconds} ms");
                    if (n > 0 && goodId < 0) goodId = id;
                }

                // ---- 2. 命令扫描（只读命令；写类命令会改键盘状态，必须排除） ----
                w.WriteLine();
                w.WriteLine("  [2] 只读命令扫描（已排除 0x03/0x05/0x06/0x07/0x09/0x0A/0x0B/0x0F/0x10/0x13/0x15）");
                byte[] cmds =
                {
                    0x01, 0x02, 0x04, 0x08, 0x0C, 0x0D, 0x0E, 0x11, 0x12, 0x14,
                    0x00, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
                    0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0, 0xFF,
                };
                foreach (byte cmd in cmds)
                {
                    var req = new byte[dataLen];
                    req[0] = cmd;
                    // 清空残留
                    for (int k = 0; k < 4; k++) if (conn.Read(20) == null) break;

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int n = conn.Write(req, (byte)Math.Max(0, goodId));
                    if (n <= 0)
                    {
                        w.WriteLine($"      cmd=0x{cmd:X2}  写入失败 (Win32 {conn.LastWin32Error})");
                        continue;
                    }
                    var resp = conn.Read(500);
                    sw.Stop();
                    if (resp == null || resp.Length == 0)
                    {
                        w.WriteLine($"      cmd=0x{cmd:X2}  →  无响应");
                        continue;
                    }
                    bool echo = resp.Length == req.Length && resp.SequenceEqual(req);
                    string note = echo ? "  ← 与请求完全相同（回显/未处理）" : "";
                    w.WriteLine($"      cmd=0x{cmd:X2}  →  {Convert.ToHexString(resp)}{note}");
                }
                w.WriteLine();
            }
        }

        w.WriteLine("################################################################");
        w.WriteLine("#  扫描结束");
        w.WriteLine("################################################################");
    }

    /// <summary>在两条通道上尝试各种写入策略，定位"写不进去"的原因。</summary>
    public static void WriteTest()
    {
        var dev = DeviceDiscovery.Discover();
        if (!dev.Any)
        {
            Console.WriteLine("未检测到设备。");
            return;
        }

        foreach (var (label, iface) in new[] { ("VIA 通道", dev.ViaChannel), ("私有通道", dev.PrivateChannel) })
        {
            if (iface == null) continue;
            Console.WriteLine($"===== {label}  MI={iface.InterfaceNumber}  OUT={iface.OutputReportByteLength} =====");

            using var conn = new HidConnection(iface);
            bool opened = conn.Open();
            Console.WriteLine($"  打开: {(opened ? "成功" : "失败")}  可写={conn.OpenedForWrite}  Win32={conn.LastWin32Error}");
            if (!opened) { Console.WriteLine(); continue; }

            int outLen = Math.Max(1, (int)iface.OutputReportByteLength);
            var payload = new byte[outLen];
            payload[0] = ViaClient.CmdGetProtocolVersion;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = conn.Write(payload);
            Console.WriteLine($"  [重叠 WriteFile]        {(n > 0 ? $"成功 {n} 字节" : "失败")}  耗时 {sw.ElapsedMilliseconds} ms  Win32={conn.LastWin32Error}");

            var raw = new byte[outLen];
            Array.Copy(payload, raw, outLen);
            bool ok2 = false;
            int err2 = 0;
            var t = new Thread(() =>
            {
                ok2 = Win32Hid.HidD_SetOutputReport(conn.Handle!, raw, raw.Length);
                err2 = Marshal.GetLastWin32Error();
            })
            { IsBackground = true };
            sw.Restart();
            t.Start();
            if (t.Join(2500)) Console.WriteLine($"  [HidD_SetOutputReport]  {(ok2 ? "成功" : "失败")}  耗时 {sw.ElapsedMilliseconds} ms  Win32={err2}");
            else Console.WriteLine("  [HidD_SetOutputReport]  2.5 秒未返回（挂起）");

            var leftover = conn.Read(400);
            Console.WriteLine($"  [读取残留]              {(leftover == null ? "无数据" : Convert.ToHexString(leftover))}");
            Console.WriteLine();
        }
    }

    /// <summary>从注册表转储目标设备的全部报告描述符（IOCTL 被拒时的唯一途径）。</summary>
    public static void DumpDescriptors()
    {
        var all = Win32Hid.Enumerate().Where(a => a.VendorId == DeviceDiscovery.KnownVid).ToList();
        if (all.Count == 0)
        {
            Console.WriteLine($"未找到 VID_{DeviceDiscovery.KnownVid:X4} 的设备。");
            return;
        }

        foreach (var grp in all.GroupBy(a => (a.ProductId, a.InterfaceNumber)).OrderBy(g => g.Key.InterfaceNumber))
        {
            var sample = grp.First();
            Console.WriteLine("================================================================");
            Console.WriteLine($"PID_{grp.Key.ProductId:X4}  MI={grp.Key.InterfaceNumber}   接口数 {grp.Count()}");
            foreach (var a in grp)
                Console.WriteLine($"    UP=0x{a.UsagePage:X4} U=0x{a.Usage:X4}  IN={a.InputReportByteLength} OUT={a.OutputReportByteLength} FEAT={a.FeatureReportByteLength}");

            Console.WriteLine();
        }

        Console.WriteLine("================================================================");
        Console.WriteLine("注册表里找到的报告描述符：");
        foreach (var pid in all.Select(a => a.ProductId).Distinct())
        {
            var found = Win32Hid.FindReportDescriptors(DeviceDiscovery.KnownVid, pid);
            Console.WriteLine($"\n--- PID_{pid:X4}  共 {found.Count} 份 ---");
            foreach (var (keyPath, desc) in found)
            {
                Console.WriteLine($"\n### {keyPath}");
                var parsed = HidReportDescriptorInfo.Parse(desc);
                Console.WriteLine($"  {desc.Length} 字节，{parsed.Collections.Count} 个集合:");
                foreach (var c in parsed.Collections)
                    Console.WriteLine($"     - depth={c.Depth} app={c.IsApplication} {c}");
                Console.WriteLine("  原始字节:");
                Console.WriteLine(HidReportDescriptorInfo.HexDump(desc).TrimEnd());
            }
        }
    }
}
