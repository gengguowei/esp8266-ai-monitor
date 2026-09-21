using System.Security.Cryptography;
using AIClockFlasher.Esp;

namespace AIClockFlasher;

sealed class DeviceSnapshot
{
    public string Port;
    public string Chip = "ESP8266";
    public string Mac;
    public uint FlashId;
    public long FlashBytes;
    public string FlashVendor;
    public RunningFirmware Running;      // null if the device did not answer #HELLO
    public string ProbeNote;
}

enum BackupScope
{
    /// 0x0..0x100000 - the application image only. Enough to roll a firmware
    /// version back, and it is what the auto-backup before a flash uses
    /// because the write never touches anything above it.
    Application,

    /// The whole 4MB, LittleFS included: WiFi credentials, brightness, custom
    /// sprite animations. The one to take before doing anything unusual.
    Full,
}

/// Owns the connection to the board and runs the long operations. All public
/// methods block; MainForm calls them on a worker thread.
sealed class FlashService : IDisposable
{
    public const int AppRegionSize = 1024 * 1024;

    readonly Action<string> _log;
    readonly BridgeControl _bridge;

    EspLoader _loader;

    public DeviceSnapshot Device { get; private set; }
    public bool IsConnected => _loader != null;
    public string BackupDirectory { get; set; }

    public event Action<string, double> Progress;   // phase text, 0..1 (-1 = indeterminate)

    public FlashService(Action<string> log)
    {
        _log = log ?? (_ => { });
        _bridge = new BridgeControl(_log);
    }

    void Report(string phase, double value) => Progress?.Invoke(phase, value);

    // =====================================================================
    // connect / disconnect
    // =====================================================================

    public DeviceSnapshot Connect(string portName, int baud, CancellationToken cancel)
    {
        Disconnect();

        _bridge.Stop();

        var snapshot = new DeviceSnapshot { Port = portName };

        // Read the installed version *first*, while the firmware is still
        // running and can answer for itself. Once we reset into the
        // bootloader the only thing left to ask is the flash chip.
        Report("读取设备当前固件版本…", -1);
        try
        {
            snapshot.Running = DeviceProbe.Query(portName);
            if (snapshot.Running?.Version != null)
                _log($"设备当前固件：v{snapshot.Running.Version}");
            else
                snapshot.ProbeNote = "设备没有回应版本查询（可能是空片、刷了别的固件，或正处于配网界面）。";
        }
        catch (Exception e)
        {
            snapshot.ProbeNote = $"版本查询失败：{e.Message}";
        }
        if (snapshot.ProbeNote != null) _log(snapshot.ProbeNote);

        // Windows does not always hand the port back the instant the probe's
        // SerialPort is disposed; reopening too fast fails with "access denied".
        Thread.Sleep(300);

        var loader = new EspLoader(portName, _log);
        try
        {
            loader.Open();
            _log("让设备进入下载模式（接下来屏幕会黑掉，这是正常的——断开时会自动复位恢复）");
            Report("让设备进入下载模式…", -1);
            loader.Connect(cancel);
            loader.EnsureEsp8266();

            snapshot.Mac = loader.ReadMac();
            loader.RunStub(cancel);

            snapshot.FlashId = loader.FlashId();
            snapshot.FlashBytes = EspLoader.FlashSizeFromId(snapshot.FlashId);
            snapshot.FlashVendor = EspLoader.FlashVendorFromId(snapshot.FlashId);

            if (baud != EspLoader.RomBaud) loader.ChangeBaud(baud);

            _log($"芯片 ESP8266  MAC {snapshot.Mac}  Flash {Human(snapshot.FlashBytes)} ({snapshot.FlashVendor})");
        }
        catch
        {
            loader.Dispose();
            _bridge.Restart();
            throw;
        }

        _loader = loader;
        Device = snapshot;
        Report("已连接", 0);
        return snapshot;
    }

    public void Disconnect(bool rebootDevice = true, bool restartBridge = true)
    {
        if (_loader == null) return;
        try
        {
            if (rebootDevice)
            {
                _log("复位设备，让固件重新运行…");
                _loader.HardReset();
            }
        }
        catch { }
        _loader.Dispose();
        _loader = null;
        Device = null;
        if (restartBridge) _bridge.Restart();
        Report("未连接", 0);
    }

    /// Bounce the connection so the device boots what was just written and
    /// reports its own version again. The bridge stays stopped throughout -
    /// starting it just to kill it 1.5 seconds later would race with its own
    /// port scan.
    public DeviceSnapshot Reconnect(int baud, CancellationToken cancel)
    {
        var port = Device?.Port ?? _loader?.PortName;
        if (port == null) throw new EspException("没有可重连的串口。");
        Disconnect(rebootDevice: true, restartBridge: false);
        Thread.Sleep(2000);   // let the firmware boot far enough to answer #HELLO
        return Connect(port, baud, cancel);
    }

    public void Dispose() => Disconnect();

    EspLoader Require()
        => _loader ?? throw new EspException("设备未连接。");

    // =====================================================================
    // backup
    // =====================================================================

    public string Backup(BackupScope scope, string versionHint, CancellationToken cancel)
    {
        var loader = Require();
        int length = scope == BackupScope.Full
            ? (int)Math.Max(Device?.FlashBytes ?? 4L * 1024 * 1024, AppRegionSize)
            : AppRegionSize;

        var dir = ResolveBackupDirectory();
        Directory.CreateDirectory(dir);
        var version = string.IsNullOrWhiteSpace(versionHint) ? "unknown" : versionHint;
        var tag = scope == BackupScope.Full ? "full4m" : "app1m";
        var path = Path.Combine(dir,
            $"backup-esp8266-{version}-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");

        _log($"备份 {Human(length)} 到 {path}");
        var data = loader.ReadFlash(0, length,
            (done, total) => Report($"备份中 {Human(done)} / {Human(total)}", (double)done / total),
            cancel);

        File.WriteAllBytes(path, data);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        _log($"备份完成，SHA-256 {sha}");
        Report("备份完成", 1);
        return path;
    }

    public string ResolveBackupDirectory()
    {
        if (!string.IsNullOrWhiteSpace(BackupDirectory)) return BackupDirectory;
        var repo = FirmwareImage.FindRepoRoot();
        return repo != null
            ? Path.Combine(repo, "dist", "backups")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIClockFlasher", "backups");
    }

    // =====================================================================
    // flash
    // =====================================================================

    public sealed class FlashResult
    {
        public string BackupPath;
        public bool Verified;
        public TimeSpan Elapsed;
    }

    public FlashResult Flash(FirmwareImage image, bool autoBackup, BackupScope backupScope,
                             bool verify, CancellationToken cancel)
    {
        var loader = Require();
        var started = DateTime.UtcNow;
        var result = new FlashResult();

        GuardImage(image);

        if (autoBackup)
            result.BackupPath = Backup(backupScope, Device?.Running?.Version, cancel);

        _log($"写入 {image.FileName}（{Human(image.Length)}，v{image.Version ?? "?"}）");
        loader.WriteFlash(0, image.Data,
            (done, total) => Report($"写入中 {Human(done)} / {Human(total)}", (double)done / total),
            cancel);

        if (verify)
        {
            Report("校验中…", -1);
            var onChip = loader.FlashMd5(0, image.Length);
            if (!string.Equals(onChip, image.Md5, StringComparison.OrdinalIgnoreCase))
                throw new EspException(
                    $"校验失败：设备上的 MD5 是 {onChip}，固件文件是 {image.Md5}。" +
                    "设备现在可能无法启动，请重新刷写或用备份恢复。");
            result.Verified = true;
            _log($"校验通过，MD5 {onChip}");
        }

        loader.FlashFinish(reboot: false);
        result.Elapsed = DateTime.UtcNow - started;
        Report("刷写完成", 1);
        return result;
    }

    /// Sanity checks that are cheap to run and expensive to skip.
    void GuardImage(FirmwareImage image)
    {
        if (Device == null) return;

        if (Device.FlashBytes > 0 && image.FlashSizeBytes > Device.FlashBytes)
            throw new EspException(
                $"固件是按 {image.FlashSize} Flash 编译的，而这块板子只有 {Human(Device.FlashBytes)}。已中止。");

        if (Device.FlashBytes > 0 && image.Length > Device.FlashBytes)
            throw new EspException("固件比设备的 Flash 还大。已中止。");

        if (image.Length > AppRegionSize)
            _log($"提示：固件 {Human(image.Length)} 超过 1MB 应用区，写入会覆盖到文件系统区域。");
    }

    // =====================================================================
    // restore
    // =====================================================================

    public void Restore(string backupPath, CancellationToken cancel)
    {
        var loader = Require();
        var data = File.ReadAllBytes(backupPath);
        if (data.Length == 0) throw new EspException("备份文件是空的。");
        if (data[0] != 0xE9)
            throw new EspException("这个文件开头不是 0xE9，看起来不是设备镜像，已中止。");
        if (Device?.FlashBytes > 0 && data.Length > Device.FlashBytes)
            throw new EspException("备份比设备的 Flash 还大，已中止。");

        _log($"从备份恢复：{Path.GetFileName(backupPath)}（{Human(data.Length)}）");
        loader.WriteFlash(0, data,
            (done, total) => Report($"恢复中 {Human(done)} / {Human(total)}", (double)done / total),
            cancel);

        Report("校验中…", -1);
        var onChip = loader.FlashMd5(0, data.Length);
        var expected = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
        if (!string.Equals(onChip, expected, StringComparison.OrdinalIgnoreCase))
            throw new EspException("恢复后的校验失败，请重试。");

        loader.FlashFinish(reboot: false);
        _log("恢复完成并校验通过。");
        Report("恢复完成", 1);
    }

    public static string Human(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.##} MB"
         : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
         : $"{bytes} B";
}
