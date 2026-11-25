using System.Text.Json;

namespace RS8Control.Model;

/// <summary>QMK / VIA 键码名称表与解析。</summary>
public static class Keycodes
{
    public const ushort KC_NO = 0x0000;
    public const ushort KC_TRNS = 0x0001;

    // QMK 键码区间
    private const ushort QK_MODS = 0x0100;
    private const ushort QK_MOD_TAP = 0x2000;
    private const ushort QK_LAYER_TAP = 0x4000;
    private const ushort QK_LAYER_MOD = 0x5000;
    private const ushort QK_TO = 0x5200;
    private const ushort QK_MO = 0x5220;
    private const ushort QK_DF = 0x5240;
    private const ushort QK_TG = 0x5260;
    private const ushort QK_OSL = 0x5280;
    private const ushort QK_OSM = 0x52A0;
    private const ushort QK_TT = 0x52C0;
    private const ushort QK_PDF = 0x52E0;
    private const ushort QK_KB_0 = 0x7E00;

    /// <summary>HID Usage ID (0x00-0xE7) 名称表，索引即 usage 值。</summary>
    private static readonly Dictionary<byte, string> Hid = new()
    {
        [0x00] = "—",
        [0x04] = "A", [0x05] = "B", [0x06] = "C", [0x07] = "D", [0x08] = "E", [0x09] = "F",
        [0x0A] = "G", [0x0B] = "H", [0x0C] = "I", [0x0D] = "J", [0x0E] = "K", [0x0F] = "L",
        [0x10] = "M", [0x11] = "N", [0x12] = "O", [0x13] = "P", [0x14] = "Q", [0x15] = "R",
        [0x16] = "S", [0x17] = "T", [0x18] = "U", [0x19] = "V", [0x1A] = "W", [0x1B] = "X",
        [0x1C] = "Y", [0x1D] = "Z",
        [0x1E] = "1!", [0x1F] = "2@", [0x20] = "3#", [0x21] = "4$", [0x22] = "5%",
        [0x23] = "6^", [0x24] = "7&", [0x25] = "8*", [0x26] = "9(", [0x27] = "0)",
        [0x28] = "Enter", [0x29] = "Esc", [0x2A] = "Backspace", [0x2B] = "Tab", [0x2C] = "Space",
        [0x2D] = "-_", [0x2E] = "=+", [0x2F] = "[{", [0x30] = "]}", [0x31] = "\\|",
        [0x32] = "非US #", [0x33] = ";:", [0x34] = "'\"", [0x35] = "`~",
        [0x36] = ",<", [0x37] = ".>", [0x38] = "/?", [0x39] = "CapsLock",
        [0x3A] = "F1", [0x3B] = "F2", [0x3C] = "F3", [0x3D] = "F4", [0x3E] = "F5", [0x3F] = "F6",
        [0x40] = "F7", [0x41] = "F8", [0x42] = "F9", [0x43] = "F10", [0x44] = "F11", [0x45] = "F12",
        // 编辑区按真实键帽印字缩写，否则 1u 键宽会被省略号截成 "Page…"
        [0x46] = "PRINT", [0x47] = "SCRLK", [0x48] = "PAUSE",
        [0x49] = "INS", [0x4A] = "HOME", [0x4B] = "PGUP", [0x4C] = "DEL",
        [0x4D] = "END", [0x4E] = "PGDN", [0x4F] = "→", [0x50] = "←", [0x51] = "↓", [0x52] = "↑",
        [0x53] = "NumLock", [0x54] = "小键盘 /", [0x55] = "小键盘 *", [0x56] = "小键盘 -",
        [0x57] = "小键盘 +", [0x58] = "小键盘 Enter", [0x59] = "小键盘 1", [0x5A] = "小键盘 2",
        [0x5B] = "小键盘 3", [0x5C] = "小键盘 4", [0x5D] = "小键盘 5", [0x5E] = "小键盘 6",
        [0x5F] = "小键盘 7", [0x60] = "小键盘 8", [0x61] = "小键盘 9", [0x62] = "小键盘 0",
        [0x63] = "小键盘 .", [0x64] = "非US \\|", [0x65] = "菜单键", [0x66] = "电源", [0x67] = "小键盘 =",
        [0x68] = "F13", [0x69] = "F14", [0x6A] = "F15", [0x6B] = "F16", [0x6C] = "F17", [0x6D] = "F18",
        [0x6E] = "F19", [0x6F] = "F20", [0x70] = "F21", [0x71] = "F22", [0x72] = "F23", [0x73] = "F24",
        [0x74] = "Execute", [0x75] = "Help", [0x76] = "Menu", [0x77] = "Select", [0x78] = "Stop",
        [0x79] = "Again", [0x7A] = "Undo", [0x7B] = "Cut", [0x7C] = "Copy", [0x7D] = "Paste",
        [0x7E] = "Find", [0x7F] = "静音", [0x80] = "音量+", [0x81] = "音量−",
        [0x82] = "锁定 CapsLock", [0x83] = "锁定 NumLock", [0x84] = "锁定 ScrollLock",
        [0x85] = "小键盘 ,", [0x86] = "小键盘 = (AS400)", [0x87] = "国际1",
        [0x88] = "国际2", [0x89] = "国际3", [0x8A] = "国际4", [0x8B] = "国际5",
        [0x8C] = "国际6", [0x8D] = "国际7", [0x8E] = "国际8", [0x8F] = "国际9",
        [0x90] = "语言1", [0x91] = "语言2", [0x92] = "语言3", [0x93] = "语言4", [0x94] = "语言5",
        [0x95] = "语言6", [0x96] = "语言7", [0x97] = "语言8", [0x98] = "语言9",
        [0x99] = "AltErase", [0x9A] = "SysReq", [0x9B] = "Cancel", [0x9C] = "Clear",
        [0x9D] = "Prior", [0x9E] = "Return", [0x9F] = "Separator", [0xA0] = "Out",
        [0xA1] = "Oper", [0xA2] = "ClearAgain", [0xA3] = "CrSel", [0xA4] = "ExSel",
        [0xB0] = "小键盘 00", [0xB1] = "小键盘 000", [0xB2] = "千位分隔符",
        [0xB3] = "小数点", [0xB4] = "货币单位", [0xB5] = "货币子单位",
        [0xB6] = "小键盘 (", [0xB7] = "小键盘 )", [0xB8] = "小键盘 {", [0xB9] = "小键盘 }",
        [0xBA] = "小键盘 Tab", [0xBB] = "小键盘 Backspace", [0xBC] = "小键盘 A", [0xBD] = "小键盘 B",
        [0xBE] = "小键盘 C", [0xBF] = "小键盘 D", [0xC0] = "小键盘 E", [0xC1] = "小键盘 F",
        [0xC2] = "小键盘 XOR", [0xC3] = "小键盘 ^", [0xC4] = "小键盘 %", [0xC5] = "小键盘 <",
        [0xC6] = "小键盘 >", [0xC7] = "小键盘 &", [0xC8] = "小键盘 &&", [0xC9] = "小键盘 |",
        [0xCA] = "小键盘 ||", [0xCB] = "小键盘 :", [0xCC] = "小键盘 #", [0xCD] = "小键盘 空格",
        [0xCE] = "小键盘 @", [0xCF] = "小键盘 !",
        [0xD0] = "小键盘 MS", [0xD1] = "小键盘 MR", [0xD2] = "小键盘 MC", [0xD3] = "小键盘 M+",
        [0xD4] = "小键盘 M−", [0xD5] = "小键盘 M*", [0xD6] = "小键盘 M/",
        [0xD7] = "小键盘 +/−", [0xD8] = "小键盘 Clear", [0xD9] = "小键盘 ClearEntry",
        [0xDA] = "小键盘 二进制", [0xDB] = "小键盘 八进制", [0xDC] = "小键盘 十进制", [0xDD] = "小键盘 十六进制",
        [0xE0] = "左 Ctrl", [0xE1] = "左 Shift", [0xE2] = "左 Alt", [0xE3] = "左 Win",
        [0xE4] = "右 Ctrl", [0xE5] = "右 Shift", [0xE6] = "右 Alt", [0xE7] = "右 Win",
    };

    private static readonly string[] ModNames = { "Ctrl", "Shift", "Alt", "Win" };

    /// <summary>内置扩展键码中文名兜底（Consumer / 系统 / RGB 背光等，无 shortcuts.json 时也能正常显示）。</summary>
    private static readonly Dictionary<ushort, string> BuiltInExtraNames = new()
    {
        // QMK/HID Consumer & 系统键 (0x00A5..0x00C2)
        [0x00A8] = "静音",
        [0x00A9] = "音量+",
        [0x00AA] = "音量−",
        [0x00AB] = "下一曲",
        [0x00AC] = "上一曲",
        [0x00AE] = "暂停/播放",
        [0x00AF] = "播放器",
        [0x00B1] = "邮件",
        [0x00B2] = "计算器",
        [0x00B5] = "主页",
        [0x00BD] = "屏幕亮度+",
        [0x00BE] = "屏幕亮度−",
        [0x00C1] = "调度中心",
        [0x00C2] = "启动台",

        // QMK RGB 背光键
        [0x7820] = "开关全灯",
        [0x7821] = "切换灯效",
        [0x7823] = "切换颜色",
        [0x7825] = "饱和度−",
        [0x7826] = "饱和度+",
        [0x7827] = "亮度+",
        [0x7828] = "亮度−",
        [0x7829] = "速度+",
        [0x782A] = "速度−",

        // 其他扩展功能键
        [0x7C03] = "恢复出厂",
        [0x7E1E] = "环绕灯速度−",
        [0x7E1F] = "环绕灯速度+",
    };

    /// <summary>层别名（如 2→"FN"、3→"Mac"），由 defs/shortcuts.json 提供；未设置时用通用措辞。</summary>
    public static IReadOnlyDictionary<int, string> LayerAliases { get; private set; } = new Dictionary<int, string>();

    /// <summary>由 defs/shortcuts.json 的 keycodeNames 加载的中文键名映射。</summary>
    private static IReadOnlyDictionary<ushort, string> ShortcutNames = new Dictionary<ushort, string>();

    /// <summary>兼容旧调用名；等价于 <see cref="LoadShortcutsMetadata"/>。</summary>
    public static void LoadLayerAliases(string jsonPath) => LoadShortcutsMetadata(jsonPath);

    /// <summary>从 defs/shortcuts.json 读取 layerAliases 与 keycodeNames。失败时保持空表，不抛异常。</summary>
    public static void LoadShortcutsMetadata(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("layerAliases", out var el) && el.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<int, string>();
                foreach (var p in el.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out int layer)) continue;
                    if (p.Value.ValueKind != JsonValueKind.String) continue;
                    string alias = p.Value.GetString() ?? "";
                    if (alias.Length > 0) map[layer] = alias;
                }
                LayerAliases = map;
            }

            if (root.TryGetProperty("keycodeNames", out var kn) && kn.ValueKind == JsonValueKind.Object)
            {
                var names = new Dictionary<ushort, string>();
                foreach (var p in kn.EnumerateObject())
                {
                    string hex = p.Name.Trim();
                    if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
                    if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ushort code))
                        continue;
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!p.Value.TryGetProperty("zh", out var zhEl) || zhEl.ValueKind != JsonValueKind.String)
                        continue;
                    string zh = zhEl.GetString() ?? "";
                    if (zh.Length > 0) names[code] = zh;
                }
                ShortcutNames = names;
            }
        }
        catch
        {
            // 定义文件缺失或格式损坏都不该影响启动：保持空表，界面回退到通用措辞
        }
    }

    /// <summary>尝试从 shortcuts.json 或内置扩展表查中文名。</summary>
    public static bool TryGetExtraName(ushort kc, out string name) =>
        ShortcutNames.TryGetValue(kc, out name!) || BuiltInExtraNames.TryGetValue(kc, out name!);

    /// <summary>层号的显示名：有别名用别名，否则「固件层 n」。</summary>
    private static string LayerName(int layer) =>
        LayerAliases.TryGetValue(layer, out var alias) ? alias : $"固件层 {layer}";

    /// <summary>把键码翻译成人类可读名称。</summary>
    public static string Describe(ushort kc, IReadOnlyList<string>? customKeycodes = null)
    {
        if (kc == KC_NO) return "—";
        if (kc == KC_TRNS) return "▽ 透传";

        // 宏键码 QK_MACRO_0..QK_MACRO_15（0x7700 是宏 1，固件声明共 16 个）。
        // 再往后到 0x77FF 仍是 QMK 的宏键码块，但本固件没有对应宏，显示成「宏 ?」而不是裸十六进制。
        if (kc >= MacroCodec.QK_MACRO_0 && kc < MacroCodec.QK_MACRO_0 + 0x100)
        {
            int idx = kc - MacroCodec.QK_MACRO_0;
            return idx < MacroCodec.MaxMacros ? $"宏 {idx + 1}" : "宏 ?";
        }

        // VIA 自定义键码 QK_KB_0..QK_KB_63
        if (kc >= QK_KB_0 && kc < QK_KB_0 + 0x40)
        {
            int idx = kc - QK_KB_0;
            if (ShortcutNames.TryGetValue(kc, out var skn))
                return skn;
            if (BuiltInExtraNames.TryGetValue(kc, out var bkn))
                return bkn;
            if (customKeycodes != null && idx < customKeycodes.Count)
                return "★ " + customKeycodes[idx].Split('—')[0].Trim();
            return $"★ 自定义#{idx}";
        }

        if (kc < 0x0100)
        {
            if (Hid.TryGetValue((byte)kc, out var n)) return n;
            if (ShortcutNames.TryGetValue(kc, out n)) return n;
            if (BuiltInExtraNames.TryGetValue(kc, out n)) return n;
            return $"0x{kc:X4}";
        }

        // 修饰键组合
        if (kc >= QK_MODS && kc < QK_MOD_TAP)
        {
            byte mod = (byte)(kc >> 8);
            byte basic = (byte)(kc & 0xFF);
            return $"{ModString(mod)} + {Describe(basic, customKeycodes)}";
        }

        // 层修饰 / 层切换。0x5200–0x52FF 里每个家族占 0x20，层号必须按各自家族基址算，
        // 不能统一减 QK_TO（否则 MO(1)=0x5221 会被算成「按住层 33」）。
        if (kc >= QK_TO && kc < QK_TO + 0x100)
        {
            // 单次修饰 OSM：OSM(mod) = 0x52A0 | mod，mod 直接就是 MOD_LCTL/LSFT/LALT/LGUI 位
            // （0x01/0x02/0x04/0x08），不要再 +1 —— 加了会把 0x52A1(Ctrl) 算成 Shift。
            if (kc >= QK_OSM && kc < QK_OSM + 0x20)
                return $"单次修饰 {ModString((byte)(kc - QK_OSM))}";

            ushort family = kc switch
            {
                _ when kc < QK_MO => QK_TO,
                _ when kc < QK_DF => QK_MO,
                _ when kc < QK_TG => QK_DF,
                _ when kc < QK_OSL => QK_TG,
                _ when kc < QK_OSM => QK_OSL,
                _ when kc < QK_PDF => QK_TT,
                _ => QK_PDF,
            };
            // 措辞统一成「固件层 n」；shortcuts.json 给了别名时（2→FN、3→Mac）优先用别名。
            int layer = kc - family;
            string name = LayerName(layer);
            bool aliased = LayerAliases.ContainsKey(layer);
            return kc switch
            {
                // MO 就是键帽上印着的那个 Fn 键（RS8 层 0 的 Fn = 0x5222 = MO(2)），
                // 有别名时直接显示别名本身，不再加「按住」前缀。
                _ when kc < QK_MO => $"切到 {name}",
                _ when kc < QK_DF => aliased ? name : $"按住固件层 {layer}",
                _ when kc < QK_TG => $"默认 {name}",
                _ when kc < QK_OSL => aliased ? $"{name} 锁定" : $"切换固件层 {layer}",
                _ when kc < QK_OSM => $"单次 {name}",
                _ when kc < QK_PDF => $"点按 {name}",
                _ => $"永久默认 {name}",
            };
        }

        if (kc >= QK_LAYER_MOD && kc < QK_TO)
        {
            int layer = (kc >> 5) & 0x0F;
            byte mod = (byte)(kc & 0x1F);
            return $"{LayerName(layer)} + {ModString(mod)}";
        }

        if (kc >= QK_LAYER_TAP && kc < QK_LAYER_MOD)
        {
            int layer = (kc >> 8) & 0x0F;
            byte basic = (byte)(kc & 0xFF);
            return $"按住 {LayerName(layer)} / 点按 {Describe(basic, customKeycodes)}";
        }

        if (kc >= QK_MOD_TAP && kc < QK_LAYER_TAP)
        {
            byte mod = (byte)(kc >> 8);
            byte basic = (byte)(kc & 0xFF);
            return $"{ModString(mod)} / {Describe(basic, customKeycodes)}";
        }

        if (ShortcutNames.TryGetValue(kc, out var sn)) return sn;
        if (BuiltInExtraNames.TryGetValue(kc, out var bn)) return bn;

        return $"0x{kc:X4}";
    }

    private static string ModString(byte mod)
    {
        string prefix = (mod & 0x10) != 0 ? "右 " : "";
        var parts = new List<string>();
        for (int i = 0; i < 4; i++)
            if ((mod & (1 << i)) != 0) parts.Add(prefix + ModNames[i]);
        return parts.Count == 0 ? "?" : string.Join(" + ", parts);
    }

    /// <summary>供下拉选择用的常用键码列表（值 + 显示名）。</summary>
    public static List<(ushort Code, string Name)> CommonList(IReadOnlyList<string>? customKeycodes = null)
    {
        var list = new List<(ushort, string)> { (KC_NO, "— 无"), (KC_TRNS, "▽ 透传") };
        foreach (var kv in Hid.OrderBy(k => k.Key))
        {
            if (kv.Key == 0x00) continue;
            list.Add((kv.Key, kv.Value));
        }
        // 层功能键（0-3 层）：名称一律走 Describe，保证下拉列表和主界面措辞一致
        for (int l = 0; l < 4; l++)
        {
            list.Add(((ushort)(QK_MO + l), Describe((ushort)(QK_MO + l))));
            list.Add(((ushort)(QK_TG + l), Describe((ushort)(QK_TG + l))));
            list.Add(((ushort)(QK_TO + l), Describe((ushort)(QK_TO + l))));
        }
        list.Add(((ushort)(QK_OSL + 0), Describe((ushort)(QK_OSL + 0))));
        list.Add(((ushort)(QK_OSL + 1), Describe((ushort)(QK_OSL + 1))));
        if (customKeycodes != null)
        {
            for (int i = 0; i < customKeycodes.Count; i++)
                list.Add(((ushort)(QK_KB_0 + i), "★ " + customKeycodes[i]));
        }
        return list;
    }
}
