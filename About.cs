using System.Diagnostics;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop;

/// <summary>Version/build facts and the plain-text block shared by the About dialog and --about.</summary>
internal static class AboutInfo
{
    public const string RepositoryUrl = "https://github.com/BigBearKing/dsh-desktop";
    public const string LicenseName = "MIT";
    public const string AppName = "DeepSeek Harness 桌面壳";

    /// <summary>Informational version, e.g. "1.0.0" or "1.0.0+aa45ec8" in a git checkout.</summary>
    static string InformationalVersion
    {
        get
        {
            try
            {
                var assembly = typeof(AboutInfo).Assembly;
                var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;

                var file = FileVersionInfo.GetVersionInfo(assembly.Location);
                return file.ProductVersion ?? file.FileVersion ?? "未知";
            }
            catch
            {
                return "未知";
            }
        }
    }

    /// <summary>Version without the build metadata suffix.</summary>
    public static string VersionText
    {
        get
        {
            var value = InformationalVersion;
            var plus = value.IndexOf('+');
            return plus > 0 ? value[..plus] : value;
        }
    }

    /// <summary>Short source revision the SDK stamped in, when built from a git checkout.</summary>
    public static string? BuildRevision
    {
        get
        {
            var value = InformationalVersion;
            var plus = value.IndexOf('+');
            if (plus <= 0 || plus == value.Length - 1)
                return null;
            var revision = value[(plus + 1)..];
            return revision.Length > 7 ? revision[..7] : revision;
        }
    }

    public static string WebView2Version
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                return "未安装 / 未知";
            }
        }
    }

    public static string FullTitle =>
        BuildRevision is null ? $"{AppName} {VersionText}" : $"{AppName} {VersionText}（修订 {BuildRevision}）";

    /// <summary>The copyable block: everything needed to describe a bug report.</summary>
    public static string ToPlainText(Toolchain? toolchain)
    {
        var lines = new List<string>
        {
            FullTitle,
            $"dsh         : {(string.IsNullOrEmpty(toolchain?.DshVersion) ? "未检测到" : "v" + toolchain!.DshVersion)}",
            $"Node.js     : {toolchain?.NodeVersionText ?? "未检测到"}",
            $"WebView2    : {WebView2Version}",
            $"许可证      : {LicenseName}",
            $"项目主页    : {RepositoryUrl}",
            $"配置目录    : {AppConfig.DataDirectory}",
        };

        if (!string.IsNullOrEmpty(toolchain?.DshBinJs))
            lines.Add($"dsh 入口    : {toolchain!.DshBinJs}");
        if (!string.IsNullOrEmpty(toolchain?.NodePath))
            lines.Add($"Node 路径   : {toolchain!.NodePath}");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>The About window: version, environment, licence and a clickable project link.</summary>
internal sealed class AboutForm : Form
{
    static readonly Color CanvasColor = Color.FromArgb(0x0B, 0x0F, 0x14);
    static readonly Color TextColor = Color.FromArgb(0xE6, 0xED, 0xF3);
    static readonly Color MutedColor = Color.FromArgb(0x7D, 0x8A, 0x99);
    static readonly Color LinkColor = Color.FromArgb(0x6E, 0x9B, 0xFF);

    readonly Toolchain? _toolchain;

    public AboutForm(Toolchain? toolchain)
    {
        _toolchain = toolchain;

        Text = "关于 " + AboutInfo.AppName;
        BackColor = CanvasColor;
        ForeColor = TextColor;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9.5F);
        ClientSize = new Size(600, 340);

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Keep the default icon.
        }

        BuildLayout();
    }

    void BuildLayout()
    {
        var title = new Label
        {
            Text = AboutInfo.AppName,
            Font = new Font("Segoe UI", 16F),
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(22, 20),
        };

        var version = new Label
        {
            Text = $"版本 {AboutInfo.VersionText}" +
                   (AboutInfo.BuildRevision is null ? string.Empty : $"　·　修订 {AboutInfo.BuildRevision}"),
            Font = new Font("Segoe UI", 10F),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(24, 56),
        };

        var link = new LinkLabel
        {
            Text = AboutInfo.RepositoryUrl,
            Font = new Font("Segoe UI", 10F),
            LinkColor = LinkColor,
            ActiveLinkColor = Color.White,
            VisitedLinkColor = LinkColor,
            AutoSize = true,
            Location = new Point(24, 80),
        };
        link.LinkClicked += (_, _) => OpenUrl(AboutInfo.RepositoryUrl);

        var license = new Label
        {
            Text = $"许可证：{AboutInfo.LicenseName}",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(24, 106),
        };

        var details = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Color.FromArgb(0x05, 0x08, 0x0B),
            ForeColor = Color.FromArgb(0xA8, 0xB6, 0xC4),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9F),
            Location = new Point(24, 136),
            Size = new Size(552, 140),
            Text = AboutInfo.ToPlainText(_toolchain).Replace("\n", Environment.NewLine),
        };

        var copyButton = MakeButton("复制信息", 24);
        var openButton = MakeButton("打开项目主页", 140);
        var closeButton = MakeButton("关闭", 270);
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(AboutInfo.ToPlainText(_toolchain));
            }
            catch
            {
                // Clipboard can be locked by another process.
            }
        };
        openButton.Click += (_, _) => OpenUrl(AboutInfo.RepositoryUrl);
        closeButton.Click += (_, _) => Close();

        Controls.Add(title);
        Controls.Add(version);
        Controls.Add(link);
        Controls.Add(license);
        Controls.Add(details);
        Controls.Add(copyButton);
        Controls.Add(openButton);
        Controls.Add(closeButton);

        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    static Button MakeButton(string text, int left)
    {
        return new Button
        {
            Text = text,
            Left = left,
            Top = 290,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 4, 12, 4),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = Color.FromArgb(0x2C, 0x38, 0x46) },
            BackColor = Color.FromArgb(0x1B, 0x24, 0x30),
            ForeColor = TextColor,
            UseVisualStyleBackColor = false,
        };
    }

    static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No handler for the scheme.
        }
    }
}
