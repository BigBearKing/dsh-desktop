using System.Diagnostics;
using System.Text.Json;

namespace DshDesktop;

/// <summary>Raw facts about the toolchain found on this machine.</summary>
internal sealed class Toolchain
{
    public string? NodePath { get; set; }
    public bool NodeRunnable { get; set; }
    public Version? NodeVersion { get; set; }
    public string? NodeVersionText { get; set; }
    public string? NodeProbeError { get; set; }

    public string? DshBinJs { get; set; }
    public string? DshRoot { get; set; }
    public string? DshVersion { get; set; }

    public string? NpmPath { get; set; }

    public bool NodeOk => NodeRunnable;
    public bool DshOk => !string.IsNullOrEmpty(DshBinJs);
    public bool Ok => NodeOk && DshOk;
}

/// <summary>
/// Locates node, the dsh CLI entry point and npm.
///
/// Both the start-up gate and the server host use this, so "what we report" and "what we
/// launch" can never disagree.
/// </summary>
internal static class ToolLocator
{
    /// <summary>
    /// When false, only PATH is consulted (no known install directories are probed).
    /// Set by the <c>--path-only</c> diagnostic switch.
    /// </summary>
    public static bool SearchCommonLocations { get; set; } = true;

    /// <summary>Node major version we refuse to start under.</summary>
    public const int MinimumNodeMajor = 18;

    public static Toolchain Probe(AppConfig config)
    {
        var toolchain = new Toolchain
        {
            NodePath = FindNode(config),
            DshBinJs = FindDshBinJs(config),
            NpmPath = FindNpm(),
        };

        if (!string.IsNullOrEmpty(toolchain.DshBinJs))
        {
            toolchain.DshRoot = Directory.GetParent(Path.GetDirectoryName(toolchain.DshBinJs)!)?.FullName;
            toolchain.DshVersion = ReadPackageVersion(toolchain.DshRoot);
        }

        if (!string.IsNullOrEmpty(toolchain.NodePath))
        {
            var (ok, versionText, error) = RunVersionProbe(toolchain.NodePath);
            toolchain.NodeRunnable = ok;
            toolchain.NodeVersionText = versionText;
            toolchain.NodeProbeError = error;
            toolchain.NodeVersion = ParseVersion(versionText);
        }

        return toolchain;
    }

    // ── lookups ─────────────────────────────────────────────────────────────────────────────

    public static string? FindNode(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.NodePath) && File.Exists(config.NodePath))
            return config.NodePath;

        var onPath = FindOnPath("node.exe");
        if (onPath is not null)
            return onPath;

        if (!SearchCommonLocations)
            return null;

        foreach (var candidate in CommonNodeLocations())
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Places node ends up on Windows. Worth checking because a process started from Explorer
    /// keeps the PATH it had at logon, so a just-installed Node may not be on it yet.
    /// </summary>
    static IEnumerable<string> CommonNodeLocations()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(programFiles, "nodejs", "node.exe");
        yield return Path.Combine(programFilesX86, "nodejs", "node.exe");
        yield return Path.Combine(localAppData, "Programs", "nodejs", "node.exe");   // nvm-windows / volta
        yield return Path.Combine(localAppData, "Volta", "bin", "node.exe");
        yield return Path.Combine(appData, "nvm", "node.exe");
        yield return Path.Combine(userProfile, "scoop", "shims", "node.exe");
        yield return @"C:\ProgramData\chocolatey\bin\node.exe";
    }

    public static string? FindDshBinJs(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DshBinJs) && File.Exists(config.DshBinJs))
            return config.DshBinJs;

        foreach (var candidate in DshCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    static IEnumerable<string?> DshCandidates()
    {
        // The shim on PATH is authoritative and cheap: it tells us where npm put the global tree.
        var shim = FindOnPath("dsh.cmd");
        if (shim is not null)
        {
            var root = Path.GetDirectoryName(shim);
            if (root is not null)
                yield return Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        }

        if (!SearchCommonLocations)
            yield break;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

        var prefix = Environment.GetEnvironmentVariable("npm_config_prefix");
        if (!string.IsNullOrWhiteSpace(prefix))
            yield return Path.Combine(prefix, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "nodejs", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    }

    public static string? FindNpm() => FindOnPath("npm.cmd") ?? FindOnPath("npm.exe");

    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Malformed PATH entry; skip it.
            }
        }
        return null;
    }

    public static string? ResolveWorkspace(AppConfig config)
    {
        var candidate = config.WorkspaceDirectory;
        if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            return candidate;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(home) ? home : null;
    }

    // ── version probing ─────────────────────────────────────────────────────────────────────

    static (bool Ok, string? VersionText, string? Error) RunVersionProbe(string nodePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(nodePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, null, "进程无法启动");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, null, "执行超时");
            }
            if (process.ExitCode != 0)
                return (false, stdout.Trim(), $"退出码 {process.ExitCode} {stderr.Trim()}".Trim());

            var text = stdout.Trim();
            return (true, text, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var cleaned = text.Trim().TrimStart('v', 'V');
        var cut = cleaned.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0)
            cleaned = cleaned[..cut];
        return Version.TryParse(cleaned, out var version) ? version : null;
    }

    static string? ReadPackageVersion(string? root)
    {
        if (string.IsNullOrEmpty(root))
            return null;
        try
        {
            var manifest = Path.Combine(root, "package.json");
            if (!File.Exists(manifest))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
