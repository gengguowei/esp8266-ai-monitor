using System.Text;
using System.Text.Json;

namespace AIClockBridge;

// Port of the Mac StatusReader. No account APIs / keys are touched -
// everything comes from the JSONL session logs Claude Code and Codex CLI
// already write to disk (same paths on Windows, under %USERPROFILE%):
//   ~/.claude/projects/**/*.jsonl   (Claude Code transcripts)
//   ~/.codex/sessions/**/*.jsonl    (Codex CLI rollouts, incl. rate_limits)

class ClaudeStatus
{
    public string Status = "offline";
    public int TokensToday;
    public int SessionMin;
    public int SessionWindowMin = 300;
    public double? FiveHourPct;
    public int? FiveHourResetMin;
    public double? SevenDayPct;
    public int? SevenDayResetMin;
    public bool NeedsInput; // waiting on a permission/approval prompt
}

class CodexStatus
{
    public string Status = "offline";
    public int TokensToday;
    public double? PrimaryPct;
    public int? PrimaryWindowMin;
    public int? PrimaryResetMin;
    public double? WeeklyPct;
    public int? WeeklyWindowMin;
    public int? WeeklyResetMin;
    public bool NeedsInput;
}

class StatusSnapshot
{
    public ClaudeStatus Claude = new();
    public CodexStatus Codex = new();
    public long Ts;
    public bool ScreenOn = true;

    /// Serializes to the exact JSON shape the firmware's parseStatusJson expects.
    public byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("ts", Ts);
            // Whether the Beijing date of `ts` is an A-share trading day
            // (weekends + exchange holidays). Firmware 0.4.20+ uses it for
            // the auto stock page; older firmware ignores the field.
            w.WriteBoolean("cn_trading_day", ChinaMarketCalendar.IsTradingDay(Ts));
            w.WriteBoolean("screen_on", ScreenOn);
            w.WriteStartObject("claude");
            w.WriteString("status", Claude.Status);
            w.WriteNumber("tokens_today", Claude.TokensToday);
            w.WriteNumber("session_min", Claude.SessionMin);
            w.WriteNumber("session_window_min", Claude.SessionWindowMin);
            WriteNullable(w, "five_hour_pct", Claude.FiveHourPct);
            WriteNullable(w, "five_hour_reset_min", Claude.FiveHourResetMin);
            WriteNullable(w, "seven_day_pct", Claude.SevenDayPct);
            WriteNullable(w, "seven_day_reset_min", Claude.SevenDayResetMin);
            w.WriteBoolean("needs_input", Claude.NeedsInput);
            w.WriteEndObject();
            w.WriteStartObject("codex");
            w.WriteString("status", Codex.Status);
            w.WriteNumber("tokens_today", Codex.TokensToday);
            WriteNullable(w, "primary_pct", Codex.PrimaryPct);
            WriteNullable(w, "primary_window_min", Codex.PrimaryWindowMin);
            WriteNullable(w, "primary_reset_min", Codex.PrimaryResetMin);
            WriteNullable(w, "weekly_pct", Codex.WeeklyPct);
            WriteNullable(w, "weekly_window_min", Codex.WeeklyWindowMin);
            WriteNullable(w, "weekly_reset_min", Codex.WeeklyResetMin);
            w.WriteBoolean("needs_input", Codex.NeedsInput);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static void WriteNullable(Utf8JsonWriter w, string name, double? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value); else w.WriteNull(name);
    }

    static void WriteNullable(Utf8JsonWriter w, string name, int? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value); else w.WriteNull(name);
    }

    StatusSnapshot() { }

    public StatusSnapshot(ClaudeStatus claude, CodexStatus codex, long ts)
    {
        Claude = claude;
        Codex = codex;
        Ts = ts;
    }

    public StatusSnapshot Clone()
    {
        return new StatusSnapshot
        {
            Claude = (ClaudeStatus)Claude.MemberwiseCloneOf(),
            Codex = (CodexStatus)Codex.MemberwiseCloneOf(),
            Ts = Ts,
            ScreenOn = ScreenOn,
        };
    }
}

static class CloneHelper
{
    public static object MemberwiseCloneOf(this object o)
    {
        var clone = Activator.CreateInstance(o.GetType());
        foreach (var f in o.GetType().GetFields())
            f.SetValue(clone, f.GetValue(o));
        return clone;
    }
}

/// Reads the logs and derives status, with a small time cache so back-to-back
/// HTTP polls and the mirror timer don't each re-scan the whole tree.
sealed class StatusService
{
    readonly string _claudeDir;
    readonly string _codexDir;

    public StatusService(string claudeDir = null, string codexDir = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        _claudeDir = claudeDir ?? Path.Combine(string.IsNullOrWhiteSpace(config)
            ? Path.Combine(profile, ".claude") : config, "projects");
        _codexDir = codexDir ?? Path.Combine(profile, ".codex", "sessions");
    }

    /// Real OAuth quota (5h/weekly windows) merged into snapshots when set;
    /// log-derived values remain the fallback for offline use.
    public UsageFetcher Usage;

    /// Whether the device backlight should be temporarily enabled. Windows
    /// session lock/suspend state supplies this without changing user brightness.
    public Func<bool> ScreenOnProvider;

    // Hook-pushed live state (POST /event from Claude Code / Codex hooks).
    // Events beat the mtime heuristic while fresh: "working" for up to 10min
    // (a long tool run emits nothing between PreToolUse and PostToolUse),
    // "idle" for 60s (long enough to kill the mtime tail after Stop, short
    // enough that a session without hooks isn't stuck idle).
    record AgentEvent(string State, double At);

    AgentEvent _claudeEvent;
    AgentEvent _codexEvent;
    readonly Dictionary<string, (AgentEvent Event, double? Attention)> _claudeSessions = new();
    readonly Dictionary<string, AgentEvent> _claudeTranscriptEvents = new();
    string _lastClaudeStatus;
    // "needs input": a permission/approval prompt is on screen, waiting on the
    // user. Set by an attention event, cleared by the next concrete lifecycle
    // event (the prompt got answered) or by TTL.
    double? _claudeNeedsInputAt;
    double? _codexNeedsInputAt;
    const double WorkingEventTTL = 10 * 60;
    const double IdleEventTTL = 60;
    const double NeedsInputTTL = 5 * 60;

    static readonly HashSet<string> WorkingEvents = new()
    {
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "SubagentStart", "SubagentStop",
        "PreCompact", "PostCompact", "WorktreeCreate",
    };
    static readonly HashSet<string> IdleEvents = new() { "Stop", "SessionEnd", "SessionStart" };
    // Codex PermissionRequest and MCP Elicitation are always a real "act now"
    // prompt. Claude's Notification is broader — it also fires on task
    // completion / 60s-idle — so it only counts as needs-input when its
    // message is actually a permission request.
    static readonly HashSet<string> AttentionEvents = new() { "Elicitation", "PermissionRequest" };

    static bool IsPermissionNotification(string message)
    {
        var m = message?.ToLowerInvariant() ?? "";
        return m.Contains("permission") || m.Contains("approve") || m.Contains("approval");
    }

    /// Called by the /event endpoint. Unknown event names are ignored.
    /// `message` is only sent for Claude's Notification hook.
    public void RecordEvent(string agent, string ev, string message = null, string sessionId = null)
    {
        lock (_lock)
        {
            var now = Now();
            if (agent == "claude" && ev == "Notification" && message == "idle_prompt") ev = "Stop";
            if (agent == "claude" && !string.IsNullOrWhiteSpace(sessionId))
            {
                Log.Write($"[status] claude hook={ev}; tracked_sessions={_claudeSessions.Count}");
                foreach (var key in _claudeSessions.Where(p => now - p.Value.Event.At > IdleThreshold)
                    .Select(p => p.Key).ToArray()) _claudeSessions.Remove(key);
                if (ev == "SessionEnd") { _claudeSessions.Remove(sessionId); return; }
                var attention = AttentionEvents.Contains(ev)
                    || (ev == "Notification" && IsPermissionNotification(message));
                if (!attention && !WorkingEvents.Contains(ev) && !IdleEvents.Contains(ev)
                    && ev != "StopFailure" && ev != "PostToolUseFailure" && ev != "ElicitationResult") return;
                if (_claudeSessions.Count >= 256 && !_claudeSessions.ContainsKey(sessionId)) return;
                _claudeSessions.TryGetValue(sessionId, out var previous);
                var sessionState = attention ? previous.Event?.State ?? "idle"
                    : IdleEvents.Contains(ev) || ev == "StopFailure" ? "idle" : "working";
                _claudeSessions[sessionId] = (new AgentEvent(sessionState, now), attention ? now : null);
                return;
            }
            // Claude Notification: flash only for permission prompts, not for
            // "task done / waiting for your input" notifications.
            if (ev == "Notification")
            {
                if (IsPermissionNotification(message))
                {
                    if (agent == "claude") _claudeNeedsInputAt = now;
                    else if (agent == "codex") _codexNeedsInputAt = now;
                }
                return;
            }
            if (AttentionEvents.Contains(ev))
            {
                if (agent == "claude") _claudeNeedsInputAt = now;
                else if (agent == "codex") _codexNeedsInputAt = now;
                return;
            }
            string state;
            if (WorkingEvents.Contains(ev)) state = "working";
            else if (IdleEvents.Contains(ev)) state = "idle";
            else return;
            var e = new AgentEvent(state, now);
            // any concrete lifecycle event means the prompt (if any) was answered
            if (agent == "claude") { _claudeEvent = e; _claudeNeedsInputAt = null; }
            else if (agent == "codex") { _codexEvent = e; _codexNeedsInputAt = null; }
        }
    }

    static bool NeedsInput(double? at, double now) => at.HasValue && now - at.Value < NeedsInputTTL;

    /// Event override, applied on top of the log-derived status. "offline"
    /// from logs is only upgraded by a fresh working event (a live hook means
    /// the CLI is definitely running).
    static string OverrideStatus(string logStatus, AgentEvent ev, double now)
    {
        if (ev == null) return logStatus;
        var age = now - ev.At;
        if (ev.State == "working" && age < WorkingEventTTL) return "working";
        if (ev.State == "idle" && age < IdleEventTTL) return "idle";
        return logStatus;
    }

    const double WorkingThreshold = 20;        // log touched within this -> "working"
    const double IdleThreshold = 30 * 60;      // within this -> "idle", else "offline"
    const double CacheTTL = 5;

    readonly object _lock = new();
    StatusSnapshot _cached;
    double _cachedAt;

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public StatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = Now();
            StatusSnapshot snap;
            if (_cached != null && now - _cachedAt < CacheTTL)
            {
                snap = _cached.Clone();
            }
            else
            {
                snap = new StatusSnapshot(ReadClaude(), ReadCodex(), (long)now);
                _cached = snap.Clone();
                _cachedAt = now;
            }
            snap.Ts = (long)now;

            // overlays are cheap and applied on every call, so hook events and
            // fresh quota show through instantly even while the log scan is cached
            if (Usage != null)
            {
                var cu = Usage.Claude;
                snap.Claude.FiveHourPct = cu.DesktopStale ? null : cu.PrimaryPct;
                snap.Claude.FiveHourResetMin = cu.PrimaryResetMin;
                snap.Claude.SevenDayPct = cu.DesktopStale ? null : cu.WeeklyPct;
                snap.Claude.SevenDayResetMin = cu.WeeklyResetMin;
                var xu = Usage.Codex;
                if (xu.PrimaryPct.HasValue)
                {
                    snap.Codex.PrimaryPct = xu.PrimaryPct;
                    snap.Codex.PrimaryResetMin = xu.PrimaryResetMin;
                }
                if (xu.WeeklyPct.HasValue)
                {
                    snap.Codex.WeeklyPct = xu.WeeklyPct;
                    snap.Codex.WeeklyResetMin = xu.WeeklyResetMin;
                }
            }
            snap.Claude.Status = OverrideStatus(snap.Claude.Status, _claudeEvent, now);
            snap.Codex.Status = OverrideStatus(snap.Codex.Status, _codexEvent, now);
            snap.Claude.NeedsInput = NeedsInput(_claudeNeedsInputAt, now);
            // A lost Stop must not keep a completed turn working for ten minutes.
            // Reconcile only the SAME session, and only with a newer transcript event.
            foreach (var id in _claudeSessions.Keys.ToArray())
            {
                var tracked = _claudeSessions[id];
                if (_claudeTranscriptEvents.TryGetValue(id, out var transcript)
                    && transcript.State == "idle" && transcript.At >= tracked.Event.At)
                    _claudeSessions[id] = (transcript, null);
            }
            var sessions = _claudeSessions.Values.Concat(_claudeTranscriptEvents
                .Where(p => !_claudeSessions.ContainsKey(p.Key))
                .Select(p => (Event: p.Value, Attention: (double?)null)))
                .Where(s => now - s.Event.At < IdleThreshold).ToArray();
            if (sessions.Length > 0)
            {
                if (sessions.Any(s => s.Event.State == "working" && now - s.Event.At < WorkingEventTTL))
                    snap.Claude.Status = "working";
                else snap.Claude.Status = "idle";
                snap.Claude.NeedsInput |= sessions.Any(s => NeedsInput(s.Attention, now));
            }
            snap.Codex.NeedsInput = NeedsInput(_codexNeedsInputAt, now);
            if (_lastClaudeStatus != snap.Claude.Status)
            {
                Log.Write($"[status] claude {_lastClaudeStatus ?? "unknown"}->{snap.Claude.Status};"
                    + $" hook_sessions={_claudeSessions.Count}; transcript_sessions={_claudeTranscriptEvents.Count}");
                _lastClaudeStatus = snap.Claude.Status;
            }
            snap.ScreenOn = ScreenOnProvider?.Invoke() ?? true;
            return snap;
        }
    }

    // MARK: - helpers

    static string StatusFromDelta(double delta)
    {
        if (delta < WorkingThreshold) return "working";
        if (delta < IdleThreshold) return "idle";
        return "offline";
    }

    static double? ParseIso(string s)
    {
        if (s == null) return null;
        if (DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return d.ToUnixTimeMilliseconds() / 1000.0;
        return null;
    }

    static double TodayStartEpoch() =>
        new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds() / 1000.0;

    /// Lossy UTF-8 read split into lines (skips files locked by the CLIs).
    static string[] ReadLines(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return null;
        }
    }

    static int IntVal(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return (int)v.GetDouble();
        return 0;
    }

    static double? DoubleVal(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    static string StringVal(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    static bool TryProp(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value);
    }

    // MARK: - Claude

    ClaudeStatus ReadClaude()
    {
        var todayStart = TodayStartEpoch();
        var now = Now();
        var tokensToday = 0;
        double lastMtime = 0;
        double? firstActiveInWindow = null;
        _claudeTranscriptEvents.Clear();

        if (Directory.Exists(_claudeDir))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_claudeDir, "*.jsonl", SearchOption.AllDirectories);
            }
            catch
            {
                files = Array.Empty<string>();
            }
            foreach (var file in files)
            {
                double mtime;
                try
                {
                    mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds() / 1000.0;
                }
                catch
                {
                    continue;
                }
                if (mtime > lastMtime) lastMtime = mtime;
                if (mtime < todayStart) continue; // no activity today, skip parsing
                var lines = ReadLines(file);
                if (lines == null) continue;
                foreach (var line in lines)
                {
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch { continue; }
                    using (doc)
                    {
                        var root = doc.RootElement;
                        ReadClaudeTranscriptEvent(root, now);
                        if (!TryProp(root, "message", out var message)
                            || !TryProp(message, "usage", out var usage)) continue;
                        var entryEpoch = ParseIso(StringVal(root, "timestamp"));
                        if (entryEpoch.HasValue && entryEpoch.Value < todayStart) continue;
                        tokensToday += IntVal(usage, "input_tokens") + IntVal(usage, "output_tokens")
                            + IntVal(usage, "cache_creation_input_tokens")
                            + IntVal(usage, "cache_read_input_tokens");
                        if (entryEpoch.HasValue && now - entryEpoch.Value < 5 * 3600)
                        {
                            if (!firstActiveInWindow.HasValue || entryEpoch.Value < firstActiveInWindow.Value)
                                firstActiveInWindow = entryEpoch.Value;
                        }
                    }
                }
            }
        }

        var s = new ClaudeStatus { TokensToday = tokensToday };
        if (firstActiveInWindow.HasValue) s.SessionMin = (int)((now - firstActiveInWindow.Value) / 60);
        s.Status = StatusFromDelta(lastMtime > 0 ? now - lastMtime : 1e9);
        // Background metadata writes are not proof the agent is still working.
        // Explicit conversation events take precedence over file modification time.
        if (_claudeTranscriptEvents.Count > 0)
            s.Status = _claudeTranscriptEvents.Values.Any(e => e.State == "working"
                && now - e.At < WorkingEventTTL) ? "working"
                : _claudeTranscriptEvents.Values.Any(e => now - e.At < IdleThreshold) ? "idle" : "offline";
        return s;
    }

    void ReadClaudeTranscriptEvent(JsonElement root, double now)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (TryProp(root, "isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return;
        var id = StringVal(root, "sessionId");
        var at = ParseIso(StringVal(root, "timestamp"));
        if (string.IsNullOrEmpty(id) || !at.HasValue || at > now + 5) return;
        var type = StringVal(root, "type");
        string state = null;
        if (type == "user" || type == "api-request") state = "working";
        if (type == "assistant" && TryProp(root, "message", out var message))
        {
            var reason = StringVal(message, "stop_reason");
            state = reason == "end_turn" || reason == "stop_sequence" ? "idle" : "working";
        }
        if (state != null && (!_claudeTranscriptEvents.TryGetValue(id, out var previous) || at >= previous.At))
            _claudeTranscriptEvents[id] = new AgentEvent(state, at.Value);
    }

    // MARK: - Codex

    CodexStatus ReadCodex()
    {
        var now = Now();
        double lastMtime = 0;

        // Whole-tree scan just for the freshest mtime (drives working/idle).
        if (Directory.Exists(_codexDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_codexDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds() / 1000.0;
                    if (mtime > lastMtime) lastMtime = mtime;
                }
            }
            catch
            {
                // partial scan is fine
            }
        }

        // Tokens + rate limits only from today's day directory.
        var today = DateTime.Today;
        var dayDir = Path.Combine(_codexDir, $"{today.Year:D4}", $"{today.Month:D2}", $"{today.Day:D2}");

        var tokensToday = 0;
        JsonElement? latestRateLimits = null;
        JsonDocument latestRateLimitsDoc = null;
        double latestRateLimitsTs = 0;

        if (Directory.Exists(dayDir))
        {
            foreach (var file in Directory.EnumerateFiles(dayDir, "*.jsonl"))
            {
                var lines = ReadLines(file);
                if (lines == null) continue;
                var sessionMaxTokens = 0;
                foreach (var line in lines)
                {
                    if (!line.Contains("\"token_count\"")) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch { continue; }
                    var root = doc.RootElement;
                    if (!TryProp(root, "payload", out var payload)
                        || StringVal(payload, "type") != "token_count")
                    {
                        doc.Dispose();
                        continue;
                    }
                    if (TryProp(payload, "info", out var info)
                        && TryProp(info, "total_token_usage", out var totalUsage))
                    {
                        var total = IntVal(totalUsage, "total_tokens");
                        if (total > sessionMaxTokens) sessionMaxTokens = total;
                    }
                    if (TryProp(payload, "rate_limits", out var rl))
                    {
                        var e = ParseIso(StringVal(root, "timestamp")) ?? 0;
                        if (e >= latestRateLimitsTs)
                        {
                            latestRateLimitsTs = e;
                            latestRateLimitsDoc?.Dispose();
                            latestRateLimitsDoc = doc; // keep doc alive for rl
                            latestRateLimits = rl;
                            continue;
                        }
                    }
                    doc.Dispose();
                }
                tokensToday += sessionMaxTokens;
            }
        }

        var s = new CodexStatus { TokensToday = tokensToday };
        s.Status = StatusFromDelta(lastMtime > 0 ? now - lastMtime : 1e9);
        if (latestRateLimits.HasValue)
        {
            // Same window-length classification as UsageFetcher.FetchCodex:
            // since Codex dropped the 5h limit (2026-07) its session logs put
            // the weekly window in the "primary" slot, so sort by length.
            var rl = latestRateLimits.Value;
            foreach (var (slot, fallbackMin) in new[] { ("primary", 300), ("secondary", 7 * 1440) })
            {
                if (!TryProp(rl, slot, out var w)) continue;
                var pct = DoubleVal(w, "used_percent");
                var winMin = (int?)DoubleVal(w, "window_minutes");
                int? resetMin = null;
                var reset = DoubleVal(w, "resets_at");
                if (reset.HasValue) resetMin = Math.Max(0, (int)((reset.Value - now) / 60));
                if ((winMin ?? fallbackMin) >= 2 * 1440)
                {
                    if (!s.WeeklyPct.HasValue)
                    {
                        s.WeeklyPct = pct;
                        s.WeeklyWindowMin = winMin;
                        s.WeeklyResetMin = resetMin;
                    }
                }
                else if (!s.PrimaryPct.HasValue)
                {
                    s.PrimaryPct = pct;
                    s.PrimaryWindowMin = winMin;
                    s.PrimaryResetMin = resetMin;
                }
            }
        }
        latestRateLimitsDoc?.Dispose();
        return s;
    }
}
