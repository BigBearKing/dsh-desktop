using System.Text;

namespace DshDesktop;

/// <summary>One line of the start-up checklist.</summary>
internal sealed record PrerequisiteItem(
    string Label,
    bool Ok,
    bool Blocking,
    string Detail,
    string? FixCommand,
    string? FixUrl);

internal sealed class PrerequisiteReport
{
    public required Toolchain Toolchain { get; init; }
    public required IReadOnlyList<PrerequisiteItem> Items { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Only blocking items decide whether the shell may start.</summary>
    public bool Ok => Items.Where(item => item.Blocking).All(item => item.Ok);

    public IEnumerable<PrerequisiteItem> Missing => Items.Where(item => item.Blocking && !item.Ok);

    public string? FirstFixUrl => Missing.FirstOrDefault(item => item.FixUrl is not null)?.FixUrl;

    public string FixCommands() =>
        string.Join(Environment.NewLine, Missing.Where(item => item.FixCommand is not null)
            .Select(item => item.FixCommand!)
            .Distinct());

    public string ToPlainText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"DeepSeek Harness 环境检测  {CheckedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"PATH 查找: 开    常见安装位置查找: {(ToolLocator.SearchCommonLocations ? "开" : "关（--path-only）")}");
        builder.AppendLine();
        foreach (var item in Items)
        {
            var mark = item.Ok ? "[+]" : (item.Blocking ? "[x]" : "[!]");
            builder.AppendLine($"{mark} {item.Label}: {item.Detail}");
            if (!item.Ok && item.FixCommand is not null)
                builder.AppendLine($"    修复: {item.FixCommand}");
            if (!item.Ok && item.FixUrl is not null)
                builder.AppendLine($"    下载: {item.FixUrl}");
        }
        builder.AppendLine();
        builder.AppendLine(Ok ? "结论: 环境就绪，可以启动。" : "结论: 缺少运行环境，无法启动 dsh。");
        return builder.ToString();
    }
}

internal static class Prerequisites
{
    public static string ReportPath => Path.Combine(AppConfig.DataDirectory, "env-check.txt");

    public static PrerequisiteReport Check(AppConfig config)
    {
        var toolchain = ToolLocator.Probe(config);
        var items = new List<PrerequisiteItem> { NodeItem(toolchain), DshItem(toolchain), NpmItem(toolchain) };
        return new PrerequisiteReport { Toolchain = toolchain, Items = items };
    }

    static PrerequisiteItem NodeItem(Toolchain toolchain)
    {
        const string fixCommand = "winget install OpenJS.NodeJS.LTS";
        const string fixUrl = "https://nodejs.org/en/download";

        if (string.IsNullOrEmpty(toolchain.NodePath))
        {
            return new PrerequisiteItem("Node.js 运行时", false, true,
                "未找到 node.exe（PATH 和常见安装位置都没有）", fixCommand, fixUrl);
        }

        if (!toolchain.NodeRunnable)
        {
            return new PrerequisiteItem("Node.js 运行时", false, true,
                $"{toolchain.NodePath} 无法执行：{toolchain.NodeProbeError}", fixCommand, fixUrl);
        }

        if (toolchain.NodeVersion is not null && toolchain.NodeVersion.Major < ToolLocator.MinimumNodeMajor)
        {
            return new PrerequisiteItem("Node.js 运行时", false, true,
                $"版本过低：{toolchain.NodeVersionText}（需要 {ToolLocator.MinimumNodeMajor} 或更高）　{toolchain.NodePath}",
                fixCommand, fixUrl);
        }

        return new PrerequisiteItem("Node.js 运行时", true, true,
            $"{toolchain.NodeVersionText ?? "版本未知"}　{toolchain.NodePath}", fixCommand, fixUrl);
    }

    static PrerequisiteItem DshItem(Toolchain toolchain)
    {
        const string fixCommand = "npm i -g @deepseek-ai/dsh";
        const string fixUrl = "https://www.npmjs.com/package/@deepseek-ai/dsh";

        if (string.IsNullOrEmpty(toolchain.DshBinJs))
        {
            return new PrerequisiteItem("dsh 命令行", false, true,
                "未找到 @deepseek-ai/dsh 的 bin.js（可用 npm 全局安装）", fixCommand, fixUrl);
        }

        var version = string.IsNullOrEmpty(toolchain.DshVersion) ? "版本未知" : $"v{toolchain.DshVersion}";
        return new PrerequisiteItem("dsh 命令行", true, true,
            $"{version}　{toolchain.DshBinJs}", fixCommand, fixUrl);
    }

    /// <summary>Informational: npm is only needed to install the two blocking items.</summary>
    static PrerequisiteItem NpmItem(Toolchain toolchain)
    {
        const string fixCommand = "winget install OpenJS.NodeJS.LTS";
        const string fixUrl = "https://nodejs.org/en/download";

        return string.IsNullOrEmpty(toolchain.NpmPath)
            ? new PrerequisiteItem("npm（安装上面缺的组件要用）", false, false,
                "未找到 npm.cmd（安装 Node.js 时会自带）", fixCommand, fixUrl)
            : new PrerequisiteItem("npm（安装上面缺的组件要用）", true, false,
                toolchain.NpmPath, fixCommand, fixUrl);
    }

    /// <summary>Always writes the report, so a failed start-up can be diagnosed after the fact.</summary>
    public static string WriteReportFile(PrerequisiteReport report)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            File.WriteAllText(ReportPath, report.ToPlainText(), new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics are best-effort only.
        }
        return ReportPath;
    }
}
