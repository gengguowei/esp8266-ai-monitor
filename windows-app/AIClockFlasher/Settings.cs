using System.Text.Json;

namespace AIClockFlasher;

/// A handful of remembered choices in %APPDATA%\AIClockFlasher\settings.json.
/// Deliberately separate from the bridge's own settings file - this tool must
/// never write into anything AIClockBridge reads.
sealed class Settings
{
    public string LastPort { get; set; }
    public string LastFirmware { get; set; }
    public int Baud { get; set; } = 460800;
    public bool AutoConnect { get; set; } = true;
    public bool AutoBackup { get; set; } = true;
    public bool Verify { get; set; } = true;
    public string BackupScope { get; set; } = nameof(AIClockFlasher.BackupScope.Application);
    public string BackupDirectory { get; set; }

    static string PathOnDisk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIClockFlasher", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOnDisk)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(PathOnDisk,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
