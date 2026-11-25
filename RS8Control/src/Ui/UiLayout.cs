namespace RS8Control.Ui;

/// <summary>
/// 界面排版的公共约定。
///
/// 背景：各页顶栏里的控件 y 坐标原来都是手写死的（12 / 15 / 16 / 19 / 24 …），
/// 同一栏里「标签 / 下拉框 / 按钮 / 提示文字」各偏各的 —— 实测键位映射页提示文字
/// 比下拉框高了 11px，主窗口里「只读」比状态文字低了 3px。这里统一成
/// 「按容器（或行）高度算垂直中心」一条规则，所有顶栏都走它。
/// </summary>
internal static class UiLayout
{
    /// <summary>
    /// 单行顶栏的标准高度。所有单行顶栏统一用它，切换标签页时顶栏不会跳高。
    /// （原来 44 / 46 / 52 / 56 各不一样，切页时能明显看到内容上下窜。）
    /// 取 64 是因为栏内最高的元素是按钮（实测 PreferredSize.Height = 37），
    /// 上下各留 13px，与常见工具条的留白一致。
    /// </summary>
    public const int BarHeight = 64;

    /// <summary>两行顶栏（主行 + 提示行）的标准高度：键位映射页、原始调试页。</summary>
    public const int TallBarHeight = 92;

    /// <summary>主窗口顶栏：状态行 + 明细行。</summary>
    public const int MainBarHeight = 72;

    /// <summary>栏底那条 1px 分隔线占掉的高度，算垂直居中时要先扣掉。</summary>
    public const int Separator = 1;

    /// <summary>两行栏里「主行」与「提示行」之间的间距。</summary>
    public const int RowGap = 6;

    /// <summary>按控件 DPI 缩放后的单行顶栏高度。</summary>
    public static int BarHeightFor(Control c) => Theme.Scale(c, BarHeight);

    /// <summary>按控件 DPI 缩放后的两行顶栏高度。</summary>
    public static int TallBarHeightFor(Control c) => Theme.Scale(c, TallBarHeight);

    /// <summary>按控件 DPI 缩放后的主窗口顶栏高度。</summary>
    public static int MainBarHeightFor(Control c) => Theme.Scale(c, MainBarHeight);

    private static int ChildHeight(Control c) => c.AutoSize ? c.PreferredSize.Height : c.Height;

    /// <summary>把控件在容器内垂直居中放置，返回实际 y。</summary>
    public static int CenterVertically(Control child, int containerHeight, int topOffset = 0)
        => topOffset + Math.Max(0, (containerHeight - topOffset - ChildHeight(child)) / 2);

    /// <summary>在 [rowTop, rowTop + rowHeight) 这一行内垂直居中，返回实际 y。</summary>
    public static int CenterInRow(Control child, int rowTop, int rowHeight)
        => rowTop + Math.Max(0, (rowHeight - ChildHeight(child)) / 2);

    /// <summary>把控件在整条栏内垂直居中（自动扣掉栏底那条 1px 分隔线）。</summary>
    public static int CenterInBar(Control child, int barHeight)
        => CenterVertically(child, Math.Max(0, barHeight - Separator));

    /// <summary>
    /// 一行里最高的控件高度。用 PreferredSize 而不是 Height —— AutoSize 控件在
    /// 布局跑过之前 Height 还是旧值（构造期尤其明显）。
    /// </summary>
    public static int RowHeight(params Control[] children)
    {
        int h = 0;
        foreach (var c in children) h = Math.Max(h, c.PreferredSize.Height + c.Margin.Vertical);
        return h;
    }

    /// <summary>
    /// 两行栏的排布：把「主行 + 间距 + 提示行」整块在栏内垂直居中，
    /// 返回两行各自的位置与行高，调用方再用 <see cref="CenterInRow"/> 摆具体控件。
    /// </summary>
    public static (int MainTop, int MainHeight, int HintTop, int HintHeight) TwoRowBar(
        int barHeight, int mainRowHeight, int hintRowHeight, int gap = RowGap)
    {
        int avail = Math.Max(0, barHeight - Separator);
        int block = mainRowHeight + gap + hintRowHeight;
        int top = Math.Max(0, (avail - block) / 2);
        return (top, mainRowHeight, top + mainRowHeight + gap, hintRowHeight);
    }

    /// <summary>
    /// Label 里「一行文字」占的高度。不能拿 TextRenderer.MeasureText 的结果顶替 ——
    /// 实测同一字体下 TextRenderer 量到 17，而 Label 的 AutoSize 高度是 20（Label 自己多算 3px）；
    /// 差这 3px 会让「按两行预留」的行高把第二行裁掉一半。
    /// 这里用一个同字体的尺子 Label 直接问它，不写死那个偏移量。
    /// </summary>
    public static int LabelLineHeight(Font font)
    {
        using var ruler = new Label { Font = font, AutoSize = true, Text = "Ag" };
        return ruler.PreferredSize.Height;
    }

    /// <summary>
    /// Dock=Right / Dock=Left 的按钮条：FlowLayoutPanel 的子控件一律从 Padding 开始摆，
    /// 所以「把按钮条在栏内垂直居中」等价于「算出正确的 Padding.Top」。
    /// </summary>
    public static void CenterFlowBar(FlowLayoutPanel flow, int barHeight)
        => CenterFlowRow(flow, 0, Math.Max(0, barHeight - Separator));

    /// <summary>同上，但只在 [rowTop, rowTop + rowHeight) 这一行内居中。</summary>
    public static void CenterFlowRow(FlowLayoutPanel flow, int rowTop, int rowHeight)
    {
        int h = 0;
        foreach (Control c in flow.Controls)
            h = Math.Max(h, ChildHeight(c) + c.Margin.Vertical);
        flow.Padding = new Padding(flow.Padding.Left,
            rowTop + Math.Max(0, (rowHeight - h) / 2), flow.Padding.Right, 0);
    }

    /// <summary>
    /// 按钮条里比按钮矮的控件（复选框等）要和按钮同中心：FlowLayoutPanel 只按
    /// Padding + Margin 起摆，不会自己居中，矮控件会贴着行的顶部。
    /// 做法是给它的 Margin.Top 补上高度差的一半。
    /// </summary>
    public static void AlignInFlow(FlowLayoutPanel flow, Control child, int leftMargin, int rightMargin)
    {
        int tallest = 0;
        foreach (Control c in flow.Controls)
            if (!ReferenceEquals(c, child)) tallest = Math.Max(tallest, ChildHeight(c));
        child.Margin = new Padding(leftMargin, Math.Max(0, (tallest - ChildHeight(child)) / 2), rightMargin, 0);
    }
}
