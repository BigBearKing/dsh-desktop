# DeepSeek Harness 桌面壳（DshDesktop）

把 `dsh web` 的浏览器界面封装成一个**原生 Windows 窗口程序**：没有地址栏、没有标签页，
有自己的任务栏图标和窗口生命周期，同时由它自己负责 `dsh web` 服务的启动与关闭。

底层是 **C# WinForms + WebView2**（复用系统已安装的 Edge WebView2 运行时），
所以产物只有一个约 1.4 MB 的 exe，而不是塞进一整个 Chromium。

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

## 构建与运行

```powershell
cd <本项目目录>

# 调试构建 + 跑起来
dotnet build -c Release
.\bin\Release\net10.0-windows\DshDesktop.exe

# 发布单文件 exe（当前所用）
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -o publish
```

产物：`publish\DshDesktop.exe`（约 1.41 MB，框架依赖）。

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
       [重新检测] [复制安装命令] [打开下载页] [打开配置文件夹] [退出]
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
DshDesktop.exe --path-only              # 正常开窗口，但环境检测只按 PATH 查
```

演示缺组件界面（在一台已装好 Node 的机器上复现"干净电脑"）：把 PATH 剥空再配合 `--path-only`：

```powershell
cmd /c "set PATH=C:\Windows\System32 && publish\DshDesktop.exe --path-only"
```

## 窗口内的操作

- `F5` / `Ctrl+R` 重新加载界面，`Ctrl+Shift+I` / `F12` 打开 DevTools
- `Ctrl` + `+` / `-` / `0` 缩放
- 关闭窗口即结束它自己拉起的 `dsh` 服务；启动失败时窗口会给出错误、日志尾部和「重试 / 在浏览器中打开 / 打开配置文件夹」

## 已验证行为

`tools\` 下的实测脚本（都是本机真实运行，不是推断）：

| 脚本 | 覆盖内容 |
| --- | --- |
| `smoke-test.ps1` | 拉起 `web --no-open --port 0` → 系统分配端口 → HTTP 200 且含 DSH 标记 → CDP 确认 WebView2 真的渲染了页面 → 强杀窗口后 `node` 子进程一并退出、无孤儿 |
| `smoke-test-attach.ps1` | 复用 3080 上已有的实例、不另起服务进程、优雅关窗不误杀外部服务 |
| `smoke-test-missing-env.ps1` | 缺组件时：写出失败报告、**不拉起 node**、**不初始化 WebView2**（数据目录不创建）、不新增 `msedgewebview2` 进程、窗口不崩 |
| `portability-test.ps1` | 把 node + dsh 树拷到临时目录，用剥离的 PATH 启动，验证这套组合可以脱离全局 npm 安装自举 |

## 已知限制

- 需要 **WebView2 运行时**（Win11 自带；缺失时窗口会提示安装），以及 **.NET 10 桌面运行时**
  （本机由 SDK 10.0.401 提供）。想分发到没装 .NET 的机器，把发布命令改成 `--self-contained true`，
  实测体积 **103 MB**（WinForms 不允许剪裁，所以省不掉）。
- 单实例：重复启动只会弹提示，不会开第二个窗口。
- 图标由 `tools\make-icon.ps1` 从 `tools\icon-source.svg`（DeepSeek 官方 logo）生成多尺寸 `app.ico`：
  logo 路径含椭圆弧，GDI+ 无法忠实表达，所以脚本改用**无头 Edge 逐尺寸原生栅格化**，
  再打包成 7 个尺寸（16/24/32/48/64/128/256）的 ico，并自带空白渲染自检。
  想要白色圆角底板版本：`pwsh tools\make-icon.ps1 -Plate white`（另有 `-Plate dark`）。
  脚本按尺寸用**绝对像素定位**而非 CSS 百分比 + flex —— 后者在 128/256 px 下让无头 Edge
  把图形排到画布外，会产出空白图标条目。

### 关于 NativeAOT 和「把 dsh 一起打包」的实测结论

两条都试过，结论记录在这里免得重复踩：

- **NativeAOT 走不通**：SDK 直接报 `error NETSDK1175: 启用剪裁时，不支持或不推荐使用 Windows 窗体`。
  AOT 必然启用剪裁，而 WinForms/WPF 不在支持范围内（WebView2 托管封装同样依赖 WinForms 宿主）。
  要 AOT 就得把界面重写成裸 Win32 窗口 + WebView2 COM 互操作，等于另做一个程序。
- **完整内嵌 dsh 的体积代价**：实测 `node.exe` 89 MB + `@deepseek-ai/dsh` 依赖树 194 MB ≈ **283 MB**；
  叠加自包含 .NET 运行时约 390 MB（压缩进 exe 后仍约 250 MB），首次启动还要解压。
  另外 `~/.dsh/profiles/node_modules` 是 **529 个 Junction**，指向全局 npm 树（其中一个还指向
  `npm-cache\_npx\...`），**不能靠拷贝搬迁**，必须重建链接或解除引用复制。
  需求只是"在干净电脑上能用"时，做启动检测 + 明确指引（本节内容）比背 300 MB 划算得多。

