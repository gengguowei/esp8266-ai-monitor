using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIClockBridge;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}
var now = DateTime.UtcNow;
var ms = new DateTimeOffset(now).ToUnixTimeMilliseconds();
ProviderUsage Parse(string json)
{
    using var doc = JsonDocument.Parse(json);
    return ClaudeDesktopUsage.Parse(doc.RootElement, now);
}
var v1 = Parse($$$"""{"version":1,"samples":[{"t":{{{ms}}},"fh":0,"sd":22}]}""");
Check(v1.PrimaryPct == 0 && v1.WeeklyPct == 22 && !v1.DesktopStale, "v1 incl zero");
var v2 = Parse($$$"""{"version":2,"samples":[{"t":{{{ms}}},"org":"new","u":{"fh":31}},{"t":{{{ms-1000}}},"org":"old","u":{"fh":90,"sd":99}}]}""");
Check(v2.PrimaryPct == 31 && v2.WeeklyPct == null, "newest sample, no cross-org mixing");
Check(v2.PrimaryResetMin == null && v2.WeeklyResetMin == null, "no fabricated reset times");
Check(Parse("{\"version\":\"2\",\"samples\":[]}") == null, "invalid version type");
Check(Parse("{\"version\":3,\"samples\":[]}") == null, "future version");
Check(Parse($$$"""{"version":2,"samples":[{"t":{{{ms+600000}}},"u":{"fh":50}}]}""") == null, "future timestamp");
var invalid = Parse($$$"""{"version":2,"samples":[null,{"t":{{{ms}}},"u":{"fh":101,"sd":-1}}]}""");
Check(invalid.PrimaryPct == null && invalid.WeeklyPct == null, "invalid percentages");
var stale = Parse($$$"""{"version":2,"samples":[{"t":{{{ms - 40*60*1000}}},"u":{"fh":50,"sd":25}}]}""");
Check(stale.DesktopStale && stale.FetchedAt < now.AddMinutes(-35), "sample age preserved");

// Claude Desktop appends a sample every 15.00 min (measured on a real install),
// so the staleness threshold has to clear two of them. At the old value of 15
// min the newest sample expired exactly when the next was written, blanking the
// quota on the device once per cycle.
Check(ProviderUsage.DesktopStaleAfter > ProviderUsage.DesktopSampleInterval * 2,
    "stale threshold clears two Desktop sampling intervals");
ProviderUsage Aged(int minutes) =>
    Parse($$$"""{"version":2,"samples":[{"t":{{{ms - minutes*60*1000}}},"u":{"fh":50,"sd":25}}]}""");
Check(!Aged(14).DesktopStale, "14 min old - normal mid-cycle age, still fresh");
Check(!Aged(16).DesktopStale, "16 min old - the case that used to blank the device");
Check(!Aged(29).DesktopStale, "one missed sample is not yet stale");
Check(Aged(36).DesktopStale, "two missed samples means Desktop is gone");

// Cache discovery must not depend on the MSIX publisher hash staying constant.
var cachePaths = ClaudeDesktopUsage.Paths().ToArray();
Check(cachePaths.Length >= 1
      && cachePaths.All(p => p.EndsWith("plan-usage-history.json", StringComparison.Ordinal)),
    "every candidate path names the cache file");
Check(cachePaths.Any(p => !p.Contains(@"\Packages\", StringComparison.Ordinal)),
    "non-MSIX fallback path still offered");

var settings = JsonNode.Parse("""{"env":{"CUSTOM":"kept"},"hooks":{"Stop":[{"hooks":[{"type":"command","command":"existing"}]}]}}""")!.AsObject();
ClaudeHooks.Merge(settings, "test-hook");
var once = settings.ToJsonString();
ClaudeHooks.Merge(settings, "test-hook");
Check(settings.ToJsonString() == once, "hook installation idempotent");
Check(settings["env"]["CUSTOM"].ToString() == "kept" && settings["hooks"]["Stop"].AsArray().Count == 2,
    "existing settings/hooks preserved");

// No user log/config/network access: use absent test paths and skip auto-refresh.
var absent = Path.Combine(Path.GetTempPath(), "aiclock-test-" + Guid.NewGuid());
var service = new StatusService(absent, absent);
service.RecordEvent("claude", "SessionStart", sessionId: "a");
Check(service.Snapshot().Claude.Status == "idle", "desktop session idle without logs");
service.RecordEvent("claude", "UserPromptSubmit", sessionId: "a");
service.RecordEvent("claude", "UserPromptSubmit", sessionId: "b");
service.RecordEvent("claude", "Stop", sessionId: "a");
Check(service.Snapshot().Claude.Status == "working", "one stop cannot hide other work");
service.RecordEvent("claude", "PermissionRequest", sessionId: "b");
Check(service.Snapshot().Claude.NeedsInput, "permission needs input");
service.RecordEvent("claude", "PostToolUse", sessionId: "b");
Check(!service.Snapshot().Claude.NeedsInput, "tool completion clears permission");
service.RecordEvent("claude", "Notification", "idle_prompt", "b");
Check(!service.Snapshot().Claude.NeedsInput, "idle notification is not permission");
service.RecordEvent("claude", "StopFailure", sessionId: "b");
Check(service.Snapshot().Claude.Status == "idle", "failed turn ends working");
service.RecordEvent("claude", "SessionEnd", sessionId: "a");
service.RecordEvent("claude", "SessionEnd", sessionId: "b");
Check(service.Snapshot().Claude.Status == "offline", "all sessions ended");
service.RecordEvent("codex", "PreToolUse");
Check(service.Snapshot().Codex.Status == "working", "legacy hooks remain supported");

// Bypass constructor to avoid loading real persisted settings in this unit test.
var fetcher = (UsageFetcher)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UsageFetcher));
void Set(string field, object value) => typeof(UsageFetcher).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fetcher, value);
Set("_lock", new object()); Set("_claude", stale); Set("_codex", new ProviderUsage());
service.Usage = fetcher;
using var status = JsonDocument.Parse(service.Snapshot().ToJson());
Check(status.RootElement.GetProperty("claude").GetProperty("five_hour_pct").ValueKind == JsonValueKind.Null,
    "expired desktop percentage withheld from firmware");
Set("_claude", v1);
Check(service.Snapshot().Claude.FiveHourPct == 0, "fresh zero forwarded to firmware");

// Poll cadences must stay split: the Desktop cache is a local file and is read
// every minute, while network fetches keep the slower interval they had when
// the timer itself ran at 120s. Collapsing these back into one constant would
// silently double the Codex / Claude-OAuth request rate.
TimeSpan Interval(string name) => (TimeSpan)typeof(UsageFetcher)
    .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
Check(Interval("DesktopPollInterval") == TimeSpan.FromMinutes(1), "desktop cache re-read every minute");
Check(Interval("NetworkFetchInterval") >= TimeSpan.FromSeconds(120), "auto network cadence not accelerated");
Check(Interval("MinFetchInterval") <= Interval("NetworkFetchInterval"),
    "manual refresh floor no slower than the auto cadence");
Check(Interval("RateLimitBackoff") > Interval("NetworkFetchInterval"), "429 backoff outlasts the normal cadence");

// Autostart: pure helpers only. The registry side is deliberately untested -
// this suite must not write to the user's HKCU.
Check(AutoStart.FormatCommand(@"C:\Program Files\AI\AIClockBridge.exe")
    == "\"C:\\Program Files\\AI\\AIClockBridge.exe\" --autostart",
    "run command is quoted and carries the autostart flag");
Check(AutoStart.ExtractExePath("\"C:\\a b\\x.exe\" --autostart") == @"C:\a b\x.exe",
    "quoted path with args");
Check(AutoStart.ExtractExePath(@"C:\a\x.exe --autostart") == @"C:\a\x.exe", "unquoted path with args");
Check(AutoStart.ExtractExePath(@"C:\a\x.exe") == @"C:\a\x.exe", "bare path");
Check(AutoStart.ExtractExePath("") == "" && AutoStart.ExtractExePath("   ") == "", "empty command");
Check(AutoStart.ExtractExePath(AutoStart.FormatCommand(@"C:\a b\x.exe")) == @"C:\a b\x.exe",
    "format and extract round-trip");
Check(AutoStart.SamePath(@"C:\A\X.EXE", @"c:\a\x.exe"), "path compare is case-insensitive");
Check(!AutoStart.SamePath(@"C:\AIClockBridge-0.4.13-Windows-x64\x.exe",
                          @"C:\AIClockBridge-0.4.14-Windows-x64\x.exe"),
    "a version bump counts as moved, so SelfHeal repoints");
Check(!AutoStart.SamePath("", @"C:\a\x.exe"), "empty path never matches");
Check(AutoStart.ApprovedByWindows(null) && AutoStart.ApprovedByWindows(Array.Empty<byte>()),
    "missing StartupApproved value means enabled");
Check(AutoStart.ApprovedByWindows(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
    "0x02 blob is enabled");
Check(!AutoStart.ApprovedByWindows(new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
    "0x03 blob means disabled in Task Manager");
Check(!AutoStart.ApprovedByWindows(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
    "0x07 blob is disabled too");
Check(AutoStart.IsRegistrablePath(@"C:\a\AIClockBridge.exe"), "published exe is registrable");
Check(!AutoStart.IsRegistrablePath(@"C:\Program Files\dotnet\dotnet.exe"),
    "dotnet muxer refused - `dotnet run` must not register itself");
Check(!AutoStart.IsRegistrablePath("") && !AutoStart.IsRegistrablePath(@"C:\a\thing.dll"),
    "empty and non-exe paths refused");

// WinINET proxy parsing. The bug this guards: HttpClient caches the system
// proxy for the life of the process, so a proxy client toggled while the bridge
// runs strands every outbound request (Codex quota + stock quotes) until a
// restart. SystemProxy re-reads these values on each client rebuild.
Check(SystemProxy.ParseProxyServer("127.0.0.1:10809", "https") == "127.0.0.1:10809",
    "bare host:port applies to every scheme");
Check(SystemProxy.ParseProxyServer("http=127.0.0.1:1080;https=127.0.0.1:1081;socks=127.0.0.1:1082", "https")
      == "127.0.0.1:1081", "per-scheme list picks https");
Check(SystemProxy.ParseProxyServer("http=127.0.0.1:1080;socks=127.0.0.1:1082", "https") == "",
    "no https entry means no https proxy");
Check(SystemProxy.ParseProxyServer("HTTPS=127.0.0.1:1081", "https") == "127.0.0.1:1081",
    "scheme match is case-insensitive");
Check(SystemProxy.ParseProxyServer("", "https") == ""
      && SystemProxy.ParseProxyServer(null, "https") == "", "empty settings mean no proxy");
Check(SystemProxy.Normalize("127.0.0.1:10809") == "http://127.0.0.1:10809",
    "bare endpoint gets a scheme WebProxy can parse");
Check(SystemProxy.Normalize("http://127.0.0.1:10809") == "http://127.0.0.1:10809",
    "an endpoint that already has a scheme is left alone");

// CJK stock name strips over the serial link (so a USB-only device is not stuck
// showing bare codes just because it has no WiFi).
const int NameW = 156, NameH = 16, Strips = 3;
var stripBytes = NameW * 2 * NameH;
var namesBlob = new byte[1 + Strips * stripBytes];
namesBlob[0] = Strips;
new Random(7).NextBytes(namesBlob.AsSpan(1));
var frames = StockNameFrames.Build(namesBlob, 4, NameW, NameH);
Check(frames.Count == 1 + Strips * NameH, "one header plus one frame per pixel row");
Check(frames[0] == "#NAMES 4 3", "header carries the revision and strip count");
// The firmware splits on newline into char serialLine[1600] and drops anything
// longer, so this is a hard cross-boundary limit, not a style preference.
Check(frames.All(f => f.Length < 1500), "every frame fits the firmware's 1600-byte line buffer");
Check(frames.Skip(1).All(f => f.StartsWith("#NAMEROW 4 ")), "rows are tagged with the revision");
// A row must survive base64 exactly, or the strip renders as garbage pixels.
var firstRow = frames[1]["#NAMEROW 4 0 0 ".Length..];
var decoded = Convert.FromBase64String(firstRow);
Check(decoded.Length == NameW * 2, "a frame carries exactly one pixel row");
Check(decoded.SequenceEqual(namesBlob.Skip(1).Take(NameW * 2)), "pixel row round-trips through base64");
var lastRow = frames[^1];
Check(lastRow.StartsWith($"#NAMEROW 4 {Strips - 1} {NameH - 1} "), "last frame is the last row of the last strip");
Check(StockNameFrames.Build(new byte[] { 3, 0, 0 }, 1, NameW, NameH).Count == 0,
    "a truncated blob (render still in flight) produces nothing");
Check(StockNameFrames.Build(null, 1, NameW, NameH).Count == 0
      && StockNameFrames.Build(new byte[] { 0 }, 1, NameW, NameH).Count == 0,
    "no strips rendered means nothing to send");

// Claude source precedence: the OAuth endpoint outranks the Desktop cache when
// it works (live, and the only source with reset countdowns), but must never
// turn a usable cached reading into an error message.
ProviderUsage Api(double? pct, string err = null) =>
    new() { PrimaryPct = pct, WeeklyPct = pct, Error = err, FetchedAt = now };
ProviderUsage Cache(double? pct) =>
    new() { PrimaryPct = pct, WeeklyPct = pct, DesktopCache = true, FetchedAt = now };

var live = Api(70);
var cached = Cache(50);
var apiErr = Api(null, "Claude 用量请求失败：boom");

Check(ReferenceEquals(UsageFetcher.ChooseClaude(live, cached, didNetwork: true, apiTrusted: false), live),
    "a working OAuth read outranks the desktop cache");
Check(ReferenceEquals(UsageFetcher.ChooseClaude(apiErr, cached, didNetwork: true, apiTrusted: false), cached),
    "a failed OAuth read falls back to the cache, not to an error");
Check(ReferenceEquals(UsageFetcher.ChooseClaude(apiErr, null, didNetwork: true, apiTrusted: false), apiErr),
    "with no cache the API error is what gets shown");
Check(UsageFetcher.ChooseClaude(null, cached, didNetwork: false, apiTrusted: true) == null,
    "a desktop-only tick must not demote a still-trusted live reading");
Check(ReferenceEquals(UsageFetcher.ChooseClaude(null, cached, didNetwork: false, apiTrusted: false), cached),
    "once the API goes quiet the cache takes over again");
Check(UsageFetcher.ChooseClaude(null, null, didNetwork: false, apiTrusted: false) == null,
    "no sources at all yields nothing");
Check(UsageFetcher.HasNumbers(live) && !UsageFetcher.HasNumbers(apiErr)
      && !UsageFetcher.HasNumbers(null), "a reading counts only when it carries a percentage");
TimeSpan Span(string name) => (TimeSpan)typeof(UsageFetcher)
    .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
Check(Span("ApiTrustWindow") > Span("NetworkFetchInterval"),
    "the trust window outlasts one network cadence, or the cache would clobber live data");

// Network gates are per provider. A shared pair let a chatgpt.com 429 - whose
// Retry-After can be hours - strand the Claude OAuth fetch on the Desktop cache
// for that whole window, with re-logging in and the tray's "刷新" both powerless
// to bring the live reading back.
var gateType = typeof(UsageFetcher).GetNestedType("Gate", BindingFlags.NonPublic)!;
object NewGate() => Activator.CreateInstance(gateType, nonPublic: true)!;
void GateAdvance(object g, DateTime at, ProviderUsage result) =>
    gateType.GetMethod("Advance")!.Invoke(g, new object[] { at, result });
bool GateDue(object g, DateTime at, bool userInitiated) =>
    (bool)gateType.GetMethod("Due")!.Invoke(g, new object[] { at, userInitiated })!;
DateTime GateNextAllowed(object g) => (DateTime)gateType.GetField("NextAllowed")!.GetValue(g)!;

var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var throttled = new ProviderUsage { RateLimited = true, RetryAfterSeconds = 7200 };
var ok = new ProviderUsage { PrimaryPct = 10, FetchedAt = t0 };

var codexGate = NewGate();
var claudeGate = NewGate();
GateAdvance(codexGate, t0, throttled);
GateAdvance(claudeGate, t0, ok);
Check(GateDue(claudeGate, t0 + Span("MinFetchInterval"), userInitiated: true),
    "one vendor's 429 leaves the other vendor's gate alone");
Check(!GateDue(codexGate, t0 + Span("MinFetchInterval"), userInitiated: true),
    "the throttled vendor does stay held back");
Check(GateNextAllowed(codexGate) <= t0 + Span("MaxBackoff"),
    "an absurd Retry-After is capped, not obeyed for hours");
Check(GateNextAllowed(NewGate()) == DateTime.MinValue
      && GateDue(NewGate(), t0, userInitiated: false), "a fresh gate is open");

var plainLimit = NewGate();
GateAdvance(plainLimit, t0, new ProviderUsage { RateLimited = true });
Check(GateNextAllowed(plainLimit) == t0 + Span("RateLimitBackoff"),
    "a 429 with no Retry-After still backs off by the default");

// A Claude 429 masked by the Desktop cache must still throttle: Advance is fed
// the raw API result, never the cache reading that replaced it on screen.
var masked = UsageFetcher.ChooseClaude(
    new ProviderUsage { RateLimited = true, Error = "429" }, cached,
    didNetwork: true, apiTrusted: false);
Check(masked is { RateLimited: false },
    "the cache stand-in carries no rate-limit flag, so the raw result is what the gate needs");

Console.WriteLine($"PASS: {checks} checks");
if (args.Contains("--desktop-cache"))
{
    var actual = ClaudeDesktopUsage.Read();
    Console.WriteLine(JsonSerializer.Serialize(new { Found = actual != null,
        PrimaryPct = actual?.PrimaryPct, WeeklyPct = actual?.WeeklyPct,
        SampleTime = actual?.FetchedAt, Stale = actual?.DesktopStale }));
    if (actual == null) Environment.ExitCode = 1;
}
