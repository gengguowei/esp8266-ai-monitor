using System.Diagnostics;
using System.Text;

namespace AIClockFlasher;

sealed class MainForm : Form
{
    readonly Settings _settings = Settings.Load();
    readonly FlashService _service;
    readonly System.Windows.Forms.Timer _portTimer = new() { Interval = 1500 };

    CancellationTokenSource _cancel;
    bool _busy;
    FirmwareImage _image;
    string _lastPortSignature = "";

    // ---- controls --------------------------------------------------------
    ComboBox _portBox, _baudBox, _firmwareBox, _scopeBox;
    Button _connectButton, _refreshButton, _browseButton, _flashButton,
           _backupButton, _restoreButton, _cancelButton, _openBackupsButton;
    CheckBox _autoConnect, _autoBackup, _verify;
    Label _statusDot, _statusText;
    Label _chipValue, _macValue, _flashValue, _runningValue;
    Label _fwVersionValue, _fwSizeValue, _fwBuiltValue, _fwParamsValue, _fwHashValue, _upgradeHint;
    ProgressBar _progress;
    Label _progressText;
    TextBox _log;

    // Sections whose height is measured from the real font at run time (see
    // FitSections) rather than guessed in logical pixels.
    GroupBox _deviceGroup, _firmwareGroup;
    TableLayoutPanel _deviceRow, _firmwareRow, _optionsRow, _actionRow, _statusRow;
    Panel _progressPanel;

    public MainForm()
    {
        _service = new FlashService(AppendLog);
        _service.Progress += OnProgress;

        Text = "AI 小屏幕 · 固件刷写工具";
        Font = new Font("Microsoft YaHei UI", 9f);

        // Every size in this file is a logical pixel at 96 DPI. Without a
        // declared scaling basis WinForms leaves those numbers alone while the
        // 9pt font still renders larger on a high-DPI display - big text in
        // small boxes, which is exactly how it looked. Dpi mode scales control
        // sizes by displayDpi/96, matching what the point-sized font does on
        // its own. It must be set before BuildUi(): PerformAutoScale only
        // touches children that already exist.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        // Physical pixels, so these are multiplied on a scaled display too -
        // kept small enough that 150% on a 1080p panel still fits.
        MinimumSize = new Size(880, 620);
        ClientSize = new Size(1000, 780);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        BuildUi();

        _portTimer.Tick += (_, _) => RefreshPorts(auto: true);
        _portTimer.Start();

        Load += (_, _) =>
        {
            FitSections();
            ClampToWorkingArea();
            AppendLog("AI 小屏幕固件刷写工具。刷写内核内置，无需 Python / esptool。");
            RefreshPorts(auto: false);
            LoadFirmwareList();
        };

        FormClosing += (_, _) =>
        {
            _cancel?.Cancel();
            SaveSettings();
            _service.Dispose();
        };
    }

    // =====================================================================
    // UI construction
    // =====================================================================

    /// Docked stack with fixed section heights rather than an auto-sizing
    /// table: WinForms' AutoSize inside a TableLayoutPanel collapses to zero
    /// height often enough that a layout you cannot see while writing is not
    /// worth the risk. Controls are added bottom-first because docking runs
    /// from the end of the collection backwards.
    void BuildUi()
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        Controls.Add(root);

        root.Controls.Add(BuildLogGroup());        // Fill - takes what is left
        root.Controls.Add(BuildProgressPanel());
        root.Controls.Add(BuildActionPanel());
        root.Controls.Add(BuildOptionsPanel());
        root.Controls.Add(BuildFirmwareGroup());
        root.Controls.Add(BuildDeviceGroup());     // ends up at the top
    }

    GroupBox BuildDeviceGroup()
    {
        var group = NewGroup("设备");

        // The connection status used to live at the right end of this row and
        // was the first thing to fall off the edge. It now sits next to the
        // progress bar, which is where it was already being duplicated.
        var top = NewRow(stretchColumn: 1,
            AutoWidth, Stretch, AutoWidth, AutoWidth, AutoWidth, AutoWidth, AutoWidth);

        _portBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _refreshButton = NewButton("刷新", (_, _) => RefreshPorts(auto: false));

        _baudBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
        _baudBox.Items.AddRange(new object[] { 115200, 230400, 460800, 921600 });
        _baudBox.SelectedItem = _settings.Baud;
        if (_baudBox.SelectedIndex < 0) _baudBox.SelectedItem = 460800;

        _connectButton = NewButton("连接设备", (_, _) => ToggleConnection());
        _connectButton.Margin = new Padding(12, 3, 3, 3);

        _autoConnect = new CheckBox
        {
            Text = "插上就自动连接",
            Checked = _settings.AutoConnect,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(10, 3, 3, 3),
        };

        AddRow(top, NewLabel("串口"), _portBox, _refreshButton, NewLabel("波特率", leftPad: 12),
            _baudBox, _connectButton, _autoConnect);

        var info = NewInfoGrid(4);
        AddInfoRow(info, 0, "芯片", out _chipValue);
        AddInfoRow(info, 1, "MAC 地址", out _macValue);
        AddInfoRow(info, 2, "Flash", out _flashValue);
        AddInfoRow(info, 3, "设备当前固件", out _runningValue);
        info.Dock = DockStyle.Top;

        group.Controls.Add(info);
        group.Controls.Add(top);
        _deviceGroup = group;
        _deviceRow = top;
        return group;
    }

    GroupBox BuildFirmwareGroup()
    {
        var group = NewGroup("固件");

        // The firmware path is the longest string in the window, so its column
        // is the one that absorbs the extra width.
        var top = NewRow(stretchColumn: 1, AutoWidth, Stretch, AutoWidth, AutoWidth);

        _firmwareBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        _firmwareBox.SelectedIndexChanged += (_, _) => OnFirmwareSelected();
        _browseButton = NewButton("浏览…", (_, _) => BrowseFirmware());

        AddRow(top, NewLabel("文件"), _firmwareBox, _browseButton,
            NewLabel("（也可以把 .bin 直接拖进窗口）", leftPad: 8, dim: true));

        var info = NewInfoGrid(5);
        AddInfoRow(info, 0, "版本", out _fwVersionValue);
        AddInfoRow(info, 1, "大小", out _fwSizeValue);
        AddInfoRow(info, 2, "文件时间", out _fwBuiltValue);
        AddInfoRow(info, 3, "镜像参数", out _fwParamsValue);
        AddInfoRow(info, 4, "SHA-256", out _fwHashValue);
        info.Dock = DockStyle.Top;

        _upgradeHint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.FromArgb(0, 102, 51),
            Padding = new Padding(4, 4, 0, 2),
            Text = "",
        };

        group.Controls.Add(_upgradeHint);
        group.Controls.Add(info);
        group.Controls.Add(top);
        _firmwareGroup = group;
        _firmwareRow = top;
        return group;
    }

    Panel BuildOptionsPanel()
    {
        var panel = NewRow(stretchColumn: 4,
            AutoWidth, AutoWidth, AutoWidth, AutoWidth, Stretch);
        panel.Padding = new Padding(0, 6, 0, 0);

        _autoBackup = new CheckBox
        {
            Text = "刷写前自动备份",
            Checked = _settings.AutoBackup,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };

        _scopeBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left,
        };
        _scopeBox.Items.AddRange(new object[] { "应用区 1MB（约 20 秒）", "完整 4MB（约 90 秒）" });
        _scopeBox.SelectedIndex = _settings.BackupScope == nameof(BackupScope.Full) ? 1 : 0;

        _verify = new CheckBox
        {
            Text = "写入后校验 MD5",
            Checked = _settings.Verify,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(16, 3, 3, 3),
        };

        _openBackupsButton = NewButton("打开备份目录", (_, _) => OpenBackupFolder());
        _openBackupsButton.Margin = new Padding(16, 3, 3, 3);

        AddRow(panel, _autoBackup, _scopeBox, _verify, _openBackupsButton, new Label { Text = "" });
        _optionsRow = panel;
        return panel;
    }

    Panel BuildActionPanel()
    {
        var panel = NewRow(stretchColumn: 4,
            AutoWidth, AutoWidth, AutoWidth, AutoWidth, Stretch);
        panel.Padding = new Padding(0, 6, 0, 0);

        _flashButton = NewButton("刷写固件", (_, _) => StartFlash(), bold: true);
        _backupButton = NewButton("仅备份", (_, _) => StartBackup());
        _restoreButton = NewButton("从备份恢复…", (_, _) => StartRestore());
        _cancelButton = NewButton("取消", (_, _) => _cancel?.Cancel());
        _cancelButton.Enabled = false;

        AddRow(panel, _flashButton, _backupButton, _restoreButton, _cancelButton, new Label { Text = "" });
        _actionRow = panel;
        return panel;
    }

    Panel BuildProgressPanel()
    {
        var panel = new Panel { Dock = DockStyle.Top, Padding = new Padding(3, 6, 3, 3) };

        _statusDot = new Label { Text = "●", ForeColor = Color.Gray, AutoSize = true, Anchor = AnchorStyles.Left };
        _statusText = NewLabel("未连接");
        _progressText = NewLabel("就绪", leftPad: 16);

        var status = NewRow(stretchColumn: 2, AutoWidth, AutoWidth, Stretch);
        AddRow(status, _statusDot, _statusText, _progressText);
        status.Dock = DockStyle.Top;

        _progress = new ProgressBar { Dock = DockStyle.Top, Maximum = 1000 };

        panel.Controls.Add(status);
        panel.Controls.Add(_progress);
        _progressPanel = panel;
        _statusRow = status;
        return panel;
    }

    GroupBox BuildLogGroup()
    {
        var group = new GroupBox { Text = "日志", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 9f),
            WordWrap = true,
        };
        group.Controls.Add(_log);
        return group;
    }

    int Sc(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    /// Sizes every section from the font the machine is actually rendering
    /// with, instead of from logical pixels chosen while writing the code.
    ///
    /// Declaring a scaling basis fixed the "big text in small boxes" problem
    /// but not this one: a section tall enough for four rows of Latin text at
    /// the font I assumed is not tall enough for four rows of 微软雅黑 at the
    /// size this machine renders it, and the fourth row gets cut in half.
    /// TextRenderer measures the real thing, so there is nothing left to guess.
    void FitSections()
    {
        var line = TextRenderer.MeasureText("Ag国", Font).Height;
        var control = line + Sc(12);      // a combo box / button, with its chrome
        var infoRow = line + Sc(7);       // one caption/value pair plus its margins

        FitComboWidth(_baudBox);
        FitComboWidth(_scopeBox);

        _deviceRow.Height = control + Sc(6);
        _firmwareRow.Height = control + Sc(6);
        _optionsRow.Height = control + Sc(12);
        _actionRow.Height = control + Sc(18);
        _statusRow.Height = line + Sc(6);

        _progress.Height = Sc(20);
        _progressPanel.Height = _progress.Height + _statusRow.Height + Sc(12);

        SetGroupHeight(_deviceGroup, _deviceRow.Height + 4 * infoRow + Sc(14));
        SetGroupHeight(_firmwareGroup, _firmwareRow.Height + 5 * infoRow + line + Sc(20));
    }

    /// GroupBox chrome - the caption band, the border and the padding - is
    /// whatever the current height is minus the area it offers its children.
    static void SetGroupHeight(GroupBox group, int contentHeight)
    {
        var chrome = Math.Max(0, group.Height - group.DisplayRectangle.Height);
        group.Height = contentHeight + chrome;
    }

    /// Re-measure when the window is dragged to a display with a different
    /// scale factor; WinForms rescales the controls but not these decisions.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(FitSections);
    }

    /// The default size is in logical pixels, so on a 150%-scaled 1080p panel
    /// it becomes taller than the desktop and the log group hangs off the
    /// bottom edge. Shrink to whatever actually fits and re-centre.
    void ClampToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        if (width == Width && height == Height) return;

        // MinimumSize scaled up with everything else and would veto the shrink.
        MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
        Size = new Size(width, height);
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2));
    }

    // ---- small builders --------------------------------------------------

    static GroupBox NewGroup(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 100,               // provisional; FitSections sets the real one
        Padding = new Padding(10, 6, 10, 10),
    };

    static Label NewLabel(string text, int leftPad = 0, bool dim = false) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,        // vertically centred in its cell
        Margin = new Padding(3 + leftPad, 3, 3, 3),
        ForeColor = dim ? Color.Gray : SystemColors.ControlText,
    };

    // Column kinds for NewRow, named so the call sites read as a layout.
    const SizeType AutoWidth = SizeType.AutoSize;
    const SizeType Stretch = SizeType.Percent;

    /// One horizontal strip of controls. Exactly one column is Percent(100)
    /// and absorbs the window's extra width; the rest size to their content,
    /// so nothing has to be re-tuned when the display DPI changes.
    static TableLayoutPanel NewRow(int stretchColumn, params SizeType[] columns)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,            // provisional; FitSections sets the real one
            ColumnCount = columns.Length,
            RowCount = 1,
        };
        for (int i = 0; i < columns.Length; i++)
            row.ColumnStyles.Add(i == stretchColumn
                ? new ColumnStyle(SizeType.Percent, 100)
                : new ColumnStyle(columns[i]));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return row;
    }

    static void AddRow(TableLayoutPanel row, params Control[] controls)
    {
        for (int i = 0; i < controls.Length; i++)
        {
            if (controls[i].Anchor == (AnchorStyles.Top | AnchorStyles.Left))
                controls[i].Anchor = AnchorStyles.Left;
            row.Controls.Add(controls[i], i, 0);
        }
    }

    /// AutoSize, always. Hand-picked widths were how "打开备份目录" ended up
    /// rendering as "打开备份目" - a button sized for the font I assumed rather
    /// than the one the machine actually uses.
    static Button NewButton(string text, EventHandler onClick, bool bold = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 5, 12, 5),
        };
        if (bold) b.Font = new Font(b.Font, FontStyle.Bold);
        b.Click += onClick;
        return b;
    }

    /// Widen a fixed-choice list to its longest entry plus room for the arrow.
    /// Same reason as NewButton: "460800" was showing as "46080".
    void FitComboWidth(ComboBox box)
    {
        int width = 0;
        foreach (var item in box.Items)
            width = Math.Max(width, TextRenderer.MeasureText(item?.ToString() ?? "", box.Font).Width);
        box.Width = width + Sc(34);
    }

    static TableLayoutPanel NewInfoGrid(int rows)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = rows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(2, 6, 2, 2),
        };
        // AutoSize rather than a fixed 110px caption column: the labels are
        // Chinese text whose width tracks the font, and a pinned width either
        // clips them or leaves a gap once the display scales.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return grid;
    }

    static void AddInfoRow(TableLayoutPanel grid, int row, string caption, out Label value)
    {
        grid.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.Gray,
            Margin = new Padding(3, 4, 12, 2),
        }, 0, row);
        value = new Label { Text = "—", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 2) };
        grid.Controls.Add(value, 1, row);
    }

    // =====================================================================
    // ports
    // =====================================================================

    void RefreshPorts(bool auto)
    {
        List<PortEntry> ports;
        try { ports = SerialPorts.List(); }
        catch { return; }

        var signature = string.Join("|", ports.Select(p => p.ToString()));
        if (auto && signature == _lastPortSignature) return;
        _lastPortSignature = signature;

        var selected = (_portBox.SelectedItem as PortEntry)?.Name ?? _settings.LastPort;

        _portBox.BeginUpdate();
        _portBox.Items.Clear();
        foreach (var p in ports) _portBox.Items.Add(p);
        _portBox.EndUpdate();

        if (ports.Count == 0)
        {
            _portBox.SelectedIndex = -1;
            if (!_busy && _service.IsConnected) DisconnectQuietly("设备已拔出。");
            return;
        }

        var match = ports.FirstOrDefault(p => p.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    ?? ports.FirstOrDefault(p => p.IsKnownBoard)
                    ?? ports[0];
        _portBox.SelectedItem = match;

        if (!auto) AppendLog($"找到 {ports.Count} 个串口：{string.Join("，", ports.Select(p => p.ToString()))}");

        if (_autoConnect.Checked && !_busy && !_service.IsConnected && match.IsKnownBoard)
            ToggleConnection();
    }

    // =====================================================================
    // firmware selection
    // =====================================================================

    void LoadFirmwareList()
    {
        var repo = FirmwareImage.FindRepoRoot();
        var candidates = FirmwareImage.Discover(repo);
        if (!string.IsNullOrEmpty(_settings.LastFirmware) && File.Exists(_settings.LastFirmware))
            candidates.Insert(0, _settings.LastFirmware);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _firmwareBox.Items.Clear();
        foreach (var path in candidates)
            if (seen.Add(Path.GetFullPath(path))) _firmwareBox.Items.Add(path);

        if (_firmwareBox.Items.Count > 0) _firmwareBox.SelectedIndex = 0;
        else AppendLog("没有自动找到固件文件，点「浏览…」选一个 .bin。");
    }

    void SelectFirmware(string path)
    {
        var existing = _firmwareBox.Items.Cast<string>()
            .FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            _firmwareBox.Items.Insert(0, path);
            existing = path;
        }
        _firmwareBox.SelectedItem = existing;
    }

    void BrowseFirmware()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择固件文件",
            Filter = "固件镜像 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_firmwareBox.SelectedItem as string ?? "") ?? "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) SelectFirmware(dialog.FileName);
    }

    void OnFirmwareSelected()
    {
        if (_firmwareBox.SelectedItem is not string path) return;
        try
        {
            _image = FirmwareImage.Load(path);
            _fwVersionValue.Text = _image.Version != null
                ? $"v{_image.Version}   ({_image.VersionSource})"
                : "无法识别版本号";
            _fwSizeValue.Text = $"{FlashService.Human(_image.Length)}（{_image.Length:N0} 字节）";
            _fwBuiltValue.Text = _image.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss");
            _fwParamsValue.Text = $"{_image.SegmentCount} 段 · {_image.FlashMode} · {_image.FlashSize} · " +
                                  $"{_image.FlashFreq} · 入口 0x{_image.Entry:X8}";
            _fwHashValue.Text = _image.Sha256;
            AppendLog($"已选择固件 {_image.FileName}，版本 v{_image.Version ?? "?"}");
        }
        catch (Exception e)
        {
            _image = null;
            _fwVersionValue.Text = "读取失败";
            _fwSizeValue.Text = _fwBuiltValue.Text = _fwParamsValue.Text = _fwHashValue.Text = "—";
            AppendLog($"固件读取失败：{e.Message}");
        }
        UpdateUpgradeHint();
        UpdateButtons();
    }

    void UpdateUpgradeHint()
    {
        var installed = _service.Device?.Running?.Version;
        var target = _image?.Version;

        if (installed == null || target == null) { _upgradeHint.Text = ""; return; }

        var compare = CompareVersions(installed, target);
        (_upgradeHint.Text, _upgradeHint.ForeColor) = compare switch
        {
            < 0 => ($"将从 v{installed} 升级到 v{target}", Color.FromArgb(0, 102, 51)),
            0 => ($"设备上已经是 v{installed}，这次是重复刷写", Color.FromArgb(150, 100, 0)),
            _ => ($"注意：这是降级，从 v{installed} 回到 v{target}", Color.FromArgb(170, 60, 0)),
        };
    }

    static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Parse(a);
        var y = Parse(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    // =====================================================================
    // connect / disconnect
    // =====================================================================

    void ToggleConnection()
    {
        if (_busy) return;

        if (_service.IsConnected)
        {
            RunAsync("断开", _ => { _service.Disconnect(); return null; }, _ => SetConnectedUi(null));
            return;
        }

        if (_portBox.SelectedItem is not PortEntry port)
        {
            AppendLog("没有可用的串口。检查数据线和 CH340 驱动。");
            return;
        }

        var baud = (int)(_baudBox.SelectedItem ?? 460800);
        RunAsync("连接", cancel => _service.Connect(port.Name, baud, cancel),
            result => SetConnectedUi(result as DeviceSnapshot));
    }

    void DisconnectQuietly(string why)
    {
        AppendLog(why);
        try { _service.Disconnect(rebootDevice: false); } catch { }
        SetConnectedUi(null);
    }

    void SetConnectedUi(DeviceSnapshot device)
    {
        if (device == null)
        {
            _statusDot.ForeColor = Color.Gray;
            _statusText.Text = "未连接";
            _connectButton.Text = "连接设备";
            _chipValue.Text = _macValue.Text = _flashValue.Text = _runningValue.Text = "—";
        }
        else
        {
            _statusDot.ForeColor = Color.FromArgb(0, 160, 60);
            _statusText.Text = $"已连接 {device.Port}";
            _connectButton.Text = "断开设备";
            _chipValue.Text = device.Chip;
            _macValue.Text = device.Mac ?? "—";
            _flashValue.Text = device.FlashBytes > 0
                ? $"{FlashService.Human(device.FlashBytes)} · {device.FlashVendor} · ID 0x{device.FlashId:X6}"
                : $"未知（ID 0x{device.FlashId:X6}）";
            _runningValue.Text = device.Running?.Version != null
                ? $"v{device.Running.Version}" +
                  (device.Running.Brightness >= 0 ? $"   亮度 {device.Running.Brightness}%" : "")
                : device.ProbeNote ?? "未知";
        }
        UpdateUpgradeHint();
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var connected = _service.IsConnected;
        _flashButton.Enabled = !_busy && connected && _image != null;
        _backupButton.Enabled = !_busy && connected;
        _restoreButton.Enabled = !_busy && connected;
        _connectButton.Enabled = !_busy;
        _portBox.Enabled = _baudBox.Enabled = !_busy && !connected;
        _firmwareBox.Enabled = _browseButton.Enabled = _refreshButton.Enabled = !_busy;
        _cancelButton.Enabled = _busy;
    }

    // =====================================================================
    // operations
    // =====================================================================

    BackupScope SelectedScope()
        => _scopeBox.SelectedIndex == 1 ? BackupScope.Full : BackupScope.Application;

    void StartFlash()
    {
        if (_image == null || !_service.IsConnected) return;

        var installed = _service.Device?.Running?.Version;
        var summary = new StringBuilder();
        summary.AppendLine($"固件：{_image.FileName}");
        summary.AppendLine($"版本：{(installed != null ? $"v{installed}  →  " : "")}v{_image.Version ?? "?"}");
        summary.AppendLine($"大小：{FlashService.Human(_image.Length)}，写入地址 0x00000");
        summary.AppendLine(_autoBackup.Checked
            ? $"刷写前会先备份（{(SelectedScope() == BackupScope.Full ? "完整 4MB" : "应用区 1MB")}）"
            : "不做备份");
        summary.AppendLine();
        summary.Append("只覆盖应用区，不会擦除 WiFi 配置、亮度和自定义桌宠。确认开始？");

        if (MessageBox.Show(this, summary.ToString(), "确认刷写",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        var image = _image;
        var autoBackup = _autoBackup.Checked;
        var scope = SelectedScope();
        var verify = _verify.Checked;

        RunAsync("刷写", cancel => _service.Flash(image, autoBackup, scope, verify, cancel), result =>
        {
            var r = (FlashService.FlashResult)result;
            var text = new StringBuilder("刷写完成。");
            if (r.BackupPath != null) text.Append($"\n备份：{r.BackupPath}");
            if (r.Verified) text.Append("\nMD5 校验通过。");
            text.Append($"\n耗时 {r.Elapsed.TotalSeconds:0.0} 秒。");
            AppendLog(text.ToString().Replace("\n", "  "));
            MessageBox.Show(this, text.ToString(), "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            AfterWrite();
        });
    }

    void StartBackup()
    {
        if (!_service.IsConnected) return;
        var scope = SelectedScope();
        var version = _service.Device?.Running?.Version;
        RunAsync("备份", cancel => _service.Backup(scope, version, cancel), result =>
        {
            AppendLog($"备份已保存：{result}");
            MessageBox.Show(this, $"备份已保存到：\n{result}", "备份完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    void StartRestore()
    {
        if (!_service.IsConnected) return;

        var dir = _service.ResolveBackupDirectory();
        Directory.CreateDirectory(dir);
        using var dialog = new OpenFileDialog
        {
            Title = "选择要恢复的备份",
            Filter = "备份镜像 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            InitialDirectory = dir,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = dialog.FileName;

        var size = new FileInfo(path).Length;
        var warning = size > FlashService.AppRegionSize
            ? "\n\n这是一份完整备份，会把 WiFi 配置、亮度、自定义桌宠一起恢复到备份时的状态。"
            : "";
        if (MessageBox.Show(this,
                $"把这份备份写回设备？\n\n{Path.GetFileName(path)}\n{FlashService.Human(size)}{warning}",
                "确认恢复", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        RunAsync("恢复", cancel => { _service.Restore(path, cancel); return null; }, _ =>
        {
            MessageBox.Show(this, "恢复完成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            AfterWrite();
        });
    }

    /// After anything that changed the flash, the version the tool is showing
    /// is stale. Bounce the connection so the device reboots into the new
    /// firmware and reports its own version again.
    void AfterWrite()
    {
        if (!_service.IsConnected) return;
        var baud = (int)(_baudBox.SelectedItem ?? 460800);
        RunAsync("重新读取设备信息", cancel => _service.Reconnect(baud, cancel),
            result => SetConnectedUi(result as DeviceSnapshot));
    }

    void OpenBackupFolder()
    {
        var dir = _service.ResolveBackupDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    // =====================================================================
    // worker plumbing
    // =====================================================================

    void RunAsync(string what, Func<CancellationToken, object> work, Action<object> onSuccess)
    {
        if (_busy) return;
        _busy = true;
        _cancel = new CancellationTokenSource();
        UpdateButtons();
        _progress.Style = ProgressBarStyle.Marquee;
        _progressText.Text = $"{what}中…";

        var token = _cancel.Token;
        Task.Run(() =>
        {
            try
            {
                var result = work(token);
                BeginInvoke(() =>
                {
                    Finish();
                    onSuccess?.Invoke(result);
                });
            }
            catch (OperationCanceledException)
            {
                BeginInvoke(() =>
                {
                    Finish();
                    AppendLog($"{what}已取消。");
                    _progressText.Text = "已取消";
                });
            }
            catch (Exception e)
            {
                BeginInvoke(() =>
                {
                    Finish();
                    AppendLog($"{what}失败：{e.Message}");
                    _progressText.Text = $"{what}失败";
                    MessageBox.Show(this, e.Message, $"{what}失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (!_service.IsConnected) SetConnectedUi(null);
                });
            }
        });

        void Finish()
        {
            _busy = false;
            _cancel?.Dispose();
            _cancel = null;
            _progress.Style = ProgressBarStyle.Blocks;
            UpdateButtons();
        }
    }

    void OnProgress(string phase, double value)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _progressText.Text = phase;
            if (value < 0)
            {
                _progress.Style = ProgressBarStyle.Marquee;
            }
            else
            {
                _progress.Style = ProgressBarStyle.Blocks;
                _progress.Value = (int)Math.Clamp(value * 1000, 0, 1000);
            }
        });
    }

    void AppendLog(string message)
    {
        if (IsDisposed || _log == null) return;
        if (InvokeRequired && IsHandleCreated) { BeginInvoke(() => AppendLog(message)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    // =====================================================================
    // drag & drop
    // =====================================================================

    void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    void OnDragDrop(object sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var bin = files.FirstOrDefault(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) ?? files[0];
        SelectFirmware(bin);
    }

    void SaveSettings()
    {
        _settings.LastPort = (_portBox.SelectedItem as PortEntry)?.Name;
        _settings.LastFirmware = _firmwareBox.SelectedItem as string;
        _settings.Baud = (int)(_baudBox.SelectedItem ?? 460800);
        _settings.AutoConnect = _autoConnect.Checked;
        _settings.AutoBackup = _autoBackup.Checked;
        _settings.Verify = _verify.Checked;
        _settings.BackupScope = SelectedScope().ToString();
        _settings.Save();
    }
}
