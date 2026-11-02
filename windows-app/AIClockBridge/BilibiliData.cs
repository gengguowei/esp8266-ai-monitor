using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

sealed record BilibiliVideo(string Bvid, string Title, long Published, string Author);
sealed record BilibiliProfile(string Uid, string Name, string Avatar, long? Followers,
    BilibiliVideo Video, DateTimeOffset? VideoCheckedAt, DateTimeOffset? FansCheckedAt)
{
    public string VideoSource { get; init; } = "视频列表";
    public string IdentitySource { get; init; } = "视频作者";
    public string FansSource { get; init; } = "粉丝统计";
    public string SourceWarning { get; init; }
}
sealed record BilibiliIdentity(string Name, string Avatar);
sealed record BilibiliDynamic(BilibiliIdentity Identity, BilibiliVideo Video);

static class BilibiliData
{
    public static bool TryUid(string input, out string uid)
    {
        uid = (input ?? "").Trim();
        if (Uri.TryCreate(uid, UriKind.Absolute, out var url))
        {
            if (url.Scheme != "https" || url.Host != "space.bilibili.com") return false;
            uid = url.AbsolutePath.Trim('/').Split('/')[0];
        }
        if (uid.Length == 0 || uid.Any(c => c < '0' || c > '9') ||
            !long.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var mid) || mid <= 0)
            return false;
        uid = mid.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    public static string Text(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public static long Number(JsonElement root, string key, long fallback = 0) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : fallback;

    public static BilibiliVideo LatestVideo(JsonElement data, string uid)
    {
        if (!data.TryGetProperty("list", out var list) ||
            !list.TryGetProperty("vlist", out var videos) || videos.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("视频列表格式变化");
        BilibiliVideo latest = null;
        foreach (var item in videos.EnumerateArray())
        {
            if (Number(item, "mid").ToString(CultureInfo.InvariantCulture) != uid) continue;
            var bvid = Text(item, "bvid");
            var title = Text(item, "title");
            var published = Number(item, "created");
            if (!bvid.StartsWith("BV", StringComparison.Ordinal) || title.Length == 0 || title.Length > 1024 ||
                published <= 0 || published > 253402300799) continue;
            var video = new BilibiliVideo(bvid, title, published, Text(item, "author"));
            if (latest == null || published > latest.Published) latest = video;
        }
        // Empty/risk-filtered lists must not silently erase the last good video.
        if (latest == null) throw new InvalidDataException("没有获取到该 UP 主的公开视频信息");
        return latest;
    }

    public static BilibiliIdentity Identity(JsonElement user, string uid)
    {
        var mid = Text(user, "mid");
        if (mid.Length == 0) mid = Number(user, "mid").ToString(CultureInfo.InvariantCulture);
        var name = Text(user, "name"); var face = Text(user, "face").Replace("http://", "https://");
        if (mid != uid || name.Length == 0 || face.Length == 0)
            throw new InvalidDataException("作者资料与 UID 不匹配或字段缺失");
        return new(name, face);
    }

    public static BilibiliIdentity CardIdentity(JsonElement data, string uid) => Identity(data.GetProperty("card"), uid);
    public static long CardFollowers(JsonElement data, string uid)
    {
        _ = CardIdentity(data, uid);
        var followers = Number(data, "follower", Number(data.GetProperty("card"), "fans", -1));
        if (followers < 0) throw new InvalidDataException("用户名片未返回有效粉丝量");
        return followers;
    }

    // Desktop modules are an array. Only direct video posts by this UID count:
    // never follow a forwarded item's archive into another owner's video.
    public static BilibiliDynamic DesktopDynamic(JsonElement data, string uid)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("动态列表格式变化");
        BilibiliIdentity identity = null; BilibiliVideo latest = null;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array) continue;
            JsonElement author = default, archive = default;
            foreach (var module in modules.EnumerateArray())
            {
                if (Text(module, "module_type") == "MODULE_TYPE_AUTHOR" && module.TryGetProperty("module_author", out var a)) author = a;
                if (Text(module, "module_type") == "MODULE_TYPE_DYNAMIC" && module.TryGetProperty("module_dynamic", out var dynamic)
                    && dynamic.TryGetProperty("dyn_archive", out var video)) archive = video;
            }
            if (author.ValueKind != JsonValueKind.Object || !author.TryGetProperty("user", out var user)) continue;
            BilibiliIdentity owner;
            try { owner = Identity(user, uid); } catch (InvalidDataException) { continue; }
            identity ??= owner;
            if (Text(item, "type") != "DYNAMIC_TYPE_AV" || archive.ValueKind != JsonValueKind.Object) continue;
            var bvid = Text(archive, "bvid"); var title = Text(archive, "title"); var published = Number(author, "pub_ts");
            if (!bvid.StartsWith("BV", StringComparison.Ordinal) || title.Length == 0 || title.Length > 1024 || published <= 0 || published > 253402300799) continue;
            if (latest == null || published > latest.Published) latest = new(bvid, title, published, owner.Name);
        }
        if (identity == null) throw new InvalidDataException("动态未返回该 UID 的作者信息");
        return new(identity, latest);
    }

    public static BilibiliDynamic WebDynamic(JsonElement data, string uid)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Web 动态格式变化");
        BilibiliIdentity identity = null; BilibiliVideo latest = null;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Object ||
                !modules.TryGetProperty("module_author", out var author)) continue;
            BilibiliIdentity owner;
            try { owner = Identity(author, uid); } catch (InvalidDataException) { continue; }
            identity ??= owner;
            if (Text(item, "type") != "DYNAMIC_TYPE_AV" || !modules.TryGetProperty("module_dynamic", out var dynamic) ||
                !dynamic.TryGetProperty("major", out var major) || !major.TryGetProperty("archive", out var archive)) continue;
            var bvid = Text(archive, "bvid"); var title = Text(archive, "title"); var published = Number(author, "pub_ts");
            if (!bvid.StartsWith("BV", StringComparison.Ordinal) || title.Length == 0 || title.Length > 1024 || published <= 0 || published > 253402300799) continue;
            if (latest == null || published > latest.Published) latest = new(bvid, title, published, owner.Name);
        }
        if (identity == null) throw new InvalidDataException("Web 动态未返回该 UID 的作者信息");
        return new(identity, latest);
    }

    static readonly int[] Permutation = {46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,
        27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,
        0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52};

    public static string WbiKey(string imgUrl, string subUrl)
    {
        var key = Path.GetFileNameWithoutExtension(new Uri(imgUrl).AbsolutePath) +
                  Path.GetFileNameWithoutExtension(new Uri(subUrl).AbsolutePath);
        if (key.Length != 64) throw new InvalidDataException("WBI 签名素材格式变化");
        return new string(Permutation.Take(32).Select(i => key[i]).ToArray());
    }

    public static string Sign(IDictionary<string, string> values, string key, long timestamp)
    {
        var parameters = new SortedDictionary<string, string>(values, StringComparer.Ordinal)
        { ["wts"] = timestamp.ToString(CultureInfo.InvariantCulture) };
        var query = string.Join('&', parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" +
            Uri.EscapeDataString(new string(pair.Value.Where(c => !"!'()*".Contains(c)).ToArray()))));
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(query + key));
        return query + "&w_rid=" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}

static class BilibiliPollPolicy
{
    public static readonly TimeSpan VideoInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan FansInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan SwitchFloor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(6);
    public static TimeSpan SuccessDelay(double jitter) => VideoInterval +
        TimeSpan.FromMinutes(10 * Math.Clamp(jitter, 0, 1));
    public static TimeSpan FailureDelay(int failures, bool risk) => TimeSpan.FromHours(
        Math.Min(risk ? 48 : 24, (risk ? 6 : 2) * Math.Pow(2, Math.Clamp(failures - 1, 0, 5))));
    public static bool IsRisk(int code) => code is 412 or 429 or 403 or -352 or -412 or -509 or -401 or -403 or -799;
}
