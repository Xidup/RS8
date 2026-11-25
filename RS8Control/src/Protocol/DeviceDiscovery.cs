using RS8Control.Hid;

namespace RS8Control.Protocol;

/// <summary>已发现的设备通道集合。</summary>
public sealed class DeviceSet
{
    /// <summary>VIA Raw HID 通道（Usage Page 0xFF60 / Usage 0x61）——公开协议，读写键位与灯效。</summary>
    public HidInterfaceInfo? ViaChannel { get; set; }

    /// <summary>所有候选 VIA 通道，按优先级排序（有线优先）。</summary>
    public List<HidInterfaceInfo> ViaCandidates { get; } = new();

    /// <summary>厂商私有通道（Usage Page 0xFF00，64 字节双向）——官方驱动用它，尚未完全逆向。</summary>
    public HidInterfaceInfo? PrivateChannel { get; set; }

    /// <summary>其余同 VID 的接口（键盘 boot、消费者控制等）。</summary>
    public List<HidInterfaceInfo> Others { get; } = new();

    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string SerialNumber { get; set; } = "";

    public bool Any => ViaChannel != null || PrivateChannel != null;
    public bool ViaAvailable => ViaChannel != null;
    public bool PrivateAvailable => PrivateChannel != null;
}

public static class DeviceDiscovery
{
    public const ushort KnownVid = 0x36B0;
    public const ushort ViaUsagePage = 0xFF60;
    public const ushort ViaUsage = 0x61;

    /// <summary>
    /// 找出所有属于 RS8（VID 0x36B0）的 VIA 通道接口，按优先级排序：
    /// 有线（PID 0x3005）优先于 2.4G 接收器（PID 0x3002），其余同 VID 接口排最后。
    /// 严格按 KnownVid 过滤，避免误连并误写用户电脑上的其它 QMK/VIA 键盘。
    /// </summary>
    public static List<HidInterfaceInfo> FindViaCandidates() => FindViaCandidates(Win32Hid.Enumerate());

    private static List<HidInterfaceInfo> FindViaCandidates(IReadOnlyList<HidInterfaceInfo> all)
    {
        return all
            .Where(a => a.VendorId == KnownVid && a.UsagePage == ViaUsagePage && a.Usage == ViaUsage)
            .OrderBy(a => Priority(a))
            .ThenBy(a => a.ProductId)
            .ToList();
    }

    private static int Priority(HidInterfaceInfo a)
    {
        if (a.VendorId == KnownVid && a.ProductId == 0x3005) return 0;   // 有线
        if (a.VendorId == KnownVid && a.ProductId == 0x3002) return 1;   // 2.4G 接收器
        return 2;
    }

    /// <summary>
    /// 发现 RS8 设备（只做一次 HID 枚举，避免重复开销与引用不匹配）。
    /// 严格限定 VID 0x36B0，私有通道优先与主 VIA 通道同 PID。
    /// </summary>
    public static DeviceSet Discover()
    {
        var all = Win32Hid.Enumerate();
        var result = new DeviceSet();

        var vendorDevices = all.Where(a => a.VendorId == KnownVid).ToList();

        // VIA 通道：Usage Page 0xFF60 / Usage 0x61，有线优先
        result.ViaCandidates.AddRange(FindViaCandidates(vendorDevices));
        result.ViaChannel = result.ViaCandidates.FirstOrDefault();

        // 私有通道：优先匹配与 ViaChannel 同 PID 的接口，避免把有线与 2.4G 接收器的通道混拼
        ushort? viaPid = result.ViaChannel?.ProductId;
        var privPool = viaPid.HasValue
            ? vendorDevices.Where(a => a.ProductId == viaPid.Value).ToList()
            : vendorDevices;
        result.PrivateChannel = FindPrivateChannel(privPool) ?? FindPrivateChannel(vendorDevices);

        var anchor = result.ViaChannel ?? result.PrivateChannel ?? vendorDevices.FirstOrDefault();
        if (anchor != null)
        {
            result.VendorId = anchor.VendorId;
            result.ProductId = anchor.ProductId;
            result.ProductName = anchor.Product;
            result.Manufacturer = anchor.Manufacturer;
            result.SerialNumber = anchor.SerialNumber;
        }

        string? viaPath = result.ViaChannel?.DevicePath;
        string? privPath = result.PrivateChannel?.DevicePath;
        foreach (var a in vendorDevices)
        {
            if (viaPath != null && string.Equals(a.DevicePath, viaPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (privPath != null && string.Equals(a.DevicePath, privPath, StringComparison.OrdinalIgnoreCase)) continue;
            result.Others.Add(a);
        }

        return result;
    }

    private static HidInterfaceInfo? FindPrivateChannel(IReadOnlyList<HidInterfaceInfo> pool) =>
        pool.FirstOrDefault(a => a.UsagePage == 0xFF00 && a.OutputReportByteLength >= 64)
        ?? pool.FirstOrDefault(a => a.OutputReportByteLength >= 64 && a.InputReportByteLength >= 64 && a.UsagePage >= 0xFF00);

    /// <summary>把当前设备信息格式化成一行人类可读的状态。</summary>
    public static string Describe(DeviceSet d)
    {
        if (!d.Any && d.Others.Count == 0) return "未检测到 RS8 设备";
        var parts = new List<string>();
        parts.Add($"VID_{d.VendorId:X4}/PID_{d.ProductId:X4}");
        if (!string.IsNullOrWhiteSpace(d.ProductName)) parts.Add($"\"{d.ProductName.Trim()}\"");
        parts.Add(d.ViaAvailable ? "VIA✓" : "VIA✗");
        parts.Add(d.PrivateAvailable ? "私有✓" : "私有✗");
        return string.Join("  ", parts);
    }
}
