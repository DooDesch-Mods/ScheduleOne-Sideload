using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MelonLoader.Utils;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// Where the DevTools INTERFACE comes from.
    ///
    /// Sideload implements the protocol, but it cannot ship the frontend: that is sixteen megabytes of Google's
    /// JavaScript and has no business inside a mod DLL. So the interface is either loaded from Google's servers when
    /// the inspector is opened - which needs internet - or served off this machine, which does not.
    ///
    /// This decides which, and can put the local copy in place by itself: one npm tarball, once, in the background.
    /// Everything here fails soft. A download that does not work leaves no cache behind and costs nothing except the
    /// hosted frontend the developer would have been using anyway; it can never keep the game or the server from
    /// starting.
    ///
    /// The cache lands in the same folder <c>Workspace/tools/install-devtools-frontend.ps1</c> writes, so the manual
    /// route and the automatic one are interchangeable and neither has to know about the other.
    /// </summary>
    internal static class FrontendCache
    {
        /// <summary>
        /// React Native's build of the DevTools frontend, and deliberately not Google's own
        /// <c>chrome-devtools-frontend</c> package: that one is the Chromium SOURCE tree - TypeScript under
        /// <c>front_end/</c>, GN files, no <c>inspector.html</c> anywhere in it - and would need a full GN/ninja build
        /// before anything could be served. This one ships the frontend already built, and is the same copy Metro
        /// serves for React Native's own debugger.
        ///
        /// The only coupling is the query parameter: it reads <c>?ws=</c> and dials that endpoint, which is exactly
        /// the URL shape <see cref="Targets.FrontendUrl"/> hands out.
        /// </summary>
        private const string Package = "@react-native/debugger-frontend";

        /// <summary>Pinned rather than "latest" so every machine gets a frontend that has actually been tried here,
        /// and so an existing cache is only ever rebuilt when this line changes.</summary>
        private const string Version = "0.86.1";

        /// <summary>
        /// npm's sha512 for exactly this tarball, as published alongside it. Checked before a single byte reaches the
        /// disk: this is JavaScript that will run in the developer's browser, so pinning has to mean the bytes and
        /// not merely the URL.
        /// </summary>
        private const string Sha512 = "DRl6ctuVNcY1CtiAKFstWMDlJhXQ4tM96XVib3tTOH2zIoJPgeiGQdXUUa4tsPgqR4RkXsd5adDYwcFkE5QvVg==";

        /// <summary>The registry's URL shape for a scoped package: the scope is in the path but NOT repeated in the
        /// file name, which is the easy thing to get wrong here.</summary>
        private static string TarballUrl => $"https://registry.npmjs.org/{Package}/-/debugger-frontend-{Version}.tgz";

        /// <summary>Ceiling on the download. The tarball is about 4.5 MB, so this is room to grow rather than a limit
        /// anything real should meet.</summary>
        private const int MaxDownloadBytes = 24 * 1024 * 1024;

        /// <summary>Ceiling on what the archive is allowed to unpack to, checked as it is written. A tar can claim any
        /// size it likes, and filling the developer's disk is not an acceptable outcome of a failed download.</summary>
        private const long MaxUnpackedBytes = 96L * 1024 * 1024;

        /// <summary>Whole-download budget. Generous because it runs in the background and nothing waits on it, but
        /// finite so a hung connection cannot keep a thread and a socket forever.</summary>
        private const int TimeoutMs = 180_000;

        /// <summary>The package holds about 800 files. This only exists so a malformed archive cannot loop.</summary>
        private const int MaxEntries = 20_000;

        /// <summary>How far to look for the folder that holds <c>inspector.html</c>. The tarball nests it four deep
        /// and a hand-installed copy is at the top, so this is slack, not a target.</summary>
        private const int MaxSearchDepth = 8;

        /// <summary>Bound on the search itself, because it runs at startup and the configured folder is whatever the
        /// developer typed.</summary>
        private const int MaxFoldersScanned = 512;

        private static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(CreateClient);

        /// <summary>The folder to serve, or null while the hosted frontend is in use. Written once at startup and
        /// again if a background download lands, so an inspector opened after that point gets the local copy without
        /// anyone having to restart the game.</summary>
        private static volatile string _root;

        private static int _fetching;

        /// <summary>The folder that directly contains <c>inspector.html</c>, or null when there is no local copy and
        /// the hosted frontend should be linked instead.</summary>
        internal static string Root => _root;

        /// <summary>Where a downloaded frontend lives. Shared with the install script on purpose.</summary>
        internal static string CacheDirectory =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "Sideload", "devtools-frontend");

        /// <summary>Which package version put the cache there, kept beside the folder rather than inside it so the
        /// served tree is byte for byte what the install script produces.</summary>
        private static string MarkerFile => CacheDirectory + ".version";

        // ------------------------------------------------------------------ resolution --

        /// <summary>
        /// Work out which frontend to use, in the order a developer would expect: their own choice, then a copy
        /// already on disk, then - if they have allowed it - a copy fetched in the background, then Google's.
        ///
        /// Returns immediately in every branch. The download does not block it and nothing waits on the result.
        /// </summary>
        internal static void Resolve()
        {
            try { Decide(); }
            catch (Exception e)
            {
                _root = null;
                Core.Log?.Warning("[Sideload/cdp] devtools frontend: working out where it lives failed (" +
                                  Describe(e) + ") - Google's hosted copy is used, which needs internet.");
            }
        }

        private static void Decide()
        {
            // (a) The developer named a folder. That always wins - it is the only setting that can point at a build
            // this code knows nothing about.
            string configured = Config.Preferences.DevToolsFrontend;
            if (!string.IsNullOrEmpty(configured))
            {
                string chosen = FindInspectorRoot(configured);
                if (chosen != null)
                {
                    _root = chosen;
                    Core.Log?.Msg("[Sideload/cdp] devtools frontend: your DevToolsFrontend folder, " + chosen +
                                  " - works offline.");
                    return;
                }

                Core.Log?.Warning("[Sideload/cdp] devtools frontend: DevToolsFrontend is set to '" + configured +
                                  "' but no inspector.html was found under it - ignoring that setting.");
            }

            // (b) A copy from a previous run, or from the install script. A copy with no marker was put there by hand
            // and is left alone; only one this code wrote is ever considered out of date.
            string cached = FindInspectorRoot(CacheDirectory);
            string installed = InstalledVersion();
            bool stale = cached != null && installed != null && installed != Version;

            if (cached != null)
            {
                _root = cached;
                Core.Log?.Msg("[Sideload/cdp] devtools frontend: the local copy at " + cached + " - works offline." +
                              (stale ? " Pinned version is now " + Version + ", refreshing in the background." : ""));

                if (!stale) return;
            }

            if (!Config.Preferences.DevToolsFetchFrontend)
            {
                // A stale copy still works, and the developer has said not to download. Keeping it beats the hosted
                // frontend, which needs internet they may not have.
                if (cached != null) return;

                Core.Log?.Msg("[Sideload/cdp] devtools frontend: Google's hosted copy, which needs internet. For " +
                              "offline use switch DevToolsFetchFrontend on, or run " +
                              "Workspace/tools/install-devtools-frontend.ps1.");
                return;
            }

            // (c) Nothing usable on disk and downloading is allowed.
            if (cached == null)
                Core.Log?.Msg("[Sideload/cdp] devtools frontend: none on disk - downloading " + Package + "@" +
                              Version + " (about 4.5 MB) once, in the background, to " + CacheDirectory +
                              ". Google's hosted copy is used until it lands.");

            Begin();
        }

        /// <summary>
        /// The folder under <paramref name="start"/> that directly contains <c>inspector.html</c>, or null.
        ///
        /// Searched rather than assumed: the package nests it under <c>package/dist/third-party/front_end</c> today,
        /// the install script strips that nesting away, and a developer pointing DevToolsFrontend at a checkout may
        /// mean either. Breadth first, so the shallowest match wins and the deep parts of a frontend tree are never
        /// walked.
        /// </summary>
        private static string FindInspectorRoot(string start)
        {
            if (string.IsNullOrEmpty(start)) return null;

            string from;
            try { from = Path.GetFullPath(start); }
            catch { return null; }

            if (!Directory.Exists(from)) return null;

            var pending = new Queue<KeyValuePair<string, int>>();
            pending.Enqueue(new KeyValuePair<string, int>(from, 0));

            for (int scanned = 0; pending.Count > 0 && scanned < MaxFoldersScanned; scanned++)
            {
                KeyValuePair<string, int> here = pending.Dequeue();

                if (File.Exists(Path.Combine(here.Key, "inspector.html"))) return here.Key;
                if (here.Value >= MaxSearchDepth) continue;

                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(here.Key))
                        pending.Enqueue(new KeyValuePair<string, int>(sub, here.Value + 1));
                }
                catch { /* unreadable folder - the rest of the tree is still worth looking at */ }
            }

            return null;
        }

        /// <summary>The version recorded when this code last populated the cache, or null when nothing did.</summary>
        private static string InstalledVersion()
        {
            try
            {
                if (!File.Exists(MarkerFile)) return null;

                string text = File.ReadAllText(MarkerFile).Trim();
                int at = text.LastIndexOf('@');
                return at > 0 && at < text.Length - 1 ? text.Substring(at + 1) : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ download --

        /// <summary>Start the one-time download. Never runs twice at once, and never on the calling thread.</summary>
        private static void Begin()
        {
            if (Interlocked.Exchange(ref _fetching, 1) != 0) return;

            _ = Task.Run(async () =>
            {
                try { await Fetch().ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Core.Log?.Warning("[Sideload/cdp] devtools frontend: the download gave up after " +
                                      TimeoutMs / 1000 + "s - Google's hosted copy is used instead.");
                }
                catch (Exception e)
                {
                    Core.Log?.Warning("[Sideload/cdp] devtools frontend: the download failed (" + Describe(e) +
                                      ") - Google's hosted copy is used instead.");
                }
                finally { Interlocked.Exchange(ref _fetching, 0); }
            });
        }

        private static async Task Fetch()
        {
            Stopwatch clock = Stopwatch.StartNew();
            using var budget = new CancellationTokenSource(TimeoutMs);

            byte[] tarball = await Download(budget.Token).ConfigureAwait(false);

            if (!Authentic(tarball))
            {
                Core.Log?.Warning("[Sideload/cdp] devtools frontend: the download did not match the checksum npm " +
                                  "publishes for " + Package + "@" + Version + " - discarding it and using Google's " +
                                  "hosted copy.");
                return;
            }

            string staging = CacheDirectory + ".download";
            Discard(staging);

            using (var archive = new MemoryStream(tarball, writable: false))
            using (var plain = new GZipStream(archive, CompressionMode.Decompress))
                Untar(plain, staging);

            string extracted = FindInspectorRoot(staging);
            if (extracted == null)
            {
                Discard(staging);
                Core.Log?.Warning("[Sideload/cdp] devtools frontend: " + Package + "@" + Version + " unpacked with " +
                                  "no inspector.html in it - has the package changed shape? Google's hosted copy is " +
                                  "used instead.");
                return;
            }

            string target = Publish(extracted, staging);
            clock.Stop();

            Core.Log?.Msg("[Sideload/cdp] devtools frontend: downloaded " + Package + "@" + Version + " (" +
                          Megabytes(tarball.Length) + " in " + Seconds(clock.ElapsedMilliseconds) + ") to " + target +
                          " - DevTools works offline from now on.");
        }

        private static async Task<byte[]> Download(CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TarballUrl);
            using HttpResponseMessage response = await _client.Value
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException("the registry answered " + (int)response.StatusCode + " " +
                                               response.ReasonPhrase);

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes)
                throw new InvalidDataException("the tarball is " + Megabytes(declared.Value) + ", past the " +
                                               Megabytes(MaxDownloadBytes) + " cap");

            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            var buffer = new MemoryStream(declared is > 0 and <= MaxDownloadBytes ? (int)declared.Value : 1 << 22);
            var chunk = new byte[81920];

            while (true)
            {
                int read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                if (read <= 0) break;

                if (buffer.Length + read > MaxDownloadBytes)
                    throw new InvalidDataException("the download went past the " + Megabytes(MaxDownloadBytes) +
                                                   " cap");

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>Whether these are the exact bytes npm published for the pinned version.</summary>
        private static bool Authentic(byte[] tarball)
        {
            using var sha = SHA512.Create();
            return string.Equals(Convert.ToBase64String(sha.ComputeHash(tarball)), Sha512, StringComparison.Ordinal);
        }

        /// <summary>
        /// Move the extracted frontend into the served location and record what it is.
        ///
        /// The marker is written last, so a run interrupted anywhere before this point leaves a cache that is simply
        /// rebuilt rather than one that claims to be complete.
        /// </summary>
        private static string Publish(string extracted, string staging)
        {
            string target = CacheDirectory;

            Discard(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Directory.Move(extracted, target);
            Discard(staging);

            File.WriteAllText(MarkerFile, Package + "@" + Version);
            _root = target;

            return target;
        }

        private static void Discard(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/cdp] devtools frontend: could not remove " + directory + " (" +
                                  e.Message + ")");
            }
        }

        // ------------------------------------------------------------------ tar --

        /// <summary>
        /// Unpack a USTAR archive into <paramref name="destination"/>, regular files and directories only.
        ///
        /// Hand-rolled because System.Formats.Tar arrived in .NET 7 and MelonLoader hosts .NET 6, and the format is
        /// small enough for that to be honest work: one 512-byte header per entry carrying an octal byte count, the
        /// content padded out to the next 512 boundary, two zero blocks at the end.
        ///
        /// Anything that is not a plain file or a directory - a link, a device node, a PAX or GNU extension header -
        /// is stepped over rather than interpreted. npm publishes plain ustar, so nothing real is lost, and a reader
        /// that quietly does something with a symlink entry is a reader that can write outside its destination.
        /// </summary>
        private static void Untar(Stream tar, string destination)
        {
            string root = Path.GetFullPath(destination);
            var header = new byte[512];
            var block = new byte[81920];
            long unpacked = 0;
            int entries = 0;

            while (Fill(tar, header, 512))
            {
                if (IsZero(header)) break;

                if (++entries > MaxEntries)
                    throw new InvalidDataException("the archive holds more than " + MaxEntries + " entries");

                // A name over 100 characters is split across the prefix and name fields, which 165 of this package's
                // entries need. Reading only the name field silently truncates those paths instead of failing.
                string name = Text(header, 0, 100);
                string prefix = Text(header, 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;

                long size = Octal(header, 124, 12);
                long padding = ((size + 511) / 512) * 512 - size;
                char kind = (char)header[156];

                unpacked += size;
                if (unpacked > MaxUnpackedBytes)
                    throw new InvalidDataException("the archive unpacks to more than " +
                                                   Megabytes(MaxUnpackedBytes) + ", past the cap");

                // '\0' is what pre-ustar writers used for a regular file, and some writers record a directory as a
                // zero-length file whose name ends in a slash.
                bool file = kind is '0' or '\0';
                bool folder = kind == '5' || (file && name.EndsWith("/", StringComparison.Ordinal));
                if (folder) file = false;

                string relative = file || folder ? SafeRelativePath(name) : null;
                string full = relative == null ? null : Path.GetFullPath(Path.Combine(root, relative));

                // Belt and braces over SafeRelativePath: whatever the header said, the result has to be inside the
                // destination, and that is cheapest to assert on the resolved path.
                if (full != null && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    full = null;

                if (full == null) { Skip(tar, block, size + padding); continue; }

                if (folder)
                {
                    Directory.CreateDirectory(full);
                    Skip(tar, block, size + padding);
                    continue;
                }

                // The package carries no directory entries at all, so every folder is created from its files.
                Directory.CreateDirectory(Path.GetDirectoryName(full));

                using (FileStream output = File.Create(full)) Copy(tar, output, size, block);
                Skip(tar, block, padding);
            }
        }

        /// <summary>
        /// The entry's path reduced to something that can only land under the destination, or null to refuse it.
        ///
        /// An archive is untrusted input even from a registry: an absolute path or a ".." segment is exactly how a
        /// tar is used to write somewhere it was never unpacked to.
        /// </summary>
        private static string SafeRelativePath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // A leading separator is absolute, and on Windows so is "C:..." and a UNC path, even without one.
            if (name[0] == '/' || name[0] == '\\') return null;
            if (name.Length > 1 && name[1] == ':') return null;

            var kept = new List<string>();

            foreach (string segment in name.Replace('\\', '/').Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..") return null;

                kept.Add(segment);
            }

            return kept.Count == 0 ? null : string.Join(Path.DirectorySeparatorChar.ToString(), kept);
        }

        /// <summary>Read exactly <paramref name="count"/> bytes, or report that the stream ended. Stream.ReadExactly
        /// is .NET 7, and a decompressing stream returns short reads constantly.</summary>
        private static bool Fill(Stream stream, byte[] buffer, int count)
        {
            int filled = 0;

            while (filled < count)
            {
                int read = stream.Read(buffer, filled, count - filled);
                if (read <= 0) return false;

                filled += read;
            }

            return true;
        }

        private static void Copy(Stream source, Stream destination, long count, byte[] block)
        {
            while (count > 0)
            {
                int read = source.Read(block, 0, (int)Math.Min(count, block.Length));
                if (read <= 0) throw new InvalidDataException("the archive ended in the middle of a file");

                destination.Write(block, 0, read);
                count -= read;
            }
        }

        private static void Skip(Stream stream, byte[] block, long count)
        {
            while (count > 0)
            {
                int read = stream.Read(block, 0, (int)Math.Min(count, block.Length));
                if (read <= 0) return;

                count -= read;
            }
        }

        private static bool IsZero(byte[] block)
        {
            foreach (byte b in block)
                if (b != 0) return false;

            return true;
        }

        /// <summary>A NUL-terminated header field. Trailing spaces are part of how tar pads numbers.</summary>
        private static string Text(byte[] header, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && header[end] != 0) end++;

            return Encoding.UTF8.GetString(header, offset, end - offset).Trim();
        }

        private static long Octal(byte[] header, int offset, int length)
        {
            string text = Text(header, offset, length);
            if (text.Length == 0) return 0;

            long value = 0;

            foreach (char c in text)
            {
                // Sizes past 8 GB use a base-256 encoding instead. Nothing here is remotely that large, so refusing
                // is more useful than a number that would be wrong.
                if (c < '0' || c > '7') throw new InvalidDataException("a tar header holds a size that is not octal");

                value = value * 8 + (c - '0');
            }

            return value;
        }

        // ------------------------------------------------------------------ plumbing --

        private static HttpClient CreateClient()
        {
            // The registry redirects to its CDN, and there is no allowlist question here the way there is for a
            // page's fetch: the destination is a constant and the bytes are checksum-verified before they are used.
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            };

            var client = new HttpClient(handler)
            {
                // The per-download token owns the deadline, so a second limit here would only cut it somewhere else.
                Timeout = Timeout.InfiniteTimeSpan,
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Sideload/" + typeof(Core).Assembly.GetName().Version + " (Schedule I mod)");

            return client;
        }

        private static string Megabytes(long bytes) =>
            (bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";

        private static string Seconds(long milliseconds) =>
            (milliseconds / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s";

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
