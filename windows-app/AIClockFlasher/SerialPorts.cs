using System.IO.Ports;
using Microsoft.Win32;

namespace AIClockFlasher;

sealed class PortEntry
{
    public string Name;          // COM5
    public string Description;   // USB-SERIAL CH340
    public bool IsKnownBoard;    // CH340 / CP210x - the chips this board ships with

    public override string ToString()
        => string.IsNullOrEmpty(Description) ? Name : $"{Name} - {Description}";
}

/// Enumerates serial ports with friendly names, and marks the ones that are
/// the USB-serial bridge this board actually uses. Registry-only: no
/// System.Management dependency, same approach the bridge already takes.
static class SerialPorts
{
    // CH340 is what the SD2 board ships with; CP210x shows up on compatible builds.
    static readonly string[] BoardVidPid = { "VID_1A86&PID_7523", "VID_1A86&PID_55D4", "VID_10C4&PID_EA60" };

    public static List<PortEntry> List()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var known = UsbPorts();
        var result = new List<PortEntry>();

        foreach (var name in present.OrderBy(PortNumber))
        {
            known.TryGetValue(name, out var description);
            result.Add(new PortEntry
            {
                Name = name,
                Description = description,
                IsKnownBoard = description != null,
            });
        }

        // Board ports first: with a hub full of adapters, the right one should
        // be preselected rather than merely present in the list.
        return result.OrderByDescending(p => p.IsKnownBoard).ThenBy(p => PortNumber(p.Name)).ToList();
    }

    static Dictionary<string, string> UsbPorts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return map;
            foreach (var deviceName in usb.GetSubKeyNames())
            {
                if (!BoardVidPid.Any(id => deviceName.StartsWith(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                using var device = usb.OpenSubKey(deviceName);
                if (device == null) continue;
                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var instance = device.OpenSubKey(instanceName);
                    using var parameters = instance?.OpenSubKey("Device Parameters");
                    if (parameters?.GetValue("PortName") is not string port ||
                        !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;

                    var friendly = instance?.GetValue("FriendlyName") as string
                                   ?? instance?.GetValue("DeviceDesc") as string
                                   ?? "USB 串口";
                    // "@oem12.inf,%ch340%;USB-SERIAL CH340 (COM5)" -> "USB-SERIAL CH340"
                    var semi = friendly.LastIndexOf(';');
                    if (semi >= 0) friendly = friendly[(semi + 1)..];
                    var paren = friendly.LastIndexOf(" (COM", StringComparison.OrdinalIgnoreCase);
                    if (paren >= 0) friendly = friendly[..paren];
                    map[port] = friendly.Trim();
                }
            }
        }
        catch
        {
            // Restricted registry: the plain port list still works, we just
            // lose the friendly names and the preferred ordering.
        }
        return map;
    }

    static int PortNumber(string name)
        => int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue;
}
