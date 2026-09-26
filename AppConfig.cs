using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshDesktop;

/// <summary>
/// User-editable settings. Lives in %LOCALAPPDATA%\DeepSeekHarness\config.json and is
/// only ever written once (as a commented template) so hand edits are never clobbered.
/// </summary>
internal sealed class AppConfig
{
    /// <summary>Folder handed to `dsh web` as its working directory (the agent workspace).</summary>
    public string? WorkspaceDirectory { get; set; }

    /// <summary>Port for the private server. 0 = let the OS pick a free one (recommended).</summary>
    public int Port { get; set; }

    /// <summary>Attach to an already-running `dsh web` instead of starting a private one.</summary>
    public bool ReuseExistingServer { get; set; } = true;

    /// <summary>Port probed when <see cref="ReuseExistingServer"/> is on.</summary>
    public int ExistingServerPort { get; set; } = 3080;

    /// <summary>Explicit node.exe path. Empty = look it up on PATH.</summary>
    public string? NodePath { get; set; }

    /// <summary>Explicit path to the dsh CLI entry point (lib/bin.js). Empty = auto-detect.</summary>
    public string? DshBinJs { get; set; }

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (!File.Exists(ConfigPath))
                File.WriteAllText(ConfigPath, Template(DefaultWorkspace()));

            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), ReadOptions);
            if (cfg is null)
                return new AppConfig { WorkspaceDirectory = DefaultWorkspace() };
            if (string.IsNullOrWhiteSpace(cfg.WorkspaceDirectory))
                cfg.WorkspaceDirectory = DefaultWorkspace();
            return cfg;
        }
        catch
        {
            return new AppConfig { WorkspaceDirectory = DefaultWorkspace() };
        }
    }

    static string DefaultWorkspace()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var codex = Path.Combine(documents, "Codex");
        if (Directory.Exists(codex))
            return codex;
        if (Directory.Exists(documents))
            return documents;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    static string Template(string workspace) => $$"""
        // DeepSeek Harness 桌面壳配置。JSON 允许注释和尾随逗号，改完重启程序生效。
        {
          // dsh 的工作目录（智能体默认可读写的目录）。
          "WorkspaceDirectory": {{JsonSerializer.Serialize(workspace)}},

          // 私有服务端口。0 = 让系统自动挑一个空闲端口，避免和已有实例冲突。
          "Port": 0,

          // true = 如果下面这个端口上已经跑着一个 dsh web，就直接连上去，不再另起一个。
          "ReuseExistingServer": true,
          "ExistingServerPort": 3080,

          // 留空表示自动探测；装法特殊时再填绝对路径。
          "NodePath": "",
          "DshBinJs": ""
        }
        """;
}
