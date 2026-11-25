using RS8Control.Model;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 「多键组合」小对话框：勾选修饰键 + 选一个主键，得到 QMK 的 QK_MODS 键码
/// <c>(修饰位 &lt;&lt; 8) | 基本键码</c>，修饰位 Ctrl=1 / Shift=2 / Alt=4 / Win=8（其中 0x0100 本身即 Ctrl 位）。
/// 例：Ctrl+Shift+C = (0x03 &lt;&lt; 8) | 0x06 = 0x0306，Shift+C = (0x02 &lt;&lt; 8) | 0x06 = 0x0206。
///
/// 只做「修饰键 + 一个基本键」，不允许递归选另一个组合键，也不涉及 VIA 宏缓冲。
/// </summary>
public sealed class ComboDialog : Form
{
    private const ushort QkMods = 0x0100;

    /// <summary>下拉里可选的主键：常用基本键，不包含任何组合键。</summary>
    private static readonly ushort[] BasicKeys = BuildBasicKeys();

    private readonly FlatCheckBox[] _mods = new FlatCheckBox[4];
    private readonly FlatComboBox _basic = new();
    private readonly Label _head = new();
    private readonly Label _basicCaption = new();
    private readonly Label _resultCaption = new();
    private readonly Label _result = new();
    private readonly FlatButton _ok = new("确定", ButtonVariant.Primary);
    private readonly FlatButton _cancel = new("取消") { DialogResult = DialogResult.Cancel };

    /// <summary>点「确定」后的组合键码。</summary>
    public ushort Selected { get; private set; }

    public ComboDialog(ushort current)
    {
        Selected = current;

        Text = "多键组合";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Fonts.Body;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        ForeColor = Theme.Ink.Primary;

        // 已有键码回填：QK_MODS 组合（0x0100–0x1FFF）拆成修饰位 + 基本键，普通键只回填主键
        byte modBits = 0;
        ushort basic = 0;
        if (current >= QkMods && current < 0x2000)
        {
            modBits = (byte)(current >> 8);
            basic = (byte)(current & 0xFF);
        }
        else if (current > 0 && current < QkMods)
        {
            basic = current;
        }

        _head.Text = "修饰键：";
        _head.AutoSize = true;
        Controls.Add(_head);

        string[] names = { "Ctrl", "Shift", "Alt", "Win" };
        for (int i = 0; i < 4; i++)
        {
            var cb = new FlatCheckBox
            {
                Text = names[i],
                Checked = (modBits & (1 << i)) != 0,
            };
            cb.CheckedChanged += (_, _) => UpdateResult();
            _mods[i] = cb;
            Controls.Add(cb);
        }

        _basicCaption.Text = "主  键：";
        _basicCaption.AutoSize = true;
        Controls.Add(_basicCaption);

        _basic.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (ushort code in BasicKeys)
            _basic.Items.Add(new Option(code, Keycodes.Describe(code)));
        int want = Array.IndexOf(BasicKeys, basic);
        _basic.SelectedIndex = want >= 0 ? want : Array.IndexOf(BasicKeys, (ushort)0x04);
        _basic.SelectedIndexChanged += (_, _) => UpdateResult();
        Controls.Add(_basic);

        _resultCaption.Text = "结果：";
        _resultCaption.AutoSize = true;
        _resultCaption.Font = Theme.Fonts.BodyBold;
        Controls.Add(_resultCaption);

        _result.AutoSize = true;
        _result.ForeColor = Theme.Accent.Base;
        Controls.Add(_result);

        // 先算好结果再关窗体，避免依赖「按钮的 DialogResult 与 Click 谁先执行」
        _ok.Click += (_, _) => { Selected = BuildCode(); DialogResult = DialogResult.OK; };
        Controls.Add(_ok);
        Controls.Add(_cancel);
        AcceptButton = _ok;
        CancelButton = _cancel;

        LayoutDialog();
        UpdateResult();
    }

    private void LayoutDialog()
    {
        ClientSize = new Size(Theme.Scale(this, 432), Theme.Scale(this, 196));

        int leftMargin = Theme.Scale(this, 16);
        int col2 = Theme.Scale(this, 88);

        // 每一行的标签与控件按同一垂直中心摆
        int row1Top = Theme.Scale(this, 19);
        for (int i = 0; i < 4; i++)
            _mods[i].Size = _mods[i].GetPreferredSize(Size.Empty);
        int row1H = Math.Max(Theme.Scale(this, 26),
            UiLayout.RowHeight(_head, _mods[0], _mods[1], _mods[2], _mods[3]));
        _head.Location = new Point(leftMargin, UiLayout.CenterInRow(_head, row1Top, row1H));

        int mx = col2;
        int modGap = Theme.Scale(this, 6);
        for (int i = 0; i < 4; i++)
        {
            _mods[i].Location = new Point(mx, UiLayout.CenterInRow(_mods[i], row1Top, row1H));
            mx += Math.Max(Theme.Scale(this, i == 0 ? 58 : 62), _mods[i].Width + modGap);
        }

        int row2Top = Theme.Scale(this, 63);
        _basic.Size = new Size(Theme.Scale(this, 236), Theme.Scale(this, Theme.Metrics.ControlHeight));
        int row2H = UiLayout.RowHeight(_basicCaption, _basic);
        _basicCaption.Location = new Point(leftMargin, UiLayout.CenterInRow(_basicCaption, row2Top, row2H));
        _basic.Location = new Point(col2, UiLayout.CenterInRow(_basic, row2Top, row2H));

        int row3Top = Theme.Scale(this, 108);
        int row3H = UiLayout.RowHeight(_resultCaption, _result);
        _resultCaption.Location = new Point(leftMargin, UiLayout.CenterInRow(_resultCaption, row3Top, row3H));
        _result.Location = new Point(col2, UiLayout.CenterInRow(_result, row3Top, row3H));

        int buttonTop = Theme.Scale(this, 146);
        int buttonH = Theme.Scale(this, Theme.Metrics.ControlHeight);
        int rightMargin = Theme.Scale(this, 16);
        int btnGap = Theme.Scale(this, 10);
        _cancel.Size = _cancel.GetPreferredSize(Size.Empty);
        _ok.Size = _ok.GetPreferredSize(Size.Empty);
        _cancel.Location = new Point(ClientSize.Width - rightMargin - _cancel.Width, UiLayout.CenterInRow(_cancel, buttonTop, buttonH));
        _ok.Location = new Point(_cancel.Left - btnGap - _ok.Width, UiLayout.CenterInRow(_ok, buttonTop, buttonH));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutDialog();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutDialog();
    }

    private static ushort[] BuildBasicKeys()
    {
        var list = new List<ushort>();
        for (ushort c = 0x04; c <= 0x1D; c++) list.Add(c);          // A–Z
        for (ushort c = 0x1E; c <= 0x27; c++) list.Add(c);          // 1!–0)
        list.AddRange(new ushort[] { 0x28, 0x29, 0x2A, 0x2B, 0x2C }); // Enter Esc Bksp Tab Space
        list.AddRange(new ushort[] { 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38 });
        for (ushort c = 0x3A; c <= 0x45; c++) list.Add(c);          // F1–F12
        for (ushort c = 0x46; c <= 0x4E; c++) list.Add(c);          // PRINT–PGDN
        list.AddRange(new ushort[] { 0x4F, 0x50, 0x51, 0x52 });      // → ← ↓ ↑
        return list.ToArray();
    }

    private byte ModBits()
    {
        byte m = 0;
        for (int i = 0; i < 4; i++)
            if (_mods[i].Checked) m |= (byte)(1 << i);
        return m;
    }

    /// <summary>修饰位为 0 时直接返回基本键码本身；否则按 QK_MODS 高字节左移 8 位拼装（0x0100 即 Ctrl）。</summary>
    private ushort BuildCode()
    {
        if (_basic.SelectedItem is not Option o) return Keycodes.KC_NO;
        byte m = ModBits();
        return m == 0 ? o.Code : (ushort)((m << 8) | o.Code);
    }

    private void UpdateResult()
    {
        if (_basic.SelectedItem is not Option o)
        {
            _result.Text = "（请选择主键）";
            return;
        }
        ushort code = BuildCode();
        _result.Text = $"{Keycodes.Describe(code)}  (0x{code:X4})";
    }

    /// <summary>下拉项：键码 + 显示名（名字用 <see cref="Keycodes.Describe"/>，与主界面一致）。</summary>
    private sealed record Option(ushort Code, string Name)
    {
        public override string ToString() => Name;
    }
}
