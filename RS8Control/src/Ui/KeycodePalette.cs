using RS8Control.Model;

namespace RS8Control.Ui;

/// <summary>
/// 调色板里的一个按键：显示文字 + 键码 + 相对宽度（1 = 一个标准键宽）。
/// <para><see cref="Label"/> 为空字符串表示「占位键」：渲染器只推进 x、不画任何东西，
/// 用来撑出真实键盘上的空隙以保证列对齐。</para>
/// <para><see cref="Action"/> 为 true 表示这一格不是键码，而是界面入口（如「多键组合…」），
/// 点击时由画布抛出 <c>PaletteCanvas.ActionClicked</c>，交给窗体自己处理。</para>
/// </summary>
public sealed record PaletteKey(string Label, ushort Code, double Width = 1, bool Enabled = true, bool Action = false, string Tooltip = "");

/// <summary>一个键码分类：按行组织，行内按键按相对宽度排列，用来画成键盘的样子。</summary>
public sealed class KeycodeCategory
{
    public string Name { get; }
    public List<List<PaletteKey>> Rows { get; } = new();

    public KeycodeCategory(string name, params List<PaletteKey>[] rows)
    {
        Name = name;
        Rows.AddRange(rows);
    }

    /// <summary>该分类下所有按键（用于搜索与反查标签）。</summary>
    public IEnumerable<PaletteKey> AllKeys() => Rows.SelectMany(r => r);

    /// <summary>最宽一行的总键宽，用来计算缩放比例。</summary>
    public double MaxRowWidth()
    {
        // 手写循环而不是 Rows.Max(r => r.Sum(...))：嵌套 lambda 会触发 CS9236 噪声
        double best = 0;
        foreach (var row in Rows)
        {
            double w = 0;
            foreach (var k in row) w += k.Width;
            if (w > best) best = w;
        }
        return best > 0 ? best : 1;
    }
}

/// <summary>
/// 图形化键码选择器用的键位表。
///
/// 分类与顺序：基本键 / 层功能 / 媒体键 / 宏 / 自定义。
/// 基本键是一张完整的 104 键 ANSI 配列（总宽 22.5 单位），修饰键就画在它们的真实位置上，
/// 不再单独占一个分类。媒体键取的是 RS8 Fn 层实机使用的 0x00A8–0x00BE 那一批
/// （QMK 媒体语义，与 defs/shortcuts.json 里经实机读取验证过的中文名一致）。
/// </summary>
public static class KeycodePalette
{
    // ---- QMK 层功能键码基址（与 Model.Keycodes 内部常量保持一致）----
    private const ushort QkTo = 0x5200;   // TO(n)
    private const ushort QkMo = 0x5220;   // MO(n)
    private const ushort QkTg = 0x5260;   // TG(n)
    private const ushort QkOsl = 0x5280;  // OSL(n)
    private const ushort QkOsm = 0x52A0;  // OSM(mod)
    private const ushort QkLayerTap = 0x4000;   // LT(n, kc)
    private const ushort QkTt = 0x52C0;   // TT(n)
    private const ushort QkKb0 = 0x7E00;  // VIA 自定义键码起始

    /// <summary>「多键组合…」入口的哨兵键码：不属于任何真实键码，只用来标识这个入口。</summary>
    public const ushort ComboActionCode = 0xFFFE;

    /// <summary>104 配列总宽度（单位）。主键区 15 + 空 0.25 + 编辑区 3 + 空 0.25 + 小键盘 4。</summary>
    public const double AnsiTotalWidth = 22.5;

    private static PaletteKey K(string label, ushort code, double width = 1) => new(label, code, width);

    /// <summary>占位键：渲染器只推进 x，不画任何东西（见 <see cref="PaletteKey.Label"/>）。</summary>
    private static PaletteKey Sp(double width) => new("", 0, width);

    /// <summary>构建全部分类。<paramref name="layerCount"/> 决定层功能类里出现几层。</summary>
    public static List<KeycodeCategory> Build(int layerCount, IReadOnlyList<string>? customKeycodes)
    {
        int layers = Math.Clamp(layerCount, 1, 16);
        var list = new List<KeycodeCategory> { Basic(), LayerFunctions(layers), Media(), Macro() };

        var custom = new KeycodeCategory("自定义");

        // 第一行：多键组合入口（Action=true，点击后由 KeycodePicker 打开 ComboDialog）
        custom.Rows.Add(new List<PaletteKey>
        {
            new("＋ 多键组合…", ComboActionCode, 3.0, true, true, "创建修饰键 + 主键组合（如 Ctrl + Shift + C）"),
            new("恢复出厂", 0x7C03, 2.0, true, false, "恢复键盘出厂设置 (0x7C03)"),
        });

        var row = new List<PaletteKey>();
        int n = customKeycodes?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            ushort code = (ushort)(QkKb0 + i);
            string raw = customKeycodes![i] ?? "";
            string shortName = raw.Split('—')[0].Trim();
            if (shortName.Length == 0) shortName = $"KB{i}";
            string label = Keycodes.TryGetExtraName(code, out var zh) ? zh : shortName;
            string tip = string.IsNullOrWhiteSpace(raw) ? $"{label} (0x{code:X4})" : $"{label} · {raw} (0x{code:X4})";
            row.Add(new PaletteKey(label, code, 2.0, true, false, tip));
            if (row.Count == 6) { custom.Rows.Add(row); row = new List<PaletteKey>(); }
        }
        // 补齐 JSON 未声明但实机可用的环绕灯速度键码 (0x7E1E / 0x7E1F)
        foreach (ushort code in new ushort[] { 0x7E1E, 0x7E1F })
        {
            string label = Keycodes.Describe(code);
            row.Add(new PaletteKey(label, code, 2.0, true, false, $"{label} (0x{code:X4})"));
            if (row.Count == 6) { custom.Rows.Add(row); row = new List<PaletteKey>(); }
        }
        if (row.Count > 0) custom.Rows.Add(row);

        // 背光控制键码 (0x7820..0x782A)
        ushort[] rgbCodes = { 0x7820, 0x7821, 0x7823, 0x7825, 0x7826, 0x7827, 0x7828, 0x7829, 0x782A };
        var rgbRow = new List<PaletteKey>();
        foreach (ushort code in rgbCodes)
        {
            string label = Keycodes.Describe(code);
            rgbRow.Add(new PaletteKey(label, code, 2.0, true, false, $"{label} (0x{code:X4})"));
            if (rgbRow.Count == 6) { custom.Rows.Add(rgbRow); rgbRow = new List<PaletteKey>(); }
        }
        if (rgbRow.Count > 0) custom.Rows.Add(rgbRow);

        if (n == 0) custom.Rows.Add(new List<PaletteKey> { new("（该键盘定义没有额外自定义键码）", 0, 10, false) });
        list.Add(custom);

        return list;
    }

    /// <summary>
    /// 基本键：完整的 104 键 ANSI 配列 —— 功能行 + 主键区 + 编辑区 + 方向键 + 数字小键盘，
    /// 总宽 22.5 单位（见 <see cref="AnsiTotalWidth"/>），每一行都用占位键补齐到同一宽度，
    /// 这样小键盘各列才能和上面几行对齐。
    /// </summary>
    private static KeycodeCategory Basic() => new("基本键",
        // 功能行：Esc [空1] F1-F4 [空0.5] F5-F8 [空0.5] F9-F12 [空0.25] PRINT SCRLK PAUSE [尾部空4.25]
        new List<PaletteKey>
        {
            K("Esc", 0x29), Sp(1),
            K("F1", 0x3A), K("F2", 0x3B), K("F3", 0x3C), K("F4", 0x3D), Sp(0.5),
            K("F5", 0x3E), K("F6", 0x3F), K("F7", 0x40), K("F8", 0x41), Sp(0.5),
            K("F9", 0x42), K("F10", 0x43), K("F11", 0x44), K("F12", 0x45), Sp(0.25),
            K("PRINT", 0x46), K("SCRLK", 0x47), K("PAUSE", 0x48), Sp(4.25),
        },
        // 数字行：`~..=+ Bksp(2) [空0.25] INS HOME PGUP [空0.25] NumLock KP/ KP* KP-
        new List<PaletteKey>
        {
            K("`~", 0x35), K("1!", 0x1E), K("2@", 0x1F), K("3#", 0x20), K("4$", 0x21), K("5%", 0x22),
            K("6^", 0x23), K("7&", 0x24), K("8*", 0x25), K("9(", 0x26), K("0)", 0x27),
            K("-_", 0x2D), K("=+", 0x2E), K("Bksp", 0x2A, 2), Sp(0.25),
            K("INS", 0x49), K("HOME", 0x4A), K("PGUP", 0x4B), Sp(0.25),
            K("Num", 0x53), K("KP /", 0x54), K("KP *", 0x55), K("KP -", 0x56),
        },
        // Q 行：Tab(1.5) Q..]} \|(1.5) [空0.25] DEL END PGDN [空0.25] KP7 KP8 KP9 KP+
        new List<PaletteKey>
        {
            K("Tab", 0x2B, 1.5), K("Q", 0x14), K("W", 0x1A), K("E", 0x08), K("R", 0x15), K("T", 0x17),
            K("Y", 0x1C), K("U", 0x18), K("I", 0x0C), K("O", 0x12), K("P", 0x13),
            K("[{", 0x2F), K("]}", 0x30), K("\\|", 0x31, 1.5), Sp(0.25),
            K("DEL", 0x4C), K("END", 0x4D), K("PGDN", 0x4E), Sp(0.25),
            K("KP 7", 0x5F), K("KP 8", 0x60), K("KP 9", 0x61), K("KP +", 0x57),
        },
        // A 行：Caps(1.75) A..'" Enter(2.25) [空0.25 空3 空0.25] KP4 KP5 KP6 [空1]
        new List<PaletteKey>
        {
            K("Caps", 0x39, 1.75), K("A", 0x04), K("S", 0x16), K("D", 0x07), K("F", 0x09), K("G", 0x0A),
            K("H", 0x0B), K("J", 0x0D), K("K", 0x0E), K("L", 0x0F), K(";:", 0x33), K("'\"", 0x34),
            K("Enter", 0x28, 2.25), Sp(0.25), Sp(3), Sp(0.25),
            K("KP 4", 0x5C), K("KP 5", 0x5D), K("KP 6", 0x5E), Sp(1),
        },
        // Z 行：Shift(2.25) Z../? Shift(2.75) [空0.25 空1] ↑ [空1 空0.25] KP1 KP2 KP3 KPEnter
        // （方向键是倒 T：↑ 在倒数第二行、正对着最底行的 ↓，← ↓ → 在最底行 —— 真实 104 的排法）
        new List<PaletteKey>
        {
            K("Shift", 0xE1, 2.25), K("Z", 0x1D), K("X", 0x1B), K("C", 0x06), K("V", 0x19), K("B", 0x05),
            K("N", 0x11), K("M", 0x10), K(",<", 0x36), K(".>", 0x37), K("/?", 0x38), K("Shift", 0xE5, 2.75),
            Sp(0.25), Sp(1), K("↑", 0x52), Sp(1), Sp(0.25),
            K("KP 1", 0x59), K("KP 2", 0x5A), K("KP 3", 0x5B), K("KP Ent", 0x58),
        },
        // 底行：Ctrl Win Alt Space(6.25) Alt Win 菜单键 Ctrl [空0.25] ← ↓ → [空0.25] KP0(2) KP. [空1]
        new List<PaletteKey>
        {
            K("Ctrl", 0xE0, 1.25), K("Win", 0xE3, 1.25), K("Alt", 0xE2, 1.25), K("Space", 0x2C, 6.25),
            K("Alt", 0xE6, 1.25), K("Win", 0xE7, 1.25), K("菜单键", 0x65, 1.25), K("Ctrl", 0xE4, 1.25),
            Sp(0.25), K("←", 0x50), K("↓", 0x51), K("→", 0x4F), Sp(0.25),
            K("KP 0", 0x62, 2), K("KP .", 0x63), Sp(1),
        });

    /// <summary>
    /// 层功能：透传 + TO/MO/TG/OSL/TT + 单次修饰(OSM) + LT(n,kc)。
    /// 文案一律由 <see cref="Keycodes.Describe"/> 生成，跟主界面保持一致（层别名 2→FN、3→Mac）。
    /// </summary>
    private static KeycodeCategory LayerFunctions(int layers)
    {
        var cat = new KeycodeCategory("层功能");

        // 本分类的按钮宽度按「标签要完整显示」定，不按语义定：键帽单元上限压到 54 之后，
        // 1.5 单位的键只有 81px 内宽，放不下「按住固件层 1」（7 个字符）这类标签。
        // 同一行里的键宽度统一，行与行之间才不会有忽宽忽窄的观感。
        const double LayerKeyW = 2.4;
        const double TapKeyW = 3.2;   // LT 的标签最长（「点按 固件层 1 / 按住 Space」）

        cat.Rows.Add(new List<PaletteKey> { K("▽ 透传", Keycodes.KC_TRNS, LayerKeyW) });

        var to = new List<PaletteKey>();
        var mo = new List<PaletteKey>();
        var tg = new List<PaletteKey>();
        var osl = new List<PaletteKey>();
        var tt = new List<PaletteKey>();
        for (int l = 0; l < layers; l++)
        {
            to.Add(K(Keycodes.Describe((ushort)(QkTo + l)), (ushort)(QkTo + l), LayerKeyW));
            mo.Add(K(Keycodes.Describe((ushort)(QkMo + l)), (ushort)(QkMo + l), LayerKeyW));
            tg.Add(K(Keycodes.Describe((ushort)(QkTg + l)), (ushort)(QkTg + l), LayerKeyW));
            osl.Add(K(Keycodes.Describe((ushort)(QkOsl + l)), (ushort)(QkOsl + l), LayerKeyW));
            tt.Add(K(Keycodes.Describe((ushort)(QkTt + l)), (ushort)(QkTt + l), LayerKeyW));
        }
        cat.Rows.Add(to);
        cat.Rows.Add(mo);
        cat.Rows.Add(tg);
        cat.Rows.Add(osl);
        cat.Rows.Add(tt);

        // 单次修饰（OSM）：原来放在「修饰键」分类里，修饰键合并进基本键后挪到这里单独一行。
        // QMK 编码是 OSM(mod) = 0x52A0 | mod，修饰位沿用 MOD_LCTL/LSFT/LALT/LGUI = 1/2/4/8，
        // 所以是 0x52A1/0x52A2/0x52A4/0x52A8，**不是**连号的 0x52A0–0x52A3
        // （0x52A3 = Ctrl|Shift，显示成「单次 Ctrl+Shift」才对）。
        (string Label, ushort Mod)[] osms =
        {
            ("单次 Ctrl", 0x01), ("单次 Shift", 0x02), ("单次 Alt", 0x04), ("单次 Win", 0x08),
        };
        var osm = new List<PaletteKey>();
        foreach (var (label, mod) in osms)
            osm.Add(K(label, (ushort)(QkOsm | mod), LayerKeyW));
        cat.Rows.Add(osm);

        // LT(n, kc)：点按出键码、按住切层。只列最常用的几个基准键。
        (string Label, ushort Code)[] bases =
        {
            ("Space", 0x2C), ("Enter", 0x28), ("Esc", 0x29), ("Tab", 0x2B),
        };
        for (int l = 1; l < layers; l++)
        {
            var row = new List<PaletteKey>();
            foreach (var (_, code) in bases)
            {
                ushort lt = (ushort)(QkLayerTap | (l << 8) | code);
                row.Add(K(Keycodes.Describe(lt), lt, TapKeyW));
            }
            cat.Rows.Add(row);
        }

        return cat;
    }

    /// <summary>媒体键：RS8 Fn 层实际使用的多媒体键（中文名取自实机验证的 shortcuts.json）。</summary>
    private static KeycodeCategory Media() => new("媒体键",
        new List<PaletteKey>
        {
            K("静音", 0x00A8, 1.2), K("音量+", 0x00A9, 1.3), K("音量−", 0x00AA, 1.3),
            K("播放/暂停", 0x00AE, 1.6), K("下一曲", 0x00AB, 1.3), K("上一曲", 0x00AC, 1.3),
            K("播放器", 0x00AF, 1.3),
        },
        new List<PaletteKey>
        {
            K("邮件", 0x00B1, 1.2), K("计算器", 0x00B2, 1.4), K("主页", 0x00B5, 1.2),
            K("屏幕亮度+", 0x00BD, 1.9), K("屏幕亮度−", 0x00BE, 1.9),
            K("调度中心", 0x00C1, 1.6), K("启动台", 0x00C2, 1.3),
        });

    /// <summary>
    /// 宏：QK_MACRO_0 起的 16 个宏键码，按下即播放键盘宏缓冲里对应的那个宏。
    /// 宏内容本身在「宏」标签页里编辑，这里只是把触发键放上去。
    /// </summary>
    private static KeycodeCategory Macro()
    {
        var cat = new KeycodeCategory("宏");
        var row = new List<PaletteKey>();
        for (int i = 0; i < MacroCodec.MaxMacros; i++)
        {
            row.Add(K($"宏 {i + 1}", (ushort)(MacroCodec.QK_MACRO_0 + i), 1.5));
            if (row.Count == 8) { cat.Rows.Add(row); row = new List<PaletteKey>(); }
        }
        if (row.Count > 0) cat.Rows.Add(row);
        return cat;
    }

    /// <summary>
    /// 反查一个键码在调色板里的显示名。查不到返回 null（调用方再回退到 Keycodes.Describe）。
    /// 占位键（空 Label）与入口键（Action）不参与反查。
    /// </summary>
    public static string? LabelOf(IEnumerable<KeycodeCategory> categories, ushort code)
    {
        foreach (var cat in categories)
            foreach (var key in cat.AllKeys())
                if (!key.Action && key.Label.Length > 0 && key.Code == code) return key.Label;
        return null;
    }
}
