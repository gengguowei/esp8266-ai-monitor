using Microsoft.Win32;

namespace AIClockBridge;

// Start-at-logon through HKCU\...\CurrentVersion\Run: per-user, no admin rights,
// no UAC, and Registry is already a dependency here (SerialLink reads HKLM to
// enumerate CH340 ports).
//
// The registry is the single source of truth - deliberately nothing mirrored
// into settings.json, because the user can also flip this from Task Manager's
// "Startup apps" tab and two copies would drift apart within a week.
//
// The Task Manager subtlety: disabling an entry there does NOT delete the Run
// value, it writes a flag blob under StartupApproved\Run. Reading only the Run
// value would leave the tray checkbox claiming "on" while Windows silently skips
// us, so IsEnabled() consults both.
static class AutoStart
{
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "AIClockBridge";

    /// Marks a launch as coming from the autostart entry. The duplicate-instance
    /// notice stays silent for it, so a stray double-start at logon can never pop
    /// a dialog in front of a user who is not looking yet.
    public const string AutoStartFlag = "--autostart";

    // MARK: - pure helpers (unit-tested; these touch no registry and no disk)

    /// Always quoted - releases live under a versioned folder and the path may
    /// contain spaces.
    internal static string FormatCommand(string exePath) => $"\"{exePath}\" {AutoStartFlag}";

    /// First token of a Run command line, quotes stripped.
    internal static string ExtractExePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var s = command.Trim();
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end < 0 ? s[1..] : s[1..end];
        }
        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// Environment.ProcessPath is already absolute and canonical, so trimming
    /// plus a case-insensitive compare is enough - and keeps this helper free of
    /// filesystem access so it stays unit-testable.
    internal static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// StartupApproved blob: bit 0 of the first byte is the "disabled" flag
    /// (0x02/0x06 enabled, 0x03/0x07 disabled). A missing value means enabled.
    internal static bool ApprovedByWindows(byte[] blob) =>
        blob == null || blob.Length == 0 || (blob[0] & 1) == 0;

    /// Under `dotnet run` the process path is the muxer; registering that would
    /// launch a bare dotnet.exe at logon and do nothing useful.
    internal static bool IsRegistrablePath(string exePath) =>
        !string.IsNullOrWhiteSpace(exePath)
        && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFileName(exePath), "dotnet.exe",
            StringComparison.OrdinalIgnoreCase);

    // MARK: - registry state

    /// Correct under PublishSingleFile, where Assembly.Location returns "".
    public static string ExePath => Environment.ProcessPath ?? "";

    static string ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return key?.GetValue(ValueName) as string ?? "";
        }
        catch { return ""; }
    }

    static byte[] ReadApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            return key?.GetValue(ValueName) as byte[];
        }
        catch { return null; }
    }

    public static bool IsEnabled() =>
        ReadCommand().Length > 0 && ApprovedByWindows(ReadApproval());

    /// Windows treats a missing approval value as enabled, so deleting it is how
    /// a Task Manager "disable" gets undone.
    static void ClearApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }

    public static void Enable()
    {
        var exe = ExePath;
        if (!IsRegistrablePath(exe))
            throw new InvalidOperationException(
                $"当前进程不是从 exe 启动的（{exe}），无法设置开机自启。\n"
                + "请改用发布出来的 AIClockBridge.exe 运行。");
        using (var key = Registry.CurrentUser.CreateSubKey(RunPath))
            key.SetValue(ValueName, FormatCommand(exe), RegistryValueKind.String);
        ClearApproval();
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best effort */ }
        ClearApproval();   // leave no stale "disabled" flag behind for next time
    }

    /// Releases live in versioned folders (dist\AIClockBridge-<ver>-Windows-x64),
    /// so upgrading moves the exe and leaves the Run entry pointing at a path
    /// that no longer exists. Called once at startup: if an entry exists but
    /// names some other location, repoint it at this build.
    public static void SelfHeal()
    {
        var command = ReadCommand();
        if (command.Length == 0) return;             // not enabled, nothing to fix
        var exe = ExePath;
        if (!IsRegistrablePath(exe)) return;         // dev run: do not clobber it
        if (SamePath(ExtractExePath(command), exe)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath);
            key.SetValue(ValueName, FormatCommand(exe), RegistryValueKind.String);
            Log.Write($"[autostart] entry repointed to {exe}");
        }
        catch (Exception e)
        {
            Log.Write($"[autostart] could not repoint entry: {e.Message}");
        }
    }

    /// The README used to tell people to drop a shortcut into shell:startup by
    /// hand. One of those left next to a Run entry starts the bridge twice, so
    /// the tray offers to clear them out when the toggle is switched on.
    public static string[] LegacyStartupShortcuts()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (dir.Length == 0 || !Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*AIClockBridge*.lnk")
                .Concat(Directory.GetFiles(dir, "*AI Clock*.lnk"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
