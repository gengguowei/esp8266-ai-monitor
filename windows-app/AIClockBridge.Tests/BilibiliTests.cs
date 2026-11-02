using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using AIClockBridge;

static class BilibiliTests
{
    public static async Task Run(Action<bool, string> check, string[] args)
    {
        check(BilibiliData.TryUid("https://space.bilibili.com/39627524/dynamic", out var uid) && uid == "39627524", "space URL accepted as UID");
        foreach (var bad in new[] { "0", "-1", "1&ps=100", "https://evil.example/1", "999999999999999999999", "１２３" })
            check(!BilibiliData.TryUid(bad, out _), "invalid UID rejected: " + bad);
        check(BilibiliPollPolicy.SuccessDelay(0) == TimeSpan.FromHours(1) && BilibiliPollPolicy.SuccessDelay(1) == TimeSpan.FromMinutes(70), "video interval bounded at 60–70 minutes");
        check(BilibiliPollPolicy.FansInterval == TimeSpan.FromHours(6), "fans checked at six-hour cadence");
        check(BilibiliPollPolicy.FailureDelay(1, true) == TimeSpan.FromHours(6) && BilibiliPollPolicy.FailureDelay(2, true) == TimeSpan.FromHours(12), "risk cooldown grows from 6 to 12 hours");
        var key = BilibiliData.WbiKey("https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png", "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png");
        var signed = BilibiliData.Sign(new Dictionary<string, string> { ["mid"]="39627524", ["pn"]="1", ["ps"]="5", ["order"]="pubdate", ["platform"]="web" }, key, 1790737043);
        check(signed.EndsWith("w_rid=2c1ac21924ea8dc9404984aa367d5bcc"), "WBI signature matches successful probe");
        using var list = JsonDocument.Parse("""{"list":{"vlist":[{"mid":39627524,"bvid":"BVold","title":"置顶","created":100},{"mid":39627524,"bvid":"BVpaid","title":"充电视频","created":200},{"mid":1,"bvid":"BVwrong","title":"其他作者","created":300}]}}""");
        check(BilibiliData.LatestVideo(list.RootElement, uid).Bvid == "BVpaid", "newest timestamp wins including paid metadata, excluding other owners");
        var now = new DateTimeOffset(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);
        var directory = Path.Combine(Path.GetTempPath(), "aiclock-up-test-" + Guid.NewGuid());
        using var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        using (var monitor = new BilibiliMonitor(http, directory, uid, () => now, TimeSpan.Zero, () => 0))
        {
            await monitor.Activate();
            check(handler.Requests == 5 && monitor.Snapshot.Followers == 9683276 && monitor.Snapshot.Name == "食贫道", "one initial cycle gets video, owner, avatar and fans");
            await Task.WhenAll(monitor.Activate(), monitor.Activate(), monitor.RefreshIfDue());
            check(handler.Requests == 5, "opening page repeatedly cannot force another API cycle");
            var frame = monitor.Screen;
            var packet = monitor.RawScreen();
            check(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet) == frame.Revision && packet.AsSpan(4).SequenceEqual(frame.Pixels), "HTTP image revision and pixels form one snapshot");
            var frames = UpScreenFrames.Build(frame.Pixels, frame.Revision);
            check(frames.Count == 242 && frames.All(f => f.Length + 1 <= UpScreenFrames.BurstBudget), "every UART frame fits the pacing and line buffers");
            for (var y = 0; y < 240; y++)
            {
                var text = Encoding.ASCII.GetString(frames[y + 1]);
                var decoded = UpScreenFrames.DecodeRow(Convert.FromBase64String(text[(text.LastIndexOf(' ') + 1)..]));
                check(decoded.AsSpan().SequenceEqual(frame.Pixels.AsSpan(y * 480, 480)), "UART pixels round-trip exactly, row " + y);
            }
            now += TimeSpan.FromHours(1);
            await monitor.RefreshIfDue();
            check(handler.Requests == 6, "hourly unchanged-video check reuses owner/avatar and does not fetch fans");
            check(monitor.Screen.Revision == frame.Revision, "unchanged pixels are not retransmitted every hour");
        }
        using (var restarted = new BilibiliMonitor(http, directory, uid, () => now, TimeSpan.Zero, () => 0))
        {
            await restarted.Activate();
            check(handler.Requests == 6 && restarted.Snapshot.Video != null, "restart preserves data and refresh gate");
            restarted.SetUid("12345");
            await restarted.Activate();
            // A UID change is allowed after the global five-minute floor, but
            // wrong-owner data must not be displayed under the new UID.
            check(restarted.Snapshot.Uid == "12345" && restarted.Snapshot.Video == null, "UID change never presents previous owner as the new one");
        }
        using var riskHandler = new FakeHandler { Risk = true };
        using var riskHttp = new HttpClient(riskHandler);
        var riskDirectory = directory + "-risk";
        using (var risk = new BilibiliMonitor(riskHttp, riskDirectory, uid, () => now, TimeSpan.Zero, () => 0))
        {
            await risk.Activate();
            check(riskHandler.Requests == 1 && risk.NextRefreshAt == now.AddHours(6), "412 stops immediately and cools down for six hours");
            risk.SetUid("12345"); await risk.Activate();
            check(riskHandler.Requests == 1, "switching UID cannot bypass global risk cooldown");
            now += TimeSpan.FromHours(6); await risk.RefreshIfDue();
            check(riskHandler.Requests == 2 && risk.NextRefreshAt == now.AddHours(12), "second risk failure doubles cooldown");
        }
        using (var riskRestart = new BilibiliMonitor(riskHttp, riskDirectory, "12345", () => now, TimeSpan.Zero, () => 0))
        {
            await riskRestart.Activate();
            await riskRestart.FetchNow();
            check(riskHandler.Requests == 2, "restart cannot bypass global risk cooldown");
        }
        var sample = new BilibiliProfile(uid, "食贫道", "", 9683276, new("BVsample", "硅 谷 八 卦", 1790245165, "食贫道"), now, now);
        using var limitedHandler = new FakeHandler { Risk = true, RetryHours = 18 };
        using var limitedHttp = new HttpClient(limitedHandler);
        using (var limited = new BilibiliMonitor(limitedHttp, directory + "-429", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await limited.Activate();
            await limited.FetchNow();
            check(limited.NextRefreshAt == now.AddHours(18), "server Retry-After longer than six hours is honored");
            check(limitedHandler.Requests == 1, "manual click cannot bypass Retry-After");
        }
        using var partialHandler = new FakeHandler { FailFans = true, FailCard = true };
        using var partialHttp = new HttpClient(partialHandler);
        using (var partial = new BilibiliMonitor(partialHttp, directory + "-partial", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await partial.Activate();
            check(partial.Snapshot.Video != null && partial.Snapshot.Name == "食贫道" && partial.Snapshot.Followers == null && partial.Error != null, "fans failure preserves useful video and owner fields");
        }
        await BackupAndManualTests(check, uid, now, directory);
        await SessionTests(check, uid, now, directory);
        byte[] avatar = null;
        var avatarPath = Path.GetFullPath("dist/research/bilibili-39627524/avatar.jpg");
        if (File.Exists(avatarPath)) avatar = File.ReadAllBytes(avatarPath);
        var longTitle = "硅谷打工人中午吃啥？20美元越南河粉，搭配炖到脱骨的牛肋排";
        using (var bitmap = new Bitmap(240, 240))
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei", 20, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            var lines = BilibiliScreen.Wrap(longTitle, g, font, 212);
            check(string.Concat(lines) == longTitle && lines.Any(line => line.Contains("20")) && lines.All(line => g.MeasureString(line, font, int.MaxValue, StringFormat.GenericTypographic).Width <= 212), "title stays complete, numbers intact, every line within width");
        }
        var randomRow = new byte[480]; new Random(73).NextBytes(randomRow);
        var randomEncoded = UpScreenFrames.EncodeRow(randomRow);
        check(randomEncoded.Length <= 720 && UpScreenFrames.DecodeRow(randomEncoded).SequenceEqual(randomRow), "worst-case pixel row fits decoder");
        var pages = BilibiliScreen.Render(sample with { Video = sample.Video with { Title = string.Concat(Enumerable.Repeat(longTitle, 4)) } }, avatar);
        check(pages.Length > 1 && pages.All(p => p.Length == 115200), "long titles paginate without shrinking fonts or losing text");
        check(pages.All(p => p[0] == 0 && p[1] == 0 && p[^1] == 0 && p[^2] == 0), "UP background is exact RGB565 black on all pages");
        if (args.Contains("--up-preview"))
        {
            var output = Path.GetFullPath("dist/research/bilibili-39627524"); Directory.CreateDirectory(output);
            SavePng(BilibiliScreen.Render(sample, avatar)[0], Path.Combine(output, "implemented-latest.png"));
            SavePng(BilibiliScreen.Render(sample with { Video = sample.Video with { Title = longTitle, Published = 1790222997 } }, avatar)[0], Path.Combine(output, "implemented-long.png"));
        }
        if (args.Contains("--up-live"))
        {
            // Explicit opt-in only: normal production spacing and persisted gates.
            var output = Path.GetFullPath("dist/research/bilibili-39627524/live-monitor");
            using var live = new BilibiliMonitor(directory: output, uid: uid);
            await live.Activate();
            Console.WriteLine(JsonSerializer.Serialize(new { Profile = live.Snapshot, live.Error, live.NextRefreshAt }));
            SavePng(live.Screen.Pixels, Path.Combine(output, "screen.png"));
            if (live.Error != null || live.Snapshot.Video == null) Environment.ExitCode = 1;
        }
        if (args.Contains("--up-dynamics-live"))
        {
            var target = Settings.Get("bilibili_uid");
            if (!BilibiliData.TryUid(target, out var targetUid)) throw new InvalidDataException("未设置目标 UID");
            var sessions = new BilibiliSessionStore();
            if (sessions.Load() == null) throw new InvalidOperationException("没有已验证的登录会话");
            using var live = new BilibiliMonitor(directory: Path.GetFullPath("dist/research/bilibili-20261008/auth-dynamics"), uid: targetUid, sessionStore: sessions);
            var getJson = typeof(BilibiliMonitor).GetMethod("GetJson", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object result;
            try
            {
                var data = await (Task<JsonElement>)getJson.Invoke(live, new object[] { "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?host_mid=" + targetUid + "&platform=web&features=itemOpusStyle", targetUid, false });
                var count = data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : -1;
                result = new { Uid = targetUid, Success = true, ItemCount = count, TestedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)) };
            }
            catch (Exception e) { result = new { Uid = targetUid, Success = false, Error = e.Message }; }
            var text = JsonSerializer.Serialize(result);
            File.WriteAllText(Path.GetFullPath("dist/research/bilibili-20261008/auth-dynamics-result.json"), text);
            Console.WriteLine("AUTH DYNAMICS: " + text);
        }
    }

    static async Task BackupAndManualTests(Action<bool, string> check, string uid, DateTimeOffset now, string directory)
    {
        using var card = JsonDocument.Parse("""{"card":{"mid":"39627524","name":"食贫道","face":"http://i2.hdslb.com/test.png","fans":8},"follower":9}""");
        check(BilibiliData.CardIdentity(card.RootElement, uid).Avatar.StartsWith("https://") && BilibiliData.CardFollowers(card.RootElement, uid) == 9, "card parses string UID and prefers precise follower count");
        using var cardFans = JsonDocument.Parse("""{"card":{"mid":39627524,"name":"食贫道","face":"https://i2.hdslb.com/test.png","fans":8}}""");
        check(BilibiliData.CardFollowers(cardFans.RootElement, uid) == 8, "card fans field is accepted as numeric fallback");
        bool rejected = false;
        try { BilibiliData.CardFollowers(card.RootElement, "12345"); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "wrong UID card is rejected");
        var recorded = Path.GetFullPath("dist/research/bilibili-39627524/dynamic-desktop.response.txt");
        if (File.Exists(recorded))
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(recorded));
            var parsed = BilibiliData.DesktopDynamic(fixture.RootElement.GetProperty("data"), uid);
            check(parsed.Identity.Name == "食贫道" && parsed.Video.Bvid == "BV1RCac6yEPN", "desktop backup parses previously successful real response");
        }
        using var manualHandler = new FakeHandler(); using var manualHttp = new HttpClient(manualHandler);
        using var concurrentHandler = new FakeHandler();
        using var hold = new HoldFirstHandler(concurrentHandler);
        using var concurrentHttp = new HttpClient(hold);
        using (var concurrent = new BilibiliMonitor(concurrentHttp, directory + "-concurrent", uid, () => now, TimeSpan.Zero, () => 0))
        {
            var first = concurrent.FetchNow();
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = await concurrent.FetchNow();
            check(second == "正在获取，请稍候", "overlapping manual clicks do not launch duplicate requests");
            hold.Release.SetResult(); await first;
            check(concurrentHandler.Requests == 5, "one concurrent cycle fetches each initial field once");
        }
        using (var manual = new BilibiliMonitor(manualHttp, directory + "-manual", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await manual.Activate();
            var immediate = await manual.FetchNow();
            check(manualHandler.Requests == 8 && immediate == "已获取 UP主数据", "manual click fetches immediately after success despite automatic five-minute floor");
            now += TimeSpan.FromMinutes(5);
            await manual.RefreshIfDue();
            check(manualHandler.Requests == 8, "automatic hour schedule remains intact");
            var message = await manual.FetchNow();
            check(manualHandler.Requests == 11 && message == "已获取 UP主数据", "manual click skips hourly schedule and refreshes video owner and fans once");
            check(manual.Snapshot.FansCheckedAt == now && manual.Snapshot.VideoCheckedAt == now, "manual refresh also updates otherwise fresh fans");
            manualHandler.FailVideos = true;
            now += TimeSpan.FromMinutes(5); await manual.FetchNow();
            check(manual.Snapshot.Video.Bvid == "BV1qFaA6QEMm" && manual.Snapshot.VideoCheckedAt == now.AddMinutes(-5), "older dynamic backup never replaces newer paid metadata or its check time");
            check(manual.Error == null && manual.SourceWarning.Contains("早于缓存"), "successful stale fallback exposes warning without a failure loop");
        }
        using var backupHandler = new FakeHandler { FailVideos = true, FailFans = true };
        using var backupHttp = new HttpClient(backupHandler);
        using (var backup = new BilibiliMonitor(backupHttp, directory + "-backup", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await backup.Activate();
            check(backup.Error == null && backup.Snapshot.Video.Bvid == "BV1RCac6yEPN" && backup.Snapshot.Followers == 9683276, "ordinary video and fans failures recover from desktop and card");
            check(backup.Snapshot.VideoSource == "动态备用" && backup.Snapshot.IdentitySource == "动态作者备用" && backup.Snapshot.FansSource == "用户名片备用", "field sources are visible and persisted");
            check(backupHandler.Paths.Count(p => p.EndsWith("/card")) == 1 && !backupHandler.Paths.Contains("/x/web-interface/view"), "fallback reuses desktop owner and fetches card at most once");
        }
        using var identityHandler = new FakeHandler { FailView = true, FailFans = true };
        using var identityHttp = new HttpClient(identityHandler);
        using (var identity = new BilibiliMonitor(identityHttp, directory + "-identity", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await identity.Activate();
            check(identity.Error == null && identity.Snapshot.IdentitySource == "用户名片备用" && identity.Snapshot.Followers == 9683276, "card backs up owner and fans in one request");
            check(identityHandler.Paths.Count(p => p.EndsWith("/card")) == 1 && !identityHandler.Paths.Contains("/x/relation/stat"), "already fetched valid card avoids redundant fans request");
        }
        using var noVideoHandler = new FakeHandler { FailVideos = true, FailDynamic = true };
        using var noVideoHttp = new HttpClient(noVideoHandler);
        using (var noVideo = new BilibiliMonitor(noVideoHttp, directory + "-no-video", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await noVideo.Activate();
            check(noVideo.Error != null && noVideo.Snapshot.Video == null && noVideo.Snapshot.Name == "食贫道" && noVideo.Snapshot.Followers == 9683276, "video outage still saves independent identity and fans");
            check(noVideo.NextRefreshAt == now.AddHours(2), "ordinary exhausted fallback cools down two hours");
            var requestCount = noVideoHandler.Requests;
            await noVideo.RefreshIfDue();
            check(noVideoHandler.Requests == requestCount, "ordinary failure backoff still gates automatic refresh");
            noVideoHandler.FailVideos = noVideoHandler.FailDynamic = false;
            var retried = await noVideo.FetchNow();
            check(noVideo.Error == null && noVideoHandler.Requests > requestCount && retried == "已获取 UP主数据", "manual click immediately retries ordinary failure without waiting two hours");
        }
        using (var healthyRestart = new BilibiliMonitor(manualHttp, directory + "-manual", uid, () => now, TimeSpan.Zero, () => 0))
        {
            manualHandler.FailVideos = false;
            var before = manualHandler.Requests;
            await healthyRestart.FetchNow();
            check(manualHandler.Requests > before && healthyRestart.Error == null, "persisted successful schedule does not gate manual fetch after restart");
        }
        using var ordinaryHandler = new FakeHandler { FailFans = true, FailCard = true };
        using var ordinaryHttp = new HttpClient(ordinaryHandler);
        var ordinaryCache = directory + "-ordinary-restart";
        using (var ordinary = new BilibiliMonitor(ordinaryHttp, ordinaryCache, uid, () => now, TimeSpan.Zero, () => 0)) await ordinary.Activate();
        using (var ordinaryRestart = new BilibiliMonitor(ordinaryHttp, ordinaryCache, uid, () => now, TimeSpan.Zero, () => 0))
        {
            ordinaryHandler.FailFans = ordinaryHandler.FailCard = false;
            await ordinaryRestart.FetchNow();
            check(ordinaryRestart.Error == null && ordinaryRestart.Snapshot.Followers.HasValue, "ordinary failure remains manually retryable after restart");
        }
        foreach (var version in new[] { 0, 1 })
        {
            var legacyCache = directory + "-legacy-" + version; Directory.CreateDirectory(legacyCache);
            File.WriteAllText(Path.Combine(legacyCache, "cache.json"), JsonSerializer.Serialize(new {
                GateVersion = version, NextAllowed = now.AddHours(6), Failures = 1,
                HardCooldownUntil = version == 1 ? now.AddHours(6) : default,
                HardCooldownReason = version == 1 ? "旧版保存的接口冷却" : null,
                Profiles = new Dictionary<string, BilibiliProfile> { [uid] = new(uid, "缓存名称", "", 42, null, null, now) }
            }));
            using (var legacy = new BilibiliMonitor(ordinaryHttp, legacyCache, uid, () => now, TimeSpan.Zero, () => 0))
            {
                var before = ordinaryHandler.Requests;
                await legacy.Activate();
                check(ordinaryHandler.Requests == before && legacy.Snapshot.Name == "缓存名称" && legacy.Snapshot.Followers == 42,
                    "legacy migration preserves cached fields and automatic backoff, version " + version);
                var message = await legacy.FetchNow();
                check(ordinaryHandler.Requests == before + 5 && message == "已获取 UP主数据" && legacy.Error == null,
                    "legacy unknown cooldown permits actual immediate manual requests, version " + version);
            }
            using var migrated = new BilibiliMonitor(ordinaryHttp, legacyCache, uid, () => now, TimeSpan.Zero, () => 0);
            var restartBefore = ordinaryHandler.Requests;
            var restartedMessage = await migrated.FetchNow();
            check(ordinaryHandler.Requests > restartBefore && restartedMessage == "已获取 UP主数据",
                "saved migration remains manually fetchable after restart, version " + version);
        }
        foreach (var reason in new[] { "接口已返回风控或验证响应", "接口要求暂缓请求（Retry-After）" })
        {
            var explicitCache = directory + "-explicit-" + (reason.Contains("Retry") ? "retry" : "risk");
            Directory.CreateDirectory(explicitCache);
            File.WriteAllText(Path.Combine(explicitCache, "cache.json"), JsonSerializer.Serialize(new {
                GateVersion = 1, NextAllowed = now.AddHours(6), HardCooldownUntil = now.AddHours(6), HardCooldownReason = reason
            }));
            using var explicitMonitor = new BilibiliMonitor(ordinaryHttp, explicitCache, uid, () => now, TimeSpan.Zero, () => 0);
            var before = ordinaryHandler.Requests;
            var blocked = await explicitMonitor.FetchNow();
            check(ordinaryHandler.Requests == before && blocked.Contains(reason), "migration keeps explicitly recorded restriction: " + reason);
        }
        using var retryHandler = new FakeHandler { RetryOnly = true, RetryHours = 18 };
        using var retryHttp = new HttpClient(retryHandler);
        using (var retry = new BilibiliMonitor(retryHttp, directory + "-retry-only", uid, () => now, TimeSpan.Zero, () => 0))
        {
            await retry.Activate(); var before = retryHandler.Requests; var blocked = await retry.FetchNow();
            check(retryHandler.Requests == before && retry.NextRefreshAt == now.AddHours(18) && blocked.Contains("Retry-After"), "503 with explicit Retry-After still blocks manual retry");
        }
        foreach (var mode in new[] { "http", "html", "voucher", "risk-flag" })
        {
            using var videoRiskHandler = new FakeHandler { RiskVideos = mode == "http", HtmlVideos = mode == "html", VoucherVideos = mode == "voucher", RiskFlagVideos = mode == "risk-flag" };
            using var videoRiskHttp = new HttpClient(videoRiskHandler);
            var riskPath = directory + "-video-risk-" + mode;
            using var videoRisk = new BilibiliMonitor(videoRiskHttp, riskPath, uid, () => now, TimeSpan.Zero, () => 0);
            await videoRisk.Activate(); await videoRisk.FetchNow();
            check(videoRiskHandler.Requests == 2 && videoRisk.NextRefreshAt == now.AddHours(6), "video risk/verification page stops all backups and manual fetch: " + mode);
            using var riskCache = JsonDocument.Parse(File.ReadAllText(Path.Combine(riskPath, "cache.json")));
            check(videoRisk.Error.Contains("视频列表接口") && riskCache.RootElement.GetProperty("LastFailureEndpoint").GetString() == "/x/space/wbi/arc/search",
                "failure identifies and persists the exact video endpoint: " + mode);
        }
        var latestCardPath = Path.GetFullPath("dist/research/bilibili-20261008/card.json");
        if (File.Exists(latestCardPath))
        {
            using var actualCard = JsonDocument.Parse(File.ReadAllText(latestCardPath));
            var actualData = actualCard.RootElement.GetProperty("data");
            check(BilibiliData.CardIdentity(actualData, "3546619609876957").Name == "王师傅の日记" && BilibiliData.CardFollowers(actualData, "3546619609876957") == 460924,
                "real current card parses exact large UID, name, avatar and followers");
        }
        using var noDynamics = JsonDocument.Parse("""{"items":[],"has_more":false}""");
        bool emptyRejected = false;
        try { BilibiliData.DesktopDynamic(noDynamics.RootElement, uid); } catch (InvalidDataException) { emptyRejected = true; }
        check(emptyRejected, "code-zero empty dynamics do not masquerade as a valid latest-video backup");
        using var parserFixture = JsonDocument.Parse("""{"items":[{"type":"DYNAMIC_TYPE_FORWARD","modules":[{"module_type":"MODULE_TYPE_AUTHOR","module_author":{"user":{"mid":39627524,"name":"食贫道","face":"https://i2.hdslb.com/test.png"},"pub_ts":300}},{"module_type":"MODULE_TYPE_DYNAMIC","module_dynamic":{"dyn_archive":{"bvid":"BVforward","title":"转发"}}}]},{"type":"DYNAMIC_TYPE_AV","modules":[{"module_type":"MODULE_TYPE_AUTHOR","module_author":{"user":{"mid":1,"name":"其他","face":"https://i2.hdslb.com/test.png"},"pub_ts":400}},{"module_type":"MODULE_TYPE_DYNAMIC","module_dynamic":{"dyn_archive":{"bvid":"BVwrong","title":"其他作者"}}}]}]}""");
        check(BilibiliData.DesktopDynamic(parserFixture.RootElement, uid).Video == null, "desktop parser excludes forwards and other owners");
    }

    static async Task SessionTests(Action<bool, string> check, string uid, DateTimeOffset now, string directory)
    {
        var session = new BilibiliSession("SESSDATA=session-fixture; DedeUserID=12345; buvid3=fixture", "Browser fixture", "12345", "登录测试");
        var sessionDirectory = directory + "-session-store";
        var store = new BilibiliSessionStore(sessionDirectory);
        store.Save(session);
        check(store.Load() == session, "session survives Windows current-user encryption round-trip");
        check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(sessionDirectory, "session.bin"))).Contains("session-fixture"), "session file contains no plaintext credential");
        check(BilibiliSessionStore.HeaderFor(session, new Uri("https://api.bilibili.com/x/relation/stat")) == session.Cookie, "session is sent to intended Bilibili HTTPS endpoint");
        foreach (var url in new[] { "https://i2.hdslb.com/avatar.png", "https://bilibili.com.evil.example/", "http://api.bilibili.com/", "https://other.example/" })
            check(BilibiliSessionStore.HeaderFor(session, new Uri(url)) == null, "session is never sent to another origin: " + url);
        check(!BilibiliSessionStore.Valid(session with { Cookie = session.Cookie + "\r\nInjected: true" }), "session header injection is rejected");
        check(!BilibiliSessionStore.Valid(session with { AccountUid = "54321" }), "cookie account must match verified account");
        check(BilibiliSessionStore.SameLogin(session, session with { Cookie = session.Cookie + "; b_lsid=changed" }), "ordinary browser cookies do not count as a new authenticated session");

        using var handler = new FakeHandler(); using var http = new HttpClient(handler);
        var videoBrowser = new FakeVideoBrowser();
        using (var monitor = new BilibiliMonitor(http, directory + "-browser-source", uid, () => now, TimeSpan.Zero, () => 0, store) { VideoBrowser = videoBrowser })
        {
            monitor.UseVerifiedSession(session);
            var message = await monitor.FetchNow();
            check(message == "已获取 UP主数据" && videoBrowser.Calls == 1 && monitor.Snapshot.VideoSource == "登录浏览器投稿页", "authenticated video comes from official browser response provider");
            check(!handler.Paths.Contains("/x/space/wbi/arc/search") && !handler.Paths.Contains("/x/web-interface/nav"), "browser session does not launch a second anonymous signed video request");
            check(monitor.Snapshot.Name == "食贫道" && monitor.Snapshot.Followers == 9683276 && handler.Requests == 2, "authenticated metadata reuses one card response plus avatar");
            check(handler.CookieHeaders.Where(pair => pair.Path == "/x/web-interface/card").All(pair => pair.Cookie == session.Cookie) &&
                handler.CookieHeaders.Where(pair => pair.Path.EndsWith(".png")).All(pair => string.IsNullOrEmpty(pair.Cookie)), "API receives session while avatar CDN does not");

            videoBrowser.FailRisk = true;
            await monitor.FetchNow();
            var count = videoBrowser.Calls;
            var gate = monitor.NextRefreshAt;
            monitor.UseVerifiedSession(session with { Cookie = session.Cookie + "; b_lsid=fresh" });
            await monitor.FetchNow();
            check(videoBrowser.Calls == count && monitor.NextRefreshAt == gate, "saving same login cannot bypass real browser risk cooldown");
            videoBrowser.FailRisk = false;
            monitor.UseVerifiedSession(session with { Cookie = "SESSDATA=new-fixture; DedeUserID=12345" });
            await monitor.FetchNow();
            check(videoBrowser.Calls == count + 1 && monitor.Error == null, "a newly verified authenticated context permits one fresh fetch");
        }
        var retryDirectory = directory + "-session-retry"; Directory.CreateDirectory(retryDirectory);
        File.WriteAllText(Path.Combine(retryDirectory, "cache.json"), JsonSerializer.Serialize(new {
            GateVersion = 2, NextAllowed = now.AddHours(18), HardCooldownUntil = now.AddHours(18), HardCooldownReason = "接口要求暂缓请求（Retry-After）"
        }));
        var retryBrowser = new FakeVideoBrowser();
        using (var retry = new BilibiliMonitor(http, retryDirectory, uid, () => now, TimeSpan.Zero, () => 0, store) { VideoBrowser = retryBrowser })
        {
            retry.UseVerifiedSession(session);
            await retry.FetchNow();
            check(retryBrowser.Calls == 0 && retry.NextRefreshAt == now.AddHours(18), "new login still obeys explicit server Retry-After");
        }
        using var dynamic = JsonDocument.Parse("""{"items":[{"type":"DYNAMIC_TYPE_AV","modules":{"module_author":{"mid":39627524,"name":"食贫道","face":"https://i2.hdslb.com/test.png","pub_ts":200},"module_dynamic":{"major":{"archive":{"bvid":"BVweb","title":"Web 动态"}}}}},{"type":"DYNAMIC_TYPE_FORWARD","modules":{"module_author":{"mid":39627524,"name":"食贫道","face":"https://i2.hdslb.com/test.png","pub_ts":300},"module_dynamic":{"major":{"archive":{"bvid":"BVforward","title":"转发"}}}}}]}""");
        check(BilibiliData.WebDynamic(dynamic.RootElement, uid).Video.Bvid == "BVweb", "authenticated Web dynamics parse object modules and exclude forwards");
    }

    sealed class FakeVideoBrowser : IBilibiliVideoBrowser
    {
        public bool HasSession => true;
        public int Calls;
        public bool FailRisk;
        public Task<BilibiliVideo> LatestVideo(string uid, CancellationToken cancellationToken)
        {
            Calls++;
            return FailRisk ? Task.FromException<BilibiliVideo>(new BilibiliBrowserFailure("browser fixture HTTP 412", true)) :
                Task.FromResult(new BilibiliVideo("BV1qFaA6QEMm", "硅 谷 八 卦", 1790245165, "食贫道"));
        }
    }

    static void SavePng(byte[] pixels, string path)
    {
        using var image = new Bitmap(240, 240);
        for (var y = 0; y < 240; y++) for (var x = 0; x < 240; x++)
        {
            var offset = (y * 240 + x) * 2; var value = (pixels[offset] << 8) | pixels[offset + 1];
            image.SetPixel(x, y, Color.FromArgb(((value >> 11) & 31) * 255 / 31, ((value >> 5) & 63) * 255 / 63, (value & 31) * 255 / 31));
        }
        image.Save(path, ImageFormat.Png);
    }

    sealed class HoldFirstHandler : HttpMessageHandler
    {
        readonly HttpMessageInvoker _inner;
        int _started;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HoldFirstHandler(HttpMessageHandler inner) => _inner = new(inner, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref _started) == 1)
            { Entered.SetResult(); await Release.Task.WaitAsync(token); }
            return await _inner.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests; public bool Risk, RetryOnly, FailFans, FailCard, FailVideos, FailView, FailDynamic, RiskVideos, HtmlVideos, VoucherVideos, RiskFlagVideos; public int RetryHours;
        public readonly List<string> Paths = new();
        public readonly List<(string Path, string Cookie)> CookieHeaders = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            CookieHeaders.Add((request.RequestUri.AbsolutePath, request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null));
            if (Risk || RetryOnly)
            {
                var failed = new HttpResponseMessage(RetryOnly ? HttpStatusCode.ServiceUnavailable : RetryHours > 0 ? HttpStatusCode.TooManyRequests : HttpStatusCode.PreconditionFailed);
                if (RetryHours > 0) failed.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(RetryHours));
                return Task.FromResult(failed);
            }
            var path = request.RequestUri.AbsolutePath;
            Paths.Add(path);
            if ((FailFans && path == "/x/relation/stat") || (FailCard && path == "/x/web-interface/card") ||
                (FailVideos && path == "/x/space/wbi/arc/search") || (FailView && path == "/x/web-interface/view") ||
                (FailDynamic && path.Contains("/desktop/"))) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (path == "/x/space/wbi/arc/search" && RiskVideos) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
            if (path == "/x/space/wbi/arc/search" && HtmlVideos) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captcha</html>") });
            if (path == "/x/space/wbi/arc/search" && (VoucherVideos || RiskFlagVideos))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(VoucherVideos ?
                    """{"code":0,"data":{"v_voucher":"fixture"}}""" : """{"code":0,"data":{"is_risk":true}}""", Encoding.UTF8, "application/json") });
            string json = path switch
            {
                "/x/web-interface/nav" => """{"code":-101,"data":{"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""",
                "/x/space/wbi/arc/search" => """{"code":0,"data":{"list":{"vlist":[{"mid":39627524,"bvid":"BV1qFaA6QEMm","title":"硅 谷 八 卦","created":1790245165,"author":"食贫道"}]}}}""",
                "/x/web-interface/view" => """{"code":0,"data":{"owner":{"mid":39627524,"name":"食贫道","face":"https://i2.hdslb.com/test.png"}}}""",
                "/x/relation/stat" => """{"code":0,"data":{"mid":39627524,"follower":9683276}}""",
                "/x/web-interface/card" => """{"code":0,"data":{"card":{"mid":"39627524","name":"食贫道","face":"https://i2.hdslb.com/test.png","fans":9683276},"follower":9683276}}""",
                "/x/polymer/web-dynamic/desktop/v1/feed/space" => """{"code":0,"data":{"items":[{"type":"DYNAMIC_TYPE_AV","modules":[{"module_type":"MODULE_TYPE_AUTHOR","module_author":{"user":{"mid":39627524,"name":"食贫道","face":"https://i2.hdslb.com/test.png"},"pub_ts":1790222997}},{"module_type":"MODULE_TYPE_DYNAMIC","module_dynamic":{"dyn_archive":{"bvid":"BV1RCac6yEPN","title":"硅谷打工人中午吃啥？"}}}]}]}}""",
                _ => null
            };
            if (json != null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            using var image = new Bitmap(1, 1); using var bytes = new MemoryStream(); image.Save(bytes, ImageFormat.Png);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) });
        }
    }
}
