namespace RS8Control.Ui;

/// <summary>按钮的三种视觉变体。</summary>
internal enum ButtonVariant
{
    /// <summary>主操作：Accent 填充、白字、无描边。</summary>
    Primary,
    /// <summary>次要操作：白底 + 1px 描边。</summary>
    Secondary,
    /// <summary>轻量操作：透明底、无描边，悬停才出底色。</summary>
    Ghost,
}

/// <summary>
/// 全程序统一的自绘按钮。
///
/// 背景：改造前有 5 份几乎一样的按钮样式函数（<c>MainForm.Style</c>、<c>DiagPanel2.Style</c>、
/// <c>DiagPanel.Btn</c>、<c>KeycodePicker.Style</c>、<c>ComboDialog.Style</c>），
/// 而且用的是 <c>FlatStyle.Flat</c> —— 画出来是**直角**，和圆角卡片打架。
///
/// 这里统一成一处实现：
///   · 圆角 6px、固定高 32px、左右内边距 14px
///   · Primary / Secondary / Ghost 三变体，各带悬停与按下态
///   · 禁用态走「浅底 + 灰字 + 浅描边」
///   · 焦点环用 Accent 40% 透明描边，替代 WinForms 那个虚点矩形
///
/// 性能：全程用 <see cref="Theme.RoundedPath"/> 的缓存路径与缓存画笔，
/// <c>OnPaint</c> 内零分配；不挂 Paint 事件，靠子类化统一处理。
/// </summary>
internal sealed class FlatButton : Button
{
    private ButtonVariant _variant;
    private bool _hover;
    private bool _pressed;

    public FlatButton(string text = "", ButtonVariant variant = ButtonVariant.Secondary)
    {
        _variant = variant;
        Text = text;

        // 字体用共享实例（WinForms 不会连带释放通过 Font 属性赋入的字体，已实测确认）
        Font = Theme.Fonts.Body;
        Cursor = Cursors.Hand;
        AutoSize = true;
        UseVisualStyleBackColor = false;
        TabStop = true;

        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>视觉变体。可在运行时切换（如灯区开关的 开/关 两态）。</summary>
    public ButtonVariant Variant
    {
        get => _variant;
        set
        {
            if (_variant == value) return;
            _variant = value;
            Invalidate();
        }
    }

    /// <summary>固定高度 32px（按 DPI 缩放），宽度按文字实测 + 左右内边距。</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text;
        if (IsHandleCreated && !IsDisposed)
        {
            using var g = CreateGraphics();
            text = TextRenderer.MeasureText(g, Text, Font);
        }
        else
        {
            text = TextRenderer.MeasureText(Text, Font);
        }

        return new Size(
            text.Width + Theme.Scale(this, Theme.Metrics.ButtonPadX) * 2,
            Theme.Scale(this, Theme.Metrics.ControlHeight));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        Color backdrop = ParentBackColor();
        // 必须先铺满整个控件矩形再画圆角形状：圆角之外那四块小三角不会被 FillPath 覆盖，
        // 不铺底就会露出双缓冲里的陈旧像素 —— 实测表现为按钮左下/右下角出现深色小豁口。
        g.Clear(backdrop);

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.Scale(this, Theme.Metrics.ControlRadius);

        Color fill, border, ink;
        bool hasBorder;

        if (!Enabled)
        {
            fill = Theme.Bg.Sunken;
            border = Theme.Line.Subtle;
            ink = Theme.Ink.Disabled;
            hasBorder = true;
        }
        else
        {
            switch (_variant)
            {
                case ButtonVariant.Primary:
                    fill = _pressed ? Theme.Accent.Pressed : _hover ? Theme.Accent.Hover : Theme.Accent.Base;
                    border = fill;
                    ink = Color.White;
                    hasBorder = false;
                    break;

                case ButtonVariant.Ghost:
                    fill = _pressed ? Theme.Bg.Sunken : _hover ? Theme.Bg.Hover : backdrop;
                    border = fill;
                    ink = Theme.Ink.Body;
                    hasBorder = false;
                    break;

                default: // Secondary
                    fill = _pressed ? Theme.Bg.Sunken : _hover ? Theme.Bg.Hover : Theme.Bg.Surface;
                    border = Theme.Line.Control;
                    ink = Theme.Ink.Primary;
                    hasBorder = true;
                    break;
            }
        }

        // 填充
        g.FillPath(Theme.Brush(fill), Theme.RoundedPath(r, radius));
        if (hasBorder) g.DrawPath(Theme.Pen(border), Theme.RoundedPath(r, radius));

        // 焦点环：Accent 40% 透明，画在按钮内侧 1px
        if (Focused && ShowFocusCues && Enabled)
        {
            var inner = new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f);
            g.DrawPath(Theme.Pen(Color.FromArgb(102, Theme.Accent.Base)), Theme.RoundedPath(inner, radius - 1f));
        }

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, 0, Width, Height), ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Ghost 变体要融进父容器，所以取父控件的底色而不是画一块自己的。</summary>
    private Color ParentBackColor()
    {
        var p = Parent;
        return p == null || p.BackColor == Color.Empty ? Theme.Bg.Surface : p.BackColor;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed) { _pressed = false; Invalidate(); }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) { _hover = false; _pressed = false; }
        Invalidate();
    }

    /// <summary>便捷入口：替代原来散落的 <c>Style(btn, text, primary)</c>。</summary>
    public static FlatButton Make(string text, ButtonVariant variant = ButtonVariant.Secondary) =>
        new(text, variant);
}
