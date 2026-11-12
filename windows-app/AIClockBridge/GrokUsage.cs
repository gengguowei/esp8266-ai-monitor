using System.Text.Json;

namespace AIClockBridge;

// Grok Build quota. The CLI stores an OIDC session in %USERPROFILE%\.grok\auth.json
// after `grok login`. The same session calls the CLI's own billing endpoint
// (cli-chat-proxy.grok.com/v1/billing?format=credits). That response is one
// included-credit percentage plus the current weekly or monthly period — there
// is no separate 5-hour window. The token is sent only to that host.
static class GrokUsage
{
    public const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    public const string ClientVersion = "1.0.77";
    public const string MissingMessage = "未找到 Grok 登录（~/.grok/auth.json），请运行 grok login";
    public const string ExpiredMessage = "Grok 登录已过期，请运行 grok login";

    public sealed class Credential
    {
        public string AccessToken;
        public string UserId;
        public string Error;
        public bool AuthenticationError;
    }

    public static Credential ReadAuth(string path, DateTimeOffset now)
    {
        var result = new Credential();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            result.Error = MissingMessage;
            result.AuthenticationError = true;
            return result;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("auth.json 不是对象");
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var token = Str(entry.Value, "key");
                if (string.IsNullOrEmpty(token)) continue;
                var expires = When(entry.Value, "expires_at");
                if (expires.HasValue && expires.Value <= now)
                {
                    result.Error = ExpiredMessage;
                    result.AuthenticationError = true;
                    return result;
                }
                result.AccessToken = token;
                result.UserId = Str(entry.Value, "user_id") ?? "";
                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or InvalidDataException)
        {
            result.Error = "无法读取 Grok 登录：" + ex.Message;
            result.AuthenticationError = true;
            return result;
        }
        result.Error = MissingMessage;
        result.AuthenticationError = true;
        return result;
    }

    /// Maps a billing body onto the weekly slot of ProviderUsage. PrimaryPct
    /// stays empty: Grok has a single credit window, and the screen collapses
    /// to one column when the 5h number is absent.
    public static ProviderUsage Parse(string json, DateTimeOffset now)
    {
        var usage = new ProviderUsage();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch
        {
            usage.Error = "Grok 用量响应不是 JSON";
            return usage;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var config = root;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("config", out var nested)
                && nested.ValueKind == JsonValueKind.Object)
                config = nested;
            if (config.ValueKind != JsonValueKind.Object)
            {
                usage.Error = "Grok 用量响应不是 JSON";
                return usage;
            }

            var pct = Num(config, "creditUsagePercent") ?? Num(config, "credit_usage_percent");
            if (pct == null)
            {
                var limit = Cent(config, "monthlyLimit") ?? Cent(config, "monthly_limit");
                var used = Cent(config, "used");
                if (limit is > 0 && used != null) pct = 100.0 * used.Value / limit.Value;
            }
            if (pct == null)
            {
                usage.Error = "Grok 用量响应里没有额度百分比";
                return usage;
            }

            var periodObj = Obj(config, "currentPeriod") ?? Obj(config, "current_period");
            var periodType = periodObj == null ? null : Str(periodObj.Value, "type");
            var end = periodObj == null ? null : Str(periodObj.Value, "end");
            end ??= Str(config, "billingPeriodEnd") ?? Str(config, "billing_period_end");
            var monthly = periodType != null
                && periodType.Contains("MONTH", StringComparison.OrdinalIgnoreCase);

            usage.Period = monthly ? "month" : "week";
            usage.WeeklyPct = Math.Clamp(pct.Value, 0, 100);
            usage.WeeklyResetMin = MinutesUntil(end, now);
            usage.FetchedAt = now.UtcDateTime;
            return usage;
        }
    }

    static int? MinutesUntil(string iso, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            return null;
        var min = (int)Math.Ceiling((end - now).TotalMinutes);
        return Math.Max(0, min);
    }

    static JsonElement? Obj(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Object)
            return v;
        return null;
    }

    static string Str(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    static double? Num(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        return null;
    }

    static double? Cent(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("val", out var inner)
            && inner.ValueKind == JsonValueKind.Number && inner.TryGetDouble(out var c))
            return c;
        return null;
    }

    static DateTimeOffset? When(JsonElement obj, string key)
    {
        var text = Str(obj, key);
        if (string.IsNullOrEmpty(text)) return null;
        if (DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
            return t;
        return null;
    }
}
