using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace RS8Control.Ui;

internal static class FlatControlPaint
{
    public static Color ParentBackColor(Control c)
    {
        var p = c.Parent;
        return p == null || p.BackColor == Color.Empty ? Theme.Bg.Surface : p.BackColor;
    }

    public static void PrepareGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }
}

/// <summary>
/// 极简圆角平铺单行/多行文本框：包裹无边框原生 <see cref="TextBox"/>，
/// 外层统一绘制圆角背景、焦点边框与禁用态，并按 DPI 缩放高度与内边距。
/// </summary>
internal sealed class FlatTextBox : Control
{
    private readonly TextBox _inner;

    public new event EventHandler? TextChanged;

    public FlatTextBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        base.TabStop = false;
        Font = Theme.Fonts.Body;
        Cursor = Cursors.IBeam;
        Height = Theme.Scale(this, Theme.Metrics.ControlHeight);

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Bg.Surface,
            ForeColor = Theme.Ink.Primary,
            Font = Font,
        };
        _inner.GotFocus += (_, _) => Invalidate();
        _inner.LostFocus += (_, _) => Invalidate();
        _inner.TextChanged += (_, e) => TextChanged?.Invoke(this, e);
        _inner.KeyDown += (_, e) => OnKeyDown(e);
        _inner.KeyPress += (_, e) => OnKeyPress(e);
        _inner.KeyUp += (_, e) => OnKeyUp(e);

        Controls.Add(_inner);
        LayoutInner();
    }

    public new bool TabStop
    {
        get => _inner.TabStop;
        set => _inner.TabStop = value;
    }

    [AllowNull]
    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value ?? "";
    }

    public string PlaceholderText
    {
        get => _inner.PlaceholderText;
        set => _inner.PlaceholderText = value ?? "";
    }

    public bool ReadOnly
    {
        get => _inner.ReadOnly;
        set
        {
            _inner.ReadOnly = value;
            SyncInnerColors();
        }
    }

    public bool Multiline
    {
        get => _inner.Multiline;
        set
        {
            if (_inner.Multiline == value) return;
            _inner.Multiline = value;
            LayoutInner();
            Invalidate();
        }
    }

    public ScrollBars ScrollBars
    {
        get => _inner.ScrollBars;
        set => _inner.ScrollBars = value;
    }

    public bool AcceptsReturn
    {
        get => _inner.AcceptsReturn;
        set => _inner.AcceptsReturn = value;
    }

    [AllowNull]
    public override Font Font
    {
        get => base.Font;
        set
        {
            base.Font = value;
            if (_inner != null)
            {
                _inner.Font = base.Font;
                LayoutInner();
            }
            Invalidate();
        }
    }

    private void SyncInnerColors()
    {
        _inner.BackColor = Enabled ? Theme.Bg.Surface : Theme.Bg.Sunken;
        _inner.ForeColor = Enabled ? Theme.Ink.Primary : Theme.Ink.Disabled;
    }

    private void LayoutInner()
    {
        if (_inner == null) return;
        int padX = Theme.Scale(this, 8);
        if (_inner.Multiline)
        {
            int padY = Theme.Scale(this, 6);
            _inner.SetBounds(
                padX,
                padY,
                Math.Max(4, Width - padX * 2),
                Math.Max(4, Height - padY * 2));
        }
        else
        {
            _inner.Width = Math.Max(4, Width - padX * 2);
            int y = Math.Max(1, (Height - _inner.Height) / 2);
            _inner.Location = new Point(padX, y);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutInner();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInner();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (!_inner.Multiline)
            Height = Theme.Scale(this, Theme.Metrics.ControlHeight);
        LayoutInner();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _inner.Enabled = Enabled;
        SyncInnerColors();
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (Enabled) _inner.Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Enabled) _inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        FlatControlPaint.PrepareGraphics(g);
        g.Clear(FlatControlPaint.ParentBackColor(this));

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.ScaleF(this, Theme.Metrics.ControlRadius);

        Color fill = Enabled ? Theme.Bg.Surface : Theme.Bg.Sunken;
        Color border = !Enabled ? Theme.Line.Subtle
            : _inner.Focused ? Theme.Line.Focus
            : Theme.Line.Control;

        g.FillPath(Theme.Brush(fill), Theme.RoundedPath(r, radius));
        g.DrawPath(Theme.Pen(border), Theme.RoundedPath(r, radius));
    }
}

/// <summary>
/// 自绘圆角下拉选择框：点击或按 Space/Enter/F4/Alt+Down 弹出极简圆角列表，
/// 闭合状态支持方向键上下切换。
/// </summary>
internal sealed class FlatComboBox : Control
{
    public sealed class ItemCollection : IEnumerable<object>
    {
        private readonly FlatComboBox _owner;
        private readonly List<object> _list = new();

        internal ItemCollection(FlatComboBox owner) => _owner = owner;

        public int Count => _list.Count;

        public object this[int index]
        {
            get => _list[index];
            set
            {
                _list[index] = value;
                if (_owner._selectedIndex == index) _owner.Invalidate();
            }
        }

        public int Add(object item)
        {
            _list.Add(item);
            return _list.Count - 1;
        }

        public void AddRange(IEnumerable<object> items) => _list.AddRange(items);

        public void Insert(int index, object item)
        {
            _list.Insert(index, item);
            if (_owner._selectedIndex >= index) _owner._selectedIndex++;
        }

        public int IndexOf(object item) => _list.IndexOf(item);

        public bool Remove(object item)
        {
            int idx = _list.IndexOf(item);
            if (idx < 0) return false;
            RemoveAt(idx);
            return true;
        }

        public void RemoveAt(int index)
        {
            _list.RemoveAt(index);
            if (_owner._selectedIndex == index)
            {
                _owner._selectedIndex = -1;
                _owner.Invalidate();
            }
            else if (_owner._selectedIndex > index)
            {
                _owner._selectedIndex--;
            }
        }

        public void Clear()
        {
            _list.Clear();
            if (_owner._selectedIndex != -1)
            {
                _owner._selectedIndex = -1;
                _owner.Invalidate();
            }
        }

        public IEnumerator<object> GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
    }

    private sealed class FlatDropDownRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.FillRectangle(Theme.Brush(Theme.Bg.Surface), e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(Theme.Pen(Theme.Line.Control), r);
        }
    }

    private readonly ItemCollection _items;
    private readonly ToolStripDropDown _dropDown;
    private readonly ToolStripControlHost _host;
    private readonly ListBox _list;
    private readonly PointF[] _chevronPts = new PointF[3];

    private int _selectedIndex = -1;
    private bool _hover;
    private bool _isOpen;
    private int _dropHover = -1;
    private long _closedTick;

    public event EventHandler? SelectedIndexChanged;

    public FlatComboBox()
    {
        _items = new ItemCollection(this);

        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        TabStop = true;
        Font = Theme.Fonts.Body;
        Cursor = Cursors.Hand;
        Size = new Size(Theme.Scale(this, 120), Theme.Scale(this, Theme.Metrics.ControlHeight));

        _list = new ListBox
        {
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            BackColor = Theme.Bg.Surface,
            ForeColor = Theme.Ink.Primary,
            Font = Font,
        };
        _list.DrawItem += OnListDrawItem;
        _list.MouseMove += OnListMouseMove;
        _list.MouseLeave += (_, _) =>
        {
            if (_dropHover < 0) return;
            _dropHover = -1;
            _list.Invalidate();
        };
        _list.MouseUp += OnListMouseUp;
        _list.KeyDown += OnListKeyDown;

        _host = new ToolStripControlHost(_list)
        {
            AutoSize = false,
            Margin = new Padding(1),
            Padding = Padding.Empty,
        };

        _dropDown = new ToolStripDropDown
        {
            AutoSize = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Theme.Bg.Surface,
            DropShadowEnabled = true,
            Renderer = new FlatDropDownRenderer(),
        };
        _dropDown.Items.Add(_host);
        _dropDown.Closed += (_, _) =>
        {
            _isOpen = false;
            _closedTick = Environment.TickCount64;
            Invalidate();
        };
    }

    public ItemCollection Items => _items;

    public ComboBoxStyle DropDownStyle { get; set; } = ComboBoxStyle.DropDownList;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < -1 || value >= _items.Count)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem
    {
        get => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
        set => SelectedIndex = value == null ? -1 : _items.IndexOf(value);
    }

    [AllowNull]
    public override Font Font
    {
        get => base.Font;
        set
        {
            base.Font = value;
            if (_list != null) _list.Font = base.Font;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Width, Theme.Scale(this, Theme.Metrics.ControlHeight));

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = Theme.Scale(this, Theme.Metrics.ControlHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        FlatControlPaint.PrepareGraphics(g);
        g.Clear(FlatControlPaint.ParentBackColor(this));

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.ScaleF(this, Theme.Metrics.ControlRadius);

        Color fill = !Enabled ? Theme.Bg.Sunken
            : _hover && !_isOpen ? Theme.Bg.Hover
            : Theme.Bg.Surface;
        Color border = !Enabled ? Theme.Line.Subtle
            : (_isOpen || Focused) ? Theme.Accent.Base
            : Theme.Line.Control;
        Color ink = !Enabled ? Theme.Ink.Muted : Theme.Ink.Primary;

        g.FillPath(Theme.Brush(fill), Theme.RoundedPath(r, radius));
        g.DrawPath(Theme.Pen(border), Theme.RoundedPath(r, radius));

        // 选中项文字
        int padLeft = Theme.Scale(this, 10);
        int chevronArea = Theme.Scale(this, 24);
        string text = _selectedIndex >= 0 && _selectedIndex < _items.Count
            ? _items[_selectedIndex]?.ToString() ?? ""
            : "";

        if (text.Length > 0 && Width > padLeft + chevronArea)
        {
            TextRenderer.DrawText(
                g,
                text,
                Font,
                new Rectangle(padLeft, 0, Width - padLeft - chevronArea, Height),
                ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // 右侧 V 形箭头（复用字段数组，零分配）
        float cx = Width - Theme.ScaleF(this, 13f);
        float cy = Height * 0.5f;
        float halfW = Theme.ScaleF(this, 3.8f);
        float halfH = Theme.ScaleF(this, 2.0f);
        if (_isOpen)
        {
            _chevronPts[0] = new PointF(cx - halfW, cy + halfH);
            _chevronPts[1] = new PointF(cx, cy - halfH);
            _chevronPts[2] = new PointF(cx + halfW, cy + halfH);
        }
        else
        {
            _chevronPts[0] = new PointF(cx - halfW, cy - halfH);
            _chevronPts[1] = new PointF(cx, cy + halfH);
            _chevronPts[2] = new PointF(cx + halfW, cy - halfH);
        }

        Color chevronColor = !Enabled ? Theme.Ink.Disabled : Theme.Ink.Muted;
        g.DrawLines(Theme.Pen(chevronColor, Theme.ScaleF(this, 1.4f)), _chevronPts);
    }

    private void ShowDropDown()
    {
        if (!Enabled || _items.Count == 0) return;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (object item in _items)
                _list.Items.Add(item);
            _list.SelectedIndex = _selectedIndex >= 0 && _selectedIndex < _items.Count ? _selectedIndex : -1;
        }
        finally
        {
            _list.EndUpdate();
        }

        _dropHover = -1;
        int itemH = Theme.Scale(this, 28);
        _list.ItemHeight = itemH;
        _list.Font = Font;

        int maxVisible = Math.Clamp(_items.Count, 1, 10);
        int scrollW = _items.Count > 10 ? SystemInformation.VerticalScrollBarWidth : 0;
        int maxTextW = 0;
        foreach (object item in _items)
        {
            string s = item?.ToString() ?? "";
            int w = TextRenderer.MeasureText(s, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            if (w > maxTextW) maxTextW = w;
        }

        int dropW = Math.Max(Width, Math.Min(Theme.Scale(this, 400), maxTextW + Theme.Scale(this, 28) + scrollW));
        int dropH = maxVisible * itemH + Theme.Scale(this, 4);

        _dropDown.Size = new Size(dropW, dropH);
        _list.Size = new Size(dropW - 2, dropH - 2);
        _host.Size = _list.Size;

        _isOpen = true;
        Invalidate();
        _dropDown.Show(this, new Point(0, Height + Theme.Scale(this, 2)));
        _list.Focus();
    }

    private void CommitSelection(int idx)
    {
        _dropDown.Close();
        if (idx >= 0 && idx < _items.Count)
            SelectedIndex = idx;
        Focus();
    }

    private void OnListDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var g = e.Graphics;
        FlatControlPaint.PrepareGraphics(g);
        g.FillRectangle(Theme.Brush(Theme.Bg.Surface), e.Bounds);

        bool selected = (e.State & DrawItemState.Selected) != 0;
        int pad = Theme.Scale(this, 2);
        var row = new RectangleF(pad, e.Bounds.Y + pad, Math.Max(0, _list.ClientSize.Width - pad * 2), e.Bounds.Height - pad * 2);
        float radius = Theme.ScaleF(this, Theme.Metrics.ControlRadius);

        if (row.Width > 2f && row.Height > 2f)
        {
            if (selected)
            {
                g.FillPath(Theme.Brush(Theme.Accent.Subtle), Theme.RoundedPath(row, radius));
                float barW = Theme.ScaleF(this, 2f);
                float barPad = Theme.ScaleF(this, 4f);
                var bar = new RectangleF(row.X, row.Y + barPad, barW, Math.Max(2f, row.Height - barPad * 2));
                g.FillPath(Theme.Brush(Theme.Accent.Base), Theme.RoundedPath(bar, 1f));
            }
            else if (e.Index == _dropHover)
            {
                g.FillPath(Theme.Brush(Theme.Bg.Hover), Theme.RoundedPath(row, radius));
            }
        }

        string text = _list.Items[e.Index]?.ToString() ?? "";
        Color ink = selected ? Theme.Ink.Primary : Theme.Ink.Body;
        int textPad = Theme.Scale(this, 10);
        TextRenderer.DrawText(
            g,
            text,
            _list.Font,
            new Rectangle((int)row.X + textPad, e.Bounds.Y, Math.Max(0, (int)row.Width - textPad - Theme.Scale(this, 4)), e.Bounds.Height),
            ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        int idx = _list.IndexFromPoint(e.Location);
        if (_dropHover == idx) return;
        _dropHover = idx;
        _list.Invalidate();
    }

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = _list.IndexFromPoint(e.Location);
        if (idx >= 0 && idx < _items.Count)
            CommitSelection(idx);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            if (_list.SelectedIndex >= 0)
                CommitSelection(_list.SelectedIndex);
            else
                _dropDown.Close();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            _dropDown.Close();
            Focus();
        }
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
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        // ToolStripDropDown 在点击外部时于 OnMouseDown 之前自动关闭；
        // 避免刚点按钮收起下拉又立刻重新打开。
        if (_isOpen)
        {
            _dropDown.Close();
            return;
        }
        if (Environment.TickCount64 - _closedTick < 150) return;
        ShowDropDown();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
        {
            _hover = false;
            if (_isOpen) _dropDown.Close();
        }
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Space or Keys.Enter or Keys.F4 || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;

        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
        {
            e.Handled = true;
            ShowDropDown();
            return;
        }

        if (e.KeyCode == Keys.Up)
        {
            e.Handled = true;
            if (_selectedIndex > 0) SelectedIndex = _selectedIndex - 1;
        }
        else if (e.KeyCode == Keys.Down)
        {
            e.Handled = true;
            if (_selectedIndex < _items.Count - 1) SelectedIndex = _selectedIndex + 1;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dropDown.Dispose();
            _host.Dispose();
            _list.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 自绘圆角复选框（16×16 4px 圆角方框 + GDI+ 抗锯齿对勾 + 标签文本）。
/// </summary>
internal sealed class FlatCheckBox : Control
{
    private readonly PointF[] _checkPts = new PointF[3];
    private Pen? _checkPen;
    private Color _checkPenColor;
    private float _checkPenWidth;

    private bool _checked;
    private bool _hover;
    private bool _pressed;

    public event EventHandler? CheckedChanged;

    public FlatCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        TabStop = true;
        AutoSize = true;
        Font = Theme.Fonts.Body;
        Cursor = Cursors.Hand;
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int box = Theme.Scale(this, 16);
        int padX = Theme.Scale(this, 4);
        int gap = Theme.Scale(this, 6);
        Size textSize = string.IsNullOrEmpty(Text)
            ? Size.Empty
            : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix);

        int w = padX + box + (textSize.Width > 0 ? gap + textSize.Width : 0) + padX;
        int h = Math.Max(Theme.Scale(this, 24), Math.Max(box, textSize.Height) + Theme.Scale(this, 4));
        return new Size(w, h);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        FlatControlPaint.PrepareGraphics(g);
        g.Clear(FlatControlPaint.ParentBackColor(this));

        int box = Theme.Scale(this, 16);
        int padX = Theme.Scale(this, 4);
        int bx = padX;
        int by = (Height - box) / 2;
        var boxRect = new RectangleF(bx + 0.5f, by + 0.5f, box - 1f, box - 1f);
        float radius = Theme.ScaleF(this, 4f);

        Color fill, border, textColor;
        if (!Enabled)
        {
            fill = _checked ? Theme.Ink.Disabled : Theme.Bg.Sunken;
            border = Theme.Line.Subtle;
            textColor = Theme.Ink.Disabled;
        }
        else if (_checked)
        {
            fill = _pressed ? Theme.Accent.Pressed : _hover ? Theme.Accent.Hover : Theme.Accent.Base;
            border = fill;
            textColor = Theme.Ink.Primary;
        }
        else
        {
            fill = _pressed ? Theme.Bg.Sunken : _hover ? Theme.Bg.Hover : Theme.Bg.Surface;
            border = Focused ? Theme.Line.Focus : Theme.Line.Control;
            textColor = Theme.Ink.Primary;
        }

        g.FillPath(Theme.Brush(fill), Theme.RoundedPath(boxRect, radius));
        if (!_checked || !Enabled)
            g.DrawPath(Theme.Pen(border), Theme.RoundedPath(boxRect, radius));

        if (Focused && ShowFocusCues && Enabled && _checked)
        {
            var focusRect = new RectangleF(bx - 1f, by - 1f, box + 2f, box + 2f);
            g.DrawPath(Theme.Pen(Color.FromArgb(120, Theme.Accent.Base)), Theme.RoundedPath(focusRect, radius + 1f));
        }

        if (_checked)
        {
            _checkPts[0] = new PointF(bx + box * 0.23f, by + box * 0.52f);
            _checkPts[1] = new PointF(bx + box * 0.43f, by + box * 0.72f);
            _checkPts[2] = new PointF(bx + box * 0.78f, by + box * 0.30f);

            float penW = Math.Max(1.6f, box * 0.12f);
            Color penColor = Color.White;
            if (_checkPen == null || _checkPenColor != penColor || Math.Abs(_checkPenWidth - penW) > 0.01f)
            {
                _checkPen?.Dispose();
                _checkPen = new Pen(penColor, penW)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                _checkPenColor = penColor;
                _checkPenWidth = penW;
            }
            g.DrawLines(_checkPen, _checkPts);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            int tx = bx + box + Theme.Scale(this, 6);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                new Rectangle(tx, 0, Math.Max(0, Width - tx), Height),
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
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
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed) return;
        _pressed = false;
        Invalidate();
        if (Enabled && ClientRectangle.Contains(e.Location))
            Checked = !Checked;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
        {
            _hover = false;
            _pressed = false;
        }
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            Checked = !Checked;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checkPen?.Dispose();
            _checkPen = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 极简圆角数字输入框：包裹无边框居中对齐的 <see cref="NumericUpDown"/>，
/// 外层统一绘制圆角容器与焦点/禁用状态。
/// </summary>
internal sealed class FlatNumeric : Control
{
    private readonly NumericUpDown _inner;

    public event EventHandler? ValueChanged;

    public FlatNumeric()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        base.TabStop = false;
        Font = Theme.Fonts.Body;
        Size = new Size(Theme.Scale(this, 100), Theme.Scale(this, Theme.Metrics.ControlHeight));

        _inner = new NumericUpDown
        {
            BorderStyle = BorderStyle.None,
            TextAlign = HorizontalAlignment.Center,
            BackColor = Theme.Bg.Surface,
            ForeColor = Theme.Ink.Primary,
            Font = Font,
        };
        _inner.ValueChanged += (_, e) => ValueChanged?.Invoke(this, e);
        _inner.Enter += (_, _) => Invalidate();
        _inner.Leave += (_, _) => Invalidate();

        Controls.Add(_inner);
        LayoutInner();
    }

    public new bool TabStop
    {
        get => _inner.TabStop;
        set => _inner.TabStop = value;
    }

    public decimal Minimum
    {
        get => _inner.Minimum;
        set => _inner.Minimum = value;
    }

    public decimal Maximum
    {
        get => _inner.Maximum;
        set => _inner.Maximum = value;
    }

    public decimal Value
    {
        get => _inner.Value;
        set => _inner.Value = value;
    }

    [AllowNull]
    public override Font Font
    {
        get => base.Font;
        set
        {
            base.Font = value;
            if (_inner != null)
            {
                _inner.Font = base.Font;
                LayoutInner();
            }
            Invalidate();
        }
    }

    private void LayoutInner()
    {
        if (_inner == null) return;
        int padX = Theme.Scale(this, 6);
        _inner.Width = Math.Max(16, Width - padX * 2);
        int y = Math.Max(1, (Height - _inner.Height) / 2);
        _inner.Location = new Point(padX, y);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutInner();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInner();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = Theme.Scale(this, Theme.Metrics.ControlHeight);
        LayoutInner();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _inner.Enabled = Enabled;
        _inner.BackColor = Enabled ? Theme.Bg.Surface : Theme.Bg.Sunken;
        _inner.ForeColor = Enabled ? Theme.Ink.Primary : Theme.Ink.Disabled;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (Enabled) _inner.Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Enabled) _inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        FlatControlPaint.PrepareGraphics(g);
        g.Clear(FlatControlPaint.ParentBackColor(this));

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.ScaleF(this, Theme.Metrics.ControlRadius);

        Color fill = Enabled ? Theme.Bg.Surface : Theme.Bg.Sunken;
        Color border = !Enabled ? Theme.Line.Subtle
            : _inner.ContainsFocus ? Theme.Line.Focus
            : Theme.Line.Control;

        g.FillPath(Theme.Brush(fill), Theme.RoundedPath(r, radius));
        g.DrawPath(Theme.Pen(border), Theme.RoundedPath(r, radius));
    }
}
