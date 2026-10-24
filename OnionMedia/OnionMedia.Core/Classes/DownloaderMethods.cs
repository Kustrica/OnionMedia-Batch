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
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using FFmpeg.NET;
using FFmpeg.NET.Enums;
using OnionMedia.Core.Extensions;
using OnionMedia.Core.Models;
using System.Security;
using System.Net.Http;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using YoutubeDLSharp.Metadata;
using Newtonsoft.Json;
using System.Text;
using System.Drawing;
using FFMpegCore;
using OnionMedia.Core.Services;
using YoutubeExplode.Videos;
using OnionMedia.Core.ViewModels.Dialogs;
using OnionMedia.Core.Enums;

namespace OnionMedia.Core.Classes
{
	public static class DownloaderMethods
	{
		private static readonly IDispatcherService dispatcher = IoC.Default.GetService<IDispatcherService>() ?? throw new ArgumentNullException();
		private static readonly IPathProvider pathProvider = IoC.Default.GetService<IPathProvider>() ?? throw new ArgumentNullException();

		public static readonly YoutubeClient youtube = new();

		public static readonly YoutubeDL downloadClient = new(10)
		{
			FFmpegPath = pathProvider.FFmpegPath,
			YoutubeDLPath = pathProvider.YtDlPath,
			OutputFolder = pathProvider.DownloaderTempdir,
			OverwriteFiles = true
		};

		private static string GetHardwareEncodingParameters(FormatData formatData) => AppSettings.Instance.HardwareEncoder switch
		{
			HardwareEncoder.Nvidia_NVENC => $"-vcodec h264_nvenc {(formatData.VideoBitrate > 0 ? $"-b:v {formatData.VideoBitrate.ToString().Replace(',', '.') + 'k'}" : "-profile:v high -q:v 20 -preset:v slower")}",
			HardwareEncoder.Intel_QSV => $"-vcodec h264_qsv {(formatData.VideoBitrate > 0 ? $"-b:v {formatData.VideoBitrate.ToString().Replace(',', '.') + 'k'}" : "-profile:v high -q:v 20 -preset:v slower")}",
			HardwareEncoder.AMD_AMF => $"-vcodec h264_amf {(formatData.VideoBitrate > 0 ? $"-b:v {formatData.VideoBitrate.ToString().Replace(',', '.') + 'k'}" : "-profile:v high -q:v 20 -preset:v slower")}",
			_ => string.Empty
		};

		public static async Task DownloadStreamAsync(StreamItemModel stream, DownloadMode downloadMode, string customOutputDirectory = null)
		{
			if (stream == null)
				throw new ArgumentNullException(nameof(stream));

			OptionSet ytOptions = new() { RestrictFilenames = true };
			// Web-page items (fap-nation & co.) download the chosen file / embedded player with the page as Referer.
			string sourceUrl = GetDownloadSourceUrl(stream, downloadMode, out string referer);
			IDisposable cookieCleanup = YtDlpOptions.Apply(ytOptions, sourceUrl);
			if (!string.IsNullOrEmpty(referer))
				ytOptions.AddCustomOption("--referer", referer);

			//Creates a temp directory if it does not already exist.
			Directory.CreateDirectory(pathProvider.DownloaderTempdir);

			//Creates a new temp directory for this file
			string videotempdir = CreateVideoTempDir();

			SetProgressToDefault(stream);
			stream.CancelEventHandler += CancelDownload;

			var cToken = stream.CancelSource.Token;
			bool autoConvertToH264 = AppSettings.Instance.AutoConvertToH264AfterDownload;

			string tempfile = string.Empty;
			bool isShortened = false;
			Enums.ItemType itemType = Enums.ItemType.video;

			try
			{
				Debug.WriteLine($"Current State of Progress: {stream.ProgressInfo.Progress}");
				ytOptions.Output = Path.Combine(videotempdir, "%(id)s.%(ext)s");

				if (stream.CustomTimes)
				{
					ytOptions.AddCustomOption("--download-sections", $"*{stream.TimeSpanGroup.StartTime.WithoutMilliseconds()}.00-{stream.TimeSpanGroup.EndTime:hh\\:mm\\:ss}.00");
					ytOptions.AddCustomOption("--force-keyframes-at-cuts --compat", "no-direct-merge");
					ytOptions.ExternalDownloaderArgs = "ffmpeg:"; // no "-loglevel 0" here: the trim progress is parsed from ffmpeg output
					isShortened = true;
				}

				//Set download speed limit from AppSettings
				if (AppSettings.Instance.LimitDownloadSpeed && AppSettings.Instance.MaxDownloadSpeed > 0)
					ytOptions.LimitRate = (long)(AppSettings.Instance.MaxDownloadSpeed * 1000000 / 8);

				//Audiodownload
				if (downloadMode == DownloadMode.Audio)
				{
					tempfile = await RunAudioDownloadAndConversionAsync(stream, ytOptions, sourceUrl, isShortened, TimeSpan.Zero, cToken);
					itemType = Enums.ItemType.audio;
				}
				//Videodownload or GIF
				else
				{
					tempfile = await RunVideoDownloadAndConversionAsync(stream, ytOptions, sourceUrl, isShortened, autoConvertToH264, downloadMode == DownloadMode.Gif, TimeSpan.Zero, cToken);
					itemType = Enums.ItemType.video;

                    if (downloadMode == DownloadMode.Gif && Path.GetExtension(tempfile).ToLower() != ".gif")
                    {
                        stream.Converting = true;
                        tempfile = await ConvertToGifAsync(tempfile, stream, cToken);
                    }
				}
			}
			catch (Exception ex)
			{
				cookieCleanup.Dispose();
				stream.Downloading = false;
				stream.Converting = false;
				Console.WriteLine(ex.Message);
				Debug.WriteLine(ex.Message);
				switch (ex)
				{
					default:
						Debug.WriteLine(ex.Message);
						stream.DownloadState = Enums.DownloadState.IsFailed;
						stream.ProgressInfo.IsCancelledOrFailed = true;
						return;

					case TaskCanceledException:
						Debug.WriteLine("The download has cancelled.");
						stream.DownloadState = Enums.DownloadState.IsCancelled;
						stream.ProgressInfo.IsCancelledOrFailed = true;
						return;

					case HttpRequestException:
						Debug.WriteLine("No internet connection.");
						stream.DownloadState = Enums.DownloadState.IsFailed;
						stream.ProgressInfo.IsCancelledOrFailed = true;
						return;

					case SecurityException:
						Debug.WriteLine("No access to the temp-path.");
						stream.DownloadState = Enums.DownloadState.IsFailed;
						stream.ProgressInfo.IsCancelledOrFailed = true;
						return;

					case IOException:
						Debug.WriteLine("Failed to save video to the temp-path.");
						stream.DownloadState = Enums.DownloadState.IsFailed;
						stream.ProgressInfo.IsCancelledOrFailed = true;
						NotEnoughSpaceException.ThrowIfNotEnoughSpace((IOException)ex);
						throw;
				}
			}

			stream.Converting = false;
			Debug.WriteLine($"Current State of Progress: {stream.ProgressInfo.Progress}");

			try
			{
				//Writes the tags into the file
				//TODO: Fix file corruptions while saving tags
				if (Path.GetExtension(tempfile) is ".mp3" or ".mp4" or ".m4a" or ".flac")
				{
					if (stream.CustomTags is null)
					{
						SaveTags(tempfile, stream.Video);
					}
					else
					{
						SaveTags(tempfile, stream.CustomTags);
					}
				}

				// Show the real format of the saved file in the queue.
				try
				{
					var finalInfo = await MediaProbe.ProbeAsync(tempfile, ct: cToken);
					string ext = Path.GetExtension(tempfile);
					if (finalInfo != null)
						await dispatcher.EnqueueAsync(() => stream.SetFinalFormat(finalInfo, ext));
				}
				catch (Exception probeEx) { Debug.WriteLine("Final probe failed: " + probeEx.Message); }

				//Moves the video in the correct directory
				stream.Moving = true;
				stream.Path = await MoveToDisk(tempfile, stream.Video.Title, itemType, customOutputDirectory, cToken);

				stream.DownloadState = Enums.DownloadState.IsDone;
				stream.RaiseFinished();
			}
			catch (Exception ex)
			{
				stream.DownloadState = Enums.DownloadState.IsFailed;
				stream.ProgressInfo.IsCancelledOrFailed = true;
				Console.WriteLine(ex.Message);
				Debug.WriteLine(ex.Message);
				switch (ex)
				{
					default:
						Debug.WriteLine(ex.Message);
						break;

					case OperationCanceledException:
						Debug.WriteLine("Moving operation get canceled.");
						stream.DownloadState = Enums.DownloadState.IsCancelled;
						break;

					case FileNotFoundException:
						Debug.WriteLine("File not found!");
						break;

					case UnauthorizedAccessException:
						Debug.WriteLine("No permissions to access the file.");
						throw;

					case PathTooLongException:
						Debug.WriteLine("Path is too long");
						throw;

					case DirectoryNotFoundException:
						Debug.WriteLine("Directory not found!");
						throw;

					case IOException:
						Debug.WriteLine("IOException occured.");
						NotEnoughSpaceException.ThrowIfNotEnoughSpace((IOException)ex);
						throw;
				}
			}
			finally 
			{ 
				stream.Moving = false; 
				cookieCleanup.Dispose(); 
			}
		}

		/// <summary>
		/// Saves the thumbnail of a video as a real PNG/JPEG file (YouTube usually serves WebP, so it is always re-encoded with ffmpeg).
		/// Playlist/radio parameters (&amp;list=RD...) are removed first; if yt-dlp cannot get a thumbnail, the known thumbnail URL is used.
		/// Throws an <see cref="IOException"/> with a readable message if no valid image could be created.
		/// </summary>
		public static async Task DownloadThumbnailAsync(string videoUrl, string filePath, string imageFormat, CancellationToken ct = default, string directThumbnailUrl = null, string referer = null, string fallbackThumbnailUrl = null)
		{
			imageFormat = string.Equals(imageFormat, "png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
			string dir = Path.Combine(Path.GetTempPath(), "om_thumb_" + Path.GetRandomFileName());
			Directory.CreateDirectory(dir);
			try
			{
				string source = null;
				if (string.IsNullOrEmpty(directThumbnailUrl) && !string.IsNullOrWhiteSpace(videoUrl))
				{
					string url = YtDlpOptions.StripPlaylistParameters(videoUrl);
					OptionSet options = new() { WriteThumbnail = true, SkipDownload = true, NoPlaylist = true, Output = Path.Combine(dir, "thumb.%(ext)s") };
					using (YtDlpOptions.Apply(options, url, applyCustomArgs: false))
						await downloadClient.RunWithOptions(new[] { url }, options, ct);
					source = FindImageFile(dir);
				}

				string imageUrl = directThumbnailUrl ?? fallbackThumbnailUrl;
				if (source == null && !string.IsNullOrWhiteSpace(imageUrl))
					source = await FetchImageAsync(imageUrl, referer, dir, ct);
				if (source == null)
					throw new IOException("No thumbnail could be downloaded for this video.");

				string converted = Path.Combine(dir, "converted." + imageFormat);
				string quality = imageFormat == "jpg" ? "-q:v 2" : string.Empty;
				await new Engine(pathProvider.FFmpegPath).ExecuteAsync($"-y -i \"{source}\" -frames:v 1 {quality} \"{converted}\"", ct);
				if (!File.Exists(converted) || !IsImageFile(converted, imageFormat))
					throw new IOException($"The thumbnail could not be converted to {imageFormat.ToUpperInvariant()}.");

				string targetDir = Path.GetDirectoryName(filePath);
				if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
				File.Copy(converted, filePath, true);
			}
			finally
			{
				try { Directory.Delete(dir, true); } catch { }
			}
		}

		private static readonly string[] imageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp", ".image" };

		private static string FindImageFile(string dir)
			=> Directory.Exists(dir)
				? new DirectoryInfo(dir).GetFiles().Where(f => imageExtensions.Contains(f.Extension.ToLowerInvariant()) && f.Length > 0)
					.OrderByDescending(f => f.Length).Select(f => f.FullName).FirstOrDefault()
				: null;

		private static async Task<string> FetchImageAsync(string imageUrl, string referer, string dir, CancellationToken ct)
		{
			try
			{
				using var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
				using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
				http.DefaultRequestHeaders.UserAgent.ParseAdd(MediaProbe.BrowserUserAgent);
				if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r)) http.DefaultRequestHeaders.Referrer = r;
				byte[] bytes = await http.GetByteArrayAsync(imageUrl, ct);
				if (bytes.Length == 0) return null;
				string path = Path.Combine(dir, "direct.image");
				await File.WriteAllBytesAsync(path, bytes, ct);
				return path;
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				Debug.WriteLine("Thumbnail download failed: " + ex.Message);
				return null;
			}
		}

		/// <summary>Checks the file signature (PNG: 89 50 4E 47, JPEG: FF D8 FF).</summary>
		public static bool IsImageFile(string path, string format)
		{
			try
			{
				byte[] head = new byte[4];
				using (var fs = File.OpenRead(path))
					if (fs.Read(head, 0, 4) < 4) return false;
				return format == "png"
					? head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
					: head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
			}
			catch { return false; }
		}

		/// <summary>
		/// Returns the URL yt-dlp should download for this item:
		/// the normal URL, the embedded player of a web page, or the best direct file variant below the quality ceiling.
		/// </summary>
		public static string GetDownloadSourceUrl(StreamItemModel stream, DownloadMode downloadMode, out string referer)
		{
			referer = null;
			var info = WebPageVideoResolver.GetSourceInfo(stream.Video);
			if (info == null)
				return stream.Video.Url;

			referer = info.PageUrl;
			if (!string.IsNullOrEmpty(info.EmbedUrl))
				return info.EmbedUrl;

			FormatData chosen;
			if (downloadMode == DownloadMode.Audio)
			{
				// Audio only: the variants usually share the same audio track -> take the best known audio in the smallest file.
				chosen = stream.Video.Formats?.Where(f => !string.IsNullOrEmpty(f.Url))
					.OrderBy(f => FormatPlanner.NormalizeAudioCodec(f.AudioCodec) == null ? 1 : 0)
					.ThenByDescending(f => Math.Round((f.AudioBitrate ?? 0) / 32))
					.ThenBy(f => f.Height ?? int.MaxValue)
					.FirstOrDefault();
			}
			else
				chosen = FormatPlanner.PickWebFormat(stream.Video, FormatPlanner.ParseCap(stream.QualityLabel));
			if (chosen != null)
			{
				stream.downloadLog?.AppendLine($"[OnionMedia] Web page item: downloading {chosen.FormatNote ?? chosen.FormatId} ({chosen.Url})");
				dispatcher.EnqueueAsync(() => stream.SetActualFormat(chosen.FormatId, downloadMode));
				return chosen.Url;
			}
			return stream.Video.Url;
		}

		#region Video info fetch (anti-bot aware)

		/// <summary>
		/// Fetches video information with cookies, custom args and (when needed) browser impersonation.
		/// Falls back to scanning the web page for players/download links when yt-dlp finds nothing usable.
		/// </summary>
		/// <summary>Optional timing trace of <see cref="FetchVideoDataAsync"/> stages (used by tests / diagnostics).</summary>
		public static Action<string> FetchTrace;

		public static async Task<RunResult<VideoData>> FetchVideoDataAsync(string url, CancellationToken ct = default)
		{
			url = url?.Trim();
			var sw = Stopwatch.StartNew();
			void Trace(string stage) { FetchTrace?.Invoke($"{stage} +{sw.ElapsedMilliseconds} ms ({url})"); }
			RunResult<VideoData> result = await FetchOnceAsync(url, null, ct);
			Trace("yt-dlp " + (result.Success ? "ok" : "failed"));
			bool isYoutube = YtDlpOptions.IsYoutubeUrl(url);

			if ((!result.Success || result.Data == null) && !isYoutube)
			{
				bool antiBot = YtDlpOptions.LooksLikeAntiBotBlock(result.ErrorOutput);
				if (antiBot || YtDlpOptions.LooksLikeNetworkError(result.ErrorOutput))
				{
					bool impersonatedBefore = YtDlpOptions.NeedsImpersonation(url);
					if (!impersonatedBefore || !antiBot)
					{
						var retry = await FetchOnceAsync(url, !impersonatedBefore, ct);
						Trace("yt-dlp retry (impersonation=" + !impersonatedBefore + ") " + (retry.Success ? "ok" : "failed"));
						if (retry.Success && retry.Data != null)
						{
							if (!impersonatedBefore) YtDlpOptions.RememberImpersonation(url);
							result = retry;
						}
						else
						{
							if (antiBot) YtDlpOptions.RememberImpersonation(url);
							result = new RunResult<VideoData>(false, (result.ErrorOutput ?? Array.Empty<string>()).Concat(retry.ErrorOutput ?? Array.Empty<string>()).ToArray(), null);
						}
					}
				}
			}

			if (WebPageVideoResolver.ShouldTryResolve(url, result))
			{
				ct.ThrowIfCancellationRequested();
				var resolved = await WebPageVideoResolver.TryResolveAsync(url, result, ct);
				Trace("page resolver " + (resolved?.Success == true ? "ok" : "nothing"));
				if (resolved?.Success == true && resolved.Data != null)
					return resolved;
				// A "generic" playlist of loose files (ads + video) is not usable as it is.
				if (result.Data == null || result.Data.ResultType != MetadataType.Video)
					return resolved ?? result;
			}

			await EnrichDirectFileAsync(result, url, ct);
			Trace("done");
			return result;
		}

		private static async Task<RunResult<VideoData>> FetchOnceAsync(string url, bool? impersonate, CancellationToken ct)
		{
			OptionSet opts = new();
			using var cleanup = YtDlpOptions.Apply(opts, url, impersonate);
			return await downloadClient.RunVideoDataFetch(url, ct, overrideOptions: opts);
		}

		/// <summary>
		/// Direct media links and generic/KVS tube sites come without duration/width/fps/codec info – ask ffprobe
		/// (with the page as Referer) so the queue can show it and the compatibility planner knows the codecs.
		/// Never throws; anything still unknown simply stays null.
		/// </summary>
		private static async Task EnrichDirectFileAsync(RunResult<VideoData> result, string url, CancellationToken ct)
		{
			try
			{
				var data = result?.Data;
				if (data?.Formats == null || data.Formats.Length == 0 || YtDlpOptions.IsYoutubeUrl(url)) return;
				bool incomplete(FormatData f) => f != null && !string.IsNullOrEmpty(f.Url) && (f.Height == null || f.Width == null || f.FrameRate == null || string.IsNullOrEmpty(f.VideoCodec) || f.VideoCodec == "none" && f.Height != null);
				var toProbe = data.Formats.Where(incomplete).Where(f => f.Protocol == null || f.Protocol.StartsWith("http") && !f.Protocol.Contains("dash"))
					.OrderByDescending(f => f.Height ?? 0).Take(4).ToList();
				if (toProbe.Count == 0 && data.Duration is > 0) return;
				if (toProbe.Count == 0)
					toProbe = data.Formats.Where(f => f != null && !string.IsNullOrEmpty(f.Url) && (f.Protocol == null || f.Protocol.StartsWith("http"))).OrderByDescending(f => f.Height ?? 0).Take(1).ToList();
				if (toProbe.Count == 0) return;

				string referer = data.WebpageUrl ?? url;
				var probes = await Task.WhenAll(toProbe.Select(async f =>
				{
					// Short timeout: the probe only adds info (resolution/codecs); the video is usable without it.
					try { return (f, p: await MediaProbe.ProbeAsync(f.Url, referer, TimeSpan.FromSeconds(10), ct)); }
					catch (OperationCanceledException) { throw; }
					catch (Exception ex) { Debug.WriteLine("Probe failed: " + ex.Message); return (f, p: (MediaProbe.Result)null); }
				}));

				foreach (var (f, probe) in probes)
				{
					if (probe == null) continue;
					if (probe.HasVideo)
					{
						f.Width ??= probe.Width;
						f.Height ??= probe.Height;
						f.FrameRate ??= probe.FrameRate;
						if (string.IsNullOrEmpty(f.VideoCodec) || f.VideoCodec == "none") f.VideoCodec = probe.VideoCodec;
						f.VideoBitrate ??= probe.VideoBitrateKbps;
					}
					if (string.IsNullOrEmpty(f.AudioCodec)) f.AudioCodec = probe.AudioCodec ?? "none";
					f.Bitrate ??= probe.BitrateKbps;
					f.AudioBitrate ??= probe.AudioBitrateKbps;
					f.FileSize ??= probe.SizeBytes;
					f.HDR ??= "SDR";
					if (data.Duration is null or <= 0 && probe.DurationSeconds is > 0)
						data.Duration = (float?)probe.DurationSeconds;
				}

				// Same source in other qualities: assume the same aspect ratio / fps when they could not be probed.
				var reference = probes.Select(x => x.f).FirstOrDefault(f => f.Width is > 0 && f.Height is > 0);
				foreach (var f in data.Formats.Where(f => f?.Height is > 0 && f.Width == null))
				{
					f.Width = reference != null
						? (int)Math.Round(f.Height.Value * (double)reference.Width.Value / reference.Height.Value / 2) * 2
						: (int)Math.Round(f.Height.Value * 16 / 9.0 / 2) * 2;
					f.FrameRate ??= reference?.FrameRate;
				}
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { Debug.WriteLine("Direct file probe failed: " + ex.Message); }
		}

		#endregion

		private static void SetProgressToDefault(StreamItemModel stream)
		{
			stream.ProgressInfo.IsDone = false;
			stream.Converting = false;
			stream.DownloadState = Enums.DownloadState.IsLoading;
			stream.ConversionProgress = 0;
		}

		private static string CreateVideoTempDir()
		{
			string videotempdir;
			do videotempdir = Path.Combine(pathProvider.DownloaderTempdir, Path.GetRandomFileName());
			while (Directory.Exists(videotempdir));
			Directory.CreateDirectory(videotempdir);
			return videotempdir;
		}

		private static async Task<string> DownloadAudioAsync(StreamItemModel stream, OptionSet ytOptions, string sourceUrl, CancellationToken cToken = default)
		{
			string userSort = YtDlpCustomArgs.GetUserFormatSort();
			if (AppSettings.Instance.DownloadsAudioFormat != AudioConversionFormat.Opus)
				ytOptions.AddCustomOption("-S", string.IsNullOrEmpty(userSort) ? "ext" : userSort + ",ext");
			else if (!string.IsNullOrEmpty(userSort))
				ytOptions.AddCustomOption("-S", userSort);

			int downloadCounter = 0;
			bool impersonationToggled = false;
			do
			{
				downloadCounter++;
				if (downloadCounter > 1)
					stream.SetProgressToDefault();

				bool gotArguments = false;
				stream.Downloading = true;
				RunResult<string> result = await downloadClient.RunAudioDownload(sourceUrl, AudioConversionFormat.Best, cToken, stream.DownloadProgress, overrideOptions: ytOptions, output: new Progress<string>(p =>
				{
					Debug.WriteLine(p);
					if (!gotArguments)
					{
						stream.downloadLog.Insert(0, p + '\n');
						gotArguments = true;
					}
				}));
				stream.Downloading = false;

				//Return if the download completed sucessfully.
				if (!result.Data.IsNullOrEmpty())
					return result.Data;

				AppendErrors(stream, result.ErrorOutput);
				cToken.ThrowIfCancellationRequested();
				TryToggleImpersonation(ytOptions, sourceUrl, result.ErrorOutput, ref impersonationToggled);

				if (AppSettings.Instance.AutoRetryDownload)
					Debug.WriteLine("Download failed. Try: " + downloadCounter);
			}
			while ((AppSettings.Instance.AutoRetryDownload && downloadCounter <= AppSettings.Instance.CountOfDownloadRetries)
				   || (impersonationToggled && downloadCounter < 2));
			throw new Exception("Download failed!");
		}

		private static async Task<string> RunAudioDownloadAndConversionAsync(StreamItemModel stream, OptionSet ytOptions, string sourceUrl, bool isShortened = false, TimeSpan overhead = default, CancellationToken cToken = default)
		{
			//Download
			string tempfile = await DownloadAudioAsync(stream, ytOptions, sourceUrl, cToken);

			string format = AppSettings.Instance.DownloadsAudioFormat.ToString().ToLower();
			if (AppSettings.Instance.DownloadsAudioFormat is AudioConversionFormat.Vorbis)
				format = "ogg";

			if (!tempfile.EndsWith(format))
			{
				stream.Converting = true;
				tempfile = await ConvertAudioAsync(tempfile, format, overhead, stream, cToken);
			}

			Debug.WriteLine(tempfile);
			stream.ProgressInfo.Progress = 100;
			return tempfile;
		}

		private static async Task<string> RunVideoDownloadAndConversionAsync(StreamItemModel stream, OptionSet ytOptions, string sourceUrl, bool isShortened = false, bool autoConvertToH264 = false, bool forGif = false, TimeSpan overhead = default, CancellationToken cToken = default)
		{
			// Compatibility mode (Universal = H.264 + AAC in MP4, Compact = HEVC/hvc1 + AAC, Original = old behaviour).
			VideoCompatibilityMode mode = forGif ? VideoCompatibilityMode.Original : AppSettings.Instance.VideoCompatibilityMode;
			bool compat = mode != VideoCompatibilityMode.Original;
			bool isWebItem = WebPageVideoResolver.IsWebPageItem(stream.Video);
			bool isDirectWebFile = isWebItem && WebPageVideoResolver.GetSourceInfo(stream.Video)?.EmbedUrl == null;
			int? cap = stream.QualityLabel.IsNullOrEmpty() ? null : FormatPlanner.ParseCap(stream.QualityLabel);

			DownloadMergeFormat videoMergeFormat = (compat || autoConvertToH264 || isShortened) ? DownloadMergeFormat.Unspecified : DownloadMergeFormat.Mp4;
			// Original = no re-encoding: a "--recode-video mp4" would re-encode WebM/VP9 sources (slow); remuxing into MP4 is enough.
			VideoRecodeFormat videoRecodeFormat = VideoRecodeFormat.None;
			if (!(compat || autoConvertToH264 || isShortened))
				ytOptions.AddCustomOption("--remux-video", "mp4");
			ytOptions.EmbedThumbnail = true;
			// One merged -S: the user's sort fields first, then SDR, the quality ceiling ("res:<cap>" = closest to the cap) and codec preference.
			ytOptions.AddCustomOption("--format-sort", FormatPlanner.BuildFormatSort(cap, mode, YtDlpCustomArgs.GetUserFormatSort()));
			ytOptions.AddCustomOption("--no-check-formats", true);

			Size originalSize = default;
			FormatData formatData = null;

			//Don't use DASH/M3U8 in trimmed videos
			if (stream.CustomTimes)
			{
				if (!isDirectWebFile)
					formatData = GetBestTrimmableFormat(stream);
			}
			else
			{
				//TODO: Check if that works on weaker PCs with less threads.
				//Download multiple fragments at the same time to increase speed.
				ytOptions.AddCustomOption("-N", 15);
			}

			string? formatString;
			if (isDirectWebFile)
				formatString = "b/bv*+ba"; // the chosen variant URL is a single file; the ceiling was applied when picking it
			else if (formatData?.FormatId != null)
				formatString = $"{formatData.FormatId}{((formatData.AudioCodec.IsNullOrWhiteSpace() || formatData.AudioCodec == "none") && formatData.AudioBitrate is null or 0 && stream.Video.Formats.Any(f => f.VideoBitrate is null or 0 && f.AudioBitrate > 0) ? "+bestaudio[ext=m4a]/bestaudio" : string.Empty)}";
			else
				formatString = FormatPlanner.BuildFormatSelector(cap);

			int downloadCounter = 0;
			bool impersonationToggled = false;
			RunResult<string> result;
			do
			{
				downloadCounter++;
				if (downloadCounter > 1)
					stream.SetProgressToDefault();

				//Try to encode MP4 hardware-accelerated in the first try (legacy "Original" mode only)
				if (downloadCounter == 1 && !compat && formatData?.Extension == "mp4"
										 && AppSettings.Instance.AutoConvertToH264AfterDownload
										 && AppSettings.Instance.UseHardwareAcceleratedEncoding)
				{
					ytOptions.ExternalDownloaderArgs = "ffmpeg:" + GetHardwareEncodingParameters(formatData);
				}

				bool gotArguments = false;
				stream.Downloading = true;
				result = await downloadClient.RunVideoDownload(sourceUrl, formatString, videoMergeFormat, videoRecodeFormat, cToken, stream.DownloadProgress, overrideOptions: ytOptions, output: new Progress<string>(p =>
				{
					Debug.WriteLine(p);
					if (!gotArguments)
					{ 
						stream.downloadLog.Insert(0, p + '\n');
						gotArguments = true;
					}
					var fm = formatIdRegex.Match(p ?? string.Empty);
					if (fm.Success)
						stream.SetActualFormat(fm.Groups[1].Value, forGif ? DownloadMode.Gif : DownloadMode.Video);
				}));
				stream.Downloading = false;

				if (!result.Data.IsNullOrEmpty())
					break;

				AppendErrors(stream, result.ErrorOutput);
				cToken.ThrowIfCancellationRequested();

				if (AppSettings.Instance.FallBackToSoftwareEncoding)
					ytOptions.ExternalDownloaderArgs = "ffmpeg:-nostats -loglevel 0";

				// Anti-bot / flaky network: switch TLS impersonation for the next attempt (one extra attempt even if auto-retry is off).
				if (TryToggleImpersonation(ytOptions, sourceUrl, result.ErrorOutput, ref impersonationToggled))
					Debug.WriteLine("Retrying with toggled impersonation.");

				if (AppSettings.Instance.AutoRetryDownload)
					Debug.WriteLine("Download failed. Try: " + downloadCounter);
			}
			while ((AppSettings.Instance.AutoRetryDownload && downloadCounter <= AppSettings.Instance.CountOfDownloadRetries)
				   || (impersonationToggled && downloadCounter < 2));
			if (result.Data.IsNullOrEmpty())
				throw new Exception("Download failed!");


			string tempfile = result.Data;
			Debug.WriteLine(tempfile);
			stream.ProgressInfo.Progress = 100;

			if (compat)
			{
				stream.Converting = true;
				return await EnsureCompatibleAsync(tempfile, mode, stream, cToken);
			}

			//TODO Setting: Allow to recode ALWAYS to H264, even if it's already H264.
			var meta = await FFProbe.AnalyseAsync(tempfile);
			if ((autoConvertToH264 && meta.PrimaryVideoStream?.CodecName != "h264") || Path.GetExtension(tempfile) != ".mp4")
			{
				stream.Converting = true;
				tempfile = await ConvertToMp4Async(tempfile, overhead, default, stream, originalSize, meta, cToken);
			}
			return tempfile;
		}

		private static readonly System.Text.RegularExpressions.Regex formatIdRegex = new(@"Downloading \d+ format\(s\): (\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);

		private static void AppendErrors(StreamItemModel stream, IEnumerable<string> errors)
		{
			if (errors == null) return;
			foreach (var e in errors.Where(e => !string.IsNullOrWhiteSpace(e)))
				stream.downloadLog.AppendLine(e);
		}

		/// <summary>Removes/adds OnionMedia's own --impersonate option. Returns true if something was changed.</summary>
		private static bool TryToggleImpersonation(OptionSet options, string url, IEnumerable<string> errors, ref bool alreadyToggled)
		{
			if (YtDlpOptions.IsYoutubeUrl(url)) return false;
			bool antiBot = YtDlpOptions.LooksLikeAntiBotBlock(errors);
			if (!antiBot && !YtDlpOptions.LooksLikeNetworkError(errors)) return false;
			if (YtDlpCustomArgs.ActiveTokens.Any(t => t.StartsWith("--impersonate", StringComparison.Ordinal))) return false;

			bool hasImpersonation = options.CustomOptions.Any(o => o.OptionStrings.Contains("--impersonate"));
			if (hasImpersonation)
			{
				// Only drop impersonation after an SSL/network error (some CDNs dislike curl_cffi); never after a block.
				if (antiBot) return false;
				options.CustomOptions = options.CustomOptions.Where(o => !o.OptionStrings.Contains("--impersonate")).ToArray();
			}
			else
			{
				options.AddCustomOption("--impersonate", YtDlpOptions.ImpersonateTarget);
				if (antiBot) YtDlpOptions.RememberImpersonation(url);
			}
			alreadyToggled = true;
			return true;
		}

		/// <summary>
		/// Makes the downloaded file play everywhere (iPhone/QuickTime, TVs, browsers, Windows):
		/// MP4 + H.264 (Universal) or HEVC tagged hvc1 (Compact), 8-bit 4:2:0, AAC audio, faststart.
		/// Streams that already fit are copied without re-encoding.
		/// </summary>
		private static async Task<string> EnsureCompatibleAsync(string inputfile, VideoCompatibilityMode mode, StreamItemModel stream, CancellationToken cancellationToken)
		{
			var probe = await MediaProbe.ProbeAsync(inputfile, ct: cancellationToken);
			if (probe == null)
			{
				Debug.WriteLine("ffprobe failed – keeping the file as it is.");
				stream?.downloadLog.AppendLine("[OnionMedia] ffprobe failed – compatibility conversion skipped.");
				return inputfile;
			}

			string vcodec = probe.VideoCodec?.ToLowerInvariant();
			string acodec = probe.AudioCodec?.ToLowerInvariant();
			string pix = probe.PixelFormat?.ToLowerInvariant() ?? "yuv420p";
			bool eightBit420 = pix is "yuv420p" or "yuvj420p" or "nv12";

			bool copyVideo = vcodec == null
							 || (vcodec == "h264" && eightBit420)
							 || (mode == VideoCompatibilityMode.Compact && vcodec == "hevc" && (eightBit420 || pix == "yuv420p10le"));
			bool targetHevc = mode == VideoCompatibilityMode.Compact;
			bool copyAudio = acodec == null || acodec is "aac" or "mp3";
			bool isMp4 = Path.GetExtension(inputfile).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
			bool needsHvc1Tag = vcodec == "hevc" && copyVideo && probe.VideoCodecTag != "hvc1";

			if (copyVideo && copyAudio && isMp4 && !needsHvc1Tag)
			{
				// Already compatible – only move the moov atom to the front (fast, lossless).
				return await RunCompatPassAsync(inputfile, probe, "-c copy", stream, cancellationToken, allowFailure: true) ?? inputfile;
			}

			string audioArgs = acodec == null ? string.Empty : copyAudio ? "-c:a copy" : "-c:a aac -b:a 192k";
			string videoArgs;
			if (copyVideo)
				videoArgs = "-c:v copy" + (vcodec == "hevc" ? " -tag:v hvc1" : string.Empty);
			else
				videoArgs = GetCompatVideoEncoderArgs(targetHevc, AppSettings.Instance.UseHardwareAcceleratedEncoding ? AppSettings.Instance.HardwareEncoder : (HardwareEncoder?)null);

			string output = await RunCompatPassAsync(inputfile, probe, $"{videoArgs} {audioArgs}", stream, cancellationToken, allowFailure: true);
			if (output == null && !copyVideo && AppSettings.Instance.UseHardwareAcceleratedEncoding)
			{
				// Hardware encoder not available -> software fallback.
				Debug.WriteLine("Hardware encoder failed, falling back to software encoding.");
				videoArgs = GetCompatVideoEncoderArgs(targetHevc, null);
				output = await RunCompatPassAsync(inputfile, probe, $"{videoArgs} {audioArgs}", stream, cancellationToken, allowFailure: false);
			}
			else if (output == null)
				throw new Exception("Conversion to a compatible format failed.");

			return output;
		}

		private static string GetCompatVideoEncoderArgs(bool hevc, HardwareEncoder? hw)
		{
			// Quality targets close to YouTube's own renditions, noticeably smaller than near-lossless CRF 18.
			const string sdr = "-pix_fmt yuv420p";
			if (hevc)
			{
				return hw switch
				{
					HardwareEncoder.Nvidia_NVENC => $"-c:v hevc_nvenc -preset p5 -rc vbr -cq 27 -b:v 0 {sdr} -tag:v hvc1",
					HardwareEncoder.Intel_QSV => "-c:v hevc_qsv -global_quality 26 -pix_fmt nv12 -tag:v hvc1",
					HardwareEncoder.AMD_AMF => $"-c:v hevc_amf -quality quality -rc cqp -qp_i 24 -qp_p 26 {sdr} -tag:v hvc1",
					_ => $"-c:v libx265 -preset medium -crf 25 {sdr} -tag:v hvc1 -x265-params log-level=error"
				};
			}
			return hw switch
			{
				HardwareEncoder.Nvidia_NVENC => $"-c:v h264_nvenc -preset p5 -rc vbr -cq 23 -b:v 0 -profile:v high {sdr}",
				HardwareEncoder.Intel_QSV => "-c:v h264_qsv -global_quality 23 -profile:v high -pix_fmt nv12",
				HardwareEncoder.AMD_AMF => $"-c:v h264_amf -quality quality -rc cqp -qp_i 21 -qp_p 23 -profile:v high {sdr}",
				_ => $"-c:v libx264 -preset medium -crf 21 -profile:v high {sdr}"
			};
		}

		/// <summary>Runs one ffmpeg pass into "*.compat.mp4". Returns the output path, or null on failure if allowFailure.</summary>
		private static async Task<string> RunCompatPassAsync(string inputfile, MediaProbe.Result probe, string codecArgs, StreamItemModel stream, CancellationToken cancellationToken, bool allowFailure)
		{
			string outputpath = Path.ChangeExtension(inputfile, ".compat.mp4");
			for (int i = 2; File.Exists(outputpath); i++)
				outputpath = Path.ChangeExtension(inputfile, $".compat_{i}.mp4");

			bool copyAll = codecArgs.Trim() == "-c copy";
			StringBuilder args = new();
			args.Append($"-y -i \"{inputfile}\" ");
			// "V" = real video streams only (skips the embedded cover image, which is re-attached below).
			args.Append("-map 0:V:0? -map 0:a:0? ");
			args.Append(codecArgs).Append(' ');
			if (!AppSettings.Instance.AutoSelectThreadsForConversion && !copyAll)
				args.Append($"-threads {AppSettings.Instance.MaxThreadCountForConversion} ");
			args.Append("-map_metadata 0 -movflags +faststart ");
			args.Append($"\"{outputpath}\"");

			Debug.WriteLine("Compat pass: ffmpeg " + args);
			stream?.downloadLog.AppendLine("[OnionMedia] ffmpeg " + args);

			Engine ffmpeg = new(pathProvider.FFmpegPath);
			bool error = false;
			double totalSeconds = probe?.DurationSeconds ?? 0;
			ffmpeg.Error += (o, e) =>
			{
				error = true;
				Debug.WriteLine(e.Exception);
				stream?.downloadLog.AppendLine("[OnionMedia] ffmpeg error: " + e.Exception?.Message);
			};
			if (stream != null)
			{
				ffmpeg.Complete += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = 100);
				ffmpeg.Progress += async (o, e) => await dispatcher.EnqueueAsync(() =>
				{
					double total = totalSeconds > 0 ? totalSeconds : e.TotalDuration.TotalSeconds;
					if (total > 0)
						stream.ConversionProgress = (int)Math.Clamp(e.ProcessedDuration.TotalSeconds / total * 100, 0, 100);
				});
			}

			try
			{
				await ffmpeg.ExecuteAsync(args.ToString(), cancellationToken);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				error = true;
				Debug.WriteLine(ex);
			}
			cancellationToken.ThrowIfCancellationRequested();

			bool ok = !error && File.Exists(outputpath) && new FileInfo(outputpath).Length > 1024;
			if (!ok)
			{
				try { if (File.Exists(outputpath)) File.Delete(outputpath); } catch { }
				if (allowFailure) return null;
				throw new Exception("Conversion to a compatible format failed.");
			}

			// Re-attach the cover image if the source had one (thumbnail embedded by yt-dlp).
			if (probe?.AttachedPictureVideoIndex is int coverIndex)
				await TryReembedCoverAsync(inputfile, outputpath, coverIndex, cancellationToken);

			try { File.Delete(inputfile); } catch { }
			return outputpath;
		}

		private static async Task TryReembedCoverAsync(string source, string target, int coverVideoIndex, CancellationToken ct)
		{
			string withCover = Path.ChangeExtension(target, ".cover.mp4");
			try
			{
				string args = $"-y -i \"{target}\" -i \"{source}\" -map 0 -map 1:v:{coverVideoIndex} -c copy -disposition:v:1 attached_pic -movflags +faststart \"{withCover}\"";
				Engine ffmpeg = new(pathProvider.FFmpegPath);
				bool error = false;
				ffmpeg.Error += (o, e) => error = true;
				await ffmpeg.ExecuteAsync(args, ct);
				if (!error && File.Exists(withCover) && new FileInfo(withCover).Length >= new FileInfo(target).Length)
					File.Move(withCover, target, true);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { Debug.WriteLine("Could not re-embed cover: " + ex.Message); }
			finally { try { if (File.Exists(withCover)) File.Delete(withCover); } catch { } }
		}

		private static FormatData GetBestTrimmableFormat(StreamItemModel stream)
		{
			bool isFromYoutube = stream.Video.Url.Contains("youtube.com") || stream.Video.Url.Contains("youtu.be");
			var validFormats = stream.FormatQualityLabels.Where(f => ((f.Key.Protocol != "http_dash_segments" && !isFromYoutube) || (isFromYoutube && !f.Key.Protocol.Contains("m3u8") && !(f.Key.FormatNote ?? string.Empty).Contains("dash video", StringComparison.OrdinalIgnoreCase))) && f.Key.Extension != "3gp" && f.Key.HDR == "SDR");
			var heightSortedFormats = validFormats.OrderByDescending(f => f.Key.Height);
			var extSortedFormats = heightSortedFormats.ThenBy(f => f.Key.Extension == "mp4" ? 0 : 1);
			var selectedFormat = stream.QualityLabel.IsNullOrEmpty()
				? extSortedFormats.FirstOrDefault().Key
				: extSortedFormats.FirstOrDefault(f => f.Key.Height <= stream.GetVideoHeight).Key;
			return selectedFormat;
		}

		private static FormatData GetBestFormatWithoutDash(StreamItemModel stream, out Size originalSize)
		{
			FormatData formatData = null;
			originalSize = default;

			//Get the formats that doesn't use DASH
			var formats = stream.FormatQualityLabels.Where(f => f.Key.Protocol != "http_dash_segments" && f.Key.Extension != "3gp" && f.Key.HDR == "SDR");
			formatData = formats.LastOrDefault(f => stream.QualityLabel.StartsWith(f.Value.ToString())).Key;

			//Return if a matching format was found.
			if (formatData != null) return formatData;

			//Search for the next higher format.
			int selectedHeight = int.Parse(stream.QualityLabel.TrimEnd('p'));
			var higherFormats = formats.Where(f => f.Value > selectedHeight);

			if (!higherFormats.Any()) return null;

			//Get the higher format that comes closest to the choosen format.
			//TODO: Downscale to the selected resolution? (Check for same aspect ratio)
			int min = higherFormats.Min(f => f.Value);
			formatData = higherFormats.Where(f => f.Value == min).Last().Key;

			//Return the size of the choosen format to downscale it later.
			var defaultSize = stream.FormatQualityLabels.LastOrDefault(f => stream.QualityLabel.StartsWith(f.Value.ToString())).Key;
			if (defaultSize?.Height is int dh && dh > 0)
				originalSize = new Size(defaultSize.Width ?? (int)Math.Round(dh * 16 / 9.0 / 2) * 2, dh);

			//Return the format.
			return formatData;
		}

		private static void CancelDownload(object sender, CancellationEventArgs e)
		{
			if (e.Restart)
				throw new DownloadRestartedException();
			e.CancelSource.Cancel();
		}

		//Saves the tags into the file
		public static void SaveTags(string filename, VideoData meta)
		{
			var tagfile = TagLib.File.Create(filename);

			tagfile.Tag.Title = meta.Title;

			if (!meta.Description.IsNullOrEmpty())
				tagfile.Tag.Description = meta.Description;

			if (!meta.Uploader.IsNullOrEmpty())
				tagfile.Tag.Performers = new[] { meta.Uploader };

			if (meta.UploadDate != null)
				tagfile.Tag.Year = (uint)meta.UploadDate.Value.Year;

			tagfile.Save();
		}

		//Saves the tags into the file
		public static void SaveTags(string filename, FileTags tags)
		{
			var tagfile = TagLib.File.Create(filename);

			tagfile.Tag.Title = tags.Title;

			if (!tags.Description.IsNullOrEmpty())
				tagfile.Tag.Description = tags.Description;

			if (!tags.Artist.IsNullOrEmpty())
				tagfile.Tag.Performers = new[] { tags.Artist };

			tagfile.Tag.Year = tags.Year;

			tagfile.Save();
		}

		/// <summary>
		/// Converts the downloaded audio file to the selected audio format (AppSettings) and/or removes the overhead a the start.
		/// </summary>
		/// <returns>The path of the converted file.</returns>
		private static async Task<string> ConvertAudioAsync(string inputfile, string format, TimeSpan startTime = default, StreamItemModel stream = null, CancellationToken cancellationToken = default)
		{
			StringBuilder argBuilder = new();
			argBuilder.Append($"-i \"{inputfile}\" ");
			if (!inputfile.EndsWith(format))
			{
				argBuilder.Append($"-y -loglevel \"repeat+info\" -movflags \"+faststart\" -vn ");
				argBuilder.Append(GetAudioConversionArgs());
			}
			argBuilder.Append($"-ss {startTime} ");

			string outputpath = Path.ChangeExtension(inputfile, format);
			for (int i = 2; File.Exists(outputpath); i++)
				outputpath = Path.ChangeExtension(outputpath, $"_{i}.{format}");
			argBuilder.Append($"\"{outputpath}\"");

			Debug.WriteLine(argBuilder.ToString());
			Engine ffmpeg = new(pathProvider.FFmpegPath);
			var meta = await ffmpeg.GetMetaDataAsync(new InputFile(inputfile), cancellationToken);

			if (stream != null)
			{
				ffmpeg.Complete += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = 100);
				ffmpeg.Error += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = 0);
				ffmpeg.Progress += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = (int)(e.ProcessedDuration / (meta.Duration - startTime) * 100));
			}

			await ffmpeg.ExecuteAsync(argBuilder.ToString(), cancellationToken);
			return outputpath;
		}

		/// <summary>
		/// Converts a file to GIF and returns the new path
		/// </summary>
		private static async Task<string> ConvertToGifAsync(string inputfile, StreamItemModel stream, CancellationToken cancellationToken = default)
		{
			string outputpath = Path.ChangeExtension(inputfile, ".gif");
			for (int i = 2; File.Exists(outputpath); i++)
				outputpath = Path.ChangeExtension(inputfile, $"_{i}.gif");

			var input = new InputFile(inputfile);
			Engine ffmpeg = new(pathProvider.FFmpegPath);

			// Get metadata to know duration
			var meta = await ffmpeg.GetMetaDataAsync(input, cancellationToken);
			
			// Use palettegen and paletteuse for high quality GIF
			// "fps=15,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"
            // We use 15 fps to keep size reasonable, but can be adjusted.
			
			StringBuilder argBuilder = new();
			argBuilder.Append($"-i \"{inputfile}\" ");
			argBuilder.Append("-vf \"fps=15,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" ");
			argBuilder.Append("-loop 0 ");
			argBuilder.Append($"\"{outputpath}\"");

			if (stream != null)
			{
				ffmpeg.Complete += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = 100);
				ffmpeg.Error += async (o, e) => await dispatcher.EnqueueAsync(() => 
				{
					stream.ConversionProgress = 0;
					Debug.WriteLine(e.Exception);
				});
				ffmpeg.Progress += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = (int)(e.ProcessedDuration / meta.Duration * 100));
			}

			await ffmpeg.ExecuteAsync(argBuilder.ToString(), cancellationToken);
            
            // Try to delete the input video file as it was a temporary intermediate file
            try { if(File.Exists(inputfile)) File.Delete(inputfile); } catch { }

			return outputpath;
		}

		/// <summary>
		/// Converts a file to MP4 (with H264 codec if enabled in settings) and returns the new path
		/// </summary>
		private static async Task<string> ConvertToMp4Async(string inputfile, TimeSpan startTime = default, TimeSpan endTime = default, StreamItemModel stream = null, Size resolution = default, IMediaAnalysis videoMeta = null, CancellationToken cancellationToken = default)
		{
			string outputpath = Path.ChangeExtension(inputfile, ".converted.mp4");
			for (int i = 2; File.Exists(outputpath); i++)
				outputpath = Path.ChangeExtension(inputfile, $".converted_{i}.mp4");

			var input = new InputFile(inputfile);
			var output = new OutputFile(outputpath);
			Engine ffmpeg = new(pathProvider.FFmpegPath);

			var con = new ConversionOptions();
			if (AppSettings.Instance.AutoConvertToH264AfterDownload)
			{
				if (AppSettings.Instance.UseHardwareAcceleratedEncoding)
				{
					con.VideoCodec = AppSettings.Instance.HardwareEncoder switch
					{
						HardwareEncoder.Intel_QSV => VideoCodec.h264_qsv,
						HardwareEncoder.AMD_AMF => VideoCodec.h264_amf,
						HardwareEncoder.Nvidia_NVENC => VideoCodec.h264_nvenc,
						_ => VideoCodec.libx264
					};

					var meta = videoMeta ?? await FFProbe.AnalyseAsync(inputfile);
					con.VideoBitRate = (int)GlobalResources.CalculateVideoBitrate(inputfile, meta) / 1000;
				}
				else
					con.VideoCodec = VideoCodec.libx264;
			}
			else
				con.VideoCodec = VideoCodec.Default;

			StringBuilder extraArgs = new();
			extraArgs.Append($"-ss {startTime}");
			if (endTime != default)
				extraArgs.Append($" -to {endTime}");
			if (resolution != default)
				extraArgs.Append($" -vf scale={resolution.Width}:{resolution.Height}");
			con.ExtraArguments = extraArgs.ToString();

			if (!AppSettings.Instance.AutoSelectThreadsForConversion)
				con.Threads = AppSettings.Instance.MaxThreadCountForConversion;

			bool error = false;
			if (stream != null)
			{
				ffmpeg.Complete += async (o, e) => await dispatcher.EnqueueAsync(() => stream.ConversionProgress = 100);
				ffmpeg.Error += async (o, e) => await dispatcher.EnqueueAsync(() =>
				{
					stream.ConversionProgress = 0;
					error = true;
					Debug.WriteLine(e.Exception);
				});
				ffmpeg.Progress += async (o, e) => await dispatcher.EnqueueAsync(() =>
				{
					var totalDuration = e.TotalDuration - startTime;
					if (endTime != default)
						totalDuration -= totalDuration - endTime;

					stream.ConversionProgress = (int)(e.ProcessedDuration / totalDuration * 100);
				});
			}

			await ffmpeg.ConvertAsync(input, output, con, cancellationToken);

			//Fallback to software-encoding when hardware-encoding doen't work
			if (error && con.VideoCodec is VideoCodec.h264_nvenc or VideoCodec.h264_qsv or VideoCodec.h264_amf && AppSettings.Instance.FallBackToSoftwareEncoding)
			{
				error = false;
				con.VideoCodec = VideoCodec.libx264;
				con.VideoBitRate = null;
				await ffmpeg.ConvertAsync(input, output, con, cancellationToken);
			}

			if (error)
				stream.DownloadState = Enums.DownloadState.IsFailed;
			return outputpath;
		}


		private static async Task<Uri> MoveToDisk(string tempfile, string videoname, Enums.ItemType itemType, string directory = null, CancellationToken cancellationToken = default)
		{
			string extension = Path.GetExtension(tempfile);

			if (videoname.IsNullOrEmpty())
				videoname = "Download";

			string savefilepath = directory ?? (itemType == Enums.ItemType.audio ? AppSettings.Instance.DownloadsAudioSavePath : AppSettings.Instance.DownloadsVideoSavePath);

			Directory.CreateDirectory(savefilepath);
			int maxFilenameLength = Math.Min(150, 250 - (savefilepath.Length + extension.Length));
			string filename = Path.Combine(savefilepath, videoname.TrimToFilename(maxFilenameLength) + extension);

			if (!File.Exists(filename))
				await Task.Run(async () => await GlobalResources.MoveFileAsync(tempfile, filename, cancellationToken));

			else
			{
				string dir = Path.GetDirectoryName(filename);
				string name = Path.GetFileNameWithoutExtension(filename);

				//Increases the number until a possible file name is found
				for (int i = 2; File.Exists(filename); i++)
					filename = Path.Combine(dir, $"{name}_{i}{extension}");

				await Task.Run(async () => await GlobalResources.MoveFileAsync(tempfile, filename, cancellationToken));
			}
			return new Uri(filename);
		}

		public static async Task<IEnumerable<IVideo>> GetVideosFromPlaylistAsync(string playlistUrl, CancellationToken cToken = default)
		{
			//TODO: CHECK FOR VULNERABILITIES!
			Process ytdlProc = new();
			ytdlProc.StartInfo.CreateNoWindow = true;
			ytdlProc.StartInfo.FileName = pathProvider.YtDlPath;
			ytdlProc.StartInfo.RedirectStandardOutput = true;
			string denoArg = YtDlpOptions.DenoPath is string deno ? $" --js-runtimes \"deno:{deno}\"" : string.Empty;
			ytdlProc.StartInfo.Arguments = $"\"{playlistUrl}\" --flat-playlist --dump-json{denoArg}";
			ytdlProc.Start();

			string output = await ytdlProc.StandardOutput.ReadToEndAsync();
			if (!ytdlProc.HasExited)
				await ytdlProc.WaitForExitAsync(cToken);

			List<SelectableVideo> videos = new();
			foreach (var line in output.Split('\n'))
			{
				cToken.ThrowIfCancellationRequested();
				try { videos.Add(JsonConvert.DeserializeObject<SelectableVideo>(line.Trim())); }
				catch { continue; }
			}

			return videos.Where(v => v?.DurationAsFloatingNumber != null);
		}

		//Returns the resolutions from the videos.
		public static IEnumerable<string> GetResolutions(IEnumerable<StreamItemModel> videos)
		{
			ReadOnlySpan<int> validResolutions = stackalloc[] { 144, 144, 240, 360, 480, 720, 1080, 1440, 2160, 4320, 8640 };
			SortedSet<int> heights = new();
			SortedSet<int> correctHeights = new();
			List<string> resolutions = new();

			//Add video heights to a SortedSet
			foreach (var video in videos.Where(v => v != null))
				if (video.Video.Formats != null)
					foreach (var format in video.Video.Formats.Where(f => f != null))
						if (format.Height != null)
							heights.Add((int)format.Height);

			//Round up "non-default" resolutions
			foreach (int height in heights)
			{
				for (int i = 1; i < validResolutions.Length; i++)
				{
					if (height < validResolutions[i] && height > validResolutions[i - 1] || height == validResolutions[i])
					{
						correctHeights.Add(validResolutions[i]);
						break;
					}
					else if (height < validResolutions[i])
					{
						correctHeights.Add(validResolutions[i - 1]);
						break;
					}
				}
			}

			//Converts the heights to string values and adds a "p" at the end.
			foreach (int height in correctHeights.Reverse())
				resolutions.Add(height + "p");

			return resolutions;
		}

		public static async IAsyncEnumerable<SearchItemModel> GetSearchResultsAsync(string searchTerm, int maxResults = 20)
		{
			if (VideoSearchCancelSource.IsCancellationRequested)
				VideoSearchCancelSource = new();

			int counter = 0;
			await foreach (var result in youtube.Search.GetVideosAsync(searchTerm, VideoSearchCancelSource.Token))
			{
				if (counter == maxResults)
					break;

				yield return new SearchItemModel(result);
				counter++;
			}
		}
		public static CancellationTokenSource VideoSearchCancelSource { get; private set; } = new();


		private static string GetAudioConversionArgs() => AppSettings.Instance.DownloadsAudioFormat switch
		{
			AudioConversionFormat.Flac => "-acodec flac ",
			AudioConversionFormat.Opus => "-acodec libopus ",
			AudioConversionFormat.M4a => $"-acodec aac \"-bsf:a\" aac_adtstoasc -aq {GetAudioQuality("aac").ToString().Replace(',', '.')} ",
			AudioConversionFormat.Mp3 => $"-acodec libmp3lame -aq {GetAudioQuality("libmp3lame").ToString().Replace(',', '.')} ",
			AudioConversionFormat.Vorbis => $"-acodec libvorbis -aq {GetAudioQuality("libvorbis").ToString().Replace(',', '.')} ",
			_ => $"-f {AppSettings.Instance.DownloadsAudioFormat.ToString().ToLower()} "
		};

		private static readonly Dictionary<string, (float, int)> audioQualityDict = new()
		{
			{ "libmp3lame", (10, 0) },
			{ "libvorbis", (0, 10) },
            // FFmpeg's AAC encoder does not have an upper limit for the value of -aq.
            // Experimentally, with values over 4, bitrate changes were minimal or non-existent
            { "aac", (0.1f, 4) }
		};

		//Preferred quality for audio-conversion (up to 10).
		private const float preferredQuality = 5f;

		private static float GetAudioQuality(string codec)
			=> audioQualityDict[codec].Item2 + (audioQualityDict[codec].Item1 - audioQualityDict[codec].Item2) * (preferredQuality / 10);
	}
}
