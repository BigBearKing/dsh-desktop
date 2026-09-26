using System.Text;

namespace DshDesktop;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Diagnostic switches. --path-only restricts lookups to PATH, which also makes the
        // "missing prerequisites" path reproducible on a machine that does have Node installed.
        if (HasFlag(args, "--path-only"))
            ToolLocator.SearchCommonLocations = false;

        if (HasFlag(args, "--check-env"))
            return RunEnvironmentCheck();

        if (HasFlag(args, "--about"))
        {
            PrintAbout();
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\DeepSeekHarness.Desktop", createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("DeepSeek Harness 已经在运行了。", "DeepSeek Harness",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "DeepSeek Harness 未处理异常",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject?.ToString() ?? "未知错误", "DeepSeek Harness 致命错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm());
        return 0;
    }

    static bool HasFlag(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Headless environment check: <c>DshDesktop.exe --check-env [--path-only]</c>.
    /// Prints the report, always writes it to the report file, and exits 0 when ready / 1 when
    /// something blocking is missing.
    /// </summary>
    static int RunEnvironmentCheck()
    {
        var config = AppConfig.Load();
        var report = Prerequisites.Check(config);
        var reportPath = Prerequisites.WriteReportFile(report);

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // No console attached; the report file still carries the result.
        }

        Console.WriteLine(report.ToPlainText());
        Console.WriteLine($"报告已写入: {reportPath}");
        return report.Ok ? 0 : 1;
    }

    /// <summary>
    /// Headless About: <c>DshDesktop.exe --about</c> prints the same block the About window shows,
    /// so a bug report can be produced without opening the UI.
    /// </summary>
    static void PrintAbout()
    {
        var toolchain = ToolLocator.Probe(AppConfig.Load());

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // No console attached; nothing else to do.
        }

        Console.WriteLine(AboutInfo.ToPlainText(toolchain));
    }
}
