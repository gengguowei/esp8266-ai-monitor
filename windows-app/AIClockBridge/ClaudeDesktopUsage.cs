using System.Text.Json;

namespace AIClockBridge;

// Read-only: Desktop owns this internal cache, including authentication and polling.
static class ClaudeDesktopUsage
{
    const string CacheFileName = "plan-usage-history.json";

    /// Candidate locations of the cache, most likely first.
    ///
    /// Current Desktop versions store their data in Claude-Data; older MSIX
    /// versions used the package's LocalCache\Roaming\Claude directory.
    ///
    /// The package folder is enumerated rather than hardcoded: the trailing hash
    /// in "Claude_pzs8sxrjxfjjc" is derived from the signing certificate's
    /// subject, so a certificate rotation or a package rename would break a
    /// literal path *silently* - the quota would just sit at "-" with nothing in
    /// the logs to point at. Read() already picks the newest sample across every
    /// candidate, so matching more than one folder is harmless.
    public static IEnumerable<string> Paths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0)
        {
            foreach (var data in Directories(Path.Combine(local, "Claude-Data"), "claude_*"))
                yield return Path.Combine(data, CacheFileName);
            foreach (var package in Directories(Path.Combine(local, "Packages"), "*Claude*"))
                yield return Path.Combine(package, "LocalCache", "Roaming", "Claude", CacheFileName);
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (roaming.Length > 0)
            yield return Path.Combine(roaming, "Claude", CacheFileName);
    }

    static string[] Directories(string root, string pattern)
    {
        try { return Directory.GetDirectories(root, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException) { return Array.Empty<string>(); }
    }

    public static ProviderUsage Read()
    {
        ProviderUsage latest = null;
        foreach (var path in Paths())
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 8 * 1024 * 1024) continue;
                using var doc = JsonDocument.Parse(stream);
                var usage = Parse(doc.RootElement, DateTime.UtcNow);
                if (usage != null && (latest == null || usage.FetchedAt > latest.FetchedAt)) latest = usage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return latest;
    }

    internal static ProviderUsage Parse(JsonElement root, DateTime now)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v)
            || (v != 1 && v != 2)
            || !root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
            return null;
        ProviderUsage latest = null;
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object || !sample.TryGetProperty("t", out var t)
                || t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var ms)) continue;
            DateTime at;
            try { at = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { continue; }
            if (at > now.AddMinutes(5)) continue;
            var values = sample;
            if (v == 2 && (!sample.TryGetProperty("u", out values) || values.ValueKind != JsonValueKind.Object)) continue;
            // Choose the newest sample, never combine windows from different organizations.
            if (latest != null && at <= latest.FetchedAt) continue;
            latest = new ProviderUsage
            {
                PrimaryPct = Percent(values, "fh"), WeeklyPct = Percent(values, "sd"),
                FetchedAt = at, DesktopCache = true,
            };
            if (latest.PrimaryPct == null && latest.WeeklyPct == null)
                latest.Error = "桌面额度采样中没有可用的 5h/7天数据";
        }
        return latest;
    }

    static double? Percent(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 && n <= 100 ? n : null;
}
