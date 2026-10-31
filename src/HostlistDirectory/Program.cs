using System.Text.Json;
using HostlistDirectory;

// HostlistDirectory — builds Directory.json from the database folder.
// This is free and unencumbered software released into the public domain (see LICENSE).

var argList = args.ToList();
var command = argList.Count > 0 && !argList[0].StartsWith('-') ? argList[0].ToLowerInvariant() : "build";
if (argList.Count > 0 && !argList[0].StartsWith('-')) argList.RemoveAt(0);

string? Opt(string name)
{
    var i = argList.FindIndex(a => a.Equals($"--{name}", StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : null;
}
bool Flag(string name) => argList.Any(a => a.Equals($"--{name}", StringComparison.OrdinalIgnoreCase));

var root = Path.GetFullPath(Opt("root") ?? FindRepoRoot());
var log = new Log(Flag("verbose"));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (command)
{
    case "build":
        return await new DirectoryBuilder(new BuildOptions
        {
            RootDir = root,
            OutputFile = Path.GetFullPath(Opt("output") ?? Path.Combine(root, "Directory.json")),
            Parallelism = int.TryParse(Opt("parallel"), out var p) ? p : 6,
            GitHubLookup = !Flag("no-github"),
            Only = Opt("only"),
        }, log).RunAsync(cts.Token);

    case "validate":
        return await ValidateAsync(root, Flag("online"), log, cts.Token);

    case "add":
        return await AddAsync(root, argList.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)), log, cts.Token);

    case "inspect":
        return await InspectAsync(argList.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)), cts.Token);

    default:
        Console.WriteLine("""
            HostlistDirectory

              build    [--output Directory.json] [--parallel 6] [--only <key-filter>] [--no-github] [--verbose]
                       Download every list in database/ and write Directory.json.
              validate [--online]
                       Check database files (offline), optionally download each list and check its format.
              add <url> [--format <folder>] [--author <name>] [--file <name>] [--tags a,b] [--dry-run]
                       Download a list, read its header and create database/<format>/<author>/<file>.json.
              inspect <url>
                       Show what the parser sees in a list (header fields, format, entry count).

              Common: --root <repo folder>
            """);
        return command is "help" or "--help" or "-h" ? 0 : 2;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "database"))) return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static async Task<int> ValidateAsync(string root, bool online, Log log, CancellationToken ct)
{
    var problems = new List<string>();
    var records = Database.Load(Path.Combine(root, "database"), problems.Add);
    var (errors, warnings) = Database.Validate(records);
    errors.AddRange(problems);

    if (online)
    {
        using var dl = new Downloader(TimeSpan.FromMinutes(3), retries: 1);
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(records.Where(r => r.Entry.Enabled != false), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (r, token) =>
        {
            var (res, fails) = await dl.FetchAsync(r.Mirrors, () => new ListAnalyzer(r.Patterns), token);
            if (res is null) { bag.Add($"ERROR {r.RelativePath}: unreachable ({string.Join("; ", fails)})"); return; }
            foreach (var f in fails) bag.Add($"WARN  {r.RelativePath}: mirror failed {f}");
            var expected = ListAnalyzer.ExpectedFormatForFolder(r.FormatFolder);
            var got = res.Analysis.DetectedFormat;
            if (got != expected && !(expected.StartsWith("hosts") && got.StartsWith("hosts")) && !(expected == "adblock" && got == "domains"))
                bag.Add($"WARN  {r.RelativePath}: folder '{r.FormatFolder}' but content looks like '{got}'");
            log.Ok($"{r.RelativePath}: {res.Analysis.Entries:N0} entries ({got})");
        });
        foreach (var line in bag.Order())
            (line.StartsWith("ERROR") ? errors : warnings).Add(line[6..]);
    }

    foreach (var w in warnings) log.Warn(w);
    foreach (var e in errors) log.Fail(e);
    log.Info($"{records.Count} database files, {errors.Count} errors, {warnings.Count} warnings");
    return errors.Count == 0 ? 0 : 1;
}

async Task<int> InspectAsync(string? url, CancellationToken ct)
{
    if (url is null) { Console.WriteLine("usage: inspect <url>"); return 2; }
    using var dl = new Downloader(TimeSpan.FromMinutes(3), retries: 1);
    var (res, fails) = await dl.FetchAsync([url], () => new ListAnalyzer(null), ct);
    if (res is null) { Console.WriteLine($"Failed: {string.Join("; ", fails)}"); return 1; }
    Console.WriteLine($"format:   {res.Analysis.DetectedFormat}");
    Console.WriteLine($"entries:  {res.Analysis.Entries:N0} (+{res.Analysis.AllowRules:N0} allow rules)");
    Console.WriteLine($"size:     {res.SizeBytes:N0} bytes");
    Console.WriteLine($"http lm:  {res.HttpLastModified}");
    foreach (var (k, v) in res.Analysis.Metadata.OrderBy(k => k.Key)) Console.WriteLine($"{k,-16}{v}");
    return 0;
}

async Task<int> AddAsync(string root, string? url, Log log, CancellationToken ct)
{
    if (url is null) { Console.WriteLine("usage: add <url> [--format <folder>] [--author <name>] [--file <name>] [--tags a,b] [--dry-run]"); return 2; }
    using var dl = new Downloader(TimeSpan.FromMinutes(3), retries: 1);
    var (res, fails) = await dl.FetchAsync([url], () => new ListAnalyzer(null), ct);
    if (res is null) { log.Fail($"Could not download: {string.Join("; ", fails)}"); return 1; }

    var meta = res.Analysis.Metadata;
    var repo = await new GitHubEnricher(dl, true).LookupAsync(meta.GetValueOrDefault(HeaderParser.Homepage), url, ct);
    var ghRepo = GitHubEnricher.ParseRepo(url);
    var title = meta.GetValueOrDefault(HeaderParser.Title);
    var author = Opt("author") ?? ghRepo?.Owner ?? new Uri(url).Host;
    var folder = Opt("format") ?? Database.FolderForFormat(res.Analysis.DetectedFormat);
    var file = Opt("file") ?? Normalizer.Slug(title ?? Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath));

    var entry = new DatabaseEntry
    {
        Name = title ?? file,
        Description = meta.GetValueOrDefault(HeaderParser.Description) ?? repo?.Description,
        Homepage = meta.GetValueOrDefault(HeaderParser.Homepage) ?? repo?.Url,
        License = repo?.License,
        Tags = Normalizer.NormaliseTags((Opt("tags") ?? "").Split(',')),
        MainMirror = url,
        AlternativeMirrors = [],
    };

    var target = Path.Combine(root, "database", folder, author, file + ".json");
    var json = JsonSerializer.Serialize(entry, Json.WriteDatabase);
    Console.WriteLine($"Detected: {res.Analysis.DetectedFormat}, {res.Analysis.Entries:N0} entries");
    Console.WriteLine($"Header:   {string.Join(", ", meta.Keys)}");
    Console.WriteLine(json);
    if (Flag("dry-run")) return 0;
    if (File.Exists(target)) { log.Fail($"{target} already exists"); return 1; }
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    await File.WriteAllTextAsync(target, json + "\n", ct);
    log.Ok($"Created {Path.GetRelativePath(root, target)} — add tags and alternative mirrors, then run 'validate'.");
    return 0;
}
