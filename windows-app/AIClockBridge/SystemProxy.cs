using System.Net;
using Microsoft.Win32;

namespace AIClockBridge;

// Re-reads the WinINET proxy settings on demand.
//
// Why this exists: HttpClient resolves HttpClient.DefaultProxy ONCE per process
// and caches it. A proxy client (v2rayN and friends) that toggles the Windows
// system proxy after the bridge started leaves every outbound request pointed at
// a proxy that is no longer listening - Codex quota and stock quotes then fail
// forever while Claude quota (local file) and net speed (local counters) keep
// working, and restarting the bridge "fixes" it. Observed in the field.
static class SystemProxy
{
    const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// Points a handler at whatever the system proxy is *right now*.
    /// When a PAC script is configured, or the registry cannot be read, the
    /// handler is left alone so the platform default still applies - guessing
    /// would be worse than deferring.
    public static void Configure(System.Net.Http.SocketsHttpHandler handler)
    {
        string server = null, autoConfig = null;
        var enabled = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (key == null) return;                       // defer to the platform
            enabled = key.GetValue("ProxyEnable") as int? ?? 0;
            server = key.GetValue("ProxyServer") as string;
            autoConfig = key.GetValue("AutoConfigURL") as string;
        }
        catch
        {
            return;                                        // defer to the platform
        }

        // A PAC script needs a real evaluator; let the platform handle it.
        if (!string.IsNullOrWhiteSpace(autoConfig)) return;

        if (enabled == 0)
        {
            handler.UseProxy = false;                      // explicitly direct
            return;
        }

        var endpoint = ParseProxyServer(server, "https");
        if (endpoint.Length == 0) endpoint = ParseProxyServer(server, "http");
        if (endpoint.Length == 0)
        {
            handler.UseProxy = false;
            return;
        }

        try
        {
            handler.UseProxy = true;
            // Bypass-on-local covers the only case we care about; every outbound
            // call this app makes goes to a public host.
            handler.Proxy = new WebProxy(Normalize(endpoint), BypassOnLocal: true);
        }
        catch
        {
            handler.UseProxy = false;
        }
    }

    /// ProxyServer is either a bare "host:port" that applies to every scheme, or
    /// a per-scheme list: "http=host:1080;https=host:1080;socks=host:1080".
    internal static string ParseProxyServer(string raw, string scheme)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        if (!text.Contains('=')) return text;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals(scheme, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return "";
    }

    /// WebProxy wants an absolute URI; the registry stores a bare host:port.
    internal static string Normalize(string endpoint) =>
        endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "http://" + endpoint;

    /// For the log line, so a stalled fetch can be tied to what the proxy was.
    public static string Describe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (key == null) return "unreadable";
            var pac = key.GetValue("AutoConfigURL") as string;
            if (!string.IsNullOrWhiteSpace(pac)) return "pac=" + pac;
            var enabled = key.GetValue("ProxyEnable") as int? ?? 0;
            if (enabled == 0) return "direct";
            return "proxy=" + (key.GetValue("ProxyServer") as string ?? "?");
        }
        catch { return "unreadable"; }
    }
}
