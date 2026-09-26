# 开发笔记

给维护者看的记录：这里放设计取舍、被否决的方案和实测数据。面向用户的使用说明见 [../README.md](../README.md)。

## 为什么不用 NativeAOT

试过了，SDK 直接拦死：

```
error NETSDK1175: 启用剪裁时，不支持或不推荐使用 Windows 窗体。
```

AOT 必然启用剪裁，而 WinForms / WPF 不在支持范围内（`Microsoft.Web.WebView2.WinForms` 的托管封装
同样依赖 WinForms 宿主）。要 AOT 就得把界面重写成**裸 Win32 窗口 + WebView2 COM 互操作**，
等于另做一个程序，因此保留 WinForms。

自包含（非 AOT）单文件发布实测 **103 MB**——WinForms 不允许剪裁，这部分省不掉，所以默认发布
仍是框架依赖版（约 1.4 MB）。

## 为什么没有把 node 和 dsh 一起打包

目标本来是"一台干净电脑打开 exe 就能用"。实测各部分体积：

| 组成 | 体积 |
| --- | --- |
| `node.exe`（Node 24 win-x64） | 89 MB |
| `@deepseek-ai/dsh` 依赖树 | 194 MB |
| 合计 | **283 MB** |
| 叠加自包含 .NET 运行时 | ≈ 390 MB（压缩进 exe 后仍约 250 MB，首次启动还要解压） |

更麻烦的是可移植性：`~/.dsh/profiles/node_modules` 是 **529 个 Junction**，全部指向全局 npm 树
（其中一个还指向 `npm-cache\_npx\...` 这样的临时目录），**不能靠拷贝搬迁**，必须重建链接或解除引用复制。

结论：需求只是"在干净电脑上能用"时，**启动环境检测 + 明确安装指引**（见 README 的「启动时的环境检测」）
比背 300 MB 划算得多。已验证 node + dsh 树用剥离的 PATH 启动可以自举（`tools/portability-test.ps1`），
所以将来真要做绿色版，路径是通的——只是体积代价要认。

## 图标生成的两个坑

1. **GDI+ 画不了这条 logo 路径**：DeepSeek 官方 logo 含椭圆弧（`a5.526 5.526 0 …`），
   GDI+ 只有正圆 `AddArc`，手写路径解析器容易走形。改为用**无头 Edge 逐尺寸原生栅格化**
   （浏览器渲染 SVG 是权威实现），再打包成 7 个尺寸的 ico。
2. **无头 Edge 的布局陷阱**：最初用 CSS 百分比尺寸 + flex 居中，结果 16/32/48/64 px 正常（覆盖约 29%），
   而 128 px 产出**全空**、256 px 只有 17.3%——打包围盒才看出图形被排到画布外/裁掉右半边。
   加 `--virtual-time-budget` 无效，说明不是截图竞态而是确定性的布局问题。
   最终改为**每尺寸显式像素定位**（绝对定位 + `<svg width="N" height="N">`，不用百分比/flex），
   7 个尺寸覆盖稳定在 28.7%–30.6%。`tools/make-icon.ps1` 现在自带空白渲染自检，覆盖低于 5% 直接报错。

想要白色圆角底板版本：`pwsh tools\make-icon.ps1 -Plate white`（另有 `-Plate dark`）。

## 生命周期与进程清理

- 私有服务用 `--port 0` 由系统分配端口，避免和手动开的 `dsh web` 抢 3080。
- 子进程挂进 **kill-on-close 的 Job Object**：窗口进程被任务管理器强杀时，`dsh` 子进程也一定会死；
  复用别人的服务时则**不会**误杀（`Owned` 标志控制）。
- 就绪判定要求根路径 200 且页面含 `__ModuleLoader__` 标记，避免把 3080 上别的程序误认成 DSH。
- 子进程 stdout/stderr 必须全程异步抽干（保留尾部 500 行），否则管道塞满会把 node 卡死。

## 测试环境的一个坑

PowerShell 对 **GUI 子系统**的 exe 不会等待：`& app.exe` 立即返回，`$LASTEXITCODE` 为空，
重定向的输出和刚写的文件都可能读早。测试脚本一律用
`Start-Process -Wait -PassThru -RedirectStandardOutput`。

另外 `tools\smoke-test-missing-env.ps1` 会把每步写入 `tools\gate-test.log`，这样即使调用方被中断，
运行过程仍可复盘。
