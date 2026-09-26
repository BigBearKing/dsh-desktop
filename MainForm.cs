using System.Diagnostics;
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop;

/// <summary>
/// The shell window: a chromeless WebView2 surface aimed at a private `dsh web` server,
/// with its own start-up / failure states and lifecycle ownership of that server.
/// </summary>
internal sealed class MainForm : Form
{
    static readonly Color CanvasColor = Color.FromArgb(0x0B, 0x0F, 0x14);
    static readonly Color TextColor = Color.FromArgb(0xE6, 0xED, 0xF3);
    static readonly Color MutedColor = Color.FromArgb(0x7D, 0x8A, 0x99);
    static readonly Color ButtonColor = Color.FromArgb(0x1B, 0x24, 0x30);

    readonly AppConfig _config;
    readonly WindowGeometry _geometry;

    readonly WebView2 _webView = new();
    readonly Panel _splash = new();
    readonly Panel _errorPanel = new();
    readonly Label _splashTitle = new();
    readonly Label _splashDetail = new();
    readonly Label _splashHint = new();
    readonly Label _errorTitle = new();
    readonly Label _errorDetail = new();
    readonly TextBox _errorLog = new();
    readonly Button _retryButton = new();
    readonly Button _browserButton = new();
    readonly Button _folderButton = new();
    readonly Button _quitButton = new();
    readonly System.Windows.Forms.Timer _dotsTimer = new() { Interval = 400 };

    readonly Panel _envPanel = new();
    readonly Label _envTitle = new();
    readonly Label _envSubtitle = new();
    readonly FlowLayoutPanel _envRows = new();
    readonly TextBox _envFixes = new();
    readonly Button _recheckButton = new();
    readonly Button _copyFixButton = new();
    readonly Button _downloadButton = new();
    readonly Button _envFolderButton = new();
    readonly Button _envQuitButton = new();

    DshServer? _server;
    Toolchain? _toolchain;
    PrerequisiteReport? _report;
    CancellationTokenSource? _startupCts;
    bool _webViewReady;
    bool _starting;
    bool _closing;
    int _dotCount;
    string _splashBase = string.Empty;

    public MainForm()
    {
        _config = AppConfig.Load();
        _geometry = WindowGeometry.Load();

        Text = "DeepSeek Harness";
        BackColor = CanvasColor;
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Keep the default icon.
        }

        if (_geometry.TryGetBounds(out var saved))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
        else
        {
            // No usable saved geometry: open at a comfortable size, capped to the screen.
            var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            Size = new Size(
                Math.Min(1360, Math.Max(880, (int)(work.Width * 0.9))),
                Math.Min(900, Math.Max(600, (int)(work.Height * 0.9))));
        }

        BuildLayout();

        if (_geometry.Maximized)
            WindowState = FormWindowState.Maximized;

        Shown += async (_, _) => await StartAsync();
        FormClosing += OnFormClosing;
    }

    // ── layout ──────────────────────────────────────────────────────────────────────────────

    void BuildLayout()
    {
        SuspendLayout();

        _webView.Dock = DockStyle.Fill;
        _webView.Visible = false;
        try
        {
            _webView.DefaultBackgroundColor = CanvasColor;
        }
        catch
        {
            // Older WebView2 runtimes lack this property; only a cosmetic flash is lost.
        }

        BuildSplash();
        BuildErrorPanel();
        BuildEnvironmentPanel();

        Controls.Add(_envPanel);
        Controls.Add(_errorPanel);
        Controls.Add(_splash);
        Controls.Add(_webView);
        _splash.BringToFront();

        _dotsTimer.Tick += (_, _) =>
        {
            _dotCount = (_dotCount + 1) % 4;
            _splashTitle.Text = _splashBase + new string('.', _dotCount);
        };

        ResumeLayout(true);
    }

    void BuildSplash()
    {
        _splash.Dock = DockStyle.Fill;
        _splash.BackColor = CanvasColor;

        _splashTitle.AutoSize = true;
        _splashTitle.Anchor = AnchorStyles.None;
        _splashTitle.ForeColor = TextColor;
        _splashTitle.Font = new Font("Segoe UI", 20F, FontStyle.Regular);
        _splashTitle.Text = "DeepSeek Harness";
        _splashTitle.Margin = new Padding(0, 0, 0, 10);

        _splashDetail.AutoSize = true;
        _splashDetail.Anchor = AnchorStyles.None;
        _splashDetail.ForeColor = MutedColor;
        _splashDetail.Font = new Font("Segoe UI", 10F);
        _splashDetail.MaximumSize = new Size(620, 0);
        _splashDetail.TextAlign = ContentAlignment.MiddleCenter;
        _splashDetail.Text = "正在启动本地服务";

        _splashHint.AutoSize = true;
        _splashHint.Anchor = AnchorStyles.None;
        _splashHint.ForeColor = Color.FromArgb(0x4A, 0x55, 0x63);
        _splashHint.Font = new Font("Consolas", 8.5F);
        _splashHint.MaximumSize = new Size(720, 0);
        _splashHint.TextAlign = ContentAlignment.MiddleCenter;
        _splashHint.Text = _config.WorkspaceDirectory ?? string.Empty;
        _splashHint.Margin = new Padding(0, 16, 0, 0);

        var body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.Controls.Add(_splashTitle, 0, 0);
        body.Controls.Add(_splashDetail, 0, 1);
        body.Controls.Add(_splashHint, 0, 2);

        _splash.Controls.Add(body);
        _splash.Resize += (_, _) => CenterIn(_splash, body);
    }

    void BuildErrorPanel()
    {
        _errorPanel.Dock = DockStyle.Fill;
        _errorPanel.BackColor = CanvasColor;
        _errorPanel.Visible = false;

        _errorTitle.AutoSize = true;
        _errorTitle.Anchor = AnchorStyles.None;
        _errorTitle.ForeColor = Color.FromArgb(0xFF, 0x9A, 0x8A);
        _errorTitle.Font = new Font("Segoe UI", 15F);
        _errorTitle.Text = "启动失败";
        _errorTitle.Margin = new Padding(0, 0, 0, 8);

        _errorDetail.AutoSize = true;
        _errorDetail.Anchor = AnchorStyles.None;
        _errorDetail.ForeColor = MutedColor;
        _errorDetail.Font = new Font("Segoe UI", 9.5F);
        _errorDetail.MaximumSize = new Size(640, 0);
        _errorDetail.TextAlign = ContentAlignment.MiddleCenter;

        _errorLog.Multiline = true;
        _errorLog.ReadOnly = true;
        _errorLog.ScrollBars = ScrollBars.Both;
        _errorLog.WordWrap = false;
        _errorLog.BackColor = Color.FromArgb(0x05, 0x08, 0x0B);
        _errorLog.ForeColor = Color.FromArgb(0xA8, 0xB6, 0xC4);
        _errorLog.BorderStyle = BorderStyle.FixedSingle;
        _errorLog.Font = new Font("Consolas", 8.5F);
        _errorLog.Size = new Size(640, 190);
        _errorLog.Anchor = AnchorStyles.None;
        _errorLog.Margin = new Padding(0, 14, 0, 14);

        StyledButton(_retryButton, "重试");
        StyledButton(_browserButton, "在浏览器中打开");
        StyledButton(_folderButton, "打开配置文件夹");
        StyledButton(_quitButton, "退出");
        _retryButton.Click += OnRetryClick;
        _browserButton.Click += (_, _) => { if (_server is not null) OpenExternal(_server.BaseUrl.ToString()); };
        _folderButton.Click += (_, _) => OpenFolder(AppConfig.DataDirectory);
        _quitButton.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(_retryButton);
        buttons.Controls.Add(_browserButton);
        buttons.Controls.Add(_folderButton);
        buttons.Controls.Add(_quitButton);

        var body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.Controls.Add(_errorTitle, 0, 0);
        body.Controls.Add(_errorDetail, 0, 1);
        body.Controls.Add(_errorLog, 0, 2);
        body.Controls.Add(buttons, 0, 3);

        _errorPanel.Controls.Add(body);
        _errorPanel.Resize += (_, _) => CenterIn(_errorPanel, body);
    }

    void BuildEnvironmentPanel()
    {
        _envPanel.Dock = DockStyle.Fill;
        _envPanel.BackColor = CanvasColor;
        _envPanel.Visible = false;

        _envTitle.AutoSize = true;
        _envTitle.Anchor = AnchorStyles.None;
        _envTitle.ForeColor = Color.FromArgb(0xFF, 0xC1, 0x6B);
        _envTitle.Font = new Font("Segoe UI", 15F);
        _envTitle.Text = "缺少运行环境";
        _envTitle.Margin = new Padding(0, 0, 0, 6);

        _envSubtitle.AutoSize = true;
        _envSubtitle.Anchor = AnchorStyles.None;
        _envSubtitle.ForeColor = MutedColor;
        _envSubtitle.Font = new Font("Segoe UI", 9.5F);
        _envSubtitle.MaximumSize = new Size(640, 0);
        _envSubtitle.TextAlign = ContentAlignment.MiddleCenter;
        _envSubtitle.Text = "这台机器上还没有找到启动 dsh 所需的组件。装好下面标 ✗ 的组件后，点「重新检测」。";
        _envSubtitle.Margin = new Padding(0, 0, 0, 16);

        _envRows.AutoSize = true;
        _envRows.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _envRows.Anchor = AnchorStyles.None;
        _envRows.FlowDirection = FlowDirection.TopDown;
        _envRows.WrapContents = false;
        _envRows.BackColor = Color.Transparent;
        _envRows.Margin = new Padding(0);

        _envFixes.Multiline = true;
        _envFixes.ReadOnly = true;
        _envFixes.ScrollBars = ScrollBars.Vertical;
        _envFixes.WordWrap = true;
        _envFixes.BackColor = Color.FromArgb(0x05, 0x08, 0x0B);
        _envFixes.ForeColor = Color.FromArgb(0xA8, 0xB6, 0xC4);
        _envFixes.BorderStyle = BorderStyle.FixedSingle;
        _envFixes.Font = new Font("Consolas", 9F);
        _envFixes.Size = new Size(640, 74);
        _envFixes.Anchor = AnchorStyles.None;
        _envFixes.Margin = new Padding(0, 16, 0, 16);

        StyledButton(_recheckButton, "重新检测");
        StyledButton(_copyFixButton, "复制安装命令");
        StyledButton(_downloadButton, "打开下载页");
        StyledButton(_envFolderButton, "打开配置文件夹");
        StyledButton(_envQuitButton, "退出");
        _recheckButton.Click += OnRecheckClick;
        _copyFixButton.Click += OnCopyFixesClick;
        _downloadButton.Click += (_, _) =>
        {
            var url = _report?.FirstFixUrl;
            if (!string.IsNullOrEmpty(url))
                OpenExternal(url);
        };
        _envFolderButton.Click += (_, _) => OpenFolder(AppConfig.DataDirectory);
        _envQuitButton.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(_recheckButton);
        buttons.Controls.Add(_copyFixButton);
        buttons.Controls.Add(_downloadButton);
        buttons.Controls.Add(_envFolderButton);
        buttons.Controls.Add(_envQuitButton);

        var body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.Controls.Add(_envTitle, 0, 0);
        body.Controls.Add(_envSubtitle, 0, 1);
        body.Controls.Add(_envRows, 0, 2);
        body.Controls.Add(_envFixes, 0, 3);
        body.Controls.Add(buttons, 0, 4);

        _envPanel.Controls.Add(body);
        _envPanel.Resize += (_, _) => CenterIn(_envPanel, body);
    }

    /// <summary>Renders the checklist rows for one check result.</summary>
    void ShowEnvironment(PrerequisiteReport report)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowEnvironment(report)));
            return;
        }

        _dotsTimer.Stop();
        _report = report;

        _envRows.SuspendLayout();
        _envRows.Controls.Clear();
        foreach (var item in report.Items)
            _envRows.Controls.Add(BuildChecklistRow(item));
        _envRows.ResumeLayout(true);

        var fixes = report.FixCommands();
        _envFixes.Text = string.IsNullOrEmpty(fixes)
            ? "（没有需要执行的命令）"
            : fixes;

        var canFix = report.FirstFixUrl is not null;
        _downloadButton.Visible = canFix;
        _copyFixButton.Visible = !string.IsNullOrEmpty(fixes);

        _envPanel.Visible = true;
        _envPanel.BringToFront();
        _splash.Visible = false;
        _errorPanel.Visible = false;
        _webView.Visible = false;
    }

    Control BuildChecklistRow(PrerequisiteItem item)
    {
        var glyph = new Label
        {
            Text = item.Ok ? "✓" : (item.Blocking ? "✗" : "!"),
            ForeColor = item.Ok ? Color.FromArgb(0x7B, 0xD8, 0x8B) : Color.FromArgb(0xFF, 0x8A, 0x7A),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 10, 0),
        };

        var label = new Label
        {
            Text = item.Label,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };

        var detail = new Label
        {
            Text = item.Detail,
            ForeColor = MutedColor,
            Font = new Font("Consolas", 8.5F),
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 0, 0),
        };

        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        stack.Controls.Add(label, 0, 0);
        stack.Controls.Add(detail, 0, 1);

        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10),
        };
        row.Controls.Add(glyph, 0, 0);
        row.Controls.Add(stack, 1, 0);
        return row;
    }

    async void OnRecheckClick(object? sender, EventArgs e)
    {
        if (_starting)
            return;
        _recheckButton.Enabled = false;
        try
        {
            _envPanel.Visible = false;
            DisposeServer();
            await StartAsync();
        }
        finally
        {
            _recheckButton.Enabled = true;
        }
    }

    void OnCopyFixesClick(object? sender, EventArgs e)
    {
        try
        {
            var text = _report?.FixCommands();
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another process; nothing useful to do.
        }
    }

    static void StyledButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(0x2C, 0x38, 0x46);
        button.BackColor = ButtonColor;
        button.ForeColor = TextColor;
        button.Font = new Font("Segoe UI", 9.5F);
        button.Padding = new Padding(12, 5, 12, 5);
        button.Margin = new Padding(4, 0, 4, 0);
        button.UseVisualStyleBackColor = false;
    }

    static void CenterIn(Control parent, Control child)
    {
        child.Left = Math.Max(0, (parent.ClientSize.Width - child.Width) / 2);
        child.Top = Math.Max(0, (parent.ClientSize.Height - child.Height) / 2);
    }

    // ── startup ─────────────────────────────────────────────────────────────────────────────

    async Task StartAsync()
    {
        if (_starting)
            return;
        _starting = true;
        _startupCts = new CancellationTokenSource();

        try
        {
            // Gate on the prerequisites before touching the server: a clean machine should get a
            // checklist, not a FileNotFoundException from deep inside the launcher.
            ShowSplash("正在检查运行环境");
            SetSplashDetail("正在查找 Node.js、dsh 和 npm …");
            _splashHint.Text = _config.WorkspaceDirectory ?? string.Empty;

            var report = await Task.Run(() => Prerequisites.Check(_config), _startupCts.Token);
            Prerequisites.WriteReportFile(report);
            _report = report;

            if (!report.Ok)
            {
                ShowEnvironment(report);
                return;
            }

            _toolchain = report.Toolchain;

            ShowSplash("正在启动本地服务");
            _server = await DshServer.StartAsync(_config, report.Toolchain, message => SetSplashDetail(message), _startupCts.Token);
            _server.Exited += OnServerExited;

            ShowSplash("正在加载界面");
            SetSplashDetail(_server.BaseUrl.ToString());

            await EnsureWebViewAsync();
            _webView.CoreWebView2.Navigate(_server.BaseUrl.ToString());
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-startup.
        }
        catch (Exception ex)
        {
            ShowError("启动失败", ex.Message, _server?.LogTail() ?? "(没有输出)");
        }
        finally
        {
            _starting = false;
        }
    }

    async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
            return;

        var userDataFolder = Path.Combine(AppConfig.DataDirectory, "WebView2");
        Directory.CreateDirectory(userDataFolder);

        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "无法初始化 WebView2 运行时。请安装 “Microsoft Edge WebView2 Runtime” 后重试。" + Environment.NewLine + ex.Message, ex);
        }

        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsPinchZoomEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = false;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;

        _webViewReady = true;
    }

    // ── webview events ──────────────────────────────────────────────────────────────────────

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_server is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
            return;
        if (target.Scheme is "about" or "data" or "blob" or "devtools")
            return;

        var home = _server.BaseUrl;
        var samePort = target.Port == home.Port;
        var sameHost = string.Equals(target.Host, home.Host, StringComparison.OrdinalIgnoreCase)
                       || (IsLoopback(target.Host) && IsLoopback(home.Host));

        if (samePort && sameHost && target.Scheme == home.Scheme)
            return;

        // Anything leaving the local shell goes to the user's real browser instead.
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            ShowWebView();
            return;
        }
        ShowError("界面加载失败", $"导航未成功：{e.WebErrorStatus}", _server?.LogTail() ?? "(没有输出)");
    }

    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_closing)
            return;
        ShowError("界面进程异常", $"{e.ProcessFailedKind}：WebView2 渲染进程已退出。点“重试”重新加载界面。",
            _server?.LogTail() ?? "(没有输出)");
    }

    void OnServerExited(object? sender, EventArgs e)
    {
        if (_closing)
            return;
        BeginInvoke(new Action(() =>
        {
            if (_closing)
                return;
            ShowError("服务已退出", "dsh 进程意外结束（可能是崩溃或端口被占用）。", _server?.LogTail() ?? "(没有输出)");
        }));
    }

    // ── view state ──────────────────────────────────────────────────────────────────────────

    void ShowSplash(string title)
    {
        _splashBase = title;
        _splashTitle.Text = title;
        _splash.Visible = true;
        _splash.BringToFront();
        _errorPanel.Visible = false;
        _webView.Visible = false;
        _dotsTimer.Start();
    }

    void SetSplashDetail(string detail)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetSplashDetail(detail)));
            return;
        }
        _splashDetail.Text = detail;
    }

    void ShowWebView()
    {
        _dotsTimer.Stop();
        _splash.Visible = false;
        _errorPanel.Visible = false;
        _webView.Visible = true;
        _webView.BringToFront();
        _webView.Focus();
    }

    void ShowError(string title, string detail, string log)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowError(title, detail, log)));
            return;
        }
        _dotsTimer.Stop();
        _errorTitle.Text = title;
        _errorDetail.Text = detail;
        _errorLog.Text = log.Replace("\n", Environment.NewLine);
        _errorPanel.Visible = true;
        _errorPanel.BringToFront();
        _splash.Visible = false;
        _webView.Visible = false;
    }

    async void OnRetryClick(object? sender, EventArgs e)
    {
        if (_starting)
            return;
        _retryButton.Enabled = false;
        try
        {
            _errorPanel.Visible = false;
            DisposeServer();
            await StartAsync();
        }
        finally
        {
            _retryButton.Enabled = true;
        }
    }

    void DisposeServer()
    {
        var server = _server;
        _server = null;
        if (server is null)
            return;
        server.Exited -= OnServerExited;
        try
        {
            server.Dispose();
        }
        catch
        {
            // Ignore: the job object still guarantees cleanup.
        }
    }

    static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No handler for the scheme; nothing sensible to do.
        }
    }

    static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // Ignore.
        }
    }

    // ── shutdown ────────────────────────────────────────────────────────────────────────────

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing)
            return;
        _closing = true;

        _dotsTimer.Stop();
        _startupCts?.Cancel();
        SaveBounds();

        var server = _server;
        _server = null;
        if (server is not null)
        {
            // Killing the tree can take a moment; do it off the UI thread so the window closes
            // immediately. The job object covers the case where we exit first.
            Task.Run(() =>
            {
                try
                {
                    server.Dispose();
                }
                catch
                {
                    // Ignore.
                }
            });
        }
    }

    void SaveBounds()
    {
        try
        {
            if (WindowState == FormWindowState.Maximized)
            {
                var restore = RestoreBounds;
                _geometry.Maximized = true;
                _geometry.X = restore.X;
                _geometry.Y = restore.Y;
                _geometry.Width = restore.Width;
                _geometry.Height = restore.Height;
            }
            else if (WindowState == FormWindowState.Normal)
            {
                _geometry.Maximized = false;
                _geometry.X = Bounds.X;
                _geometry.Y = Bounds.Y;
                _geometry.Width = Bounds.Width;
                _geometry.Height = Bounds.Height;
            }
            _geometry.Save();
        }
        catch
        {
            // Ignore.
        }
    }
}
