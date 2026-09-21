using System.Text;
using System.Text.Json;

namespace AIClockBridge;

// Entry point. Runs tray-only (no main window) and starts the /status HTTP
// server that the ESP8266 clock polls — the same endpoints and JSON shapes as
// the Mac app, so the firmware can't tell which OS the bridge runs on.
// Headless smoke test for the petdex -> GIF -> device pipeline (same code the
// pet picker window uses): AIClockBridge --test-pet <slug> <claude|codex> <host>
static class Program
{
    const int Port = 8765;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--test-pet")
        {
            Environment.Exit(TestPet(args).GetAwaiter().GetResult());
            return;
        }

        ApplicationConfiguration.Initialize();

        // One bridge per logon session. Without this, a second launch quietly
        // fights the first over port 8765 and over the CH340 serial port - and
        // "launch it twice" stops being rare once the app starts itself at
        // logon and the user then double-clicks it out of habit.
        using var instanceLock = new Mutex(initiallyOwned: true,
            @"Local\AIClockBridge.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Silent for the logon copy: never pop a dialog at a user who is
            // not looking at the screen yet.
            if (!args.Contains(AutoStart.AutoStartFlag))
                MessageBox.Show("AI Clock Bridge 已经在运行了，看一下托盘图标。",
                    "AI Clock Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // A tray app that dies leaves nothing behind but a missing icon, so
        // capture the last words. Registered before anything else can throw.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"[fatal] {e.ExceptionObject}");
        Application.ThreadException += (_, e) =>
            Log.Write($"[fatal:ui] {e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"[fatal:task] {e.Exception}");
            e.SetObserved();
        };
        Log.Write($"[bridge] starting {Environment.ProcessPath}"
                  + $" args=[{string.Join(' ', args)}] proxy={SystemProxy.Describe()}");

        // Releases live in versioned folders, so an upgrade moves the exe out
        // from under an existing Run entry. Repoint it at this build.
        AutoStart.SelfHeal();

        var service = new StatusService();
        var usage = new UsageFetcher();
        service.Usage = usage;
        var netMonitor = new NetSpeedMonitor();
        netMonitor.Start();
        var stockMonitor = new StockMonitor();
        stockMonitor.Start();
        using var screenPower = new WindowsScreenPowerController();
        service.ScreenOnProvider = () => screenPower.ScreenOn;
        using var serialLink = new SerialLink(service, netMonitor, stockMonitor);
        DeviceClient.Serial = serialLink;
        serialLink.Start();

        var server = new MiniHttpServer(Port,
            routes: new()
            {
                ["/"] = () => service.Snapshot().ToJson(),
                ["/status"] = () => service.Snapshot().ToJson(),
                ["/net"] = () => netMonitor.ToJson(SystemStatsMonitor.Snapshot()),
                ["/stock"] = () => stockMonitor.ToJson(),
            },
            binaryRoutes: new()
            {
                ["/stock/names.raw"] = () => stockMonitor.NamesRgb565,
            },
            postRoutes: new()
            {
                // Claude Code / Codex hooks push lifecycle events here (README §7):
                // curl -d '{"agent":"claude","event":"PreToolUse"}' http://127.0.0.1:8765/event
                ["/event"] = body =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("agent", out var agent)
                            && root.TryGetProperty("event", out var ev)
                            && agent.ValueKind == JsonValueKind.String
                            && ev.ValueKind == JsonValueKind.String)
                        {
                            string message = null;
                            if (root.TryGetProperty("message", out var msg)
                                && msg.ValueKind == JsonValueKind.String)
                                message = msg.GetString();
                            string sessionId = null;
                            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                                sessionId = sid.GetString();
                            service.RecordEvent(agent.GetString(), ev.GetString(), message, sessionId);
                            return Encoding.UTF8.GetBytes("{\"ok\":true}");
                        }
                    }
                    catch
                    {
                        // malformed body
                    }
                    return Encoding.UTF8.GetBytes("{\"ok\":false}");
                },
            });
        // Passive discovery: the clock polls us, so its source IP identifies it.
        // Remember it (for auto-pairing / DHCP-change self-healing) and adopt it
        // outright when no device is configured yet.
        server.OnRequest = (path, ip) =>
        {
            if (path != "/status" && path != "/net" && path != "/stock") return;
            if (ip == "127.0.0.1" || ip == "::1" || ip.Length == 0) return;
            DeviceClient.DevicePollAt = DateTime.UtcNow;
            DeviceClient.LastSeenIp = ip;
            if (DeviceClient.Host.Length == 0) DeviceClient.Host = ip;
        };
        // Active fallback for when the passive route can't fire at all (fresh /
        // erased device knows no bridge host, so it never polls anyone): if the
        // device stays silent, find it ourselves and hand it our address.
        using var pairingWatchdog = new System.Threading.Timer(
            _ => _ = DeviceClient.HealPairingIfNeeded(Port), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        // At logon the network stack is often not up yet. A one-shot bind
        // failure used to mean no LAN bridge at all until the user restarted the
        // app by hand (USB still worked, because SerialLink reconnects itself) -
        // barely visible when you launch by hand, routine when you autostart.
        // Start() builds a fresh TcpListener each call, so retrying is safe.
        var bindLock = new object();
        var bound = false;
        System.Threading.Timer bindRetry = null;
        void TryBind()
        {
            lock (bindLock)
            {
                if (bound) return;
                try
                {
                    server.Start();
                    bound = true;
                    Log.Write($"[bridge] serving /status on 0.0.0.0:{Port}");
                    bindRetry?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                catch (Exception e)
                {
                    Log.Write($"[bridge] port {Port} unavailable: {e.Message}");
                }
            }
        }
        TryBind();
        if (!bound)
            bindRetry = new System.Threading.Timer(_ => TryBind(), null,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        using var bindRetryHandle = bindRetry;

        var context = new TrayAppContext(
            service, usage, netMonitor, stockMonitor, screenPower, Port);
        usage.StartAutoRefresh();
        Application.Run(context);
    }

    static async Task<int> TestPet(string[] args)
    {
        var slug = args[1];
        var slot = args[2];
        if (args.Length >= 4) DeviceClient.Host = args[3];
        var (w, h) = slot == "claude" ? (111, 120) : (120, 120);
        var state = PetdexService.States.First(s => s.Id == "running");
        try
        {
            var pets = await PetdexService.LoadManifest();
            var pet = pets.FirstOrDefault(p => p.Slug == slug);
            if (pet == null)
            {
                Console.WriteLine("manifest load failed or slug not found");
                return 1;
            }
            Console.WriteLine($"pet: {pet.DisplayName} {pet.SpritesheetUrl}");
            using var sheet = await PetdexService.DownloadSpritesheet(pet);
            Console.WriteLine($"sheet: {sheet.Width}x{sheet.Height}");
            var gif = PetdexService.BuildGif(sheet, state, w, h);
            if (gif == null)
            {
                Console.WriteLine("gif build failed");
                return 1;
            }
            Console.WriteLine($"gif: {gif.Length} bytes, uploading to {DeviceClient.Host} slot {slot}...");
            await DeviceClient.UploadGif(gif, slot);
            Console.WriteLine("upload ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"failed: {e.Message}");
            return 1;
        }
    }
}
