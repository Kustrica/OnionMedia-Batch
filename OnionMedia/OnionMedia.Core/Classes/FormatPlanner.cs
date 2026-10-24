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
using System.Globalization;
using System.Linq;
using OnionMedia.Core.Enums;
using YoutubeDLSharp.Metadata;

namespace OnionMedia.Core.Classes
{
    /// <summary>
    /// Builds yt-dlp format selectors / sort strings that respect the "Max Quality" ceiling and the
    /// compatibility mode, and predicts which formats yt-dlp will pick so the queue can show it.
    /// </summary>
    public static class FormatPlanner
    {
        /// <summary>"1080p" -> 1080, "1080p60" -> 1080, null/empty -> null (no ceiling).</summary>
        public static int? ParseCap(string qualityLabel)
        {
            if (string.IsNullOrWhiteSpace(qualityLabel)) return null;
            string digits = new string(qualityLabel.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out int h) && h > 0 ? h : null;
        }

        /// <summary>
        /// Hard ceiling (height &lt;= cap) first; if a video has nothing at or below the cap,
        /// the format sort "res:cap" picks the closest (smallest) one above it instead of the best.
        /// </summary>
        public static string BuildFormatSelector(int? cap)
            => cap is int h
                ? $"bv*[height<={h}]+ba/b[height<={h}]/bv*+ba/b"
                : "bv*+ba/b";

        /// <summary>yt-dlp -S string. User fields (from custom args) come first, OnionMedia's fields follow.</summary>
        public static string BuildFormatSort(int? cap, VideoCompatibilityMode mode, string userSort = null)
        {
            List<string> fields = new();
            if (!string.IsNullOrWhiteSpace(userSort))
                fields.AddRange(userSort.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0));

            void Add(string f)
            {
                string key = f.TrimStart('+').Split(':')[0];
                if (!fields.Any(x => x.TrimStart('+').Split(':')[0].Equals(key, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(f);
            }

            Add("hdr:SDR");
            if (cap is int h) Add($"res:{h}");
            Add("fps");
            switch (mode)
            {
                case VideoCompatibilityMode.Universal:
                    Add("vcodec:h264");
                    Add("acodec:aac");
                    break;
                case VideoCompatibilityMode.Compact:
                    Add("vcodec:h265");
                    Add("acodec:aac");
                    break;
            }
            return string.Join(",", fields);
        }

        /// <summary>Picks the direct-file variant for web-page items: highest height &lt;= cap, else the smallest above.</summary>
        public static FormatData PickWebFormat(VideoData video, int? cap)
        {
            var formats = video?.Formats?.Where(f => !string.IsNullOrEmpty(f?.Url)).ToList() ?? new List<FormatData>();
            if (!formats.Any()) return null;
            if (cap is not int h)
                return formats.OrderByDescending(f => f.Height ?? 0).ThenByDescending(f => f.Bitrate ?? 0).First();
            var within = formats.Where(f => f.Height == null || f.Height <= h).ToList();
            if (within.Any())
                return within.OrderByDescending(f => f.Height ?? 0).ThenByDescending(f => f.Bitrate ?? 0).First();
            return formats.OrderBy(f => f.Height ?? int.MaxValue).ThenByDescending(f => f.Bitrate ?? 0).First();
        }

        /// <summary>Approximates yt-dlp's choice for the selector + sort built above.</summary>
        public static (FormatData video, FormatData audio) Predict(VideoData video, int? cap, VideoCompatibilityMode mode)
        {
            var formats = video?.Formats?.Where(f => f != null).ToList();
            if (formats == null || formats.Count == 0) return (null, null);
            if (WebPageVideoResolver.IsWebPageItem(video) && WebPageVideoResolver.GetSourceInfo(video)?.EmbedUrl == null)
                return (PickWebFormat(video, cap), null);

            var videos = formats.Where(f => HasVideo(f) && f.Height != null && (f.HDR ?? "SDR") == "SDR").ToList();
            if (!videos.Any()) videos = formats.Where(HasVideo).ToList();
            if (!videos.Any()) return (null, formats.Where(HasAudio).OrderByDescending(f => f.AudioBitrate ?? f.Bitrate ?? 0).FirstOrDefault());

            IEnumerable<FormatData> pool = videos;
            if (cap is int h)
            {
                var within = videos.Where(f => (f.Height ?? 0) <= h).ToList();
                pool = within.Any() ? within : videos.Where(f => f.Height == videos.Min(v => v.Height));
            }

            var chosen = pool
                .OrderByDescending(f => f.Height ?? 0)
                .ThenByDescending(f => f.FrameRate ?? 0)
                .ThenBy(f => VideoCodecRank(f.VideoCodec, mode))
                .ThenByDescending(f => f.VideoBitrate ?? f.Bitrate ?? 0)
                .First();

            FormatData audio = null;
            if (!HasAudio(chosen))
            {
                audio = formats.Where(f => HasAudio(f) && !HasVideo(f))
                    .OrderBy(f => AudioCodecRank(f.AudioCodec, mode))
                    .ThenByDescending(f => f.AudioBitrate ?? f.Bitrate ?? 0)
                    .FirstOrDefault();
            }
            return (chosen, audio);
        }

        public static bool HasVideo(FormatData f) => f != null && !string.IsNullOrEmpty(f.VideoCodec) && f.VideoCodec != "none";
        public static bool HasAudio(FormatData f) => f != null && !string.IsNullOrEmpty(f.AudioCodec) && f.AudioCodec != "none";

        private static int VideoCodecRank(string codec, VideoCompatibilityMode mode)
        {
            string c = NormalizeVideoCodec(codec);
            return mode switch
            {
                VideoCompatibilityMode.Universal => c switch { "H.264" => 0, "H.265" => 1, "VP9" => 2, "AV1" => 3, _ => 4 },
                VideoCompatibilityMode.Compact => c switch { "H.265" => 0, "H.264" => 1, "VP9" => 2, "AV1" => 3, _ => 4 },
                _ => c switch { "AV1" => 0, "VP9" => 1, "H.265" => 2, "H.264" => 3, _ => 4 },
            };
        }

        private static int AudioCodecRank(string codec, VideoCompatibilityMode mode)
        {
            string c = NormalizeAudioCodec(codec);
            if (mode == VideoCompatibilityMode.Original)
                return c switch { "Opus" => 0, "AAC" => 1, _ => 2 };
            return c switch { "AAC" => 0, "MP3" => 1, "Opus" => 2, _ => 3 };
        }

        /// <summary>Codec ids from yt-dlp (avc1.640028, vp09.00.., av01..) or ffprobe (h264, hevc, vp9, av1) -> display names.</summary>
        public static string NormalizeVideoCodec(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec) || codec == "none") return null;
            string c = codec.ToLowerInvariant();
            if (c.StartsWith("avc") || c.StartsWith("h264")) return "H.264";
            if (c.StartsWith("hvc") || c.StartsWith("hev") || c.StartsWith("hevc") || c.StartsWith("h265")) return "H.265";
            if (c.StartsWith("vp09") || c.StartsWith("vp9")) return "VP9";
            if (c.StartsWith("vp8")) return "VP8";
            if (c.StartsWith("av01") || c.StartsWith("av1")) return "AV1";
            if (c == "unknown") return null;
            return codec;
        }

        public static string NormalizeAudioCodec(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec) || codec == "none") return null;
            string c = codec.ToLowerInvariant();
            if (c.StartsWith("mp4a") || c.StartsWith("aac")) return "AAC";
            if (c.StartsWith("opus")) return "Opus";
            if (c.StartsWith("vorbis")) return "Vorbis";
            if (c.StartsWith("mp3")) return "MP3";
            if (c.StartsWith("ac-3") || c == "ac3") return "AC-3";
            if (c.StartsWith("ec-3") || c == "eac3") return "E-AC-3";
            if (c.StartsWith("flac")) return "FLAC";
            if (c == "unknown") return null;
            return codec;
        }

        /// <summary>Which codecs the final file will have after OnionMedia's compatibility step.</summary>
        public static (string video, string audio, string container) PredictOutput(string sourceVideoCodec, string sourceAudioCodec, VideoCompatibilityMode mode, string sourceExt)
        {
            string v = NormalizeVideoCodec(sourceVideoCodec);
            string a = NormalizeAudioCodec(sourceAudioCodec);
            bool hasAudio = !string.IsNullOrEmpty(sourceAudioCodec) && sourceAudioCodec != "none"; // "unknown" = probably has audio
            string outA = !hasAudio ? null : (a is "AAC" or "MP3" ? a : "AAC");
            switch (mode)
            {
                case VideoCompatibilityMode.Universal:
                    return ("H.264", outA, "MP4");
                case VideoCompatibilityMode.Compact:
                    return (v is "H.264" or "H.265" ? v : "H.265", outA, "MP4");
                default:
                    return (v, a, string.IsNullOrEmpty(sourceExt) ? "MP4" : sourceExt.ToUpperInvariant());
            }
        }

        public static string FormatBitrate(double? kbps)
        {
            if (kbps is not double k || k <= 0) return null;
            return k >= 1000 ? (k / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps" : k.ToString("0", CultureInfo.InvariantCulture) + " kbps";
        }

        public static string FormatSize(long? bytes)
        {
            if (bytes is not long b || b <= 0) return null;
            double mb = b / 1024d / 1024d;
            return mb >= 1024 ? (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        public static string FormatResolution(int? width, int? height, float? fps)
        {
            if (height is not int h || h <= 0) return null;
            string res = $"{h}p";
            if (fps is float f && f > 30.5f) res += Math.Round(f).ToString(CultureInfo.InvariantCulture);
            return res;
        }
    }
}
