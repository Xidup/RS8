using System.Text.Json;

namespace RS8Control.Model;

/// <summary>键位布局中的一个按键（来自 VIA 定义的 layouts.keymap）。</summary>
public sealed class LayoutKey
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double W2 { get; set; } = 1;
    public double H2 { get; set; } = 1;
    /// <summary>矩阵坐标 "row,col"；含换行符表示带布局选项（如 ISO/ANSI）。</summary>
    public string Matrix { get; set; } = "";
    public int Row { get; set; }
    public int Col { get; set; }
    public string Label { get; set; } = "";

    /// <summary>布局选项组序号（矩阵串第 4 段 "选项,选择" 的前半）；无后缀为 -1。</summary>
    public int OptionIndex { get; set; } = -1;

    /// <summary>该键要求的选项选择值；仅当对应选项组等于此值时才显示。无后缀为 -1。</summary>
    public int ChoiceIndex { get; set; } = -1;
}

/// <summary>VIA 灯效菜单里的一个控件。</summary>
public sealed class LightControl
{
    public string Label { get; set; } = "";
    public string Type { get; set; } = "";      // range / dropdown / color
    public string ShowIf { get; set; } = "";
    public int Min { get; set; }
    public int Max { get; set; } = 255;
    public byte Channel { get; set; }
    public byte ValueId { get; set; }
    public List<(string Name, int Value)> Options { get; } = new();
}

public sealed class LightGroup
{
    public string Label { get; set; } = "";
    public List<LightControl> Controls { get; } = new();
}

/// <summary>VIA 键盘定义（官方提供的 JSON）。</summary>
public sealed class ViaDefinition
{
    public string Name { get; set; } = "未命名";
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public int Rows { get; set; }
    public int Cols { get; set; }
    public List<LayoutKey> Keys { get; } = new();
    public List<LightGroup> LightGroups { get; } = new();
    public List<string> CustomKeycodes { get; } = new();
    public string SourcePath { get; set; } = "";

    public static ViaDefinition Load(string path)
    {
        var def = new ViaDefinition { SourcePath = path };
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (root.TryGetProperty("name", out var n)) def.Name = n.GetString() ?? def.Name;
        if (root.TryGetProperty("vendorId", out var v)) def.VendorId = ParseHex(v.GetString());
        if (root.TryGetProperty("productId", out var p)) def.ProductId = ParseHex(p.GetString());

        if (root.TryGetProperty("matrix", out var m))
        {
            if (m.TryGetProperty("rows", out var r)) def.Rows = r.GetInt32();
            if (m.TryGetProperty("cols", out var c)) def.Cols = c.GetInt32();
        }

        if (root.TryGetProperty("customKeycodes", out var ck) && ck.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ck.EnumerateArray())
            {
                string nm = item.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
                string title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                def.CustomKeycodes.Add(string.IsNullOrEmpty(title) ? nm : $"{nm} — {title}");
            }
        }

        // ---- 布局 ----
        if (root.TryGetProperty("layouts", out var layouts) && layouts.TryGetProperty("keymap", out var keymap)
            && keymap.ValueKind == JsonValueKind.Array)
        {
            double x = 0, y = 0, w = 1, h = 1;
            // 第二矩形（ISO 高 Enter 的下半段等），ANSI 下用不到，但解析必须正确
            double x2 = 0, y2 = 0, w2 = 1, h2 = 1;
            foreach (var rowEl in keymap.EnumerateArray())
            {
                foreach (var cell in rowEl.EnumerateArray())
                {
                    if (cell.ValueKind == JsonValueKind.Object)
                    {
                        if (cell.TryGetProperty("x", out var xEl)) x += xEl.GetDouble();
                        if (cell.TryGetProperty("y", out var yEl)) y += yEl.GetDouble();
                        if (cell.TryGetProperty("w", out var wEl)) w = wEl.GetDouble();
                        if (cell.TryGetProperty("h", out var hEl)) h = hEl.GetDouble();
                        if (cell.TryGetProperty("x2", out var x2El)) x2 = x2El.GetDouble();
                        if (cell.TryGetProperty("y2", out var y2El)) y2 = y2El.GetDouble();
                        if (cell.TryGetProperty("w2", out var w2El)) w2 = w2El.GetDouble();
                        if (cell.TryGetProperty("h2", out var h2El)) h2 = h2El.GetDouble();
                        continue;
                    }
                    if (cell.ValueKind != JsonValueKind.String) continue;

                    // 矩阵串形如 "row,col\n<标签>\n\n<选项序号>,<选择值>"。
                    // 第 4 段（可能不存在或为空）声明该键只在对应布局选项取到指定选择值时才存在 ——
                    // RS8 的 keymap 里 Enter 同时给了 ISO（选择 1）与 ANSI（选择 0）两份定义。
                    string matrix = cell.GetString() ?? "";
                    var segs = matrix.Split('\n');
                    var parts = segs[0].Split(',');
                    if (parts.Length != 2) continue;
                    if (!int.TryParse(parts[0], out int kr) || !int.TryParse(parts[1], out int kc)) continue;

                    int optIdx = -1, choiceIdx = -1;
                    if (segs.Length > 3 && segs[3].Length > 0)
                    {
                        var opt = segs[3].Split(',');
                        if (opt.Length == 2 && int.TryParse(opt[0], out int oi) && int.TryParse(opt[1], out int ci))
                        {
                            optIdx = oi;
                            choiceIdx = ci;
                        }
                    }

                    // 键盘实测 layout_options = 0（所有选项组都取选择 0），故只保留选择 0 的键；
                    // 无后缀（choiceIdx == -1）的键始终保留。被丢弃的键仍要推进 x，否则后续键整体错位。
                    if (choiceIdx > 0)
                    {
                        x += w;
                        w = 1; h = 1; x2 = 0; y2 = 0; w2 = 1; h2 = 1;
                        continue;
                    }

                    def.Keys.Add(new LayoutKey
                    {
                        X = x, Y = y, W = w, H = h,
                        X2 = x + x2, Y2 = y + y2, W2 = w2, H2 = h2,
                        Row = kr, Col = kc, Matrix = matrix,
                        OptionIndex = optIdx, ChoiceIndex = choiceIdx,
                    });
                    x += w;
                    w = 1; h = 1; x2 = 0; y2 = 0; w2 = 1; h2 = 1;
                }
                // 换行：回到最左
                x = 0;
                y += 1;
                w = 1; h = 1; x2 = 0; y2 = 0; w2 = 1; h2 = 1;
            }
        }

        // ---- 灯效菜单 ----
        if (root.TryGetProperty("menus", out var menus) && menus.ValueKind == JsonValueKind.Array)
        {
            foreach (var menu in menus.EnumerateArray())
            {
                if (!menu.TryGetProperty("content", out var content)) continue;
                foreach (var grpEl in content.EnumerateArray())
                {
                    var grp = new LightGroup
                    {
                        Label = grpEl.TryGetProperty("label", out var gl) ? gl.GetString() ?? "" : "",
                    };
                    if (grpEl.TryGetProperty("content", out var ctrls) && ctrls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cEl in ctrls.EnumerateArray())
                        {
                            var lc = new LightControl
                            {
                                Label = cEl.TryGetProperty("label", out var l2) ? l2.GetString() ?? "" : "",
                                Type = cEl.TryGetProperty("type", out var t2) ? t2.GetString() ?? "" : "",
                                ShowIf = cEl.TryGetProperty("showIf", out var s2) ? s2.GetString() ?? "" : "",
                            };
                            if (cEl.TryGetProperty("content", out var c2) && c2.ValueKind == JsonValueKind.Array && c2.GetArrayLength() >= 3)
                            {
                                lc.Channel = (byte)c2[1].GetInt32();
                                lc.ValueId = (byte)c2[2].GetInt32();
                            }
                            if (cEl.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                            {
                                if (opts.GetArrayLength() >= 2 && opts[0].ValueKind == JsonValueKind.Number)
                                {
                                    lc.Min = opts[0].GetInt32();
                                    lc.Max = opts[1].GetInt32();
                                }
                                else
                                {
                                    foreach (var o in opts.EnumerateArray())
                                        if (o.ValueKind == JsonValueKind.Array && o.GetArrayLength() >= 2)
                                            lc.Options.Add((o[0].GetString() ?? "", o[1].GetInt32()));
                                }
                            }
                            grp.Controls.Add(lc);
                        }
                    }
                    def.LightGroups.Add(grp);
                }
            }
        }

        return def;
    }

    /// <summary>自动在 defs/ 目录里找定义文件。</summary>
    public static ViaDefinition? AutoLoad()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "defs");
        if (!Directory.Exists(dir)) return null;

        string preferred = Path.Combine(dir, "RS8_via.json");
        if (File.Exists(preferred)) return Load(preferred);

        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(s => s))
        {
            if (IsViaDefinitionFile(f))
                return Load(f);
        }
        return null;
    }

    private static bool IsViaDefinitionFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("layouts", out _)
                && root.TryGetProperty("matrix", out _);
        }
        catch
        {
            return false;
        }
    }

    private static ushort ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : (ushort)0;
    }
}
