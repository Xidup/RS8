using RS8Control.Model;
using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 「使用说明」页：把 defs/shortcuts.json 里的 sections 与 faq 排版成可滚动的卡片，
/// 每章一张卡（标题 + 带序号的条目），FAQ 用「问 / 答」两段式。全部内容来自本地 JSON，不联网。
///
/// 性能：卡片与 Label 在初始化时**只建一次**，窗口拖动改宽时只更新宽度与换行高度
/// （带消息循环合并与测量缓存），不再销毁/重建全部控件。
/// </summary>
public sealed class GuidePanel : UserControl
{
    private readonly ShortcutGuide? _guide;
    private readonly Panel _bar = new();
    private readonly Label _caption = new();
    private readonly BufferedPanel _host = new();

    private readonly List<SectionBlock> _sections = new();
    private FaqBlock? _faq;

    private int _builtWidth = -1;
    private bool _layoutPending;
    private bool _layingOut;
    private readonly Dictionary<(string Text, int Width, float Size), int> _wrapCache = new();

    private sealed class SectionBlock
    {
        public required CardPanel Card;
        public required Label Title;
        public readonly List<(Label Num, Label Body)> Items = new();
    }

    private sealed class FaqBlock
    {
        public required CardPanel Card;
        public required Label Title;
        public readonly List<(Label Q, Label A)> Pairs = new();
    }

    public GuidePanel(DeviceSession session)
    {
        _ = session;   // 本页内容全部离线，不需要会话；保留参数是为了与其它面板的构造签名一致
        try { _guide = ShortcutGuide.AutoLoad(); }
        catch { _guide = null; }

        BackColor = Theme.Bg.App;

        _bar.Dock = DockStyle.Top;
        _bar.BackColor = Theme.Bg.Surface;
        _caption.Text = "内容整理自 RS8 说明书";
        _caption.AutoSize = true;
        _caption.ForeColor = Theme.Ink.Muted;
        _bar.Controls.Add(_caption);
        _bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line.Subtle });
        LayoutBar();

        _host.Dock = DockStyle.Fill;
        _host.AutoScroll = true;
        _host.BackColor = Theme.Bg.App;
        _host.Resize += (_, _) => RequestLayout();

        BuildCardsOnce();

        Controls.Add(_host);
        Controls.Add(_bar);

        RequestLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _wrapCache.Clear();
        _builtWidth = -1;
        LayoutBar();
        RequestLayout();
    }

    private void LayoutBar()
    {
        _bar.Height = UiLayout.BarHeightFor(this);
        _caption.Left = Theme.Scale(this, 16);
        _caption.Top = UiLayout.CenterInBar(_caption, _bar.Height);
    }

    /// <summary>
    /// 本页内容完全来自本地 JSON，与键盘连接状态无关，因此这里没有需要刷新的东西。
    /// （接口保留是为了让 MainForm 统一调用。）
    /// </summary>
    public void OnSessionChanged() { }

    private void BuildCardsOnce()
    {
        if (_guide == null)
        {
            _host.Controls.Add(new Label
            {
                Text = "没有找到 defs/shortcuts.json，无法显示使用说明。",
                AutoSize = true,
                ForeColor = Theme.Ink.Muted,
                Location = new Point(16, 16),
            });
            return;
        }

        foreach (var section in _guide.Sections)
        {
            var card = new CardPanel();
            var title = new Label
            {
                Text = section.Title,
                AutoSize = true,
                Font = Theme.Fonts.Title,
                ForeColor = Theme.Ink.Primary,
            };
            card.Controls.Add(title);

            var block = new SectionBlock { Card = card, Title = title };
            for (int i = 0; i < section.Items.Count; i++)
            {
                var num = new Label
                {
                    Text = $"{i + 1}.",
                    AutoSize = true,
                    ForeColor = Theme.Ink.Muted,
                };
                var body = new Label
                {
                    Text = TrimLeadingCircledNumber(section.Items[i]),
                    AutoSize = false,
                    ForeColor = Theme.Ink.Body,
                };
                card.Controls.Add(num);
                card.Controls.Add(body);
                block.Items.Add((num, body));
            }
            _sections.Add(block);
            _host.Controls.Add(card);
        }

        if (_guide.Faqs.Count > 0)
        {
            var card = new CardPanel();
            var title = new Label
            {
                Text = "常见问题",
                AutoSize = true,
                Font = Theme.Fonts.Title,
                ForeColor = Theme.Ink.Primary,
            };
            card.Controls.Add(title);

            var faqBlock = new FaqBlock { Card = card, Title = title };
            foreach (var faq in _guide.Faqs)
            {
                var q = new Label
                {
                    Text = "问：" + faq.Q,
                    AutoSize = false,
                    Font = Theme.Fonts.BodyBold,
                    ForeColor = Theme.Ink.Primary,
                };
                var a = new Label
                {
                    Text = "答：" + faq.A,
                    AutoSize = false,
                    ForeColor = Theme.Ink.Body,
                };
                card.Controls.Add(q);
                card.Controls.Add(a);
                faqBlock.Pairs.Add((q, a));
            }
            _faq = faqBlock;
            _host.Controls.Add(card);
        }
    }

    private static string TrimLeadingCircledNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = 0;
        if (s[0] is >= '\u2460' and <= '\u2473')
        {
            i = 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
        return i > 0 ? s[i..] : s;
    }

    private void RequestLayout()
    {
        if (IsDisposed || _layingOut || _layoutPending) return;

        if (!IsHandleCreated)
        {
            _layingOut = true;
            try { LayoutCards(); }
            finally { _layingOut = false; }
            return;
        }

        _layoutPending = true;
        try { BeginInvoke(new Action(FlushLayout)); }
        catch (InvalidOperationException) { _layoutPending = false; }
    }

    private void FlushLayout()
    {
        if (IsDisposed) { _layoutPending = false; return; }
        _layoutPending = false;
        _layingOut = true;
        try { LayoutCards(); }
        finally { _layingOut = false; }
    }

    private void LayoutCards()
    {
        int margin = Theme.Scale(this, 12);
        int gap = Theme.Scale(this, 12);
        int minW = Theme.Scale(this, 560);
        int w = Math.Max(minW, _host.ClientSize.Width - margin * 2 - SystemInformation.VerticalScrollBarWidth);
        if (w == _builtWidth) return;
        _builtWidth = w;

        int padX = Theme.Scale(this, 18);
        int padTop = Theme.Scale(this, 14);
        int titleGap = Theme.Scale(this, 30);
        int itemLeft = Theme.Scale(this, 44);
        int itemGap = Theme.Scale(this, 9);
        int faqQaGap = Theme.Scale(this, 5);
        int faqPairGap = Theme.Scale(this, 14);

        _host.SuspendLayout();
        try
        {
            int cardY = margin;

            foreach (var sec in _sections)
            {
                sec.Card.SuspendLayout();
                sec.Card.Width = w;
                sec.Card.Location = new Point(margin, cardY + _host.AutoScrollPosition.Y);
                sec.Title.Location = new Point(padX, padTop);

                int cy = padTop + titleGap;
                int bodyW = Math.Max(Theme.Scale(this, 80), w - itemLeft - padX);
                foreach (var (num, body) in sec.Items)
                {
                    num.Location = new Point(padX, cy);
                    body.Location = new Point(itemLeft, cy);
                    body.Width = bodyW;
                    body.Height = WrappedHeight(body.Text, body.Font, bodyW);
                    cy += body.Height + itemGap;
                }
                sec.Card.Height = cy + Theme.Scale(this, 8);
                sec.Card.ResumeLayout(true);
                cardY += sec.Card.Height + gap;
            }

            if (_faq != null)
            {
                _faq.Card.SuspendLayout();
                _faq.Card.Width = w;
                _faq.Card.Location = new Point(margin, cardY + _host.AutoScrollPosition.Y);
                _faq.Title.Location = new Point(padX, padTop);

                int cy = padTop + titleGap;
                int bodyW = Math.Max(Theme.Scale(this, 80), w - padX * 2);
                foreach (var (q, a) in _faq.Pairs)
                {
                    q.Location = new Point(padX, cy);
                    q.Width = bodyW;
                    q.Height = WrappedHeight(q.Text, q.Font, bodyW);
                    cy += q.Height + faqQaGap;

                    a.Location = new Point(padX, cy);
                    a.Width = bodyW;
                    a.Height = WrappedHeight(a.Text, a.Font, bodyW);
                    cy += a.Height + faqPairGap;
                }
                _faq.Card.Height = cy + Theme.Scale(this, 4);
                _faq.Card.ResumeLayout(true);
                cardY += _faq.Card.Height + margin;
            }

            _host.AutoScrollMinSize = new Size(w + margin * 2, cardY);
        }
        finally
        {
            _host.ResumeLayout(true);
        }
    }

    private int WrappedHeight(string text, Font font, int width)
    {
        var key = (text, width, font.Size);
        if (_wrapCache.TryGetValue(key, out int cached)) return cached;

        int h = TextRenderer.MeasureText(text, font, new Size(Math.Max(40, width), 0),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;

        if (_wrapCache.Count > 256) _wrapCache.Clear();
        _wrapCache[key] = h;
        return h;
    }
}
