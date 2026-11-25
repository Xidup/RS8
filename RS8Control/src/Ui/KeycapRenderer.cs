namespace RS8Control.Ui;

/// <summary>
/// 键帽共享无分配绘制器（供 <c>KeyboardCanvas</c>、<c>FnKeyCanvas</c>、<c>PaletteCanvas</c> 复用）：
/// 几何路径与画笔/画刷走 <see cref="Theme"/> 缓存，文字走 <see cref="TextRenderer"/>，
/// 不在 <c>OnPaint</c> 中创建 <see cref="StringFormat"/> 或 GDI 对象。
/// </summary>
internal static class KeycapRenderer
{
    public static void DrawCap(
        Graphics g,
        RectangleF rect,
        float radius,
        Color fill,
        Color border,
        float borderWidth,
        bool drawBottomEdge = true)
    {
        if (rect.Width <= 0f || rect.Height <= 0f) return;

        var path = Theme.RoundedPath(rect, radius);
        g.FillPath(Theme.Brush(fill), path);

        if (drawBottomEdge && rect.Height >= 14f)
        {
            float inset = Math.Max(2f, Math.Min(radius, rect.Width * 0.25f));
            float y = rect.Bottom - 1.5f;
            if (rect.Width > inset * 2f + 2f)
                g.DrawLine(Theme.Pen(Theme.Key.BottomEdge, 1f), rect.X + inset, y, rect.Right - inset, y);
        }

        g.DrawPath(Theme.Pen(border, borderWidth), path);
    }

    public static void DrawCenterText(Graphics g, string text, Font font, RectangleF rect, Color color)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 1f || rect.Height <= 1f) return;

        TextRenderer.DrawText(
            g,
            text,
            font,
            Rectangle.Round(rect),
            color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    public static void DrawSplitDigit(
        Graphics g,
        string upper,
        string lower,
        Font smallFont,
        Font mainFont,
        RectangleF rect,
        Color upperColor,
        Color lowerColor)
    {
        if (rect.Width <= 2f || rect.Height <= 2f) return;

        int x = (int)Math.Round(rect.X + 1f);
        int w = Math.Max(1, (int)Math.Round(rect.Width - 2f));
        var top = new Rectangle(x, (int)Math.Round(rect.Y + 2f), w, Math.Max(1, (int)Math.Round(rect.Height * 0.46f)));
        var bottom = new Rectangle(x, (int)Math.Round(rect.Y + rect.Height * 0.42f), w, Math.Max(1, (int)Math.Round(rect.Height * 0.54f)));

        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        if (!string.IsNullOrEmpty(upper))
            TextRenderer.DrawText(g, upper, smallFont, top, upperColor, flags);
        if (!string.IsNullOrEmpty(lower))
            TextRenderer.DrawText(g, lower, mainFont, bottom, lowerColor, flags);
    }

    public static void DrawFnKeyText(
        Graphics g,
        string phys,
        string fn,
        Font nameFont,
        Font fnFont,
        RectangleF rect,
        Color physColor,
        Color fnColor)
    {
        if (rect.Width <= 4f || rect.Height <= 4f) return;

        float nameH = Math.Min(rect.Height * 0.42f, nameFont.Height + 2f);
        int x = (int)Math.Round(rect.X + 2f);
        int w = Math.Max(1, (int)Math.Round(rect.Width - 4f));

        var nameRect = new Rectangle(x, (int)Math.Round(rect.Y + 2f), w, Math.Max(1, (int)Math.Round(nameH)));
        var fnRect = new Rectangle(x, (int)Math.Round(rect.Y + 2f + nameH), w, Math.Max(1, (int)Math.Round(rect.Height - 4f - nameH)));

        if (!string.IsNullOrEmpty(phys))
        {
            TextRenderer.DrawText(
                g,
                phys,
                nameFont,
                nameRect,
                physColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        if (!string.IsNullOrEmpty(fn))
        {
            TextRenderer.DrawText(
                g,
                fn,
                fnFont,
                fnRect,
                fnColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top
                | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
