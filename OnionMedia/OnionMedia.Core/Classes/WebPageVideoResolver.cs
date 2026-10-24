/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnionMedia.Core.Services;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace OnionMedia.Core.Classes
{
    /// <summary>
    /// Fallback extractor for web pages that yt-dlp only understands through its "generic" extractor
    /// (e.g. WordPress sites like fap-nation.org with a self-hosted player plus download buttons):
    /// <list type="bullet">
    /// <item>downloads the page through yt-dlp (so cookies + Cloudflare impersonation keep working),</item>
    /// <item>collects download buttons (360p/720p/1080p links), &lt;video&gt;/&lt;source&gt; tags, player attributes,
    /// JSON-LD/og:video and iframes,</item>
    /// <item>drops ad banners (autoplay+loop+muted videos, 728x90/300x250 creatives, *.gif.mp4),</item>
    /// <item>probes the remaining files with ffprobe and exposes them as formats of ONE video,
    /// so the normal quality ceiling ("Max Quality") applies.</item>
    /// </list>
    /// </summary>
    public static class WebPageVideoResolver
    {
        public const string ExtractorName = "OnionMediaWebPage";

        public sealed class SourceInfo
        {
            /// <summary>The page the user added (used as Referer).</summary>
            public string PageUrl { get; init; }
            /// <summary>Set when the video lives in an embedded player (iframe); downloads then go through yt-dlp with this URL.</summary>
            public string EmbedUrl { get; init; }
        }

        private static readonly ConditionalWeakTable<VideoData, SourceInfo> sources = new();

        public static SourceInfo GetSourceInfo(VideoData video)
            => video != null && sources.TryGetValue(video, out var info) ? info : null;

        public static bool IsWebPageItem(VideoData video)
            => video != null && (video.Extractor == ExtractorName || GetSourceInfo(video) != null);

        /// <summary>
        /// Decides whether the normal yt-dlp result should be replaced by a page scan.
        /// Only non-YouTube URLs, and only when yt-dlp failed or returned a "generic" playlist of loose files.
        /// </summary>
        public static bool ShouldTryResolve(string url, RunResult<VideoData> result)
        {
            if (string.IsNullOrWhiteSpace(url) || YtDlpOptions.IsYoutubeUrl(url)) return false;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            var data = result?.Data;
            if (data == null) return true;

            bool noFormats = data.Formats == null || data.Formats.Length == 0;
            bool isGeneric = string.IsNullOrEmpty(data.Extractor) || data.Extractor.Equals("generic", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(data.ExtractorKey, "Generic", StringComparison.OrdinalIgnoreCase);
            if (data.ResultType is MetadataType.Playlist or MetadataType.MultiVideo && noFormats && isGeneric)
                return true;
            return false;
        }

        public static async Task<RunResult<VideoData>> TryResolveAsync(string pageUrl, RunResult<VideoData> original, CancellationToken ct = default)
        {
            List<string> log = new();
            try
            {
                string html = await FetchPageHtmlAsync(pageUrl, log, ct);
                List<Candidate> candidates = html != null ? ExtractCandidates(html, pageUrl) : new List<Candidate>();
                log.Add($"[OnionMedia] Page scan: {candidates.Count} media candidate(s) found.");

                // Also use what yt-dlp's generic extractor found (works even if the HTML could not be saved).
                foreach (var entry in original?.Data?.Entries ?? Array.Empty<VideoData>())
                {
                    string u = entry?.Url ?? entry?.Formats?.FirstOrDefault()?.Url;
                    if (!string.IsNullOrEmpty(u) && IsMediaUrl(u))
                        AddCandidate(candidates, new Candidate { Url = u, Kind = CandidateKind.Player, Score = 5 });
                }

                candidates = candidates.Where(c => !IsLikelyAd(c.Url)).ToList();
                // URLs that only appear somewhere in scripts are a last resort (often rotating ad creatives).
                if (candidates.Any(c => c.Kind < CandidateKind.Loose))
                    candidates = candidates.Where(c => c.Kind != CandidateKind.Loose).ToList();
                var direct = candidates.Where(c => c.Kind != CandidateKind.Embed).ToList();
                var embeds = candidates.Where(c => c.Kind == CandidateKind.Embed).ToList();

                PageMeta meta = html != null ? ExtractMeta(html, pageUrl) : new PageMeta();
                if (string.IsNullOrWhiteSpace(meta.Title))
                    meta.Title = CleanTitle(original?.Data?.Title) ?? pageUrl;
                meta.Thumbnail ??= original?.Data?.Thumbnail;

                if (direct.Any())
                {
                    var video = await BuildFromDirectCandidatesAsync(pageUrl, direct, meta, log, ct);
                    if (video != null)
                    {
                        sources.AddOrUpdate(video, new SourceInfo { PageUrl = pageUrl });
                        return new RunResult<VideoData>(true, log.ToArray(), video);
                    }
                }

                foreach (var embed in embeds.OrderByDescending(e => e.Score).Take(3))
                {
                    var embedded = await FetchEmbeddedAsync(embed.Url, pageUrl, log, ct);
                    if (embedded?.Data == null) continue;
                    if (string.IsNullOrWhiteSpace(embedded.Data.Title) || embedded.Data.Title.Equals(embedded.Data.ID))
                        embedded.Data.Title = meta.Title;
                    embedded.Data.Thumbnail ??= meta.Thumbnail;
                    sources.AddOrUpdate(embedded.Data, new SourceInfo { PageUrl = pageUrl, EmbedUrl = embed.Url });
                    return new RunResult<VideoData>(true, log.ToArray(), embedded.Data);
                }

                log.Add("[OnionMedia] No playable video was found on this page.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.Add("[OnionMedia] Page scan failed: " + ex.Message);
            }

            var errors = (original?.ErrorOutput ?? Array.Empty<string>()).Concat(log).ToArray();
            return new RunResult<VideoData>(false, errors, null);
        }

        // ---------------------------------------------------------------- page download

        private static async Task<string> FetchPageHtmlAsync(string pageUrl, List<string> log, CancellationToken ct)
        {
            var pathProvider = IoC.Default.GetService<IPathProvider>();
            string dir = Path.Combine(pathProvider.DownloaderTempdir, "pages_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                bool first = YtDlpOptions.NeedsImpersonation(pageUrl);
                foreach (bool impersonate in new[] { first, !first, true })
                {
                    OptionSet opts = new();
                    using var cleanup = YtDlpOptions.Apply(opts, pageUrl, forceImpersonation: impersonate);
                    string args = $"--ignore-config --no-update --no-playlist --flat-playlist --skip-download --write-pages --no-warnings{opts} \"{pageUrl.Replace("\"", "%22")}\"";
                    var (code, output) = await YtDlpCustomArgs.RunYtDlpAsync(args, TimeSpan.FromSeconds(60), ct, dir);

                    string html = Directory.GetFiles(dir, "*.dump")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .Select(f => ReadText(f.FullName))
                        .FirstOrDefault(t => t != null && t.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (html != null)
                    {
                        if (impersonate) YtDlpOptions.RememberImpersonation(pageUrl);
                        return html;
                    }
                    log.Add($"[OnionMedia] Could not save page (impersonate={impersonate}): {LastLines(output, 3)}");
                    if (YtDlpOptions.LooksLikeAntiBotBlock(new[] { output }))
                        YtDlpOptions.RememberImpersonation(pageUrl);
                }
                return null;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static string ReadText(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        private static string LastLines(string text, int count)
            => string.Join(" | ", (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(count));

        // ---------------------------------------------------------------- candidates

        private enum CandidateKind { Button, Player, Attribute, Metadata, Loose, Embed }

        private sealed class Candidate
        {
            public string Url { get; set; }
            public string Label { get; set; }
            public int? Height { get; set; }
            public CandidateKind Kind { get; set; }
            public int Score { get; set; }
            public MediaProbe.Result Probe { get; set; }
        }

        private static readonly Regex mediaExtRegex = new(@"\.(mp4|m4v|webm|mkv|mov|m3u8)(?=$|[?#&]|/(?:$|[?#]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex anchorRegex = new(@"<a\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex videoBlockRegex = new(@"<video\b([^>]*)>(.*?)</video>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex sourceTagRegex = new(@"<source\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex iframeRegex = new(@"<iframe\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex mediaAttrRegex = new(@"\b(?:src|data-src|data-video|data-video-src|data-file|file|data-url|video-url)\s*=\s*([""'])(?<u>[^""']+?)\1", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex jsonUrlRegex = new(@"""(?<k>contentUrl|embedUrl|file|src|video_url|videoUrl|mp4|hls)""\s*:\s*""(?<u>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex looseUrlRegex = new(@"https?:(?:\\?/){2}[^\s""'<>()]+?\.(?:mp4|m4v|webm|m3u8)(?:\?[^\s""'<>()]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex heightRegex = new(@"(?<![\d])(\d{3,4})\s*[pP](?![a-zA-Z])", RegexOptions.Compiled);

        // Typical IAB ad creative sizes (728x90, 300x250, ...) and other banner markers.
        private static readonly Regex adRegex = new(
            @"(banner|/ads?/|[_\-/.]ads?[_\-/.]|adserver|doubleclick|googlesyndication|exoclick|juicyads|trafficjunky|a-ads\.com|popads|preroll|promo[_\-]|sponsor|\.gif\.mp4|" +
            @"(?<!\d)(728|300|160|320|468|970|336|900|120)[x_\-]?(90|250|600|50|60|280|100|240)(?!\d))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] embedHostBlacklist = { "a-ads.com", "doubleclick", "googlesyndication", "facebook.com", "twitter.com", "x.com/", "disqus", "recaptcha", "google.com/maps", "googletagmanager", "exoclick", "juicyads", "patreon.com", "discord" };

        private static bool IsMediaUrl(string url) => mediaExtRegex.IsMatch(url ?? string.Empty);

        private static bool IsLikelyAd(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            string pathAndQuery = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath : url;
            return adRegex.IsMatch(pathAndQuery);
        }

        private static List<Candidate> ExtractCandidates(string html, string pageUrl)
        {
            List<Candidate> list = new();
            string body = Regex.Replace(html, @"<script\b[^>]*src=[^>]*>\s*</script>", string.Empty, RegexOptions.IgnoreCase);

            // 1) Download buttons / links pointing to media files (often several qualities).
            foreach (Match m in anchorRegex.Matches(body))
            {
                string href = GetAttr(m.Groups[1].Value, "href");
                string url = Normalize(href, pageUrl);
                if (url == null || !IsMediaUrl(url)) continue;
                string label = StripTags(m.Groups[2].Value);
                string attrs = m.Groups[1].Value;
                int score = 30;
                if (Regex.IsMatch(attrs, "button|btn|download", RegexOptions.IgnoreCase)) score += 10;
                if (Regex.IsMatch(label, "download|скачать|\\d{3,4}\\s*p", RegexOptions.IgnoreCase)) score += 5;
                AddCandidate(list, new Candidate { Url = url, Label = label, Height = ParseHeight(label) ?? ParseHeight(url), Kind = CandidateKind.Button, Score = score });
            }

            // 2) <video> players (skip decorative/ad videos: autoplay + loop + muted).
            HashSet<string> decorative = new(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in videoBlockRegex.Matches(body))
            {
                string attrs = m.Groups[1].Value;
                bool isDecor = Regex.IsMatch(attrs, @"\bloop\b", RegexOptions.IgnoreCase) && Regex.IsMatch(attrs, @"\bmuted\b", RegexOptions.IgnoreCase);
                List<string> urls = new();
                string own = GetAttr(attrs, "src") ?? GetAttr(attrs, "data-src");
                if (own != null) urls.Add(own);
                foreach (Match s in sourceTagRegex.Matches(m.Groups[2].Value))
                {
                    string src = GetAttr(s.Groups[1].Value, "src") ?? GetAttr(s.Groups[1].Value, "data-src");
                    if (src != null) urls.Add(src);
                }
                foreach (var raw in urls)
                {
                    string url = Normalize(raw, pageUrl);
                    if (url == null) continue;
                    if (isDecor) { decorative.Add(url); continue; }
                    string poster = GetAttr(attrs, "poster");
                    AddCandidate(list, new Candidate { Url = url, Height = ParseHeight(url), Kind = CandidateKind.Player, Score = 25 });
                }
            }

            // 3) Player web components / data attributes (presto-player src="...", data-video="...").
            foreach (Match m in mediaAttrRegex.Matches(body))
            {
                string url = Normalize(m.Groups["u"].Value, pageUrl);
                if (url == null || !IsMediaUrl(url) || decorative.Contains(url)) continue;
                AddCandidate(list, new Candidate { Url = url, Height = ParseHeight(url), Kind = CandidateKind.Attribute, Score = 15 });
            }

            // 4) JSON-LD / inline JSON (VideoObject.contentUrl, embedUrl, player configs).
            foreach (Match m in jsonUrlRegex.Matches(html))
            {
                string url = Normalize(JsonUnescape(m.Groups["u"].Value), pageUrl);
                if (url == null || decorative.Contains(url)) continue;
                if (IsMediaUrl(url))
                    AddCandidate(list, new Candidate { Url = url, Height = ParseHeight(url), Kind = CandidateKind.Metadata, Score = 20 });
                else if (m.Groups["k"].Value.Equals("embedUrl", StringComparison.OrdinalIgnoreCase))
                    AddCandidate(list, new Candidate { Url = url, Kind = CandidateKind.Embed, Score = 12 });
            }

            // 4b) KVS (Kernel Video Sharing) players used by many tube sites: flashvars video_url / video_alt_url{N}
            //     with "<N>p" labels; links may be obfuscated ("function/0/...") with the page's license_code.
            AddKvsCandidates(html, pageUrl, list, decorative);

            // 5) og:video meta tags.
            foreach (Match m in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
            {
                string tag = m.Value;
                string prop = GetAttr(tag, "property") ?? GetAttr(tag, "name");
                if (prop == null || !prop.StartsWith("og:video", StringComparison.OrdinalIgnoreCase) || prop.Contains(":type") || prop.Contains(":width") || prop.Contains(":height")) continue;
                string url = Normalize(GetAttr(tag, "content"), pageUrl);
                if (url == null) continue;
                AddCandidate(list, new Candidate { Url = url, Height = ParseHeight(url), Kind = IsMediaUrl(url) ? CandidateKind.Metadata : CandidateKind.Embed, Score = 18 });
            }

            // 6) Embedded players (iframes) – resolved later through yt-dlp.
            foreach (Match m in iframeRegex.Matches(body))
            {
                string src = GetAttr(m.Groups[1].Value, "src") ?? GetAttr(m.Groups[1].Value, "data-src") ?? GetAttr(m.Groups[1].Value, "data-lazy-src");
                string url = Normalize(src, pageUrl);
                if (url == null || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) continue;
                if (embedHostBlacklist.Any(b => url.Contains(b, StringComparison.OrdinalIgnoreCase)) || IsLikelyAd(url)) continue;
                AddCandidate(list, new Candidate { Url = url, Kind = IsMediaUrl(url) ? CandidateKind.Player : CandidateKind.Embed, Score = 10 });
            }

            // 7) Any other media URL in the page source (e.g. inside player JavaScript).
            foreach (Match m in looseUrlRegex.Matches(html))
            {
                string url = Normalize(JsonUnescape(m.Value), pageUrl);
                if (url == null || decorative.Contains(url)) continue;
                AddCandidate(list, new Candidate { Url = url, Height = ParseHeight(url), Kind = CandidateKind.Loose, Score = 3 });
            }

            return list.Where(c => !decorative.Contains(c.Url)).ToList();
        }

        private static readonly Regex kvsVarRegex = new(@"\b(?<k>video_url|video_alt_url\d*|license_code)(?<t>_text)?\s*:\s*(?<q>['""])(?<v>[^'""]*)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void AddKvsCandidates(string html, string pageUrl, List<Candidate> list, HashSet<string> decorative)
        {
            Dictionary<string, string> urls = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> labels = new(StringComparer.OrdinalIgnoreCase);
            string license = null;
            foreach (Match m in kvsVarRegex.Matches(html))
            {
                string key = m.Groups["k"].Value, value = m.Groups["v"].Value;
                if (key.Equals("license_code", StringComparison.OrdinalIgnoreCase)) { license ??= value; continue; }
                var target = m.Groups["t"].Success ? labels : urls;
                if (!target.ContainsKey(key)) target[key] = value;
            }
            foreach (var (key, raw) in urls)
            {
                string decoded = KvsGetRealUrl(JsonUnescape(raw), license);
                string url = Normalize(decoded, pageUrl);
                if (url == null || decorative.Contains(url) || url.Contains("/preview", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsMediaUrl(url) && !url.Contains("/get_file/", StringComparison.OrdinalIgnoreCase)) continue;
                labels.TryGetValue(key, out string label);
                int? height = ParseHeight(label ?? string.Empty) ?? ParseHeight(url);
                AddCandidate(list, new Candidate { Url = url, Label = label, Height = height, Kind = CandidateKind.Player, Score = key.Equals("video_url", StringComparison.OrdinalIgnoreCase) ? 28 : 26 });
            }
        }

        /// <summary>Port of yt-dlp's KVS url de-obfuscation (GenericIE._kvs_get_real_url).</summary>
        internal static string KvsGetRealUrl(string videoUrl, string licenseCode)
        {
            const string prefix = "function/0/";
            if (string.IsNullOrEmpty(videoUrl) || !videoUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return videoUrl;
            string rest = videoUrl.Substring(prefix.Length);
            try
            {
                int[] token = KvsLicenseToken(licenseCode);
                if (!Uri.TryCreate(rest, UriKind.Absolute, out var uri) || token == null) return rest;
                string[] parts = uri.AbsolutePath.Split('/');
                const int HashLength = 32;
                if (parts.Length < 4 || parts[3].Length < HashLength || token.Length < HashLength) return rest;
                string hash = parts[3].Substring(0, HashLength);
                int[] indices = Enumerable.Range(0, HashLength).ToArray();
                int accum = 0;
                for (int src = HashLength - 1; src >= 0; src--)
                {
                    accum += token[src];
                    int dest = (src + accum) % HashLength;
                    (indices[src], indices[dest]) = (indices[dest], indices[src]);
                }
                parts[3] = new string(indices.Select(i => hash[i]).ToArray()) + parts[3].Substring(HashLength);
                return new UriBuilder(uri) { Path = string.Join("/", parts) }.Uri.AbsoluteUri;
            }
            catch { return rest; }
        }

        private static int[] KvsLicenseToken(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            license = license.Replace("$", string.Empty);
            if (license.Length < 2 || !license.All(char.IsDigit)) return null;
            int[] values = license.Select(c => c - '0').ToArray();
            string mod = license.Replace('0', '1');
            int center = mod.Length / 2;
            var front = System.Numerics.BigInteger.Parse(mod.Substring(0, center + 1));
            var back = System.Numerics.BigInteger.Parse(mod.Substring(center));
            string modLicense = (4 * System.Numerics.BigInteger.Abs(front - back)).ToString();
            modLicense = modLicense.Substring(0, Math.Min(modLicense.Length, center + 1));
            List<int> token = new();
            for (int index = 0; index < modLicense.Length; index++)
                for (int offset = 0; offset < 4; offset++)
                    token.Add((values[Math.Min(index + offset, values.Length - 1)] + (modLicense[index] - '0')) % 10);
            return token.ToArray();
        }

        private static void AddCandidate(List<Candidate> list, Candidate c)
        {
            var existing = list.FirstOrDefault(x => x.Url.Equals(c.Url, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                list.Add(c);
                return;
            }
            // Seen in several places (e.g. player AND download button) -> very likely the main video.
            existing.Score += Math.Max(5, c.Score / 2);
            existing.Height ??= c.Height;
            if (string.IsNullOrWhiteSpace(existing.Label)) existing.Label = c.Label;
            if (c.Kind < existing.Kind) existing.Kind = c.Kind;
        }

        private static async Task<VideoData> BuildFromDirectCandidatesAsync(string pageUrl, List<Candidate> candidates, PageMeta meta, List<string> log, CancellationToken ct)
        {
            // Probe the most promising files (in parallel) to get duration, resolution, codecs and bitrate.
            var toProbe = candidates.OrderByDescending(c => c.Score).Take(8).ToList();
            // All at once with a short timeout: a slow/unreachable candidate (ads, dead links) must not delay the result.
            using SemaphoreSlim gate = new(8);
            await Task.WhenAll(toProbe.Select(async c =>
            {
                await gate.WaitAsync(ct);
                try { c.Probe = await MediaProbe.ProbeAsync(c.Url, pageUrl, TimeSpan.FromSeconds(10), ct); }
                finally { gate.Release(); }
            }));

            var usable = toProbe.Where(c => c.Probe == null || c.Probe.HasVideo).ToList();
            if (!usable.Any()) usable = toProbe;

            // Short clips (< 15 s) next to a real video are ads/teasers.
            double maxDuration = usable.Max(c => c.Probe?.DurationSeconds ?? 0);
            if (maxDuration >= 15)
                usable = usable.Where(c => c.Probe?.DurationSeconds == null || c.Probe.DurationSeconds >= 15).ToList();

            // Keep the files that are the same video as the best candidate (same duration ±3 s = different qualities).
            var best = usable.OrderByDescending(c => c.Score).ThenByDescending(c => c.Probe?.DurationSeconds ?? 0).First();
            double? refDuration = best.Probe?.DurationSeconds;
            var variants = usable.Where(c => refDuration == null || c.Probe?.DurationSeconds == null
                                             || Math.Abs(c.Probe.DurationSeconds.Value - refDuration.Value) <= Math.Max(3, refDuration.Value * 0.02)).ToList();
            // Unprobed loose URLs are too risky to keep when we have confirmed variants.
            if (variants.Any(v => v.Probe != null))
                variants = variants.Where(v => v.Probe != null || v.Kind is CandidateKind.Button).ToList();

            foreach (var v in variants)
                log.Add($"[OnionMedia] Variant: {v.Label ?? v.Kind.ToString()} {v.Probe?.Height ?? v.Height}p {v.Probe?.VideoCodec}/{v.Probe?.AudioCodec} {v.Url}");

            List<FormatData> formats = new();
            int index = 0;
            foreach (var v in variants.OrderBy(v => v.Probe?.Height ?? v.Height ?? 0).ThenBy(v => v.Probe?.BitrateKbps ?? 0))
            {
                var p = v.Probe;
                int? height = p?.Height ?? v.Height;
                int? width = p?.Width ?? (height.HasValue ? (int)Math.Round(height.Value * 16 / 9.0) : null);
                string ext = Path.GetExtension(new Uri(v.Url).AbsolutePath).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "mp4";
                formats.Add(new FormatData
                {
                    FormatId = $"web{index++}-{(height?.ToString() ?? "src")}p",
                    Url = v.Url,
                    Extension = ext == "m3u8" ? "mp4" : ext,
                    Protocol = ext == "m3u8" ? "m3u8_native" : "https",
                    Width = width,
                    Height = height,
                    FrameRate = p?.FrameRate ?? 0f,
                    VideoCodec = p?.VideoCodec ?? (ext == "m3u8" ? null : "unknown"),
                    AudioCodec = p?.AudioCodec ?? (p == null ? "unknown" : "none"),
                    Bitrate = p?.BitrateKbps,
                    VideoBitrate = p?.VideoBitrateKbps,
                    AudioBitrate = p?.AudioBitrateKbps,
                    FileSize = p?.SizeBytes,
                    ContainerFormat = ext,
                    FormatNote = string.IsNullOrWhiteSpace(v.Label) ? v.Kind.ToString().ToLowerInvariant() : v.Label.Trim(),
                    Resolution = width.HasValue && height.HasValue ? $"{width}x{height}" : null,
                    HDR = "SDR",
                });
            }
            if (!formats.Any()) return null;

            double? duration = variants.Select(v => v.Probe?.DurationSeconds).Where(d => d.HasValue).DefaultIfEmpty(null).Max();
            return new VideoData
            {
                ID = "web-" + MakeId(pageUrl),
                Title = meta.Title,
                Url = pageUrl,
                WebpageUrl = pageUrl,
                Extractor = ExtractorName,
                ExtractorKey = ExtractorName,
                Thumbnail = meta.Thumbnail,
                Description = meta.Description,
                Uploader = YtDlpOptions.GetHost(pageUrl),
                UploadDate = meta.UploadDate,
                Duration = duration.HasValue ? (float)duration.Value : 0f,
                Formats = formats.ToArray(),
                ResultType = MetadataType.Video,
            };
        }

        private static async Task<RunResult<VideoData>> FetchEmbeddedAsync(string embedUrl, string pageUrl, List<string> log, CancellationToken ct)
        {
            foreach (bool impersonate in new[] { YtDlpOptions.NeedsImpersonation(embedUrl), !YtDlpOptions.NeedsImpersonation(embedUrl) })
            {
                OptionSet opts = new();
                opts.AddCustomOption("--referer", pageUrl);
                using var cleanup = YtDlpOptions.Apply(opts, embedUrl, forceImpersonation: impersonate);
                var result = await DownloaderMethods.downloadClient.RunVideoDataFetch(embedUrl, ct, overrideOptions: opts);
                if (result.Success && result.Data?.Formats?.Length > 0)
                {
                    if (impersonate) YtDlpOptions.RememberImpersonation(embedUrl);
                    log.Add($"[OnionMedia] Using embedded player {embedUrl}");
                    return result;
                }
                log.Add($"[OnionMedia] Embedded player {embedUrl} failed: {string.Join(" | ", result.ErrorOutput.TakeLast(2))}");
            }
            return null;
        }

        // ---------------------------------------------------------------- metadata

        private sealed class PageMeta
        {
            public string Title { get; set; }
            public string Thumbnail { get; set; }
            public string Description { get; set; }
            public DateTime? UploadDate { get; set; }
        }

        private static PageMeta ExtractMeta(string html, string pageUrl)
        {
            PageMeta meta = new();
            string MetaContent(string key)
            {
                foreach (Match m in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    string prop = GetAttr(m.Value, "property") ?? GetAttr(m.Value, "name");
                    if (prop != null && prop.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return WebUtility.HtmlDecode(GetAttr(m.Value, "content") ?? string.Empty).Trim();
                }
                return null;
            }

            var h1 = Regex.Match(html, @"<h1\b[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string h1Text = h1.Success ? StripTags(h1.Groups[1].Value) : null;
            string ogTitle = MetaContent("og:title");
            var titleTag = Regex.Match(html, @"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // og:title first: on many tube sites the first <h1> is a site/section heading (e.g. "AI Series").
            meta.Title = new[] { CleanTitle(ogTitle), h1Text, CleanTitle(titleTag.Success ? StripTags(titleTag.Groups[1].Value) : null) }
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && t.Length >= 3);

            meta.Thumbnail = Normalize(MetaContent("og:image"), pageUrl);
            if (meta.Thumbnail == null)
            {
                var poster = Regex.Match(html, @"\bposter\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (poster.Success) meta.Thumbnail = Normalize(poster.Groups[1].Value, pageUrl);
            }
            meta.Description = MetaContent("og:description") ?? MetaContent("description");

            string date = MetaContent("article:published_time");
            if (date == null)
            {
                var m = Regex.Match(html, @"""uploadDate""\s*:\s*""([^""]+)""");
                if (m.Success) date = m.Groups[1].Value;
            }
            if (date != null && DateTimeOffset.TryParse(date, out var dto))
                meta.UploadDate = dto.DateTime;
            return meta;
        }

        /// <summary>Removes the " » SITE" / " | SITE" / " - SITE" suffix from page titles.</summary>
        private static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            title = WebUtility.HtmlDecode(title).Trim();
            var m = Regex.Match(title, @"^(.*\S)\s+(?:»|\||–|—|-)\s+([^»|–—]{2,30})$");
            return m.Success ? m.Groups[1].Value.Trim() : title;
        }

        // ---------------------------------------------------------------- helpers

        private static string GetAttr(string attrs, string name)
        {
            var m = Regex.Match(attrs ?? string.Empty, @"(?<![\w-])" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            return string.IsNullOrWhiteSpace(v) ? null : WebUtility.HtmlDecode(v.Trim());
        }

        private static string Normalize(string raw, string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = WebUtility.HtmlDecode(raw.Trim());
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("#")) return null;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return null;
            if (raw.StartsWith("//")) raw = baseUri.Scheme + ":" + raw;
            return Uri.TryCreate(baseUri, raw, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
                ? abs.AbsoluteUri
                : null;
        }

        private static string JsonUnescape(string s) => (s ?? string.Empty).Replace("\\/", "/").Replace("\\u0026", "&").Replace("&amp;", "&");

        private static string StripTags(string s)
            => WebUtility.HtmlDecode(Regex.Replace(s ?? string.Empty, "<[^>]+>", " ")).Replace('\u00a0', ' ').Trim();

        private static int? ParseHeight(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (Regex.IsMatch(text, @"\b4k\b|2160", RegexOptions.IgnoreCase)) return 2160;
            if (Regex.IsMatch(text, @"\b2k\b", RegexOptions.IgnoreCase)) return 1440;
            var m = heightRegex.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int h) && h is >= 144 and <= 4320) return h;
            return null;
        }

        private static string MakeId(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                string slug = u.AbsolutePath.Trim('/').Split('/').LastOrDefault(s => s.Length > 0);
                if (!string.IsNullOrEmpty(slug)) return Regex.Replace(slug, @"[^\w\-]", "_");
            }
            return Math.Abs(url.GetHashCode()).ToString();
        }
    }
}
