using System.Globalization;
using System.Text.RegularExpressions;

namespace HostlistDirectory;

public static partial class Normalizer
{
    private static readonly string[] ExactFormats =
    [
        "d MMM yyyy HH:mm", "d MMM yyyy HH:mm:ss", "dd MMM yyyy HH:mm", "dd MMM yyyy HH:mm:ss",
        "d MMMM yyyy HH:mm", "d MMMM yyyy HH:mm:ss", "dd MMMM yyyy HH:mm:ss",
        "d MMM yyyy", "d MMMM yyyy", "MMMM d yyyy", "MMM d yyyy", "MMMM d yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd",
        "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd", "yyyy.MM.dd", "dd.MM.yyyy", "dd.MM.yyyy HH:mm",
        "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy", "MMM d HH:mm:ss yyyy", "MMM dd HH:mm:ss yyyy",
        "MM/dd/yyyy HH:mm", "M/d/yyyy HH:mm", "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
    ];

    /// <summary>Turns the many date styles used in list headers into ISO 8601 UTC. Returns null if it can't be parsed confidently.</summary>
    public static string? ToIsoDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Unix timestamp
        if (UnixRegex().IsMatch(s) && long.TryParse(s, out var unix))
        {
            var dto = s.Length > 10 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
            return Iso(dto);
        }

        // "Thu, 15 Oct 2026 at 17:57:51 GMT" / "29 October 2026 15:30:38 (UTC)" / "24 Oct 2026 22:48 UTC+7" / "Mon Jun 26 13:29:58 CEST 2023"
        s = s.Replace(" at ", " ", StringComparison.OrdinalIgnoreCase);
        s = ParenZoneRegex().Replace(s, " $1 ");
        s = ParenRegex().Replace(s, " ");
        s = OrdinalSuffixRegex().Replace(s, "$1");
        s = DayNameRegex().Replace(s, "");
        s = s.Replace(",", " ");
        var assumeUtc = true;
        var offset = TimeSpan.Zero;
        var zone = ZoneRegex().Match(s);
        if (zone.Success)
        {
            var abbr = zone.Groups["abbr"].Value.ToUpperInvariant();
            offset = abbr.Length > 0 && ZoneOffsets.TryGetValue(abbr, out var baseOffset) ? baseOffset : TimeSpan.Zero;
            if (zone.Groups["off"].Success && ParseOffset(zone.Groups["off"].Value) is { } extra) offset = extra; // "UTC+7", "GMT+05:30", "+0200"
            assumeUtc = offset == TimeSpan.Zero;
            s = s.Remove(zone.Index, zone.Length);
        }
        s = WhitespaceRegex().Replace(s, " ").Trim();

        // ISO-ish strings parse directly
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) && IsoLikeRegex().IsMatch(s))
            return Iso(assumeUtc ? parsed : new DateTimeOffset(parsed.DateTime, offset));

        if (DateTime.TryParseExact(s, ExactFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            var dto = assumeUtc ? new DateTimeOffset(DateTime.SpecifyKind(exact, DateTimeKind.Utc)) : new DateTimeOffset(DateTime.SpecifyKind(exact, DateTimeKind.Unspecified), offset);
            return Iso(dto);
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose) && loose.Year > 1995)
            return Iso(loose);

        return null;
    }

    private static readonly Dictionary<string, TimeSpan> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = TimeSpan.Zero, ["GMT"] = TimeSpan.Zero, ["Z"] = TimeSpan.Zero,
        ["CET"] = TimeSpan.FromHours(1), ["CEST"] = TimeSpan.FromHours(2), ["EET"] = TimeSpan.FromHours(2), ["EEST"] = TimeSpan.FromHours(3),
        ["BST"] = TimeSpan.FromHours(1), ["IST"] = new TimeSpan(5, 30, 0), ["JST"] = TimeSpan.FromHours(9),
        ["AEST"] = TimeSpan.FromHours(10), ["AEDT"] = TimeSpan.FromHours(11),
        ["EST"] = TimeSpan.FromHours(-5), ["EDT"] = TimeSpan.FromHours(-4), ["CST"] = TimeSpan.FromHours(-6), ["CDT"] = TimeSpan.FromHours(-5),
        ["MST"] = TimeSpan.FromHours(-7), ["MDT"] = TimeSpan.FromHours(-6), ["PST"] = TimeSpan.FromHours(-8), ["PDT"] = TimeSpan.FromHours(-7),
    };

    private static TimeSpan? ParseOffset(string off)
    {
        var m = OffsetRegex().Match(off);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var min = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        var ts = new TimeSpan(h, min, 0);
        return m.Groups[1].Value == "-" ? -ts : ts;
    }

    public static string Iso(DateTimeOffset dto) => dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"8 hours", "4 days (update frequency)", "12h", "1 week" → hours.</summary>
    public static double? ToHours(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = DurationRegex().Match(raw);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        var unit = m.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            var u when u.StartsWith("min") => Math.Round(n / 60, 2),
            var u when u.StartsWith('h') => n,
            var u when u.StartsWith('d') => n * 24,
            var u when u.StartsWith('w') => n * 24 * 7,
            var u when u.StartsWith("mo") => n * 24 * 30,
            _ => null,
        };
    }

    public static List<string> NormaliseTags(IEnumerable<string>? tags)
    {
        if (tags is null) return [];
        return [.. tags
            .SelectMany(t => (t ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim().ToLowerInvariant().Replace(' ', '-'))
            .Select(t => t switch { "tracking" or "tracker" => "trackers", "ad" => "ads", _ => t })
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)];
    }

    public static string Slug(string value)
    {
        var s = value.ToLowerInvariant();
        s = s.Replace("++", "-plusplus").Replace("+", "-plus");
        s = NonSlugRegex().Replace(s, "-");
        return s.Trim('-');
    }

    [GeneratedRegex(@"^\d{9,13}$")] private static partial Regex UnixRegex();
    [GeneratedRegex(@"(\d)(st|nd|rd|th)\b", RegexOptions.IgnoreCase)] private static partial Regex OrdinalSuffixRegex();
    [GeneratedRegex(@"^\s*(mon|tue|wed|thu|fri|sat|sun)[a-z]*\.?,?\s+", RegexOptions.IgnoreCase)] private static partial Regex DayNameRegex();
    [GeneratedRegex(@"(?<![\w:+-])(?:(?<abbr>UTC|GMT|Z|CEST|CET|EEST|EET|BST|EST|EDT|CST|CDT|MST|MDT|PST|PDT|IST|JST|AEST|AEDT)(?<off>[+-]\d{1,2}(?::?\d{2})?)?|(?<=\s)(?<off>[+-]\d{2}:?\d{2}))(?![\w])")] private static partial Regex ZoneRegex();
    [GeneratedRegex(@"^([+-])(\d{1,2})(?::?(\d{2}))?$")] private static partial Regex OffsetRegex();
    [GeneratedRegex(@"\((UTC|GMT)\)", RegexOptions.IgnoreCase)] private static partial Regex ParenZoneRegex();
    [GeneratedRegex(@"\([^)]*\)")] private static partial Regex ParenRegex();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")] private static partial Regex IsoLikeRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(minutes?|mins?|hours?|hrs?|h|days?|d|weeks?|w|months?)\b", RegexOptions.IgnoreCase)] private static partial Regex DurationRegex();
    [GeneratedRegex(@"[^a-z0-9.]+")] private static partial Regex NonSlugRegex();
}
