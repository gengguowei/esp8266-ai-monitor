using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AIClockBridge;

// Live "mirror" of the ESP8266 screen, shown as a popup near the tray icon.
// Not a video stream: the PC re-renders the same scene from the same data —
// /api/info says which app the device is showing (and a sprite_rev that bumps
// when animations change), /sprite/<app>/raw provides the exact frames the
// device draws (custom upload or built-in), and the local StatusService
// supplies the quota numbers the device gets from /status. Result: what you
// see here is what the panel shows, including the walk cycle animating only
// while that app is "working".

// MARK: - the 240x240 replica control

sealed class MirrorControl : Control
{
    // scene state, all in the device's 240x240 logical coordinates
    public List<Bitmap> Frames = new();
    public int FrameIdx;
    public int SpriteW = 120, SpriteH = 120;
    public double RingPct;
    public bool NeedsInput; // shown app waiting on approval -> red border flash
    public bool FlashOn;
    public string Line1 = "5h -";
    public string Line2 = "Weekly -";
    public bool ShowingClaude = true;
    public bool DeviceOK;
    public bool StockMode;
    public bool UpMode;
    public Bitmap UpFrame;
    public StockMonitor.Row[] StockRows = Array.Empty<StockMonitor.Row>();

    static readonly Image ClaudeLogo = LoadAsset("claude-logo.png");
    static readonly Image CodexLogo = LoadAsset("codex-logo.png");

    static readonly Color Green = Color.FromArgb(0, 217, 51);
    static readonly Color Yellow = Color.FromArgb(255, 204, 0);

    public MirrorControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal static Image LoadAsset(string name)
    {
        var asm = typeof(MirrorControl).Assembly;
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (resource == null) return new Bitmap(1, 1);
        using var stream = asm.GetManifestResourceStream(resource);
        return Image.FromStream(stream);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var scale = Width / 240f;
        g.ScaleTransform(scale, scale);

        // panel background
        using (var panel = RoundedRect(new RectangleF(0, 0, 240, 240), 10))
        {
            g.FillPath(Brushes.Black, panel);
            g.SetClip(panel);
        }

        if (UpMode)
        {
            if (UpFrame != null) g.DrawImage(UpFrame, new Rectangle(0, 0, 240, 240));
            return;
        }
        if (StockMode)
        {
            DrawStockScene(g);
            return;
        }

        // square quota ring: margin 4, thickness 10, clockwise from top-left
        const float m = 4, t = 10;
        const float side = 240 - 2 * m;
        using (var ring = new SolidBrush(DeviceOK ? Green : Color.FromArgb(90, 90, 90)))
        {
            var remaining = side * 4 * (float)(Math.Clamp(RingPct, 0, 100) / 100);
            const float x0 = m, y0 = m, x1 = 240 - m;
            var seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x0, y0, seg, t);                    // top
            remaining -= side;
            seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x1 - t, y0, t, seg);                // right
            remaining -= side;
            seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x1 - seg, 240 - m - t, seg, t);     // bottom
            remaining -= side;
            seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x0, 240 - m - seg, t, seg);         // left
        }

        // sprite, centered, pixel-crisp
        if (Frames.Count > 0)
        {
            var img = Frames[Math.Min(FrameIdx, Frames.Count - 1)];
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, new Rectangle(120 - SpriteW / 2, 120 - SpriteH / 2, SpriteW, SpriteH));
            g.Restore(state);
        }

        // app logo, top-left inside the ring (firmware draws it at 14,18 @40px)
        g.DrawImage(ShowingClaude ? ClaudeLogo : CodexLogo, new Rectangle(14, 18, 40, 40));

        // quota text
        using (var font = new Font("Consolas", 13, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.DrawString(Line1, font, Brushes.White, new RectangleF(0, 188, 240, 18), fmt);
            g.DrawString(Line2, font, Brushes.White, new RectangleF(0, 206, 240, 18), fmt);
        }

        if (!DeviceOK)
        {
            using var font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center };
            using var red = new SolidBrush(Color.FromArgb(255, 69, 58));
            g.DrawString("设备离线", font, red, new RectangleF(0, 60, 240, 20), fmt);
        }

        // approval pending: blink the whole border red over everything else
        if (NeedsInput && FlashOn)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 59, 48));
            g.FillRectangle(red, m, m, side, t);
            g.FillRectangle(red, m, 240 - m - t, side, t);
            g.FillRectangle(red, m, m, t, side);
            g.FillRectangle(red, 240 - m - t, m, t, side);
        }
    }

    void DrawStockScene(Graphics g)
    {
        using var greyBrush = new SolidBrush(Color.FromArgb(140, 140, 140));
        using var codeFont = new Font("Microsoft YaHei UI", 7.5f);
        using var valueFont = new Font("Consolas", 13f, FontStyle.Bold);
        using var labelFont = new Font("Consolas", 6.5f);
        if (StockRows.Length == 0)
        {
            using var center0 = new StringFormat { Alignment = StringAlignment.Center };
            using var hintFont = new Font("Microsoft YaHei UI", 8.5f);
            g.DrawString("未配置自选股\n右键托盘 → 设置自选股…", hintFont, greyBrush,
                         new RectangleF(0, 104, 240, 40), center0);
            return;
        }
        for (int i = 0; i < Math.Min(StockRows.Length, 4); i++)
        {
            var row = StockRows[i];
            float y0 = 10 + i * 54;
            var label = row.Name.Length == 0 ? row.Code : $"{row.Code}  {row.Name}";
            g.DrawString(label, codeFont, greyBrush, 14, y0);
            g.DrawString(row.Price, valueFont, Brushes.White, 12, y0 + 16);
            using var pctBrush = new SolidBrush(row.Up > 0 ? Color.FromArgb(255, 59, 48)
                : (row.Up < 0 ? Green : Color.LightGray));
            using var right = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString(row.Pct, valueFont, pctBrush, new RectangleF(120, y0 + 16, 106, 22), right);
        }
        using var center = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("STOCKS", labelFont, greyBrush, new RectangleF(0, 226, 240, 12), center);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// MARK: - popup form (the popover)

sealed class MirrorForm : Form
{
    readonly StatusService _service;
    readonly StockMonitor _stockMonitor;
    readonly BilibiliMonitor _upMonitor;
    int _upRevision = -1;
    readonly MirrorControl _mirror = new();
    readonly RadioButton[] _modeButtons;
    static readonly string[] Modes = { "auto", "claude", "codex", "stock", "up" };
    static readonly string[] ModeLabels = { "自动", "Claude", "Codex", "股票", "UP主" };
    readonly Label _statusLabel = new();
    readonly TrackBar _brightness = new() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None };
    readonly Label _brightnessValue = new();
    readonly Button _upFetchButton = new() { Text = "立即获取", Visible = false };
    readonly ToolTip _upTooltip = new();
    readonly Control[] _bottomControls;
    bool _upControlsShown;
    string _upActionMessage;
    DateTime _upActionUntil;
    // Drag streams many scroll events; posts to the single-threaded ESP8266 web
    // server are throttled mid-drag and the final value always flushes on mouse-up.
    int? _pendingBrightness;
    DateTime _lastBrightnessSentAt = DateTime.MinValue;

    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 120 };
    readonly Dictionary<string, (int Rev, List<Bitmap> Frames, int W, int H)> _spriteCache = new();
    DeviceInfo _lastInfo;
    StatusSnapshot _latestSnapshot;
    readonly HashSet<string> _fetchingSlots = new();
    bool _applyingMode; // suppress CheckedChanged while reflecting device state
    bool _tickRunning, _tickAgain;
    bool _modePumpRunning;
    string _pendingMode, _requestedMode, _visibleShowing;
    DateTime _modeRequestedAt;

    public MirrorForm(StatusService service, StockMonitor stockMonitor, BilibiliMonitor upMonitor)
    {
        _service = service;
        _stockMonitor = stockMonitor;
        _upMonitor = upMonitor;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = SystemColors.Control;
        Padding = new Padding(1);

        ClientSize = new Size(Px(316), Px(424));

        _mirror.SetBounds(Px(14), Px(14), Px(288), Px(288));
        Controls.Add(_mirror);

        _modeButtons = new RadioButton[Modes.Length];
        // Five readable tags over two rows; do not squeeze Claude/Codex into
        // 48px buttons or rely on clipping at high DPI.
        ClientSize = new Size(Px(316), Px(454));
        for (int i = 0; i < Modes.Length; i++)
        {
            var segWidth = Px(288) / (i < 3 ? 3 : 2);
            var btn = new RadioButton
            {
                Appearance = Appearance.Button,
                Text = ModeLabels[i],
                TextAlign = ContentAlignment.MiddleCenter,
                Tag = Modes[i],
                AutoSize = false,
            };
            btn.SetBounds(Px(14) + (i < 3 ? i : i - 3) * segWidth, Px(306 + i / 3 * 30), segWidth, Px(28));
            btn.CheckedChanged += ModeChanged;
            _modeButtons[i] = btn;
            Controls.Add(btn);
        }

        var sunLabel = new Label
        {
            Text = "☀",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };
        sunLabel.SetBounds(Px(12), Px(346), Px(24), Px(26));
        Controls.Add(sunLabel);
        _brightness.SetBounds(Px(36), Px(346), Px(216), Px(26));
        _brightness.Scroll += (_, _) => OnBrightnessInput(final: false);
        _brightness.MouseUp += (_, _) => OnBrightnessInput(final: true);
        Controls.Add(_brightness);
        _brightnessValue.SetBounds(Px(254), Px(346), Px(48), Px(26));
        _brightnessValue.TextAlign = ContentAlignment.MiddleRight;
        _brightnessValue.ForeColor = SystemColors.GrayText;
        _brightnessValue.Font = new Font("Microsoft YaHei UI", 8.5f);
        _brightnessValue.Text = "100%";
        Controls.Add(_brightnessValue);

        _statusLabel.SetBounds(Px(10), Px(378), Px(296), Px(36));
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Font = new Font("Microsoft YaHei UI", 8.5f);
        _statusLabel.Text = "连接设备中…";
        _statusLabel.AutoEllipsis = true;
        Controls.Add(_statusLabel);
        _bottomControls = new Control[] { sunLabel, _brightness, _brightnessValue, _statusLabel };
        foreach (Control control in _bottomControls)
            control.Top += Px(30);
        _upFetchButton.SetBounds(Px(14), Px(370), Px(288), Px(28));
        _upFetchButton.Click += async (_, _) => await FetchUpNow();
        Controls.Add(_upFetchButton);

        _pollTimer.Tick += async (_, _) => await Tick();
        _animTimer.Tick += (_, _) => AnimTick();
        Deactivate += (_, _) => HidePopup(); // transient, like NSPopover
    }

    float ScaleF() => DeviceDpi / 96f;
    int Px(int logical) => (int)Math.Round(logical * ScaleF());

    void SetUpControls(bool shown)
    {
        if (_upControlsShown == shown) return;
        _upControlsShown = shown;
        var delta = Px(shown ? 30 : -30);
        foreach (var control in _bottomControls) control.Top += delta;
        _upFetchButton.Visible = shown;
        ClientSize = new Size(Px(316), Px(shown ? 484 : 454));
        if (Visible) Top -= delta;
    }

    async Task FetchUpNow()
    {
        _upFetchButton.Enabled = false;
        _upFetchButton.Text = "获取中…";
        _upActionMessage = "正在获取 UP主数据…";
        _upActionUntil = DateTime.MaxValue;
        ShowUpStatus();
        try
        {
            _upActionMessage = await _upMonitor.FetchNow();
            _upActionUntil = DateTime.Now.AddSeconds(12);
            if (_mirror.UpMode) { ApplyUpFrame(); ShowUpStatus(); }
        }
        finally { _upFetchButton.Enabled = true; _upFetchButton.Text = "立即获取"; }
    }

    void ShowUpStatus()
    {
        if (!_mirror.UpMode) return;
        var profile = _upMonitor.Snapshot;
        _statusLabel.Text = DateTime.Now < _upActionUntil ? _upActionMessage :
            _upMonitor.IsFetching ? "正在获取 UP主数据…" :
            _upMonitor.Error != null ? $"UP主 · {_upMonitor.Error}（保留缓存）" :
            !string.IsNullOrEmpty(_upMonitor.SourceWarning) ? "UP主 · " + _upMonitor.SourceWarning :
            profile.VideoCheckedAt.HasValue ? $"UP主 · 更新于 {profile.VideoCheckedAt.Value.ToLocalTime():HH:mm} · 低频刷新" :
            "UP主 · 等待首次获取";
        if (_lastInfo?.SupportsUp == false && DateTime.Now >= _upActionUntil)
            _statusLabel.Text = "UP主预览 · 设备需固件 0.4.17 或更新版本";
        _upTooltip.SetToolTip(_statusLabel, _statusLabel.Text + "\n" + _upMonitor.SourcesSummary);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(120, 120, 120));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void Toggle()
    {
        if (Visible)
        {
            HidePopup();
            return;
        }
        // anchor to the tray corner of the primary screen, near the cursor
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Min(Math.Max(Cursor.Position.X - Width / 2, area.Left + 8),
                         area.Right - Width - 8);
        var y = area.Bottom - Height - 8;
        Location = new Point(x, y);
        Show();
        Activate();
        _pollTimer.Start();
        _animTimer.Start();
        _ = Tick();
    }

    void HidePopup()
    {
        Hide();
        _pollTimer.Stop();
        _animTimer.Stop();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HidePopup();
        }
        base.OnFormClosing(e);
    }

    void OnBrightnessInput(bool final)
    {
        var level = _brightness.Value;
        _brightnessValue.Text = $"{level}%";
        _pendingBrightness = level;
        if (!final && (DateTime.Now - _lastBrightnessSentAt).TotalMilliseconds < 250) return;
        FlushBrightness();
    }

    void FlushBrightness()
    {
        if (_pendingBrightness is not int level) return;
        _pendingBrightness = null;
        _lastBrightnessSentAt = DateTime.Now;
        _ = DeviceClient.SetBrightness(level);
    }

    /// Follow the device's reported brightness (changed via its web page or
    /// another client) — but never while the user is mid-adjustment here.
    void SyncBrightness(DeviceInfo info)
    {
        if (_pendingBrightness != null ||
            (DateTime.Now - _lastBrightnessSentAt).TotalSeconds < 2) return;
        var level = Math.Clamp(info.Brightness, 0, 100);
        _brightness.Value = level;
        _brightnessValue.Text = $"{level}%";
    }

    async Task Tick()
    {
        if (_tickRunning) { _tickAgain = true; return; }
        _tickRunning = true;
        try
        {
            do
            {
                _tickAgain = false;
                await RefreshInfo();
            } while (_tickAgain && Visible);
        }
        finally { _tickRunning = false; }
    }

    async Task RefreshInfo()
    {
        DeviceInfo info;
        try
        {
            info = await DeviceClient.FetchInfo();
        }
        catch (Exception)
        {
            if (!Visible) return;
            if (_requestedMode == "up" || _mirror.UpMode) ApplyUpFrame();
            _mirror.DeviceOK = false;
            _mirror.Invalidate();
            _statusLabel.Text = DeviceClient.Host.Length == 0
                ? "未设置设备地址（右键托盘 → 设置设备地址）" : $"无法连接 {DeviceClient.Host}";
            if (_mirror.UpMode) ShowUpStatus();
            return;
        }
        if (!Visible) return;
        // Snapshot can rescan large session-log trees every 5s. Do that off
        // the WinForms thread; animation and click previews use this copy.
        var snapshot = await Task.Run(() => _service.Snapshot());
        if (!Visible) return;
        _latestSnapshot = snapshot;
        _lastInfo = info;
        _mirror.DeviceOK = true;
        if (_requestedMode == info.Mode ||
            (_requestedMode != null && !_modePumpRunning &&
             DateTime.UtcNow - _modeRequestedAt > TimeSpan.FromSeconds(8) &&
             !(_requestedMode == "up" && !info.SupportsUp)))
            _requestedMode = null;
        var requested = _requestedMode;
        ApplyScene(info, requested);
        EnsureSprite(info, requested);
        SyncBrightness(info);
        var modeIdx = Math.Max(0, Array.IndexOf(Modes, requested ?? info.Mode));
        _applyingMode = true;
        _modeButtons[modeIdx].Checked = true;
        _applyingMode = false;
        var modeText = (requested ?? info.Mode) == "auto" ? "自动切换" : "固定显示";
        _statusLabel.Text = $"{info.Transport} · {modeText}";
        ShowUpStatus();
    }

    /// Quota lines & ring exactly as the firmware computes them from /status.
    void ApplyScene(DeviceInfo info, string requested = null)
    {
        var effective = requested is "claude" or "codex" or "stock" or "up"
            ? requested : info.Effective;
        var showing = requested is "claude" or "codex" ? requested : info.Showing;
        _mirror.StockMode = effective == "stock";
        _mirror.UpMode = effective == "up";
        SetUpControls(_mirror.UpMode);
        _visibleShowing = showing;
        if (_mirror.UpMode) { ApplyUpFrame(); return; }
        if (_mirror.StockMode)
        {
            _mirror.StockRows = _stockMonitor.Snapshot;
            _mirror.Invalidate();
            return;
        }
        var snap = _latestSnapshot;
        if (snap == null) return;
        _mirror.ShowingClaude = showing != "codex";
        if (_mirror.ShowingClaude)
        {
            var pct = snap.Claude.FiveHourPct
                ?? (snap.Claude.SessionWindowMin > 0
                    ? 100.0 * snap.Claude.SessionMin / snap.Claude.SessionWindowMin : 0);
            _mirror.RingPct = pct;
            _mirror.Line1 = "5h " + PctText(pct);
            _mirror.Line2 = "Weekly " + PctText(snap.Claude.SevenDayPct);
            _mirror.NeedsInput = snap.Claude.NeedsInput;
        }
        else
        {
            // Codex may only have a weekly window now (5h limit dropped):
            // ring + single line follow whatever windows actually exist.
            _mirror.RingPct = snap.Codex.PrimaryPct ?? snap.Codex.WeeklyPct ?? 0;
            if (!snap.Codex.PrimaryPct.HasValue && snap.Codex.WeeklyPct.HasValue)
            {
                _mirror.Line1 = "Weekly " + PctText(snap.Codex.WeeklyPct);
                _mirror.Line2 = "";
            }
            else
            {
                _mirror.Line1 = "5h " + PctText(snap.Codex.PrimaryPct);
                _mirror.Line2 = "Weekly " + PctText(snap.Codex.WeeklyPct);
            }
            _mirror.NeedsInput = snap.Codex.NeedsInput;
        }
        _mirror.Invalidate();
    }

    static string PctText(double? pct) =>
        pct.HasValue && pct.Value >= 0 ? $"{(int)pct.Value}%" : "-";

    void EnsureSprite(DeviceInfo info, string requested = null)
    {
        if (_mirror.StockMode || _mirror.UpMode) return;
        var slot = (requested is "claude" or "codex" ? requested : info.Showing) == "codex"
            ? "codex" : "claude";
        var w = slot == "claude" ? info.ClaudeW : info.CodexW;
        var h = slot == "claude" ? info.ClaudeH : info.CodexH;
        if (_spriteCache.TryGetValue(slot, out var cached) && cached.Rev == info.SpriteRev)
        {
            _mirror.Frames = cached.Frames;
            _mirror.SpriteW = cached.W;
            _mirror.SpriteH = cached.H;
            return;
        }
        if (_mirror.Frames.Count > 0)
        {
            _mirror.Frames = new(); // do not show the previous app's sprite while loading
            _mirror.Invalidate();
        }
        if (!_fetchingSlots.Add(slot)) return;
        _ = FetchSprite(slot, info.SpriteRev, w, h);
    }

    async Task FetchSprite(string slot, int rev, int w, int h)
    {
        try
        {
            var data = await DeviceClient.FetchSpriteRaw(slot);
            var frames = await Task.Run(() => Rgb565.DecodeSpriteFrames(data, w, h));
            if (frames.Count == 0) return;
            if (_spriteCache.TryGetValue(slot, out var old))
                foreach (var f in old.Frames) f.Dispose();
            _spriteCache[slot] = (rev, frames, w, h);
            if (_visibleShowing == slot && !_mirror.StockMode && !_mirror.UpMode)
            {
                _mirror.Frames = frames;
                _mirror.SpriteW = w;
                _mirror.SpriteH = h;
                _mirror.Invalidate();
            }
        }
        catch (Exception)
        {
            // device unreachable / mid-upload: next tick retries
        }
        finally
        {
            _fetchingSlots.Remove(slot);
        }
    }

    int _flashCounter;

    void AnimTick()
    {
        if (_lastInfo == null || _latestSnapshot == null || _mirror.StockMode || _mirror.UpMode) return;

        // ~400ms red-border flash while an approval is pending (device cadence)
        if (_mirror.NeedsInput)
        {
            _flashCounter++;
            if (_flashCounter >= 3) // 3 * 0.12s ≈ 0.36s
            {
                _flashCounter = 0;
                _mirror.FlashOn = !_mirror.FlashOn;
                _mirror.Invalidate();
            }
        }
        else if (_mirror.FlashOn)
        {
            _mirror.FlashOn = false;
            _mirror.Invalidate();
        }

        if (_mirror.Frames.Count == 0) return;
        var snap = _latestSnapshot;
        var working = _visibleShowing == "codex"
            ? snap.Codex.Status == "working" : snap.Claude.Status == "working";
        if (working)
        {
            _mirror.FrameIdx = (_mirror.FrameIdx + 1) % _mirror.Frames.Count;
        }
        else if (_mirror.FrameIdx != 0)
        {
            _mirror.FrameIdx = 0;
        }
        _mirror.Invalidate();
    }

    void ModeChanged(object sender, EventArgs e)
    {
        if (_applyingMode || sender is not RadioButton { Checked: true, Tag: string mode }) return;
        _requestedMode = _pendingMode = mode;
        _modeRequestedAt = DateTime.UtcNow;
        _mirror.UpMode = mode == "up";
        SetUpControls(_mirror.UpMode);
        if (mode == "up") { _ = _upMonitor.Activate(); _mirror.UpMode = true; ApplyUpFrame(); }
        if (_lastInfo != null)
        {
            ApplyScene(_lastInfo, mode);
            EnsureSprite(_lastInfo, mode);
        }
        _ = PumpModeChanges();
    }

    void ApplyUpFrame()
    {
        SetUpControls(true);
        _ = _upMonitor.Activate();
        var frame = _upMonitor.Screen;
        if (_upRevision != frame.Revision)
        {
            var bitmap = Rgb565.Decode(frame.Pixels, 0, 240, 240);
            _mirror.UpFrame?.Dispose(); _mirror.UpFrame = bitmap; _upRevision = frame.Revision;
        }
        _mirror.Invalidate();
    }

    async Task PumpModeChanges()
    {
        if (_modePumpRunning) return;
        _modePumpRunning = true;
        try
        {
            while (_pendingMode != null)
            {
                await Task.Delay(80); // coalesce a burst of tab clicks
                var mode = _pendingMode;
                _pendingMode = null;
                if (mode == "up" && _lastInfo?.SupportsUp == false) continue;
                try { await Task.Run(() => DeviceClient.SetDisplayMode(mode)); }
                catch (Exception) { if (_requestedMode == mode) _requestedMode = null; }
            }
        }
        finally { _modePumpRunning = false; }
        await Tick();
    }
}
