using System.Text.Json;

namespace HostlistDirectory;

/// <summary>A database file plus everything derived from its location and contents.</summary>
public sealed class DatabaseRecord
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required string Key { get; init; }
    public required string FormatFolder { get; init; }
    public required string AuthorFolder { get; init; }
    public required DatabaseEntry Entry { get; init; }
    public Dictionary<string, string> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Patterns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = [];

    public IReadOnlyList<string> Mirrors =>
        [.. new[] { Entry.MainMirror }
            .Concat(Entry.AlternativeMirrors ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}

public static class Database
{
    public static readonly string[] KnownFolders =
        ["adblock", "DNSMasq", "domains-subdomains", "hosts", "hosts-compressed", "pac", "rpz", "unbound", "wildcard-asterisk", "wildcard-domains"];

    /// <summary>Folder a newly added list should go in, by sniffed format.</summary>
    public static string FolderForFormat(string detected) => detected switch
    {
        "domains" => "domains-subdomains",
        "dnsmasq" => "DNSMasq",
        "hosts" or "hosts-compressed" or "adblock" or "rpz" or "unbound" or "wildcard-asterisk" or "pac" => detected,
        _ => "hosts",
    };

    public static List<DatabaseRecord> Load(string databaseDir, Action<string> warn)
    {
        var records = new List<DatabaseRecord>();
        if (!Directory.Exists(databaseDir))
        {
            warn($"Database folder not found: {databaseDir}");
            return records;
        }

        foreach (var file in Directory.EnumerateFiles(databaseDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = System.IO.Path.GetRelativePath(databaseDir, file).Replace('\\', '/');
            var parts = rel.Split('/');
            if (parts.Length < 3)
            {
                warn($"{rel}: expected database/<format>/<author>/<list>.json, skipping");
                continue;
            }

            DatabaseEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<DatabaseEntry>(File.ReadAllText(file), Json.Read);
            }
            catch (JsonException ex)
            {
                warn($"{rel}: invalid JSON ({ex.Message}), skipping");
                continue;
            }
            if (entry is null) continue;

            var key = string.Join('/', parts[..^1].Select(Normalizer.Slug).Append(Normalizer.Slug(System.IO.Path.GetFileNameWithoutExtension(file))));
            var record = new DatabaseRecord
            {
                Path = file,
                RelativePath = rel,
                Key = key,
                FormatFolder = parts[0],
                AuthorFolder = parts[1],
                Entry = entry,
            };

            if (entry.Overrides is not null)
                foreach (var (k, v) in entry.Overrides)
                    if (!string.IsNullOrWhiteSpace(v)) record.Overrides[k] = v.Trim();
            if (entry.Patterns is not null)
                foreach (var (k, v) in entry.Patterns)
                    record.Patterns[k] = v;

            ApplyLegacy(record);
            records.Add(record);
        }
        return records;
    }

    /// <summary>v1 "detectionMethods": user-specified → override, text-locate/regex → custom pattern.</summary>
    private static void ApplyLegacy(DatabaseRecord record)
    {
        if (record.Entry.DetectionMethods is null) return;
        foreach (var (field, method) in record.Entry.DetectionMethods)
        {
            switch (method.Type?.ToLowerInvariant())
            {
                case "user-specified":
                    var value = method.Value switch
                    {
                        { ValueKind: JsonValueKind.String } s => s.GetString(),
                        { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(value) && !HeaderParser.IsPlaceholder(value) && !record.Overrides.ContainsKey(field))
                        record.Overrides[field] = value.Trim();
                    break;
                case "text-locate" or "regex":
                    if (method.Patterns is { Count: > 0 } && !record.Patterns.ContainsKey(field))
                        record.Patterns[field] = method.Patterns;
                    break;
            }
        }
    }

    /// <summary>Offline checks. Returns (errors, warnings).</summary>
    public static (List<string> Errors, List<string> Warnings) Validate(List<DatabaseRecord> records)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var mirrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var r in records)
        {
            var e = r.Entry;
            var at = r.RelativePath;
            if (string.IsNullOrWhiteSpace(e.Name)) errors.Add($"{at}: 'name' is required");
            if (string.IsNullOrWhiteSpace(e.MainMirror)) errors.Add($"{at}: 'mainMirror' is required");
            foreach (var url in r.Mirrors)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    errors.Add($"{at}: '{url}' is not a valid http(s) URL");
                else if (uri.Scheme == "http")
                    warnings.Add($"{at}: '{url}' uses plain http");
            }
            if (e.AlternativeMirrors?.Any(string.IsNullOrWhiteSpace) == true)
                warnings.Add($"{at}: alternativeMirrors contains an empty string (use [] instead)");
            if (!string.IsNullOrWhiteSpace(e.MainMirror))
            {
                if (mirrors.TryGetValue(e.MainMirror, out var other)) errors.Add($"{at}: mainMirror duplicates {other}");
                else mirrors[e.MainMirror] = at;
            }
            if (keys.TryGetValue(r.Key, out var dupe)) errors.Add($"{at}: key '{r.Key}' collides with {dupe}");
            else keys[r.Key] = at;

            if (!KnownFolders.Contains(r.FormatFolder, StringComparer.OrdinalIgnoreCase))
                warnings.Add($"{at}: unknown format folder '{r.FormatFolder}' (known: {string.Join(", ", KnownFolders)})");
            if (e.Tags is null || e.Tags.Count == 0) warnings.Add($"{at}: no tags");
            foreach (var (field, patterns) in r.Patterns)
                foreach (var p in patterns)
                {
                    try { _ = new System.Text.RegularExpressions.Regex(p); }
                    catch (ArgumentException ex) { errors.Add($"{at}: bad regex for '{field}': {ex.Message}"); }
                }
            foreach (var field in r.Overrides.Keys.Concat(r.Patterns.Keys))
                if (!HeaderParser.AllFields.Contains(field, StringComparer.OrdinalIgnoreCase))
                    warnings.Add($"{at}: unknown field '{field}' in overrides/patterns");
            if (e.Extra is { Count: > 0 })
                foreach (var k in e.Extra.Keys.Where(k => k != "$schema"))
                    warnings.Add($"{at}: unknown property '{k}'");
        }
        return (errors, warnings);
    }
}
