namespace AIClockBridge;

// Wire format for pushing the CJK stock-name strips down the USB serial link.
//
// The device normally pulls these from /stock/names.raw over HTTP, which needs
// WiFi - so a USB-only device showed stock codes with the names permanently
// missing, and nothing anywhere said why. Over serial the exchange is:
//
//   device -> bridge:  #WANTNAMES <rev>
//   bridge -> device:  #NAMES <rev> <count>
//                      #NAMEROW <rev> <strip> <row> <base64 of one pixel row>
//                      ... one per pixel row ...
//
// One PIXEL ROW per frame is the whole trick. The serial protocol splits on
// newline into a fixed char serialLine[1600] on the device, so a frame has to
// stay well under that - and a raw strip (156 x 16 x 2 = 4992 bytes) does not.
// A single row is 156 * 2 = 312 bytes, 416 base64 chars, ~440 with the prefix.
// It also means the device decodes straight into the same 312-byte rowBuf it
// already blits from on the HTTP path: no 15KB buffer on a chip that has no
// room for one.
//
// 312 is divisible by 3, so base64 never pads and every row frame is identical
// in length.
static class StockNameFrames
{
    /// Firmware `char serialLine[1600]`; anything longer is dropped on the
    /// floor by pumpSerial(). Left with real headroom on purpose.
    public const int MaxFrameLength = 1500;

    /// <param name="data">[1 byte strip count][strip rows...], as served by /stock/names.raw</param>
    /// Returns an empty list when there is nothing complete to send - a render
    /// still in flight leaves a short blob, and sending half of it would paint
    /// garbage rows onto the screen.
    public static List<string> Build(byte[] data, int rev, int width, int height)
    {
        var frames = new List<string>();
        if (data == null || data.Length < 1) return frames;

        int count = data[0];
        var rowBytes = width * 2;
        var expected = 1 + count * height * rowBytes;
        if (count <= 0 || data.Length < expected) return frames;

        frames.Add($"#NAMES {rev} {count}");
        for (var strip = 0; strip < count; strip++)
        {
            for (var row = 0; row < height; row++)
            {
                var offset = 1 + (strip * height + row) * rowBytes;
                frames.Add($"#NAMEROW {rev} {strip} {row} "
                           + Convert.ToBase64String(data, offset, rowBytes));
            }
        }
        return frames;
    }
}
