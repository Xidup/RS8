using RS8Control.Protocol;
using RS8Control.Ui;
using RS8Control.Ui.Panels;

namespace RS8Control.Diag;

/// <summary>
/// 无窗口渲染指定面板到 PNG，用于验证界面排版（不需要显示窗口，也不会被其它窗口遮挡）。
/// 用法：RS8Control.exe --renderui
/// </summary>
public static class RenderUi
{
    private static int _failures;

    public static int Run()
    {
        _failures = 0;
        string dir = Path.Combine(AppContext.BaseDirectory, "ui_render");
        Directory.CreateDirectory(dir);

        var session = new DeviceSession();
        try { session.Refresh(); }
        catch (Exception ex) { Console.WriteLine("连接失败（继续离线渲染）：" + ex.Message); }
        Thread.Sleep(400);

        Render(session, () => new ColorProbePanel(session), "colorprobe", dir);
        // 快捷键页在四种宽度各渲染一张：默认窗口 1080 是问题复现宽度，
        // 780/900 验证窄窗口下是否退化成堆叠，1400 验证宽窗口下并排不裁切。
        Render(session, () => new ShortcutPanel(session), "shortcut_w780", dir, 1750, 780);
        Render(session, () => new ShortcutPanel(session), "shortcut_w900", dir, 1750, 900);
        Render(session, () => new ShortcutPanel(session), "shortcut_w1080", dir, 1750, 1080);
        Render(session, () => new ShortcutPanel(session), "shortcut_w1400", dir, 1750, 1400);
        Render(session, () => new LightingPanel(session), "lighting", dir, 1200);   // 三张灯区卡片竖排，要够高才拍得全
        Render(session, () => new MacroPanel(session), "macro", dir, 900);          // 宏编辑页：16 项列表 + 步骤区，给高一点
        Render(session, () => new KeymapPanel(session), "keymap", dir);
        Render(session, () => new GuidePanel(session), "guide", dir, 900);
        // 「诊断与调试」页里的四个子页：它们平时藏在子标签里，单独渲染才能逐张核对顶栏
        Render(session, () => new DiagPanel(session), "diag", dir, 700);
        Render(session, () => new LinkPanel(session), "link", dir, 700);
        Render(session, () => new RawPanel(session), "raw", dir, 700);
        RenderKeycodePicker(session, dir);
        RenderComboDialog(dir);
        RenderMainForm(dir);

        Console.WriteLine($"渲染完成 → {dir}（失败 {_failures}）");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 渲染主窗口。和其它 Form 一样必须先真正显示一次才会完成布局，
    /// 所以 Show → 泵消息 → DrawToBitmap → Hide（窗口挪到屏幕外，不注入任何合成鼠标事件）。
    /// 主窗口 Shown 会自动检测设备，等它一会儿再截图。
    /// </summary>
    private static void RenderMainForm(string dir)
    {
        MainForm? form = null;
        try
        {
            form = new MainForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(100); }
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, "mainform.png"), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("  mainform.png");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("  mainform 渲染失败:\n" + ex);
        }
        finally
        {
            form?.Hide();
            form?.Dispose();
        }
    }

    /// <summary>
    /// 渲染键码选择器（Form）的每个分类页。
    /// Form 必须先真正显示一次才会完成布局，所以这里 Show → 泵一次消息 → DrawToBitmap → Hide；
    /// 窗口被挪到屏幕外，且全程不注入任何合成鼠标事件。
    /// </summary>
    private static void RenderKeycodePicker(DeviceSession session, string dir)
    {
        KeycodePicker? form = null;
        try
        {
            form = new KeycodePicker(0x5222, session.Definition?.CustomKeycodes, Math.Max(session.LayerCount, 4));
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            Application.DoEvents();
            Thread.Sleep(300);

            var tabs = FindTabs(form);
            if (tabs == null)
            {
                _failures++;
                Console.WriteLine("  keycodepicker 渲染失败: 找不到分类标签控件");
                return;
            }

            for (int i = 0; i < tabs.PageCount; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                Thread.Sleep(200);

                string name = "keycodepicker_" + Sanitize(tabs.PageTitle(i));
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  " + name + ".png");
            }
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("  keycodepicker 渲染失败:\n" + ex);
        }
        finally
        {
            form?.Hide();
            form?.Dispose();
        }
    }

    /// <summary>渲染「多键组合」对话框（同样是 Form，先显示一次再截图）。</summary>
    private static void RenderComboDialog(string dir)
    {
        ComboDialog? dlg = null;
        try
        {
            dlg = new ComboDialog(0x0306);   // Ctrl + Shift + C，顺便验证回填
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(-4000, -4000);
            dlg.Show();
            Application.DoEvents();
            Thread.Sleep(300);
            using var bmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
            bmp.Save(Path.Combine(dir, "combodialog.png"), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("  combodialog.png");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("  combodialog 渲染失败:\n" + ex);
        }
        finally
        {
            dlg?.Hide();
            dlg?.Dispose();
        }
    }

    /// <summary>递归查找自绘标签宿主（原来是找 TabControl，改用 TabHost 后同步调整）。</summary>
    private static TabHost? FindTabs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TabHost th) return th;
            var inner = FindTabs(c);
            if (inner != null) return inner;
        }
        return null;
    }

    /// <summary>分类名（如「基本键」）直接做文件名，去掉文件名里非法的字符。</summary>
    private static string Sanitize(string s)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '_');
        return s;
    }

    private static void Render(DeviceSession session, Func<Control> factory, string name, string dir,
                               int height = 680, int width = 1080)
    {
        try
        {
            using var c = factory();
            c.Size = new Size(width, height);
            c.GetType().GetMethod("OnSessionChanged")?.Invoke(c, null);

            // 面板的 OnSessionChanged 会启动后台读取（ShortcutPanel / LightingPanel 都是），
            // 它们的 await 续体通过 SynchronizationContext 排回 UI 线程并调用 Invalidate()。
            // 这些失效如果在 DrawToBitmap 的 WM_PRINT 期间被处理，会重入绘制并卡死。
            // 所以截图前先把消息队列泵干净，等后台任务落定。
            for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(60); }

            using var bmp = new Bitmap(c.Width, c.Height);
            c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("  " + name + ".png");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("  " + name + " 渲染失败:\n" + ex);
        }
    }
}
