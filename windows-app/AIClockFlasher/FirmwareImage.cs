using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AIClockFlasher;

/// Everything the tool can learn about a .bin file without a device attached:
/// the ESP8266 image header, the firmware version, and a hash to show next to
/// the one the chip reports back after the write.
sealed class FirmwareImage
{
    public string Path { get; private init; }
    public byte[] Data { get; private init; }
    public DateTime BuiltAt { get; private init; }

    public byte SegmentCount { get; private init; }
    public uint Entry { get; private init; }
    public string FlashMode { get; private init; }
    public string FlashSize { get; private init; }
    public long FlashSizeBytes { get; private init; }
    public string FlashFreq { get; private init; }

    /// Version parsed out of the image. The firmware draws "Firmware v0.4.14"
    /// on the setup screen, so that literal is in every build and is a more
    /// trustworthy source than the file name (which a rename can lie about).
    public string Version { get; private init; }
    public string VersionSource { get; private init; }

    public string Sha256 { get; private init; }
    public string Md5 { get; private init; }
    public int Length => Data.Length;
    public string FileName => System.IO.Path.GetFileName(Path);

    static readonly Regex VersionInBinary = new(@"Firmware v(\d+\.\d+\.\d+)", RegexOptions.Compiled);
    static readonly Regex VersionInName = new(@"(\d+\.\d+\.\d+)", RegexOptions.Compiled);

    public static FirmwareImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8)
            throw new InvalidDataException("文件太小，不是固件镜像。");
        if (data[0] != 0xE9)
            throw new InvalidDataException(
                $"不是 ESP8266 固件镜像（首字节应为 0xE9，实际是 0x{data[0]:X2}）。" +
                "选到备份文件或者别的 bin 了？");

        var sizeFreq = data[3];
        var (sizeName, sizeBytes) = (sizeFreq >> 4) switch
        {
            0x0 => ("512KB", 512L * 1024),
            0x1 => ("256KB", 256L * 1024),
            0x2 => ("1MB", 1L * 1024 * 1024),
            0x3 => ("2MB", 2L * 1024 * 1024),
            0x4 => ("4MB", 4L * 1024 * 1024),
            0x5 => ("2MB-c1", 2L * 1024 * 1024),
            0x6 => ("4MB-c1", 4L * 1024 * 1024),
            0x8 => ("8MB", 8L * 1024 * 1024),
            0x9 => ("16MB", 16L * 1024 * 1024),
            _ => ($"未知(0x{sizeFreq >> 4:X})", 0L),
        };

        var version = FindVersion(data, path, out var source);

        return new FirmwareImage
        {
            Path = System.IO.Path.GetFullPath(path),
            Data = data,
            BuiltAt = File.GetLastWriteTime(path),
            SegmentCount = data[1],
            Entry = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)),
            FlashMode = data[2] switch
            {
                0 => "QIO", 1 => "QOUT", 2 => "DIO", 3 => "DOUT", _ => $"0x{data[2]:X2}"
            },
            FlashSize = sizeName,
            FlashSizeBytes = sizeBytes,
            FlashFreq = (sizeFreq & 0x0F) switch
            {
                0x0 => "40MHz", 0x1 => "26MHz", 0x2 => "20MHz", 0xF => "80MHz",
                _ => $"0x{sizeFreq & 0x0F:X}"
            },
            Version = version,
            VersionSource = source,
            Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            Md5 = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(),
        };
    }

    static string FindVersion(byte[] data, string path, out string source)
    {
        // Scanning ~840 KB for one short ASCII marker is a few milliseconds,
        // and it is the only way to know the version of a .bin someone dropped
        // in with an arbitrary name.
        var text = Encoding.ASCII.GetString(data);
        var m = VersionInBinary.Match(text);
        if (m.Success)
        {
            source = "镜像内嵌";
            return m.Groups[1].Value;
        }

        var n = VersionInName.Match(System.IO.Path.GetFileNameWithoutExtension(path));
        if (n.Success)
        {
            source = "文件名推断";
            return n.Groups[1].Value;
        }

        source = "未知";
        return null;
    }

    /// Where firmware normally shows up in this repo, newest first. Saves the
    /// user from a file dialog in the common case.
    public static List<string> Discover(string repoRoot)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(repoRoot)) return found;

        var built = System.IO.Path.Combine(repoRoot, @"firmware\.pio\build\nodemcuv2\firmware.bin");
        if (File.Exists(built)) found.Add(built);

        var dist = System.IO.Path.Combine(repoRoot, "dist");
        if (Directory.Exists(dist))
        {
            found.AddRange(Directory.GetFiles(dist, "firmware-*.bin")
                .OrderByDescending(File.GetLastWriteTime));
        }

        return found;
    }

    /// Walks up from the .exe looking for the repo, so the tool finds
    /// dist\firmware-*.bin whether it runs from bin\Release or from dist\.
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "firmware")) &&
                Directory.Exists(System.IO.Path.Combine(dir.FullName, "windows-app")))
                return dir.FullName;
        }
        return null;
    }
}
