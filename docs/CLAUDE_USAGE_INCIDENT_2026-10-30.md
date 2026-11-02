# Claude 用量异常现场记录（2026-10-30）

10:33 左右，运行中的 `dist/AIClockBridge-0.5.15-Windows-x64/AIClockBridge.exe`
通过 `http://127.0.0.1:8765/status` 返回 Claude `status=working`，但
`five_hour_pct=null`、`seven_day_pct=null`。设备在没有真实 5h 百分比时
使用会话时长估算额度环，周额度则显示为空。

`%APPDATA%/AIClockBridge/logs/bridge.log` 于 10:29:52 和 10:32:52 均记录：

```text
[usage] claude: Claude refresh token 已过期，请运行 claude auth login
```

桌面版 Claude 4.29 正在运行。旧缓存位置
`%LOCALAPPDATA%/Packages/Claude_pzs8sxrjxfjjc/LocalCache/Roaming/Claude/plan-usage-history.json`
不存在，而新位置
`%LOCALAPPDATA%/Claude-Data/claude_pzs8sxrjxfjjc/plan-usage-history.json`
存在，最后写入时间为 10:20:23。其版本 2 的最新样本包含 `fh=0`、`sd=2`。
桥接当前的 `ClaudeDesktopUsage.Paths()` 只枚举旧 MSIX 目录和 `%APPDATA%/Claude`，
所以漏掉了新缓存；CLI 凭据又无法刷新，导致两项额度均为空。桥接持久化的
Claude API 缓存最后更新于 9 月 22 日，早已超过有效期。

修复：枚举 `%LOCALAPPDATA%/Claude-Data/claude_*` 中的额度缓存，同时保留旧路径。
回归测试和本机缓存探针证实修复后读取 `5h=0`、`7d=2`，采样时间为 10:20:23。
此缓存不含额度重置时间；CLI OAuth 未恢复前，两个重置倒计时仍不可用。

现场检查只提取额度、时间、日志错误和路径；未复制凭据、组织 ID 或会话正文。

## 追记（2026-11-02）：两条额度链路为何经常同时失效

**桌面缓存只在“近期点开过托盘”时才更新。** 桌面版 4.4212 的 `[plan-usage]`
后台轮询每 15 分钟（交互后 5 分钟）拉一次额度并追加样本，但有两道闸：
服务端配置 `pollRequiresTrayOpenWithinHours` 要求托盘用量界面在 N 小时内被打开过，
否则暂停（日志 "background poll paused: tray not opened recently"）；系统空闲
超过 10 分钟也跳过。上次托盘打开记录在桌面版 `config.json` 的
`planUsageLastTrayOpenAt`（本机为 10-29 18:24）。10-30 的连续样本恰在
18:03 停止，推断 N=24。超出窗口后只有桌面版启动时写一个样本：今天 14:01
启动后到 15:30 再无样本，桥接 35 分钟后判定过期并清空额度。桌面版一直开着
也无济于事，必须点一下它的托盘图标。

**CLI OAuth 链路自 10-09 前到 11-02 15:28 一直不可用。** 日志依次为
“refresh token 已被服务端拒绝”（10-09 起，最早一次拒绝早于现存日志）、
“refresh token 已过期”（10-21 起）、“未找到凭据”（11-02 14:57 起，失效凭据被移除）。
桌面版拉起的 Claude Code 使用桌面版自己的登录，不会维护
`~/.claude/.credentials.json`，所以 Claude Code 正常可用并不代表这条链路正常。
15:28 重新登录后 `/status` 恢复实时 5h/7d 与重置倒计时。

实时 working/idle 状态（hook + transcript）在此期间工作正常。
托盘过期提示已从“请打开 Claude”改为“点一下 Claude 托盘图标可恢复”。
