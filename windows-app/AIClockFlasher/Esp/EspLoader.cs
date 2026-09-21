using System.Buffers.Binary;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;

namespace AIClockFlasher.Esp;

/// A self-contained ESP8266 ROM-bootloader client: SLIP framing, the download
/// protocol, the flasher stub, and enough SPI-register poking to identify the
/// flash chip. This is a C# port of the parts of esptool.py this project uses,
/// so that flashing needs nothing but the .exe - no Python, no pip, no
/// firmware\.flash-tools\ that has to be installed on every machine first.
///
/// Everything is synchronous and blocking; callers run it on a worker thread
/// and get progress through the callbacks.
sealed class EspLoader : IDisposable
{
    // ---- SLIP ------------------------------------------------------------
    const byte SlipEnd = 0xC0;
    const byte SlipEsc = 0xDB;
    const byte SlipEscEnd = 0xDC;
    const byte SlipEscEsc = 0xDD;

    // ---- commands --------------------------------------------------------
    const byte CmdFlashBegin = 0x02;
    const byte CmdFlashData = 0x03;
    const byte CmdFlashEnd = 0x04;
    const byte CmdMemBegin = 0x05;
    const byte CmdMemEnd = 0x06;
    const byte CmdMemData = 0x07;
    const byte CmdSync = 0x08;
    const byte CmdWriteReg = 0x09;
    const byte CmdReadReg = 0x0A;
    const byte CmdChangeBaud = 0x0F;  // stub only
    const byte CmdSpiFlashMd5 = 0x13;
    const byte CmdReadFlash = 0xD2;   // stub only

    const byte ChecksumMagic = 0xEF;
    const int StatusBytes = 2;        // ESP8266 replies carry 2 status bytes

    public const int FlashSectorSize = 0x1000;

    /// How many 4 KB blocks the stub may send before waiting for our ack.
    /// esptool uses 64 (256 KB), which only works because the reader never
    /// falls behind; 16 keeps the whole window inside the driver buffer above
    /// and costs nothing, because the wire is the bottleneck either way.
    const int ReadFlashMaxInFlight = 16;
    const int RamBlock = 0x1800;
    const int RomFlashWriteSize = 0x400;
    const int StubFlashWriteSize = 0x4000;
    public const int RomBaud = 115200;

    // ---- registers -------------------------------------------------------
    const uint ChipDetectMagicReg = 0x40001000;
    const uint Esp8266MagicValue = 0xFFF0C101;
    const uint OtpMac0 = 0x3FF00050;
    const uint OtpMac1 = 0x3FF00054;
    const uint OtpMac3 = 0x3FF0005C;
    const uint SpiRegBase = 0x60000200;
    const uint SpiUsrOffs = 0x1C;
    const uint SpiUsr1Offs = 0x20;
    const uint SpiUsr2Offs = 0x24;
    const uint SpiW0Offs = 0x40;

    readonly SerialPort _port;
    readonly Queue<byte> _rx = new();
    readonly byte[] _readBuf = new byte[8192];
    readonly Action<string> _log;

    int _flashWriteSize = RomFlashWriteSize;

    public bool StubRunning { get; private set; }
    public string PortName => _port.PortName;

    public EspLoader(string portName, Action<string> log)
    {
        _log = log ?? (_ => { });
        _port = new SerialPort(portName, RomBaud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 100,
            WriteTimeout = 5000,
            // The stub streams flash reads ahead of our acks, so the driver
            // buffer has to hold more than one in-flight window (see
            // ReadFlashMaxInFlight) or bytes are dropped and the frame after
            // the gap looks like corruption.
            ReadBufferSize = 1 << 18,
            WriteBufferSize = 1 << 17,
        };
    }

    public void Open()
    {
        try
        {
            _port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            throw new EspException(
                $"{_port.PortName} 被别的程序占用了。桥接程序 AIClockBridge 会一直握着这个口，" +
                "本工具会自动停掉它；如果还是占用，请关掉串口助手 / PlatformIO 监视器再试。");
        }
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
    }

    // =====================================================================
    // SLIP transport
    // =====================================================================

    void WriteFrame(ReadOnlySpan<byte> packet)
    {
        var buf = new List<byte>(packet.Length + 16) { SlipEnd };
        foreach (var b in packet)
        {
            if (b == SlipEnd) { buf.Add(SlipEsc); buf.Add(SlipEscEnd); }
            else if (b == SlipEsc) { buf.Add(SlipEsc); buf.Add(SlipEscEsc); }
            else buf.Add(b);
        }
        buf.Add(SlipEnd);
        var arr = buf.ToArray();
        _port.Write(arr, 0, arr.Length);
    }

    /// Reads one SLIP frame. Bytes outside a frame are the chip's boot log and
    /// are dropped (they are what "wrong boot mode" looks like on the wire).
    byte[] ReadFrame(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var frame = new List<byte>(4096);
        bool inFrame = false;
        bool escaped = false;

        while (true)
        {
            if (_rx.Count == 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    throw new EspTimeoutException("等待设备回应超时。");
                _port.ReadTimeout = (int)Math.Clamp(left, 1, 250);
                int n;
                try { n = _port.Read(_readBuf, 0, _readBuf.Length); }
                catch (TimeoutException) { continue; }
                for (int i = 0; i < n; i++) _rx.Enqueue(_readBuf[i]);
                continue;
            }

            var b = _rx.Dequeue();

            if (!inFrame)
            {
                if (b == SlipEnd) { inFrame = true; frame.Clear(); escaped = false; }
                continue;
            }

            if (escaped)
            {
                frame.Add(b == SlipEscEnd ? SlipEnd : b == SlipEscEsc ? SlipEsc : b);
                escaped = false;
                continue;
            }

            if (b == SlipEsc) { escaped = true; continue; }

            if (b == SlipEnd)
            {
                if (frame.Count == 0) continue;   // back-to-back 0xC0, keep going
                return frame.ToArray();
            }

            frame.Add(b);
        }
    }

    public void FlushInput()
    {
        try { _port.DiscardInBuffer(); } catch { }
        _rx.Clear();
    }

    // =====================================================================
    // command layer
    // =====================================================================

    (uint val, byte[] data) Command(byte? op, byte[] data, uint checksum, int timeoutMs)
    {
        if (op.HasValue)
        {
            var pkt = new byte[8 + data.Length];
            pkt[0] = 0x00;
            pkt[1] = op.Value;
            BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(2), (ushort)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), checksum);
            data.CopyTo(pkt, 8);
            WriteFrame(pkt);
        }

        // Some ESP8266s answer a sync with more replies than were asked for,
        // so keep reading until the op matches instead of trusting the first.
        for (int retry = 0; retry < 100; retry++)
        {
            var p = ReadFrame(timeoutMs);
            if (p.Length < 8) continue;
            if (p[0] != 0x01) continue;
            var opRet = p[1];
            var val = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4));
            var payload = p[8..];
            if (!op.HasValue || opRet == op.Value) return (val, payload);
        }
        throw new EspException("设备回应与请求不匹配。");
    }

    byte[] CheckCommand(string what, byte op, byte[] data, uint checksum = 0, int timeoutMs = 3000)
    {
        var (val, payload) = Command(op, data, checksum, timeoutMs);
        if (payload.Length < StatusBytes)
            throw new EspException($"{what} 失败：设备回应过短。");
        var status = payload[^StatusBytes..];
        if (status[0] != 0)
            throw new EspCommandException(what, status[1]);
        if (payload.Length > StatusBytes)
            return payload[..^StatusBytes];
        return BitConverter.GetBytes(val);
    }

    static uint Checksum(ReadOnlySpan<byte> data)
    {
        byte state = ChecksumMagic;
        foreach (var b in data) state ^= b;
        return state;
    }

    static byte[] Pack(params uint[] words)
    {
        var buf = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4), words[i]);
        return buf;
    }

    // =====================================================================
    // reset & sync
    // =====================================================================

    /// The classic auto-reset wiggle for the two-transistor DTR/RTS circuit on
    /// CH340 boards: hold EN low, bring it up with IO0 held low, then let IO0
    /// go. The chip lands in serial download mode.
    void ClassicReset(int resetDelayMs)
    {
        _port.DtrEnable = false;   // IO0 = HIGH
        _port.RtsEnable = true;    // EN  = LOW  -> in reset
        Thread.Sleep(100);
        _port.DtrEnable = true;    // IO0 = LOW
        _port.RtsEnable = false;   // EN  = HIGH -> out of reset, IO0 still low
        Thread.Sleep(resetDelayMs);
        _port.DtrEnable = false;   // IO0 = HIGH, done
    }

    /// Pulse EN so the freshly written firmware starts running.
    public void HardReset()
    {
        _port.DtrEnable = false;
        _port.RtsEnable = true;
        Thread.Sleep(100);
        _port.RtsEnable = false;
    }

    void Sync()
    {
        var payload = new byte[36];
        payload[0] = 0x07; payload[1] = 0x07; payload[2] = 0x12; payload[3] = 0x20;
        for (int i = 4; i < payload.Length; i++) payload[i] = 0x55;
        Command(CmdSync, payload, 0, 100);
        for (int i = 0; i < 7; i++)
        {
            try { Command(null, Array.Empty<byte>(), 0, 100); }
            catch (EspTimeoutException) { break; }
        }
    }

    /// Reset into the bootloader and handshake. Mirrors esptool's Windows
    /// strategy: alternate a short and a long reset delay, seven attempts.
    public void Connect(CancellationToken cancel)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 7; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            FlushInput();
            ClassicReset(attempt % 2 == 0 ? 50 : 390);
            FlushInput();

            for (int i = 0; i < 5; i++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    FlushInput();
                    Sync();
                    _log($"已进入下载模式（第 {attempt + 1} 次尝试）");
                    FlushInput();
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    Thread.Sleep(50);
                }
            }
        }
        throw new EspException(
            "无法让设备进入下载模式。检查：USB 线是不是只能充电的那种、CH340 驱动是否已装、" +
            "串口是不是被别的程序占着。", last);
    }

    // =====================================================================
    // registers / identity
    // =====================================================================

    public uint ReadReg(uint addr)
    {
        var (val, data) = Command(CmdReadReg, Pack(addr), 0, 3000);
        if (data.Length > 0 && data[0] != 0)
            throw new EspException($"读取寄存器 0x{addr:X8} 失败。");
        return val;
    }

    void WriteReg(uint addr, uint value, uint mask = 0xFFFFFFFF, uint delayUs = 0)
        => CheckCommand("写寄存器", CmdWriteReg, Pack(addr, value, mask, delayUs));

    public void EnsureEsp8266()
    {
        var magic = ReadReg(ChipDetectMagicReg);
        if (magic != Esp8266MagicValue)
            throw new EspException(
                $"这个串口后面不是 ESP8266（芯片识别字为 0x{magic:X8}）。已中止，什么都没有写入。");
    }

    public string ReadMac()
    {
        var mac0 = ReadReg(OtpMac0);
        var mac1 = ReadReg(OtpMac1);
        var mac3 = ReadReg(OtpMac3);

        byte[] oui;
        if (mac3 != 0)
            oui = new[] { (byte)(mac3 >> 16), (byte)(mac3 >> 8), (byte)mac3 };
        else if (((mac1 >> 16) & 0xFF) == 0)
            oui = new byte[] { 0x18, 0xFE, 0x34 };
        else if (((mac1 >> 16) & 0xFF) == 1)
            oui = new byte[] { 0xAC, 0xD0, 0x74 };
        else
            return "未知";

        var mac = new[] { oui[0], oui[1], oui[2], (byte)(mac1 >> 8), (byte)mac1, (byte)(mac0 >> 24) };
        return string.Join(":", mac.Select(b => b.ToString("X2")));
    }

    /// SPI RDID through the chip's "user command" registers. Gives the flash
    /// manufacturer and the capacity code, which is how we prove the board
    /// really has the 4MB layout the firmware image is built for.
    public uint FlashId() => RunSpiFlashCommand(0x9F, Array.Empty<byte>(), 24);

    uint RunSpiFlashCommand(byte command, byte[] data, int readBits)
    {
        const uint UsrCommand = 1u << 31;
        const uint UsrMiso = 1u << 28;
        const uint UsrMosi = 1u << 27;
        const uint CmdUsr = 1u << 18;
        const int Usr2CommandLenShift = 28;
        const int MosiBitlenShift = 17;
        const int MisoBitlenShift = 8;

        uint cmdReg = SpiRegBase + 0x00;
        uint usrReg = SpiRegBase + SpiUsrOffs;
        uint usr1Reg = SpiRegBase + SpiUsr1Offs;
        uint usr2Reg = SpiRegBase + SpiUsr2Offs;
        uint w0Reg = SpiRegBase + SpiW0Offs;

        if (readBits > 32) throw new EspException("SPI 读取超过 32 位不受支持。");
        if (data.Length > 64) throw new EspException("SPI 写入超过 64 字节不受支持。");

        int dataBits = data.Length * 8;
        var oldUsr = ReadReg(usrReg);
        var oldUsr2 = ReadReg(usr2Reg);

        uint flags = UsrCommand;
        if (readBits > 0) flags |= UsrMiso;
        if (dataBits > 0) flags |= UsrMosi;

        uint mosiMask = dataBits == 0 ? 0 : (uint)(dataBits - 1);
        uint misoMask = readBits == 0 ? 0 : (uint)(readBits - 1);
        WriteReg(usr1Reg, (misoMask << MisoBitlenShift) | (mosiMask << MosiBitlenShift));

        WriteReg(usrReg, flags);
        WriteReg(usr2Reg, (7u << Usr2CommandLenShift) | command);

        if (dataBits == 0)
        {
            WriteReg(w0Reg, 0);
        }
        else
        {
            var padded = new byte[(data.Length + 3) / 4 * 4];
            data.CopyTo(padded, 0);
            for (int i = 0; i < padded.Length / 4; i++)
                WriteReg(w0Reg + (uint)(i * 4), BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i * 4)));
        }

        WriteReg(cmdReg, CmdUsr);

        bool done = false;
        for (int i = 0; i < 10 && !done; i++)
            done = (ReadReg(cmdReg) & CmdUsr) == 0;
        if (!done) throw new EspException("SPI 命令未在预期时间内完成。");

        var status = ReadReg(w0Reg);
        WriteReg(usrReg, oldUsr);
        WriteReg(usr2Reg, oldUsr2);
        return status;
    }

    /// Capacity code -> byte count, the same table esptool uses.
    public static long FlashSizeFromId(uint flashId)
    {
        var sizeId = (byte)((flashId >> 16) & 0xFF);
        return sizeId switch
        {
            0x12 or 0x32 => 256L * 1024,
            0x13 or 0x33 => 512L * 1024,
            0x14 or 0x34 => 1L * 1024 * 1024,
            0x15 or 0x35 => 2L * 1024 * 1024,
            0x16 or 0x36 => 4L * 1024 * 1024,
            0x17 or 0x37 => 8L * 1024 * 1024,
            0x18 or 0x38 => 16L * 1024 * 1024,
            0x19 or 0x39 => 32L * 1024 * 1024,
            0x1A or 0x3A or 0x20 => 64L * 1024 * 1024,
            0x1B or 0x21 => 128L * 1024 * 1024,
            _ => 0,
        };
    }

    public static string FlashVendorFromId(uint flashId) => (flashId & 0xFF) switch
    {
        0x20 => "XMC/Micron",
        0x5E => "Zbit",
        0x68 => "Boya",
        0x85 => "Puya",
        0xC8 => "GigaDevice",
        0xEB => "TH",
        0xEF => "Winbond",
        0x1C => "Eon",
        0xA1 => "Fudan",
        _ => $"0x{flashId & 0xFF:X2}",
    };

    // =====================================================================
    // stub loader
    // =====================================================================

    public void RunStub(CancellationToken cancel)
    {
        if (StubRunning) return;
        var stub = EspStub.Load();
        _log("上传 flasher stub…");

        foreach (var (blob, offset) in new[] { (stub.Text, stub.TextStart), (stub.Data, stub.DataStart) })
        {
            cancel.ThrowIfCancellationRequested();
            int blocks = (blob.Length + RamBlock - 1) / RamBlock;
            CheckCommand("进入内存下载模式", CmdMemBegin,
                Pack((uint)blob.Length, (uint)blocks, RamBlock, offset));
            for (int seq = 0; seq < blocks; seq++)
            {
                int from = seq * RamBlock;
                int len = Math.Min(RamBlock, blob.Length - from);
                var chunk = blob.AsSpan(from, len).ToArray();
                var payload = Pack((uint)len, (uint)seq, 0, 0).Concat(chunk).ToArray();
                CheckCommand("写入内存", CmdMemData, payload, Checksum(chunk));
            }
        }

        // The ROM may reset the UART before its TX FIFO drains, so a missing
        // reply to MEM_END is normal and must not abort the run.
        try { CheckCommand("离开内存下载模式", CmdMemEnd, Pack(0, stub.Entry), 0, 50); }
        catch (EspException) { }

        var hello = ReadFrame(3000);
        if (Encoding.ASCII.GetString(hello) != "OHAI")
            throw new EspException("stub 启动失败（设备没有回应 OHAI）。");

        StubRunning = true;
        _flashWriteSize = StubFlashWriteSize;
        _log("stub 已运行。");
    }

    public void ChangeBaud(int baud)
    {
        if (baud == _port.BaudRate) return;
        if (!StubRunning) throw new EspException("只有在 stub 运行后才能切换波特率。");
        _log($"波特率切换到 {baud}…");
        Command(CmdChangeBaud, Pack((uint)baud, (uint)_port.BaudRate), 0, 3000);
        _port.BaudRate = baud;
        Thread.Sleep(50);
        FlushInput();
    }

    // =====================================================================
    // flash read / write / verify
    // =====================================================================

    public byte[] ReadFlash(uint offset, int length, Action<int, int> progress, CancellationToken cancel)
    {
        if (!StubRunning) throw new EspException("读取 Flash 需要 stub（ESP8266 的 ROM 不支持读取）。");

        CheckCommand("开始读取 Flash", CmdReadFlash,
            Pack(offset, (uint)length, FlashSectorSize, ReadFlashMaxInFlight));

        var data = new byte[length];
        int got = 0;
        while (got < length)
        {
            cancel.ThrowIfCancellationRequested();
            var p = ReadFrame(10000);
            if (got + p.Length > length)
                throw new EspException("设备返回的数据超过预期长度。");
            p.CopyTo(data, got);
            got += p.Length;
            if (got < length && p.Length < FlashSectorSize)
                throw new EspException($"数据损坏：期望 {FlashSectorSize} 字节，收到 {p.Length} 字节。");
            WriteFrame(Pack((uint)got));
            progress?.Invoke(got, length);
        }

        var digestFrame = ReadFrame(5000);
        if (digestFrame.Length != 16)
            throw new EspException("读取结束时没有收到 MD5 摘要。");
        var digest = MD5.HashData(data);
        if (!digest.SequenceEqual(digestFrame))
            throw new EspException("读回的数据 MD5 与设备计算的不一致，备份不可信。");
        return data;
    }

    public void WriteFlash(uint offset, byte[] image, Action<int, int> progress, CancellationToken cancel)
    {
        int blockSize = _flashWriteSize;
        int blocks = (image.Length + blockSize - 1) / blockSize;
        uint eraseSize = StubRunning ? (uint)image.Length : RomEraseSize(offset, image.Length);

        _log($"擦除 0x{offset:X6} 起 {image.Length:N0} 字节，共 {blocks} 块（每块 {blockSize / 1024} KB）");
        CheckCommand("进入 Flash 下载模式", CmdFlashBegin,
            Pack(eraseSize, (uint)blocks, (uint)blockSize, offset), 0, 60000);

        for (int seq = 0; seq < blocks; seq++)
        {
            cancel.ThrowIfCancellationRequested();
            int from = seq * blockSize;
            int len = Math.Min(blockSize, image.Length - from);
            var block = new byte[blockSize];
            Array.Fill(block, (byte)0xFF);
            Array.Copy(image, from, block, 0, len);

            var payload = Pack((uint)block.Length, (uint)seq, 0, 0).Concat(block).ToArray();
            CheckCommand($"写入第 {seq} 块", CmdFlashData, payload, Checksum(block), 10000);
            progress?.Invoke(Math.Min(from + len, image.Length), image.Length);
        }
    }

    public void FlashFinish(bool reboot)
        => CheckCommand("离开 Flash 模式", CmdFlashEnd, Pack(reboot ? 0u : 1u));

    public string FlashMd5(uint offset, int length)
    {
        var res = CheckCommand("计算 Flash MD5", CmdSpiFlashMd5,
            Pack(offset, (uint)length, 0, 0), 0, 60000);
        if (res.Length == 32) return Encoding.ASCII.GetString(res).ToLowerInvariant();
        if (res.Length == 16) return Convert.ToHexString(res).ToLowerInvariant();
        throw new EspException($"MD5 命令返回了意外的 {res.Length} 字节。");
    }

    /// The ROM bootloader's erase-size quirk. Only used if the stub is not
    /// running; kept so the write path still works in that fallback.
    static uint RomEraseSize(uint offset, int size)
    {
        const int sectorsPerBlock = 16;
        int numSectors = (size + FlashSectorSize - 1) / FlashSectorSize;
        int startSector = (int)(offset / FlashSectorSize);
        int headSectors = sectorsPerBlock - (startSector % sectorsPerBlock);
        if (numSectors < headSectors) headSectors = numSectors;
        return numSectors < 2 * headSectors
            ? (uint)((numSectors + 1) / 2 * FlashSectorSize)
            : (uint)((numSectors - headSectors) * FlashSectorSize);
    }
}
