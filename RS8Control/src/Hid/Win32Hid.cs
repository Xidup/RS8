using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RS8Control.Hid;

/// <summary>一个 HID 接口（对应复合设备的一个 collection）的静态信息。</summary>
public sealed class HidInterfaceInfo
{
    public string DevicePath { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort VersionNumber { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public ushort InputReportByteLength { get; init; }
    public ushort OutputReportByteLength { get; init; }
    public ushort FeatureReportByteLength { get; init; }
    public ushort NumberLinkCollectionNodes { get; init; }

    /// <summary>从设备路径里解析出的接口号（\\?\hid#vid_xxxx&amp;pid_xxxx&amp;mi_03#… → 3）。没有则 -1。</summary>
    public int InterfaceNumber
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(DevicePath, @"mi_(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
    }

    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";

    public override string ToString() =>
        $"{VidPid} MI={InterfaceNumber} UP=0x{UsagePage:X4} U=0x{Usage:X4} IN={InputReportByteLength} OUT={OutputReportByteLength} FEAT={FeatureReportByteLength}";
}

/// <summary>
/// 基于 Windows SetupAPI + hid.dll 的 HID 访问层。
/// 不使用 hidapi，避免额外 DLL 依赖；不使用 WMI，避免慢。
/// </summary>
public static class Win32Hid
{
    // ---------- P/Invoke ----------
    private static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int numberOfBytesToRead,
        out int numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int numberOfBytesToWrite,
        out int numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint ioControlCode,
        byte[]? inBuffer, int inBufferSize, byte[]? outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    // IOCTL_HID_GET_REPORT_DESCRIPTOR = CTL_CODE(FILE_DEVICE_KEYBOARD, 1, METHOD_NEITHER, FILE_ANY_ACCESS)
    private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0007;
    // 部分驱动/文档使用这个值，作为兜底
    private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR_ALT = 0x000B0190;

    // ---------- 枚举 ----------

    /// <summary>枚举系统上所有 HID 接口。vid/pid 为 null 时返回全部。</summary>
    public static List<HidInterfaceInfo> Enumerate(ushort? vid = null, ushort? pid = null)
    {
        var result = new List<HidInterfaceInfo>();
        var guid = GUID_DEVINTERFACE_HID;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

            for (uint i = 0; ; i++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did))
                {
                    if (Marshal.GetLastWin32Error() == 259 /*ERROR_NO_MORE_ITEMS*/) break;
                    break;
                }

                string? path = GetDevicePath(set, ref did);
                if (path == null) continue;

                var info = TryQuery(path, vid, pid);
                if (info != null) result.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        result.Sort((a, b) =>
        {
            int c = a.InterfaceNumber.CompareTo(b.InterfaceNumber);
            if (c != 0) return c;
            c = a.UsagePage.CompareTo(b.UsagePage);
            return c != 0 ? c : a.Usage.CompareTo(b.Usage);
        });
        return result;
    }

    private static string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0) return null;

        IntPtr buf = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: x64 为 8，x86 为 6
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, required, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static HidInterfaceInfo? TryQuery(string path, ushort? vid, ushort? pid)
    {
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attrs)) return null;

        if (vid.HasValue && attrs.VendorID != vid.Value) return null;
        if (pid.HasValue && attrs.ProductID != pid.Value) return null;

        ushort up = 0, u = 0, inLen = 0, outLen = 0, featLen = 0, nodes = 0;
        if (HidD_GetPreparsedData(h, out IntPtr ppd))
        {
            try
            {
                if (HidP_GetCaps(ppd, out var caps) == 0x00110000 /*HIDP_STATUS_SUCCESS*/)
                {
                    up = caps.UsagePage; u = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                    nodes = caps.NumberLinkCollectionNodes;
                }
            }
            finally { HidD_FreePreparsedData(ppd); }
        }

        return new HidInterfaceInfo
        {
            DevicePath = path,
            VendorId = attrs.VendorID,
            ProductId = attrs.ProductID,
            VersionNumber = attrs.VersionNumber,
            Manufacturer = ReadString(h, 1),
            Product = ReadString(h, 2),
            SerialNumber = ReadString(h, 3),
            UsagePage = up,
            Usage = u,
            InputReportByteLength = inLen,
            OutputReportByteLength = outLen,
            FeatureReportByteLength = featLen,
            NumberLinkCollectionNodes = nodes,
        };
    }

    private static string ReadString(SafeFileHandle h, int which)
    {
        var raw = ReadStringRaw(h, which);
        if (raw == null) return "";
        // 实测：HidD_Get*String 直接把 UTF-16LE 字符串写在缓冲起始处，没有长度头
        int end = 0;
        while (end + 1 < raw.Length && !(raw[end] == 0 && raw[end + 1] == 0)) end += 2;
        if (end == 0) return "";
        return Encoding.Unicode.GetString(raw, 0, end);
    }

    /// <summary>取出 HidD_Get*String 的原始缓冲，供诊断用。</summary>
    public static byte[]? ReadStringRaw(SafeFileHandle h, int which)
    {
        var buf = new byte[512];
        bool ok = which switch
        {
            1 => HidD_GetManufacturerString(h, buf, buf.Length),
            2 => HidD_GetProductString(h, buf, buf.Length),
            _ => HidD_GetSerialNumberString(h, buf, buf.Length),
        };
        return ok ? buf : null;
    }

    /// <summary>诊断：转储某个 HID 设备的三条字符串描述符原始字节。</summary>
    public static string DumpStringDescriptors(string devicePath)
    {
        using var h = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return "  打开失败";
        var sb = new StringBuilder();
        foreach (var (name, which) in new[] { ("Manufacturer", 1), ("Product", 2), ("Serial", 3) })
        {
            var raw = ReadStringRaw(h, which);
            if (raw == null) { sb.AppendLine($"  {name}: 读取失败"); continue; }
            sb.AppendLine($"  {name}: 前 48 字节");
            sb.AppendLine("    " + Convert.ToHexString(raw[..48]));
            sb.AppendLine($"    → \"{ReadString(h, which)}\"");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>读取 HID 报告描述符原始字节。失败返回空数组。</summary>
    public static byte[] GetReportDescriptor(string devicePath)
    {
        using var h = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return Array.Empty<byte>();

        var buf = new byte[4096];
        foreach (uint code in new[] { IOCTL_HID_GET_REPORT_DESCRIPTOR, IOCTL_HID_GET_REPORT_DESCRIPTOR_ALT })
        {
            if (DeviceIoControl(h, code, null, 0, buf, buf.Length, out int returned, IntPtr.Zero) && returned > 0)
                return buf[..returned];
        }
        return Array.Empty<byte>();
    }

    /// <summary>
    /// 在注册表的 HID 枚举树里递归查找报告描述符。
    /// 键名格式随 Windows 版本/多集合设备而变，所以按前缀匹配 + 递归搜索最稳。
    /// </summary>
    public static List<(string KeyPath, byte[] Descriptor)> FindReportDescriptors(ushort vid, ushort pid)
    {
        var results = new List<(string, byte[])>();
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (root == null) return results;
            string prefix = $"VID_{vid:X4}&PID_{pid:X4}";
            foreach (var devName in root.GetSubKeyNames())
            {
                if (!devName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                using var devKey = root.OpenSubKey(devName);
                if (devKey == null) continue;
                foreach (var instName in devKey.GetSubKeyNames())
                    WalkForDescriptors(devKey, instName, devName + "\\" + instName, results);
            }
        }
        catch
        {
            // 注册表不可访问时静默返回
        }
        return results;
    }

    private static void WalkForDescriptors(Microsoft.Win32.RegistryKey parent, string name, string path, List<(string, byte[])> sink)
    {
        using var key = parent.OpenSubKey(name);
        if (key == null) return;
        if (key.GetValue("ReportDescriptor") is byte[] rd && rd.Length > 0)
            sink.Add((path, rd));
        foreach (var sub in key.GetSubKeyNames())
            WalkForDescriptors(key, sub, path + "\\" + sub, sink);
    }

    /// <summary>同步发送输出报告（hid.dll 提供，内部处理报告 ID 与阻塞）。</summary>
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

    /// <summary>同步读取输入报告。</summary>
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);
}

/// <summary>
/// 已打开的 HID 设备句柄。
/// 使用重叠(Overlapped) I/O + 超时取消，避免同步 ReadFile 永久阻塞并泄漏线程
/// —— 这是 HID 读写的关键：键盘空闲时同步 ReadFile 会一直挂住。
/// </summary>
public sealed class HidConnection : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileRaw(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int toRead, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, int toWrite, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_NOT_FOUND = 1168;
    private const int WAIT_OBJECT_0 = 0;

    private SafeFileHandle? _handle;
    private readonly object _ioLock = new();

    public HidInterfaceInfo Info { get; }
    public bool IsOpen => _handle is { IsInvalid: false };

    /// <summary>最近一次 I/O 的 Win32 错误码，便于诊断。</summary>
    public int LastWin32Error { get; private set; }

    /// <summary>是否以可写方式打开了句柄（否则 Write 必然失败）。</summary>
    public bool OpenedForWrite { get; private set; }

    /// <summary>底层句柄（供 hid.dll 的同步 API 使用）。</summary>
    public SafeFileHandle? Handle => _handle;

    public HidConnection(HidInterfaceInfo info) => Info = info;

    public bool Open()
    {
        Close();
        // 先尝试读写打开，失败再退回只读（部分接口不允许写）
        _handle = NativeOpen(Info.DevicePath, true);
        OpenedForWrite = !_handle.IsInvalid;
        if (_handle.IsInvalid)
        {
            LastWin32Error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            _handle = NativeOpen(Info.DevicePath, false);
            if (_handle.IsInvalid) LastWin32Error = Marshal.GetLastWin32Error();
        }
        return !_handle.IsInvalid;
    }

    private static SafeFileHandle NativeOpen(string path, bool readWrite)
    {
        uint access = readWrite ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
        return CreateFileRaw(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
    }

    /// <summary>
    /// 写一个输出报告。payload 不含报告 ID 字节（会自动补 0）。
    /// timeoutMs 为等待写完成的上限；无线链路下写入可能永不完成，调用方可传更短的值避免长时间卡住。
    /// </summary>
    public int Write(byte[] payload, byte reportId = 0, int timeoutMs = 500)
    {
        LastWin32Error = 0;
        int expect = Math.Max(1, (int)Info.OutputReportByteLength - 1);
        int frameLen = expect + 1;
        int copyLen = Math.Min(payload.Length, expect);

        lock (_ioLock)
        {
            var h = _handle;
            if (h == null || h.IsInvalid) return -1;

            var ev = CreateEventW(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero) return -1;

            IntPtr bufPtr = IntPtr.Zero;
            IntPtr ovPtr = IntPtr.Zero;
            try
            {
                bufPtr = Marshal.AllocHGlobal(frameLen);
                Marshal.WriteByte(bufPtr, 0, reportId);
                if (copyLen > 0) Marshal.Copy(payload, 0, bufPtr + 1, copyLen);
                ZeroMemory(bufPtr, 1 + copyLen, frameLen - (1 + copyLen));

                ovPtr = AllocOverlapped(ev);
                bool ok = WriteFile(h, bufPtr, frameLen, out int written, ovPtr);
                if (!ok)
                {
                    LastWin32Error = Marshal.GetLastWin32Error();
                    if (LastWin32Error != ERROR_IO_PENDING) return -1;
                    if (WaitForSingleObject(ev, timeoutMs) != WAIT_OBJECT_0)
                    {
                        CancelAndDrain(h, ovPtr);
                        LastWin32Error = -1;
                        return -1;
                    }
                }
                bool done = GetOverlappedResult(h, ovPtr, out written, false);
                if (!done) LastWin32Error = Marshal.GetLastWin32Error();
                return done ? written : -1;
            }
            finally
            {
                if (ovPtr != IntPtr.Zero) Marshal.FreeHGlobal(ovPtr);
                if (bufPtr != IntPtr.Zero) Marshal.FreeHGlobal(bufPtr);
                CloseHandle(ev);
            }
        }
    }

    /// <summary>读一个输入报告，返回不含报告 ID 的负载。超时返回 null。</summary>
    public byte[]? Read(int timeoutMs = 300)
    {
        LastWin32Error = 0;
        int expect = Math.Max(2, (int)Info.InputReportByteLength);

        lock (_ioLock)
        {
            var h = _handle;
            if (h == null || h.IsInvalid) return null;

            var ev = CreateEventW(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero) return null;

            IntPtr bufPtr = IntPtr.Zero;
            IntPtr ovPtr = IntPtr.Zero;
            try
            {
                bufPtr = Marshal.AllocHGlobal(expect);
                ZeroMemory(bufPtr, 0, expect);
                ovPtr = AllocOverlapped(ev);

                bool ok = ReadFile(h, bufPtr, expect, out int read, ovPtr);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING)
                    {
                        LastWin32Error = err;
                        return null;
                    }
                    int w = WaitForSingleObject(ev, timeoutMs);
                    if (w != WAIT_OBJECT_0)
                    {
                        CancelAndDrain(h, ovPtr);
                        return null;
                    }
                }
                if (!GetOverlappedResult(h, ovPtr, out read, false))
                {
                    LastWin32Error = Marshal.GetLastWin32Error();
                    return null;
                }
                if (read <= 0) return null;
                int n = Math.Min(read, expect);
                if (n <= 1) return Array.Empty<byte>();
                var result = new byte[n - 1];
                Marshal.Copy(bufPtr + 1, result, 0, n - 1);
                return result;
            }
            finally
            {
                if (ovPtr != IntPtr.Zero) Marshal.FreeHGlobal(ovPtr);
                if (bufPtr != IntPtr.Zero) Marshal.FreeHGlobal(bufPtr);
                CloseHandle(ev);
            }
        }
    }

    private static IntPtr AllocOverlapped(IntPtr ev)
    {
        int size = Marshal.SizeOf<OVERLAPPED>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        ZeroMemory(ptr, 0, size);
        Marshal.StructureToPtr(new OVERLAPPED { hEvent = ev }, ptr, false);
        return ptr;
    }

    private static void CancelAndDrain(SafeFileHandle h, IntPtr ovPtr)
    {
        bool canceled = CancelIoEx(h, ovPtr);
        if (canceled || Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
        {
            // 等内核完成 IRP 归还缓冲后，finally 才能安全释放 ovPtr / bufPtr
            GetOverlappedResult(h, ovPtr, out _, true);
        }
    }

    private static void ZeroMemory(IntPtr ptr, int offset, int length)
    {
        for (int i = 0; i < length; i++)
            Marshal.WriteByte(ptr, offset + i, 0);
    }

    public void Close()
    {
        lock (_ioLock)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    public void Dispose() => Close();
}
