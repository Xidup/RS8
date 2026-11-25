namespace RS8Control.Model;

/// <summary>
/// 灯效英文标识符 → 中文名的翻译表。
///
/// 标识符直接来自官方 VIA 定义里 dropdown 的选项名（如 <c>hue_wave</c>），
/// 因此不需要按通道/序号硬编码分组：同一标识符在任何通道下译名都相同。
/// 查不到时回退显示原始英文，绝不返回空串。
/// </summary>
public static class EffectNames
{
    /// <summary>
    /// 正面 Logo 灯与侧边灯共用的 7 项效果表。
    /// （JSON 里这两个分组的 dropdown 选项完全一致。）
    /// </summary>
    private static readonly Dictionary<string, string> Logo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "关闭",
        ["wave"] = "波浪",
        ["fixed wave"] = "固定波浪",
        ["fixed_wave"] = "固定波浪",
        ["spectrum"] = "光谱流转",
        ["breathe"] = "呼吸",
        ["light"] = "常亮",
        ["shutdown"] = "熄灭",
    };

    /// <summary>主键区背光的 46 项效果表。</summary>
    private static readonly Dictionary<string, string> Backlight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "关闭",
        ["solid_color"] = "单色",
        ["alphas_mods"] = "字母/修饰高亮",
        ["gradient_up_down"] = "上下渐变",
        ["gradient_left_right"] = "左右渐变",
        ["breathing"] = "呼吸",
        ["band_sat"] = "色带·饱和度",
        ["band_val"] = "色带·亮度",
        ["band_pinwheel_sat"] = "风车色带·饱和度",
        ["band_pinwheel_val"] = "风车色带·亮度",
        ["band_spiral_sat"] = "螺旋色带·饱和度",
        ["band_spiral_val"] = "螺旋色带·亮度",
        ["cycle_all"] = "整体循环",
        ["cycle_left_right"] = "左右循环",
        ["cycle_up_down"] = "上下循环",
        ["cycle_out_in"] = "内外循环",
        ["cycle_out_in_dual"] = "内外循环·双向",
        ["rainbow_moving_chevron"] = "彩虹箭形",
        ["cycle_pinwheel"] = "风车循环",
        ["cycle_spiral"] = "螺旋循环",
        ["dual_beacon"] = "双灯塔",
        ["rainbow_beacon"] = "彩虹灯塔",
        ["rainbow_pinwheels"] = "彩虹风车",
        ["flower_blooming"] = "绽放",
        ["raindrops"] = "雨滴",
        ["jellybean_raindrops"] = "彩豆雨滴",
        ["hue_breathing"] = "色相呼吸",
        ["hue_pendulum"] = "色相摆动",
        ["hue_wave"] = "色相波浪",
        ["pixel_flow"] = "像素流",
        ["digital_rain"] = "数字雨",
        ["solid_reactive"] = "单色涟漪",
        ["solid_reactive_wide"] = "单色涟漪·宽",
        ["solid_reactive_multiwide"] = "单色涟漪·多宽",
        ["solid_reactive_cross"] = "单色十字",
        ["solid_reactive_multicross"] = "单色多十字",
        ["solid_reactive_nexus"] = "单色枢纽",
        ["solid_reactive_multinexus"] = "单色多枢纽",
        ["splash"] = "水花",
        ["multisplash"] = "多水花",
        ["solid_splash"] = "单色水花",
        ["solid_multisplash"] = "单色多水花",
        ["starlight"] = "星光",
        ["starlight_dual_hue"] = "星光·双色相",
        ["starlight_dual_sat"] = "星光·双饱和",
        ["riverflow"] = "河流",
    };

    /// <summary>
    /// 翻译一个效果标识符。先查 46 项表，再查 7 项表（两张表同名的项译名一致，顺序无关）；
    /// 都查不到时返回去掉下划线的原始英文，保证界面上永远有字。
    /// </summary>
    public static string Translate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        string key = raw.Trim();
        if (Backlight.TryGetValue(key, out var zh)) return zh;
        if (Logo.TryGetValue(key, out zh)) return zh;
        // 兜底：把 snake_case 变成空格分隔，仍比空白可读
        return key.Replace('_', ' ');
    }
}
