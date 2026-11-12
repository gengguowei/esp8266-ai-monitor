using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIClockBridge;

// Quota for the two CLIs this bridge still follows, reusing credentials already
// on disk. No extra login:
//   Grok:  %USERPROFILE%\.grok\auth.json, then GET the CLI billing endpoint
//          (one included-credit percentage, weekly or monthly)
//   Codex: %USERPROFILE%\.codex\auth.json, then GET
//          https://chatgpt.com/backend-api/wham/usage
// Tokens never leave this machine except toward their own vendor's API.

class ProviderUsage
{
    public double? PrimaryPct;     // 5h window used %
    public int? PrimaryResetMin;   // minutes until it resets
    public double? WeeklyPct;      // 7d / weekly window used %, or Grok's single credit window
    public int? WeeklyResetMin;
    public string Period;          // Grok: "week" or "month". Null for Codex.
    public string Error;
    public DateTime? FetchedAt;
    public bool RateLimited;
    public int? RetryAfterSeconds;
    public bool AuthenticationError;
    public bool DesktopCache;

    /// Claude Desktop appends a sample every 15 minutes (5 shortly after an
    /// interaction) - but only while its background poll runs. Desktop 4.x
    /// pauses that poll unless its tray usage view was opened within a
    /// server-configured window (observed: 24h, key planUsageLastTrayOpenAt in
    /// Desktop's config.json) and skips it while the PC is idle for 10 minutes.
    /// Outside that window the cache gets a single sample at Desktop startup.
    public static readonly TimeSpan DesktopSampleInterval = TimeSpan.FromMinutes(15);

    /// Must clear TWO sampling intervals. The old threshold was 15 minutes -
    /// exactly the sampling period - so the newest sample expired at the very
    /// instant Desktop wrote the next one, and the quota blanked out on the
    /// device (and read "已过期" in the tray) for up to a poll interval every
    /// single cycle. Desktop also self-updates every ~4h and restarts, which
    /// drops the occasional sample, so the honest test for "Desktop is gone" is
    /// two missed samples plus slack.
    public static readonly TimeSpan DesktopStaleAfter = TimeSpan.FromMinutes(35);

    public bool DesktopStale => DesktopCache && (!FetchedAt.HasValue
        || DateTime.UtcNow - FetchedAt.Value > DesktopStaleAfter);
}

sealed class UsageFetcher
{
    // Rebuildable: a proxy client toggled while we run would otherwise strand
    // every quota request until the process restarts. See ResilientHttpClient.
    static readonly ResilientHttpClient Http = new("usage", TimeSpan.FromSeconds(20));

    readonly object _lock = new();
    ProviderUsage _claude = new();
    ProviderUsage _codex = new();
    System.Windows.Forms.Timer _timer;
    bool _fetching;
    readonly Gate _claudeGate = new();
    readonly Gate _codexGate = new();
    DateTime _claudeApiOkAt = DateTime.MinValue;      // last good OAuth reading
    string _claudeAuthError;                          // see ClaudeAuthError

    // Three cadences, deliberately different.
    //  - DesktopPollInterval: the Claude Desktop quota cache is a local file, so
    //    re-reading it is free. This is what the timer ticks at, and what makes
    //    a new Desktop sample reach the tray/device within a minute, not two.
    //  - NetworkFetchInterval: automatic cadence for the vendor endpoints,
    //    unchanged from when the timer itself ran at 120s. Ticking faster must
    //    not raise the API request rate.
    //  - MinFetchInterval: hard floor under *any* network fetch, user-initiated
    //    ones included, so opening the tray menu repeatedly cannot hammer them.
    static readonly TimeSpan DesktopPollInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan NetworkFetchInterval = TimeSpan.FromSeconds(120);
    static readonly TimeSpan MinFetchInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(300);
    /// Ceiling on a honoured Retry-After. chatgpt.com has answered with headers
    /// measured in hours; obeying one literally took a provider off the air for
    /// the rest of the session, with nothing in the UI to say so.
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(900);

    /// One provider's network throttle.
    ///
    /// These used to be a single pair of fields shared by both vendors, so a 429
    /// from chatgpt.com - which can carry a Retry-After of hours - also blocked
    /// the Claude OAuth fetch for that entire window. Claude then dropped to the
    /// Desktop cache and stayed there: re-logging in did not help, and neither
    /// did the tray's "刷新", because the block had nothing to do with
    /// credentials and userInitiated only bypasses the *cadence*, not the floor.
    /// One vendor's rate limit may only ever slow that vendor down.
    sealed class Gate
    {
        public DateTime NextAllowed = DateTime.MinValue;    // hard floor + 429 backoff
        public DateTime NextScheduled = DateTime.MinValue;  // automatic cadence

        public bool Due(DateTime now, bool userInitiated) =>
            now >= NextAllowed && (userInitiated || now >= NextScheduled);

        /// <param name="result">
        /// The provider's own raw response. It must be the network result, not
        /// whatever ended up on screen: once the Desktop cache stands in for a
        /// failed Claude fetch, the displayed reading carries no RateLimited
        /// flag, so a Claude 429 read off it was silently ignored.
        /// </param>
        public void Advance(DateTime now, ProviderUsage result)
        {
            var backoff = result is { RateLimited: true }
                ? TimeSpan.FromSeconds(Math.Clamp(result.RetryAfterSeconds ?? 0,
                    (int)RateLimitBackoff.TotalSeconds, (int)MaxBackoff.TotalSeconds))
                : MinFetchInterval;
            NextAllowed = now + backoff;
            // A 429 backoff longer than the normal cadence wins.
            NextScheduled = now
                + (backoff > NetworkFetchInterval ? backoff : NetworkFetchInterval);
        }
    }

    /// How long a good OAuth reading keeps precedence over the Desktop cache.
    /// Must exceed NetworkFetchInterval, or the cache would overwrite a live
    /// reading during the desktop-only ticks in between.
    static readonly TimeSpan ApiTrustWindow = TimeSpan.FromTicks(NetworkFetchInterval.Ticks * 2);

    // Persisted last-good quota, so a cold start that lands inside the Claude
    // endpoint's 429 window shows the previous percentages instead of a blank
    // until the first successful fetch (which Merge() then keeps rolling).
    const string ClaudeCacheKey = "usage_cache_grok";
    const string CodexCacheKey = "usage_cache_codex";
    static readonly double CacheMaxAgeSec = 24 * 3600;

    public ProviderUsage Claude { get { lock (_lock) return _claude; } }
    public ProviderUsage Codex { get { lock (_lock) return _codex; } }

    /// The CLI OAuth credential failure that is keeping the live Claude source
    /// down, or null. Read off the raw network result, not the displayed
    /// reading: on a Desktop fallback the latter carries no auth flag. Only a
    /// successful fetch clears it, so a 429 in between does not end the episode.
    public string ClaudeAuthError { get { lock (_lock) return _claudeAuthError; } }

    /// Repeat interval for the credential notice. That source was dead for three
    /// weeks (2026-10-09 → 11-02) with nothing but tray-menu text to show for it.
    internal static readonly TimeSpan ClaudeAuthNoticeInterval = TimeSpan.FromHours(24);

    /// Notify only while the failure actually costs the user their quota: the
    /// Desktop cache may still be covering it, and a Desktop-only setup with
    /// no CLI login must not be nagged as long as that cache stays fresh.
    internal static bool ClaudeAuthNoticeDue(string authError, ProviderUsage shown,
                                             DateTime now, DateTime? lastNoticeAt) =>
        authError != null
        && !(HasNumbers(shown) && !shown.DesktopStale)
        && (lastNoticeAt == null || now - lastNoticeAt.Value >= ClaudeAuthNoticeInterval);

    /// Raised on the UI thread after either provider updates.
    public Action OnUpdate;

    System.Threading.SynchronizationContext _ui;

    public UsageFetcher()
    {
        var c = LoadCache(ClaudeCacheKey);
        if (c != null) _claude = c;
        var x = LoadCache(CodexCacheKey);
        if (x != null) _codex = x;
    }

    /// Pass null to tick at DesktopPollInterval (the default).
    public void StartAutoRefresh(int? intervalSeconds = null)
    {
        var interval = intervalSeconds ?? (int)DesktopPollInterval.TotalSeconds;
        _ui = System.Threading.SynchronizationContext.Current;
        Refresh();
        _timer = new System.Windows.Forms.Timer { Interval = interval * 1000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    /// <param name="userInitiated">
    /// True when a person asked for this (tray menu opened, "刷新" clicked).
    /// Such a pass skips the automatic cadence and is held only by the hard
    /// floor, which is what the single old gate did before the desktop cache
    /// was split out.
    /// </param>
    public void Refresh(bool userInitiated = false)
    {
        bool doClaude, doCodex;
        lock (_lock)
        {
            if (_fetching) return;
            // The gates now hold back only the network half; the desktop cache
            // is re-read on every pass, so opening the tray menu or hitting
            // "刷新" always picks up a new Desktop sample instead of being
            // swallowed by the throttle.
            var now = DateTime.UtcNow;
            doClaude = _claudeGate.Due(now, userInitiated);
            doCodex = _codexGate.Due(now, userInitiated);
            _fetching = true;
        }

        Task.Run(async () =>
        {
            var api = doClaude ? await FetchGrok() : null;
            if (doClaude && HasNumbers(api)) _claudeApiOkAt = DateTime.UtcNow;
            var claude = api;
            var codex = doCodex ? await FetchCodex() : null;

            if (claude == null && codex == null)
            {
                lock (_lock) _fetching = false;
                return;   // desktop-only pass with no Desktop cache: nothing to do
            }

            ProviderUsage mergedClaude, mergedCodex;
            lock (_lock)
            {
                // Keep the last good numbers when a refresh only produced an
                // error (network hiccup / 429) - stale quota beats no quota.
                if (claude != null) _claude = Merge(_claude, claude);
                if (codex != null) _codex = Merge(_codex, codex);
                if (HasNumbers(api)) _claudeAuthError = null;
                else if (api is { AuthenticationError: true }) _claudeAuthError = api.Error;
                mergedClaude = _claude;
                mergedCodex = _codex;
                _fetching = false;
                // Only a pass that actually touched the network may move a
                // gate. A desktop-cache read must not push the next API fetch
                // out, or the CLI fallback would starve.
                var gateNow = DateTime.UtcNow;
                if (doClaude) _claudeGate.Advance(gateNow, api);
                if (doCodex) _codexGate.Advance(gateNow, codex);
            }
            // Only a successful response may advance the persisted cache age.
            // Re-saving merged stale data after an error made an old value look
            // freshly fetched forever.
            if (claude != null && claude.FetchedAt.HasValue && !claude.DesktopCache)
                SaveCache(mergedClaude, ClaudeCacheKey);
            if (codex != null && codex.FetchedAt.HasValue) SaveCache(mergedCodex, CodexCacheKey);
            if (api?.Error != null) Log.Write($"[usage] grok: {api.Error}");
            else if (api?.WeeklyPct != null)
                Log.Write($"[usage] grok: {api.WeeklyPct:0.#}% {api.Period}");
            if (codex?.Error != null) Log.Write($"[usage] codex: {codex.Error}");
            if (_ui != null) _ui.Post(_ => OnUpdate?.Invoke(), null);
            else OnUpdate?.Invoke();
        });
    }

    internal static bool HasNumbers(ProviderUsage u) =>
        u != null && (u.PrimaryPct.HasValue || u.WeeklyPct.HasValue);

    /// Picks the Claude reading for one pass.
    ///
    /// The OAuth endpoint outranks Claude Desktop's plan-usage-history.json when
    /// it works, for two reasons: it is live rather than up to 15 minutes stale,
    /// and it is the ONLY source that carries the 5h/weekly reset countdowns -
    /// the cache holds percentages and nothing else, which is why the device
    /// shows no reset timer while running off it. The cache remains the fallback
    /// for machines with no CLI/plugin login, and it still wins over an API
    /// error, because stale real numbers beat an error message.
    ///
    /// <param name="apiTrusted">
    /// A good API reading landed within ApiTrustWindow. On the desktop-only
    /// ticks between network passes this suppresses the cache entirely -
    /// otherwise a live reading, reset times and all, would be demoted to an
    /// older cache sample once a minute.
    /// </param>
    internal static ProviderUsage ChooseClaude(ProviderUsage api, ProviderUsage desktop,
                                               bool didNetwork, bool apiTrusted)
    {
        if (didNetwork)
        {
            if (HasNumbers(api)) return api;
            return desktop ?? api;
        }
        return apiTrusted ? null : desktop;
    }

    // MARK: - last-good cache (Settings store under %APPDATA%)

    /// Persist absolute reset instants (not minute countdowns), so a value
    /// reloaded later still resolves to a correct remaining time.
    static void SaveCache(ProviderUsage u, string key)
    {
        if ((u.PrimaryPct == null && u.WeeklyPct == null) || !u.FetchedAt.HasValue) return;
        var fetchedAt = new DateTimeOffset(u.FetchedAt.Value.ToUniversalTime()).ToUnixTimeSeconds();
        var dict = new Dictionary<string, double> { ["at"] = fetchedAt };
        if (u.PrimaryPct.HasValue) dict["pPct"] = u.PrimaryPct.Value;
        if (u.PrimaryResetMin.HasValue)
            dict["pResetAt"] = fetchedAt + u.PrimaryResetMin.Value * 60.0;
        if (u.WeeklyPct.HasValue) dict["wPct"] = u.WeeklyPct.Value;
        if (u.WeeklyResetMin.HasValue)
            dict["wResetAt"] = fetchedAt + u.WeeklyResetMin.Value * 60.0;
        if (u.Period == "month") dict["period"] = 2;
        else if (u.Period == "week") dict["period"] = 1;
        try { Settings.Set(key, JsonSerializer.Serialize(dict)); }
        catch { /* best effort */ }
    }

    static ProviderUsage LoadCache(string key)
    {
        var raw = Settings.Get(key);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(raw);
            if (dict == null) return null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var at = dict.TryGetValue("at", out var a) ? a : 0;
            if (now - at >= CacheMaxAgeSec) return null;   // drop stale cache
            var u = new ProviderUsage();
            if (dict.TryGetValue("pPct", out var pp)) u.PrimaryPct = pp;
            if (dict.TryGetValue("wPct", out var wp)) u.WeeklyPct = wp;
            if (u.PrimaryPct == null && u.WeeklyPct == null) return null;
            if (dict.TryGetValue("pResetAt", out var pr)) u.PrimaryResetMin = Math.Max(0, (int)((pr - now) / 60));
            if (dict.TryGetValue("wResetAt", out var wr)) u.WeeklyResetMin = Math.Max(0, (int)((wr - now) / 60));
            if (dict.TryGetValue("period", out var period))
                u.Period = period >= 2 ? "month" : "week";
            u.FetchedAt = DateTimeOffset.FromUnixTimeSeconds((long)at).UtcDateTime;
            return u;
        }
        catch { return null; }
    }

    static ProviderUsage Merge(ProviderUsage old, ProviderUsage fresh)
    {
        // `old` may itself be a Desktop-cache reading: that happens when the
        // cache covered an earlier OAuth failure and this pass found the cache
        // file gone too. Keeping its numbers (with DesktopCache carried over,
        // so the tray still labels them and DesktopStale can expire them) beats
        // blanking the quota out entirely, which is what excluding that case
        // used to do.
        if (!fresh.DesktopCache && !fresh.AuthenticationError
            && fresh.PrimaryPct == null && fresh.WeeklyPct == null
            && (old.PrimaryPct != null || old.WeeklyPct != null))
        {
            return new ProviderUsage
            {
                PrimaryPct = old.PrimaryPct,
                PrimaryResetMin = old.PrimaryResetMin,
                WeeklyPct = old.WeeklyPct,
                WeeklyResetMin = old.WeeklyResetMin,
                Period = old.Period,
                FetchedAt = old.FetchedAt,
                DesktopCache = old.DesktopCache,
                Error = fresh.Error,
                RateLimited = fresh.RateLimited,
                RetryAfterSeconds = fresh.RetryAfterSeconds,
            };
        }
        return fresh;
    }

    // MARK: - Grok (cli-chat-proxy.grok.com billing)

    static async Task<ProviderUsage> FetchGrok()
    {
        var usage = new ProviderUsage();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");
        var auth = GrokUsage.ReadAuth(path, DateTimeOffset.UtcNow);
        if (auth.Error != null)
        {
            usage.Error = auth.Error;
            usage.AuthenticationError = auth.AuthenticationError;
            return usage;
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, GrokUsage.BillingUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.AccessToken);
        req.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
        if (!string.IsNullOrEmpty(auth.UserId))
            req.Headers.TryAddWithoutValidation("x-userid", auth.UserId);
        req.Headers.TryAddWithoutValidation("x-grok-client-version", GrokUsage.ClientVersion);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        string body;
        int code;
        try
        {
            using var resp = await Http.Client.SendAsync(req);
            code = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync();
            if (code == 429) usage.RetryAfterSeconds = RetryAfterSeconds(resp);
            Http.ReportSuccess();
        }
        catch (Exception ex)
        {
            Http.ReportTransportFailure(ex.Message);
            usage.Error = "Grok 用量请求失败：" + ex.Message;
            return usage;
        }
        if (code == 401 || code == 403)
        {
            usage.Error = GrokUsage.ExpiredMessage;
            usage.AuthenticationError = true;
            return usage;
        }
        if (code == 429)
        {
            usage.Error = "Grok 用量接口限流";
            usage.RateLimited = true;
            return usage;
        }
        if (code < 200 || code >= 300)
        {
            usage.Error = "Grok 用量请求失败：HTTP " + code;
            return usage;
        }
        var parsed = GrokUsage.Parse(body, DateTimeOffset.UtcNow);
        parsed.RetryAfterSeconds = usage.RetryAfterSeconds;
        return parsed;
    }

    static int? RetryAfterSeconds(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is TimeSpan delta)
            return Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds));
        if (retry?.Date is DateTimeOffset date)
            return Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        return null;
    }

    // MARK: - Codex (chatgpt.com/backend-api/wham/usage)

    static async Task<ProviderUsage> FetchCodex()
    {
        var usage = new ProviderUsage();
        var creds = CodexCredentials();
        if (creds == null)
        {
            usage.Error = "未找到 Codex 登录凭据 (~/.codex/auth.json)";
            return usage;
        }
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {creds.Value.AccessToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "AIClockBridge");
        if (creds.Value.AccountId != null)
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.Value.AccountId);

        string body;
        int code;
        try
        {
            using var resp = await Http.Client.SendAsync(req);
            code = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync();
            Http.ReportSuccess();
        }
        catch (Exception ex)
        {
            Http.ReportTransportFailure(ex.Message);
            usage.Error = $"Codex 用量请求失败：{ex.Message}";
            return usage;
        }
        if (code < 200 || code > 299)
        {
            usage.Error = code == 401 || code == 403
                ? "Codex 凭据过期，运行 codex 重新登录" : $"Codex 用量接口 HTTP {code}";
            return usage;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("rate_limit", out var rateLimit))
            {
                usage.Error = "Codex 用量响应解析失败";
                return usage;
            }
            // Codex dropped the 5h window (2026-07): primary_window now carries
            // the weekly (604800s) limit and secondary_window is null. Classify
            // each window by its advertised length instead of trusting the slot.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            foreach (var (slot, fallbackSec) in new[]
                     { ("primary_window", 5.0 * 3600), ("secondary_window", 7.0 * 86400) })
            {
                if (!rateLimit.TryGetProperty(slot, out var w)
                    || w.ValueKind != JsonValueKind.Object) continue;
                var pct = NumberOrNull(w, "used_percent");
                int? resetMin = null;
                var reset = NumberOrNull(w, "reset_at");
                if (reset.HasValue) resetMin = Math.Max(0, (int)((reset.Value - now) / 60));
                var windowSec = NumberOrNull(w, "limit_window_seconds") ?? fallbackSec;
                if (windowSec >= 2 * 86400)
                {
                    if (!usage.WeeklyPct.HasValue)
                    {
                        usage.WeeklyPct = pct;
                        usage.WeeklyResetMin = resetMin;
                    }
                }
                else if (!usage.PrimaryPct.HasValue)
                {
                    usage.PrimaryPct = pct;
                    usage.PrimaryResetMin = resetMin;
                }
            }
            usage.FetchedAt = DateTime.UtcNow;
        }
        catch
        {
            usage.Error = "Codex 用量响应解析失败";
        }
        return usage;
    }

    static (string AccessToken, string AccountId)? CodexCredentials()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)
                || !tokens.TryGetProperty("access_token", out var accessEl)
                || accessEl.ValueKind != JsonValueKind.String) return null;
            var access = accessEl.GetString();
            if (string.IsNullOrEmpty(access)) return null;
            string accountId = null;
            if (tokens.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String)
                accountId = acc.GetString();
            if (accountId == null && tokens.TryGetProperty("id_token", out var idTok)
                && idTok.ValueKind == JsonValueKind.String)
                accountId = AccountIdFromJwt(idTok.GetString());
            return (access, accountId);
        }
        catch
        {
            return null;
        }
    }

    /// auth.json without a top-level account_id keeps it inside the id_token
    /// JWT claims (https://api.openai.com/auth -> chatgpt_account_id).
    static string AccountIdFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += "=";
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.TryGetProperty("chatgpt_account_id", out var id)
                && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch
        {
            // malformed JWT
        }
        return null;
    }

    // MARK: - helpers

    static double? NumberOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    static string StringOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    static int? MinutesUntil(string iso, DateTimeOffset now)
    {
        if (iso == null) return null;
        if (!DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return null;
        return Math.Max(0, (int)((d - now).TotalMinutes));
    }
}
