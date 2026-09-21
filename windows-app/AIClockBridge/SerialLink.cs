using Microsoft.Win32;
using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

/// USB-first transport for the CH340 UART on the clock. The firmware keeps
/// WiFi available as a standby path, but stops polling it while these frames
/// arrive. Unplugging USB closes the port and firmware resumes HTTP after its
/// 15-second wired timeout.
sealed class SerialLink : IDisposable
{
    const string PortKey = "serial_port";
    static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan NetInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(12);

    // CJK stock names are RGB565 strips the device normally pulls over HTTP,
    // which needs WiFi - so a USB-only device showed stock codes and no names,
    // with nothing to say why. It can ask for them over the wire instead:
    //   device -> bridge:  #WANTNAMES <rev>
    //   bridge -> device:  #NAMES <rev> <count>, then one #NAMEROW per pixel row
    //
    // One pixel row per frame is what makes this fit: 156 px * 2 bytes = 312,
    // base64 416 chars, ~450 with the prefix - comfortably inside the firmware's
    // 1600-byte line buffer, and it decodes straight into the same rowBuf the
    // HTTP path already blits from, so the device needs no 15KB buffer.
    //
    // Paced, because the device's 2048-byte UART buffer is only drained once per
    // loop() and a full set is ~21KB: two rows per 250ms tick is ~3.6 KB/s
    // against a 11.5 KB/s line. A full set takes ~6s, and is only ever sent when
    // the watchlist changes.
    const int NameRowsPerTick = 2;
    static readonly TimeSpan NameRequestDebounce = TimeSpan.FromSeconds(2);

    readonly StatusService _service;
    readonly NetSpeedMonitor _netMonitor;
    readonly StockMonitor _stockMonitor;
    readonly object _ioLock = new();
    readonly object _stateLock = new();
    readonly List<byte> _rx = new();
    readonly CancellationTokenSource _stop = new();

    SerialPort _port;
    Task _worker;
    volatile bool _linked;
    string _portName = "";
    DeviceInfo _deviceInfo;
    DateTime _openedAt = DateTime.MinValue;
    DateTime _lastDeviceAt = DateTime.MinValue;
    DateTime _lastHelloAt = DateTime.MinValue;
    DateTime _lastStatusAt = DateTime.MinValue;
    DateTime _lastNetAt = DateTime.MinValue;
    DateTime _lastScanAt = DateTime.MinValue;
    List<byte[]> _nameFrames;          // pending #NAMES/#NAMEROW lines
    int _nameFramesSent;
    DateTime _lastNameRequestAt = DateTime.MinValue;

    public SerialLink(StatusService service, NetSpeedMonitor netMonitor, StockMonitor stockMonitor)
    {
        _service = service;
        _netMonitor = netMonitor;
        _stockMonitor = stockMonitor;
    }

    public bool IsLinked => _linked;
    public string PortName => _portName;

    public void Start() => _worker ??= Task.Run(Run);

    public bool TryGetInfo(out DeviceInfo info)
    {
        lock (_stateLock)
        {
            info = _linked ? _deviceInfo : null;
            return info != null;
        }
    }

    public bool TrySendCommand(Dictionary<string, object> command)
    {
        if (!_linked) return false;
        return SendFrame("#CMD ", JsonSerializer.SerializeToUtf8Bytes(command));
    }

    async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Tick();
                await Task.Delay(250, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Write($"[serial] {e.Message}");
                ClosePort();
            }
        }
    }

    void Tick()
    {
        if (_port == null)
        {
            if (DateTime.UtcNow - _lastScanAt >= ScanInterval)
            {
                _lastScanAt = DateTime.UtcNow;
                ScanAndOpen();
            }
            return;
        }

        ReadPending();
        var now = DateTime.UtcNow;
        if (!_linked && now - _openedAt >= LinkTimeout)
        {
            ClosePort();
            return;
        }
        if (_linked && now - _lastDeviceAt >= LinkTimeout)
        {
            ClosePort();
            return;
        }
        if (now - _lastHelloAt >= HelloInterval)
        {
            _lastHelloAt = now;
            SendFrame("#HELLO", null);
        }
        if (!_linked) return;

        if (now - _lastStatusAt >= StatusInterval)
        {
            _lastStatusAt = now;
            SendFrame("#STATUS ", _service.Snapshot().ToJson());
            SendFrame("#STOCK ", _stockMonitor.ToJson());
        }
        if (now - _lastNetAt >= NetInterval)
        {
            _lastNetAt = now;
            SendFrame("#NET ", _netMonitor.ToJson(SystemStatsMonitor.Snapshot()));
        }
        PumpNameFrames();
    }

    /// Trickles the queued name-strip rows out behind the regular frames.
    void PumpNameFrames()
    {
        if (_nameFrames == null) return;
        for (var i = 0; i < NameRowsPerTick && _nameFramesSent < _nameFrames.Count; i++)
        {
            if (!SendLine(_nameFrames[_nameFramesSent])) { _nameFrames = null; return; }
            _nameFramesSent++;
        }
        if (_nameFramesSent < _nameFrames.Count) return;
        Log.Write($"[serial] sent {_nameFrames.Count} name-strip frames");
        _nameFrames = null;
    }

    void HandleWantNames(string args)
    {
        // The device re-asks while it is still waiting, so ignore repeats until
        // the current transfer has had time to land.
        var now = DateTime.UtcNow;
        if (_nameFrames != null) return;
        if (now - _lastNameRequestAt < NameRequestDebounce) return;
        _lastNameRequestAt = now;

        if (!_stockMonitor.TryGetNames(out var rev, out var data))
        {
            Log.Write("[serial] device asked for name strips, none rendered yet");
            return;
        }
        if (!int.TryParse(args.Trim(), out var wantRev) || wantRev != rev)
        {
            // The device is chasing a revision we no longer have; send the
            // current one - its own rev check will accept whatever matches the
            // #STOCK frame it has.
            Log.Write($"[serial] name strips requested for rev {args.Trim()}, sending rev {rev}");
        }
        _nameFrames = StockNameFrames.Build(data, rev, StockMonitor.NameW, StockMonitor.NameH)
            .Select(Encoding.ASCII.GetBytes).ToList();
        _nameFramesSent = 0;
    }


    void ScanAndOpen()
    {
        foreach (var name in CandidatePorts())
        {
            try
            {
                var port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    DtrEnable = false,
                    RtsEnable = false,
                    ReadTimeout = 50,
                    WriteTimeout = 1000,
                    ReadBufferSize = 4096,
                    WriteBufferSize = 4096,
                };
                port.Open();
                port.DiscardInBuffer();
                lock (_ioLock) _port = port;
                _portName = name;
                _openedAt = DateTime.UtcNow;
                _lastDeviceAt = DateTime.MinValue;
                _lastHelloAt = DateTime.MinValue;
                _rx.Clear();
                Log.Write($"[serial] trying {name}");
                return;
            }
            catch
            {
                // Busy ports (for example while flashing) are retried later.
            }
        }
    }

    static IEnumerable<string> CandidatePorts()
    {
        var present = SerialPort.GetPortNames()
            .OrderBy(PortNumber).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = Settings.Get(PortKey);
        if (present.Contains(saved, StringComparer.OrdinalIgnoreCase) && seen.Add(saved))
            yield return saved;

        // The shipped board uses CH340 (1A86:7523). CP210x is accepted for
        // compatible builds. Restricting normal scans avoids touching unrelated
        // serial equipment merely because it happens to be attached.
        foreach (var port in RegistryUsbPorts(new[] { "VID_1A86&PID_7523", "VID_10C4&PID_EA60" }))
            if (present.Contains(port, StringComparer.OrdinalIgnoreCase) && seen.Add(port))
                yield return port;

        var fallback = present.Where(p => !p.Equals("COM1", StringComparison.OrdinalIgnoreCase)
                                          && !seen.Contains(p)).ToArray();
        if (fallback.Length == 1) yield return fallback[0];
    }

    static IEnumerable<string> RegistryUsbPorts(string[] ids)
    {
        var result = new List<string>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return result;
            foreach (var deviceName in usb.GetSubKeyNames())
            {
                if (!ids.Any(id => deviceName.StartsWith(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                using var device = usb.OpenSubKey(deviceName);
                if (device == null) continue;
                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var parameters = device.OpenSubKey(instanceName + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && port.StartsWith("COM"))
                        result.Add(port);
                }
            }
        }
        catch
        {
            // Registry enumeration may be restricted; the single-port fallback
            // still covers the common laptop/desktop case.
        }
        return result;
    }

    static int PortNumber(string port) =>
        int.TryParse(port.AsSpan(Math.Min(3, port.Length)), out var n) ? n : int.MaxValue;

    void ReadPending()
    {
        try
        {
            lock (_ioLock)
            {
                if (_port == null || !_port.IsOpen) return;
                var count = Math.Min(_port.BytesToRead, 4096);
                if (count <= 0) return;
                var data = new byte[count];
                var read = _port.Read(data, 0, data.Length);
                for (var i = 0; i < read; i++) _rx.Add(data[i]);
            }
        }
        catch
        {
            ClosePort();
            return;
        }

        if (_rx.Count > 16384) _rx.Clear();
        int newline;
        while ((newline = _rx.IndexOf((byte)'\n')) >= 0)
        {
            var data = _rx.GetRange(0, newline).ToArray();
            _rx.RemoveRange(0, newline + 1);
            var line = Encoding.UTF8.GetString(data).Trim();
            HandleLine(line);
        }
    }

    void HandleLine(string line)
    {
        if (line.StartsWith("#WANTNAMES", StringComparison.Ordinal))
        {
            HandleWantNames(line.Length > 10 ? line[10..] : "");
            return;
        }
        if (!line.StartsWith("#DEVICE ", StringComparison.Ordinal)) return;
        var json = line[8..];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("name", out var name)
                || name.GetString() != "aiclock") return;
            if (!DeviceClient.TryParseInfo(json, out var info)) return;
            lock (_stateLock) _deviceInfo = info;
            _lastDeviceAt = DateTime.UtcNow;
            DeviceClient.DevicePollAt = _lastDeviceAt;
            if (_linked) return;
            _linked = true;
            Settings.Set(PortKey, _portName);
            _lastStatusAt = DateTime.MinValue;
            _lastNetAt = DateTime.MinValue;
            // ip/ssid/bridge matter even on a USB link: the stock page's CJK
            // name strips are fetched by the device over HTTP, so they need
            // WiFi and a bridge host regardless of how the prices arrive.
            Log.Write($"[serial] linked {_portName}, fw={info.Firmware}, "
                      + $"ip={(info.Ip.Length > 0 ? info.Ip : "-")}, "
                      + $"ssid={(info.Ssid.Length > 0 ? info.Ssid : "-")}, "
                      + $"bridge={(info.Bridge.Length > 0 ? info.Bridge : "-")}");
        }
        catch
        {
            // A truncated/noisy response is ignored; the next HELLO retries.
        }
    }

    /// A frame whose bytes are already complete (no prefix/payload split).
    bool SendLine(byte[] line) => SendFrame("", line);

    bool SendFrame(string prefix, byte[] payload)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var data = new byte[prefixBytes.Length + (payload?.Length ?? 0) + 1];
        Buffer.BlockCopy(prefixBytes, 0, data, 0, prefixBytes.Length);
        if (payload != null) Buffer.BlockCopy(payload, 0, data, prefixBytes.Length, payload.Length);
        data[^1] = (byte)'\n';
        try
        {
            lock (_ioLock)
            {
                if (_port == null || !_port.IsOpen) return false;
                _port.Write(data, 0, data.Length);
                return true;
            }
        }
        catch
        {
            ClosePort();
            return false;
        }
    }

    void ClosePort()
    {
        lock (_ioLock)
        {
            if (_port != null)
            {
                try { _port.Close(); } catch { }
                try { _port.Dispose(); } catch { }
            }
            _port = null;
        }
        if (_linked || _portName.Length > 0)
            Log.Write($"[serial] closed {_portName}");
        _linked = false;
        _portName = "";
        lock (_stateLock) _deviceInfo = null;
        _rx.Clear();
        _nameFrames = null;          // the device will ask again after re-linking
        _lastNameRequestAt = DateTime.MinValue;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _worker?.Wait(1000); } catch { }
        ClosePort();
        _stop.Dispose();
    }
}
