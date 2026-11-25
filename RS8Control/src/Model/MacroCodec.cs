using System.Globalization;

namespace RS8Control.Model;

/// <summary>宏里的一步操作。</summary>
public abstract record MacroStep;

/// <summary>逐字打出来的一段文本（只能放可打印 ASCII）。</summary>
public sealed record MacroText(string Text) : MacroStep;

/// <summary>一个键的按下 / 松开 / 点按。<paramref name="Keycode"/> 是 HID usage 单字节。</summary>
public sealed record MacroKey(byte Keycode, MacroKeyKind Kind) : MacroStep;

/// <summary>延时若干毫秒。</summary>
public sealed record MacroDelay(int Ms) : MacroStep;

public enum MacroKeyKind { Tap, Down, Up }

/// <summary>
/// VIA 宏缓冲的编解码。纯逻辑、不做任何 IO，方便单独验证。
///
/// 固件里那块缓冲的布局（实机 0x0C/0x0D/0x0E 读出来确认过）：
/// <code>
/// [宏0 的内容字节…] 0x00 [宏1 的内容字节…] 0x00 … [宏15 的内容字节…] 0x00 … 补 0 到 338 字节
/// </code>
/// 每个宏以 0x00 结尾，第 N 个宏 = 跳过 N 个 0x00 之后开始。
/// <b>整个缓冲的最后一个字节必须是 0x00</b> —— 固件靠它判断上次写入是否完整，
/// 非 0 就拒绝播放任何宏（见 <see cref="TryBuildBuffer"/> 末尾的断言）。
///
/// 宏内容用的是 QMK <c>send_string</c> 的字节流：
/// <list type="bullet">
///   <item>0x20–0x7E 可打印 ASCII → 直接打出该字符</item>
///   <item><c>0x01 0x01 &lt;键码&gt;</c> → 点按；<c>0x02</c> 按下；<c>0x03</c> 松开</item>
///   <item><c>0x01 0x04 &lt;十进制数字的 ASCII&gt; 0x7C('|')</c> → 延时该毫秒数</item>
/// </list>
/// 0x01 是转义前缀（SS_QMK_PREFIX），0x00 是宏结束符 —— 两者都不能裸出现在宏内容里。
/// </summary>
public static class MacroCodec
{
    /// <summary>触发第 1 个宏的键码 QK_MACRO_0，宏 n（从 0 起）= 0x7700 + n。</summary>
    public const ushort QK_MACRO_0 = 0x7700;

    /// <summary>宏数量（RS8 固件 0x0C 声明为 16）。</summary>
    public const int MaxMacros = 16;

    /// <summary>宏缓冲字节数（RS8 固件 0x0D 声明为 338）。离线时用它做预览/编码。</summary>
    public const int DefaultBufferSize = 338;

    /// <summary>转义前缀 SS_QMK_PREFIX。</summary>
    public const byte Prefix = 0x01;

    public const byte TapCode = 0x01;
    public const byte DownCode = 0x02;
    public const byte UpCode = 0x03;
    public const byte DelayCode = 0x04;

    /// <summary>延时数字的结束符 '|'。</summary>
    public const byte DelayTerminator = 0x7C;

    public const int MinDelayMs = 1;
    public const int MaxDelayMs = 65535;

    /// <summary>可打印 ASCII 区间。</summary>
    private const byte MinPrintable = 0x20;
    private const byte MaxPrintable = 0x7E;

    // ============================================================
    //  编码
    // ============================================================

    /// <summary>
    /// 把步骤序列编码成 send_string 字节流（不含结尾的 0x00）。
    /// 出现无法编码的内容时抛 <see cref="ArgumentException"/>，消息里说明是哪一步、为什么。
    /// </summary>
    public static byte[] Encode(IReadOnlyList<MacroStep> steps)
    {
        if (!TryEncode(steps, out byte[] bytes, out string error))
            throw new ArgumentException(error, nameof(steps));
        return bytes;
    }

    /// <summary>不抛异常的编码。失败时 <paramref name="error"/> 说明原因（绝不静默丢弃内容）。</summary>
    public static bool TryEncode(IReadOnlyList<MacroStep> steps, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        if (steps == null)
        {
            error = "步骤列表为空。";
            return false;
        }

        var buf = new List<byte>(steps.Count * 4);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            switch (step)
            {
                case MacroText t:
                    foreach (char c in t.Text ?? "")
                    {
                        if (c < MinPrintable || c > MaxPrintable)
                        {
                            error = $"第 {i + 1} 步「打字」里有不可打印的字符 '{Show(c)}'（U+{(int)c:X4}）："
                                  + "宏只能发送 0x20–0x7E 的可打印 ASCII，中文、换行、Tab 都放不进去。";
                            return false;
                        }
                        buf.Add((byte)c);
                    }
                    break;

                case MacroDelay d:
                {
                    // 毫秒夹到 1–65535：0 会让固件空转，超上限则会撑爆缓冲
                    int ms = Math.Clamp(d.Ms, MinDelayMs, MaxDelayMs);
                    buf.Add(Prefix);
                    buf.Add(DelayCode);
                    foreach (char c in ms.ToString(CultureInfo.InvariantCulture)) buf.Add((byte)c);
                    buf.Add(DelayTerminator);
                    break;
                }

                case MacroKey k:
                {
                    // 键码 0x00 = KC_NO，写进宏会被固件当成"宏到此结束"，必须拒绝
                    if (k.Keycode == 0x00)
                    {
                        error = $"第 {i + 1} 步「按键」的键码是 0x00（无键码）：0x00 是宏的结束符，不能放进宏里。";
                        return false;
                    }
                    buf.Add(Prefix);
                    buf.Add(k.Kind switch
                    {
                        MacroKeyKind.Tap => TapCode,
                        MacroKeyKind.Down => DownCode,
                        _ => UpCode,
                    });
                    buf.Add(k.Keycode);
                    break;
                }

                default:
                    error = $"第 {i + 1} 步是不认识的步骤类型 {step?.GetType().Name ?? "<null>"}。";
                    return false;
            }
        }

        bytes = buf.ToArray();
        return true;
    }

    /// <summary>把字符显示成人能看懂的样子（控制字符用 ^X 形式）。</summary>
    private static string Show(char c) =>
        c < 0x20 || c == 0x7F ? "^" + (char)(c + 0x40) : c.ToString();

    // ============================================================
    //  解码
    // ============================================================

    /// <summary>
    /// 把 send_string 字节流解码回步骤序列，用于展示键盘上已有的宏。
    /// 遇到无法识别的转义序列、裸控制字节或 0x00 就停下，保留已解析出来的部分 —— 不抛异常。
    /// 连续的文本字节会合并成**一个** <see cref="MacroText"/>（所以"两个相邻的 MacroText"
    /// 往返后会变成一个；空文本不会产生字节，也就不会还原）。
    /// </summary>
    public static List<MacroStep> Decode(ReadOnlySpan<byte> bytes)
    {
        var steps = new List<MacroStep>();
        var text = new StringBuilder();
        int i = 0;

        void FlushText()
        {
            if (text.Length == 0) return;
            steps.Add(new MacroText(text.ToString()));
            text.Clear();
        }

        while (i < bytes.Length)
        {
            byte b = bytes[i];

            if (b == 0x00) break;                      // 宏结束符

            if (b == Prefix)
            {
                if (i + 1 >= bytes.Length) break;      // 转义不完整
                byte code = bytes[i + 1];

                if (code is TapCode or DownCode or UpCode)
                {
                    if (i + 2 >= bytes.Length) break;
                    FlushText();
                    steps.Add(new MacroKey(bytes[i + 2], code switch
                    {
                        TapCode => MacroKeyKind.Tap,
                        DownCode => MacroKeyKind.Down,
                        _ => MacroKeyKind.Up,
                    }));
                    i += 3;
                    continue;
                }

                if (code == DelayCode)
                {
                    int j = i + 2, ms = 0;
                    bool anyDigit = false, terminated = false;
                    while (j < bytes.Length)
                    {
                        byte c = bytes[j];
                        if (c == DelayTerminator) { terminated = true; j++; break; }
                        if (c < '0' || c > '9') break;
                        anyDigit = true;
                        ms = Math.Min(ms * 10 + (c - '0'), MaxDelayMs);   // 顺便防溢出
                        j++;
                    }
                    if (!terminated || !anyDigit) break;   // 数字不合法 / 没有结束符
                    FlushText();
                    steps.Add(new MacroDelay(Math.Max(ms, MinDelayMs)));
                    i = j;
                    continue;
                }

                break;   // 不认识的转义码
            }

            if (b < MinPrintable || b > MaxPrintable) break;   // 裸控制字节

            text.Append((char)b);
            i++;
        }

        FlushText();
        return steps;
    }

    // ============================================================
    //  整块缓冲
    // ============================================================

    /// <summary>
    /// 把多个宏拼成完整缓冲（各宏以 0x00 结尾，补 0 到 <paramref name="bufferSize"/>，
    /// 末字节保证为 0）。宏个数超上限、内容里混进 0x00、或总长度超缓冲都会失败并说明原因，
    /// 绝不悄悄截断。
    /// </summary>
    public static bool TryBuildBuffer(IReadOnlyList<byte[]> macros, int bufferSize,
                                      out byte[] buffer, out string error)
    {
        buffer = Array.Empty<byte>();
        error = "";

        if (bufferSize <= 0)
        {
            error = $"缓冲大小 {bufferSize} 非法。";
            return false;
        }
        if (macros == null)
        {
            error = "宏列表为空。";
            return false;
        }
        if (macros.Count > MaxMacros)
        {
            error = $"最多 {MaxMacros} 个宏，收到 {macros.Count} 个。";
            return false;
        }

        int need = 0;
        for (int i = 0; i < macros.Count; i++)
        {
            var m = macros[i] ?? Array.Empty<byte>();
            for (int j = 0; j < m.Length; j++)
            {
                if (m[j] == 0x00)
                {
                    error = $"宏 {i + 1} 的内容里第 {j + 1} 字节是 0x00：它是宏的结束符，不能出现在内容里。";
                    return false;
                }
            }
            need += m.Length + 1;
        }

        if (need > bufferSize)
        {
            error = $"内容共需 {need} 字节（含每个宏的结束符），超过缓冲上限 {bufferSize} 字节。"
                  + "请把宏改短一些，或拆到别的宏里。";
            return false;
        }

        buffer = new byte[bufferSize];
        int p = 0;
        for (int i = 0; i < macros.Count; i++)
        {
            var m = macros[i] ?? Array.Empty<byte>();
            Array.Copy(m, 0, buffer, p, m.Length);
            p += m.Length;
            buffer[p++] = 0x00;   // 每个宏的结束符
        }

        // 固件会检查整个缓冲的最后一个字节：非 0 就认为上次写入被中断，拒绝播放任何宏。
        if (buffer[bufferSize - 1] != 0x00)
        {
            buffer = Array.Empty<byte>();
            error = "内部错误：缓冲末字节不是 0x00，固件会拒绝播放全部宏。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 把完整缓冲拆回各个宏的内容字节（不含各自的结尾 0x00）。
    /// 缓冲提前用尽时，剩下的宏返回空数组，返回值长度始终是 <paramref name="macroCount"/>。
    /// </summary>
    public static List<byte[]> SplitBuffer(byte[] buffer, int macroCount)
    {
        var list = new List<byte[]>(Math.Max(0, macroCount));
        int p = 0;
        for (int i = 0; i < macroCount; i++)
        {
            int start = p;
            while (p < buffer.Length && buffer[p] != 0x00) p++;
            list.Add(buffer[start..p]);
            if (p < buffer.Length) p++;   // 跳过结尾 0x00
        }
        return list;
    }

    // ============================================================
    //  展示
    // ============================================================

    /// <summary>一步的可读描述，用于步骤列表，如 <c>打字 "hello"</c> / <c>延时 100 ms</c> / <c>点按 Ctrl</c>。</summary>
    public static string DescribeStep(MacroStep step) => step switch
    {
        MacroText t => $"打字 \"{t.Text}\"",
        MacroDelay d => $"延时 {Math.Clamp(d.Ms, MinDelayMs, MaxDelayMs)} ms",
        MacroKey k => $"{KindName(k.Kind)} {Keycodes.Describe(k.Keycode)}",
        _ => "?",
    };

    /// <summary>整段宏的一行预览，如 <c>hello&lt;100ms 延时&gt;Ctrl</c>。</summary>
    public static string Describe(IReadOnlyList<MacroStep> steps)
    {
        if (steps == null || steps.Count == 0) return "（空宏）";

        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            switch (step)
            {
                case MacroText t:
                    sb.Append(t.Text);
                    break;
                case MacroDelay d:
                    sb.Append('<').Append(Math.Clamp(d.Ms, MinDelayMs, MaxDelayMs)).Append("ms 延时>");
                    break;
                case MacroKey k when k.Kind == MacroKeyKind.Tap:
                    sb.Append(Keycodes.Describe(k.Keycode));
                    break;
                case MacroKey k:
                    sb.Append(KindName(k.Kind)).Append(' ').Append(Keycodes.Describe(k.Keycode));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string KindName(MacroKeyKind kind) => kind switch
    {
        MacroKeyKind.Tap => "点按",
        MacroKeyKind.Down => "按下",
        _ => "松开",
    };
}
