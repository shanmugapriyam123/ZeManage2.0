using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Crash
{
    /// <summary>
    /// Talks to the journal-parser web app (Flask) using its SAS-based chunked upload flow:
    ///   1. POST /upload/sas      -> { sas_url, blob_name, job_id }
    ///   2. PUT to sas_url        -> upload directly to Azure Blob via Azure.Storage.Blobs SDK
    ///   3. POST /upload/process  -> kick off async parsing { job_id }
    ///   4. GET  /upload/status/{job_id} (poll)  -> { status, result } / { status, error }
    ///
    /// The SDK handles block staging, retries, and progress reporting for arbitrarily large files,
    /// so we no longer hit Azure App Service's 230s gateway timeout (the cause of HTTP 504 on the
    /// legacy /upload endpoint).
    /// </summary>
    internal sealed class JournalAnalyzerClient
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxAnalysisWait = TimeSpan.FromMinutes(10);

        // Single process-wide HttpClient. Per-call HttpClient instances cause TIME_WAIT socket
        // exhaustion under repeated use (the well-known dotnet/runtime guidance since 2018).
        // 30s timeout is sized for the small JSON endpoints — /upload/sas, /upload/process, and
        // /upload/status/{id}.
        private static readonly HttpClient SharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),  // unchanged — for JSON endpoints
        };
        private static readonly HttpClient UploadHttp = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),  // for block PUTs on slow connections
        };

        private readonly string _baseUrl;
        private readonly ILogger? _logger;

        public JournalAnalyzerClient(string baseUrl, ILogger? logger)
        {
            _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
            _logger = logger;
            // Build-marker (helps confirm the right DLL is loaded after a redeploy — if the
            // SAS→/upload fallback isn't visible in the log on /upload/sas 503, the marker
            // below will be missing entirely, meaning Revit loaded an older DLL).
            _logger?.LogInfo($"[JournalAnalyzerClient v4: base={_baseUrl} SAS→/upload (sync inline analysis) fallback enabled]");
        }

        /// <summary>
        /// Backwards-compat: returns only the analysis JSON. Use the (string, string)
        /// overload below if you also need the jobId — required for server-side PDF
        /// generation via /generate-pdf, which keys off the stored result.
        /// </summary>
        public async Task<string> UploadAndAnalyzeAsync(
            string filePath,
            IProgress<long>? progress,
            CancellationToken ct)
        {
            var (json, _) = await UploadAndAnalyzeWithJobIdAsync(filePath, progress, ct).ConfigureAwait(false);
            return json;
        }

        /// <summary>
        /// Same upload + analyze flow but exposes the server-issued jobId. The
        /// caller needs jobId to ask the analyzer to render the PDF server-side
        /// (the staging endpoint already does this correctly when the user uploads
        /// via the web UI — same code path now used from Revit).
        /// </summary>
        public async Task<(string AnalysisJson, string JobId)> UploadAndAnalyzeWithJobIdAsync(
            string filePath,
            IProgress<long>? progress,
            CancellationToken ct)
        {
            var uploadPath = PrepareJournalForUpload(filePath);
            try
            {
                var filename = Path.GetFileName(filePath);
                var fileSize = new FileInfo(uploadPath).Length;

                // The website's drag-drop runs in two modes — verified against
                // /static/script.js (Browse files → Analyze: it first GETs /upload/mode,
                // then takes the SAS path when blob storage is provisioned, otherwise the
                // direct /upload path). We mirror that: try SAS first (large-file friendly,
                // no gateway timeout), and on ANY SAS-path failure (the server has been
                // returning HTTP 503 from /upload/sas intermittently, the symptom the user
                // hit) silently fall back to direct multipart POST /upload — the same
                // endpoint the website uses when SAS is unavailable. The two paths converge
                // on the same /upload/status/{jobId} poller so downstream code is identical.
                string jobId;
                try
                {
                    _logger?.LogInfo($"Requesting SAS for journal upload: {filename} ({fileSize} bytes)");
                    var sas = await PostJsonAsync<SasResponse>(
                        "/upload/sas", new { filename, file_size = fileSize }, ct).ConfigureAwait(false);
                    if (sas == null || string.IsNullOrEmpty(sas.SasUrl) || string.IsNullOrEmpty(sas.JobId))
                        throw new HttpRequestException("Analyzer returned an invalid SAS response.");

                    _logger?.LogInfo($"SAS issued: jobId={sas.JobId} blob={sas.BlobName}");

                    await UploadToSasAsync(sas.SasUrl, uploadPath, progress, ct).ConfigureAwait(false);
                    _logger?.LogInfo($"Blob upload complete: {sas.BlobName}");

                    await PostJsonAsync<object>(
                        "/upload/process", new { job_id = sas.JobId }, ct).ConfigureAwait(false);
                    _logger?.LogInfo($"Processing started (SAS path): jobId={sas.JobId}");
                    jobId = sas.JobId;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception sasEx)
                {
                    _logger?.LogWarning($"SAS upload path failed ({sasEx.GetType().Name}: {sasEx.Message}). Falling back to direct POST /upload.");
                    // The direct /upload endpoint is SYNCHRONOUS — POST the file, server
                    // runs analysis inline, returns the full analysis JSON in the response
                    // body (verified 2026-06-16: body shape has "addins":{"autodesk":[...]}
                    // top-level fields, not { job_id }). No polling needed; return what we
                    // got. JobId is synthesised (the website uses jobId only for
                    // /generate-pdf, and we POST the analysis JSON to that endpoint anyway).
                    var directJson = await UploadDirectAnalysisAsync(uploadPath, filename, progress, ct).ConfigureAwait(false);
                    _logger?.LogInfo($"Direct /upload returned analysis inline ({directJson.Length} chars). Skipping poll.");
                    return (directJson, "direct-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                }

                var json = await WaitForAnalysisAsync(jobId, ct).ConfigureAwait(false);
                return (json, jobId);
            }
            finally
            {
                if (uploadPath != filePath && File.Exists(uploadPath))
                {
                    try { File.Delete(uploadPath); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }

        /// <summary>
        /// Asks the analyzer to render a PDF for an analysis result and writes the bytes
        /// to <paramref name="outputPath"/>. Posts the FULL analysis JSON to /generate-pdf
        /// — same path the web UI uses for its ""Download PDF Report"" button (verified
        /// against /static/script.js downloadPdf(): the handler does
        /// `fetch('/generate-pdf', { method: 'POST', body: JSON.stringify(analysisData) })`).
        /// The endpoint reads its fields from the request body, NOT from any server-side
        /// store — passing just `{ job_id }` (as an earlier version of this method did)
        /// returns the empty default PDF, which is the bug the user kept seeing.
        /// </summary>
        /// <param name="analysisJson">The analysis JSON returned by UploadAndAnalyzeAsync.
        /// May be wrapped (`{"result": {...}}`) or flat — the analyzer handles both, but
        /// the web UI sends the unwrapped form, so we unwrap here too to match.</param>
        /// <returns>true if a populated PDF was written, false on any failure / empty default response.</returns>
        public async Task<bool> DownloadPdfForAnalysisAsync(
            string analysisJson, string outputPath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(analysisJson)) return false;

            // Unwrap the result envelope if present — the website's analysisData is the
            // inner object, not the `{ result: {...} }` wrapper that /upload/result/{id}
            // returns. Sending the wrapped form produces an empty default PDF because the
            // PDF template reads top-level fields (summary.session_status, session_info,
            // errors, …) that don't exist on the wrapper.
            string bodyJson = analysisJson;
            try
            {
                using var doc = JsonDocument.Parse(analysisJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("result", out var inner)
                    && inner.ValueKind == JsonValueKind.Object)
                {
                    bodyJson = inner.GetRawText();
                }
            }
            catch
            {
                // Not JSON or malformed → send as-is; server will 400 and we fall back.
            }

            // Try BOTH common request shapes the website's fetch() might have used.
            // We don't have the website's bundled script.js source, and a previous change
            // proved the server discriminates against payloads it doesn't expect (returns
            // the empty default template). Brute-force two attempts:
            //   1) application/json — what most modern web UIs send
            //   2) text/plain;charset=UTF-8 — what fetch() DEFAULTS to when given a string
            //      body without explicit Content-Type
            // Whichever returns the larger PDF wins.
            var url = _baseUrl + "/generate-pdf";
            byte[]? bestPdfBytes = null;
            int bestStatus = 0;
            string bestContentType = "";
            foreach (var mediaType in new[] { "application/json", "text/plain" })
            {
                try
                {
                    using var content = new StringContent(bodyJson, Encoding.UTF8, mediaType);
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    // Match a browser's fetch() context: some servers gate output on Origin /
                    // Referer (cross-origin protection). Pretend to be the analyzer page itself.
                    req.Headers.TryAddWithoutValidation("Accept", "application/pdf, */*");
                    req.Headers.TryAddWithoutValidation("Origin", _baseUrl);
                    req.Headers.TryAddWithoutValidation("Referer", _baseUrl + "/");
                    req.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) BIManageRevit/1.0 Safari/537.36");

                    using var resp = await UploadHttp.SendAsync(req, ct).ConfigureAwait(false);
                    var status = (int)resp.StatusCode;
                    var ctype = resp.Content.Headers.ContentType?.ToString() ?? "";
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger?.LogWarning($"Analyzer /generate-pdf [{mediaType}] HTTP {status} ({ctype}).");
                        continue;
                    }

                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    _logger?.LogInfo($"Analyzer /generate-pdf [{mediaType}] HTTP {status} ({ctype}), {bytes.Length} bytes.");
                    if (bestPdfBytes == null || bytes.Length > bestPdfBytes.Length)
                    {
                        bestPdfBytes = bytes;
                        bestStatus = status;
                        bestContentType = ctype;
                    }
                }
                catch (TaskCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger?.LogDebug("Analyzer /generate-pdf cancelled by caller.");
                    return false;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Analyzer /generate-pdf [{mediaType}] threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (bestPdfBytes == null || bestPdfBytes.Length == 0)
            {
                _logger?.LogWarning("Analyzer /generate-pdf: no successful response from any attempt.");
                return false;
            }

            // Reject very small responses that are clearly the empty default template
            // (the populated Comprehensive Analysis Report is usually 60-400 KB; an empty
            // default with only the cover page is typically 3-15 KB). Lowered the floor
            // from 50 KB to 20 KB after seeing the website's Export Analysis Report PDF
            // come back smaller-than-expected for short journals.
            if (bestPdfBytes.Length < 20_000)
            {
                _logger?.LogWarning($"Analyzer /generate-pdf: best response was only {bestPdfBytes.Length} bytes (HTTP {bestStatus}, {bestContentType}) — treating as empty default and falling back.");
                return false;
            }

            try
            {
                File.WriteAllBytes(outputPath, bestPdfBytes);
                _logger?.LogInfo($"Analyzer PDF written from /generate-pdf: {bestPdfBytes.Length} bytes (HTTP {bestStatus}, {bestContentType}) → {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Analyzer /generate-pdf: failed to write {bestPdfBytes.Length} bytes to {outputPath}: {ex.Message}");
                return false;
            }
        }

        private async Task<string> WaitForAnalysisAsync(string jobId, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + MaxAnalysisWait;
            var backoff = TimeSpan.FromSeconds(2);
            var maxBackoff = TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                HttpResponseMessage resp;
                try
                {
                    resp = await SharedHttp.GetAsync($"{_baseUrl}/upload/status/{jobId}", ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    _logger?.LogWarning($"Network error: {ex.Message}; retrying.");
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
                    continue;
                }
                using (resp)
                {
                    if ((int)resp.StatusCode is 502 or 503 or 504 or 408 or 429)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta ?? backoff;
                        _logger?.LogWarning($"Transient {(int)resp.StatusCode}; retrying in {wait.TotalSeconds}s.");
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
                        continue;
                    }
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Status poll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
                            $"Body: {body.Substring(0, Math.Min(200, body.Length))}");
                    using var doc = JsonDocument.Parse(body);
                    var status = doc.RootElement.GetProperty("status").GetString();
                    if (status == "completed")
                    {
                        // Fetch full result ONCE from the new cheap endpoint
                        using var resultResp = await SharedHttp.GetAsync(
                            $"{_baseUrl}/upload/result/{jobId}", ct).ConfigureAwait(false);
                        resultResp.EnsureSuccessStatusCode();
                        return await resultResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                    if (status == "failed")
                    {
                        var error = doc.RootElement.TryGetProperty("error", out var e)
                            ? e.GetString() : "Unknown error";
                        if (error?.Contains("Server restarted") == true)
                            _logger?.LogWarning($"Job {jobId} lost to server restart; user should retry.");
                        throw new InvalidOperationException($"Analysis failed: {error}");
                    }
                    // status == "processing" or "pending" — reset backoff, sleep
                    backoff = TimeSpan.FromSeconds(2);
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("Analysis timed out after 10 minutes.");
        }

        /// <summary>
        /// Direct multipart POST /upload — fallback when /upload/sas returns 5xx. Verified
        /// against zediag.zestinetech.com on 2026-06-16: this endpoint is SYNCHRONOUS — it
        /// runs the analysis inline and returns the full analysis JSON in the response body
        /// (top-level fields like "addins", "errors", "summary", "session_info"). No job_id,
        /// no polling. We return the body as-is and let the caller treat it as the analysis
        /// JSON, same shape as what /upload/result/{jobId} returns for the SAS path.
        /// </summary>
        private async Task<string> UploadDirectAnalysisAsync(
            string filePath, string filename, IProgress<long>? progress, CancellationToken ct)
        {
            using var content = new MultipartFormDataContent();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 81920, useAsync: true);

            // ProgressStreamContent reports bytes as the multipart body is streamed to
            // the server so the UI's "Uploading X%..." indicator keeps moving on the
            // fallback path. Wrapping StreamContent + a progress callback is enough —
            // no need for the SAS path's block-staging machinery here.
            var fileContent = new ProgressStreamContent(fs, progress, ct);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", filename);

            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/upload") { Content = content };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Origin", _baseUrl);
            req.Headers.TryAddWithoutValidation("Referer", _baseUrl + "/");

            using var resp = await UploadHttp.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Direct POST /upload failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(body, 200)}");
            }

            // Sanity-check the body is a JSON object before handing it to the caller —
            // anything else (HTML error page, empty body) means the endpoint changed shape.
            if (string.IsNullOrWhiteSpace(body))
                throw new HttpRequestException("Direct POST /upload returned an empty body.");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new HttpRequestException($"Direct POST /upload returned non-object JSON. Body: {Truncate(body, 200)}");
            }
            catch (JsonException jex)
            {
                throw new HttpRequestException($"Direct POST /upload returned invalid JSON: {jex.Message}. Body: {Truncate(body, 200)}");
            }

            return body;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        /// <summary>
        /// Stream content that forwards write-progress to an IProgress&lt;long&gt; sink.
        /// Used by the direct /upload fallback so the UI's "Uploading X%..." display
        /// matches the SAS path's behaviour.
        /// </summary>
        private sealed class ProgressStreamContent : StreamContent
        {
            private readonly Stream _stream;
            private readonly IProgress<long>? _progress;
            private readonly CancellationToken _ct;
            public ProgressStreamContent(Stream stream, IProgress<long>? progress, CancellationToken ct) : base(stream)
            {
                _stream = stream; _progress = progress; _ct = ct;
            }
            protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context)
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length, _ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, _ct).ConfigureAwait(false);
                    total += read;
                    _progress?.Report(total);
                }
            }
            protected override bool TryComputeLength(out long length)
            {
                if (_stream.CanSeek) { length = _stream.Length; return true; }
                length = -1; return false;
            }
        }

        private async Task UploadToSasAsync(string sasUrl, string filePath,
            IProgress<long>? progress, CancellationToken ct)
        {
            const int chunkSize = 4 * 1024 * 1024;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 81920, useAsync: true);
            var blockIds = new List<string>();
            var buffer = new byte[chunkSize];
            long uploaded = 0;
            int blockNum = 0;
            int read;
            while ((read = await fs.ReadAsync(buffer, 0, chunkSize, ct)) > 0)
            {
                var blockId = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"block-{blockNum:D6}"));
                blockIds.Add(blockId);
                var url = $"{sasUrl}&comp=block&blockid={Uri.EscapeDataString(blockId)}";
                using var content = new ByteArrayContent(buffer, 0, read);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var resp = await UploadHttp.PutAsync(url, content, ct);
                resp.EnsureSuccessStatusCode();
                uploaded += read;
                progress?.Report(uploaded);
                blockNum++;
            }
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" +
                      string.Join("", blockIds.Select(id => $"<Latest>{id}</Latest>")) +
                      "</BlockList>";
            var commitUrl = $"{sasUrl}&comp=blocklist";
            using var commitContent = new StringContent(xml, Encoding.UTF8, "application/xml");
            var commitResp = await UploadHttp.PutAsync(commitUrl, commitContent, ct);
            commitResp.EnsureSuccessStatusCode();
        }

        private string PrepareJournalForUpload(string sourcePath)
        {
            var info = new FileInfo(sourcePath);
            if (info.Length < 5 * 1024 * 1024) return sourcePath;
            const int HEAD = 2000, TAIL = 1000;
            var headLines = new List<string>(HEAD);
            var tailLines = new Queue<string>(TAIL);
            // FileShare.ReadWrite is critical: Active-session journals are still held open
            // by Revit (with FileShare.Write — Revit lets others write but NOT read). Using
            // the default StreamReader(path) ctor opens with FileShare.Read, which Windows
            // refuses → "The process cannot access the file because it is being used by
            // another process." This mirrors what UploadToSasAsync below already does.
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite, 81920, useAsync: false))
            using (var sr = new StreamReader(fs))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (headLines.Count < HEAD)
                        headLines.Add(line);
                    else
                    {
                        if (tailLines.Count == TAIL) tailLines.Dequeue();
                        tailLines.Enqueue(line);
                    }
                }
            }
            var temp = Path.Combine(Path.GetTempPath(),
                $"truncated_{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
            using (var sw = new StreamWriter(temp))
            {
                foreach (var l in headLines) sw.WriteLine(l);
                foreach (var l in tailLines) sw.WriteLine(l);
            }
            return temp;
        }

        private async Task<T?> PostJsonAsync<T>(string relativePath, object payload, CancellationToken ct)
            where T : class
        {
            var json = JsonSerializer.Serialize(payload);
            var url = $"{_baseUrl}{relativePath}";
            var backoff = TimeSpan.FromSeconds(2);
            var maxBackoff = TimeSpan.FromSeconds(30);
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                HttpResponseMessage resp;
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    resp = await SharedHttp.PostAsync(url, content, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt == maxAttempts) throw;
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
                    continue;
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
                    continue;
                }
                using (resp)
                {
                    if ((int)resp.StatusCode is 502 or 503 or 504 or 408 or 429)
                    {
                        if (attempt == maxAttempts)
                            throw new HttpRequestException(
                                $"POST {relativePath} failed after {maxAttempts} attempts: HTTP {(int)resp.StatusCode}");
                        var wait = resp.Headers.RetryAfter?.Delta ?? backoff;
                        _logger?.LogWarning(
                            $"Transient {(int)resp.StatusCode} on POST {relativePath}; attempt {attempt}/{maxAttempts}, retry in {wait.TotalSeconds}s.");
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
                        continue;
                    }
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var snippet = body.Length > 300 ? body.Substring(0, 300) : body;
                        throw new HttpRequestException(
                            $"POST {relativePath} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {snippet}");
                    }
                    if (typeof(T) == typeof(object) || string.IsNullOrEmpty(body))
                        return null;
                    return JsonSerializer.Deserialize<T>(body);
                }
            }
            throw new InvalidOperationException("Unreachable");
        }

        private sealed class SasResponse
        {
            [JsonPropertyName("sas_url")] public string SasUrl { get; set; } = string.Empty;
            [JsonPropertyName("blob_name")] public string BlobName { get; set; } = string.Empty;
            [JsonPropertyName("job_id")] public string JobId { get; set; } = string.Empty;
        }
    }
}
