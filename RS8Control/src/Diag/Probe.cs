using System.Text;
using RS8Control.Hid;
using RS8Control.Protocol;

namespace RS8Control.Diag;

/// <summary>
/// 设备诊断：把"看不到的东西"全部导出成文本。
/// </summary>
public static class Probe
{
    public static string Run()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("================================================================");
        L("  RS8Control 设备诊断报告");
        L("================================================================");
        L($"生成时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"操作系统      : {Environment.OSVersion}");
        L($"进程架构      : {(Environment.Is64BitProcess ? "x64" : "x86")}");
        L($"运行时        : {Environment.Version}");
        L($"计算机名      : {Environment.MachineName}");
        L();

        // ---------------- 1. 全部 HID 接口 ----------------
        L("----------------------------------------------------------------");
        L("[1] 系统全部 HID 接口（按 VID/PID 分组）");
        L("----------------------------------------------------------------");
        List<HidInterfaceInfo> all;
        try
        {
            all = Win32Hid.Enumerate();
        }
        catch (Exception ex)
        {
            L($"枚举失败: {ex}");
            return sb.ToString();
        }

        L($"共发现 {all.Count} 个 HID 接口。");
        L();
        foreach (var g in all.GroupBy(a => (a.VendorId, a.ProductId)).OrderBy(g => g.Key.VendorId))
        {
            var f = g.First();
            L($"  VID_{g.Key.VendorId:X4} PID_{g.Key.ProductId:X4}   接口数 {g.Count()}   \"{f.Product.Trim()}\"  [{f.Manufacturer.Trim()}]");
            foreach (var a in g)
            {
                string mark = a.UsagePage == DeviceDiscovery.ViaUsagePage && a.Usage == DeviceDiscovery.ViaUsage
                    ? "  <== VIA Raw HID"
                    : (a.UsagePage == 0xFF00 && a.OutputReportByteLength >= 64 ? "  <== 私有通道" : "");
                L($"      MI={a.InterfaceNumber,-3} UP=0x{a.UsagePage:X4} U=0x{a.Usage:X4}  IN={a.InputReportByteLength,-4} OUT={a.OutputReportByteLength,-4} FEAT={a.FeatureReportByteLength,-4}{mark}");
            }
        }
        L();

        // ---------------- 2. 目标设备 ----------------
        L("----------------------------------------------------------------");
        L("[2] 目标设备发现结果");
        L("----------------------------------------------------------------");
        var dev = DeviceDiscovery.Discover();
        L($"VID/PID        : VID_{dev.VendorId:X4} / PID_{dev.ProductId:X4}");
        L($"产品名         : \"{dev.ProductName.Trim()}\"");
        L($"制造商         : \"{dev.Manufacturer.Trim()}\"");
        L($"序列号         : \"{dev.SerialNumber.Trim()}\"");
        L($"VIA 通道       : {(dev.ViaChannel == null ? "未找到" : $"MI={dev.ViaChannel.InterfaceNumber} IN={dev.ViaChannel.InputReportByteLength} OUT={dev.ViaChannel.OutputReportByteLength}")}");
        L($"私有通道       : {(dev.PrivateChannel == null ? "未找到" : $"MI={dev.PrivateChannel.InterfaceNumber} IN={dev.PrivateChannel.InputReportByteLength} OUT={dev.PrivateChannel.OutputReportByteLength}")}");
        L($"其他接口       : {dev.Others.Count} 个");
        var anchor = dev.ViaChannel ?? dev.PrivateChannel;
        if (anchor != null)
        {
            L();
            L("字符串描述符原始字节:");
            L(Win32Hid.DumpStringDescriptors(anchor.DevicePath));
        }
        L();

        if (!dev.Any)
        {
            L("!! 没有可用的控制通道。");
            L("!! 请确认键盘已连接（USB 线或 2.4G 接收器），并且官方驱动没有独占设备。");
            return sb.ToString();
        }

        // ---------------- 3. 报告描述符 ----------------
        foreach (var (label, iface) in new[] { ("VIA 通道", dev.ViaChannel), ("私有通道", dev.PrivateChannel) })
        {
            if (iface == null) continue;
            L("----------------------------------------------------------------");
            L($"[3] {label} 报告描述符（MI={iface.InterfaceNumber}）");
            L("----------------------------------------------------------------");
            var desc = Win32Hid.GetReportDescriptor(iface.DevicePath);
            if (desc.Length == 0)
            {
                L("  读取失败（IOCTL 被拒绝）");
            }
            else
            {
                var parsed = HidReportDescriptorInfo.Parse(desc);
                L($"  {desc.Length} 字节，{parsed.Collections.Count} 个集合:");
                foreach (var col in parsed.Collections) L($"     - {col}");
                L("  原始字节:");
                L(HidReportDescriptorInfo.HexDump(desc).TrimEnd());
            }
            L();
        }

        // ---------------- 4. VIA 握手与能力探测 ----------------
        L("----------------------------------------------------------------");
        L("[4] VIA 协议实测");
        L("----------------------------------------------------------------");
        if (dev.ViaChannel == null)
        {
            L("没有 VIA 通道，跳过。");
        }
        else
        {
            L($"找到接口 MI={dev.ViaChannel.InterfaceNumber}，报告长度 IN={dev.ViaChannel.InputReportByteLength} OUT={dev.ViaChannel.OutputReportByteLength}");

            // 裸探测：原样打出请求与响应字节，用于判断"回显 / 私有协议 / 真 VIA"
            using (var raw = new HidConnection(dev.ViaChannel))
            {
                if (!raw.Open())
                {
                    L($"  裸探测: 打开失败 (Win32 {raw.LastWin32Error})");
                }
                else
                {
                    int outLen = Math.Max(1, (int)dev.ViaChannel.OutputReportByteLength - 1);
                    var req = new byte[outLen];
                    req[0] = ViaClient.CmdGetProtocolVersion;
                    for (int k = 0; k < 4; k++) if (raw.Read(20) == null) break;
                    int n = raw.Write(req);
                    L($"  裸探测: 可写={raw.OpenedForWrite}  写入={(n > 0 ? $"{n} 字节" : "失败")} (Win32 {raw.LastWin32Error})");
                    L($"  请求字节: {Convert.ToHexString(req)}");
                    if (n > 0)
                    {
                        var resp = raw.Read(800);
                        L($"  原始响应: {(resp == null ? "<无响应>" : Convert.ToHexString(resp))}");
                        if (resp != null && resp.Length == req.Length && resp.SequenceEqual(req))
                            L("  → 响应与请求完全相同：该接口疑似回显，或固件未实现 VIA 命令");
                    }
                }
            }
            L();

            using var client = new ViaClient(dev.ViaChannel);
            if (!client.Open())
            {
                L($"!! 打开失败: {client.LastError}");
            }
            else if (client.Handshake(out string msg))
            {
                L($"√ 握手成功: {msg}");
                L($"    层数            : {client.GetLayerCount()}");
                L($"    固件版本        : 0x{client.GetFirmwareVersion():X4}");
                L($"    运行时间        : {client.GetUptime() / 60000.0:F1} 分钟");
                L($"    Layout Options  : {client.GetKeyboardValue(ViaClient.KvLayoutOptions)}");
                L($"    宏数量          : {client.GetMacroCount()}");
                L($"    宏缓冲大小      : {client.GetMacroBufferSize()} 字节");
                L();
                L("    键位抽查（层 0，前 8 个矩阵位）:");
                var row = new List<string>();
                for (byte r = 0; r < 1; r++)
                    for (byte c = 0; c < 8; c++)
                    {
                        ushort kc = client.GetKeycode(0, r, c);
                        row.Add($"[{r},{c}]=0x{kc:X4}");
                    }
                L("      " + string.Join("  ", row));
                L();
                L("    灯效通道读回（通道 1-5 × 值 1-5）:");
                for (byte ch = 1; ch <= 5; ch++)
                {
                    var vals = new List<string>();
                    for (byte id = 1; id <= 5; id++) vals.Add($"{id}:{client.GetCustomValue(ch, id)}");
                    L($"      通道 {ch}: {string.Join("  ", vals)}");
                }
            }
            else
            {
                L($"!! 握手失败: {msg}");
                L("!! 接口存在但未按 VIA 协议响应。");
            }
        }
        L();

        // ---------------- 5. 私有通道被动监听 ----------------
        L("----------------------------------------------------------------");
        L("[5] 私有通道被动监听（2 秒，不发送任何数据）");
        L("----------------------------------------------------------------");
        if (dev.PrivateChannel == null)
        {
            L("没有私有通道，跳过。");
        }
        else
        {
            using var conn = new HidConnection(dev.PrivateChannel);
            if (!conn.Open())
            {
                L($"!! 打开失败（可能被官方驱动独占）: {dev.PrivateChannel.DevicePath}");
            }
            else
            {
                L($"  已打开 MI={dev.PrivateChannel.InterfaceNumber}");
                var deadline = DateTime.UtcNow.AddSeconds(2);
                int got = 0;
                while (DateTime.UtcNow < deadline && got < 10)
                {
                    var data = conn.Read(300);
                    if (data == null) continue;
                    got++;
                    L($"    收到 #{got} ({data.Length}B): {Convert.ToHexString(data)}");
                }
                if (got == 0) L("    2 秒内无数据（键盘空闲时不主动上报，属正常）");
            }
        }
        L();

        L("================================================================");
        L("  报告结束");
        L("================================================================");
        return sb.ToString();
    }
}
