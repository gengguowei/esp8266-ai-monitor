using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

sealed record BilibiliSession(string Cookie, string UserAgent, string AccountUid, string AccountName);

sealed class BilibiliSessionStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIClockBridge.Bilibili.Session.v1");
    readonly string _path;
    readonly object _lock = new();
    public BilibiliSessionStore(string directory = null) => _path = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIClockBridge", "bilibili-session"), "session.bin");
    public BilibiliSession Load()
    {
        lock (_lock)
        {
            try
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                var session = JsonSerializer.Deserialize<BilibiliSession>(bytes);
                return Valid(session) ? session : null;
            }
            catch { return null; }
        }
    }
    public void Save(BilibiliSession session)
    {
        if (!Valid(session)) throw new InvalidDataException("登录会话无效，请在官方页面重新登录");
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path + ".tmp", encrypted);
            File.Move(_path + ".tmp", _path, true);
        }
    }
    internal static string CookieValue(string header, string name) => (header ?? "").Split(';')
        .Select(part => part.Trim().Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == name)
        .Select(pair => pair[1]).FirstOrDefault() ?? "";
    internal static bool Valid(BilibiliSession session) => session != null &&
        session.Cookie?.Length is > 0 and <= 16384 && !session.Cookie.Any(char.IsControl) &&
        session.UserAgent?.Length is > 0 and <= 1024 && !session.UserAgent.Any(char.IsControl) &&
        BilibiliData.TryUid(session.AccountUid, out var uid) && uid == session.AccountUid &&
        CookieValue(session.Cookie, "SESSDATA").Length > 0 && CookieValue(session.Cookie, "DedeUserID") == uid;
    internal static bool SameLogin(BilibiliSession before, BilibiliSession after) => before != null && after != null &&
        before.AccountUid == after.AccountUid && CookieValue(before.Cookie, "SESSDATA") == CookieValue(after.Cookie, "SESSDATA");
    internal static string HeaderFor(BilibiliSession session, Uri destination) =>
        destination.Scheme == "https" && (destination.Host == "bilibili.com" || destination.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase))
        ? session?.Cookie : null;
}

interface IBilibiliVideoBrowser
{
    bool HasSession { get; }
    Task<BilibiliVideo> LatestVideo(string uid, CancellationToken cancellationToken);
}

sealed class BilibiliBrowserFailure : Exception
{
    public bool Risk { get; }
    public DateTimeOffset? RetryAt { get; }
    public BilibiliBrowserFailure(string message, bool risk = false, DateTimeOffset? retryAt = null) : base(message)
    { Risk = risk; RetryAt = retryAt; }
}
