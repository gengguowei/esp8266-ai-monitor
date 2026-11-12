using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIClockBridge;

static class ClaudeHooks
{
    // Forward only lifecycle metadata, never prompts, tool inputs or transcripts.
    internal const string Script = """
        try {
            $eventData = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $payload = @{ agent = 'grok'; event = $eventData.hook_event_name; session_id = $eventData.session_id }
            if ($eventData.notification_type -eq 'permission_prompt') { $payload.message = 'permission' }
            if ($eventData.notification_type -eq 'idle_prompt') { $payload.message = 'idle_prompt' }
            $json = $payload | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri 'http://127.0.0.1:8765/event' -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 2 | Out-Null
        } catch { }
        exit 0
        """;

    internal static readonly string[] Events = { "SessionStart", "SessionEnd", "UserPromptSubmit",
        "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "StopFailure",
        "PermissionRequest", "Notification", "Elicitation", "ElicitationResult" };

    static readonly JsonDocumentOptions SettingsOptions =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    static string ConfigDir()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir)) configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.GetFullPath(configDir);
    }

    static string ScriptPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIClockBridge", "claude-status-hook.ps1");

    internal static string Command(string scriptPath) =>
        "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";

    public static string Install()
    {
        var configDir = ConfigDir();
        var path = Path.Combine(configDir, "settings.json");
        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        var root = original == null ? new JsonObject()
            : JsonNode.Parse(original, documentOptions: SettingsOptions) as JsonObject;
        if (root == null) throw new InvalidDataException("Claude settings.json 不是 JSON 对象");
        var scriptPath = ScriptPath();
        var scriptDir = Path.GetDirectoryName(scriptPath);
        var command = Command(scriptPath);
        Merge(root, command);
        Directory.CreateDirectory(scriptDir);
        Directory.CreateDirectory(configDir);
        File.WriteAllText(scriptPath, Script, new UTF8Encoding(false));
        if (original != null) File.Copy(path, path + ".aiclock-" + Guid.NewGuid().ToString("N") + ".bak");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if ((File.Exists(path) ? File.ReadAllText(path) : null) != original)
                throw new IOException("Claude 设置刚被其他程序修改，请重试");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }

    /// Startup upgrade of the hook script. Install() is a menu action, so after
    /// an upgrade the old script kept running until someone clicked it again
    /// (2026-11-02: the 10-30 script still lacked idle_prompt). Only an
    /// installation this bridge made is touched - the script must exist and
    /// settings.json must still register it - and settings.json itself is never
    /// written. Returns null when nothing changed, else a line for the log.
    public static string RefreshScript() =>
        RefreshScript(ScriptPath(), Path.Combine(ConfigDir(), "settings.json"));

    internal static string RefreshScript(string scriptPath, string settingsPath)
    {
        try
        {
            if (!File.Exists(scriptPath) || File.ReadAllText(scriptPath) == Script) return null;
            if (!File.Exists(settingsPath)
                || !IsRegistered(File.ReadAllText(settingsPath), Command(scriptPath))) return null;
            // A hook may be reading the script right now: write aside, then swap.
            var temp = scriptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, Script, new UTF8Encoding(false));
                File.Move(temp, scriptPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return "updated " + scriptPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "update failed, will retry next start: " + ex.Message;
        }
    }

    internal static bool IsRegistered(string settingsJson, string command) =>
        JsonNode.Parse(settingsJson, documentOptions: SettingsOptions)?["hooks"] is JsonObject hooks
        && hooks.Select(p => p.Value).OfType<JsonArray>()
            .SelectMany(entries => entries.OfType<JsonObject>())
            .Select(entry => entry["hooks"]).OfType<JsonArray>()
            .SelectMany(handlers => handlers.OfType<JsonObject>())
            .Any(h => h["command"]?.ToString() == command);

    internal static void Merge(JsonObject root, string command)
    {
        if (root["hooks"] == null) root["hooks"] = new JsonObject();
        if (root["hooks"] is not JsonObject hooks) throw new InvalidDataException("hooks 格式无效");
        foreach (var ev in Events)
        {
            if (hooks[ev] == null) hooks[ev] = new JsonArray();
            if (hooks[ev] is not JsonArray entries) throw new InvalidDataException(ev + " hooks 格式无效");
            if (entries.OfType<JsonObject>().Any(entry => entry["hooks"] is JsonArray handlers
                && handlers.OfType<JsonObject>().Any(h => h["command"]?.ToString() == command))) continue;
            entries.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject {
                ["type"] = "command", ["command"] = command, ["timeout"] = 3 }) });
        }
    }
}
