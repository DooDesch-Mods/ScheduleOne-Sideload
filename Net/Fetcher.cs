using System.Net;
using System.Text;

namespace Sideload.Net
{
    /// <summary>One request as the script asked for it, already validated and reduced to plain values.</summary>
    internal sealed class FetchCall
    {
        internal string AppId = "";
        internal Uri Url;
        internal string Method = "GET";
        internal List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        internal string Body;

        /// <summary>Whole-call budget, redirects included.</summary>
        internal int TimeoutMs = Fetcher.DefaultTimeoutMs;

        /// <summary>Ceiling on the response body. Per call rather than a constant so a test can prove the cap
        /// without moving four megabytes.</summary>
        internal int MaxBytes = Fetcher.DefaultMaxBytes;
    }

    /// <summary>What came back. Either <see cref="Error"/> is set or the response fields are.</summary>
    internal sealed class FetchOutcome
    {
        internal string Error;
        internal int Status;
        internal string StatusText = "";
        internal string Url = "";
        internal bool Redirected;

        /// <summary>Response headers as "name: value" lines. A flat string because that is the only shape that
        /// crosses into the script engine without an interop question.</summary>
        internal string HeaderLines = "";

        internal string Body = "";

        internal bool Failed => Error != null;
    }

    /// <summary>
    /// The HTTP side of <c>fetch</c>. Runs entirely off the Unity main thread and hands the result back through a
    /// callback; nothing here touches the script engine, so a slow endpoint costs no frame time at all.
    ///
    /// Redirects are followed by hand rather than by <see cref="HttpClientHandler"/>, because every hop has to be put
    /// back through <see cref="HostAllowlist"/>. An automatic redirect would let the allowed server choose the next
    /// destination, which turns one allowed host into an open proxy.
    /// </summary>
    internal static class Fetcher
    {
        /// <summary>Long enough for a slow API, short enough that a dead endpoint cannot leave a promise pending for
        /// the rest of the session.</summary>
        internal const int DefaultTimeoutMs = 10_000;

        /// <summary>Response ceiling. A phone app renders text; anything past this is a mistake, and buffering it
        /// would be paid for in the game's memory.</summary>
        internal const int DefaultMaxBytes = 4 * 1024 * 1024;

        /// <summary>Browsers stop at 20; a UI that needs more than a handful is misconfigured, and each hop costs
        /// another round trip inside the same timeout.</summary>
        internal const int MaxRedirects = 5;

        private static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(CreateClient);

        /// <summary>
        /// Start the request. Returns immediately; <paramref name="done"/> runs on a thread-pool thread and is called
        /// exactly once. It never throws - every failure arrives as <see cref="FetchOutcome.Error"/>.
        /// </summary>
        internal static void Send(FetchCall call, Action<FetchOutcome> done)
        {
            if (done == null) return;
            if (call?.Url == null) { done(Fail("fetch was given no URL.")); return; }

            _ = Task.Run(async () =>
            {
                FetchOutcome outcome;
                try { outcome = await Run(call).ConfigureAwait(false); }
                catch (Exception e) { outcome = Fail($"fetch to '{call.Url}' failed: {Describe(e)}"); }

                try { done(outcome); }
                catch { /* the caller's queue is the only consumer; a fault there must not take the pool thread down */ }
            });
        }

        private static async Task<FetchOutcome> Run(FetchCall call)
        {
            using var budget = new CancellationTokenSource();
            budget.CancelAfter(Math.Clamp(call.TimeoutMs, 100, 120_000));

            Uri target = call.Url;
            string method = string.IsNullOrWhiteSpace(call.Method) ? "GET" : call.Method.Trim().ToUpperInvariant();
            string body = call.Body;
            bool redirected = false;

            for (int hop = 0; ; hop++)
            {
                // Every hop, not just the first: a 302 must not be able to name a host the mod never allowed.
                if (!HostAllowlist.Allows(call.AppId, target, out string reason))
                {
                    HostAllowlist.ReportOnce(call.AppId, target, reason);
                    return Fail(redirected ? $"a redirect led somewhere blocked - {reason}" : reason);
                }

                HttpResponseMessage response;
                try
                {
                    using HttpRequestMessage request = Build(method, target, body, call.Headers);
                    response = await _client.Value
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested)
                {
                    return Fail($"fetch to '{call.Url}' timed out after {call.TimeoutMs} ms.");
                }
                catch (Exception e)
                {
                    return Fail($"fetch to '{target}' failed: {Describe(e)}");
                }

                using (response)
                {
                    Uri next = RedirectTarget(response, target);
                    if (next != null && hop < MaxRedirects)
                    {
                        // 303, and 301/302 on a non-idempotent method, continue as a plain GET - the same rule
                        // browsers follow, and the reason a redirected POST must not silently repeat its body.
                        int code = (int)response.StatusCode;
                        if (code == 303 || (code is 301 or 302 && method != "GET" && method != "HEAD"))
                        {
                            method = "GET";
                            body = null;
                        }

                        target = next;
                        redirected = true;
                        continue;
                    }

                    if (next != null)
                        return Fail($"fetch to '{call.Url}' failed: more than {MaxRedirects} redirects.");

                    return await ReadBody(response, call, target, redirected, budget.Token).ConfigureAwait(false);
                }
            }
        }

        private static async Task<FetchOutcome> ReadBody(HttpResponseMessage response, FetchCall call, Uri target,
                                                         bool redirected, CancellationToken token)
        {
            int cap = Math.Max(call.MaxBytes, 1024);
            var buffer = new MemoryStream();

            try
            {
                using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var chunk = new byte[64 * 1024];

                while (true)
                {
                    int read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                    if (read <= 0) break;

                    if (buffer.Length + read > cap)
                        return Fail($"the response from '{target.IdnHost}' is larger than the {Megabytes(cap)} cap " +
                                    "that Sideload puts on a fetch body.");

                    buffer.Write(chunk, 0, read);
                }
            }
            catch (OperationCanceledException)
            {
                return Fail($"fetch to '{call.Url}' timed out after {call.TimeoutMs} ms.");
            }
            catch (Exception e)
            {
                return Fail($"reading the response from '{target.IdnHost}' failed: {Describe(e)}");
            }

            return new FetchOutcome
            {
                Status = (int)response.StatusCode,
                StatusText = response.ReasonPhrase ?? response.StatusCode.ToString(),
                Url = target.ToString(),
                Redirected = redirected,
                HeaderLines = HeaderLines(response),
                Body = Decode(buffer.ToArray(), response),
            };
        }

        private static HttpRequestMessage Build(string method, Uri target, string body,
                                                List<KeyValuePair<string, string>> headers)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), target);

            if (body != null && method != "GET" && method != "HEAD")
                request.Content = new StringContent(body, Encoding.UTF8);

            for (int i = 0; headers != null && i < headers.Count; i++)
            {
                KeyValuePair<string, string> header = headers[i];
                if (string.IsNullOrWhiteSpace(header.Key)) continue;

                // Content-Type and friends live on the content, everything else on the request. Asking the request
                // first and falling back is cheaper than keeping a list of which is which in sync with the BCL.
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
                request.Content?.Headers.Remove(header.Key);
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return request;
        }

        /// <summary>Where this response wants us to go next, or null if it is the answer. Relative Locations resolve
        /// against the URL that produced them, as the HTTP spec requires.</summary>
        private static Uri RedirectTarget(HttpResponseMessage response, Uri from)
        {
            int code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308)) return null;

            Uri location = response.Headers.Location;
            if (location == null) return null;

            return location.IsAbsoluteUri ? location : new Uri(from, location);
        }

        private static string HeaderLines(HttpResponseMessage response)
        {
            var sb = new StringBuilder();

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
                sb.Append(header.Key).Append(':').Append(string.Join(", ", header.Value)).Append('\n');

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
                sb.Append(header.Key).Append(':').Append(string.Join(", ", header.Value)).Append('\n');

            return sb.ToString();
        }

        /// <summary>Bytes to text using the charset the server named, UTF-8 otherwise. An unknown charset is not worth
        /// failing a request over - the body is far more likely to be readable than not.</summary>
        private static string Decode(byte[] bytes, HttpResponseMessage response)
        {
            string charset = response.Content.Headers.ContentType?.CharSet?.Trim().Trim('"');

            if (!string.IsNullOrEmpty(charset))
            {
                try { return Encoding.GetEncoding(charset).GetString(bytes); }
                catch { /* fall through to UTF-8 */ }
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                // Followed by hand so every hop can be re-checked against the allowlist.
                AllowAutoRedirect = false,

                // A page has no session and no origin, so cookies would only be a way for one app's traffic to be
                // recognised across another's.
                UseCookies = false,

                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };

            return new HttpClient(handler)
            {
                // The per-call CancellationTokenSource owns the deadline; a client-level timeout would cut the whole
                // redirect chain at a different, invisible limit.
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        private static FetchOutcome Fail(string message) => new FetchOutcome { Error = message };

        private static string Megabytes(int bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#",
            System.Globalization.CultureInfo.InvariantCulture) + " MB";

        /// <summary>The innermost message, which is the one that names the actual problem - the outer
        /// HttpRequestException only ever says "An error occurred while sending the request".</summary>
        private static string Describe(Exception e)
        {
            Exception inner = e;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.Message;
        }
    }
}
