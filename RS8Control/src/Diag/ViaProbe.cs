using System.Diagnostics;
using RS8Control.Hid;
using RS8Control.Protocol;

namespace RS8Control.Diag;

/// <summary>针对 VIA 命令集的定向探针：确认报文布局、可用命令、以及读写是否真的生效。</summary>
public static class ViaProbe
{
    private const int Size = 32;

    public static void Run()
    {
        var dev = DeviceDiscovery.Discover();
        if (dev.ViaCandidates.Count == 0)
        {
            Console.WriteLine("没有找到 0xFF60/0x61 候选接口。请插上 USB 线。");
            return;
        }

        // 找一个"活着"的接口：能写进去且 0x0C 返回非 0
        HidInterfaceInfo? live = null;
        HidConnection? conn = null;
        foreach (var cand in dev.ViaCandidates)
        {
            var c = new HidConnection(cand);
            if (!c.Open()) { c.Dispose(); continue; }
            var r = Exchange(c, 0x0C, 0);
            if (r != null && r[0] == 0x0C)
            {
                live = cand;
                conn = c;
                break;
            }
            c.Dispose();
        }

        if (live == null || conn == null)
        {
            Console.WriteLine("所有 0xFF60 候选接口都不响应。请确认：");
            Console.WriteLine("  1) 用 USB 数据线连接（不是 2.4G 接收器）");
            Console.WriteLine("  2) 键盘已唤醒");
            return;
        }

        Console.WriteLine($"使用接口 MI={live.InterfaceNumber}  PID_{live.ProductId:X4}  IN={live.InputReportByteLength} OUT={live.OutputReportByteLength}");
        Console.WriteLine();

        using (conn)
        {
            // ---------- 1. 协议版本 ----------
            Console.WriteLine("【1】协议版本 (0x01)");
            Dump(conn, "0x01 无参数", 0x01);
            Dump(conn, "0x01 + 0x00", 0x01, 0x00);
            Dump(conn, "0x01 + 0x0C", 0x01, 0x0C);
            Console.WriteLine();

            // ---------- 2. 键码读取的报文布局 ----------
            Console.WriteLine("【2】键码读取 (0x04) —— 用不同参数判断是「请求回显」还是「直接结果」");
            Dump(conn, "layer=0 row=0 col=0", 0x04, 0, 0, 0);
            Dump(conn, "layer=1 row=2 col=3", 0x04, 1, 2, 3);
            Dump(conn, "layer=0 row=0 col=1", 0x04, 0, 0, 1);
            Dump(conn, "layer=0 row=0 col=5", 0x04, 0, 0, 5);
            Dump(conn, "layer=0 row=1 col=0", 0x04, 0, 1, 0);
            Console.WriteLine();

            // ---------- 3. 键盘信息 ----------
            Console.WriteLine("【3】键盘信息 (0x02) —— value_id 1..5");
            for (byte id = 1; id <= 5; id++) Dump(conn, $"value_id={id}", 0x02, id);
            Console.WriteLine();

            // ---------- 4. 灯效自定义值 ----------
            Console.WriteLine("【4】灯效自定义值 (0x08) —— channel 1..6 × id 1..5");
            for (byte ch = 1; ch <= 6; ch++)
            {
                for (byte id = 1; id <= 5; id++)
                {
                    var r = Exchange(conn, 0x08, ch, id);
                    Console.WriteLine($"      ch={ch} id={id}  →  {(r == null ? "<无响应>" : Convert.ToHexString(r[..6]))}");
                }
            }
            Console.WriteLine();

            // ---------- 5. 键位缓冲读取 ----------
            Console.WriteLine("【5】键位缓冲 (0x12) —— offset=0 size=4");
            Dump(conn, "offset=0 size=4", 0x12, 0, 0, 4);
            Dump(conn, "offset=0 size=8", 0x12, 0, 0, 8);
            Console.WriteLine();

            // ---------- 6. 宏与层数 ----------
            Console.WriteLine("【6】宏与层数");
            Dump(conn, "0x0C 宏数量", 0x0C);
            Dump(conn, "0x0D 宏缓冲大小", 0x0D);
            Dump(conn, "0x11 层数", 0x11);
            Console.WriteLine();

            // ---------- 7. 写入往返验证（改完立刻改回） ----------
            Console.WriteLine("【7】写入往返验证 —— 临时把矩阵 (0,0,0) 改成 F1(0x3A) 再改回 Esc(0x29)");
            ushort orig = ReadKc(conn, 0, 0, 0);
            Console.WriteLine($"      原始值      : 0x{orig:X4}");
            Console.WriteLine($"      写入 F1     : {(WriteKc(conn, 0, 0, 0, 0x003A) ? "命令已发" : "失败")}");
            Thread.Sleep(250);
            ushort after = ReadKc(conn, 0, 0, 0);
            Console.WriteLine($"      读回        : 0x{after:X4}   {(after == 0x003A ? "√ 写入生效" : "✗ 写入未生效")}");
            Console.WriteLine($"      改回 0x{orig:X4}   : {(WriteKc(conn, 0, 0, 0, orig) ? "命令已发" : "失败")}");
            Thread.Sleep(250);
            ushort restored = ReadKc(conn, 0, 0, 0);
            Console.WriteLine($"      最终读回    : 0x{restored:X4}   {(restored == orig ? "√ 已复原" : "✗ 未复原，请点「恢复出厂键位」")}");
            Console.WriteLine();

            // ---------- 7b. 灯效写入往返 ----------
            Console.WriteLine("【7b】灯效写入往返 —— 临时把通道2亮度改为 100 再改回");
            int origB = ReadLight(conn, 2, 1);
            Console.WriteLine($"      原始亮度    : {origB}");
            if (origB >= 0)
            {
                WriteLight(conn, 2, 1, 100);
                Thread.Sleep(250);
                int b2 = ReadLight(conn, 2, 1);
                Console.WriteLine($"      改 100 后读回: {b2}   {(b2 == 100 ? "√ 写入生效" : "✗ 未生效")}");
                WriteLight(conn, 2, 1, (ushort)origB);
                Thread.Sleep(250);
                int b3 = ReadLight(conn, 2, 1);
                Console.WriteLine($"      复原后读回  : {b3}   {(b3 == origB ? "√ 已复原" : "✗ 未复原")}");
            }
            Console.WriteLine();

            // ---------- 7c. 灯效编号范围探测 ----------
            Console.WriteLine("【7c】灯效编号范围探测 —— 判断原厂固件的效果表是否与 QMK 一致");
            Console.WriteLine("      (VIA JSON 里 QMK rgb_matrix 有 0..45；原厂 RS8Light.xml 只有 0..15 + 254)");
            foreach (byte ch in new byte[] { 2, 3, 4 })
            {
                int origFx = ReadLight(conn, ch, 2);
                var accepted = new List<int>();
                foreach (int fx in new[] { 0, 1, 5, 9, 15, 16, 20, 30, 45, 46, 100, 254, 255 })
                {
                    WriteLight(conn, ch, 2, (ushort)fx);
                    Thread.Sleep(70);
                    int rb = ReadLight(conn, ch, 2);
                    if (rb == fx) accepted.Add(fx);
                }
                Console.WriteLine($"      通道 {ch}: 原值={origFx}  原样接受的值={string.Join(",", accepted)}");
                if (origFx >= 0) WriteLight(conn, ch, 2, (ushort)origFx);
            }
            Thread.Sleep(200);
            Console.WriteLine();

            // ---------- 8. 命令集探测（只读！绝不发送写/重置类命令） ----------
            Console.WriteLine("【8】只读命令探测（跳过 0x05/0x06/0x07/0x09/0x0A/0x0B/0x0F/0x10/0x13 等写类命令）");
            Console.WriteLine("      —— 避免误触发键位重置 / EEPROM 清除 / 跳引导");
            byte[] readOnly =
            {
                0x01, 0x02, 0x04, 0x08, 0x0C, 0x0D, 0x0E, 0x11, 0x12, 0x14,
                0x00, 0x03, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
                0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0, 0xFF,
            };
            var handled = new List<byte>();
            var unknown = new List<byte>();
            var echoed = new List<byte>();
            foreach (byte cmd in readOnly)
            {
                var req = new byte[Size];
                req[0] = cmd;
                var r = ExchangeRaw(conn, req);
                if (r == null) continue;
                if (r[0] == 0xFF) unknown.Add(cmd);
                else if (r.SequenceEqual(req)) echoed.Add(cmd);
                else handled.Add(cmd);
            }
            Console.WriteLine($"      返回真实数据: {string.Join(" ", handled.Select(b => $"0x{b:X2}"))}");
            Console.WriteLine($"      返回 0xFF   : {string.Join(" ", unknown.Select(b => $"0x{b:X2}"))}");
            Console.WriteLine($"      原样回显    : {string.Join(" ", echoed.Select(b => $"0x{b:X2}"))}");
        }

        Console.WriteLine();
        Console.WriteLine("探针结束。");
    }

    /// <summary>恢复出厂键位（VIA 标准命令 0x06）。用于修复误写。</summary>
    public static void ResetKeymap()
    {
        var conn = OpenLive();
        if (conn == null) { Console.WriteLine("没有找到可用的 VIA 接口。"); return; }
        using (conn)
        {
            Console.WriteLine("发送 0x06 (dynamic_keymap_reset) …");
            var r = Exchange(conn, 0x06);
            Console.WriteLine($"响应: {(r == null ? "<无>" : Convert.ToHexString(r[..4]))}");
            Thread.Sleep(300);
            Console.WriteLine($"读回矩阵 (0,0,0): {Hex(Exchange(conn, 0x04, 0, 0, 0))}  （期望以 29 结尾 = Esc）");
        }
    }

    /// <summary>
    /// 0x13 (set_keymap_buffer) 幂等验证。
    /// 做法：用 0x12 读出整份键位缓冲 → 用 0x13 **原样写回** → 再读回比对。
    /// 全部一致才敢在正常流程里启用批量写入，否则回退逐键 0x05。
    /// 之所以先做这个：0x13 是写命令，写错位置会破坏键位（有 --reset-keymap 可救，但不该冒险）。
    /// </summary>
    public static void RunBufferTest()
    {
        var conn = OpenLive();
        if (conn == null) { Console.WriteLine("没有找到可用的 VIA 接口。请插 USB 线。"); return; }

        using (conn)
        {
            const int Chunk = Size - 4;  // 请求 [13][off_hi][off_lo][size][data…] → 数据位 28 字节
            const int Total = 192;       // 6 行 × 16 列 × 2 字节

            Console.WriteLine($"【1】0x12 批量读取 —— 每次 {Chunk} 字节，共 {Total} 字节（{(Total + Chunk - 1) / Chunk} 次传输）");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var before = ReadBuffer(conn, 0, Total, Chunk);
            sw.Stop();
            if (before == null) { Console.WriteLine("  读取失败 —— 0x12 不可用。"); return; }
            Console.WriteLine($"  耗时 {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  {Convert.ToHexString(before)}");
            Console.WriteLine();

            Console.WriteLine("【2】0x13 幂等写入 —— 把刚读出的数据原样写回（内容不变，安全）");
            sw.Restart();
            bool wrote = WriteBuffer(conn, 0, before, Chunk);
            sw.Stop();
            Console.WriteLine($"  {(wrote ? "写入命令已发" : "写入失败")}，耗时 {sw.ElapsedMilliseconds} ms");
            Thread.Sleep(400);
            Console.WriteLine();

            Console.WriteLine("【3】回读比对");
            var after = ReadBuffer(conn, 0, Total, Chunk);
            if (after == null) { Console.WriteLine("  回读失败。"); return; }
            Console.WriteLine($"  {Convert.ToHexString(after)}");
            bool same = before.SequenceEqual(after);
            Console.WriteLine(same
                ? "  √ 完全一致 —— 0x13 可安全用于批量写入"
                : "  ✗ 存在差异 —— 0x13 不可用，必须回退逐键 0x05");
            if (!same)
            {
                for (int i = 0; i < Total; i++)
                    if (before[i] != after[i])
                        Console.WriteLine($"    字节 {i,3} (矩阵键 {i / 2,3}): {before[i]:X2} → {after[i]:X2}");
            }
            Console.WriteLine();

            Console.WriteLine("【4】交叉验证 —— 逐键 0x04 抽查前 8 个键，与缓冲数据对照");
            int bad = 0;
            for (int i = 0; i < 8; i++)
            {
                byte row = (byte)(i / 16), col = (byte)(i % 16);
                ushort kc = ReadKc(conn, 0, row, col);
                ushort bufKc = (ushort)((after[i * 2] << 8) | after[i * 2 + 1]);
                bool ok = kc == bufKc;
                if (!ok) bad++;
                Console.WriteLine($"  ({row},{col})  0x04={kc:X4}   0x12={bufKc:X4}   {(ok ? "√" : "✗")}");
            }
            Console.WriteLine(bad == 0
                ? "  √ 缓冲读取与逐键读取结果一致，索引换算正确"
                : $"  ✗ 有 {bad} 处不一致，缓冲布局假设有误");
        }
    }

    /// <summary>
    /// 性能对比（实机）：改造后（0x12 批量 + 无排空）vs 改造前复现（逐键 0x04 + 每条命令 3×Read(15) 排空）。
    /// 所有测量都是真实耗时，不做估算。
    /// </summary>
    public static void RunPerfTest()
    {
        var dev = DeviceDiscovery.Discover();
        HidInterfaceInfo? live = null;
        foreach (var cand in dev.ViaCandidates)
        {
            if (cand.ProductId == 0x3002) continue;   // 2.4G 跳过
            var probe = new ViaClient(cand);
            if (!probe.Open()) { probe.Dispose(); continue; }
            bool ok = probe.Handshake(out _);
            probe.Dispose();
            if (ok) { live = cand; break; }
        }
        if (live == null)
        {
            Console.WriteLine("没有可用的有线 VIA 接口（PID 0x3005）。请插 USB 线。");
            return;
        }

        Console.WriteLine($"接口 MI={live.InterfaceNumber}  PID_{live.ProductId:X4}");
        Console.WriteLine();

        // ---------- 改造后：0x12 批量 ----------
        using (var via = new ViaClient(live))
        {
            via.Open();
            via.Handshake(out _);

            var sw = Stopwatch.StartNew();
            var data = ReadBufferViaClient(via, 0, 192);
            sw.Stop();
            long first = sw.ElapsedMilliseconds;
            Console.WriteLine($"【1】改造后 0x12 批量读 192 字节（7 次传输）: {first} ms   {(data == null ? "失败" : "成功")}");

            long best = long.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                sw.Restart();
                var d = ReadBufferViaClient(via, 0, 192);
                sw.Stop();
                if (d == null) { best = -1; break; }
                best = Math.Min(best, sw.ElapsedMilliseconds);
            }
            Console.WriteLine($"     再跑 3 次取最优: {(best < 0 ? "失败" : best + " ms")}");

            // ---------- 改造后：逐键 0x04 ----------
            sw.Restart();
            int okKeys = 0;
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 16; c++)
                    if (via.GetKeycode(0, (byte)r, (byte)c) != 0xFFFF) okKeys++;
            sw.Stop();
            Console.WriteLine($"【2】改造后 逐键 0x04 读 96 键: {sw.ElapsedMilliseconds} ms   成功 {okKeys}/96");

            // ---------- 层偏移验证：0x12(offset=192) 是否等于第 1 层 ----------
            var l1 = ReadBufferViaClient(via, 192, 192);
            if (l1 == null)
            {
                Console.WriteLine("【3】层偏移验证: 0x12 offset=192 读取失败");
            }
            else
            {
                int bad = 0;
                var diffs = new List<string>();
                for (int i = 0; i < 8; i++)
                {
                    ushort kc = via.GetKeycode(1, (byte)(i / 16), (byte)(i % 16));
                    ushort bufKc = (ushort)((l1[i * 2] << 8) | l1[i * 2 + 1]);
                    if (kc != bufKc) { bad++; diffs.Add($"(1,{i / 16},{i % 16}) 0x04={kc:X4} 0x12={bufKc:X4}"); }
                }
                Console.WriteLine($"【3】层偏移验证 0x12(offset=192) vs 0x04(layer=1) 前 8 键: "
                    + (bad == 0 ? "√ 一致，层偏移 = layer*192 正确" : $"✗ {bad} 处不一致 → {string.Join(", ", diffs)}"));
            }
        }
        Console.WriteLine();

        // ---------- 改造前复现：逐键 0x04 + 每条命令 3×Read(15) 排空 ----------
        using (var conn = new HidConnection(live))
        {
            if (!conn.Open())
            {
                Console.WriteLine("打开接口失败，跳过改造前复现。");
            }
            else
            {
                OldExchange(conn, 0x04, 0, 0, 0);   // 热身
                var sw = Stopwatch.StartNew();
                int ok = 0;
                for (int r = 0; r < 6; r++)
                    for (int c = 0; c < 16; c++)
                        if (OldExchange(conn, 0x04, 0, (byte)r, (byte)c) != null) ok++;
                sw.Stop();
                Console.WriteLine($"【4】改造前复现 逐键 0x04 + 3×Read(15) 排空 读 96 键: {sw.ElapsedMilliseconds} ms   成功 {ok}/96");
            }
        }
        Console.WriteLine();

        // ---------- DeviceSession 端到端（真实 UI 走的路径） ----------
        var session = new DeviceSession();
        var sw2 = Stopwatch.StartNew();
        session.Refresh();
        sw2.Stop();
        Console.WriteLine($"【5】DeviceSession.Refresh()（枚举+握手）: {sw2.ElapsedMilliseconds} ms  →  {session.StatusText}");
        if (!session.Ready)
        {
            Console.WriteLine($"     {session.DetailText}");
            session.Dispose();
            return;
        }

        sw2.Restart();
        var map = session.ReadLayer(0);
        sw2.Stop();
        Console.WriteLine($"【6】DeviceSession.ReadLayer(0) 端到端: {sw2.ElapsedMilliseconds} ms   {(map == null ? "失败" : $"成功 {map.Length} 键")}");

        sw2.Restart();
        var amap = session.ReadLayerAsync(0).GetAwaiter().GetResult();
        sw2.Stop();
        Console.WriteLine($"【7】DeviceSession.ReadLayerAsync(0): {sw2.ElapsedMilliseconds} ms   {(amap == null ? "失败" : $"成功 {amap.Length} 键")}");

        if (map != null)
        {
            // 写回刚读出的内容（内容不变，安全），顺便验证 0x13 批量路径
            sw2.Restart();
            bool w1 = session.WriteLayer(0, map);
            sw2.Stop();
            Console.WriteLine($"【8】WriteLayer(0) 首次（逐键回退，同时后台自检 0x13）: {sw2.ElapsedMilliseconds} ms   {(w1 ? "成功" : "失败")}");

            Thread.Sleep(1000);   // 等后台幂等自检完成

            sw2.Restart();
            bool w2 = session.WriteLayer(0, map);
            sw2.Stop();
            Console.WriteLine($"【9】WriteLayer(0) 再次（批量 0x13）: {sw2.ElapsedMilliseconds} ms   {(w2 ? "成功" : "失败")}");

            var back = session.ReadLayer(0);
            Console.WriteLine($"【10】写回后回读比对: {(back != null && back.SequenceEqual(map) ? "√ 完全一致" : "✗ 存在差异")}");
        }

        session.Dispose();
        Console.WriteLine();
        Console.WriteLine("性能测试结束。");
    }

    /// <summary>
    /// 2.4G 只读模式验证：打印候选接口、复现旧代码在 2.4G 接口上的卡顿、再跑改造后的 Refresh。
    /// 拔掉 USB 线（只剩 PID_3002）时运行本模式即可看到只读状态。
    /// </summary>
    public static void RunReadOnlyTest()
    {
        var dev = DeviceDiscovery.Discover();
        Console.WriteLine("VIA 候选接口（按优先级）:");
        foreach (var c in dev.ViaCandidates)
            Console.WriteLine($"  MI={c.InterfaceNumber}  PID_{c.ProductId:X4}");

        // 旧行为复现：对 2.4G 接口发一条命令，看要等多久（旧代码在这里反复重试 3 次）
        var r24 = dev.ViaCandidates.FirstOrDefault(c => c.ProductId == 0x3002);
        if (r24 != null)
        {
            var v = new ViaClient(r24) { WriteTimeoutMs = 500, WriteRetries = 3 };
            if (v.Open())
            {
                var sw = Stopwatch.StartNew();
                var resp = v.Cmd(ViaClient.CmdGetProtocolVersion);
                sw.Stop();
                Console.WriteLine($"\n[旧行为复现] 对 2.4G 接口发 1 条命令: {(resp == null ? "无响应" : "有响应")}，耗时 {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"             （旧 WarmUp+Handshake 要连发 7 条命令 → 约 {sw.ElapsedMilliseconds * 7 / 1000.0:F1} 秒）");
            }
            v.Dispose();
        }

        var s = new DeviceSession();
        var sw2 = Stopwatch.StartNew();
        s.Refresh();
        sw2.Stop();
        Console.WriteLine($"\n[改造后] DeviceSession.Refresh(): {sw2.ElapsedMilliseconds} ms");
        Console.WriteLine($"  State      = {s.State}");
        Console.WriteLine($"  Ready      = {s.Ready}");
        Console.WriteLine($"  StatusText = {s.StatusText}");
        Console.WriteLine($"  DetailText = {s.DetailText}");
        Console.WriteLine($"  ViaChannel = {(s.Device?.ViaChannel == null ? "<null>" : $"MI={s.Device.ViaChannel.InterfaceNumber} PID_{s.Device.ViaChannel.ProductId:X4}")}");
        s.Dispose();
    }

    /// <summary>用 ViaClient 的 0x12 分段读（走改造后的收发层）。</summary>
    private static byte[]? ReadBufferViaClient(ViaClient via, int offset, int total)
    {
        var buf = new byte[total];
        for (int off = 0; off < total; off += ViaClient.MaxPayload)
        {
            int n = Math.Min(ViaClient.MaxPayload, total - off);
            int abs = offset + off;
            var r = via.Cmd(ViaClient.CmdKeymapGetBuffer, (byte)(abs >> 8), (byte)(abs & 0xFF), (byte)n);
            if (r == null || r[0] != ViaClient.CmdKeymapGetBuffer) return null;
            Array.Copy(r, 4, buf, off, n);
        }
        return buf;
    }

    /// <summary>复现改造前 ViaClient.Cmd 的行为：每条命令前固定 3×Read(15) 排空。</summary>
    private static byte[]? OldExchange(HidConnection conn, byte cmd, params byte[] args)
    {
        var frame = new byte[Size];
        frame[0] = cmd;
        Array.Copy(args, 0, frame, 1, Math.Min(args.Length, Size - 1));
        for (int i = 0; i < 3; i++) if (conn.Read(15) == null) break;
        if (conn.Write(frame) < 0) return null;
        return conn.Read(600);
    }

    /// <summary>
    /// 转储指定层的完整键位（批量 0x12 读，按矩阵顺序打印）。
    /// 用于对照说明书核对厂商出厂烧录的 Fn 层。
    /// </summary>
    public static void DumpLayer(int layer)
    {
        var conn = OpenLive();
        if (conn == null) { Console.WriteLine("没有找到可用的 VIA 接口。请插 USB 线。"); return; }

        using (conn)
        {
            const int Chunk = Size - 4;   // 28 字节/帧
            const int Rows = 6, Cols = 16;
            const int Total = Rows * Cols * 2;

            var raw = ReadBuffer(conn, layer * Total, Total, Chunk);
            if (raw == null) { Console.WriteLine("读取失败。"); return; }

            Console.WriteLine($"层 {layer}（缓冲偏移 {layer * Total}）:");
            for (int r = 0; r < Rows; r++)
            {
                var sb = new StringBuilder($"  row{r,2}: ");
                for (int c = 0; c < Cols; c++)
                {
                    int i = r * Cols + c;
                    sb.Append($"{(ushort)((raw[i * 2] << 8) | raw[i * 2 + 1]):X4} ");
                }
                Console.WriteLine(sb.ToString().TrimEnd());
            }
            Console.WriteLine();

            int nonEmpty = 0;
            for (int i = 0; i < Rows * Cols; i++)
            {
                ushort kc = (ushort)((raw[i * 2] << 8) | raw[i * 2 + 1]);
                if (kc != 0x0000 && kc != 0x0001) nonEmpty++;
            }
            Console.WriteLine($"非空（既非 0x0000 也非 0x0001=透明）的键位: {nonEmpty} / {Rows * Cols}");
        }
    }

    /// <summary>
    /// 偏移量语义验证（只读，安全）。
    /// 目的：确认 0x12 的 offset 参数是真的被固件使用，还是被忽略（永远返回基址数据）。
    /// 这直接决定能不能按 layer*192 写第 2/3/4 层 —— 若 offset 被忽略，写第 2 层会覆盖第 1 层。
    /// </summary>
    public static void RunOffsetTest()
    {
        var conn = OpenLive();
        if (conn == null) { Console.WriteLine("没有找到可用的 VIA 接口。请插 USB 线。"); return; }

        using (conn)
        {
            const int Chunk = Size - 4;

            var full = ReadBuffer(conn, 0, 192, Chunk);
            if (full == null) { Console.WriteLine("offset=0 读取失败。"); return; }
            Console.WriteLine($"基准 offset=0 读 192 字节：");
            Console.WriteLine($"  {Convert.ToHexString(full)}");
            Console.WriteLine();

            Console.WriteLine("【A】offset 是否真的生效？比较 offset=96 与基准的第 96..123 字节");
            var at96 = ReadBuffer(conn, 96, Chunk, Chunk);
            if (at96 == null) { Console.WriteLine("  读取失败"); return; }
            var slice96 = full[96..(96 + Chunk)];
            bool honored = at96.SequenceEqual(slice96);
            Console.WriteLine($"  offset=96 读回 : {Convert.ToHexString(at96)}");
            Console.WriteLine($"  基准[96..124]  : {Convert.ToHexString(slice96)}");
            Console.WriteLine(honored
                ? "  √ 一致 —— offset 参数被固件正确使用"
                : "  ✗ 不一致 —— offset 可能被忽略！");
            Console.WriteLine();

            Console.WriteLine("【B】offset=192（第 2 层起始）返回的是什么？");
            var at192 = ReadBuffer(conn, 192, Chunk, Chunk);
            if (at192 == null) { Console.WriteLine("  读取失败"); return; }
            Console.WriteLine($"  offset=192 读回 : {Convert.ToHexString(at192)}");
            Console.WriteLine($"  基准[0..28]     : {Convert.ToHexString(full[..Chunk])}");
            bool sameAsBase = at192.SequenceEqual(full[..Chunk]);
            Console.WriteLine(sameAsBase
                ? "  ⚠ 与第 1 层开头完全相同 —— 第 2 层未被单独初始化（或 offset 被忽略）"
                : "  √ 与第 1 层不同 —— 层数据独立");
            Console.WriteLine();

            Console.WriteLine("【C】逐键 0x04 对比 layer=0 与 layer=1 的前 8 键");
            bool anyDiff = false;
            for (int i = 0; i < 8; i++)
            {
                byte row = (byte)(i / 16), col = (byte)(i % 16);
                ushort k0 = ReadKc(conn, 0, row, col);
                ushort k1 = ReadKc(conn, 1, row, col);
                if (k0 != k1) anyDiff = true;
                Console.WriteLine($"  ({row},{col})  L0={k0:X4}  L1={k1:X4}  {(k0 == k1 ? "相同" : "不同")}");
            }
            Console.WriteLine(anyDiff
                ? "  √ 存在差异 —— 固件支持多层"
                : "  ✗ 完全一致 —— 固件忽略 layer 参数，VIA 层接口读不到 Fn 层");
            Console.WriteLine();
            Console.WriteLine("结论：A 一致 → offset 生效；C 有差异 → 固件支持多层，Fn 层可通过 0x04 读取。");
        }
    }

    /// <summary>
    /// 宏系统探针（**只读**，不写任何东西）。
    /// 目的：确认厂商固件是否真的实现了 VIA 的宏缓冲命令 —— 0x0C 报了 16 个宏、
    /// 0x0D 报了 338 字节，但那只是"声明"，0x0E 能不能真读出数据才是能不能做宏编辑器的前提。
    /// 判据：响应首字节是 0x0E 且第 4 字节（长度回显）等于请求长度，且数据不是请求原样回显。
    /// </summary>
    public static void RunMacroTest()
    {
        var conn = OpenLive();
        if (conn == null) { Console.WriteLine("没有找到可用的 VIA 接口。请插 USB 线。"); return; }

        using (conn)
        {
            var r = Exchange(conn, 0x0C);
            int count = r != null && r[0] == 0x0C ? r[1] : -1;
            r = Exchange(conn, 0x0D);
            int size = r != null && r[0] == 0x0D ? (r[1] << 8) | r[2] : -1;

            Console.WriteLine("【声明值】");
            Console.WriteLine($"  0x0C 宏数量     = {count}");
            Console.WriteLine($"  0x0D 缓冲字节数 = {size}");
            Console.WriteLine();

            if (size <= 0)
            {
                Console.WriteLine("固件没报告宏缓冲大小，宏功能不可用。");
                return;
            }

            Console.WriteLine("【0x0E 读宏缓冲】请求 = [0E][off_hi][off_lo][size]");
            int total = Math.Min(size, 338);
            var buf = new byte[total];
            int ok = 0, echo = 0, bad = 0;

            for (int off = 0; off < total; off += 28)
            {
                int n = Math.Min(28, total - off);
                var resp = Exchange(conn, 0x0E, (byte)(off >> 8), (byte)(off & 0xFF), (byte)n);
                if (resp == null) { bad++; Console.WriteLine($"  off={off,3} → 无响应"); continue; }

                // 请求原样回显的特征：第 4 字节起的载荷与请求字节完全相同
                bool looksEcho = true;
                for (int i = 0; i < n; i++)
                    if (resp[4 + i] != 0) { looksEcho = false; break; }

                if (resp[0] != 0x0E) { bad++; Console.WriteLine($"  off={off,3} → 首字节 0x{resp[0]:X2}（异常）"); continue; }
                if (looksEcho) echo++; else ok++;

                Array.Copy(resp, 4, buf, off, n);
                Console.WriteLine($"  off={off,3} size={n,2} → {Convert.ToHexString(resp[4..(4 + Math.Min(n, 12))])}{(looksEcho ? "   ← 全 0，疑似未实现" : "")}");
            }

            Console.WriteLine();
            Console.WriteLine($"有数据段 {ok} / 全 0 段 {echo} / 异常段 {bad}");
            Console.WriteLine();
            Console.WriteLine("完整缓冲：");
            Console.WriteLine("  " + Convert.ToHexString(buf));
            Console.WriteLine();
            Console.WriteLine("【实现与否的判别】");
            Console.WriteLine("  在请求第 5 字节塞哨兵 A5 5A，看它会不会被原样带回 ——");
            Console.WriteLine("  固件若把请求原样回显，说明没实现；若真去读缓冲，该位置应是缓冲内容。");
            Console.WriteLine("  先用已知可用的 0x12 做对照，验证这个判别法本身有效。");
            bool k12 = ProbeImplemented(conn, 0x12, "0x12 键位缓冲（对照组，已知可用）");
            bool k0E = ProbeImplemented(conn, 0x0E, "0x0E 宏缓冲  （待判定）        ");
            Console.WriteLine();

            if (k12 && k0E)
                Console.WriteLine("√ 0x0E 是真实实现 —— 缓冲区全 0 只是因为出厂没定义过宏，宏功能可以做");
            else if (k12 && !k0E)
                Console.WriteLine("✗ 0x0E 只是原样回显 —— 厂商固件没实现宏缓冲，宏功能在这块固件上做不了");
            else
                Console.WriteLine("⚠ 对照组 0x12 也没通过，判别法本身失效，结论不可信");

            Console.WriteLine();
            Console.WriteLine(ok > 0
                ? "缓冲里有非 0 数据。"
                : "（缓冲区当前全 0）");
        }
    }

    /// <summary>
    /// 用哨兵法判别某条"读缓冲"命令是否真的被固件实现。
    /// 返回 true = 真实实现，false = 原样回显（未实现）。
    /// </summary>
    private static bool ProbeImplemented(HidConnection conn, byte cmd, string label)
    {
        var req = new byte[Size];
        req[0] = cmd;
        req[1] = 0x00; req[2] = 0x00; req[3] = 0x04;   // 请求读 4 字节
        req[4] = 0xA5; req[5] = 0x5A;                  // 哨兵：正规实现不该把它带回来
        var resp = ExchangeRaw(conn, req);
        if (resp == null) { Console.WriteLine($"  {label}: 无响应"); return false; }

        bool echo = resp[4] == 0xA5 && resp[5] == 0x5A;
        Console.WriteLine($"  {label}: 首字节 0x{resp[0]:X2}  载荷 {Convert.ToHexString(resp[4..8])}  → {(echo ? "原样回显 = 未实现" : "真实数据 = 已实现")}");
        return !echo;
    }

    /// <summary>用 0x12 分段读出键位缓冲。失败返回 null。</summary>
    private static byte[]? ReadBuffer(HidConnection conn, int offset, int total, int chunk)
    {
        var buf = new byte[total];
        for (int off = 0; off < total; off += chunk)
        {
            int n = Math.Min(chunk, total - off);
            int abs = offset + off;   // 必须用绝对偏移，否则每段都会从 0 读
            var r = Exchange(conn, 0x12, (byte)(abs >> 8), (byte)(abs & 0xFF), (byte)n);
            if (r == null || r[0] != 0x12) return null;
            Array.Copy(r, 4, buf, off, n);
        }
        return buf;
    }

    /// <summary>用 0x13 分段写入键位缓冲。</summary>
    private static bool WriteBuffer(HidConnection conn, int offset, byte[] data, int chunk)
    {
        for (int off = 0; off < data.Length; off += chunk)
        {
            int n = Math.Min(chunk, data.Length - off);
            int abs = offset + off;   // 同上：必须用绝对偏移
            var payload = new byte[Size];
            payload[0] = 0x13;
            payload[1] = (byte)(abs >> 8);
            payload[2] = (byte)(abs & 0xFF);
            payload[3] = (byte)n;
            Array.Copy(data, off, payload, 4, n);
            var r = ExchangeRaw(conn, payload);
            if (r == null || r[0] != 0x13) return false;
        }
        return true;
    }

    /// <summary>
    /// 通道 2 (Logo) 与通道 4 (侧边灯) 的效果值 6 (shutdown) 幂等测试（读原值 → 写 6 → 回读 → 写回原值）。
    /// </summary>
    public static void RunLightShutdownTest()
    {
        var conn = OpenLive();
        if (conn == null) { Console.WriteLine("没有找到可用的 VIA 接口。请插 USB 线。"); return; }

        using (conn)
        {
            foreach (byte ch in new byte[] { 2, 4 })
            {
                int orig = ReadLight(conn, ch, 2);
                Console.WriteLine($"通道 {ch} 原效果: {orig}");
                if (orig < 0) continue;

                WriteLight(conn, ch, 2, 6);
                Thread.Sleep(200);
                int after = ReadLight(conn, ch, 2);
                Console.WriteLine($"  写 6 (shutdown) 后回读: {after}  {(after == 6 ? "√ 固件接受 6" : "✗ 固件忽略/改写")}");

                WriteLight(conn, ch, 2, (ushort)orig);
                Thread.Sleep(200);
                int restored = ReadLight(conn, ch, 2);
                Console.WriteLine($"  复原为 {orig} 后回读: {restored}  {(restored == orig ? "√ 已复原" : "✗ 未复原")}");
            }
        }
    }

    private static HidConnection? OpenLive()
    {
        var dev = DeviceDiscovery.Discover();
        foreach (var cand in dev.ViaCandidates)
        {
            var c = new HidConnection(cand);
            if (!c.Open()) { c.Dispose(); continue; }
            var r = Exchange(c, 0x0C, 0);
            if (r != null && r[0] == 0x0C) return c;
            c.Dispose();
        }
        return null;
    }

    private static void Dump(HidConnection conn, string label, params byte[] payload)
    {
        var r = ExchangeRaw(conn, payload);
        Console.WriteLine($"      {label,-22} →  {(r == null ? "<无响应>" : Convert.ToHexString(r[..8]))}");
    }

    private static string Hex(byte[]? b) => b == null ? "<无响应>" : Convert.ToHexString(b[..8]);

    private static ushort ReadKc(HidConnection conn, byte layer, byte row, byte col)
    {
        var r = Exchange(conn, 0x04, layer, row, col);
        return r == null ? (ushort)0xFFFF : (ushort)((r[4] << 8) | r[5]);
    }

    private static bool WriteKc(HidConnection conn, byte layer, byte row, byte col, ushort kc)
    {
        var r = Exchange(conn, 0x05, layer, row, col, (byte)(kc >> 8), (byte)(kc & 0xFF));
        return r != null && r[0] == 0x05;
    }

    private static int ReadLight(HidConnection conn, byte ch, byte id)
    {
        var r = Exchange(conn, 0x08, ch, id);
        if (r == null || r[0] == 0xFF) return -1;
        return (r[4] << 8) | r[3];
    }

    private static void WriteLight(HidConnection conn, byte ch, byte id, ushort v)
    {
        Exchange(conn, 0x07, ch, id, (byte)(v & 0xFF), (byte)(v >> 8));
    }

    /// <summary>发一条命令并返回完整 32 字节响应（不清零尾部，便于看回显）。</summary>
    private static byte[]? ExchangeRaw(HidConnection conn, byte[] payload)
    {
        var frame = new byte[Size];
        Array.Copy(payload, frame, Math.Min(payload.Length, Size));
        for (int k = 0; k < 4; k++) if (conn.Read(20) == null) break;
        if (conn.Write(frame) < 0) return null;
        return conn.Read(600);
    }

    private static byte[]? Exchange(HidConnection conn, byte cmd, params byte[] args)
    {
        var payload = new byte[Size];
        payload[0] = cmd;
        Array.Copy(args, 0, payload, 1, Math.Min(args.Length, Size - 1));
        return ExchangeRaw(conn, payload);
    }
}
