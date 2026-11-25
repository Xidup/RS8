using RS8Control.Hid;
using RS8Control.Model;
using RS8Control.Protocol;
using RS8Control.Ui;
using RS8Control.Ui.Panels;

namespace RS8Control.Diag;

/// <summary>纯逻辑回归自检（--selftest）：无需连接键盘，覆盖核心协议/编解码/UI 回归项。</summary>
public static class SelfTest
{
    public static int Run()
    {
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                pass++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                fail++;
                Console.WriteLine($"  [FAIL] {name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
            }
        }

        Console.WriteLine("=== RS8Control --selftest ===");

        // 1. ComboDialog 编码（确保不强制附加 Ctrl）
        foreach (var (code, label) in new (ushort, string)[]
        {
            (0x0204, "Shift + A"),
            (0x043D, "Alt + F4"),
            (0x0807, "Win + D"),
            (0x0306, "Ctrl + Shift + C"),
            (0x054C, "Ctrl + Alt + DEL"),
        })
        {
            using var dlg = new ComboDialog(code);
            dlg.CreateControl();
            (dlg.AcceptButton as Control)?.GetType();
            if (dlg.AcceptButton is IButtonControl btn) btn.PerformClick();
            Check($"ComboDialog({label} == 0x{code:X4})", dlg.Selected == code, $"got 0x{dlg.Selected:X4}");
        }

        // 2. Keycodes.Describe 语义与中文名
        string shortcutsPath = Path.Combine(AppContext.BaseDirectory, "defs", "shortcuts.json");
        Keycodes.LoadShortcutsMetadata(shortcutsPath);

        string ltDesc = Keycodes.Describe(0x422C);
        Check("Keycodes LT(2, Space)", ltDesc.Contains("按住 FN") && ltDesc.Contains("点按 Space"), ltDesc);

        string lmDesc = Keycodes.Describe(0x5021);
        Check("Keycodes LM(1, Ctrl)", lmDesc == "固件层 1 + Ctrl", lmDesc);

        string pdfDesc = Keycodes.Describe(0x52E0);
        Check("Keycodes PDF(0)", pdfDesc == "永久默认 固件层 0", pdfDesc);

        string mediaDesc = Keycodes.Describe(0x00A8);
        Check("Keycodes 媒体键 0x00A8", mediaDesc == "静音", mediaDesc);

        string rgbDesc = Keycodes.Describe(0x7820);
        Check("Keycodes 背光键 0x7820", rgbDesc == "开/关全部灯光", rgbDesc);

        string customDesc = Keycodes.Describe(0x7E08);
        Check("Keycodes 自定义键 0x7E08", customDesc == "Logo 灯开关", customDesc);

        // 3. MacroCodec 往返与缓冲尾 0 约束
        var steps = new MacroStep[]
        {
            new MacroKey(0xE0, MacroKeyKind.Down),
            new MacroText("ab"),
            new MacroDelay(50),
            new MacroKey(0xE0, MacroKeyKind.Up),
        };
        bool encOk = MacroCodec.TryEncode(steps, out var rawMacro, out var encErr);
        var decoded = MacroCodec.Decode(rawMacro);
        bool rtOk = encOk && decoded.Count == 4
            && decoded[0] is MacroKey k0 && k0 == new MacroKey(0xE0, MacroKeyKind.Down)
            && decoded[1] is MacroText t1 && t1.Text == "ab"
            && decoded[2] is MacroDelay d2 && d2.Ms == 50
            && decoded[3] is MacroKey k3 && k3 == new MacroKey(0xE0, MacroKeyKind.Up);
        Check("MacroCodec Down/Text/Delay/Up 往返", rtOk, encErr);

        var bufOk = MacroCodec.TryBuildBuffer(new[] { rawMacro }, MacroCodec.DefaultBufferSize, out var fullBuf, out var bufErr);
        Check("MacroCodec TryBuildBuffer 338B 且末字节为 0",
            bufOk && fullBuf.Length == MacroCodec.DefaultBufferSize && fullBuf[^1] == 0x00, bufErr);

        // 4. ViaDefinition.AutoLoad 优先级
        var def = ViaDefinition.AutoLoad();
        Check("ViaDefinition.AutoLoad 加载 RS8_via.json",
            def != null && def.Name == "RS 8" && def.Keys.Count == 87,
            def == null ? "null" : $"{def.Name}, keys={def.Keys.Count}");

        // 5. HidReportDescriptor 位宽累加 & Long Item
        byte[] descBytes =
        {
            0x05, 0x01,       // Usage Page (Generic Desktop)
            0x09, 0x06,       // Usage (Keyboard)
            0xA1, 0x01,       // Collection (Application)
            0x75, 0x01,       // Report Size (1)
            0x95, 0x05,       // Report Count (5)
            0x91, 0x02,       // Output (Data, Var, Abs) -> 5 bits
            0x95, 0x03,       // Report Count (3)
            0x91, 0x03,       // Output (Const)          -> +3 bits = 8 bits = 1 byte
            0xFE, 0x02, 0xFF, 0x00, 0x00, // Long item (length 2)
            0x75, 0x08,       // Report Size (8)
            0x95, 0x02,       // Report Count (2)
            0x81, 0x02,       // Input (2 bytes)
            0xC0              // End Collection
        };
        var parsedDesc = HidReportDescriptorInfo.Parse(descBytes);
        var col0 = parsedDesc.Collections.FirstOrDefault();
        Check("HidReportDescriptor 位宽累加与 Long Item",
            col0 != null && col0.OutputBytes == 1 && col0.InputBytes == 2,
            col0 == null ? "null" : $"OUT={col0.OutputBytes} IN={col0.InputBytes}");

        // 6. ShortcutPanel 离线 Fn 映射（大小写匹配 END）
        using var session = new DeviceSession();
        using var sp = new ShortcutPanel(session);
        var fnCanvas = FindChild<FnKeyCanvas>(sp);
        int endIdx = fnCanvas == null ? -1 : Array.IndexOf(fnCanvas.Physical, "END");
        string endFn = (fnCanvas != null && endIdx >= 0) ? fnCanvas.FnLabelOf(endIdx) : "";
        Check("ShortcutPanel 离线 END 键大小写匹配", endFn == "恢复出厂设置", $"endIdx={endIdx}, fn='{endFn}'");

        // 7. MainForm 懒加载启动控件数校验
        int totalControls = 0, rtbCount = 0;
        using (var mf = new MainForm())
        {
            CountDescendants(mf, ref totalControls, ref rtbCount);
        }
        Check($"MainForm 启动懒加载控件数 (total={totalControls}, rtb={rtbCount})",
            totalControls < 80 && rtbCount == 0,
            $"total={totalControls}, rtb={rtbCount}");

        // 8. MacroPanel 生命周期（CreateControl -> DrawToBitmap -> Dispose，防 ToolTip 空引用）
        Exception? macroDisposeEx = null;
        try
        {
            using var mp = new MacroPanel(session);
            mp.Size = new Size(1080, 900);
            mp.OnSessionChanged();
            using var bmp = new Bitmap(1080, 900);
            mp.DrawToBitmap(bmp, new Rectangle(0, 0, 1080, 900));
        }
        catch (Exception ex) { macroDisposeEx = ex; }
        Check("MacroPanel CreateControl/DrawToBitmap/Dispose 无异常", macroDisposeEx == null, macroDisposeEx?.Message ?? "");

        // 9. DeviceSession.Refresh 必须触发 Changed 事件（防止 try 内提前 return 跳过 Raise）
        int changedCount = 0;
        using (var ds = new DeviceSession())
        {
            ds.Changed += () => changedCount++;
            ds.Refresh();
        }
        Check($"DeviceSession.Refresh 触发 Changed 事件 (count={changedCount})", changedCount == 1, $"count={changedCount}");

        Console.WriteLine($"结果: 通过 {pass}，失败 {fail}");
        return fail == 0 ? 0 : 1;
    }

    private static void CountDescendants(Control root, ref int total, ref int rtbs)
    {
        foreach (Control c in root.Controls)
        {
            total++;
            if (c is RichTextBox) rtbs++;
            CountDescendants(c, ref total, ref rtbs);
        }
    }

    private static T? FindChild<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T hit) return hit;
            var inner = FindChild<T>(c);
            if (inner != null) return inner;
        }
        return null;
    }
}
