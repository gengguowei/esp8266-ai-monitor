# AIClockBridge for Windows

`mac-app/` 菜单栏桥接的 Windows 移植版：同一套功能、同一套设备协议（固件感知不到
桥接跑在哪个系统上），以系统托盘图标形式常驻。

功能与 Mac 版一致：

- **左键托盘图标** → ESP8266 屏幕实时镜像（额度环 + 桌宠动画 + 网速图 + 股票页，
  与设备渲染同一份数据），底部附 自动/Claude/Codex/网速/股票 快速切换
- **右键托盘图标** → 控制菜单：Claude/Codex 完整额度（5h/周 + 重置倒计时）、
  自动查找并配对设备、设置设备地址、屏幕显示模式、petdex 桌宠画廊、恢复默认动画、
  把本机设为设备桥接、桥接服务地址
- **跟随 Windows 锁屏** → 锁定/睡眠时临时关闭设备背光，解锁/恢复时自动点亮；
  不改变用户亮度，掉线重连或设备重启后会从 `/status` 自动校正
- **USB 优先、WiFi 回退**：自动识别 CH340/CP210x 串口，通过 USB 推送状态、网速和
  股票数据；USB 拔出或链路超时后，固件自动恢复局域网 HTTP 轮询
- 本地 HTTP 服务 `0.0.0.0:8765`：`/status`、`/net`、`/stock`、
  `/stock/names.raw`、`POST /event`（Claude Code / Codex hooks 秒级状态推送）
- 数据来源同 Mac 版：`%USERPROFILE%\.claude\projects` / `%USERPROFILE%\.codex\sessions`
  的 JSONL 日志 + 各自官方用量接口（凭据读
  `%USERPROFILE%\.claude\.credentials.json` 和 `%USERPROFILE%\.codex\auth.json`，
  token 只发给各自官方 API）
- Claude access token 到期前 90 秒会进行一次兜底刷新并原子写回凭据；Claude CLI
  自身的提前刷新仍然优先，刷新失败后 15 分钟内不会重复请求
- **Claude 桌面版无需安装 CLI**：优先读取桌面版额度历史；托盘显示采样时间，
  超过 35 分钟标记过期，镜像和固件不再使用过期额度。没有桌面缓存才回退到 CLI OAuth。
  桌面额度是本地文件，**每 60 秒重读一次**，打开托盘菜单和点「刷新」也一定重读；
  走网络的额度接口（Codex、无桌面版时的 Claude OAuth）仍是 120 秒一次，不受影响
- 网速取物理网卡（以太网/WiFi）字节计数，4Hz 采样，排除 VPN/虚拟网卡

与 Mac 版的差异：

- 无固件刷写入口（刷写请用网页版刷写工具）
- 唯一的第三方依赖是 [ImageSharp](https://github.com/SixLabors/ImageSharp)——
  System.Drawing 解不了 petdex 的 WebP 精灵图、也编不了多帧 GIF

## 构建 / 运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 10
19041+ / Windows 11）。**注意只装运行时是不够的**——共享 host `dotnet.exe` 照样存在，
但 `dotnet build` 会报 "No .NET SDKs were found"。

### 首次准备环境（只需一次）

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-app\setup-dev-env.ps1
```

走微软官方 `dotnet-install.ps1` 把 SDK 装到 `%LOCALAPPDATA%\Microsoft\dotnet`：
**用户级安装，不需要管理员权限、不弹 UAC、不写你 profile 以外的任何地方**，也不会干扰
系统级 .NET 或 Visual Studio。装完把该目录追加进用户 PATH、设好 `DOTNET_ROOT`，再
restore 一遍依赖确认 nuget.org 通。可重复执行：已有 8.x SDK 会直接跳过，PATH 也只追加一次。

| 开关 | 作用 |
|---|---|
| `-SkipPath` | 不动 PATH / `DOTNET_ROOT`（`build-release.ps1` 会自己探 `%LOCALAPPDATA%`，构建照样能跑，只是终端里敲 `dotnet` 找不到） |
| `-SkipRestore` | 跳过 restore 验证 |
| `-Force` | 已有 8.x SDK 也强制重装 |
| `-Channel` | 换版本线，默认 `8.0` |

卸载：删掉 `%LOCALAPPDATA%\Microsoft\dotnet`，再从用户 PATH 里移除该项并删掉
`DOTNET_ROOT`（脚本结束时会把具体值打印出来）。

### 日常运行

```powershell
cd windows-app\AIClockBridge
dotnet run                # 前台运行（托盘出现小电脑图标）
```

### 出 Release 包

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-app\build-release.ps1
```

脚本先跑一遍回归测试，再发布**框架依赖的单文件** exe，产物固定落在
`dist\AIClockBridge-<版本>-Windows-x64\AIClockBridge.exe`（约 27MB），最后打印大小和
SHA256。版本号取自 `AIClockBridge.csproj` 的 `<Version>`，那是发布目录命名的唯一来源。

**版本线独立于固件**：桥接和固件（`firmware/include/config.h` 的 `FW_VERSION`）曾经同步升，
从 **0.5.0** 起分开走——自启动、单实例、额度缓存修正这类改动完全不碰设备，设备协议也没变，
没理由拖着固件一起升版。发版流程：改 `<Version>` → 在 [`CHANGELOG.md`](CHANGELOG.md) 记一笔
→ 跑 `build-release.ps1`，产物目录名自动跟上。

常用开关：

| 开关 | 作用 |
|---|---|
| `-SelfContained` | 改出自包含单文件（约 150MB），目标机器不需要装任何运行时 |
| `-SkipTests` | 跳过回归测试 |
| `-KeepStale` | 保留 `dist\` 下的旧 Windows 产物目录（默认会清掉，固件 bin 和刷机备份不动） |
| `-StopRunning` | 构建前自动结束正在运行的 AIClockBridge。**不加的话，托盘里开着旧版就会构建失败**——运行中的 exe 锁着自己，删不掉；脚本会提前拦下来并提示你从托盘退出 |

默认产物是**框架依赖**的，用户机器需要 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)；
不想让用户装运行时就用 `-SelfContained`。

首次启动 Windows 会弹防火墙授权（HTTP 服务监听 0.0.0.0:8765，设备要从局域网访问，
选"允许"）。

**开机自启**：右键托盘图标 → 勾选「开机自动启动」。写的是
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，用户级、不需要管理员权限。几点说明：

- 注册表是唯一状态来源，不在 `settings.json` 里存副本——你也可以在任务管理器「启动应用」
  里禁用它，勾选框每次打开菜单都重新读注册表，不会跟系统显示打架
- 任务管理器的「禁用」不删 Run 值，而是写 `StartupApproved\Run` 的标志位；判断时两处都读，
  重新勾选会清掉那个标志
- 升级换了版本目录后路径会失效，所以每次启动会自愈：发现已启用但注册的路径不是当前 exe，
  就自动改指过来
- 如果你以前按旧文档往启动文件夹里放过快捷方式，勾选时会提示你删掉，否则会启动两次
- **首次开机自启时 Windows 防火墙会弹授权框**（HTTP 服务要监听 0.0.0.0:8765）。想避开，
  就先手动运行一次点掉「允许」，再打开这个开关

程序现在是**单实例**的：重复启动会提示「已经在运行了」并退出，不会出现两个托盘图标抢
8765 端口和 CH340 串口。开机自启那份带 `--autostart` 参数，重复启动时静默退出、不弹框。

**Hooks 实时状态**（可选，同主 README §7）：Claude Code / Codex 的 hooks 往
`http://127.0.0.1:8765/event` POST 事件即可，Windows 下 curl 自带。

## 验证

```powershell
curl.exe -s http://localhost:8765/status | python -m json.tool
```

配置持久化在 `%APPDATA%\AIClockBridge\settings.json`（设备地址等）。

## 排查问题

**日志**：`%APPDATA%\AIClockBridge\logs\bridge.log`，超过 1MB 滚动一代
（托盘菜单 →「打开日志文件夹」）。记录串口连接/断开、HTTP 端口绑定、额度和行情的
请求错误、HttpClient 重建及当时的系统代理、未捕获异常。程序是 `WinExe` 没有控制台，
所以**这个文件是唯一的诊断来源**。

**「额度/行情卡住不动」**：先看托盘。Codex 那行若带 `（缓存：…）`、股票那行若显示
「股票：行情请求失败：…」，说明是**上游取数失败**，跟设备通讯无关。这两项是唯二需要
公网的数据（`chatgpt.com` 和 `qt.gtimg.cn`），而 Claude 额度读本地文件、网速读本地
计数器，所以它们仍然正常——这个组合就是判据。常见原因是代理客户端在桥接运行期间改了
系统代理；0.5.1 起 HttpClient 会在连续 3 次传输层失败后重建并重读代理设置，不必再
重启程序。日志里搜 `[http:` 能看到重建记录和当时的代理配置。

## Claude 桌面版

打开并登录 Claude 桌面版后，桥接自动读取 `plan-usage-history.json`，支持：

- **MSIX 安装（实际就是这条）**：枚举 `%LOCALAPPDATA%\Packages\*Claude*\LocalCache\Roaming\Claude\plan-usage-history.json`
- 兜底：`%APPDATA%\Claude\plan-usage-history.json`

Windows 上官方只提供 MSIX 一种格式（`Add-AppxPackage` 单用户 / `Add-AppxProvisionedPackage`
全机器，数据路径相同），而 MSIX 应用写 `%APPDATA%\Claude` 会被容器重定向到
`LocalCache\Roaming\Claude`——所以第二条其实命不中，只是留个兜底。

包目录用**通配枚举而不是写死** `Claude_pzs8sxrjxfjjc`：结尾那串是签名证书主体算出来的
publisher hash，证书轮换或改包名都会让写死的路径**静默失效**（额度永远是 `-`，日志里没有
任何线索）。多个目录命中也没关系，读取器本来就取样本最新的那个。

支持历史格式 v1 和 v2；v2 取最新一条组织采样，不跨组织拼接额度。
该文件是桌面版内部缓存，`fh`/`sd` 分别为 5 小时/7 天已用百分比；它没有重置时间，
因此不显示重置倒计时。客户端不会读取桌面登录凭据，也不会请求桌面认证接口。
桌面版关闭后缓存可能停止更新；托盘仍显示历史值和采样时间，35 分钟后设备额度变为未知。

**采样节拍**：实测桌面版每 **15.00 分钟**整点写一条样本（`:11:45 / :26:45 / :41:45 / :56:45`
这种固定时刻）。托盘显示的「采样时间」是样本自带的时间戳，不是我们读文件的时刻，所以
**最大显示滞后 ≈ 15 分钟 + ≤60 秒**，这是桌面版的节奏决定的，调我们的轮询间隔改变不了。
过期阈值必须大于两个采样周期：早先设成 15 分钟（正好等于采样间隔）时，最新样本会在下一条
写入的同一瞬间被判过期，导致每个周期都有最长一个轮询间隔的时间设备额度变成 `-`。
桌面版还会每约 4 小时自动更新并重启，期间会漏采，也需要这个余量。
`dist/diag-desktop-usage.ps1` 可以随时复测这个节拍。
固件现有的会话进度回退显示仍保持原行为。缓存结构未来若变化，需要更新读取器。

在托盘菜单点击 **启用 Claude 桌面状态同步**，再重新打开 Code 页签的本地会话。
安装器将状态转发脚本写入 `%APPDATA%\AIClockBridge\claude-status-hook.ps1`，
合并 Hooks 到 `%USERPROFILE%\.claude\settings.json`（支持 `CLAUDE_CONFIG_DIR`），
保留已有配置并在写入前备份；重复点击不会重复添加相同 Hook。
脚本只向 `127.0.0.1:8765/event` 发送事件名、会话 ID 和权限通知类型，
不会转发提示词、工具参数或会话正文，也不批准或拒绝 Claude 的权限请求。
桥接未启动时转发最多等待 2 秒，然后正常退出。

状态按会话合并：任一会话工作则显示工作中，任一会话等待审批则显示需要输入；
一项任务完成不会覆盖另一项任务的工作状态。无日志的桌面会话也可显示空闲。
工作事件有效期为 10 分钟，等待输入为 5 分钟，空闲为 30 分钟；无后续事件时自动降级，
因此超过 10 分钟没有事件的长任务可能暂时显示空闲。进程重启后等待下一次 Hook 恢复状态。
此入口用于 Code 本地会话，不代表普通聊天、Cowork、SSH 或云会话已接入。

如需停用，在 Claude 的 `settings.json` 中仅删除 command 指向
`claude-status-hook.ps1` 的条目；保留其他 Hooks。

回归验证（不请求额度 API、不修改 Claude 设置）：

```powershell
dotnet run --project windows-app/AIClockBridge.Tests -c Release
# 可选：额外只读检查本机桌面额度缓存，仅输出额度、采样时间与过期标志
dotnet run --project windows-app/AIClockBridge.Tests -c Release -- --desktop-cache
```

## 代码结构

| 文件 | 对应 Mac 版 | 说明 |
|---|---|---|
| `Program.cs` | `main.swift` | 入口 + 路由表 + 被动发现 |
| `TrayAppContext.cs` | `MenuBarController.swift` | 托盘图标 + 控制菜单 |
| `MirrorForm.cs` | `MirrorPopover.swift` | 240x240 屏幕镜像弹窗 |
| `PetPickerForm.cs` | `PetPickerWindow.swift` | petdex 桌宠选择器 |
| `PetdexService.cs` | `PetdexService.swift` | manifest / 精灵图 / GIF 合成 |
| `StatusService.cs` | `StatusReader.swift` | JSONL 日志扫描 + hook 事件 |
| `UsageFetcher.cs` | `UsageFetcher.swift` | 官方额度接口 |
| `NetSpeedMonitor.cs` | `NetSpeedMonitor.swift` | 4Hz 网速采样环 |
| `SerialLink.cs` | `SerialLink.swift` | USB 串口发现、握手、状态推送和自动重连 |
| `DeviceClient.cs` | `DeviceClient.swift` | 设备 HTTP API + 自动配对/子网扫描 |
| `MiniHttpServer.cs` | `HTTPServer.swift` | 0.0.0.0:8765 极简 HTTP 服务 |
| `Rgb565.cs` | （MirrorPopover 内联） | RGB565 大端编解码 |
