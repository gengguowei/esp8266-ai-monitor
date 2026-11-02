using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIClockBridge;

sealed class BilibiliBrowserForm : Form, IBilibiliVideoBrowser
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Label _message = new() { Dock = DockStyle.Top, Height = 58, Padding = new Padding(12), Text = "请在下方 B 站官方网页登录，然后点击“保存登录并获取”。" };
    readonly Button _save = new() { Text = "保存登录并获取", Width = 170, Height = 34 };
    readonly BilibiliMonitor _monitor;
    readonly BilibiliSessionStore _sessions;
    readonly string _profile;
    Task _initializing;
    TaskCompletionSource<BilibiliVideo> _pendingVideo;
    TaskCompletionSource<JsonElement> _pendingNav;
    string _pendingUid;
    bool _closing;
    readonly bool _hideOnClose;
    public bool HasSession => _sessions.Load() != null;

    public BilibiliBrowserForm(BilibiliMonitor monitor, BilibiliSessionStore sessions, string profile = null, bool hideOnClose = true)
    {
        _monitor = monitor; _sessions = sessions;
        _hideOnClose = hideOnClose;
        _profile = profile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIClockBridge", "bilibili-browser");
        Text = "Bilibili 登录与 UP主取数";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 750);
        MinimumSize = new Size(760, 560);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 4, 0, 4) };
        buttons.Controls.Add(_save);
        var video = new Button { Text = "打开关注的投稿页", Width = 170, Height = 34 };
        video.Click += async (_, _) => { await InitializeBrowser(); _web.CoreWebView2.Navigate($"https://space.bilibili.com/{_monitor.Uid}/video"); };
        buttons.Controls.Add(video);
        Controls.Add(_web); Controls.Add(buttons); Controls.Add(_message);
        _save.Click += async (_, _) => await CaptureAndFetch();
        Shown += async (_, _) =>
        {
            try
            {
                await InitializeBrowser();
                if (string.IsNullOrEmpty(_web.CoreWebView2.Source) || _web.CoreWebView2.Source == "about:blank")
                    _web.CoreWebView2.Navigate("https://www.bilibili.com/");
            }
            catch (Exception) { _message.Text = "浏览器初始化失败，请确认已安装 Microsoft Edge WebView2 Runtime。"; }
        };
        FormClosing += (_, e) =>
        {
            if (_hideOnClose && !_closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        // Own dispatcher handle lets background refreshes invoke the same
        // browser session without opening a second browser or stealing focus.
        _ = Handle;
    }

    Task InitializeBrowser() => _initializing ??= InitializeCore();
    async Task InitializeCore()
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
        await _web.EnsureCoreWebView2Async(environment);
        Log.Write("[bilibili:browser] browser initialized; official login window ready");
        _web.CoreWebView2.NavigationCompleted += (_, e) =>
            Log.Write($"[bilibili:browser] navigation completed; success={e.IsSuccess}; status={e.WebErrorStatus}");
        _web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (!AllowedPage(e.Uri)) { e.Cancel = true; _message.Text = "请使用 B 站官方页面登录。"; }
        };
        _web.CoreWebView2.NewWindowRequested += (_, e) =>
        { e.Handled = true; if (AllowedPage(e.Uri)) _web.CoreWebView2.Navigate(e.Uri); };
        _web.CoreWebView2.WebResourceResponseReceived += ReceiveResponse;
    }
    static bool AllowedPage(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        ((uri.Scheme == "https" && (uri.Host == "bilibili.com" || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase))) || text == "about:blank");

    async Task CaptureAndFetch()
    {
        _save.Enabled = false;
        try
        {
            await InitializeBrowser();
            _message.Text = "正在验证登录状态…";
            // The account is authenticated by the official page in this
            // browser. Only the user enters passwords, scans QR or verifies.
            _pendingNav = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // ExecuteScriptAsync does not await a JavaScript Promise. Observe
            // the real network response rather than serializing that Promise.
            await _web.ExecuteScriptAsync("void fetch('https://api.bilibili.com/x/web-interface/nav',{credentials:'include',cache:'no-store'}).catch(()=>{});");
            var root = await _pendingNav.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (BilibiliData.Number(root, "code", -1) != 0 || !root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("isLogin", out var logged) || logged.ValueKind != JsonValueKind.True)
            { _message.Text = "尚未检测到登录。请在官方网页点击登录并完成扫码，然后再保存。"; return; }
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://api.bilibili.com/");
            var header = string.Join("; ", cookies.Where(c => c.Domain.TrimStart('.') == "bilibili.com" || c.Domain == "api.bilibili.com")
                .Select(c => c.Name + "=" + c.Value));
            var agent = JsonSerializer.Deserialize<string>(await _web.ExecuteScriptAsync("navigator.userAgent"));
            var session = new BilibiliSession(header, agent, BilibiliData.Number(data, "mid").ToString(System.Globalization.CultureInfo.InvariantCulture), BilibiliData.Text(data, "uname"));
            _monitor.UseVerifiedSession(session);
            _message.Text = "登录会话已保存，正在读取关注的投稿…";
            _message.Text = await _monitor.FetchNow();
            Log.Write("[bilibili:browser] verified session saved; fetch finished (credentials omitted)");
        }
        catch (Exception e)
        {
            // Do not log script output, request headers or exception details:
            // those may contain session data returned by the official page.
            _message.Text = e is InvalidOperationException ? "取数尚未结束，请稍后再保存登录。" : "登录验证或取数未完成，请保持官方页面可用后重试。";
            Log.Write("[bilibili:browser] session verification failed (details omitted)");
        }
        finally { _pendingNav = null; _save.Enabled = true; }
    }

    public async Task<BilibiliVideo> LatestVideo(string uid, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BilibiliVideo>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Start()
        {
            try
            {
                await InitializeBrowser();
                if (_pendingVideo != null) throw new InvalidOperationException("浏览器正在取数");
                _pendingVideo = completion; _pendingUid = uid;
                _web.CoreWebView2.Navigate($"https://space.bilibili.com/{uid}/video");
            }
            catch (Exception) { completion.TrySetException(new BilibiliBrowserFailure("投稿页浏览器未准备好，请打开 Bilibili 登录窗口")); }
        }
        if (InvokeRequired) BeginInvoke((Action)Start); else Start();
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken); }
        catch (TimeoutException) { throw new BilibiliBrowserFailure("投稿页未返回视频列表，请打开登录窗口检查页面或验证提示"); }
        finally
        {
            void Clear() { if (_pendingVideo == completion) { _pendingVideo = null; _pendingUid = null; } }
            if (!IsDisposed) { if (InvokeRequired) BeginInvoke((Action)Clear); else Clear(); }
        }
    }

    async void ReceiveResponse(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var navUri) && navUri.Scheme == "https" &&
            navUri.Host == "api.bilibili.com" && navUri.AbsolutePath == "/x/web-interface/nav" && _pendingNav is { } nav)
        {
            try
            {
                using var response = await e.Response.GetContentAsync();
                using var reader = new StreamReader(response);
                var text = await reader.ReadToEndAsync();
                if (text.Length > 2_000_000) throw new InvalidDataException();
                using var document = JsonDocument.Parse(text);
                nav.TrySetResult(document.RootElement.Clone());
            }
            catch (Exception) { nav.TrySetException(new InvalidDataException("登录状态未返回有效数据")); }
            return;
        }
        var pending = _pendingVideo; var uid = _pendingUid;
        if (pending == null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "api.bilibili.com" || uri.AbsolutePath != "/x/space/wbi/arc/search" || QueryValue(uri, "mid") != uid) return;
        try
        {
            var status = e.Response.StatusCode;
            if (status != 200)
            {
                DateTimeOffset? retryAt = null;
                if (e.Response.Headers.Contains("Retry-After"))
                {
                    var retry = e.Response.Headers.GetHeader("Retry-After");
                    if (int.TryParse(retry, out var seconds) && seconds >= 0) retryAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                    else if (DateTimeOffset.TryParse(retry, out var date)) retryAt = date;
                }
                throw new BilibiliBrowserFailure($"登录浏览器的视频列表返回 HTTP {status}", BilibiliPollPolicy.IsRisk(status), retryAt);
            }
            using var stream = await e.Response.GetContentAsync();
            if (stream == null) return;
            using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            { if (bytes.Length + read > 2_000_000) throw new InvalidDataException("视频响应过大"); bytes.Write(buffer, 0, read); }
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement; var code = (int)BilibiliData.Number(root, "code", int.MinValue);
            if (code != 0) throw new BilibiliBrowserFailure($"登录浏览器的视频列表返回业务码 {code}", BilibiliPollPolicy.IsRisk(code));
            var data = root.GetProperty("data");
            if (data.TryGetProperty("v_voucher", out _) || (data.TryGetProperty("is_risk", out var risk) && risk.ValueKind == JsonValueKind.True))
                throw new BilibiliBrowserFailure("登录浏览器返回风控提示，未获取视频", true);
            var video = BilibiliData.LatestVideo(data, uid);
            pending.TrySetResult(video);
            Log.Write($"[bilibili:browser] official video response captured; UID {uid}; bvid={video.Bvid}");
        }
        catch (BilibiliBrowserFailure failure) { pending.TrySetException(failure); }
        catch (Exception) { pending.TrySetException(new BilibiliBrowserFailure("投稿页响应缺少有效的视频数据")); }
    }
    static string QueryValue(Uri uri, string name) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2 && p[0] == name).Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault() ?? "";

    public void OpenLogin()
    {
        if (!Visible) Show();
        Activate();
    }
    public void StopBrowser() { _closing = true; _pendingVideo?.TrySetCanceled(); Close(); Dispose(); }
}
