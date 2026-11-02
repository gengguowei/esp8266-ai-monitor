using System.Net;
using System.Text.Json;

namespace AIClockBridge;

sealed record UpScreen(int Revision, byte[] Pixels, int Page, int Pages);

sealed class BilibiliMonitor : IDisposable
{
    const string UidKey = "bilibili_uid";
    const string DefaultUid = "39627524";
    readonly object _lock = new();
    readonly CancellationTokenSource _stop = new();
    // Domestic public API: one stable direct path, no rotating proxy or
    // alternate-path retry after risk control. Other providers keep their proxy.
    readonly ResilientHttpClient _transport = new("bilibili", TimeSpan.FromSeconds(20), useSystemProxy: false);
    readonly HttpClient _injectedHttp;
    readonly Func<DateTimeOffset> _clock;
    readonly string _directory;
    readonly bool _saveSettings;
    readonly BilibiliSessionStore _sessionStore;
    public IBilibiliVideoBrowser VideoBrowser { get; set; }
    public bool HasSession => _sessionStore.Load() != null;
    readonly TimeSpan _requestSpacing;
    readonly Func<double> _jitter;
    Cache _cache;
    string _uid;
    bool _active, _fetching;
    System.Threading.Timer _timer;
    Task _activationTask;
    byte[][] _screens;
    int _revision = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    DateTimeOffset _renderedAt, _lastRequest;
    string _wbiKey;
    DateTimeOffset _wbiFetchedAt;

    public sealed class Cache
    {
        public Dictionary<string, BilibiliProfile> Profiles { get; set; } = new();
        public Dictionary<string, DateTimeOffset> NextRefresh { get; set; } = new();
        public DateTimeOffset NextAllowed { get; set; }
        public DateTimeOffset SwitchAllowed { get; set; }
        public int GateVersion { get; set; }
        public DateTimeOffset HardCooldownUntil { get; set; }
        public string HardCooldownReason { get; set; }
        public string LastFailureEndpoint { get; set; }
        public int Failures { get; set; }
    }

    sealed class ApiFailure : Exception
    {
        public bool Risk { get; }
        public DateTimeOffset? RetryAt { get; }
        public string Endpoint { get; }
        public ApiFailure(string message, bool risk = false, DateTimeOffset? retryAt = null, string endpoint = null) : base(message)
        { Risk = risk; RetryAt = retryAt; Endpoint = endpoint; }
    }

    public BilibiliMonitor(HttpClient http = null, string directory = null, string uid = null,
        Func<DateTimeOffset> clock = null, TimeSpan? requestSpacing = null, Func<double> jitter = null, BilibiliSessionStore sessionStore = null)
    {
        _injectedHttp = http;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _requestSpacing = requestSpacing ?? BilibiliPollPolicy.RequestSpacing;
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
        _saveSettings = directory == null;
        _sessionStore = sessionStore ?? new BilibiliSessionStore(directory == null ? null : Path.Combine(directory, "session"));
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIClockBridge", "bilibili");
        var configured = uid ?? (_saveSettings ? Settings.Get(UidKey) : DefaultUid);
        _uid = BilibiliData.TryUid(configured, out var valid) ? valid : DefaultUid;
        try { _cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) ?? new(); }
        catch { _cache = new(); }
        _cache.Profiles ??= new(); _cache.NextRefresh ??= new();
        // Unclassified old gates affect automatic polling only. Version 1
        // incorrectly promoted them to hard cooldowns; undo that promotion
        // while keeping explicitly recorded risk/Retry-After restrictions.
        if (_cache.GateVersion < 2 && _cache.HardCooldownReason == "旧版保存的接口冷却")
        {
            _cache.HardCooldownUntil = default;
            _cache.HardCooldownReason = null;
        }
        _cache.GateVersion = 2;
        RebuildLocked();
    }

    string CachePath => Path.Combine(_directory, "cache.json");
    public string Uid { get { lock (_lock) return _uid; } }
    public string Error { get; private set; }
    public bool IsFetching { get { lock (_lock) return _fetching; } }
    public string SourceWarning => Snapshot.SourceWarning;
    public string SourcesSummary
    {
        get { var p = Snapshot; return $"视频：{(p.Video == null ? "尚未获取" : p.VideoSource)} · 资料：{(string.IsNullOrEmpty(p.Name) ? "尚未获取" : p.IdentitySource)} · 粉丝：{(p.Followers.HasValue ? p.FansSource : "尚未获取")}"; }
    }
    BilibiliProfile ProfileLocked() => _cache.Profiles.TryGetValue(_uid, out var data) ? data :
        new(_uid, "", "", null, null, null, null);
    public BilibiliProfile Snapshot { get { lock (_lock) return ProfileLocked(); } }
    public DateTimeOffset NextRefreshAt
    {
        get
        {
            lock (_lock)
            {
                _cache.NextRefresh.TryGetValue(_uid, out var next);
                return new[] { next, _cache.NextAllowed, _cache.SwitchAllowed, _cache.HardCooldownUntil }.Max();
            }
        }
    }

    public void Start() => _timer ??= new System.Threading.Timer(_ => _ = RefreshIfDue(), null,
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    // Local display/menu reads never force a refresh or bypass the persisted gate.
    public Task Activate()
    {
        lock (_lock)
        {
            _active = true;
            return _activationTask ??= Task.Run(async () =>
            {
                try { await RefreshIfDue(); }
                finally { lock (_lock) _activationTask = null; }
            });
        }
    }
    public Task<string> FetchNow()
    {
        lock (_lock) _active = true;
        return Task.Run(() => RefreshIfDue(userInitiated: true));
    }
    public void UseVerifiedSession(BilibiliSession session)
    {
        lock (_lock)
        {
            if (_fetching) throw new InvalidOperationException("正在取数，请结束后保存登录会话");
            var changed = !BilibiliSessionStore.SameLogin(_sessionStore.Load(), session);
            _sessionStore.Save(session);
            _wbiKey = null;
            if (changed && !(_cache.HardCooldownReason ?? "").Contains("Retry-After"))
            {
                // One newly verified login is a new request context. Re-saving
                // the same login cannot repeatedly clear a real risk response.
                _cache.HardCooldownUntil = default; _cache.HardCooldownReason = null;
                _cache.NextAllowed = default; _cache.Failures = 0; Error = null;
                _cache.NextRefresh[_uid] = _clock();
            }
            SaveLocked();
        }
    }
    public bool SetUid(string input)
    {
        if (!BilibiliData.TryUid(input, out var uid)) return false;
        lock (_lock)
        {
            if (_uid == uid) return true;
            _uid = uid; Error = null;
            if (_saveSettings) Settings.Set(UidKey, uid);
            RebuildLocked();
        }
        _ = Activate();
        return true;
    }

    public UpScreen Screen
    {
        get
        {
            lock (_lock)
            {
                // Hold each title page for a minute, comfortably longer than a
                // serial transfer. Long titles stay complete rather than shrink.
                int page = (int)(Math.Max(0, (_clock() - _renderedAt).TotalSeconds) / 60) % _screens.Length;
                return new(_revision + page, _screens[page], page, _screens.Length);
            }
        }
    }
    public byte[] ToJson()
    {
        lock (_lock)
        {
            var frame = Screen;
            return JsonSerializer.SerializeToUtf8Bytes(new { rev = frame.Revision, uid = _uid,
                width = 240, height = 240, page = frame.Page, pages = frame.Pages });
        }
    }
    public byte[] RawScreen()
    {
        var frame = Screen;
        var packet = new byte[4 + frame.Pixels.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, frame.Revision);
        frame.Pixels.CopyTo(packet, 4);
        return packet;
    }

    void RebuildLocked()
    {
        byte[] avatar = null;
        try { avatar = File.ReadAllBytes(Path.Combine(_directory, _uid + "-avatar.bin")); } catch { }
        var rendered = BilibiliScreen.Render(ProfileLocked(), avatar);
        if (_screens != null && _screens.Length == rendered.Length &&
            _screens.Zip(rendered).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second))) return;
        _screens = rendered;
        // Stable pixel identity avoids retransmitting unchanged pages and
        // collisions when a restarted bridge reuses a second-based revision.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(_uid));
        foreach (var pixels in rendered) hash.AppendData(pixels);
        _revision = Math.Max(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hash.GetHashAndReset()) & 0x7fffffc0);
        _renderedAt = _clock();
    }

    void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception e) { Log.Write($"[bilibili] cache save: {e.Message}"); }
    }

    bool CanTryBackup(Exception e) => !_stop.IsCancellationRequested &&
        (e is not ApiFailure failure || (!failure.Risk && !failure.RetryAt.HasValue));

    async Task<BilibiliVideo> GetPrimaryVideo(string uid)
    {
        if (VideoBrowser?.HasSession == true)
        {
            try { return await VideoBrowser.LatestVideo(uid, _stop.Token); }
            catch (BilibiliBrowserFailure e)
            { throw new ApiFailure(e.Message, e.Risk, e.RetryAt, "/x/space/wbi/arc/search"); }
        }
        if (_wbiKey == null || _clock() - _wbiFetchedAt >= TimeSpan.FromHours(6))
        {
            var nav = await GetJson("https://api.bilibili.com/x/web-interface/nav", uid, guestNav: true);
            var images = nav.GetProperty("wbi_img");
            _wbiKey = BilibiliData.WbiKey(BilibiliData.Text(images, "img_url"), BilibiliData.Text(images, "sub_url"));
            _wbiFetchedAt = _clock();
        }
        var query = BilibiliData.Sign(new Dictionary<string, string>
        { ["mid"] = uid, ["pn"] = "1", ["ps"] = "5", ["order"] = "pubdate", ["platform"] = "web" },
            _wbiKey, _clock().ToUnixTimeSeconds());
        return BilibiliData.LatestVideo(await GetJson("https://api.bilibili.com/x/space/wbi/arc/search?" + query, uid), uid);
    }

    public async Task<string> RefreshIfDue(bool userInitiated = false)
    {
        string uid; BilibiliProfile profile;
        lock (_lock)
        {
            if (!_active || _stop.IsCancellationRequested) return "尚未启用获取";
            if (_fetching) return "正在获取，请稍候";
            // Manual intent overrides our automatic schedule, switch floor and
            // ordinary failure backoff. Only an API/risk gate blocks a click.
            var allowed = userInitiated ? _cache.HardCooldownUntil : NextRefreshAt;
            if (_clock() < allowed)
                return userInitiated ? $"{_cache.HardCooldownReason ?? "接口风控冷却"}，最早 {allowed.ToLocalTime():MM-dd HH:mm} 可重试" :
                    $"尚未到自动获取时间，下次 {allowed.ToLocalTime():MM-dd HH:mm}";
            _fetching = true; uid = _uid; profile = ProfileLocked();
            _cache.SwitchAllowed = _clock() + BilibiliPollPolicy.SwitchFloor;
            _cache.NextRefresh[uid] = _clock() + BilibiliPollPolicy.SuccessDelay(_jitter());
            SaveLocked();
        }
        var updated = profile;
        var problems = new List<string>(); var warnings = new List<string>();
        Task<JsonElement> cardTask = null;
        Task<JsonElement> Card() => cardTask ??= GetJson("https://api.bilibili.com/x/web-interface/card?mid=" + uid, uid);
        BilibiliDynamic dynamic = null;
        var result = "已获取 UP主数据";
        try
        {
            BilibiliVideo latest = null; var videoSource = "视频列表";
            try { latest = await GetPrimaryVideo(uid); }
            catch (Exception e) when (CanTryBackup(e))
            {
                Log.Write($"[bilibili] video primary failed: {e.Message}; trying desktop dynamic once");
                try
                {
                    dynamic = HasSession ? BilibiliData.WebDynamic(await GetJson(
                        "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?host_mid=" + uid + "&platform=web&features=itemOpusStyle", uid), uid) :
                        BilibiliData.DesktopDynamic(await GetJson(
                        "https://api.bilibili.com/x/polymer/web-dynamic/desktop/v1/feed/space?host_mid=" + uid + "&platform=web&features=itemOpusStyle", uid), uid);
                    latest = dynamic.Video; videoSource = "动态备用";
                    warnings.Add("动态备用可能缺少充电专属或未发动态的视频");
                    if (latest == null) problems.Add("视频主源失败，备用动态中没有视频");
                }
                catch (Exception backup) when (CanTryBackup(backup)) { problems.Add("视频主源与备用源均失败：" + backup.Message); }
            }
            if (latest != null)
            {
                if (profile.Video != null && latest.Published < profile.Video.Published)
                    warnings.Add("备用或主源结果早于缓存，保留上次视频");
                else updated = updated with { Video = latest, Name = latest.Author, VideoCheckedAt = _clock(), VideoSource = VideoBrowser?.HasSession == true && videoSource == "视频列表" ? "登录浏览器投稿页" : videoSource };
            }

            // Fetch each field group independently: an ordinary video/profile
            // failure must not prevent a still-healthy fans source from working.
            if (userInitiated || updated.Video?.Bvid != profile.Video?.Bvid || string.IsNullOrEmpty(profile.Avatar) ||
                !File.Exists(Path.Combine(_directory, uid + "-avatar.bin")))
            {
                BilibiliIdentity identity = dynamic?.Identity;
                var identitySource = identity != null ? "动态作者备用" : "视频作者";
                if (identity == null && HasSession)
                {
                    try { identity = BilibiliData.CardIdentity(await Card(), uid); identitySource = "登录用户名片"; }
                    catch (Exception e) when (CanTryBackup(e)) { }
                }
                if (identity == null)
                {
                    try
                    {
                        if (updated.Video == null) throw new InvalidDataException("暂无可用视频作者信息");
                        var view = await GetJson("https://api.bilibili.com/x/web-interface/view?bvid=" + Uri.EscapeDataString(updated.Video.Bvid), uid);
                        identity = BilibiliData.Identity(view.GetProperty("owner"), uid);
                    }
                    catch (Exception e) when (CanTryBackup(e))
                    {
                        try { identity = BilibiliData.CardIdentity(await Card(), uid); identitySource = "用户名片备用"; }
                        catch (Exception backup) when (CanTryBackup(backup)) { problems.Add("作者主源与备用源均失败：" + backup.Message); }
                    }
                }
                if (identity != null)
                {
                    updated = updated with { Name = identity.Name, IdentitySource = identitySource };
                    if (identitySource.Contains("备用")) warnings.Add("资料使用" + identitySource);
                    try
                    {
                        if (identity.Avatar != profile.Avatar || !File.Exists(Path.Combine(_directory, uid + "-avatar.bin")))
                        {
                            if (!Uri.TryCreate(identity.Avatar, UriKind.Absolute, out var avatarUri) || avatarUri.Scheme != "https" ||
                                !avatarUri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("头像地址格式变化");
                            var pixels = await GetBytes(identity.Avatar, uid);
                            using (var stream = new MemoryStream(pixels))
                            using (var image = System.Drawing.Image.FromStream(stream))
                                if (image.Width <= 0 || image.Height <= 0 || image.Width > 4096 || image.Height > 4096) throw new InvalidDataException("头像尺寸无效");
                            Directory.CreateDirectory(_directory);
                            var avatarPath = Path.Combine(_directory, uid + "-avatar.bin");
                            File.WriteAllBytes(avatarPath + ".tmp", pixels);
                            File.Move(avatarPath + ".tmp", avatarPath, overwrite: true);
                        }
                        updated = updated with { Avatar = identity.Avatar };
                    }
                    catch (Exception e) when (CanTryBackup(e)) { problems.Add("头像下载失败，保留已有头像：" + e.Message); }
                }
            }
            if (userInitiated || !profile.FansCheckedAt.HasValue || _clock() - profile.FansCheckedAt.Value >= BilibiliPollPolicy.FansInterval)
            {
                try
                {
                    long followers = -1; var source = "粉丝统计";
                    if (cardTask?.IsCompletedSuccessfully == true)
                    {
                        try { followers = BilibiliData.CardFollowers(await cardTask, uid); source = HasSession ? "登录用户名片" : "用户名片备用"; }
                        catch (InvalidDataException) { }
                    }
                    if (followers < 0)
                    {
                        source = "粉丝统计";
                        try
                        {
                            var fans = await GetJson("https://api.bilibili.com/x/relation/stat?vmid=" + uid, uid);
                            followers = BilibiliData.Number(fans, "follower", -1);
                            if (followers < 0 || BilibiliData.Number(fans, "mid").ToString() != uid) throw new InvalidDataException("粉丝统计格式变化");
                        }
                        catch (Exception e) when (CanTryBackup(e))
                        { followers = BilibiliData.CardFollowers(await Card(), uid); source = "用户名片备用"; }
                    }
                    updated = updated with { Followers = followers, FansCheckedAt = _clock(), FansSource = source };
                    if (source.Contains("备用")) warnings.Add("粉丝使用" + source);
                }
                catch (Exception e) when (CanTryBackup(e)) { problems.Add("粉丝主源与备用源均失败：" + e.Message); }
            }
            updated = updated with { SourceWarning = warnings.Count == 0 ? null : string.Join("；", warnings.Distinct()) };
            if (problems.Count > 0) throw new InvalidDataException(string.Join("；", problems));
            lock (_lock)
            {
                _cache.Failures = 0; _cache.NextAllowed = default;
                _cache.HardCooldownUntil = default; _cache.HardCooldownReason = null;
                _cache.LastFailureEndpoint = null;
                _cache.NextRefresh[uid] = _clock() + BilibiliPollPolicy.SuccessDelay(_jitter());
                if (_uid == uid) Error = null;
            }
            if (warnings.Count > 0) result = "已获取（含备用来源），详情见右键菜单";
            Log.Write($"[bilibili] UID {uid} latest={updated.Video?.Bvid}; sources={updated.VideoSource}/{updated.IdentitySource}/{updated.FansSource}; next video check in 60–70 min");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { result = "获取已取消"; }
        catch (Exception e)
        {
            var failure = e as ApiFailure;
            lock (_lock)
            {
                _cache.Failures++;
                var until = _clock() + BilibiliPollPolicy.FailureDelay(_cache.Failures, failure?.Risk == true);
                if (failure?.RetryAt > until) until = failure.RetryAt.Value;
                _cache.NextAllowed = until; _cache.NextRefresh[uid] = until;
                _cache.LastFailureEndpoint = failure?.Endpoint;
                var hard = failure?.Risk == true || failure?.RetryAt.HasValue == true;
                _cache.HardCooldownUntil = hard ? until : default;
                _cache.HardCooldownReason = hard ? failure?.RetryAt.HasValue == true ?
                    "接口要求暂缓请求（Retry-After）" : "接口已返回风控或验证响应" : null;
                if (_uid == uid) Error = e.Message;
                result = hard ? $"获取失败：{e.Message}；{_cache.HardCooldownReason}，最早 {until.ToLocalTime():MM-dd HH:mm} 重试" :
                    $"获取失败：{e.Message}；保留缓存，可手动重试；下次自动获取 {until.ToLocalTime():MM-dd HH:mm}";
            }
            Log.Write($"[bilibili] UID {uid}; endpoint={failure?.Endpoint ?? "data/transport"}: {e.Message}; cooling down until {NextRefreshAt:o}");
        }
        finally
        {
            lock (_lock)
            {
                if (updated != profile) _cache.Profiles[uid] = updated;
                try { if (_uid == uid && updated != profile) RebuildLocked(); }
                catch (Exception e) { Log.Write($"[bilibili] render: {e.Message}"); }
                finally { SaveLocked(); _fetching = false; }
            }
        }
        return result;
    }

    async Task<byte[]> GetBytes(string url, string uid)
    {
        var wait = _lastRequest + _requestSpacing - _clock();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _stop.Token);
        _lastRequest = _clock();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var endpoint = request.RequestUri.AbsolutePath;
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var session = _sessionStore.Load();
        request.Headers.UserAgent.ParseAdd(session?.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        var cookie = BilibiliSessionStore.HeaderFor(session, request.RequestUri);
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Referrer = new Uri($"https://space.bilibili.com/{uid}/");
        try
        {
            using var response = await (_injectedHttp ?? _transport.Client).SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
                if (response.Headers.RetryAfter?.Delta is TimeSpan delta) retryAt = _clock() + delta;
                Log.Write($"[bilibili] endpoint={endpoint}; HTTP {code}; UID {uid}");
                throw new ApiFailure($"{ApiName(endpoint)}返回 HTTP {code}", BilibiliPollPolicy.IsRisk(code), retryAt, endpoint);
            }
            if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidDataException("接口响应过大");
            var bytes = await response.Content.ReadAsByteArrayAsync(requestTimeout.Token);
            if (bytes.Length > 2_000_000) throw new InvalidDataException("接口响应过大");
            _transport.ReportSuccess();
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { _transport.ReportTransportFailure(e.Message); throw; }
    }

    async Task<JsonElement> GetJson(string url, string uid, bool guestNav = false)
    {
        var endpoint = new Uri(url).AbsolutePath;
        var bytes = await GetBytes(url, uid);
        // A gateway/captcha HTML response is not a healthy JSON endpoint;
        // stop the entire cycle rather than probing another Bilibili route.
        if (System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 200))).TrimStart().StartsWith('<'))
            throw new ApiFailure($"{ApiName(endpoint)}返回验证页面", true, endpoint: endpoint);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        int code = (int)BilibiliData.Number(root, "code", int.MinValue);
        Log.Write($"[bilibili] endpoint={endpoint}; API {code}; UID {uid}");
        if (code != 0 && !(guestNav && code == -101 && root.TryGetProperty("data", out var navData)
            && navData.ValueKind == JsonValueKind.Object && navData.TryGetProperty("wbi_img", out _)))
            throw new ApiFailure($"{ApiName(endpoint)}错误 {code}：{BilibiliData.Text(root, "message")}", BilibiliPollPolicy.IsRisk(code), endpoint: endpoint);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("接口未返回有效数据");
        // Some WBI blocks return code=0 with a voucher, without real data.
        if (data.TryGetProperty("v_voucher", out _) ||
            (data.TryGetProperty("is_risk", out var risk) && risk.ValueKind == JsonValueKind.True))
            throw new ApiFailure($"{ApiName(endpoint)}返回风控凭据，未提供有效数据", true, endpoint: endpoint);
        return data.Clone();
    }

    static string ApiName(string endpoint) => endpoint switch
    {
        "/x/space/wbi/arc/search" => "视频列表接口",
        "/x/web-interface/nav" => "签名素材接口",
        "/x/web-interface/card" => "用户名片接口",
        "/x/relation/stat" => "粉丝统计接口",
        "/x/web-interface/view" => "视频详情接口",
        _ when endpoint.Contains("feed/space") => "动态接口",
        _ => "接口"
    };

    public void Dispose() { _timer?.Dispose(); _stop.Cancel(); }
}
