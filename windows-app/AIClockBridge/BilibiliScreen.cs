using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace AIClockBridge;

static class BilibiliScreen
{
    public static readonly Color Background = Color.Black;
    static readonly Color Ink = Color.FromArgb(247, 249, 252);
    static readonly Color Secondary = Color.FromArgb(175, 190, 207);

    // Render once on the bridge. Mirror and firmware consume the exact same
    // RGB565 pixels, including Chinese type and avatar; no CJK font in firmware.
    public static byte[][] Render(BilibiliProfile profile, byte[] avatar)
    {
        using var measure = new Bitmap(240, 240);
        using var graphics = Graphics.FromImage(measure);
        using var titleFont = new Font("Microsoft YaHei", 20, FontStyle.Regular, GraphicsUnit.Pixel);
        var lines = Wrap(profile.Video?.Title ?? "等待获取最新视频", graphics, titleFont, 212);
        var pages = Math.Max(1, (lines.Count + 3) / 4);
        var result = new byte[pages][];
        Image image = null;
        MemoryStream imageStream = null;
        try
        {
            if (avatar?.Length > 0)
            {
                try { imageStream = new MemoryStream(avatar); image = Image.FromStream(imageStream); } catch { }
            }
            for (var page = 0; page < pages; page++)
            {
                using var bitmap = new Bitmap(240, 240);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Background);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using var ink = new SolidBrush(Ink);
                using var secondary = new SolidBrush(Secondary);
                using var nameFont = new Font("Microsoft YaHei", 22, FontStyle.Regular, GraphicsUnit.Pixel);
                using var fansFont = new Font("Microsoft YaHei", 16, FontStyle.Regular, GraphicsUnit.Pixel);
                using var smallFont = new Font("Microsoft YaHei", 12, FontStyle.Regular, GraphicsUnit.Pixel);
                using var dateFont = new Font("Microsoft YaHei", 14, FontStyle.Regular, GraphicsUnit.Pixel);
                using var timeFont = new Font("Microsoft YaHei", 16, FontStyle.Regular, GraphicsUnit.Pixel);
                using var avatarClip = new GraphicsPath();
                avatarClip.AddEllipse(14, 14, 48, 48);
                var saved = g.Save(); g.SetClip(avatarClip);
                if (image != null) g.DrawImage(image, new Rectangle(14, 14, 48, 48));
                else { using var dark = new SolidBrush(Background); g.FillEllipse(secondary, 14, 14, 48, 48); g.DrawString("UP", fansFont, dark, 25, 27); }
                g.Restore(saved);
                using var nameFormat = new StringFormat(StringFormat.GenericTypographic)
                { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(string.IsNullOrEmpty(profile.Name) ? "UP主" : profile.Name, nameFont, ink,
                    new RectangleF(74, 14, 152, 30), nameFormat);
                g.DrawString("粉丝", smallFont, secondary, 74, 45, StringFormat.GenericTypographic);
                g.DrawString(profile.Followers?.ToString("N0", CultureInfo.InvariantCulture) ?? "—", fansFont, ink,
                    105, 43, StringFormat.GenericTypographic);
                using var divider = new Pen(Color.FromArgb(40, 52, 71));
                g.DrawLine(divider, 14, 76, 226, 76);
                var videoLabel = profile.VideoSource == "动态备用" ? "最近视频 · 备用" : "最新视频";
                g.DrawString(pages > 1 ? $"{videoLabel}  {page + 1}/{pages}" : videoLabel, smallFont, secondary,
                    14, 85, StringFormat.GenericTypographic);
                for (var i = 0; i < 4 && page * 4 + i < lines.Count; i++)
                    g.DrawString(lines[page * 4 + i], titleFont, ink, 14, 105 + i * 25, StringFormat.GenericTypographic);
                if (profile.Video != null)
                {
                    var published = DateTimeOffset.FromUnixTimeSeconds(profile.Video.Published).ToOffset(TimeSpan.FromHours(8));
                    g.DrawString(published.ToString("yyyy-MM-dd"), dateFont, secondary, 14, 211, StringFormat.GenericTypographic);
                    using var right = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
                    g.DrawString(published.ToString("HH:mm"), timeFont, ink, new RectangleF(170, 209, 56, 22), right);
                }
                result[page] = Rgb565.Encode(bitmap);
            }
        }
        finally { image?.Dispose(); imageStream?.Dispose(); }
        return result;
    }

    public static List<string> Wrap(string text, Graphics g, Font font, float width)
    {
        var lines = new List<string>(); var line = "";
        var tokens = new List<string>(); var asciiWord = "";
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element.Length == 1 && char.IsAsciiLetterOrDigit(element[0])) { asciiWord += element; continue; }
            if (asciiWord.Length > 0) { tokens.Add(asciiWord); asciiWord = ""; }
            tokens.Add(element);
        }
        if (asciiWord.Length > 0) tokens.Add(asciiWord);
        foreach (var token in tokens)
        {
            var element = token;
            if (element == "\r") continue;
            if (element == "\n") { lines.Add(line); line = ""; continue; }
            if (line.Length > 0 && g.MeasureString(line + element, font, int.MaxValue, StringFormat.GenericTypographic).Width > width)
            { lines.Add(line.TrimEnd()); line = ""; }
            // Keep short words/numbers intact. Exceptionally long Latin words
            // still split into graphemes to stay within the physical screen.
            if (g.MeasureString(element, font, int.MaxValue, StringFormat.GenericTypographic).Width > width)
            {
                foreach (var part in WrapLongToken(element, g, font, width)) lines.Add(part);
                continue;
            }
            line += element;
        }
        if (line.Length > 0 || lines.Count == 0) lines.Add(line.TrimEnd());
        return lines;
    }

    static IEnumerable<string> WrapLongToken(string text, Graphics g, Font font, float width)
    {
        var line = "";
        foreach (var c in text)
        {
            if (line.Length > 0 && g.MeasureString(line + c, font, int.MaxValue, StringFormat.GenericTypographic).Width > width)
            { yield return line; line = ""; }
            line += c;
        }
        if (line.Length > 0) yield return line;
    }
}
