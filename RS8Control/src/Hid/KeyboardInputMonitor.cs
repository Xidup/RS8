using System.Runtime.InteropServices;

namespace RS8Control.Hid;

/// <summary>
/// 监听本键盘的按键，把按键状态通过事件抛出来。接口按 VID/PID 过滤，因此不会误收其它键盘的输入。
///
/// 实现说明：本来最直接的做法是打开键盘的 boot 接口（Usage 0x0001/0x0006）读输入报告，
/// 但 Windows 的键盘类驱动会**独占**这个集合，CreateFile 一律返回 ERROR_ACCESS_DENIED(5)
/// （实测 PID_3005 的 MI=0 / MI=2 键盘集合全部拒绝 GENERIC_READ，提权也一样；
/// 而同设备的 0xFF60 VIA 接口能正常打开，可见是键盘集合本身被系统占住）。
/// 因此这里改用 Raw Input：它是 Windows 提供的正规途径，既能拿到按键，
/// 又能通过设备句柄精确区分是哪一个键盘 —— 正好满足"只认本键盘"的要求。
/// </summary>
public sealed class KeyboardInputMonitor : IDisposable
{
    private readonly ushort _vid;
    private readonly ushort _pid;

    private volatile IntPtr[] _devices = Array.Empty<IntPtr>();
    private readonly object _keyGate = new();
    private readonly HashSet<byte> _down = new();
    private byte[] _last = Array.Empty<byte>();
    private bool _e1Pending;
    private volatile bool _running;

    /// <param name="vendorId">要监听的键盘 VID（来自 DeviceSession.Device）。</param>
    /// <param name="productId">要监听的键盘 PID；与 VID 一起构成过滤条件，其它键盘不会被收进来。</param>
    public KeyboardInputMonitor(ushort vendorId, ushort productId)
    {
        _vid = vendorId;
        _pid = productId;
    }

    /// <summary>当前按下的 HID usage 集合变化时触发（在后台线程）。</summary>
    public event Action<IReadOnlyCollection<byte>>? Changed;

    /// <summary>是否正在监听。</summary>
    public bool Running => _running;

    /// <summary>Start 失败的原因；成功时为空串。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>本键盘的 boot 键盘接口信息；Start 成功后才有值。</summary>
    public HidInterfaceInfo? Interface { get; private set; }

    /// <summary>
    /// 找到本键盘并开始监听。找不到接口 / 注册失败返回 false，原因写在 <see cref="LastError"/>。
    /// </summary>
    public bool Start()
    {
        if (_running) return true;
        LastError = "";

        // 先按 VID/PID 过滤，再确认它确实是键盘（Usage 0x0001/0x0006）。
        // 报告长度限制 8..16：同 VID/PID 下还有 32 字节的键盘集合，boot 接口才是 9 字节那个。
        var kbd = Win32Hid.Enumerate(_vid, _pid)
            .FirstOrDefault(a => a.UsagePage == 0x0001 && a.Usage == 0x0006
                                 && a.InputReportByteLength is >= 8 and <= 16);
        if (kbd == null)
        {
            LastError = "找不到键盘输入接口。请确认键盘已连接。";
            return false;
        }

        lock (_keyGate)
        {
            _down.Clear();
            _last = Array.Empty<byte>();
            _e1Pending = false;
        }
        if (!RawKeyboardHost.Subscribe(this, _vid, _pid, out _, out string err))
        {
            LastError = err;
            return false;
        }

        Interface = kbd;
        _running = true;
        return true;
    }

    /// <summary>停止监听。可重复调用。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        RawKeyboardHost.Unsubscribe(this);
        _devices = Array.Empty<IntPtr>();
        Interface = null;
    }

    public void Dispose() => Stop();

    internal IntPtr[] Devices
    {
        get => _devices;
        set => _devices = value;
    }

    /// <summary>
    /// Raw Input 线程回调：把一次按下/松开并进当前集合，**只有集合真的变了才通知**。
    /// 按住不放时 Windows 会持续发按下事件，每次都通知会把界面刷爆。
    /// </summary>
    internal void HandleKey(RawKeyboardHost.RAWKEYBOARD kb)
    {
        if (!_running) return;

        byte[]? snapshotToNotify;
        lock (_keyGate)
        {
            if (!_running) return;

            byte usage;
            if ((kb.Flags & RawKeyboardHost.RiKeyE1) != 0 && kb.MakeCode == 0x1D)
            {
                _e1Pending = true;
                return;
            }
            if (_e1Pending && kb.MakeCode == 0x45)
            {
                _e1Pending = false;
                usage = 0x48; // PAUSE
            }
            else
            {
                _e1Pending = false;
                if (!RawKeyboardHost.TryMapScan(kb.MakeCode, kb.Flags, out usage)) return;
            }

            bool down = (kb.Flags & RawKeyboardHost.RiKeyBreak) == 0;
            bool changed = down ? _down.Add(usage) : _down.Remove(usage);
            if (!changed) return;

            var snapshot = _down.ToArray();
            Array.Sort(snapshot);
            if (snapshot.Length == _last.Length && snapshot.AsSpan().SequenceEqual(_last)) return;
            _last = snapshot;
            snapshotToNotify = snapshot;
        }

        Changed?.Invoke(snapshotToNotify);
    }
}

/// <summary>
/// 进程级的 Raw Input 键盘泵。
/// 注册是进程级的、WM_INPUT 只能投递到一个窗口，所以全进程共用一个消息窗口，
/// 各监听者只做"设备句柄 → 订阅者"的分发，避免两个面板同时监听时互相抢注册。
/// </summary>
internal static class RawKeyboardHost
{
    internal const ushort RiKeyBreak = 0x01;
    internal const ushort RiKeyE0 = 0x02;
    internal const ushort RiKeyE1 = 0x04;

    private const ushort UsagePageKeyboard = 0x0001;
    private const ushort UsageKeyboard = 0x0006;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeKeyboard = 1;
    private const uint WmInput = 0x00FF;
    private const uint WmAppQuit = 0x8001;
    private const int HwndMessage = -3;

    private static readonly object Gate = new();
    private static readonly List<KeyboardInputMonitor> Subs = new();
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static string _error = "";

    // ---------------- 订阅 ----------------

    /// <summary>订阅按键事件。第一个订阅者负责建窗口、注册 Raw Input、起消息线程。</summary>
    public static bool Subscribe(KeyboardInputMonitor m, ushort vid, ushort pid, out IntPtr[] devices, out string error)
    {
        Thread? idleThread = null;
        IntPtr idleHwnd = IntPtr.Zero;
        lock (Gate)
        {
            EnsureStarted();
            if (_hwnd == IntPtr.Zero)
            {
                devices = Array.Empty<IntPtr>();
                error = _error.Length > 0 ? _error : "Raw Input 初始化失败。";
                if (Subs.Count == 0)
                {
                    _thread = null;
                    _hwnd = IntPtr.Zero;
                }
                return false;
            }

            devices = FindKeyboardDevices(vid, pid);
            if (devices.Length == 0)
            {
                error = $"Raw Input 里找不到 VID_{vid:X4}/PID_{pid:X4} 的键盘设备。请确认键盘已连接。";
                TryDetachIdleLocked(out idleThread, out idleHwnd);
            }
            else
            {
                m.Devices = devices;
                Subs.Add(m);
                error = "";
                return true;
            }
        }

        ShutdownHost(idleThread, idleHwnd);
        return false;
    }

    /// <summary>退订。最后一个订阅者退出时，连消息线程一起收掉，不留常驻后台线程。</summary>
    public static void Unsubscribe(KeyboardInputMonitor m)
    {
        Thread? thread;
        IntPtr hwnd;
        lock (Gate)
        {
            Subs.Remove(m);
            if (!TryDetachIdleLocked(out thread, out hwnd)) return;
        }

        // 注意：Join 必须在锁外做 —— 消息线程可能正卡在 HandleRawInput 里等这把锁
        ShutdownHost(thread, hwnd);
    }

    private static bool TryDetachIdleLocked(out Thread? thread, out IntPtr hwnd)
    {
        if (Subs.Count > 0)
        {
            thread = null;
            hwnd = IntPtr.Zero;
            return false;
        }
        thread = _thread;
        hwnd = _hwnd;
        _thread = null;
        _hwnd = IntPtr.Zero;
        _error = "";
        return thread != null;
    }

    private static void ShutdownHost(Thread? thread, IntPtr hwnd)
    {
        if (thread == null) return;
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WmAppQuit, IntPtr.Zero, IntPtr.Zero);
        if (thread.IsAlive && thread != Thread.CurrentThread) thread.Join(1000);
    }

    // ---------------- 扫描码 → HID usage ----------------

    /// <summary>
    /// PS/2 Set-1 扫描码 → HID usage。E0 / E1 前缀各占一个命名空间（bit8 / bit9），
    /// 因为同一个扫描码在带不带前缀时是不同键（如 1C = Enter，E0 1C = 小键盘 Enter）。
    /// 用扫描码而不是虚拟键：扫描码是物理位置，不受输入法/键盘布局影响。
    /// 映射不到的多媒体键（键盘用 Consumer Control 报，不走键盘集合）直接忽略。
    /// </summary>
    internal static bool TryMapScan(ushort makeCode, ushort flags, out byte usage)
    {
        int ns = (flags & RiKeyE1) != 0 ? 0x200 : (flags & RiKeyE0) != 0 ? 0x100 : 0;
        return ScanToUsage.TryGetValue(ns | makeCode, out usage);
    }

    private static readonly Dictionary<int, byte> ScanToUsage = new()
    {
        [0x01] = 0x29, [0x02] = 0x1E, [0x03] = 0x1F, [0x04] = 0x20, [0x05] = 0x21, [0x06] = 0x22,
        [0x07] = 0x23, [0x08] = 0x24, [0x09] = 0x25, [0x0A] = 0x26, [0x0B] = 0x27, [0x0C] = 0x2D,
        [0x0D] = 0x2E, [0x0E] = 0x2A, [0x0F] = 0x2B, [0x10] = 0x14, [0x11] = 0x1A, [0x12] = 0x08,
        [0x13] = 0x15, [0x14] = 0x17, [0x15] = 0x1C, [0x16] = 0x18, [0x17] = 0x0C, [0x18] = 0x12,
        [0x19] = 0x13, [0x1A] = 0x2F, [0x1B] = 0x30, [0x1C] = 0x28, [0x1D] = 0xE0, [0x1E] = 0x04,
        [0x1F] = 0x16, [0x20] = 0x07, [0x21] = 0x09, [0x22] = 0x0A, [0x23] = 0x0B, [0x24] = 0x0D,
        [0x25] = 0x0E, [0x26] = 0x0F, [0x27] = 0x33, [0x28] = 0x34, [0x29] = 0x35, [0x2A] = 0xE1,
        [0x2B] = 0x31, [0x2C] = 0x1D, [0x2D] = 0x1B, [0x2E] = 0x06, [0x2F] = 0x19, [0x30] = 0x05,
        [0x31] = 0x11, [0x32] = 0x10, [0x33] = 0x36, [0x34] = 0x37, [0x35] = 0x38, [0x36] = 0xE5,
        [0x37] = 0x55, [0x38] = 0xE2, [0x39] = 0x2C, [0x3A] = 0x39, [0x3B] = 0x3A, [0x3C] = 0x3B,
        [0x3D] = 0x3C, [0x3E] = 0x3D, [0x3F] = 0x3E, [0x40] = 0x3F, [0x41] = 0x40, [0x42] = 0x41,
        [0x43] = 0x42, [0x44] = 0x43, [0x45] = 0x53, [0x46] = 0x47, [0x47] = 0x5F, [0x48] = 0x60,
        [0x49] = 0x61, [0x4A] = 0x56, [0x4B] = 0x5C, [0x4C] = 0x5D, [0x4D] = 0x5E, [0x4E] = 0x57,
        [0x4F] = 0x59, [0x50] = 0x5A, [0x51] = 0x5B, [0x52] = 0x62, [0x53] = 0x63, [0x57] = 0x44,
        [0x58] = 0x45, [0x64] = 0x68, [0x65] = 0x69, [0x66] = 0x6A, [0x67] = 0x6B, [0x68] = 0x6C,
        [0x69] = 0x6D, [0x6A] = 0x6E, [0x6B] = 0x6F, [0x6C] = 0x70, [0x6D] = 0x71, [0x6E] = 0x72,
        [0x76] = 0x73,
        // 扩展键（E0 前缀）
        [0x100 | 0x1C] = 0x58, [0x100 | 0x1D] = 0xE4, [0x100 | 0x35] = 0x54, [0x100 | 0x37] = 0x46,
        [0x100 | 0x38] = 0xE6, [0x100 | 0x47] = 0x4A, [0x100 | 0x48] = 0x52, [0x100 | 0x49] = 0x4B,
        [0x100 | 0x4B] = 0x50, [0x100 | 0x4D] = 0x4F, [0x100 | 0x4F] = 0x4D, [0x100 | 0x50] = 0x51,
        [0x100 | 0x51] = 0x4E, [0x100 | 0x52] = 0x49, [0x100 | 0x53] = 0x4C, [0x100 | 0x5B] = 0xE3,
        [0x100 | 0x5C] = 0xE7, [0x100 | 0x5D] = 0x65,
        // E1 前缀：Pause 是唯一的 E1 键
        [0x200 | 0x45] = 0x48,
    };

    // ---------------- 线程 / 窗口 ----------------

    private static void EnsureStarted()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim(false);
        var t = new Thread(() => Startup(ready)) { IsBackground = true, Name = "RS8 Raw Input" };
        _thread = t;
        t.Start();
        ready.Wait(3000);   // 等窗口建好、注册结果确定，Subscribe 才能立刻给出准确结论
    }

    private static void Startup(ManualResetEventSlim ready)
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = WndProcRef,
                hInstance = GetModuleHandleW(null),
                lpszClassName = WindowClassName,
            };
            if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/)
            {
                _error = $"注册窗口类失败 (Win32 {Marshal.GetLastWin32Error()})。";
                return;
            }

            hwnd = CreateWindowExW(0, WindowClassName, "RS8Control.RawKeyboard", 0, 0, 0, 0, 0,
                new IntPtr(HwndMessage), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                _error = $"创建消息窗口失败 (Win32 {Marshal.GetLastWin32Error()})。";
                return;
            }
            _hwnd = hwnd;

            var filter = new RAWINPUTDEVICE
            {
                usUsagePage = UsagePageKeyboard,
                usUsage = UsageKeyboard,
                dwFlags = RidevInputSink,   // 窗口不在前台也要收
                hwndTarget = hwnd,
            };
            if (!RegisterRawInputDevices(new[] { filter }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                _error = $"注册 Raw Input 失败 (Win32 {Marshal.GetLastWin32Error()})。";
                DestroyWindow(hwnd);
                _hwnd = IntPtr.Zero;
                hwnd = IntPtr.Zero;
                return;
            }
        }
        catch (Exception ex)
        {
            _error = "Raw Input 初始化异常: " + ex.Message;
            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
            }
        }
        finally
        {
            ready.Set();
            if (hwnd == IntPtr.Zero)
            {
                lock (Gate)
                {
                    if (_thread == Thread.CurrentThread)
                    {
                        _thread = null;
                        _hwnd = IntPtr.Zero;
                    }
                }
            }
        }

        if (hwnd == IntPtr.Zero) return;
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        var remove = new RAWINPUTDEVICE
        {
            usUsagePage = UsagePageKeyboard,
            usUsage = UsageKeyboard,
            dwFlags = RidevRemove,
            hwndTarget = IntPtr.Zero,
        };
        RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        DestroyWindow(hwnd);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmAppQuit)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (msg == WmInput)
        {
            try { HandleRawInput(lParam); }
            catch { /* 消息处理绝不能把窗口线程打挂 */ }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRawInput, RidInput, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 4096) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RidInput, buf, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.dwType != RimTypeKeyboard) return;

            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buf + (int)headerSize);

            KeyboardInputMonitor[] targets;
            lock (Gate)
            {
                if (Subs.Count == 0) return;
                targets = Subs.Where(s => s.Devices.Contains(header.hDevice)).ToArray();
            }
            foreach (var s in targets) s.HandleKey(kb);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>列出属于指定 VID/PID 的 Raw Input 键盘设备句柄（可能不止一个 collection）。</summary>
    private static IntPtr[] FindKeyboardDevices(ushort vid, ushort pid)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        uint count = 0;
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return Array.Empty<IntPtr>();

        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return Array.Empty<IntPtr>();

        string tag = $"VID_{vid:X4}&PID_{pid:X4}";
        var found = new List<IntPtr>();
        foreach (var d in list)
        {
            if (d.dwType != RimTypeKeyboard) continue;
            if (DeviceName(d.hDevice).Contains(tag, StringComparison.OrdinalIgnoreCase)) found.Add(d.hDevice);
        }
        return found.ToArray();
    }

    private static string DeviceName(IntPtr hDevice)
    {
        uint n = 0;
        GetRawInputDeviceInfoW(hDevice, RidiDeviceName, null, ref n);
        if (n == 0) return "";
        var sb = new StringBuilder((int)n);
        return GetRawInputDeviceInfoW(hDevice, RidiDeviceName, sb, ref n) == uint.MaxValue ? "" : sb.ToString();
    }

    // ---------------- P/Invoke ----------------

    private const string WindowClassName = "RS8ControlRawKeyboardWindow";

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>必须静态保活，否则托管委托被回收后原生侧就是野指针。</summary>
    private static readonly WndProcDelegate WndProcRef = StaticWndProc;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint command, StringBuilder? data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
