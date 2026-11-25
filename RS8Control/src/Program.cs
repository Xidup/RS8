using System.Runtime.InteropServices;
using System.Text;
using RS8Control.Diag;
using RS8Control.Model;
using RS8Control.Ui;

namespace RS8Control;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static int _handlingUiCrash;

    /// <summary>
    /// exe 以 GUI 子系统构建（这样双击不会附带黑色终端窗口），副作用是命令行模式下
    /// 进程不附着任何控制台，Console.WriteLine 的输出会直接消失。
    /// 这里把标准流重新接回父进程的控制台 —— 仅在有参数（命令行模式）时执行。
    /// </summary>
    private static void AttachParentConsoleIfNeeded(string[] args)
    {
        if (args.Length == 0) return;

        // 父进程可能是 cmd/PowerShell（真控制台），也可能是 mintty 这类用管道转发的终端。
        // 前者需要 AttachConsole，后者本来就继承着有效的管道句柄。两种都试一遍。
        try { AttachConsole(AttachParentProcess); } catch { /* 没有父控制台，忽略 */ }

        try
        {
            var stdout = Console.OpenStandardOutput();
            if (stdout != null && stdout != Stream.Null)
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });

            var stderr = Console.OpenStandardError();
            if (stderr != null && stderr != Stream.Null)
                Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch
        {
            // 接管失败时保持默认行为，不因为输出问题影响命令本身
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AttachParentConsoleIfNeeded(args);

        // 层别名（2→FN、3→Mac）与内置中文键码名来自 defs/shortcuts.json；读失败就静默跳过。
        Keycodes.LoadShortcutsMetadata(Path.Combine(AppContext.BaseDirectory, "defs", "shortcuts.json"));

        // ---- 命令行诊断模式（无需 GUI） ----
        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            string text = Probe.Run();
            string outPath = Path.Combine(AppContext.BaseDirectory, "RS8_diagnostic.txt");
            try
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
                Console.WriteLine($"诊断报告已写入: {outPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入文件失败: {ex.Message}");
            }
            Console.WriteLine();
            Console.WriteLine(text);
            return 0;
        }

        if (args.Any(a => a.Equals("--probe-stdout", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(Probe.Run());
            return 0;
        }

        // ---- 交互式：实时监听按键，验证链路 ----
        if (args.Any(a => a.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
        {
            int secs = 12;
            int idx = Array.FindIndex(args, a => a.Equals("--watch", StringComparison.OrdinalIgnoreCase));
            if (idx + 1 < args.Length && int.TryParse(args[idx + 1], out int v)) secs = v;
            Interactive.Watch(secs);
            return 0;
        }

        // ---- 交互式：写入策略测试 ----
        if (args.Any(a => a.Equals("--writetest", StringComparison.OrdinalIgnoreCase)))
        {
            Interactive.WriteTest();
            return 0;
        }

        // ---- 转储报告描述符（注册表） ----
        if (args.Any(a => a.Equals("--desc", StringComparison.OrdinalIgnoreCase)))
        {
            Interactive.DumpDescriptors();
            return 0;
        }

        // ---- 自动扫描：报告 ID + 命令字 ----
        if (args.Any(a => a.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
        {
            string log = Path.Combine(AppContext.BaseDirectory, "RS8_scan.txt");
            var sw = new StringWriter();
            var old = Console.Out;
            Console.SetOut(sw);
            try { Interactive.Scan(); }
            finally { Console.SetOut(old); }
            File.WriteAllText(log, sw.ToString(), new UTF8Encoding(false));
            Console.WriteLine(sw.ToString());
            Console.WriteLine($"[已写入 {log}]");
            return 0;
        }

        // ---- VIA 命令集定向探针 ----
        if (args.Any(a => a.Equals("--via", StringComparison.OrdinalIgnoreCase)))
        {
            string log = Path.Combine(AppContext.BaseDirectory, "RS8_via_probe.txt");
            var sw = new StringWriter();
            var old = Console.Out;
            Console.SetOut(sw);
            try { ViaProbe.Run(); }
            finally { Console.SetOut(old); }
            File.WriteAllText(log, sw.ToString(), new UTF8Encoding(false));
            Console.WriteLine(sw.ToString());
            Console.WriteLine($"[已写入 {log}]");
            return 0;
        }

        // ---- 宏系统探针（只读，判断宏功能是否可用） ----
        if (args.Any(a => a.Equals("--macrotest", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.RunMacroTest();
            return 0;
        }

        // ---- 0x12 偏移量语义验证（只读） ----
        if (args.Any(a => a.Equals("--offtest", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.RunOffsetTest();
            return 0;
        }

        // ---- 转储指定层的键位 ----
        if (args.Any(a => a.Equals("--dumplayer", StringComparison.OrdinalIgnoreCase)))
        {
            int li = Array.FindIndex(args, a => a.Equals("--dumplayer", StringComparison.OrdinalIgnoreCase));
            int layer = 0;
            if (li + 1 < args.Length) int.TryParse(args[li + 1], out layer);
            ViaProbe.DumpLayer(layer);
            return 0;
        }

        // ---- 键位缓冲批量读写验证（0x12 / 0x13 幂等测试） ----
        if (args.Any(a => a.Equals("--buftest", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.RunBufferTest();
            return 0;
        }

        // ---- 2.4G 只读模式验证 ----
        if (args.Any(a => a.Equals("--readonlytest", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.RunReadOnlyTest();
            return 0;
        }

        // ---- 性能对比（实机：改造前 vs 改造后） ----
        if (args.Any(a => a.Equals("--perftest", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.RunPerfTest();
            return 0;
        }

        // ---- 恢复出厂键位（修复误写） ----
        if (args.Any(a => a.Equals("--reset-keymap", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.ResetKeymap();
            return 0;
        }

        // ---- Logo / 侧灯 shutdown(6) 幂等测试 ----
        if (args.Any(a => a.Equals("--lightshutdowntest", StringComparison.OrdinalIgnoreCase)))
        {
            ViaProbe.RunLightShutdownTest();
            return 0;
        }

        // ---- 纯逻辑回归自检（无需连接硬件） ----
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            return SelfTest.Run();
        }

        // ---- 无窗口渲染界面到 PNG（验证排版，不显示窗口） ----
        if (args.Any(a => a.Equals("--renderui", StringComparison.OrdinalIgnoreCase)))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetColorMode(SystemColorMode.Classic);
            return RenderUi.Run();
        }

        // ---- 正常 GUI 模式 ----
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.SetColorMode(SystemColorMode.Classic);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 未处理异常落盘并提示用户（防重入），避免 UI 停留在残破状态。
        Application.ThreadException += (_, e) =>
        {
            LogCrash("UI 线程", e.Exception);
            if (Interlocked.CompareExchange(ref _handlingUiCrash, 1, 0) == 0)
            {
                try
                {
                    MessageBox.Show(
                        $"程序遇到未处理异常，已记录日志到 RS8Control_crash.txt：\r\n\r\n{e.Exception.Message}",
                        "RS8Control 异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { /* 忽略弹窗失败 */ }
                finally { Interlocked.Exchange(ref _handlingUiCrash, 0); }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("非 UI 线程", e.ExceptionObject as Exception);

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            LogCrash("启动", ex);
            MessageBox.Show(ex.ToString(), "RS8Control 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }

    /// <summary>把未处理异常写到 exe 目录下的 RS8Control_crash.txt（追加，不覆盖历史）。</summary>
    private static void LogCrash(string where, Exception? ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "RS8Control_crash.txt");
            string text = $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {where} =====\r\n{ex}\r\n\r\n";
            File.AppendAllText(path, text, new UTF8Encoding(false));
            Console.Error.WriteLine(text);
        }
        catch
        {
            // 连日志都写不出去就算了，绝不能因为记日志再抛一次
        }
    }
}
