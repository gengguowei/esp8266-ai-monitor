using System.Drawing;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

// Watchlist quotes from Tencent's free endpoint (no key, realtime for A-shares,
// supports sh/sz/hk/us prefixes in one batch request, GBK-encoded response):
//   http://qt.gtimg.cn/q=sh600519,hk00700,usAAPL
// Polled every 5s; the device and the mirror both render the pre-formatted
// strings so the firmware stays dumb (and ASCII-only: it shows the code, the
// CJK company name is only used by the mirror window).
sealed class StockMonitor
{
    public record Row(string Code, string Name, string Price, string Pct, int Up);

    const string SymbolsKey = "stock_symbols";

    /// Comma-separated Tencent symbols ("sh600519,hk00700,usAAPL").
    public static string[] Symbols
    {
        get
        {
            var raw = Settings.Get(SymbolsKey);
            if (raw.Length == 0) raw = "sh000001";
            return raw.Replace('，', ',') // CN comma happens
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize).ToArray();
        }
        set => Settings.Set(SymbolsKey, string.Join(",", value));
    }

    /// Market prefix must be lowercase but the US ticker must stay UPPERCASE
    /// ("usaapl" gets v_pv_none_match back), so normalize both halves.
    /// HK codes must be zero-padded to 5 digits ("hk1810" -> "hk01810",
    /// otherwise Tencent returns v_pv_none_match).
    static string Normalize(string s)
    {
        var t = s.Trim();
        if (t.Length <= 2) return t.ToLowerInvariant();
        var prefix = t[..2].ToLowerInvariant();
        var body = t[2..].ToUpperInvariant();
        if (prefix == "hk" && body.Length < 5 && body.All(char.IsDigit))
            body = body.PadLeft(5, '0');
        return prefix + body;
    }

    // Rebuildable for the same reason as the quota client: a proxy toggled while
    // we run would otherwise freeze the quotes until the process restarts.
    static readonly ResilientHttpClient Http = new("stock", TimeSpan.FromSeconds(5));
    static readonly Encoding Gbk;

    static StockMonitor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding enc;
        try { enc = Encoding.GetEncoding("GB18030"); }
        catch { enc = Encoding.Latin1; } // names garble, numbers still parse
        Gbk = enc;
    }

    readonly object _lock = new();
    Row[] _rows = Array.Empty<Row>();
    System.Threading.Timer _timer;

    /// Last failure, or null while healthy. Quotes used to fail into a bare
    /// `catch {}`: the previous rows stayed on screen and nothing anywhere said
    /// they had stopped updating, which is exactly how a proxy outage went
    /// unnoticed. The tray now shows this.
    string _error;
    string _namesError;
    DateTime? _fetchedAt;

    public string Error { get { lock (_lock) return _error ?? _namesError; } }
    public DateTime? FetchedAt { get { lock (_lock) return _fetchedAt; } }

    /// Diagnostic for "prices update but the device shows no Chinese names":
    /// 0 means no strips have ever been rendered, so there is nothing for the
    /// device to fetch and the fault is on this side.
    public int NamesRev { get { lock (_lock) return _namesRev; } }
    public int NamesBytes { get { lock (_lock) return _namesData?.Length ?? 0; } }

    /// The 5s timer and the 5s HTTP timeout are the same number, so a slow proxy
    /// used to pile overlapping fetches on top of each other - and an older
    /// response could land after a newer one and roll the prices backwards.
    int _fetching;

    // The device's font is ASCII-only, so CJK company names go down as
    // rendered RGB565 strips: one
    // NameW x NameH strip per row, wire format [1 byte count][strips...].
    // names_rev in /stock tells the device when to re-fetch.
    public const int NameW = 156, NameH = 16;
    int _namesRev;
    byte[] _namesData = { 0 };
    string _lastNamesKey = "";

    public void Start()
    {
        _ = Fetch();
        _timer = new System.Threading.Timer(_ => _ = Fetch(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public Row[] Snapshot
    {
        get { lock (_lock) return _rows; }
    }

    public byte[] ToJson()
    {
        var stocks = Snapshot.Select(r => new Dictionary<string, object>
        {
            ["code"] = r.Code, ["name"] = r.Name, ["price"] = r.Price,
            ["pct"] = r.Pct, ["up"] = r.Up,
        }).ToArray();
        int rev;
        lock (_lock) rev = _namesRev;
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["stocks"] = stocks, ["names_rev"] = rev,
        });
    }

    /// [1 byte count][NameW x NameH RGB565 big-endian per row...]
    public byte[] NamesRgb565
    {
        get { lock (_lock) return _namesData; }
    }

    /// Revision and payload together, so the serial sender cannot pair a stale
    /// rev with fresh bytes (or the reverse) when the watchlist changes mid-send.
    public bool TryGetNames(out int rev, out byte[] data)
    {
        lock (_lock)
        {
            rev = _namesRev;
            data = _namesData;
            return data != null && data.Length > 1;
        }
    }

    void RenderNamesIfNeeded(Row[] parsed)
    {
        var top = parsed.Take(4).ToArray();
        var key = string.Join("\n", top.Select(r => r.Name));
        lock (_lock) if (key == _lastNamesKey) return;
        using var ms = new MemoryStream();
        ms.WriteByte((byte)top.Length);
        foreach (var row in top)
        {
            using var bmp = new Bitmap(NameW, NameH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Microsoft YaHei UI", 9f);
                using var brush = new SolidBrush(Color.FromArgb(184, 184, 184));
                using var right = new StringFormat
                {
                    Alignment = StringAlignment.Far,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                };
                g.DrawString(row.Name, font, brush, new RectangleF(0, 0, NameW, NameH), right);
            }
            ms.Write(Rgb565.Encode(bmp));
        }
        lock (_lock)
        {
            _lastNamesKey = key;
            _namesData = ms.ToArray();
            _namesRev++;
            _namesError = null;
            Log.Write($"[stock] rendered {top.Length} CJK name strips"
                      + $" ({_namesData.Length} bytes, names_rev={_namesRev});"
                      + " served over USB name frames or HTTP /stock/names.raw");
        }
    }

    async Task Fetch()
    {
        // One in flight at a time; a tick that arrives while the previous fetch
        // is still waiting is simply dropped.
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;
        try
        {
            var symbols = Symbols;
            if (symbols.Length == 0)
            {
                lock (_lock) { _rows = Array.Empty<Row>(); _error = null; }
                return;
            }
            Row[] rows;
            try
            {
                var bytes = await Http.Client.GetByteArrayAsync(
                    "http://qt.gtimg.cn/q=" + string.Join(",", symbols));
                Http.ReportSuccess();
                rows = Parse(Gbk.GetString(bytes), symbols);
                lock (_lock)
                {
                    _rows = rows;
                    _error = rows.Length == 0 ? "行情响应里没有可用的报价" : null;
                    _fetchedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Keep the previous rows - stale quotes beat a blank tab - but
                // record why, so a persistent outage is visible instead of
                // looking like a market that stopped moving.
                Http.ReportTransportFailure(ex.Message);
                lock (_lock) _error = $"行情请求失败：{ex.Message}";
                Log.Write($"[stock] fetch failed: {ex.Message}");
                return;
            }

            // Rendering the CJK name strips is GDI work, not network work. Keep
            // it out of the catch above: a font or GDI failure there would be
            // reported as a fetch failure, blame the wrong thing, and count
            // toward the HttpClient rebuild. It is also precisely the failure
            // that looks like "prices update but the names never appear on the
            // device", since the device pulls the strips over a separate HTTP
            // route while prices arrive over the serial link.
            try
            {
                RenderNamesIfNeeded(rows);
            }
            catch (Exception ex)
            {
                lock (_lock) _namesError = $"中文名称条渲染失败：{ex.Message}";
                Log.Write($"[stock] name strip render failed: {ex}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    /// Response is lines of `v_sh600519="1~贵州茅台~600519~1212.00~...";`
    /// fields split by "~": [1]=name [3]=price [31]=change [32]=change%.
    static Row[] Parse(string text, string[] order)
    {
        var bySymbol = new Dictionary<string, Row>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq < 0 || !line.StartsWith("v_")) continue;
            var symbol = line[2..eq].ToLowerInvariant();
            var f = line[(eq + 1)..].Trim('"', ';', '\r').Split('~');
            if (f.Length <= 32 ||
                !double.TryParse(f[3], out var price) ||
                !double.TryParse(f[31], out var chg) ||
                !double.TryParse(f[32], out var pct)) continue;
            // lowercase key so the case-normalized query symbol still matches
            bySymbol[symbol] = new Row(
                DisplayCode(symbol), f[1], FormatPrice(price),
                $"{pct:+0.00;-0.00}%", chg > 0 ? 1 : (chg < 0 ? -1 : 0));
        }
        return order.Select(s => s.ToLowerInvariant())
            .Where(bySymbol.ContainsKey).Select(s => bySymbol[s]).ToArray();
    }

    /// "sh600519" -> "600519", "usAAPL" -> "AAPL", "hk00700" -> "00700"
    static string DisplayCode(string symbol)
    {
        foreach (var p in new[] { "sh", "sz", "bj", "hk", "us" })
        {
            if (symbol.StartsWith(p) && symbol.Length > 2) return symbol[2..].ToUpperInvariant();
        }
        return symbol.ToUpperInvariant();
    }

    static string FormatPrice(double p)
    {
        if (p >= 10000) return $"{p:F0}";
        if (p >= 1000) return $"{p:F1}";
        return $"{p:F2}";
    }
}
