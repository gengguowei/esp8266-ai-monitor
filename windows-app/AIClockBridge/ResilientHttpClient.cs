namespace AIClockBridge;

// An HttpClient that can start over.
//
// A plain `static readonly HttpClient` - which is the normally-correct advice -
// pins two things for the lifetime of the process:
//   * the system proxy, resolved once into HttpClient.DefaultProxy;
//   * pooled TCP connections, since PooledConnectionLifetime defaults to
//     infinite, so they outlive DNS changes and network switches.
// Both bite when a proxy client is toggled while the bridge is running: every
// request fails until the user restarts the app. This wrapper re-reads the proxy
// and drops the pool after a run of transport failures, so it heals itself.
//
// Only *transport* failures count (no response at all: DNS, connect, TLS,
// timeout). An HTTP 401/429 means the path works fine and rebuilding would be
// pointless churn.
sealed class ResilientHttpClient
{
    /// Consecutive transport failures before starting over.
    const int RebuildAfterFailures = 3;
    /// Floor between rebuilds, so a hard-down network cannot spin us.
    static readonly TimeSpan MinRebuildInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    readonly object _lock = new();
    readonly string _name;
    readonly TimeSpan _timeout;

    HttpClient _client;
    int _failures;
    DateTime _lastRebuild = DateTime.MinValue;

    public ResilientHttpClient(string name, TimeSpan timeout)
    {
        _name = name;
        _timeout = timeout;
    }

    public HttpClient Client
    {
        get { lock (_lock) return _client ??= Build("first use"); }
    }

    public void ReportSuccess()
    {
        lock (_lock) _failures = 0;
    }

    /// Call this only from a catch of a transport-level exception.
    public void ReportTransportFailure(string reason)
    {
        HttpClient dead = null;
        lock (_lock)
        {
            _failures++;
            if (_failures < RebuildAfterFailures) return;
            if (DateTime.UtcNow - _lastRebuild < MinRebuildInterval) return;
            _failures = 0;
            dead = _client;
            _client = Build($"{RebuildAfterFailures} consecutive failures, last: {reason}");
        }
        // Nothing is succeeding on the old client by definition, so disposing it
        // cannot cut off a healthy in-flight request.
        try { dead?.Dispose(); } catch { }
    }

    HttpClient Build(string why)
    {
        _lastRebuild = DateTime.UtcNow;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = PooledConnectionLifetime,
        };
        SystemProxy.Configure(handler);
        Log.Write($"[http:{_name}] new client ({why}); system proxy is {SystemProxy.Describe()}");
        return new HttpClient(handler) { Timeout = _timeout };
    }
}
