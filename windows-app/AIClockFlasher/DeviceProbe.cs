using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace AIClockFlasher;

/// What the currently installed firmware says about itself.
sealed class RunningFirmware
{
    public string Version;
    public string Name;
    public string Mode;
    public int Brightness = -1;
    public bool Wired;
    public string Bridge;
}

/// Asks the running firmware for its version before anything is overwritten.
///
/// The device already speaks a line protocol on the same UART the bridge uses:
/// send "#HELLO", get back "#DEVICE {json}" with a "fw" field. That is a much
/// better answer to "what is on the device right now" than guessing from the
/// last file that was flashed - and it is read-only, so it is safe to do on
/// every connect.
static class DeviceProbe
{
    /// The default window is generous on purpose: opening the port can pulse
    /// DTR/RTS on some CH340 drivers, which reboots the device, and the
    /// firmware's setup() (TFT init, WiFiManager autoConnect) runs for several
    /// seconds before it starts reading the UART. Giving up early would show
    /// "unknown" for a device that was about to answer.
    public static RunningFirmware Query(string portName, int timeoutMs = 9000)
    {
        using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,     // leave EN/IO0 alone: do not reset the device
            RtsEnable = false,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            NewLine = "\n",
        };

        port.Open();
        port.DiscardInBuffer();

        var deadline = Environment.TickCount64 + timeoutMs;
        var buffer = new StringBuilder();
        var lastHello = 0L;

        while (Environment.TickCount64 < deadline)
        {
            if (Environment.TickCount64 - lastHello > 800)
            {
                lastHello = Environment.TickCount64;
                try { port.Write("#HELLO\n"); } catch { }
            }

            try
            {
                buffer.Append(port.ReadExisting());
            }
            catch (TimeoutException) { }

            var text = buffer.ToString();
            int nl;
            while ((nl = text.IndexOf('\n')) >= 0)
            {
                var line = text[..nl].Trim();
                text = text[(nl + 1)..];
                var parsed = TryParse(line);
                if (parsed != null) return parsed;
            }
            buffer.Clear();
            buffer.Append(text);

            Thread.Sleep(50);
        }

        return null;
    }

    static RunningFirmware TryParse(string line)
    {
        if (!line.StartsWith("#DEVICE ", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line[8..]);
            var root = doc.RootElement;
            return new RunningFirmware
            {
                Version = Str(root, "fw"),
                Name = Str(root, "name"),
                Mode = Str(root, "mode"),
                Bridge = Str(root, "bridge"),
                Brightness = root.TryGetProperty("brightness", out var b) && b.TryGetInt32(out var bi) ? bi : -1,
                Wired = root.TryGetProperty("wired", out var w) && w.ValueKind == JsonValueKind.True,
            };
        }
        catch
        {
            return null;
        }
    }

    static string Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
