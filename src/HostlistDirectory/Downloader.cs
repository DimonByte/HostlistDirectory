using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HostlistDirectory;

public sealed class FetchResult
{
    public required string Url { get; init; }
    public required AnalysisResult Analysis { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset? HttpLastModified { get; init; }
    public bool FromAlternative { get; init; }
}

public sealed class FetchFailure(string url, string reason)
{
    public string Url { get; } = url;
    public string Reason { get; } = reason;
    public override string ToString() => $"{Url}: {Reason}";
}

public sealed class Downloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly int _retries;

    public Downloader(TimeSpan timeout, int retries = 2)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
        };
        _http = new HttpClient(handler) { Timeout = timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HostlistDirectory/2.0 (+https://github.com/DimonByte/HostlistDirectory)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/plain, */*;q=0.8");
        _retries = retries;
    }

    /// <summary>Tries the main mirror, then each alternative, with retries. Streams and analyses the content in one pass.</summary>
    public async Task<(FetchResult? Result, List<FetchFailure> Failures)> FetchAsync(
        IReadOnlyList<string> urls, Func<ListAnalyzer> analyzerFactory, CancellationToken ct)
    {
        var failures = new List<FetchFailure>();
        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            for (var attempt = 0; attempt <= _retries; attempt++)
            {
                try
                {
                    var result = await FetchOneAsync(url, analyzerFactory(), i > 0, ct);
                    return (result, failures);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    var permanent = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
                                    or InvalidListContentException;
                    if (permanent || attempt == _retries)
                    {
                        failures.Add(new FetchFailure(url, Describe(ex)));
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)), ct);
                }
            }
        }
        return (null, failures);
    }

    private async Task<FetchResult> FetchOneAsync(string url, ListAnalyzer analyzer, bool alternative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Content-Type isn't trusted (plenty of lists are served as text/html); the analyser rejects real HTML pages itself.
        await using var raw = await response.Content.ReadAsStreamAsync(ct);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var counting = new HashingStream(raw, sha);
        using var reader = new StreamReader(counting, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            analyzer.AcceptLine(line);

        return new FetchResult
        {
            Url = url,
            Analysis = analyzer.Complete(),
            SizeBytes = counting.BytesRead,
            Sha256 = Convert.ToHexStringLower(sha.GetHashAndReset()),
            HttpLastModified = response.Content.Headers.LastModified,
            FromAlternative = alternative,
        };
    }

    public async Task<string?> GetStringAsync(string url, IEnumerable<(string, string)> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code} {code}",
        TaskCanceledException => "timed out",
        InvalidListContentException ic => ic.Message,
        _ => ex.GetBaseException().Message,
    };

    public void Dispose() => _http.Dispose();

    /// <summary>Read-only pass-through stream that hashes and counts bytes as they're read.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Track(buffer.AsSpan(offset, n));
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Track(buffer.Span[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private void Track(ReadOnlySpan<byte> data)
        {
            BytesRead += data.Length;
            hash.AppendData(data);
        }
    }
}
