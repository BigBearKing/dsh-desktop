using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DshDesktop;

/// <summary>Bounded, thread-safe tail of the child process output, for diagnostics.</summary>
internal sealed class ServerLog
{
    readonly Queue<string> _lines = new();

    public void Add(string? line)
    {
        if (line is null)
            return;
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > 500)
                _lines.Dequeue();
        }
    }

    public string Tail(int count = 80)
    {
        lock (_lines)
        {
            if (_lines.Count == 0)
                return "(没有输出)";
            return string.Join(Environment.NewLine, _lines.Skip(Math.Max(0, _lines.Count - count)));
        }
    }
}

/// <summary>
/// Owns the `dsh web` server behind the window: either attaches to one that is already
/// running, or spawns a private instance on an OS-assigned port and guarantees it dies
/// with this process (Windows job object, kill-on-job-close).
/// </summary>
internal sealed class DshServer : IDisposable
{
    const string DshMarker = "__ModuleLoader__";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly Process? _process;
    readonly IntPtr _job;
    readonly ServerLog _log;
    bool _disposed;

    public Uri BaseUrl { get; }
    public bool Owned { get; }
    public string LaunchCommand { get; }

    /// <summary>Raised when a server we spawned exits on its own (crash, Ctrl+C, ...).</summary>
    public event EventHandler? Exited;

    DshServer(Uri baseUrl, bool owned, Process? process, IntPtr job, string launchCommand, ServerLog log)
    {
        BaseUrl = baseUrl;
        Owned = owned;
        _process = process;
        _job = job;
        LaunchCommand = launchCommand;
        _log = log;
    }

    public static async Task<DshServer> StartAsync(AppConfig config, Toolchain toolchain, Action<string>? status, CancellationToken ct)
    {
        if (config.ReuseExistingServer && config.ExistingServerPort > 0)
        {
            var existing = new Uri($"http://127.0.0.1:{config.ExistingServerPort}/");
            status?.Invoke($"检测到已有实例，正在确认端口 {config.ExistingServerPort} …");
            if (await IsDshAsync(existing, ct).ConfigureAwait(false))
            {
                status?.Invoke($"已连接到运行中的 dsh（端口 {config.ExistingServerPort}）");
                return new DshServer(existing, owned: false, process: null, job: IntPtr.Zero,
                    launchCommand: "(复用已在运行的实例，退出时不会关闭它)", log: new ServerLog());
            }
        }

        var startInfo = BuildStartInfo(config, toolchain, out var command);
        var urlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var log = new ServerLog();

        void OnLine(string? line)
        {
            log.Add(line);
            if (line is null)
                return;
            const string prefix = "dsh web:";
            var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var candidate = line[(index + prefix.Length)..].Trim();
                if (candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    urlSource.TrySetResult(candidate);
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        if (!process.Start())
            throw new InvalidOperationException("无法启动 dsh 进程。");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Belt and braces: a job object with kill-on-close means the child cannot outlive us,
        // even if this process is killed from Task Manager.
        var job = JobObject.CreateKillOnClose();
        if (job != IntPtr.Zero)
        {
            try
            {
                if (!JobObject.Assign(job, process.Handle))
                {
                    JobObject.Close(job);
                    job = IntPtr.Zero;
                }
            }
            catch
            {
                JobObject.Close(job);
                job = IntPtr.Zero;
            }
        }

        try
        {
            status?.Invoke("正在启动 dsh web 服务 …");
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!urlSource.Task.IsCompleted)
            {
                if (process.HasExited)
                    throw new InvalidOperationException(
                        $"dsh 进程启动后立即退出（退出码 {process.ExitCode}）。{Environment.NewLine}{log.Tail(40)}");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"等待 dsh 输出服务地址超时。{Environment.NewLine}{log.Tail(40)}");
                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            var url = new Uri(await urlSource.Task.ConfigureAwait(false));
            status?.Invoke($"服务已就绪，正在等待 {url.Authority} 响应 …");
            await WaitForReadyAsync(url, process, ct).ConfigureAwait(false);

            var server = new DshServer(url, owned: true, process, job, command, log);
            process.Exited += (_, _) => server.Exited?.Invoke(server, EventArgs.Empty);
            return server;
        }
        catch
        {
            KillTree(process);
            process.Dispose();
            JobObject.Close(job);
            throw;
        }
    }

    public string LogTail(int lines = 80) => _log.Tail(lines);

    static async Task WaitForReadyAsync(Uri url, Process process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"dsh 进程在就绪前退出（退出码 {process.ExitCode}）。");
            try
            {
                if (await IsDshAsync(url, ct).ConfigureAwait(false))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"等待 {url} 就绪超时。{(last is null ? string.Empty : " 最后一次错误：" + last.Message)}");
    }

    /// <summary>True when the URL answers with the DSH web shell (so we do not adopt a stranger).</summary>
    static async Task<bool> IsDshAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Contains(DshMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static ProcessStartInfo BuildStartInfo(AppConfig config, Toolchain toolchain, out string command)
    {
        var port = config.Port.ToString(CultureInfo.InvariantCulture);
        var binJs = toolchain.DshBinJs;

        ProcessStartInfo startInfo;
        if (!string.IsNullOrEmpty(binJs))
        {
            // Prefer launching node with the CLI entry point directly: no intermediate cmd.exe,
            // so the process we own is the process we can kill.
            var node = toolchain.NodePath ?? "node";
            startInfo = new ProcessStartInfo(node);
            startInfo.ArgumentList.Add(binJs);
            command = $"\"{node}\" \"{binJs}\" web --no-open --port {port}";
        }
        else
        {
            var dshCmd = ToolLocator.FindOnPath("dsh.cmd")
                ?? throw new FileNotFoundException(
                    "找不到 dsh 命令。请在 config.json 里填写 DshBinJs（指向 @deepseek-ai/dsh/lib/bin.js），" +
                    "或把 npm 全局目录加入 PATH。");
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(dshCmd);
            command = $"\"{dshCmd}\" web --no-open --port {port}";
        }

        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--no-open");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port);

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        startInfo.Environment["NO_COLOR"] = "1";

        var workspace = ToolLocator.ResolveWorkspace(config);
        if (workspace is not null)
            startInfo.WorkingDirectory = workspace;

        return startInfo;
    }

    static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Owned && _process is not null)
            KillTree(_process);

        _process?.Dispose();
        JobObject.Close(_job);
    }
}

/// <summary>Minimal job-object interop: kill every assigned process when the handle closes.</summary>
internal static class JobObject
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    public static IntPtr CreateKillOnClose()
    {
        var job = IntPtr.Zero;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new JobObjectExtendedLimitInformationStruct();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            var length = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return job;
        }
        catch
        {
            if (job != IntPtr.Zero)
                CloseHandle(job);
            return IntPtr.Zero;
        }
    }

    public static bool Assign(IntPtr job, IntPtr processHandle)
    {
        if (job == IntPtr.Zero || processHandle == IntPtr.Zero)
            return false;
        return AssignProcessToJobObject(job, processHandle);
    }

    public static void Close(IntPtr job)
    {
        if (job != IntPtr.Zero)
            CloseHandle(job);
    }
}
