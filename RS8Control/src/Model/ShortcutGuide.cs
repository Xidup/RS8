using System.Text.Json;

namespace RS8Control.Model;

/// <summary>某个键码对应的中文名与功能分组（来自 defs/shortcuts.json 的 keycodeNames）。</summary>
public sealed class ShortcutEntry
{
    public string Zh { get; init; } = "";
    public string Group { get; init; } = "";
}

/// <summary>说明书「多媒体快捷组合键」表的一行。</summary>
public sealed class ManualFnRow
{
    public string Keys { get; init; } = "";
    public string Win { get; init; } = "";
    public string Mac { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>说明书「灯光控制」表的一行。</summary>
public sealed class ManualLightRow
{
    public string Keys { get; init; } = "";
    public string Desc { get; init; } = "";
}

/// <summary>说明书的一个章节。</summary>
public sealed class GuideSection
{
    public string Title { get; init; } = "";
    public List<string> Items { get; } = new();
}

/// <summary>说明书的一条问答。</summary>
public sealed class GuideFaq
{
    public string Q { get; init; } = "";
    public string A { get; init; } = "";
}

/// <summary>
/// 说明书内容（defs/shortcuts.json）。
///
/// 这份 JSON 由说明书逐字整理而来，<see cref="KeycodeNames"/> 则是把实机读出的第 2 层（Fn 层）
/// 键码翻译成中文的对照表 —— 两者都是**数据**，界面只负责渲染，不在代码里再写一份。
/// </summary>
public sealed class ShortcutGuide
{
    /// <summary>说明书写明的 Fn 层号；实机优先用层 0 里 MO(n) 键推算出来的值。</summary>
    public int FnLayerIndex { get; private set; } = 2;

    public int MatrixRows { get; private set; } = 6;
    public int MatrixCols { get; private set; } = 16;

    /// <summary>键码 → 中文名 + 分组。</summary>
    public Dictionary<ushort, ShortcutEntry> KeycodeNames { get; } = new();

    public List<ManualFnRow> ManualFnTable { get; } = new();
    public List<ManualLightRow> ManualLightTable { get; } = new();
    public string ManualLightNote { get; private set; } = "";

    public List<GuideSection> Sections { get; } = new();
    public List<GuideFaq> Faqs { get; } = new();

    /// <summary>说明书与实机固件不一致的条目（_manualVsFirmware.差异）。</summary>
    public List<string> ManualVsFirmware { get; } = new();

    public string SourcePath { get; private set; } = "";

    /// <summary>查一个键码的中文名与分组；查不到返回 null。</summary>
    public ShortcutEntry? Lookup(ushort kc) => KeycodeNames.TryGetValue(kc, out var e) ? e : null;

    public static ShortcutGuide? AutoLoad()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "defs", "shortcuts.json");
        return File.Exists(path) ? Load(path) : null;
    }

    public static ShortcutGuide Load(string path)
    {
        var guide = new ShortcutGuide { SourcePath = path };
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        guide.FnLayerIndex = ReadInt(root, "fnLayerIndex", guide.FnLayerIndex);
        if (root.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            guide.MatrixRows = ReadInt(m, "rows", guide.MatrixRows);
            guide.MatrixCols = ReadInt(m, "cols", guide.MatrixCols);
        }

        if (root.TryGetProperty("keycodeNames", out var names) && names.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in names.EnumerateObject())
            {
                if (!TryParseKeycode(p.Name, out ushort kc)) continue;
                guide.KeycodeNames[kc] = new ShortcutEntry
                {
                    Zh = Str(p.Value, "zh"),
                    Group = Str(p.Value, "group"),
                };
            }
        }

        if (root.TryGetProperty("manualFnTable", out var fn) && fn.ValueKind == JsonValueKind.Array)
            foreach (var e in fn.EnumerateArray())
                guide.ManualFnTable.Add(new ManualFnRow
                {
                    Keys = Str(e, "keys"), Win = Str(e, "win"), Mac = Str(e, "mac"), Note = Str(e, "note"),
                });

        if (root.TryGetProperty("manualLightTable", out var lt) && lt.ValueKind == JsonValueKind.Array)
            foreach (var e in lt.EnumerateArray())
                guide.ManualLightTable.Add(new ManualLightRow { Keys = Str(e, "keys"), Desc = Str(e, "desc") });

        guide.ManualLightNote = Str(root, "manualLightNote");

        if (root.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in secs.EnumerateArray())
            {
                var section = new GuideSection { Title = Str(s, "title") };
                if (s.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var it in items.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String) section.Items.Add(it.GetString() ?? "");
                guide.Sections.Add(section);
            }
        }

        if (root.TryGetProperty("faq", out var faq) && faq.ValueKind == JsonValueKind.Array)
            foreach (var f in faq.EnumerateArray())
                guide.Faqs.Add(new GuideFaq { Q = Str(f, "q"), A = Str(f, "a") });

        if (root.TryGetProperty("_manualVsFirmware", out var diff) && diff.ValueKind == JsonValueKind.Object
            && diff.TryGetProperty("差异", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var d in list.EnumerateArray())
                if (d.ValueKind == JsonValueKind.String) guide.ManualVsFirmware.Add(d.GetString() ?? "");

        return guide;
    }

    private static int ReadInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>解析 "0x7E00" 形式的键码。</summary>
    private static bool TryParseKeycode(string s, out ushort kc)
    {
        kc = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out kc);
    }
}
