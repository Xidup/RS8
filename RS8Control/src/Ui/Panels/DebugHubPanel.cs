using RS8Control.Protocol;

namespace RS8Control.Ui.Panels;

/// <summary>
/// 「诊断与调试」页：用一层子标签把链路测试 / 原始调试 / 诊断 / 颜色诊断四块收在一起。
/// 四个子页全部按需懒加载：未切到对应子标签前不创建控件与 RichTextBox，启动零额外开销。
/// </summary>
public sealed class DebugHubPanel : UserControl
{
    private readonly DeviceSession _session;
    private readonly TabHost _sub = new();

    public DebugHubPanel(DeviceSession session)
    {
        _session = session;
        BackColor = Theme.Bg.App;

        _sub.Dock = DockStyle.Fill;
        _sub.AddLazyPage("链路测试", () => new LinkPanel(_session), c => ((LinkPanel)c).OnSessionChanged());
        _sub.AddLazyPage("原始调试", () => new RawPanel(_session), c => ((RawPanel)c).OnSessionChanged());
        _sub.AddLazyPage("诊断", () => new DiagPanel(_session));
        _sub.AddLazyPage("颜色诊断", () => new ColorProbePanel(_session), c => ((ColorProbePanel)c).OnSessionChanged());

        Controls.Add(_sub);
    }

    public void OnSessionChanged()
    {
        foreach (var page in _sub.CreatedPages)
        {
            switch (page)
            {
                case LinkPanel lp: lp.OnSessionChanged(); break;
                case RawPanel rp: rp.OnSessionChanged(); break;
                case ColorProbePanel cp: cp.OnSessionChanged(); break;
            }
        }
    }
}
