# DeepSeek Harness 桌面壳（DshDesktop）

把 `dsh web` 的浏览器界面封装成一个**原生 Windows 窗口程序**：没有地址栏、没有标签页，
有自己的任务栏图标和窗口生命周期，同时由它自己负责 `dsh web` 服务的启动与关闭。

底层是 **C# WinForms + WebView2**（复用系统已安装的 Edge WebView2 运行时），
所以产物只有一个约 1.4 MB 的 exe，而不是塞进一整个 Chromium。

## 下载

到 [Releases](https://github.com/BigBearKing/dsh-desktop/releases) 下载 `DshDesktop.exe`，
双击即用，无需安装。程序启动时**先自检运行环境**，缺什么会直接给出安装命令，不会留下一堆看不懂的报错。

## 系统要求

| 要求 | 说明 |
| --- | --- |
| Windows 10 / 11（x64） | — |
| [Node.js](https://nodejs.org/) 18 或更高 | `dsh` 的运行依赖；`winget install OpenJS.NodeJS.LTS` |
| dsh CLI | `npm i -g @deepseek-ai/dsh` |
| WebView2 运行时 | Windows 11 已内置；Win10 缺失时程序会提示安装 |
| .NET 10 桌面运行时 | 缺失时程序无法启动，请安装运行时或改用自包含构建 |

Node.js、dsh、WebView2 这三项都会被启动检测覆盖，不用自己逐个排查；
检测结果同时写入 `%LOCALAPPDATA%\DeepSeekHarness\env-check.txt`。

## 它是怎么工作的

```
DshDesktop.exe (WinForms 窗口)
├── 探测 127.0.0.1:3080 是否已有 dsh web
│   ├── 有  → 直接连上去（不另起服务，关窗也不关它）
│   └── 没有 → 拉起自己的私有服务：node <dsh>/lib/bin.js web --no-open --port 0
│              ├── 解析 stdout 的 "dsh web: http://127.0.0.1:<port>" 拿到真实端口
│              ├── 轮询 GET / 直到返回 200 且页面含 __ModuleLoader__ 标记（确认确实是 DSH）
│              └── 把子进程挂进一个 kill-on-close 的 Job Object
└── WebView2 加载该地址，成为窗口内容
```

几个刻意的设计：

- **`--port 0` 让系统分配空闲端口**，所以不会和你手动开的 `dsh web` 抢 3080。
- **Job Object（kill-on-close）**：即使窗口进程被任务管理器强杀，`dsh` 子进程也一定会跟着死，
  不会留下孤儿 node 进程；反过来，如果是复用别人的服务，退出时**不会**误杀它。
- **就绪判定不只是 TCP 连通**：要求根路径 200 且含 DSH 页面标记，避免把 3080 上别的程序误认成 DSH。
- **外链外跳**：`target=_blank` 或任何跳出本机回环地址的导航，都交给系统默认浏览器打开。
- 子进程 stdout/stderr 全程异步抽干（保留尾部 500 行），既避免管道塞满把 node 卡死，
  也在启动失败时把日志显示在错误页上（可复制）。

## 配置

配置在 `%LOCALAPPDATA%\DeepSeekHarness\config.json`，首次运行时自动生成带注释的模板，
程序之后**不会覆盖它**（窗口位置另存于同目录的 `state.json`）。

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `WorkspaceDirectory` | `~/Documents/Codex` | 传给 `dsh web` 的工作目录，即智能体的工作区 |
| `Port` | `0` | 私有服务端口，`0` = 由系统分配空闲端口 |
| `ReuseExistingServer` | `true` | 是否复用已在 `ExistingServerPort` 上运行的实例 |
| `ExistingServerPort` | `3080` | 复用探测的端口 |
| `NodePath` / `DshBinJs` | 空 | 留空自动探测（`%APPDATA%\npm\node_modules\@deepseek-ai\dsh\lib\bin.js` 等） |

改完重启程序生效。

## 启动时的环境检测

窗口起来后先跑一遍环境体检，**缺组件时不会去启动服务**，而是显示一张检查清单：

```
缺少运行环境
  ✗  Node.js 运行时   未找到 node.exe（PATH 和常见安装位置都没有）
                         修复: winget install OpenJS.NodeJS.LTS
  ✗  dsh 命令行       未找到 @deepseek-ai/dsh 的 bin.js
                         修复: npm i -g @deepseek-ai/dsh
  !  npm              未找到 npm.cmd（安装 Node.js 时会自带）
       [重新检测] [复制安装命令] [打开下载页] [打开配置文件夹] [关于] [退出]
```

判定规则：

| 项目 | 是否阻塞 | 判定 |
| --- | --- | --- |
| Node.js | 是 | 找得到 `node.exe` **且** `node --version` 真能执行成功；主版本低于 18 视为不通过 |
| dsh | 是 | 找得到 `@deepseek-ai/dsh/lib/bin.js`（并读出版本号显示出来） |
| npm | 否（只提示） | 仅用于安装上面两项，`dsh` 运行时不依赖它 |

查找顺序：`config.json` 里的显式路径 → `PATH` → 常见安装位置（`Program Files\nodejs`、
`%LOCALAPPDATA%\Programs\nodejs`、Volta、nvm-windows、scoop、chocolatey 等）。
**为什么要查常见位置**：从资源管理器启动的进程沿用的是登录时的 PATH，刚装完 Node 还没重新登录时，
只查 PATH 会误报"未安装"。

每次检测都会把报告写到 `%LOCALAPPDATA%\DeepSeekHarness\env-check.txt`（无论成功失败），
失败后可以直接把这个文件发出来定位问题。

### 命令行诊断开关

```powershell
DshDesktop.exe --check-env              # 不开窗口，打印体检报告；就绪退出码 0，缺组件 1
DshDesktop.exe --check-env --path-only  # 只按 PATH 查，不查常见安装位置
DshDesktop.exe --about                  # 不开窗口，打印版本/环境信息（提 issue 时可直接贴）
DshDesktop.exe --path-only              # 正常开窗口，但环境检测只按 PATH 查
```

演示缺组件界面（在一台已装好 Node 的机器上复现"干净电脑"）：把 PATH 剥空再配合 `--path-only`：

```powershell
cmd /c "set PATH=C:\Windows\System32 && DshDesktop.exe --path-only"
```

## 窗口内的操作

- `F5` / `Ctrl+R` 重新加载界面，`Ctrl+Shift+I` / `F12` 打开 DevTools
- `Ctrl` + `+` / `-` / `0` 缩放
- 关闭窗口即结束它自己拉起的 `dsh` 服务；启动失败时窗口会给出错误、日志尾部和「重试 / 在浏览器中打开 / 打开配置文件夹 / 关于 / 退出」

### 关于

窗口是**无边框、无菜单栏**的，所以「关于」放在几个顺手的位置：

| 入口 | 说明 |
| --- | --- |
| 右键标题栏 → `关于(&A)…`，或 `Alt+空格` → A | 原生系统菜单，最可靠；追加在「关闭」之后 |
| 页面内右键 → 关于 DeepSeek Harness | 挂在 WebView2 右键菜单上，界面聚焦时最顺手 |
| `F1` | 窗口控件持有焦点时生效（启动中、环境检测页、错误页） |
| 错误页 / 环境检测页的「关于」按钮 | 出问题时不用记快捷键 |

「关于」窗口显示：应用名与版本（版本号后带构建时自动写入的 commit 短哈希）、dsh / Node.js /
WebView2 运行时版本、许可证、可点击的项目主页链接，以及配置目录和实际用到的可执行文件路径。
底部信息块可全选复制，也可点「复制信息」——**提 issue 时贴这一段最省事**。

## 已验证行为

`tools\` 下的实测脚本（都是真实运行，不是推断）：

| 脚本 | 覆盖内容 |
| --- | --- |
| `smoke-test.ps1` | 拉起 `web --no-open --port 0` → 系统分配端口 → HTTP 200 且含 DSH 标记 → CDP 确认 WebView2 真的渲染了页面 → 强杀窗口后 `node` 子进程一并退出、无孤儿 |
| `smoke-test-attach.ps1` | 复用 3080 上已有的实例、不另起服务进程、优雅关窗不误杀外部服务 |
| `smoke-test-missing-env.ps1` | 缺组件时：写出失败报告、**不拉起 node**、**不初始化 WebView2**（数据目录不创建）、不新增 `msedgewebview2` 进程、窗口不崩 |
| `smoke-test-about.ps1` | 用真实 Win32 消息验证：系统菜单确实追加了「关于」→ 发 `WM_SYSCOMMAND(0x1000)` 真的弹出关于窗口 → 关掉后主窗口仍存活 |

## 从源码构建

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\DshDesktop.exe

# 发布单文件 exe
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -o publish
```

加 `--self-contained true` 可得免装 .NET 运行时的版本（体积约 103 MB）。

## 许可证

本项目以 **MIT** 许可发布，见 [LICENSE](LICENSE)。

需要留意：应用图标取自 **DeepSeek 官方 logo**（`tools\icon-source.svg`），商标归 DeepSeek 所有，
MIT 许可并不涵盖商标授权。自用无妨，若要用于商业场景或长期公开分发，建议换成自己的图标——
替换 `tools\icon-source.svg` 后重跑 `tools\make-icon.ps1` 即可。

---

设计取舍与被否决方案的实测数据（NativeAOT、整体打包、图标生成的坑）见
[docs/engineering-notes.md](docs/engineering-notes.md)。
