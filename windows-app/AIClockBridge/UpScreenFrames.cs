namespace AIClockBridge;

// Each row: (run length, RGB565 high byte, RGB565 low byte), then base64.
// Blank background rows are tiny; even a worst-case row fits serialLine[1600].
static class UpScreenFrames
{
    public const int Width = 240, Height = 240;
    public const int BurstBudget = 1400;
    public static byte[] EncodeRow(ReadOnlySpan<byte> row)
    {
        if (row.Length != Width * 2) throw new ArgumentException("Expected one 240px row");
        var runs = new List<byte>();
        for (var x = 0; x < Width;)
        {
            int length = 1, offset = x * 2;
            while (x + length < Width && length < 255 &&
                row[(x + length) * 2] == row[offset] && row[(x + length) * 2 + 1] == row[offset + 1]) length++;
            runs.Add((byte)length); runs.Add(row[offset]); runs.Add(row[offset + 1]);
            x += length;
        }
        return runs.ToArray();
    }

    public static byte[] DecodeRow(ReadOnlySpan<byte> runs)
    {
        if (runs.Length % 3 != 0) throw new InvalidDataException("Truncated RLE run");
        var row = new byte[Width * 2]; int x = 0;
        for (var i = 0; i < runs.Length; i += 3)
        {
            int count = runs[i];
            if (count == 0 || x + count > Width) throw new InvalidDataException("Invalid RLE length");
            for (var end = x + count; x < end; x++) { row[x * 2] = runs[i + 1]; row[x * 2 + 1] = runs[i + 2]; }
        }
        if (x != Width) throw new InvalidDataException("Incomplete RLE row");
        return row;
    }

    public static List<byte[]> Build(byte[] pixels, int revision)
    {
        if (pixels == null || pixels.Length != Width * Height * 2) return new();
        var frames = new List<byte[]> { System.Text.Encoding.ASCII.GetBytes($"#UPBEGIN {revision}") };
        for (var y = 0; y < Height; y++)
            frames.Add(System.Text.Encoding.ASCII.GetBytes($"#UPROW {revision} {y} " +
                Convert.ToBase64String(EncodeRow(pixels.AsSpan(y * Width * 2, Width * 2)))));
        frames.Add(System.Text.Encoding.ASCII.GetBytes($"#UPEND {revision}"));
        return frames;
    }
}
