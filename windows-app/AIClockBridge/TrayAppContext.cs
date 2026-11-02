using System.Diagnostics;
using System.Drawing;

namespace AIClockBridge;

// Tray icon: the retro Macintosh device logo. Left click opens a live mirror
// of the ESP8266 screen (MirrorForm); right click opens the control menu with
// usage meters and device remote control. No quota text lives in the tray
// itself.
sealed class TrayAppContext : ApplicationContext
{
    readonly NotifyIcon _trayIcon;
    readonly StatusService _service;
    readonly UsageFetcher _usage;
    readonly WindowsScreenPowerController _screenPower;
    readonly StockMonitor _stockMonitor;
    readonly BilibiliMonitor _upMonitor;
    readonly BilibiliBrowserForm _upBrowser;
    readonly ToolStripMenuItem _upInfoItem = new("UP主：尚未获取") { Enabled = false };
    readonly ToolStripMenuItem _upSourcesItem = new("数据来源：尚未获取") { Enabled = false };
    readonly int _port;
    readonly MirrorForm _mirror;
    readonly ContextMenuStrip _menu = new();

    readonly ToolStripMenuItem _claudeUsageItem = new("Claude …") { Enabled = false };
    readonly ToolStripMenuItem _codexUsageItem = new("Codex …") { Enabled = false };
    readonly ToolStripMenuItem _deviceInfoItem = new("设备：未设置") { Enabled = false };
    /// The device reports its IP/SSID/bridge host in every #DEVICE reply, but
    /// none of it was ever surfaced - so "the CJK stock names never appear" had
    /// no visible cause, even though those strips are pulled over HTTP and need
    /// exactly these two things to be right.
    readonly ToolStripMenuItem _deviceNetItem = new("") { Enabled = false, Visible = false };
    /// Only shown while quotes are failing; silence used to be indistinguishable
    /// from a market that simply had not moved.
    readonly ToolStripMenuItem _stockErrorItem = new("") { Enabled = false, Visible = false };
    readonly Dictionary<string, ToolStripMenuItem> _modeItems = new();
    ToolStripMenuItem _autoStartItem;
    /// Set while we write Checked ourselves, so the resync does not re-enter the
    /// toggle handler and rewrite the registry on every menu open.
    bool _syncingAutoStart;

    public TrayAppContext(StatusService service, UsageFetcher usage,
                          StockMonitor stockMonitor, BilibiliMonitor upMonitor,
                          WindowsScreenPowerController screenPower, int port, BilibiliBrowserForm upBrowser = null)
    {
        _service = service;
        _usage = usage;
        _screenPower = screenPower;
        _stockMonitor = stockMonitor;
        _upMonitor = upMonitor;
        _upBrowser = upBrowser;
        _port = port;
        _mirror = new MirrorForm(service, stockMonitor, upMonitor);

        BuildMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFromAsset(),
            Text = "AI Clock Bridge",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _mirror.Toggle();
        };
        _menu.Opening += (_, _) =>
        {
            _usage.Refresh(userInitiated: true);
            RefreshUsageLines();
            RefreshStockError();
            RefreshUpInfo();
            SyncAutoStartItem();
            _ = RefreshDeviceSection();
        };
        _usage.OnUpdate = () =>
        {
            RefreshUsageLines();
            NotifyClaudeAuthIfNeeded();
        };
    }

    DateTime? _claudeAuthNoticeAt;

    /// Balloon for a dead CLI login. The failure used to surface only as text in
    /// the right-click menu, so the quota just went blank on the device for weeks.
    void NotifyClaudeAuthIfNeeded()
    {
        var error = _usage.ClaudeAuthError;
        if (error == null) { _claudeAuthNoticeAt = null; return; }   // episode over
        var now = DateTime.UtcNow;
        if (!UsageFetcher.ClaudeAuthNoticeDue(error, _usage.Claude, now, _claudeAuthNoticeAt)) return;
        _claudeAuthNoticeAt = now;
        Log.Write($"[usage] claude: 凭据失效通知已弹出（{error}）");
        _trayIcon.ShowBalloonTip(15000, "Claude 额度暂时取不到",
            $"{error}。\n在 Claude Code 中执行 /login（或终端 claude auth login）重新登录；"
            + "点一下 Claude 桌面版托盘图标可临时恢复。", ToolTipIcon.Warning);
    }

    /// User-supplied device logo (bezel + dark screen + smiley + green status
    /// dot). Full-color, matching the Mac menu-bar icon.
    static Icon TrayIconFromAsset()
    {
        using var bmp = new Bitmap(MirrorControl.LoadAsset("happy-mac.png"),
                                   new Size(32, 32));
        var handle = bmp.GetHicon();
        // clone so the icon owns its data; the GetHicon handle would leak
        // otherwise but a single tray icon for the app lifetime is fine
        return Icon.FromHandle(handle);
    }

    // MARK: - menu construction

    void BuildMenu()
    {
        _menu.Items.Add(_claudeUsageItem);
        _menu.Items.Add(_codexUsageItem);
        _menu.Items.Add(MakeItem("启用 Claude 桌面状态同步", (_, _) =>
        {
            try
            {
                var path = ClaudeHooks.Install();
                MessageBox.Show($"已配置状态 Hooks，请重新打开 Claude Code 本地会话。\n设置：{path}\n原有设置已保留；已有文件会生成备份。", "Claude 状态同步");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "配置失败"); }
        }));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(_deviceInfoItem);
        _menu.Items.Add(_deviceNetItem);
        _menu.Items.Add(MakeItem("自动查找并配对设备", async (_, _) => await AutoPairAction()));
        _menu.Items.Add(MakeItem("设置设备地址…", (_, _) => SetDeviceAddress()));
        _menu.Items.Add(MakeItem("打开设备网页", (_, _) => OpenDevicePage()));

        var displayMenu = new ToolStripMenuItem("屏幕显示");
        foreach (var (title, mode) in new[]
        {
            ("自动（开盘看股票，空闲轮换 Claude/Codex/UP主）", "auto"), ("固定 Claude", "claude"),
            ("固定 Codex", "codex"), ("股票行情", "stock"),
            ("UP主", "up"),
        })
        {
            var item = new ToolStripMenuItem(title);
            item.Click += async (_, _) => await SetDisplayMode(mode);
            _modeItems[mode] = item;
            displayMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(displayMenu);
        // (屏幕亮度在左键弹出的镜像页底部，做成滑条了)

        var followLockItem = new ToolStripMenuItem("跟随 Windows 锁屏")
        {
            CheckOnClick = true,
            Checked = _screenPower.Enabled,
        };
        followLockItem.CheckedChanged += (_, _) =>
            _screenPower.Enabled = followLockItem.Checked;
        _menu.Items.Add(followLockItem);

        _autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        _autoStartItem.CheckedChanged += (_, _) => ToggleAutoStart();
        _menu.Items.Add(_autoStartItem);

        _menu.Items.Add(_stockErrorItem);
        _menu.Items.Add(_upInfoItem);
        _menu.Items.Add(_upSourcesItem);
        _menu.Items.Add(MakeItem("B站登录 / 更新会话…", (_, _) => _upBrowser?.OpenLogin()));
        var upFetchItem = new ToolStripMenuItem("立即获取 UP主数据");
        upFetchItem.Click += async (_, _) =>
        {
            upFetchItem.Enabled = false;
            try { Toast("UP主", await _upMonitor.FetchNow()); RefreshUpInfo(); }
            finally { upFetchItem.Enabled = true; }
        };
        _menu.Items.Add(upFetchItem);
        _menu.Items.Add(MakeItem("设置 UP主 UID…", (_, _) =>
        {
            var input = InputDialog.Show("关注 UP主", "输入 Bilibili UID 或 UP 主空间地址", _upMonitor.Uid, "39627524");
            if (input == null) return;
            if (!_upMonitor.SetUid(input))
                MessageBox.Show("请输入有效的数字 UID 或 https://space.bilibili.com/UID 地址。", "UP主 UID");
            RefreshUpInfo();
        }));
        _menu.Items.Add(MakeItem("设置自选股…", (_, _) =>
        {
            var input = InputDialog.Show(
                "自选股",
                "逗号分隔的腾讯行情代码：sh/sz=A股、hk=港股、us=美股\n例如 sh600519,hk00700,usAAPL（设备最多显示 4 只）",
                string.Join(",", StockMonitor.Symbols), "sh000001,usAAPL");
            if (input != null)
            {
                StockMonitor.Symbols = input.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }));

        _menu.Items.Add(MakeItem("更换桌宠动画…（petdex）", (_, _) => OpenPetPicker()));

        var resetMenu = new ToolStripMenuItem("恢复默认动画");
        foreach (var (title, slot) in new[] { ("Claude 恢复默认", "claude"), ("Codex 恢复默认", "codex") })
        {
            var item = new ToolStripMenuItem(title);
            item.Click += async (_, _) => await ResetSprite(slot);
            resetMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(resetMenu);

        _menu.Items.Add(MakeItem("把本机设为设备桥接", async (_, _) => await PointBridgeHere()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(MakeItem("刷新", (_, _) =>
        {
            _usage.Refresh(userInitiated: true);
            RefreshUsageLines();
            _ = RefreshDeviceSection();
        }));
        _menu.Items.Add(MakeItem("桥接服务地址", (_, _) => ShowAddress()));
        _menu.Items.Add(MakeItem("打开日志文件夹", (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Log.Folder);
                Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                MessageBox.Show($"打不开日志文件夹：{e.Message}\n{Log.Folder}",
                    "AI Clock Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(MakeItem("退出", (_, _) =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        }));
    }

    static ToolStripMenuItem MakeItem(string title, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += onClick;
        return item;
    }

    // MARK: - start at logon

    void ToggleAutoStart()
    {
        if (_syncingAutoStart) return;
        try
        {
            if (_autoStartItem.Checked)
            {
                AutoStart.Enable();
                OfferToRemoveLegacyShortcuts();
            }
            else
            {
                AutoStart.Disable();
            }
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "开机自动启动",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SyncAutoStartItem();   // show what the registry actually says now
    }

    /// The user can also flip this in Task Manager's startup tab, so the tick is
    /// re-read from the registry every time the menu opens rather than trusted
    /// from the last click.
    void SyncAutoStartItem()
    {
        if (_autoStartItem == null) return;
        var enabled = AutoStart.IsEnabled();
        if (_autoStartItem.Checked == enabled) return;
        _syncingAutoStart = true;
        _autoStartItem.Checked = enabled;
        _syncingAutoStart = false;
    }

    /// Older READMEs told people to drop a shortcut into shell:startup by hand.
    /// Left in place next to the Run entry it would start the bridge twice.
    static void OfferToRemoveLegacyShortcuts()
    {
        var shortcuts = AutoStart.LegacyStartupShortcuts();
        if (shortcuts.Length == 0) return;
        var names = string.Join("\n", shortcuts.Select(System.IO.Path.GetFileName));
        var answer = MessageBox.Show(
            "启动文件夹里还有手动放进去的快捷方式，会让程序开机启动两次：\n\n" + names
            + "\n\n要删掉这些快捷方式吗？（只删快捷方式，程序本体不动）",
            "开机自动启动", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        foreach (var path in shortcuts)
        {
            try { System.IO.File.Delete(path); }
            catch (Exception e)
            {
                MessageBox.Show($"删除失败：{System.IO.Path.GetFileName(path)}\n{e.Message}",
                    "开机自动启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // MARK: - refresh

    /// The stock page receives Chinese name strips over USB while linked, or
    /// from /stock/names.raw over HTTP when using WiFi only.
    void ShowDeviceNetState(DeviceInfo info)
    {
        var wifi = info.Ssid.Length > 0 && info.Ip.Length > 0 && info.Ip != "0.0.0.0"
            ? $"WiFi {info.Ssid} {info.Ip}"
            : "未连 WiFi";
        var bridge = info.Bridge.Length > 0 ? info.Bridge : "未设置";
        var hint = "";
        if (wifi == "未连 WiFi" && !DeviceClient.UsbConnected)
            hint = "　← 股票中文名需要 USB 或 WiFi";
        else if (info.Bridge.Length == 0 && !DeviceClient.UsbConnected)
            hint = "　← 点上面「把本机设为设备桥接」";
        _deviceNetItem.Text = $"　{wifi} · 桥接 {bridge}{hint}";
        _deviceNetItem.Visible = true;
    }

    void RefreshStockError()
    {
        var notes = new List<string>();
        var error = _stockMonitor?.Error;
        if (!string.IsNullOrEmpty(error))
        {
            var at = _stockMonitor.FetchedAt;
            var age = at.HasValue ? $"，最后成功 {at.Value.ToLocalTime():HH:mm}" : "";
            notes.Add($"{error}{age}");
        }
        // The holiday table needs a yearly update; without it every weekday
        // counts as a trading day, so say so instead of failing silently.
        var year = ChinaMarketCalendar.BeijingDate(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Year;
        if (!ChinaMarketCalendar.HasYear(year))
            notes.Add($"休市表未含 {year} 年，节假日按交易日处理");
        _stockErrorItem.Visible = notes.Count > 0;
        if (notes.Count > 0) _stockErrorItem.Text = "股票：" + string.Join("；", notes);
    }

    void RefreshUpInfo()
    {
        var profile = _upMonitor.Snapshot;
        var name = string.IsNullOrEmpty(profile.Name) ? $"UID {_upMonitor.Uid}" : profile.Name;
        var next = _upMonitor.NextRefreshAt;
        var last = profile.VideoCheckedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未获取";
        _upInfoItem.Text = $"UP主：{name} · {last}" +
            (next > DateTimeOffset.UtcNow ? $" · 下次自动 {next.ToLocalTime():MM-dd HH:mm}" : " · 视频约每小时，粉丝每6小时");
        if (_upMonitor.Error != null) _upInfoItem.Text += $" · {_upMonitor.Error}（保留缓存）";
        _upSourcesItem.Text = _upMonitor.SourcesSummary;
        if (!string.IsNullOrEmpty(_upMonitor.SourceWarning)) _upSourcesItem.Text += " · " + _upMonitor.SourceWarning;
    }

    void RefreshUsageLines()
    {
        _claudeUsageItem.Text = UsageLine("Claude", _usage.Claude, "7天");
        _codexUsageItem.Text = UsageLine("Codex", _usage.Codex, "周");
    }

    static string UsageLine(string name, ProviderUsage u, string weeklyLabel)
    {
        if (u.Error != null && u.PrimaryPct == null && u.WeeklyPct == null)
            return $"{name}：{u.Error}";
        var parts = new List<string>();
        if (u.PrimaryPct.HasValue)
        {
            var s = $"5h {(int)u.PrimaryPct.Value}%";
            if (u.PrimaryResetMin.HasValue) s += $"（{FmtMin(u.PrimaryResetMin.Value)}后重置）";
            parts.Add(s);
        }
        if (u.WeeklyPct.HasValue)
        {
            var s = $"{weeklyLabel} {(int)u.WeeklyPct.Value}%";
            if (u.WeeklyResetMin.HasValue) s += $"（{FmtMin(u.WeeklyResetMin.Value)}）";
            parts.Add(s);
        }
        if (parts.Count == 0) return $"{name}：额度未知";
        var result = $"{name}　" + string.Join("　", parts);
        // Claude has two possible sources with visibly different quality (the
        // cache carries no reset countdown), so say which one produced this.
        if (u.DesktopCache)
        {
            result += $"　（桌面缓存 · {u.FetchedAt?.ToLocalTime():MM-dd HH:mm}"
                + (u.DesktopStale ? " · 已过期，点一下 Claude 托盘图标可恢复）" : "）");
            // On a cache reading the error explains why the live source is
            // unavailable - the whole reason this line is not "凭据实时".
            return u.Error == null ? result : result + $"　（{u.Error}）";
        }
        if (name == "Claude" && u.FetchedAt.HasValue)
            result += $"　（凭据实时 · {u.FetchedAt?.ToLocalTime():HH:mm}）";
        return u.Error == null ? result : result + $"　（缓存：{u.Error}）";
    }

    static string FmtMin(int min)
    {
        if (min >= 48 * 60) return $"{min / (24 * 60)}天";
        if (min >= 60) return $"{min / 60}h{(min % 60 > 0 ? $"{min % 60}m" : "")}";
        return $"{min}m";
    }

    async Task RefreshDeviceSection()
    {
        var host = DeviceClient.Host;
        if (host.Length == 0 && !DeviceClient.UsbConnected)
        {
            _deviceInfoItem.Text = "设备：未设置地址";
            foreach (var item in _modeItems.Values) item.Checked = false;
            return;
        }
        var target = DeviceClient.UsbConnected ? $"USB · {DeviceClient.UsbPort}" : host;
        _deviceInfoItem.Text = $"设备：{target}（连接中…）";
        DeviceInfo info;
        try
        {
            info = await DeviceClient.FetchInfo();
        }
        catch (Exception)
        {
            _deviceInfoItem.Text = $"设备：{target}（无法连接）";
            foreach (var item in _modeItems.Values) item.Checked = false;
            // self-heal: the device may have moved to a new DHCP address;
            // if it recently polled us from a different IP, adopt that.
            var seen = DeviceClient.LastSeenIp;
            if (seen.Length > 0 && !host.StartsWith(seen) && await DeviceClient.VerifyDevice(seen))
            {
                DeviceClient.Host = seen;
                await RefreshDeviceSection();
            }
            return;
        }
        var sprites = new[]
        {
            info.ClaudeCustomSprite ? "C:自定义" : "C:默认",
            info.CodexCustomSprite ? "X:自定义" : "X:默认",
        };
        var showing = info.Effective == "stock" ? "股票"
            : info.Effective == "up" ? "UP主"
            : (info.Showing == "claude" ? "Claude" : "Codex");
        _deviceInfoItem.Text =
            $"设备：{info.Transport} · 正在显示 {showing} · {string.Join(" ", sprites)}";
        ShowDeviceNetState(info);
        foreach (var (mode, item) in _modeItems) item.Checked = mode == info.Mode;
    }

    // MARK: - pairing

    async Task AutoPairAction()
    {
        _deviceInfoItem.Text = "设备：正在查找…";
        var ip = await DeviceClient.AutoPair(msg => _deviceInfoItem.Text = $"设备：{msg}");
        if (ip != null)
        {
            Toast("配对成功", $"已找到设备并配对：{ip}");
        }
        else
        {
            Toast("未找到设备", """
                局域网内没有发现 ESP8266 时钟。请确认：
                1. 设备已通电并连上同一个 WiFi（首次使用需通过 AI-Clock-Setup 热点配网）
                2. 路由器未开启"客户端隔离"
                """);
        }
        await RefreshDeviceSection();
    }

    // MARK: - actions

    void SetDeviceAddress()
    {
        var input = InputDialog.Show(
            "设备地址",
            "ESP8266 时钟的 IP（设备开机时屏幕上会显示，例如 192.168.1.50）",
            DeviceClient.Host, "192.168.1.50");
        if (input == null) return;
        DeviceClient.Host = input.Trim();
        _ = RefreshDeviceSection();
    }

    void OpenDevicePage()
    {
        var url = DeviceClient.BaseUrl;
        if (url == null)
        {
            SetDeviceAddress();
            return;
        }
        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
    }

    async Task SetDisplayMode(string mode)
    {
        if (mode is "up" or "auto") _ = _upMonitor.Activate();
        try
        {
            await DeviceClient.SetDisplayMode(mode);
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            Toast("切换失败", e.Message);
        }
    }

    void OpenPetPicker()
    {
        if (DeviceClient.Host.Length == 0) SetDeviceAddress();
        PetPickerForm.ShowShared();
    }

    async Task ResetSprite(string slot)
    {
        try
        {
            await DeviceClient.ResetSprite(slot);
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            Toast("恢复失败", e.Message);
        }
    }

    async Task PointBridgeHere()
    {
        var ip = DeviceClient.LocalIPv4();
        if (ip == null)
        {
            Toast("失败", "获取本机局域网 IP 失败");
            return;
        }
        var bridge = $"{ip}:{_port}";
        try
        {
            await DeviceClient.SetBridgeHost(bridge);
            Toast("已设置", $"设备将从 http://{bridge}/status 拉取状态");
        }
        catch (Exception e)
        {
            Toast("设置失败", e.Message);
        }
    }

    void ShowAddress()
    {
        var ip = DeviceClient.LocalIPv4() ?? "<本机局域网IP>";
        Toast("桥接服务地址",
              $"http://{ip}:{_port}/status\n\n设备端 Bridge host 填：{ip}:{_port}");
    }

    static void Toast(string title, string text)
    {
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

// Small modal prompt, the NSAlert-with-text-field equivalent.
static class InputDialog
{
    public static string Show(string title, string message, string value, string placeholder)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Microsoft YaHei UI", 9f),
            ClientSize = new Size(380, 140),
            TopMost = true,
        };
        var label = new Label { Text = message };
        label.SetBounds(14, 12, 352, 40);
        var textBox = new TextBox { Text = value, PlaceholderText = placeholder };
        textBox.SetBounds(14, 58, 352, 24);
        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK };
        ok.SetBounds(196, 96, 80, 28);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(286, 96, 80, 28);
        form.Controls.AddRange(new Control[] { label, textBox, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }
}
