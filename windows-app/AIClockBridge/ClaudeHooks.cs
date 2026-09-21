using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIClockBridge;

static class ClaudeHooks
{
    // Forward only lifecycle metadata, never prompts, tool inputs or transcripts.
    const string Script = """
        try {
            $eventData = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $payload = @{ agent = 'claude'; event = $eventData.hook_event_name; session_id = $eventData.session_id }
            if ($eventData.notification_type -eq 'permission_prompt') { $payload.message = 'permission' }
            $json = $payload | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri 'http://127.0.0.1:8765/event' -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 2 | Out-Null
        } catch { }
        exit 0
        """;

    internal static readonly string[] Events = { "SessionStart", "SessionEnd", "UserPromptSubmit",
        "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "StopFailure",
        "PermissionRequest", "Notification", "Elicitation", "ElicitationResult" };

    public static string Install()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir)) configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        configDir = Path.GetFullPath(configDir);
        var path = Path.Combine(configDir, "settings.json");
        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        var root = original == null ? new JsonObject() : JsonNode.Parse(original,
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        if (root == null) throw new InvalidDataException("Claude settings.json 不是 JSON 对象");
        var scriptDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIClockBridge");
        var scriptPath = Path.Combine(scriptDir, "claude-status-hook.ps1");
        var command = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
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
