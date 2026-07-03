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
using System.IO;
using System.Linq;
using OnionMedia.Core.Enums;
using OnionMedia.Core.Models;
using OnionMedia.Core.Services;
using YoutubeDLSharp.Options;

namespace OnionMedia.Core.Classes
{
    /// <summary>
    /// Central place that turns the current cookie settings into yt-dlp options.
    /// Applied to metadata-fetch, download and thumbnail OptionSets to avoid duplication.
    /// </summary>
    public static class CookieOptionsBuilder
    {
        private static readonly IPathProvider pathProvider =
            IoC.Default.GetService<IPathProvider>() ?? throw new ArgumentNullException();

        /// <summary>
        /// Empty cleanup token used when no temporary file was created.
        /// </summary>
        public static readonly IDisposable NoCleanup = new CleanupToken(null);

        /// <summary>
        /// Applies cookie options to the given OptionSet based on the current AppSettings.
        /// </summary>
        /// <returns>
        /// A disposable token. Dispose it AFTER the yt-dlp run finished to remove any
        /// temporary cookie file. Never null.
        /// </returns>
        public static IDisposable Apply(OptionSet options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var s = AppSettings.Instance;

            switch (s.CookieSource)
            {
                case CookieSource.Browser:
                    string key = GetBrowserKey();
                    if (!string.IsNullOrWhiteSpace(key))
                        options.AddCustomOption("--cookies-from-browser", key);
                    return NoCleanup;

                case CookieSource.File:
                    if (!string.IsNullOrWhiteSpace(s.CookieFilePath) && File.Exists(s.CookieFilePath))
                        options.AddCustomOption("--cookies", s.CookieFilePath);
                    return NoCleanup;

                case CookieSource.Pasted:
                    if (string.IsNullOrWhiteSpace(s.PastedCookies))
                        return NoCleanup;
                    string tempFile = CreateTempCookieFile(s.PastedCookies);
                    options.AddCustomOption("--cookies", tempFile);
                    return new CleanupToken(tempFile);

                default:
                    return NoCleanup; // CookieSource.None -> no cookie arguments at all
            }
        }

        /// <summary>
        /// Returns the yt-dlp browser key for the current settings, or empty string.
        /// </summary>
        public static string GetBrowserKey()
        {
            var s = AppSettings.Instance;
            if (s.CookieBrowser == CookieBrowser.Custom)
                return (s.CookieCustomBrowserKey ?? string.Empty).Trim();
            return s.CookieBrowser.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Writes the pasted cookie text into a temporary Netscape cookie file.
        /// Ensures the Netscape header and CRLF line endings (required by yt-dlp on Windows).
        /// </summary>
        private static string CreateTempCookieFile(string content)
        {
            Directory.CreateDirectory(pathProvider.DownloaderTempdir);
            string path = Path.Combine(pathProvider.DownloaderTempdir,
                "cookies_" + Path.GetRandomFileName() + ".txt");

            const string header = "# Netscape HTTP Cookie File";
            string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n').ToList();
            if (!lines.Any(l => l.TrimStart().StartsWith("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)))
                lines.Insert(0, header);

            // yt-dlp on Windows expects CRLF line endings.
            File.WriteAllText(path, string.Join("\r\n", lines));
            return path;
        }

        /// <summary>
        /// Heuristic check that the given text looks like a Netscape/Mozilla cookie file:
        /// either it carries the Netscape header, or it has at least one TAB-separated
        /// row with the expected 7 fields. Used to reject random clipboard content / URLs.
        /// </summary>
        public static bool LooksLikeCookieFile(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;

            foreach (var raw in content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("# HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (line.StartsWith("#")) continue;
                // A real cookie row has 7 TAB-separated fields.
                if (line.Split('\t').Length >= 7) return true;
            }
            return false;
        }

        /// <summary>
        /// Extracts distinct cookie domains from Netscape-formatted text (file or pasted).
        /// Returns an empty list when nothing reliable can be parsed.
        /// </summary>
        public static IReadOnlyList<string> ParseDomains(string netscapeContent)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(netscapeContent))
                return result;

            foreach (var raw in netscapeContent.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                // Netscape row: domain \t flag \t path \t secure \t expiration \t name \t value
                string[] parts = line.Split('\t');
                if (parts.Length < 7) continue;
                string domain = parts[0].TrimStart('.');
                if (domain.Length > 0 && !result.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    result.Add(domain);
            }
            return result;
        }

        private sealed class CleanupToken : IDisposable
        {
            private readonly string filePath;
            public CleanupToken(string filePath) => this.filePath = filePath;
            public void Dispose()
            {
                if (string.IsNullOrEmpty(filePath)) return;
                try { if (File.Exists(filePath)) File.Delete(filePath); }
                catch { /* never crash on temp cleanup */ }
            }
        }
    }
}
