using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HostlistDirectory;

public sealed class BuildOptions
{
    public required string RootDir { get; init; }
    public string DatabaseDir => Path.Combine(RootDir, "database");
    public string OutputFile { get; init; } = "";
    public int Parallelism { get; init; } = 6;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
    public bool GitHubLookup { get; init; } = true;
    public string? Only { get; init; }
}

public sealed class DirectoryBuilder(BuildOptions options, Log log)
{
    private readonly ConcurrentBag<string> _failures = [];
    private readonly ConcurrentBag<string> _warnings = [];

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        log.Info($"HostlistDirectory builder: {options.DatabaseDir}");

        var records = Database.Load(options.DatabaseDir, w => Warn("database", w));
        var (errors, dbWarnings) = Database.Validate(records);
        foreach (var w in dbWarnings) log.Detail($"note: {w}");
        foreach (var e in errors) Warn("database", e);

        records = [.. records.Where(r => r.Entry.Enabled != false && !string.IsNullOrWhiteSpace(r.Entry.MainMirror))];
        if (options.Only is { } only)
            records = [.. records.Where(r => r.Key.Contains(only, StringComparison.OrdinalIgnoreCase))];
        log.Info($"{records.Count} lists in database");

        var previous = LoadPrevious(options.OutputFile);
        var now = Normalizer.Iso(DateTimeOffset.UtcNow);

        using var downloader = new Downloader(options.Timeout);
        var github = new GitHubEnricher(downloader, options.GitHubLookup);
        var results = new ConcurrentDictionary<string, DirectoryEntry>();

        await Parallel.ForEachAsync(records, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = ct },
            async (record, token) =>
            {
                var entry = await ProcessAsync(record, previous, downloader, github, now, token);
                results[record.Key] = entry;
            });

        var entries = results.Values
            .OrderBy(e => e.FormatType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Author, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < entries.Count; i++) entries[i].Id = i + 1;

        var directory = new DirectoryFile
        {
            Info = new DirectoryInfoBlock
            {
                LastUpdated = now,
                TotalLists = entries.Count,
                ReachableLists = entries.Count(e => e.Status != "unreachable"),
                Formats = new SortedDictionary<string, int>(entries.GroupBy(e => e.FormatType ?? "unknown").ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal),
            },
            Entries = entries,
        };

        // A mass failure is almost always our network, not the lists: don't publish a directory full of "unreachable".
        if (entries.Count > 0 && directory.Info.ReachableLists < entries.Count / 2)
        {
            log.Fail($"Only {directory.Info.ReachableLists}/{entries.Count} lists reachable; not writing Directory.json");
            WriteSummary(directory, sw.Elapsed);
            return 1;
        }

        if (options.Only is null) WriteIfChanged(directory);
        else log.Info("--only was used, Directory.json not written");

        WriteSummary(directory, sw.Elapsed);
        log.Info($"Done in {sw.Elapsed:mm\\:ss}: {directory.Info.ReachableLists}/{directory.Info.TotalLists} reachable, {_failures.Count} failures, {_warnings.Count} warnings");
        return 0;
    }

    private async Task<DirectoryEntry> ProcessAsync(DatabaseRecord record, PreviousDirectory previous, Downloader downloader,
        GitHubEnricher github, string now, CancellationToken ct)
    {
        var db = record.Entry;
        var (fetch, failures) = await downloader.FetchAsync(record.Mirrors, () => new ListAnalyzer(record.Patterns), ct);
        var prev = previous.Find(record.Key, db.MainMirror);
        var repoTask = github.LookupAsync(db.Homepage, db.MainMirror, ct);

        var entry = new DirectoryEntry
        {
            Key = record.Key,
            Author = db.Author ?? record.AuthorFolder,
            Name = db.Name,
            Tags = Normalizer.NormaliseTags(db.Tags),
            MainMirror = db.MainMirror,
            AlternativeMirrors = [.. (db.AlternativeMirrors ?? []).Where(u => !string.IsNullOrWhiteSpace(u))],
            FormatType = record.FormatFolder,
            Notes = db.Notes,
            LastChecked = now,
        };

        // Header values for this run, or the last good values if every mirror failed.
        Dictionary<string, string> header;
        if (fetch is not null)
        {
            var a = fetch.Analysis;
            header = a.Metadata;
            entry.Status = fetch.FromAlternative ? "mirror" : "ok";
            entry.ServedFrom = fetch.Url;
            entry.LastSuccess = now;
            entry.Entries = a.Entries;
            entry.DetectedFormat = a.DetectedFormat;
            entry.SizeBytes = fetch.SizeBytes;
            entry.Sha256 = fetch.Sha256;
            log.Ok($"{record.Key}: {a.Entries:N0} entries, {a.DetectedFormat}{(fetch.FromAlternative ? $" (via mirror {fetch.Url})" : "")}");

            if (fetch.FromAlternative)
                Warn(record.Key, $"main mirror failed, served from {fetch.Url}: {string.Join("; ", failures)}");
            CheckSanity(record, a, header);

            if (!header.ContainsKey(HeaderParser.LastModified) && fetch.HttpLastModified is { } lm)
                header[HeaderParser.LastModified] = Normalizer.Iso(lm);
        }
        else
        {
            entry.Status = "unreachable";
            var reason = string.Join("; ", failures);
            _failures.Add($"{record.Key}: {reason}");
            log.Fail($"{record.Key}: unreachable ({reason})");
            header = prev is null ? [] : PreviousAsHeader(prev);
            if (prev is not null)
            {
                entry.Entries = prev.Entries;
                entry.DetectedFormat = prev.DetectedFormat;
                entry.SizeBytes = prev.SizeBytes;
                entry.Sha256 = prev.Sha256;
                entry.ServedFrom = prev.ServedFrom;
                entry.LastSuccess = prev.LastSuccess;
            }
        }

        var repo = await repoTask;
        entry.Repository = repo;

        string? Pick(string field, string? databaseValue, string? repoValue = null)
        {
            if (record.Overrides.TryGetValue(field, out var forced)) return forced;
            if (header.TryGetValue(field, out var fromHeader) && !string.IsNullOrWhiteSpace(fromHeader)) return fromHeader;
            if (!string.IsNullOrWhiteSpace(databaseValue)) return databaseValue.Trim();
            return string.IsNullOrWhiteSpace(repoValue) ? null : repoValue.Trim();
        }

        entry.Title = Pick(HeaderParser.Title, db.Name);
        entry.Description = Pick(HeaderParser.Description, db.Description, repo?.Description);
        entry.Homepage = Pick(HeaderParser.Homepage, db.Homepage, repo?.Url);
        // Licence: database value beats the header here because headers often just link to a LICENSE file.
        entry.License = record.Overrides.GetValueOrDefault(HeaderParser.License)
                        ?? NonEmpty(db.License)
                        ?? LicenseFromHeader(header.GetValueOrDefault(HeaderParser.License), repo?.License)
                        ?? repo?.License;
        entry.Expiry = Pick(HeaderParser.Expiry, null);
        entry.ExpiresHours = Normalizer.ToHours(entry.Expiry);
        entry.Version = Pick(HeaderParser.Version, null);
        var lastModified = Pick(HeaderParser.LastModified, null);
        entry.LastModified = Normalizer.ToIsoDate(lastModified) ?? lastModified;
        var declared = Pick(HeaderParser.DeclaredEntries, null);
        entry.DeclaredEntries = declared is null ? null : HeaderParser.ParseCount(declared);

        if (repo?.Archived == true) Warn(record.Key, $"GitHub repository {repo.Url} is archived; the list may no longer be maintained");
        return entry;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"https://github.com/x/y/blob/main/LICENSE" is less useful than the SPDX id GitHub gives us.</summary>
    private static string? LicenseFromHeader(string? header, string? repoLicense)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        if (HeaderParser.LooksLikeUrl(header) && header.Contains("github.com", StringComparison.OrdinalIgnoreCase) && repoLicense is not null)
            return repoLicense;
        return header;
    }

    private void CheckSanity(DatabaseRecord record, AnalysisResult a, Dictionary<string, string> header)
    {
        var expected = ListAnalyzer.ExpectedFormatForFolder(record.FormatFolder);
        var detected = a.DetectedFormat;
        var compatible = detected == expected
                         || (expected == "hosts" && detected == "hosts-compressed")
                         || (expected == "hosts-compressed" && detected == "hosts")
                         || (expected == "adblock" && detected == "domains"); // adblock lists may contain plain domains
        if (!compatible)
            Warn(record.Key, $"lives in '{record.FormatFolder}' but content looks like '{detected}'");

        if (a.Entries == 0) Warn(record.Key, "list downloaded but no entries were counted");

        foreach (var field in record.Patterns.Keys)
            if (!header.ContainsKey(field) && !record.Overrides.ContainsKey(field))
                Warn(record.Key, $"custom pattern for '{field}' matched nothing; the list header may have changed");

        if (header.TryGetValue(HeaderParser.DeclaredEntries, out var d) && HeaderParser.ParseCount(d) is { } declared && declared > 0)
        {
            var diff = Math.Abs(declared - a.Entries) / (double)declared;
            if (diff > 0.05) log.Detail($"{record.Key}: header says {declared:N0} entries, counted {a.Entries:N0}");
        }
    }

    private static Dictionary<string, string> PreviousAsHeader(DirectoryEntry prev)
    {
        var h = new Dictionary<string, string>();
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v) && !HeaderParser.IsPlaceholder(v)) h[k] = v; }
        Add(HeaderParser.Title, prev.Title);
        Add(HeaderParser.Description, prev.Description);
        Add(HeaderParser.Homepage, prev.Homepage);
        Add(HeaderParser.Expiry, prev.Expiry);
        Add(HeaderParser.LastModified, prev.LastModified);
        Add(HeaderParser.Version, prev.Version);
        if (prev.DeclaredEntries is { } de) Add(HeaderParser.DeclaredEntries, de.ToString());
        return h;
    }

    private void Warn(string scope, string message)
    {
        _warnings.Add($"{scope}: {message}");
        log.Warn($"{scope}: {message}");
    }

    // ---------- previous Directory.json (keeps unreachable lists alive) ----------

    private sealed class PreviousDirectory
    {
        public Dictionary<string, DirectoryEntry> ByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DirectoryEntry> ByMirror { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DirectoryEntry? Find(string key, string? mirror) =>
            ByKey.GetValueOrDefault(key) ?? (mirror is null ? null : ByMirror.GetValueOrDefault(mirror));
    }

    private PreviousDirectory LoadPrevious(string file)
    {
        var prev = new PreviousDirectory();
        if (!File.Exists(file)) return prev;
        try
        {
            // Lenient: v1 files have "Unknown" strings and no key.
            var root = JsonNode.Parse(File.ReadAllText(file));
            foreach (var node in root?["entries"]?.AsArray() ?? [])
            {
                if (node is null) continue;
                DirectoryEntry? e;
                try { e = node.Deserialize<DirectoryEntry>(Json.Read); }
                catch (JsonException) { continue; }
                if (e is null) continue;
                if (!string.IsNullOrEmpty(e.Key)) prev.ByKey[e.Key] = e;
                if (!string.IsNullOrEmpty(e.MainMirror)) prev.ByMirror[e.MainMirror] = e;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Could not read previous {Path.GetFileName(file)}: {ex.Message}");
        }
        return prev;
    }

    private void WriteIfChanged(DirectoryFile directory)
    {
        var json = JsonSerializer.Serialize(directory, Json.Write) + "\n";
        if (File.Exists(options.OutputFile))
        {
            var old = File.ReadAllText(options.OutputFile);
            if (StripVolatile(old) == StripVolatile(json))
            {
                log.Info("No changes besides timestamps; Directory.json left untouched");
                return;
            }
        }
        File.WriteAllText(options.OutputFile, json, new UTF8Encoding(false));
        log.Info($"Wrote {options.OutputFile}");
    }

    /// <summary>Removes fields that change every run so "nothing really changed" doesn't produce a commit.</summary>
    private static string StripVolatile(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["hostlistdirectoryinfo"] is JsonObject info) info.Remove("lastUpdated");
            foreach (var e in root?["entries"]?.AsArray() ?? [])
                if (e is JsonObject o) { o.Remove("lastChecked"); o.Remove("lastSuccess"); }
            return root?.ToJsonString() ?? json;
        }
        catch (JsonException) { return json; }
    }

    private void WriteSummary(DirectoryFile directory, TimeSpan elapsed)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(path)) return;
        var sb = new StringBuilder();
        sb.AppendLine("## Hostlist Directory build");
        sb.AppendLine();
        sb.AppendLine($"**{directory.Info.ReachableLists}/{directory.Info.TotalLists}** lists reachable · built in {elapsed:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("| Format | Lists |");
        sb.AppendLine("|---|---|");
        foreach (var (f, n) in directory.Info.Formats) sb.AppendLine($"| {f} | {n} |");
        if (!_failures.IsEmpty)
        {
            sb.AppendLine().AppendLine("### Unreachable (kept with last known data)");
            foreach (var f in _failures.Order()) sb.AppendLine($"- {f}");
        }
        if (!_warnings.IsEmpty)
        {
            sb.AppendLine().AppendLine("### Warnings");
            foreach (var w in _warnings.Distinct().Order()) sb.AppendLine($"- {w}");
        }
        File.AppendAllText(path, sb.ToString());
    }
}
