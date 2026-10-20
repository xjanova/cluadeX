using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack.Sources;

namespace CluadeX.Services;

/// <summary>
/// Downloads that finish on a slow or flaky line (owner 2026-10-20: "มีเวอร์ชั่นใหม่ ก็เจอช้า โหลดช้าก็ตัด").
///
/// What this replaces had a cap on the TOTAL time — 30 s on the zip path, 30 min inside Velopack's own downloader —
/// and started from zero after any drop, so a 500 MB package on a slow line could not finish. Here:
/// <list type="bullet">
/// <item>no cap on the total time: an attempt fails only when no byte arrives for <see cref="StallTimeout"/>;</item>
/// <item>a dropped or stalled attempt goes on where it stopped (HTTP Range, guarded by If-Range on the ETag), also after
/// the app restarts — the partial file and its validators live in <see cref="CacheDir"/>;</item>
/// <item>only failures in a row with no progress count toward <see cref="MaxFailuresInARow"/>; a 4xx other than 408/429
/// is final at once.</item>
/// </list>
/// Implements Velopack's <see cref="IFileDownloader"/>, so the self-update downloads through it too.
/// </summary>
public sealed class ResumableDownloader : IFileDownloader, IDisposable
{
    /// <summary>One file's progress: bytes on disk so far, the full size when known (else 0), and whether this attempt
    /// went on from an earlier one.</summary>
    public readonly record struct FileProgress(string FileName, long Bytes, long Total, bool Resumed, int FailuresInARow, string? LastError);

    public string CacheDir { get; }
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxFailuresInARow { get; init; } = 8;
    /// <summary>The wait before retry n (1-based). Tests make it zero.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(n, 6)));
    /// <summary>Partial downloads older than this are deleted when another download starts.</summary>
    public TimeSpan KeepPartsFor { get; init; } = TimeSpan.FromDays(10);

    /// <summary>Raised from the downloading thread, at most every ~150 ms per file plus once at the end.</summary>
    public event Action<FileProgress>? Progress;

    private readonly HttpClient _http;
    // An attempt that moved the file on by this much was not a failure "in a row": a line that drops every few seconds
    // but keeps delivering still finishes, however many drops it takes.
    private const long ProgressResetsFailures = 16 * 1024;

    public ResumableDownloader(string cacheDir, HttpMessageHandler? handler = null, string? userAgent = null)
    {
        CacheDir = cacheDir;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,   // byte offsets must be offsets in the file
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // time is watched per read (stall), never in total
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? $"CluadeX/{AutoUpdateService.CurrentVersion}");
    }

    public void Dispose() => _http.Dispose();

    // ─── IFileDownloader ───────────────────────────────────────────────────────────────

    public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null,
        double timeout = 30, CancellationToken cancelToken = default)
    {
        Directory.CreateDirectory(CacheDir);
        PruneOldParts();
        string key = CacheKey(url);
        string part = Path.Combine(CacheDir, key + ".part");
        string metaPath = part + ".json";
        string name = FileNameOf(url);

        var meta = PartMeta.Load(metaPath);
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > 0 && (meta == null || meta.Url != url || (meta.Total > 0 && offset > meta.Total)))
        {
            ResetPart(part, metaPath);   // a partial we cannot vouch for starts over
            offset = 0; meta = null;
        }

        int failures = 0, lastPercent = -1;
        Exception? lastError = null;
        bool resumedOnce = offset > 0;
        while (true)
        {
            cancelToken.ThrowIfCancellationRequested();
            if (meta is { Total: > 0 } && offset == meta.Total) break;   // already whole (a restart right after the last byte)

            long before = offset;
            try
            {
                var outcome = await AttemptAsync(url, headers, part, metaPath, meta, offset, name, resumedOnce, progress, () => lastPercent, p => lastPercent = p, cancelToken).ConfigureAwait(false);
                meta = outcome.Meta;
                offset = outcome.Offset;
                if (outcome.Complete) break;
                lastError = new IOException(outcome.Why ?? "the connection ended early");
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }   // the partial stays for next time
            catch (FinalHttpException) { ResetPart(part, metaPath); throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or TimeoutException)
            {
                lastError = ex is OperationCanceledException ? new TimeoutException($"no data for {StallTimeout.TotalSeconds:0} s", ex) : ex;
                offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                meta = PartMeta.Load(metaPath);
            }

            resumedOnce |= offset > 0;
            failures = offset - before >= ProgressResetsFailures ? 1 : failures + 1;
            Progress?.Invoke(new FileProgress(name, offset, meta?.Total ?? 0, resumedOnce, failures, lastError?.Message));
            if (failures > MaxFailuresInARow)
                throw new IOException($"{name}: download failed {failures} times in a row — {lastError?.Message}", lastError);
            var wait = lastError is RetryAfterException ra ? ra.Wait : Backoff(failures);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancelToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);
        File.Move(part, targetFile, overwrite: true);
        try { File.Delete(metaPath); } catch { }
        if (lastPercent < 100) progress?.Invoke(100);
        Progress?.Invoke(new FileProgress(name, offset, Math.Max(offset, meta?.Total ?? 0), resumedOnce, 0, null));
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        // Small files (release lists, feeds): retried a few times, each try capped — these are never the slow part.
        Exception? last = null;
        for (int i = 1; i <= 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(timeout, 0.25, 2)));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaders(req, headers);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    var ex = new HttpRequestException($"HTTP {code} {resp.ReasonPhrase} — {url}", null, resp.StatusCode);
                    if (!IsRetryable(code)) throw ex;
                    last = ex;
                }
                else return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } sc && !IsRetryable((int)sc)) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { last = ex; }
            if (i < 3) await Task.Delay(Backoff(i)).ConfigureAwait(false);
        }
        throw last ?? new IOException("download failed: " + url);
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

    // ─── one attempt ───────────────────────────────────────────────────────────────────

    private readonly record struct Outcome(bool Complete, long Offset, PartMeta? Meta, string? Why);

    private async Task<Outcome> AttemptAsync(string url, IDictionary<string, string>? headers, string part, string metaPath, PartMeta? meta,
        long offset, string name, bool resumed, Action<int>? progress, Func<int> getPercent, Action<int> setPercent, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req, headers);
        if (offset > 0)
        {
            req.Headers.Range = new RangeHeaderValue(offset, null);
            // If-Range: the server sends the rest only if the file is still the one we started; otherwise the whole
            // new file (200). A weak ETag cannot be used here (RFC 9110 §13.1.5); the date can.
            if (meta?.ETag is { Length: > 0 } etag && !etag.StartsWith("W/", StringComparison.Ordinal))
                req.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(etag));
            else if (meta?.LastModified is { } lm)
                req.Headers.IfRange = new RangeConditionHeaderValue(lm);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        int code = (int)resp.StatusCode;
        long total;
        if (code == 206 && offset > 0)
        {
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.From != offset)
            {
                ResetPart(part, metaPath);
                return new Outcome(false, 0, null, "the server sent a different part of the file");
            }
            total = cr.Length ?? meta?.Total ?? 0;
        }
        else if (code == 416 && offset > 0)
        {
            // Nothing past our offset: either we already have it all, or the file on the server changed size.
            long? serverTotal = resp.Content.Headers.ContentRange?.Length;
            if (serverTotal == offset || (serverTotal == null && meta?.Total == offset)) return new Outcome(true, offset, meta, null);
            ResetPart(part, metaPath);
            return new Outcome(false, 0, null, "the file on the server changed");
        }
        else if (resp.IsSuccessStatusCode)
        {
            // 200: a fresh start — first try, or the server ignored the range / the file changed (If-Range failed).
            offset = 0;
            total = resp.Content.Headers.ContentLength ?? 0;
        }
        else
        {
            if (!IsRetryable(code)) throw new FinalHttpException($"HTTP {code} {resp.ReasonPhrase} — {name}", resp.StatusCode);
            var after = resp.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null) : null;
            if (after is { } w && w > TimeSpan.Zero) throw new RetryAfterException($"HTTP {code} — the server asked to wait", w > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : w);
            throw new HttpRequestException($"HTTP {code} {resp.ReasonPhrase} — {name}", null, resp.StatusCode);
        }

        if (offset == 0 || meta == null)
        {
            meta = new PartMeta
            {
                Url = url, Total = total,
                ETag = resp.Headers.ETag?.ToString(),
                LastModified = resp.Content.Headers.LastModified,
            };
            meta.Save(metaPath);
        }

        await using var body = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        fs.SetLength(offset);
        fs.Position = offset;
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 17);
        try
        {
            var lastReport = DateTime.MinValue;
            while (true)
            {
                int n = await body.ReadAsync(buf.AsMemory(), stall.Token).ConfigureAwait(false);
                if (n == 0) break;
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                offset += n;
                stall.CancelAfter(StallTimeout);   // data came: the stall clock starts again

                if (total > 0)
                {
                    int pct = (int)Math.Min(99, offset * 100 / total);   // 100 only once the file is in place
                    if (pct != getPercent()) { setPercent(pct); progress?.Invoke(pct); }
                }
                var now = DateTime.UtcNow;
                if (now - lastReport >= TimeSpan.FromMilliseconds(150))
                {
                    lastReport = now;
                    Progress?.Invoke(new FileProgress(name, offset, total, resumed, 0, null));   // data is flowing: not failing now
                }
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }

        if (total > 0 && offset < total) return new Outcome(false, offset, meta, $"the connection ended at {offset:N0} of {total:N0} bytes");
        if (total > 0 && offset > total)
        {
            fs.Close();
            ResetPart(part, metaPath);
            return new Outcome(false, 0, null, "more bytes than the server announced");
        }
        if (meta.Total <= 0) { meta.Total = offset; meta.Save(metaPath); }
        return new Outcome(true, offset, meta, null);
    }

    // ─── helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>408, 425, 429 and 5xx are worth another try; any other 4xx (404, 403, 401…) will not change.</summary>
    public static bool IsRetryable(int status) => status is 408 or 425 or 429 or >= 500;

    private static void AddHeaders(HttpRequestMessage req, IDictionary<string, string>? headers)
    {
        if (headers == null) return;
        foreach (var (k, v) in headers)
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrEmpty(v))
            {
                if (k.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Headers.Accept.Clear();
                req.Headers.TryAddWithoutValidation(k, v);
            }
    }

    /// <summary>The file name a URL points at ("CluadeX-3.0.179-delta.nupkg"), for messages.</summary>
    public static string FileNameOf(string url)
    {
        try { var seg = new Uri(url).AbsolutePath.TrimEnd('/'); var last = Uri.UnescapeDataString(seg[(seg.LastIndexOf('/') + 1)..]); return last.Length > 0 ? last : "download"; }
        catch { return "download"; }
    }

    /// <summary>A stable, file-system-safe name for a URL's partial file: its readable file name plus a hash of the
    /// whole URL (the query too — a different signed link is a different download only if the path differs, and
    /// GitHub's release links carry no query).</summary>
    public static string CacheKey(string url)
    {
        var safe = new StringBuilder();
        foreach (char c in FileNameOf(url)) safe.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        string readable = safe.Length > 80 ? safe.ToString(0, 80) : safe.ToString();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant();
        return readable + "-" + hash;
    }

    private static void ResetPart(string part, string metaPath)
    {
        try { File.Delete(part); } catch { }
        try { File.Delete(metaPath); } catch { }
    }

    private void PruneOldParts()
    {
        try
        {
            var cutoff = DateTime.UtcNow - KeepPartsFor;
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.part*"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private sealed class FinalHttpException(string message, HttpStatusCode status) : HttpRequestException(message, null, status);
    private sealed class RetryAfterException(string message, TimeSpan wait) : IOException(message) { public TimeSpan Wait { get; } = wait; }

    private sealed class PartMeta
    {
        public string Url { get; set; } = "";
        public long Total { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastModified { get; set; }

        public static PartMeta? Load(string path)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(path)) : null; }
            catch { return null; }
        }

        public void Save(string path)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(this)); } catch { }
        }
    }
}
