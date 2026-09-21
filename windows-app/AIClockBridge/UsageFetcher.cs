using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIClockBridge;

// Claude Desktop quota comes from its read-only plan-usage-history.json cache.
// If unavailable, real quota for both CLIs is fetched by reusing the OAuth tokens the
// CLIs already store locally, no extra login. On Windows both live in files
// (no Keychain):
//   Claude: %USERPROFILE%\.claude\.credentials.json, then GET
//           https://api.anthropic.com/api/oauth/usage  (5h + 7d windows)
//   Codex:  %USERPROFILE%\.codex\auth.json, then GET
//           https://chatgpt.com/backend-api/wham/usage (weekly window; older
//           accounts also had a 5h one - see the classification in FetchCodex)
// Tokens never leave this machine except toward their own vendor's API.

class ProviderUsage
{
    public double? PrimaryPct;     // 5h window used %
    public int? PrimaryResetMin;   // minutes until it resets
    public double? WeeklyPct;      // 7d / weekly window used %
    public int? WeeklyResetMin;
    public string Error;
    public DateTime? FetchedAt;
    public bool RateLimited;
    public int? RetryAfterSeconds;
    public bool AuthenticationError;
    public bool DesktopCache;

    /// Measured on a real MSIX install: Claude Desktop appends a sample every
    /// 15.00 minutes, on a fixed clock (…:11:45, :26:45, :41:45, …).
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

sealed class ClaudeCredentialInfo
{
    public string FilePath;
    public string AccessToken;
    public string RefreshToken;
    public string ClientId;
    public string[] Scopes = Array.Empty<string>();
    public DateTimeOffset? ExpiresAt;
    public DateTimeOffset? RefreshTokenExpiresAt;
}

sealed class UsageFetcher
{
    // Rebuildable: a proxy client toggled while we run would otherwise strand
    // every quota request until the process restarts. See ResilientHttpClient.
    static readonly ResilientHttpClient Http = new("usage", TimeSpan.FromSeconds(20));
    static readonly SemaphoreSlim ClaudeRefreshLock = new(1, 1);

    // Claude Code refreshes five minutes early.  Wait until the last 90 seconds
    // so an active CLI gets first chance and this bridge remains only a fallback.
    static readonly TimeSpan ClaudeRefreshWindow = TimeSpan.FromSeconds(90);
    static readonly TimeSpan ClaudeRefreshFailureBackoff = TimeSpan.FromMinutes(15);
    const string ClaudeOAuthTokenUrl = "https://platform.claude.com/v1/oauth/token";
    const string ClaudeDefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    static readonly string[] ClaudeDefaultScopes =
    {
        "user:profile", "user:inference", "user:sessions:claude_code",
        "user:mcp_servers", "user:file_upload"
    };
    static DateTimeOffset _claudeRefreshRetryAt = DateTimeOffset.MinValue;
    static string _claudeRejectedRefreshToken;

    readonly object _lock = new();
    ProviderUsage _claude = new();
    ProviderUsage _codex = new();
    System.Windows.Forms.Timer _timer;
    bool _fetching;
    readonly Gate _claudeGate = new();
    readonly Gate _codexGate = new();
    DateTime _claudeApiOkAt = DateTime.MinValue;      // last good OAuth reading

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
    const string ClaudeCacheKey = "usage_cache_claude";
    const string CodexCacheKey = "usage_cache_codex";
    static readonly double CacheMaxAgeSec = 24 * 3600;

    public ProviderUsage Claude { get { lock (_lock) return _claude; } }
    public ProviderUsage Codex { get { lock (_lock) return _codex; } }

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
            // Free local file read, so it happens on every pass and acts as the
            // floor under whatever the network can or cannot provide.
            var desktop = ClaudeDesktopUsage.Read();
            var desktopError = desktop?.Error;   // kept for the log; overwritten below
            var api = doClaude ? await FetchClaudeApi() : null;
            if (doClaude && HasNumbers(api)) _claudeApiOkAt = DateTime.UtcNow;
            var claude = ChooseClaude(api, desktop, doClaude,
                DateTime.UtcNow - _claudeApiOkAt <= ApiTrustWindow);
            // Falling back is not silent: carry the reason onto the cache
            // reading so the tray says why the live source went away instead of
            // just showing an older timestamp.
            if (claude is { DesktopCache: true } && api?.Error != null)
                claude.Error = claude.Error == null ? api.Error
                    : $"{claude.Error}；{api.Error}";
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
            // Log the OAuth failure itself, not the reading that replaced it.
            // `claude` is the Desktop cache object on a fallback pass and its
            // Error is normally null, so every fallback used to be invisible -
            // no log line, no way to tell a stuck quota from an unchanged one.
            if (api?.Error != null)
                Log.Write($"[usage] claude: {api.Error}"
                    + (claude is { DesktopCache: true } ? "（改用桌面缓存）" : ""));
            if (desktopError != null) Log.Write($"[usage] claude 桌面缓存: {desktopError}");
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
                FetchedAt = old.FetchedAt,
                DesktopCache = old.DesktopCache,
                Error = fresh.Error,
                RateLimited = fresh.RateLimited,
                RetryAfterSeconds = fresh.RetryAfterSeconds,
            };
        }
        return fresh;
    }

    // MARK: - Claude (api.anthropic.com/api/oauth/usage)

    /// CLI-OAuth fallback. Refresh() reads the Desktop cache first and only
    /// calls this when there is none, so this method never touches that cache.
    static async Task<ProviderUsage> FetchClaudeApi()
    {
        var usage = new ProviderUsage();
        var credential = ClaudeCredential();
        if (credential == null)
        {
            // The Desktop cache is consulted separately now, so this branch only
            // means "no OAuth credentials" - it is not fatal on its own.
            usage.Error = "未找到 CLI/插件凭据（~/.claude/.credentials.json），回退到桌面版缓存";
            usage.AuthenticationError = true;
            return usage;
        }

        var refresh = await RefreshClaudeCredentialIfNeeded(credential);
        if (refresh.Credential != null) credential = refresh.Credential;
        if (refresh.Error != null)
        {
            var expired = credential.ExpiresAt.HasValue
                && credential.ExpiresAt.Value <= DateTimeOffset.UtcNow;
            if (expired || refresh.AuthenticationError || refresh.RateLimited)
            {
                usage.Error = refresh.Error;
                usage.AuthenticationError = refresh.AuthenticationError;
                usage.RateLimited = refresh.RateLimited;
                usage.RetryAfterSeconds = refresh.RetryAfterSeconds;
                return usage;
            }
        }
        if (credential.ExpiresAt.HasValue
            && credential.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            usage.Error = "Claude 凭据已过期，请运行 claude auth login 重新登录";
            usage.AuthenticationError = true;
            return usage;
        }
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.anthropic.com/api/oauth/usage");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");

        string body;
        int code;
        try
        {
            using var resp = await Http.Client.SendAsync(req);
            code = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync();
            if (code == 429) usage.RetryAfterSeconds = RetryAfterSeconds(resp);
            Http.ReportSuccess();   // a 401/429 still proves the path works
        }
        catch (Exception ex)
        {
            Http.ReportTransportFailure(ex.Message);
            usage.Error = $"Claude 用量请求失败：{ex.Message}";
            return usage;
        }
        if (code != 200)
        {
            usage.RateLimited = code == 429;
            usage.AuthenticationError = code == 401 || code == 403;
            usage.Error = code == 401 ? "Claude 凭据过期，运行 claude 重新登录"
                : code == 429 ? "Claude 用量接口限流，稍后自动重试"
                : $"Claude 用量接口 HTTP {code}";
            return usage;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var now = DateTimeOffset.UtcNow;
            if (doc.RootElement.TryGetProperty("five_hour", out var fiveHour))
            {
                usage.PrimaryPct = NumberOrNull(fiveHour, "utilization");
                usage.PrimaryResetMin = MinutesUntil(StringOrNull(fiveHour, "resets_at"), now);
            }
            if (doc.RootElement.TryGetProperty("seven_day", out var sevenDay))
            {
                usage.WeeklyPct = NumberOrNull(sevenDay, "utilization");
                usage.WeeklyResetMin = MinutesUntil(StringOrNull(sevenDay, "resets_at"), now);
            }
            usage.FetchedAt = DateTime.UtcNow;
        }
        catch
        {
            usage.Error = "Claude 用量响应解析失败";
        }
        return usage;
    }

    /// Claude Code on Windows stores OAuth credentials as a plain JSON file:
    /// {"claudeAiOauth":{"accessToken":…}}
    static ClaudeCredentialInfo ClaudeCredential()
    {
        var credFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(credFile));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                var t = token.GetString();
                if (string.IsNullOrEmpty(t)) return null;
                string refreshToken = null;
                if (oauth.TryGetProperty("refreshToken", out var refresh)
                    && refresh.ValueKind == JsonValueKind.String)
                    refreshToken = refresh.GetString();
                string clientId = null;
                if (oauth.TryGetProperty("clientId", out var client)
                    && client.ValueKind == JsonValueKind.String)
                    clientId = client.GetString();
                var scopes = Array.Empty<string>();
                if (oauth.TryGetProperty("scopes", out var scopeElement)
                    && scopeElement.ValueKind == JsonValueKind.Array)
                    scopes = scopeElement.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .ToArray();
                return new ClaudeCredentialInfo
                {
                    FilePath = credFile,
                    AccessToken = t,
                    RefreshToken = refreshToken,
                    ClientId = clientId,
                    Scopes = scopes,
                    ExpiresAt = EpochMilliseconds(oauth, "expiresAt"),
                    RefreshTokenExpiresAt = EpochMilliseconds(oauth, "refreshTokenExpiresAt")
                };
            }
        }
        catch
        {
            // missing / unreadable
        }
        return null;
    }

    static DateTimeOffset? EpochMilliseconds(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value)) return null;
        long epochMs;
        if (!((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out epochMs))
              || (value.ValueKind == JsonValueKind.String
                  && long.TryParse(value.GetString(), out epochMs)))) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(epochMs); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static async Task<(ClaudeCredentialInfo Credential, string Error,
        bool AuthenticationError, bool RateLimited, int? RetryAfterSeconds)>
        RefreshClaudeCredentialIfNeeded(ClaudeCredentialInfo credential)
    {
        var now = DateTimeOffset.UtcNow;
        if (!credential.ExpiresAt.HasValue
            || credential.ExpiresAt.Value > now + ClaudeRefreshWindow)
            return (credential, null, false, false, null);

        if (string.IsNullOrEmpty(credential.RefreshToken))
            return (credential, "Claude 凭据已过期且没有 refresh token，请运行 claude auth login", true, false, null);
        if (credential.RefreshTokenExpiresAt.HasValue
            && credential.RefreshTokenExpiresAt.Value <= now)
            return (credential, "Claude refresh token 已过期，请运行 claude auth login", true, false, null);
        if (credential.RefreshToken == _claudeRejectedRefreshToken)
            return (credential, "Claude refresh token 已被服务端拒绝，请运行 claude auth login", true, false, null);
        if (now < _claudeRefreshRetryAt)
            return (credential, "Claude 凭据自动刷新暂时失败，稍后再试", false, false,
                Math.Max(1, (int)Math.Ceiling((_claudeRefreshRetryAt - now).TotalSeconds)));

        await ClaudeRefreshLock.WaitAsync();
        try
        {
            // Another refresh (or claude CLI) may have completed while waiting.
            var latest = ClaudeCredential();
            if (latest == null) return (credential, "Claude 凭据文件无法读取", true, false, null);
            credential = latest;
            now = DateTimeOffset.UtcNow;
            if (!credential.ExpiresAt.HasValue
                || credential.ExpiresAt.Value > now + ClaudeRefreshWindow)
                return (credential, null, false, false, null);
            if (string.IsNullOrEmpty(credential.RefreshToken))
                return (credential, "Claude 凭据已过期且没有 refresh token，请运行 claude auth login", true, false, null);
            if (credential.RefreshTokenExpiresAt.HasValue
                && credential.RefreshTokenExpiresAt.Value <= now)
                return (credential, "Claude refresh token 已过期，请运行 claude auth login", true, false, null);
            if (credential.RefreshToken == _claudeRejectedRefreshToken)
                return (credential, "Claude refresh token 已被服务端拒绝，请运行 claude auth login", true, false, null);
            if (now < _claudeRefreshRetryAt)
                return (credential, "Claude 凭据自动刷新暂时失败，稍后再试", false, false,
                    Math.Max(1, (int)Math.Ceiling((_claudeRefreshRetryAt - now).TotalSeconds)));

            // Do not rotate a refresh token unless the credential file is
            // writable; a successful refresh may invalidate the old token.
            try
            {
                using var writable = new FileStream(credential.FilePath, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex)
            {
                return (credential, $"Claude 凭据文件不可写：{ex.Message}", false, false, null);
            }

            var scopes = credential.Scopes.Length > 0 ? credential.Scopes : ClaudeDefaultScopes;
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.RefreshToken,
                ["client_id"] = string.IsNullOrEmpty(credential.ClientId)
                    ? ClaudeDefaultClientId : credential.ClientId,
                ["scope"] = string.Join(" ", scopes)
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeOAuthTokenUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Content = new StringContent(payload, Encoding.UTF8);
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

            HttpResponseMessage response;
            string body;
            try
            {
                response = await Http.Client.SendAsync(request);
                body = await response.Content.ReadAsStringAsync();
                Http.ReportSuccess();
            }
            catch (Exception ex)
            {
                Http.ReportTransportFailure(ex.Message);
                _claudeRefreshRetryAt = DateTimeOffset.UtcNow + ClaudeRefreshFailureBackoff;
                return (credential, $"Claude 凭据自动刷新失败：{ex.Message}", false, false,
                    (int)ClaudeRefreshFailureBackoff.TotalSeconds);
            }
            using (response)
            {
                var code = (int)response.StatusCode;
                if (code < 200 || code > 299)
                {
                    var description = OAuthErrorDescription(body);
                    var authError = code == 400 || code == 401 || code == 403;
                    if (authError) _claudeRejectedRefreshToken = credential.RefreshToken;
                    var retryAfter = code == 429 ? RetryAfterSeconds(response) : null;
                    var delay = retryAfter.HasValue
                        ? TimeSpan.FromSeconds(Math.Max(retryAfter.Value,
                            (int)ClaudeRefreshFailureBackoff.TotalSeconds))
                        : ClaudeRefreshFailureBackoff;
                    _claudeRefreshRetryAt = DateTimeOffset.UtcNow + delay;
                    var message = authError
                        ? $"Claude 凭据自动刷新被拒绝{description}，请运行 claude auth login"
                        : code == 429
                            ? "Claude 凭据刷新接口限流，稍后自动重试"
                            : $"Claude 凭据自动刷新 HTTP {code}";
                    return (credential, message, authError, code == 429,
                        code == 429 ? (int)delay.TotalSeconds : null);
                }
            }

            ClaudeCredentialInfo refreshed;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out var accessElement)
                    || accessElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(accessElement.GetString())
                    || !root.TryGetProperty("expires_in", out var expiresElement)
                    || expiresElement.ValueKind != JsonValueKind.Number
                    || !expiresElement.TryGetDouble(out var expiresIn)
                    || expiresIn <= 0)
                    throw new JsonException("missing access_token or expires_in");

                var refreshToken = credential.RefreshToken;
                if (root.TryGetProperty("refresh_token", out var refreshElement)
                    && refreshElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(refreshElement.GetString()))
                    refreshToken = refreshElement.GetString();
                var refreshedScopes = scopes;
                if (root.TryGetProperty("scope", out var returnedScope)
                    && returnedScope.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(returnedScope.GetString()))
                    refreshedScopes = returnedScope.GetString()
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                DateTimeOffset? refreshExpiresAt = credential.RefreshTokenExpiresAt;
                if (root.TryGetProperty("refresh_token_expires_in", out var refreshExpires)
                    && refreshExpires.ValueKind == JsonValueKind.Number
                    && refreshExpires.TryGetDouble(out var refreshExpiresIn)
                    && refreshExpiresIn > 0)
                    refreshExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshExpiresIn);

                refreshed = new ClaudeCredentialInfo
                {
                    FilePath = credential.FilePath,
                    AccessToken = accessElement.GetString(),
                    RefreshToken = refreshToken,
                    ClientId = credential.ClientId,
                    Scopes = refreshedScopes,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                    RefreshTokenExpiresAt = refreshExpiresAt
                };
            }
            catch
            {
                _claudeRefreshRetryAt = DateTimeOffset.UtcNow + ClaudeRefreshFailureBackoff;
                return (credential, "Claude 凭据刷新响应解析失败", false, false,
                    (int)ClaudeRefreshFailureBackoff.TotalSeconds);
            }

            try
            {
                var persisted = PersistClaudeCredential(
                    credential.AccessToken, credential.RefreshToken, refreshed);
                if (persisted != null) refreshed = persisted;
            }
            catch (Exception ex)
            {
                _claudeRefreshRetryAt = DateTimeOffset.UtcNow + ClaudeRefreshFailureBackoff;
                return (credential, $"Claude 新凭据写入失败：{ex.Message}", true, false, null);
            }

            _claudeRejectedRefreshToken = null;
            _claudeRefreshRetryAt = DateTimeOffset.MinValue;
            return (refreshed, null, false, false, null);
        }
        finally
        {
            ClaudeRefreshLock.Release();
        }
    }

    static ClaudeCredentialInfo PersistClaudeCredential(string postedAccessToken,
        string postedRefreshToken, ClaudeCredentialInfo refreshed)
    {
        var root = JsonNode.Parse(File.ReadAllText(refreshed.FilePath)) as JsonObject;
        var oauth = root?["claudeAiOauth"] as JsonObject;
        if (root == null || oauth == null) throw new JsonException("凭据文件结构无效");
        var currentAccessToken = oauth["accessToken"]?.GetValue<string>();
        var currentRefreshToken = oauth["refreshToken"]?.GetValue<string>();
        // If Claude CLI won the race, use its newer credentials and never
        // overwrite them with this response.
        if (!string.Equals(currentAccessToken, postedAccessToken, StringComparison.Ordinal)
            || !string.Equals(currentRefreshToken, postedRefreshToken, StringComparison.Ordinal))
            return ClaudeCredential();

        oauth["accessToken"] = refreshed.AccessToken;
        oauth["refreshToken"] = refreshed.RefreshToken;
        if (refreshed.ExpiresAt.HasValue)
            oauth["expiresAt"] = refreshed.ExpiresAt.Value.ToUnixTimeMilliseconds();
        if (refreshed.RefreshTokenExpiresAt.HasValue)
            oauth["refreshTokenExpiresAt"] =
                refreshed.RefreshTokenExpiresAt.Value.ToUnixTimeMilliseconds();
        var scopeArray = new JsonArray();
        foreach (var scope in refreshed.Scopes) scopeArray.Add(scope);
        oauth["scopes"] = scopeArray;

        var temp = refreshed.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }), new UTF8Encoding(false));
            var latest = ClaudeCredential();
            if (latest == null) throw new IOException("凭据文件无法重新读取");
            if (!string.Equals(latest.AccessToken, postedAccessToken, StringComparison.Ordinal)
                || !string.Equals(latest.RefreshToken, postedRefreshToken, StringComparison.Ordinal))
                return latest;
            File.Replace(temp, refreshed.FilePath, null, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        return refreshed;
    }

    static string OAuthErrorDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = StringOrNull(doc.RootElement, "error");
            var description = StringOrNull(doc.RootElement, "error_description");
            var text = !string.IsNullOrWhiteSpace(description) ? description : error;
            return string.IsNullOrWhiteSpace(text) ? "" : $"（{text}）";
        }
        catch { return ""; }
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
