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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnionMedia.Core.Models;
using OnionMedia.Core.Services;

namespace OnionMedia.Core.Classes
{
    /// <summary>
    /// Keeps yt-dlp up to date. The MSIX install folder is read-only, so the bundled yt-dlp.exe is copied
    /// to LocalState\yt-dlp\ once and updated there with "yt-dlp -U" (at most once per day).
    /// IPathProvider.YtDlPath prefers that writable copy.
    /// </summary>
    public static class YtDlpUpdater
    {
        private static readonly SemaphoreSlim gate = new(1, 1);
        private static readonly TimeSpan checkInterval = TimeSpan.FromHours(24);

        public static string WritableDirectory => Path.Combine(IoC.Default.GetService<IPathProvider>().LocalPath, "yt-dlp");
        public static string WritablePath => Path.Combine(WritableDirectory, "yt-dlp.exe");
        public static string BundledPath => Path.Combine(IoC.Default.GetService<IPathProvider>().ExternalBinariesDir, "yt-dlp.exe");

        /// <summary>Raised after the yt-dlp executable changed (path or version).</summary>
        public static event EventHandler<string> Updated;

        /// <summary>
        /// Makes sure the writable copy exists and (if enabled or forced) runs "yt-dlp -U".
        /// Returns a human readable log.
        /// </summary>
        public static async Task<string> EnsureUpToDateAsync(bool force = false, CancellationToken ct = default)
        {
            if (!force && !AppSettings.Instance.AutoUpdateYtDlp) return "Automatic yt-dlp updates are disabled.";
            if (!await gate.WaitAsync(force ? TimeSpan.FromMinutes(3) : TimeSpan.Zero, ct)) return "An update is already running.";
            try
            {
                string log = await PrepareWritableCopyAsync(ct);
                long last = AppSettings.Instance.LastYtDlpUpdateCheckTicks;
                if (!force && last > 0 && DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < checkInterval)
                    return log + "Last update check was less than 24 h ago.";

                if (!File.Exists(WritablePath))
                    return log + "No writable yt-dlp copy – update skipped.";

                string before = await GetVersionAsync(WritablePath, ct);
                var (code, output) = await YtDlpCustomArgs.RunYtDlpAsync("-U", TimeSpan.FromMinutes(3), ct, WritableDirectory, WritablePath);
                string after = await GetVersionAsync(WritablePath, ct);
                if (code == 0 || after != before)
                    AppSettings.Instance.LastYtDlpUpdateCheckTicks = DateTime.UtcNow.Ticks;

                TryDelete(WritablePath + ".old");
                ApplyPath();
                if (after != before) Updated?.Invoke(null, after);
                return log + output.Trim() + Environment.NewLine + $"yt-dlp version: {after ?? "unknown"}";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("yt-dlp update failed: " + ex);
                return "yt-dlp update failed: " + ex.Message;
            }
            finally { gate.Release(); }
        }

        /// <summary>Copies the bundled yt-dlp to LocalState if missing or if the bundled one is newer (app update).</summary>
        private static async Task<string> PrepareWritableCopyAsync(CancellationToken ct)
        {
            if (!File.Exists(BundledPath)) return string.Empty;
            Directory.CreateDirectory(WritableDirectory);

            bool copy = !File.Exists(WritablePath);
            if (!copy)
            {
                string bundledVersion = await GetVersionAsync(BundledPath, ct);
                string localVersion = await GetVersionAsync(WritablePath, ct);
                copy = localVersion == null || (bundledVersion != null && string.CompareOrdinal(bundledVersion, localVersion) > 0);
            }
            if (!copy)
            {
                ApplyPath();
                return string.Empty;
            }

            string tmp = WritablePath + ".tmp";
            File.Copy(BundledPath, tmp, true);
            File.Move(tmp, WritablePath, true);
            ApplyPath();
            Updated?.Invoke(null, null);
            return "Copied the bundled yt-dlp to " + WritableDirectory + Environment.NewLine;
        }

        /// <summary>Points the shared YoutubeDL client to the currently preferred executable.</summary>
        public static void ApplyPath()
        {
            string path = IoC.Default.GetService<IPathProvider>().YtDlPath;
            if (DownloaderMethods.downloadClient.YoutubeDLPath != path)
                DownloaderMethods.downloadClient.YoutubeDLPath = path;
        }

        public static async Task<string> GetVersionAsync(string exePath = null, CancellationToken ct = default)
        {
            try
            {
                var (code, output) = await YtDlpCustomArgs.RunYtDlpAsync("--version", TimeSpan.FromSeconds(30), ct, null, exePath);
                if (code != 0) return null;
                var m = Regex.Match(output, @"\d{4}\.\d{2}\.\d{2}(\.\d+)?");
                return m.Success ? m.Value : output.Trim().Split('\n').LastOrDefault()?.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
