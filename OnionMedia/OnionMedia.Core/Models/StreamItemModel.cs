/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using OnionMedia.Core.Classes;
using OnionMedia.Core.Extensions;
using OnionMedia.Core.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;

namespace OnionMedia.Core.Models
{
    [ObservableObject]
    public partial class StreamItemModel
    {
        /// <summary>
        /// Initializes a new StreamItemModel.
        /// </summary>
        /// <param name="video">The video to get informations from.</param>
        public StreamItemModel(RunResult<VideoData> video)
        {
            if (video.Data == null)
                throw new ArgumentNullException("video.Data is null!");

            if (video.Data.IsLive == true)
                throw new NotSupportedException("Livestreams cannot be downloaded.");

            Video = video.Data;
            if (Video.Thumbnail.IsNullOrEmpty())
                Video.Thumbnail = "https://upload.wikimedia.org/wikipedia/commons/thumb/a/ac/No_image_available.svg/300px-No_image_available.svg.png";
            // Duration is missing for some generic/web results; a null cast used to crash the item silently.
            Duration = TimeSpan.FromSeconds(Math.Max(0, Math.Round(Video.Duration ?? 0)));
            TimeSpanGroup = new TimeSpanGroup(Duration);

            // Some sites report only the height or only the width of a format: estimate the missing side (16:9),
            // so the quality list, the Max Quality ceiling and the item info work for them too.
            if (Video.Formats != null)
                foreach (var f in Video.Formats.Where(f => f != null && f.VideoCodec != "none"))
                {
                    if (f.Height is not > 0 && f.Width is > 0) f.Height = (int)Math.Round(f.Width.Value * 9 / 16.0);
                    else if (f.Width is not > 0 && f.Height is > 0) f.Width = (int)Math.Round(f.Height.Value * 16 / 9.0);
                }

            //Add video formats to the list
            if (Video.Formats != null)
                // Generic/KVS sites often report only the height (no width/fps) – the height is enough for the quality list.
                foreach (var format in Video.Formats.Where(f => f != null && f.Height is > 0 && !FormatQualityLabels.ContainsKey(f)))
                    FormatQualityLabels.Add(format, (format.Height ?? 0).RoundUpToNearestNeighbor(validResolutions));

            TimeSpanGroup.PropertyChanged += (o, e) =>
            {
                OnPropertyChanged(nameof(CustomTimes));
                RefreshChips();   // duration chip shows "trimmed (original)"
            };
            CancelSource = new();
            SetProgressToDefault();
            UpdatePlannedFormat(null, Enums.DownloadMode.Video);
        }

        #region Format info (shown in the queue)

        /// <summary>"m:ss" / "h:mm:ss", or null if unknown.</summary>
        public string DurationText => Duration.TotalSeconds < 1 ? null
            : Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");

        /// <summary>Resolution, bitrate, codecs, container, size of the format that is (or will be) downloaded.</summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(HasFormatInfo))]
        private IReadOnlyList<string> formatChips = Array.Empty<string>();

        /// <summary>One-line description of the result, e.g. "→ MP4 · H.264 + AAC (re-encode from AV1)".</summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(HasFormatInfo))]
        [AlsoNotifyChangeFor(nameof(FormatTooltip))]
        private string formatSummary;

        /// <summary>Planned / Downloading / Saved</summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(FormatTooltip))]
        private string formatStage;

        public bool HasFormatInfo => (FormatChips?.Count ?? 0) > 0 || !string.IsNullOrEmpty(FormatSummary);

        /// <summary>Most important info first: duration, resolution, size.</summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(HasSecondaryChips))]
        private IReadOnlyList<string> primaryChips = Array.Empty<string>();

        /// <summary>Technical details after the separator: bitrate, video codec, audio codec, container.</summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(HasSecondaryChips))]
        private IReadOnlyList<string> secondaryChips = Array.Empty<string>();

        public bool HasSecondaryChips => (SecondaryChips?.Count ?? 0) > 0 && (PrimaryChips?.Count ?? 0) > 0;

        /// <summary>Short hint when the file will be converted, e.g. "→ H.264" (details in <see cref="FormatSummary"/> as tooltip).</summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(HasConversionChip))]
        private string conversionChip;

        public bool HasConversionChip => !string.IsNullOrEmpty(ConversionChip);

        /// <summary>Tooltip for the chip row.</summary>
        public string FormatTooltip => formatDetails;

        /// <summary>Bare domain of the page the video comes from, e.g. "youtube.com" or "fap-nation.org".</summary>
        public string SiteDomain
        {
            get
            {
                string url = WebPageVideoResolver.GetSourceInfo(Video)?.PageUrl ?? Video?.Url ?? Video?.WebpageUrl;
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                    return Video?.ExtractorKey ?? Video?.Extractor;
                string host = uri.Host.ToLowerInvariant();
                foreach (string prefix in new[] { "www.", "m.", "music.", "mobile." })
                    if (host.StartsWith(prefix) && host.Count(c => c == '.') >= 2) { host = host[prefix.Length..]; break; }
                if (host == "youtu.be") host = "youtube.com";
                return host;
            }
        }

        private bool hasFinalFormat;

        /// <summary>Predicts which format will be downloaded for the given "Max Quality" label and download mode.</summary>
        public void UpdatePlannedFormat(string qualityLabel, Enums.DownloadMode downloadMode)
        {
            if (hasFinalFormat && DownloadState is Enums.DownloadState.IsDone) return;
            hasFinalFormat = false;
            try
            {
                var mode = downloadMode == Enums.DownloadMode.Video ? AppSettings.Instance.VideoCompatibilityMode : Enums.VideoCompatibilityMode.Original;
                var (v, a) = FormatPlanner.Predict(Video, downloadMode == Enums.DownloadMode.Audio ? null : FormatPlanner.ParseCap(qualityLabel), mode);
                if (downloadMode == Enums.DownloadMode.Audio)
                {
                    var audio = a ?? Video.Formats?.Where(FormatPlanner.HasAudio).OrderByDescending(f => f.AudioBitrate ?? 0).FirstOrDefault();
                    ApplyFormats(null, audio ?? v, "Planned", downloadMode);
                    string target = AppSettings.Instance.DownloadsAudioFormat.ToString().ToUpperInvariant();
                    FormatSummary = $"→ {(target == "BEST" ? "best audio" : target)}";
                    string srcAudio = FormatPlanner.NormalizeAudioCodec((audio ?? v)?.AudioCodec);
                    ConversionChip = null;   // shown as "MP3 (Opus)" in the codec chip
                    return;
                }
                ApplyFormats(v, a, "Planned", downloadMode);
                FormatSummary = BuildOutputSummary(v, a, downloadMode);
                ConversionChip = downloadMode == Enums.DownloadMode.Gif ? "→ GIF" : null;   // conversions are shown as "H.264 (AV1)"
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Format prediction failed: " + ex.Message);
            }
        }

        /// <summary>Called with yt-dlp's "Downloading 1 format(s): 299+140" ids.</summary>
        public void SetActualFormat(string formatIds, Enums.DownloadMode downloadMode)
        {
            if (string.IsNullOrWhiteSpace(formatIds) || Video.Formats == null) return;
            var parts = formatIds.Split('+');
            var found = parts.Select(id => Video.Formats.FirstOrDefault(f => f.FormatId == id)).Where(f => f != null).ToList();
            if (!found.Any())
            {
                FormatStage = $"Downloading {formatIds}";
                return;
            }
            var v = found.FirstOrDefault(FormatPlanner.HasVideo);
            var a = found.FirstOrDefault(f => f != v && FormatPlanner.HasAudio(f));
            ApplyFormats(v, a, $"Downloading {formatIds}", downloadMode);
            if (downloadMode == Enums.DownloadMode.Video)
            {
                FormatSummary = BuildOutputSummary(v, a, downloadMode);
                ConversionChip = downloadMode == Enums.DownloadMode.Gif ? "→ GIF" : null;
            }
        }

        /// <summary>Shows the real properties of the saved file (values that differ from the original as "result (original)").</summary>
        public void SetFinalFormat(MediaProbe.Result r, string extension)
        {
            if (r == null) return;
            List<string> primary = new(), secondary = new();
            if (r.DurationSeconds is double secs && secs >= 1)
            {
                string dur = FormatDuration(TimeSpan.FromSeconds(Math.Round(secs)));
                Add(primary, CustomTimes && DurationText != null ? WithOriginal(dur, DurationText) : dur);
            }
            var best = BestAvailableFormat;
            string res = FormatPlanner.FormatResolution(r.Width, r.Height, r.FrameRate);
            Add(primary, best?.Height is int bh && r.Height is int rh && bh > rh ? WithOriginal(res, FormatPlanner.FormatResolution(best.Width, best.Height, best.FrameRate)) : res);
            Add(primary, FormatPlanner.FormatSize(r.SizeBytes));
            Add(secondary, FormatPlanner.FormatBitrate(r.BitrateKbps));
            string finalV = FormatPlanner.NormalizeVideoCodec(r.VideoCodec);
            Add(secondary, WithOriginal(finalV, lastSrcV));
            string finalAudio = FormatPlanner.NormalizeAudioCodec(r.AudioCodec);
            Add(secondary, WithOriginal(finalAudio, lastSrcA));
            string container = extension?.TrimStart('.').ToUpperInvariant();
            if (!string.Equals(container, finalAudio, StringComparison.OrdinalIgnoreCase))
                Add(secondary, WithOriginal(container, lastSrcC));
            SetChips(primary, secondary);
            FormatStage = "Saved";
            FormatSummary = null;
            ConversionChip = null;
            hasFinalFormat = true;
            OnPropertyChanged(nameof(FormatTooltip));
        }

        /// <summary>Tooltip: "Source: ..." (original format of the video).</summary>
        private string formatDetails;

        // Last inputs of ApplyFormats (to refresh the chips when the trim range changes) and the source codecs (for the saved file).
        private (FormatData v, FormatData a, string stage, Enums.DownloadMode mode)? lastApplied;
        private string lastSrcV, lastSrcA, lastSrcC;

        /// <summary>Highest video height any format of this video offers (null if unknown).</summary>
        public int? MaxAvailableHeight => BestAvailableFormat?.Height;

        private FormatData BestAvailableFormat => Video?.Formats?
            .Where(f => f != null && f.Height is > 0 && f.VideoCodec != "none")
            .OrderByDescending(f => f.Height).ThenByDescending(f => f.FrameRate ?? 0).FirstOrDefault();

        /// <summary>"result (original)" when they differ, otherwise just the result.</summary>
        private static string WithOriginal(string result, string original)
            => string.IsNullOrEmpty(original) || string.IsNullOrEmpty(result) || string.Equals(result, original, StringComparison.OrdinalIgnoreCase)
                ? result : $"{result} ({original})";

        private static string FormatDuration(TimeSpan d) => d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

        /// <summary>Duration chip: trimmed length with the full length in parentheses, e.g. "0:45 (5:40)".</summary>
        private string DurationChip
        {
            get
            {
                if (DurationText == null) return null;
                if (TimeSpanGroup == null || !CustomTimes) return DurationText;
                var trimmed = TimeSpanGroup.EndTime - TimeSpanGroup.StartTime;
                return trimmed > TimeSpan.Zero ? $"{FormatDuration(trimmed)} ({DurationText})" : DurationText;
            }
        }

        /// <summary>Size chip; when trimmed: estimated trimmed size with the full size in parentheses, e.g. "~5.4 MB (29.0 MB)".</summary>
        private string SizeChip(long? size, bool estimated)
        {
            if (size is not long b || b <= 0) return null;
            string full = (estimated ? "~" : string.Empty) + FormatPlanner.FormatSize(b);
            if (TimeSpanGroup == null || !CustomTimes || Duration.TotalSeconds < 1) return full;
            var trimmed = TimeSpanGroup.EndTime - TimeSpanGroup.StartTime;
            if (trimmed <= TimeSpan.Zero || trimmed >= Duration) return full;
            long part = (long)Math.Round(b * (trimmed.TotalSeconds / Duration.TotalSeconds));
            return $"~{FormatPlanner.FormatSize(part)} ({full})";
        }

        private void RefreshChips()
        {
            if (hasFinalFormat || lastApplied is not { } l) return;
            ApplyFormats(l.v, l.a, l.stage, l.mode);
        }

        /// <summary>
        /// Chips describe the FINAL file. A value that differs from the original is shown as "result (original)":
        /// resolution "1080p60 (2160p60)" when Max Quality limits it, codecs "H.264 (AV1)" / container "MP4 (WEBM)" when converted,
        /// duration "0:45 (5:40)" when trimmed. Tooltip: the original format ("Source: ...").
        /// </summary>
        private void ApplyFormats(FormatData v, FormatData a, string stage, Enums.DownloadMode downloadMode = Enums.DownloadMode.Video)
        {
            lastApplied = (v, a, stage, downloadMode);
            List<string> primary = new(), secondary = new();
            Add(primary, DurationChip);

            string srcV = v != null ? FormatPlanner.NormalizeVideoCodec(v.VideoCodec) : null;
            string rawAudio = FormatPlanner.HasAudio(v) ? v.AudioCodec : a?.AudioCodec;
            string srcA = FormatPlanner.NormalizeAudioCodec(rawAudio);
            string srcExt = v?.Extension ?? a?.Extension;
            if (srcExt == "unknown_video") srcExt = null;
            srcExt = srcExt?.ToUpperInvariant();
            lastSrcV = srcV; lastSrcA = srcA; lastSrcC = srcExt;

            string outV = srcV, outA = srcA, outC = srcExt;
            if (v != null && downloadMode == Enums.DownloadMode.Video)
            {
                var mode = AppSettings.Instance.VideoCompatibilityMode;
                if (mode == Enums.VideoCompatibilityMode.Original)
                {
                    if (AppSettings.Instance.AutoConvertToH264AfterDownload && srcV != null && srcV != "H.264") outV = "H.264";
                    outC = "MP4";
                }
                else
                {
                    var o = FormatPlanner.PredictOutput(v.VideoCodec, rawAudio, mode, v.Extension);
                    outV = o.video ?? srcV; outA = o.audio ?? srcA; outC = o.container;
                }
            }
            else if (downloadMode == Enums.DownloadMode.Audio)
            {
                string target = AppSettings.Instance.DownloadsAudioFormat.ToString().ToUpperInvariant();
                if (target != "BEST") { outA = target; outC = null; }
            }

            var best = BestAvailableFormat;
            string bestLabel = best != null ? FormatPlanner.FormatResolution(best.Width, best.Height, best.FrameRate) : null;
            string resolution = v != null ? FormatPlanner.FormatResolution(v.Width, v.Height, v.FrameRate) : null;
            bool capped = resolution != null && best?.Height is int mh && v.Height is int vh && mh > vh;
            Add(primary, capped ? WithOriginal(resolution, bestLabel) : resolution);

            long? size = (v?.FileSize ?? v?.ApproximateFileSize) + (a == null ? 0 : (a.FileSize ?? a.ApproximateFileSize ?? 0));
            bool reencodeVideo = v != null && srcV != null && outV != null && srcV != outV;
            Add(primary, SizeChip(size, reencodeVideo));

            if (v != null && !reencodeVideo)
                Add(secondary, FormatPlanner.FormatBitrate((v.VideoBitrate ?? v.Bitrate) + (a?.AudioBitrate ?? a?.Bitrate ?? 0)));
            if (v == null && a != null && downloadMode != Enums.DownloadMode.Audio) Add(secondary, FormatPlanner.FormatBitrate(a.AudioBitrate ?? a.Bitrate));
            if (downloadMode == Enums.DownloadMode.Video) Add(secondary, WithOriginal(outV, srcV));
            Add(secondary, WithOriginal(outA, srcA));
            if (!string.Equals(outC, outA, StringComparison.OrdinalIgnoreCase)) Add(secondary, WithOriginal(outC, srcExt));
            SetChips(primary, secondary);
            int total = primary.Count + secondary.Count;
            FormatStage = total > (DurationText == null ? 0 : 1) ? stage : null;

            // Tooltip: only the original ("Source"), plus which qualities the site offers when Max Quality had to pick a lower one.
            // When Max Quality picked a lower version, "Source" describes the best (original) version of the video.
            string tipV = capped ? FormatPlanner.NormalizeVideoCodec(best.VideoCodec) : srcV;
            string tipA = capped && FormatPlanner.HasAudio(best) ? FormatPlanner.NormalizeAudioCodec(best.AudioCodec) : srcA;
            string tipC = capped ? (best.Extension is string be && be != "unknown_video" ? be.ToUpperInvariant() : null) : srcExt;
            string codecs = tipV == null ? tipA : (tipA == null ? tipV : $"{tipV} + {tipA}");
            var parts = new[] { capped ? bestLabel : resolution, codecs, tipC, DurationText }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            string source = parts.Count == 0 ? null : "Source: " + string.Join(" · ", parts);
            if (source != null && capped)
            {
                var heights = Video.Formats.Where(f => f?.Height is > 0 && f.VideoCodec != "none").Select(f => f.Height.Value).Distinct().OrderByDescending(h => h).Select(h => h + "p");
                source += Environment.NewLine + "Available: " + string.Join(", ", heights);
            }
            formatDetails = source;
            OnPropertyChanged(nameof(FormatTooltip));
        }

        private void SetChips(List<string> primary, List<string> secondary)
        {
            PrimaryChips = primary;
            SecondaryChips = secondary;
            FormatChips = primary.Concat(secondary).ToList();
        }

        /// <summary>Accent chip when the file will be re-encoded after download, e.g. "converted from AV1" (the codec chips already show the result).</summary>
        private static string BuildConversionChip(FormatData v, FormatData a, Enums.DownloadMode downloadMode)
        {
            if (v == null) return null;
            if (downloadMode == Enums.DownloadMode.Gif) return "→ GIF";
            var mode = AppSettings.Instance.VideoCompatibilityMode;
            string srcV = FormatPlanner.NormalizeVideoCodec(v.VideoCodec);
            if (mode == Enums.VideoCompatibilityMode.Original)
                return AppSettings.Instance.AutoConvertToH264AfterDownload && srcV != null && srcV != "H.264" ? "converted from " + srcV : null;
            var (outV, _, _) = FormatPlanner.PredictOutput(v.VideoCodec, FormatPlanner.HasAudio(v) ? v.AudioCodec : a?.AudioCodec, mode, v.Extension);
            return srcV != null && outV != null && srcV != outV ? "converted from " + srcV : null;
        }

        private static string BuildOutputSummary(FormatData v, FormatData a, Enums.DownloadMode downloadMode)
        {
            if (v == null) return null;
            if (downloadMode == Enums.DownloadMode.Gif) return "→ GIF";
            var mode = AppSettings.Instance.VideoCompatibilityMode;
            string srcV = FormatPlanner.NormalizeVideoCodec(v.VideoCodec);
            string srcA = FormatPlanner.NormalizeAudioCodec(FormatPlanner.HasAudio(v) ? v.AudioCodec : a?.AudioCodec);
            if (mode == Enums.VideoCompatibilityMode.Original)
            {
                if (AppSettings.Instance.AutoConvertToH264AfterDownload && srcV != null && srcV != "H.264")
                    return $"→ MP4 · H.264 (re-encode from {srcV})";
                return "→ MP4 · original codecs";
            }
            var (outV, outA, container) = FormatPlanner.PredictOutput(v.VideoCodec, FormatPlanner.HasAudio(v) ? v.AudioCodec : a?.AudioCodec, mode, v.Extension);
            string codecs = outA == null ? outV : $"{outV} + {outA}";
            bool reencodeVideo = srcV != null && srcV != outV;
            string note = srcV == null ? " (converted only if needed)"
                : reencodeVideo ? $" (re-encode from {srcV})"
                : (srcA != null && srcA != outA && outA != null ? " (audio → AAC)" : string.Empty);
            return $"→ {container} · {codecs}{note}";
        }

        private static void Add(List<string> list, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
        }

        #endregion

        private readonly IToastNotificationService toastNotificationService = IoC.Default.GetService<IToastNotificationService>();

        public void SetProgressToDefault()
        {
            if (CancelSource.IsCancellationRequested)
                CancelSource = new();

            if (DownloadProgress != null)
                DownloadProgress.ProgressChanged -= OnProgressChanged;

            downloadLog = new();
            DownloadProgress = new();
            ProgressInfo = new();

            DownloadState = Enums.DownloadState.IsWaiting;
            DownloadProgress.ProgressChanged += OnProgressChanged;
        }

        protected void OnProgressChanged(object sender, DownloadProgress e)
        {
            downloadProgress = e;
            Match matchResult = null;
            if (!string.IsNullOrEmpty(e.Data))
			{
				matchResult = Regex.Match(e.Data, GlobalResources.FFMPEGTIMEFROMOUTPUTREGEX);
				if (!matchResult.Success)
				{
					downloadLog.AppendLine(e.Data);
				}
			}

            if (matchResult?.Success is true)
                ProgressInfo.Progress = (int)(TimeSpan.Parse(matchResult.Value.Remove(0, 5)) / (TimeSpanGroup.EndTime - TimeSpanGroup.StartTime) * 100);
            else if (e.Progress != 0)
                ProgressInfo.Progress = (int)(downloadProgress.Progress * 100);

            ProgressInfo.TotalSize = downloadProgress.TotalDownloadSize;
            ProgressInfo.DownloadState = downloadProgress.State;
            //Debug.WriteLine((byte)(downloadProgress.Progress * 100) + " - " + ProgressInfo.Progress);

            OnPropertyChanged(nameof(ProgressInfo));
            OnPropertyChanged(nameof(Downloading));
            OnPropertyChanged(nameof(Converting));
            ProgressChangedEventHandler?.Invoke(this, new());

            if (!isUpdating && DownloadState is Enums.DownloadState.IsLoading)
                _ = UpdateProgressInfoAsync();
        }

        private bool isUpdating;
        private DownloadProgress downloadProgress;
        private async Task UpdateProgressInfoAsync()
        {
            Debug.WriteLine("Starting update task...");
            isUpdating = true;
            while (DownloadState is Enums.DownloadState.IsLoading)
            {
                ProgressInfo.DownloadSpeed = downloadProgress.DownloadSpeed;
                OnPropertyChanged(nameof(ProgressInfo));
                await Task.Delay(1000);
            }
            isUpdating = false;
            Debug.WriteLine("Finish update task");
        }

		/// <summary>
		/// Contains informations of the video
		/// </summary>
		public VideoData Video { get; }
        public TimeSpan Duration { get; }
        public string? UploadDate => Video.UploadDate?.ToShortDateString();
        public TimeSpanGroup TimeSpanGroup { get; }
        public bool CustomTimes => TimeSpanGroup != null && !TimeSpanGroup.StartTime.Equals(TimeSpan.Zero) || !TimeSpanGroup.EndTime.Equals(TimeSpanGroup.Duration);

        /// <summary>
        /// The downloading progress
        /// </summary>
        public Progress<DownloadProgress> DownloadProgress { get; set; }

        /// <summary>
        /// If set, override the filetags on the download
        /// </summary>
        public FileTags? CustomTags { get; set; }

        /// <summary>
        /// The conversion progress after download in %
        /// </summary>
        [ObservableProperty]
        private int conversionProgress;

        /// <summary>
        /// Contains informations about the downloading progress
        /// </summary>
        public ProgressModel ProgressInfo { get; set; }

        /// <summary>
        /// The path to the downloaded video
        /// </summary>
        public Uri Path { get; set; }

        /// <summary>
        /// The CancellationTokenSource to cancel the download
        /// </summary>
        public CancellationTokenSource CancelSource { get; protected set; }

        /// <summary>
        /// Contains information about the downloading progress. Useful to identify errors.
        /// </summary>
        public string DownloadLog => downloadLog.ToString();

        internal StringBuilder downloadLog;

        /// <summary>
        /// Indicates whether the video is being downloaded
        /// </summary>
        [ObservableProperty]
        private bool downloading;

        [ObservableProperty]
        private bool converting;

        [ObservableProperty]
        private bool moving;

        public bool Success => DownloadState is Enums.DownloadState.IsDone;
        public bool Failed => DownloadState is Enums.DownloadState.IsFailed;

        /// <summary>
        /// Shows the downloadstate
        /// </summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(ProgressInfo))]
        [AlsoNotifyChangeFor(nameof(Downloading))]
        [AlsoNotifyChangeFor(nameof(Success))]
        [AlsoNotifyChangeFor(nameof(Failed))]
        private Enums.DownloadState downloadState;


        /// <summary>
        /// The dialog that matches the download status
        /// </summary>
        [ObservableProperty]
        private string progressDialog;


        /// <summary>
        /// Displays a notification when the download has completed
        /// </summary>
        public void ShowToast()
        {
            toastNotificationService.SendDownloadDoneNotification(Video, Path.OriginalString);
        }

        /// <summary>
        /// The quality for the video to download
        /// </summary>
        public string QualityLabel { get; set; }
        /// <summary>
        /// The height for the video to download
        /// </summary>
        public int GetVideoHeight => FormatPlanner.ParseCap(QualityLabel) ?? int.MaxValue;
		/// <summary>
		/// The format for the video to download
		/// </summary>
        public string Format => $"bestvideo[height<={GetVideoHeight}]+bestaudio[ext=m4a]/best[height<={GetVideoHeight}]/best";

        /// <summary>
        /// Contains foreach video format a quality label that represents the resolution
        /// TKey = Format object
        /// TValue = Rounded height (e.g. 360)
        /// </summary>
        public Dictionary<FormatData, int> FormatQualityLabels { get; } = new();

        /// <summary>
        /// Occurs when the download progress get changed
        /// </summary>
        public event EventHandler ProgressChangedEventHandler;
        /// <summary>
        /// Occurs when the download has finished
        /// </summary>
        public event EventHandler FinishedEventHandler;
        /// <summary>
        /// Occurs when the download has cancelled
        /// </summary>
        public event EventHandler<CancellationEventArgs> CancelEventHandler;

        /// <summary>
        /// Raises the CancelEvent
        /// </summary>
        /// <param name="restart">Restart the download</param>
        public void RaiseCancel(bool restart = false)
        {
            if (!restart)
                ProgressInfo.IsCancelledOrFailed = true;
            CancelEventHandler?.Invoke(this, new CancellationEventArgs(CancelSource, restart));
        }

        /// <summary>
        /// Raise the FinishedEvent
        /// </summary>
        public void RaiseFinished()
            => FinishedEventHandler?.Invoke(this, new EventArgs());

        static readonly int[] validResolutions = { 144, 240, 360, 480, 720, 1080, 1440, 2160, 4320, 8640 };
    }
}