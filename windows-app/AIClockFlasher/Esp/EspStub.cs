using System.Reflection;
using System.Text.Json;

namespace AIClockFlasher.Esp;

/// The ESP8266 flasher stub: a small program uploaded into the chip's IRAM
/// that replaces the ROM bootloader's command handler with a much faster one.
///
/// It is not optional here. The ESP8266 ROM has no flash *read* command at all
/// (ESP_READ_FLASH_SLOW exists only on ESP32), so "back up the device before
/// overwriting it" is impossible without the stub. It also lifts the write
/// block from 1 KB to 16 KB, which is most of the difference between a 20
/// second flash and a two minute one.
///
/// The blob is Espressif's own, taken verbatim from esptool-js
/// (lib/targets/stub_flasher/stub_flasher_8266.json, Apache-2.0) - byte for
/// byte the same image esptool.py uploads. See Assets/STUB-LICENSE.txt.
sealed class EspStub
{
    public byte[] Text { get; }
    public byte[] Data { get; }
    public uint TextStart { get; }
    public uint DataStart { get; }
    public uint Entry { get; }

    EspStub(byte[] text, byte[] data, uint textStart, uint dataStart, uint entry)
    {
        Text = text;
        Data = data;
        TextStart = textStart;
        DataStart = dataStart;
        Entry = entry;
    }

    static EspStub _cached;

    public static EspStub Load()
    {
        if (_cached != null) return _cached;

        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("stub_flasher_8266.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new EspException("内置的 ESP8266 stub 资源丢失，程序不完整。");

        using var stream = assembly.GetManifestResourceStream(name);
        using var doc = JsonDocument.Parse(stream!);
        var root = doc.RootElement;

        _cached = new EspStub(
            Convert.FromBase64String(root.GetProperty("text").GetString()!),
            Convert.FromBase64String(root.GetProperty("data").GetString()!),
            root.GetProperty("text_start").GetUInt32(),
            root.GetProperty("data_start").GetUInt32(),
            root.GetProperty("entry").GetUInt32());
        return _cached;
    }
}
