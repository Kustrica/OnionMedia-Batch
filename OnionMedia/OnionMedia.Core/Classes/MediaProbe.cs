/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OnionMedia.Core.Services;

namespace OnionMedia.Core.Classes
{
    /// <summary>
    /// Lightweight ffprobe wrapper that works for local files AND remote URLs
    /// (with a browser User-Agent + Referer, because Cloudflare blocks ffmpeg's default "Lavf" UA).
    /// </summary>
    public static class MediaProbe
    {
        public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        public sealed class Result
        {
            public double? DurationSeconds { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public float? FrameRate { get; set; }
            public string VideoCodec { get; set; }
            public string VideoCodecTag { get; set; }
            public string AudioCodec { get; set; }
            /// <summary>Total bitrate in kbit/s.</summary>
            public double? BitrateKbps { get; set; }
            public double? VideoBitrateKbps { get; set; }
            public double? AudioBitrateKbps { get; set; }
            public long? SizeBytes { get; set; }
            public string FormatName { get; set; }
            public string PixelFormat { get; set; }
            public bool HasVideo => !string.IsNullOrEmpty(VideoCodec);
            /// <summary>Index (among video streams) of an embedded cover image, or null.</summary>
            public int? AttachedPictureVideoIndex { get; set; }
        }

        public static string FFprobePath
        {
            get
            {
                var pathProvider = IoC.Default.GetService<IPathProvider>();
                string dir = Path.GetDirectoryName(pathProvider.FFmpegPath) ?? string.Empty;
                return Path.Combine(dir, "ffprobe.exe");
            }
        }

        public static async Task<Result> ProbeAsync(string fileOrUrl, string referer = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fileOrUrl)) return null;
            string ffprobe = FFprobePath;
            if (!File.Exists(ffprobe)) return null;

            bool isUrl = fileOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || fileOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            StringBuilder args = new("-v error -print_format json -show_format -show_streams ");
            if (isUrl)
            {
                args.Append($"-user_agent \"{BrowserUserAgent}\" ");
                if (!string.IsNullOrEmpty(referer))
                    args.Append($"-headers \"Referer: {referer.Replace("\"", string.Empty)}\r\n\" ");
                args.Append("-rw_timeout 8000000 ");   // 8 s per network read: slow hosts must not block adding a video
            }
            args.Append('"').Append(fileOrUrl.Replace("\"", "%22")).Append('"');

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = args.ToString(),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(true); } catch { }
                    return null;
                }
                string json = await stdoutTask;
                await stderrTask;
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return null;
                return Parse(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ffprobe failed for {fileOrUrl}: {ex.Message}");
                return null;
            }
        }

        private static Result Parse(string json)
        {
            var root = JObject.Parse(json);
            Result r = new();
            var format = root["format"];
            if (format != null)
            {
                r.DurationSeconds = ParseDouble(format["duration"]);
                r.BitrateKbps = ParseDouble(format["bit_rate"]) / 1000;
                r.SizeBytes = (long?)ParseDouble(format["size"]);
                r.FormatName = (string)format["format_name"];
            }
            if (root["streams"] is JArray streams)
            {
                int videoIndex = -1;
                foreach (var st in streams)
                {
                    string type = (string)st["codec_type"];
                    bool attachedPicture = st["disposition"]?["attached_pic"]?.ToString() == "1";
                    if (type == "video")
                    {
                        videoIndex++;
                        if (attachedPicture && r.AttachedPictureVideoIndex == null)
                            r.AttachedPictureVideoIndex = videoIndex;
                    }
                    if (type == "video" && r.VideoCodec == null && !attachedPicture)
                    {
                        r.VideoCodec = (string)st["codec_name"];
                        r.VideoCodecTag = (string)st["codec_tag_string"];
                        r.Width = (int?)ParseDouble(st["width"]);
                        r.Height = (int?)ParseDouble(st["height"]);
                        r.PixelFormat = (string)st["pix_fmt"];
                        r.VideoBitrateKbps = ParseDouble(st["bit_rate"]) / 1000;
                        string fr = (string)st["avg_frame_rate"] ?? (string)st["r_frame_rate"];
                        if (!string.IsNullOrEmpty(fr) && fr.Contains('/'))
                        {
                            var parts = fr.Split('/');
                            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
                                r.FrameRate = (float)Math.Round(n / d, 2);
                        }
                    }
                    else if (type == "audio" && r.AudioCodec == null)
                    {
                        r.AudioCodec = (string)st["codec_name"];
                        r.AudioBitrateKbps = ParseDouble(st["bit_rate"]) / 1000;
                    }
                }
            }
            return r;
        }

        private static double? ParseDouble(JToken token)
        {
            if (token == null) return null;
            string s = token.ToString();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }
    }
}
