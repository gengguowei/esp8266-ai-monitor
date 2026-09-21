using System.Text;

namespace AIClockBridge;

// The app ships as a WinExe, so it has no console: every Console.Error.WriteLine
// used to be written to an invalid handle and silently discarded. That made
// intermittent faults unfalsifiable - a stalled quota looked exactly like a
// working one that had not changed, with nothing anywhere to say which.
//
// Rolling text log under %APPDATA%\AIClockBridge\logs\. Deliberately dumb: no
// dependencies, no async, and every failure swallowed - logging must never be
// the reason the bridge stops working.
static class Log
{
    const long MaxBytes = 1_000_000;

    static readonly object Lock = new();
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIClockBridge", "logs");
    static readonly string Current = Path.Combine(Dir, "bridge.log");
    static readonly string Previous = Path.Combine(Dir, "bridge.1.log");

    public static string Folder => Dir;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        // Still useful under `dotnet run`, where a console does exist.
        try { Console.Error.WriteLine(line); } catch { }
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                RollIfNeeded();
                File.AppendAllText(Current, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Disk full, permissions, file locked by an editor - never throw.
            }
        }
    }

    /// Two generations is enough to cover "it broke while I was asleep" without
    /// letting the folder grow without bound.
    static void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(Current);
            if (!info.Exists || info.Length < MaxBytes) return;
            if (File.Exists(Previous)) File.Delete(Previous);
            File.Move(Current, Previous);
        }
        catch { }
    }
}
