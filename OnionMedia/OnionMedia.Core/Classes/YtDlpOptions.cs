/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnionMedia.Core.Models;
using OnionMedia.Core.Services;
using YoutubeDLSharp.Options;

namespace OnionMedia.Core.Classes
{
    /// <summary>
    /// Single place that adds every "extra" yt-dlp option to an OptionSet:
    /// cookies (CookieOptionsBuilder), browser impersonation for anti-bot protected
    /// sites (Cloudflare) and the user's custom yt-dlp parameters.
    /// </summary>
    public static class YtDlpOptions
    {
        /// <summary>Impersonation target passed to yt-dlp (needs curl_cffi, which the standalone yt-dlp.exe contains).</summary>
        public const string ImpersonateTarget = "chrome";

        // Hosts that answered with an anti-bot challenge during this session.
        private static readonly ConcurrentDictionary<string, byte> impersonationHosts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Applies cookies, impersonation (if needed for the URL) and custom args.
        /// Dispose the returned token after the yt-dlp run (removes temporary cookie files).
        /// </summary>
        public static IDisposable Apply(OptionSet options, string url, bool? forceImpersonation = null, bool applyCustomArgs = true)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            IDisposable cleanup = CookieOptionsBuilder.Apply(options);

            bool userSetImpersonate = applyCustomArgs && YtDlpCustomArgs.ActiveTokens.Any(t => t.StartsWith("--impersonate", StringComparison.Ordinal));
            bool impersonate = forceImpersonation ?? NeedsImpersonation(url);
            if (impersonate && !userSetImpersonate)
                options.AddCustomOption("--impersonate", ImpersonateTarget);

            if (applyCustomArgs)
                YtDlpCustomArgs.ApplyTo(options);

            string deno = DenoPath;
            if (deno != null && !(applyCustomArgs && YtDlpCustomArgs.ActiveTokens.Any(t => t.StartsWith("--js-runtimes", StringComparison.Ordinal))))
                options.AddCustomOption("--js-runtimes", "deno:" + deno);

            return cleanup;
        }

        /// <summary>
        /// The bundled deno.exe (needed by yt-dlp for YouTube's JS challenges). Only returned when yt-dlp runs
        /// from another folder (the auto-updated copy in LocalState), because yt-dlp would not find it there.
        /// </summary>
        public static string DenoPath
        {
            get
            {
                try
                {
                    var pp = IoC.Default.GetService<IPathProvider>();
                    string bundledDir = Path.GetFullPath(pp.ExternalBinariesDir);
                    string deno = Path.Combine(bundledDir, "deno.exe");
                    if (!File.Exists(deno)) return null;
                    string ytDir = Path.GetDirectoryName(Path.GetFullPath(pp.YtDlPath));
                    return string.Equals(ytDir?.TrimEnd('\\'), bundledDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : deno;
                }
                catch { return null; }
            }
        }

        public static bool IsYoutubeUrl(string url)
            => !string.IsNullOrEmpty(url) && (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));

        public static string GetHost(string url)
            => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

        /// <summary>True when this host is known to need a browser-like TLS fingerprint.</summary>
        public static bool NeedsImpersonation(string url)
        {
            string host = GetHost(url);
            return host.Length > 0 && impersonationHosts.ContainsKey(host);
        }

        public static void RememberImpersonation(string url)
        {
            string host = GetHost(url);
            if (host.Length > 0) impersonationHosts[host] = 0;
        }

        /// <summary>
        /// Detects yt-dlp errors caused by anti-bot protection (Cloudflare challenge, 403/429 for non-browser clients).
        /// </summary>
        /// <summary>
        /// "watch?v=ID&amp;list=RD...&amp;index=1" -> "https://www.youtube.com/watch?v=ID" (single video, no mix/radio playlist).
        /// Other URLs are returned unchanged.
        /// </summary>
        public static string StripPlaylistParameters(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !IsYoutubeUrl(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url;
            var m = System.Text.RegularExpressions.Regex.Match(uri.Query, @"[?&]v=([A-Za-z0-9_-]{6,})");
            if (m.Success) return "https://www.youtube.com/watch?v=" + m.Groups[1].Value;
            if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Length > 1)
                return "https://www.youtube.com/watch?v=" + uri.AbsolutePath.Trim('/');
            return url;
        }

        public static bool LooksLikeAntiBotBlock(IEnumerable<string> errorOutput)
        {
            if (errorOutput == null) return false;
            string text = string.Join("\n", errorOutput);
            return text.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase)
                || text.Contains("impersonat", StringComparison.OrdinalIgnoreCase)
                || text.Contains("anti-bot", StringComparison.OrdinalIgnoreCase)
                || text.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
                || text.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
                || text.Contains("HTTP Error 503", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for transient network failures (DPI resets, VPN hiccups) that deserve another try.</summary>
        public static bool LooksLikeNetworkError(IEnumerable<string> errorOutput)
        {
            if (errorOutput == null) return false;
            string text = string.Join("\n", errorOutput);
            return text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("EOF occurred", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Failed to perform", StringComparison.OrdinalIgnoreCase)
                || text.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Parsing, validation and application of the user's custom yt-dlp parameters.
    /// </summary>
    public static class YtDlpCustomArgs
    {
        /// <summary>
        /// Options that would break OnionMedia's pipeline (output location, JSON parsing, progress parsing, etc.).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> BlockedOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "-o", "the output template is managed by OnionMedia" },
            { "--output", "the output template is managed by OnionMedia" },
            { "-P", "the output path is managed by OnionMedia (use Settings → paths)" },
            { "--paths", "the output path is managed by OnionMedia (use Settings → paths)" },
            { "--exec", "OnionMedia uses --exec internally to find the downloaded file" },
            { "--exec-before-download", "not supported inside OnionMedia" },
            { "-j", "OnionMedia parses yt-dlp's JSON itself" },
            { "-J", "OnionMedia parses yt-dlp's JSON itself" },
            { "--dump-json", "OnionMedia parses yt-dlp's JSON itself" },
            { "--dump-single-json", "OnionMedia parses yt-dlp's JSON itself" },
            { "-O", "--print changes yt-dlp's output and breaks progress parsing" },
            { "--print", "--print changes yt-dlp's output and breaks progress parsing" },
            { "--print-to-file", "not supported inside OnionMedia" },
            { "-s", "the download would be skipped" },
            { "--simulate", "the download would be skipped" },
            { "--skip-download", "the download would be skipped" },
            { "-q", "quiet mode hides the progress OnionMedia shows" },
            { "--quiet", "quiet mode hides the progress OnionMedia shows" },
            { "--no-progress", "OnionMedia needs the progress output" },
            { "--progress-template", "OnionMedia needs the default progress output" },
            { "--newline", "already handled by OnionMedia" },
            { "-a", "batch files are not supported here (use 'Load from File')" },
            { "--batch-file", "batch files are not supported here (use 'Load from File')" },
            { "-U", "use the 'Update yt-dlp' button instead" },
            { "--update", "use the 'Update yt-dlp' button instead" },
            { "--update-to", "use the 'Update yt-dlp' button instead" },
            { "--version", "not a download option" },
            { "-h", "not a download option" },
            { "--help", "not a download option" },
            { "--config-locations", "config files are ignored on purpose (--ignore-config)" },
            { "--load-info-json", "not supported inside OnionMedia" },
            { "-f", "the format is chosen by the 'Max Quality' selector – use -S (format sort) instead so the quality ceiling keeps working" },
            { "--format", "the format is chosen by the 'Max Quality' selector – use -S (format sort) instead so the quality ceiling keeps working" },
        };

        /// <summary>Tokens of the currently active custom args (empty if disabled or invalid).</summary>
        public static IReadOnlyList<string> ActiveTokens
        {
            get
            {
                var s = AppSettings.Instance;
                if (!s.UseCustomYtDlpArgs || string.IsNullOrWhiteSpace(s.CustomYtDlpArgs))
                    return Array.Empty<string>();
                var result = Validate(s.CustomYtDlpArgs);
                return result.IsValid ? result.Tokens : Array.Empty<string>();
            }
        }

        /// <summary>
        /// Extracts the user's -S/--format-sort fields so they can be merged into OnionMedia's own sort string
        /// (yt-dlp would otherwise give the LAST -S priority over everything).
        /// </summary>
        public static string GetUserFormatSort()
        {
            var tokens = ActiveTokens;
            List<string> sorts = new();
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if ((t == "-S" || t == "--format-sort") && i + 1 < tokens.Count)
                    sorts.Add(tokens[++i]);
                else if (t.StartsWith("--format-sort=", StringComparison.Ordinal))
                    sorts.Add(t.Substring("--format-sort=".Length));
            }
            return string.Join(",", sorts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        /// <summary>Adds the active custom args (without -S, which is merged separately) as one raw option.</summary>
        public static void ApplyTo(OptionSet options)
        {
            var tokens = ActiveTokens;
            if (tokens.Count == 0) return;

            List<string> remaining = new();
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (t == "-S" || t == "--format-sort") { i++; continue; }
                if (t.StartsWith("--format-sort=", StringComparison.Ordinal)) continue;
                remaining.Add(t);
            }
            if (remaining.Count == 0) return;

            // A custom bool option prints its "option string" verbatim, which lets us pass a pre-quoted raw argument string.
            options.AddCustomOption(JoinForCommandLine(remaining), true);
        }

        public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Tokens, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

        /// <summary>Static validation (syntax, quotes, blocked options). Does not start yt-dlp.</summary>
        public static ValidationResult Validate(string raw)
        {
            List<string> errors = new();
            List<string> warnings = new();
            List<string> tokens = new();

            if (string.IsNullOrWhiteSpace(raw))
                return new ValidationResult(true, tokens, errors, warnings);

            // Allow comments (# ...) and one option per line for readability.
            var cleanedLines = raw.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#"));
            string cleaned = string.Join(" ", cleanedLines);

            if (!TryTokenize(cleaned, tokens, out string tokenizeError))
            {
                errors.Add(tokenizeError);
                return new ValidationResult(false, tokens, errors, warnings);
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (!t.StartsWith("-"))
                {
                    // Positional values are only valid directly after an option.
                    if (i == 0 || !tokens[i - 1].StartsWith("-"))
                        errors.Add($"'{t}' is not an option. URLs are added in the main window, not here.");
                    continue;
                }

                string name = t.Contains('=') ? t.Substring(0, t.IndexOf('=')) : t;
                if (BlockedOptions.TryGetValue(name, out string reason))
                    errors.Add($"{name} is not allowed: {reason}.");
                else if (t.Length > 2 && t[1] != '-' && BlockedOptions.Keys.Any(k => k.Length == 2 && t.StartsWith(k, StringComparison.Ordinal) && k is "-o" or "-P" or "-f"))
                    errors.Add($"{t.Substring(0, 2)} is not allowed: {BlockedOptions[t.Substring(0, 2)]}.");

                if (name is "--cookies" or "--cookies-from-browser")
                    warnings.Add($"{name} overrides the cookie button. Prefer the cookie button so the setting is visible.");
                if (name is "--remux-video" or "--recode-video" or "--merge-output-format")
                    warnings.Add($"{name} may conflict with the output codec mode (Settings → Video compatibility).");
            }

            return new ValidationResult(errors.Count == 0, tokens, errors, warnings);
        }

        /// <summary>
        /// Lets yt-dlp itself check the options: unknown options or bad values make it fail before --version is processed.
        /// </summary>
        public static async Task<(bool ok, string output)> CheckWithYtDlpAsync(string raw, CancellationToken ct = default)
        {
            var validation = Validate(raw);
            if (!validation.IsValid)
                return (false, string.Join("\n", validation.Errors));

            // Without a URL yt-dlp parses every option and then stops with "You must provide at least one URL"
            // (with --version it would exit before reporting unknown options).
            string args = "--ignore-config " + JoinForCommandLine(validation.Tokens);
            var (code, output) = await RunYtDlpAsync(args, TimeSpan.FromSeconds(30), ct);
            bool parsed = output.Contains("provide at least one URL", StringComparison.OrdinalIgnoreCase)
                          || (code == 0 && !output.Contains("error:", StringComparison.OrdinalIgnoreCase));
            string version = await YtDlpUpdater.GetVersionAsync(null, ct);
            string cleaned = string.Join("\n", output.Split('\n').Where(l => !l.Contains("provide at least one URL") && !l.Contains("Type yt-dlp --help") && !l.TrimStart().StartsWith("Usage:")).Select(l => l.TrimEnd()).Where(l => l.Length > 0));
            return (parsed, (cleaned.Length > 0 ? cleaned + "\n" : string.Empty) + $"yt-dlp {version}");
        }

        /// <summary>Runs yt-dlp in simulate mode with the custom args against a URL and returns the log.</summary>
        public static async Task<(bool ok, string output)> TestAgainstUrlAsync(string raw, string url, CancellationToken ct = default)
        {
            var validation = Validate(raw);
            if (!validation.IsValid)
                return (false, string.Join("\n", validation.Errors));
            if (string.IsNullOrWhiteSpace(url))
                return (false, "Add a video to the playlist (or select one) to test against it.");

            OptionSet tmp = new();
            using var cleanup = CookieOptionsBuilder.Apply(tmp);
            if (YtDlpOptions.NeedsImpersonation(url) && !validation.Tokens.Any(t => t.StartsWith("--impersonate")))
                tmp.AddCustomOption("--impersonate", YtDlpOptions.ImpersonateTarget);
            if (YtDlpOptions.DenoPath is string deno && !validation.Tokens.Any(t => t.StartsWith("--js-runtimes")))
                tmp.AddCustomOption("--js-runtimes", "deno:" + deno);

            string args = $"--ignore-config --no-update --no-playlist --simulate -v {tmp} {JoinForCommandLine(validation.Tokens)} \"{url.Replace("\"", string.Empty)}\"";
            var (code, output) = await RunYtDlpAsync(args, TimeSpan.FromMinutes(2), ct);
            bool userImpersonates = validation.Tokens.Any(t => t.StartsWith("--impersonate"));
            if (code != 0 && !userImpersonates && !tmp.CustomOptions.Any(o => o.OptionStrings.Contains("--impersonate"))
                && YtDlpOptions.LooksLikeAntiBotBlock(output.Split('\n')))
            {
                // Same behaviour as real fetches: retry with browser impersonation (Cloudflare).
                string retryArgs = args.Replace("--simulate -v ", $"--simulate -v --impersonate {YtDlpOptions.ImpersonateTarget} ");
                var (code2, output2) = await RunYtDlpAsync(retryArgs, TimeSpan.FromMinutes(2), ct);
                if (code2 == 0) YtDlpOptions.RememberImpersonation(url);
                return (code2 == 0, output.Trim() + "\n\n[OnionMedia] Anti-bot block detected – retried with --impersonate " + YtDlpOptions.ImpersonateTarget + ":\n" + output2.Trim());
            }
            return (code == 0, output.Trim());
        }

        internal static async Task<(int code, string output)> RunYtDlpAsync(string arguments, TimeSpan timeout, CancellationToken ct = default, string workingDirectory = null, string exePath = null)
        {
            var pathProvider = IoC.Default.GetService<IPathProvider>();
            var psi = new ProcessStartInfo
            {
                FileName = exePath ?? pathProvider.YtDlPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            StringBuilder sb = new();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                lock (sb) sb.AppendLine(ct.IsCancellationRequested ? "Cancelled." : $"Timed out after {timeout.TotalSeconds:0} s.");
                return (-1, sb.ToString());
            }
            proc.WaitForExit(); // flush async output
            return (proc.ExitCode, sb.ToString());
        }

        /// <summary>Splits a command line like a shell: supports "double" and 'single' quotes and backslash-escaped quotes.</summary>
        public static bool TryTokenize(string input, List<string> tokens, out string error)
        {
            error = null;
            StringBuilder current = new();
            bool inToken = false;
            char quote = '\0';
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < input.Length && input[i + 1] == quote) { current.Append(quote); i++; }
                    else if (c == quote) quote = '\0';
                    else current.Append(c);
                    continue;
                }
                if (c is '"' or '\'') { quote = c; inToken = true; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (inToken) { tokens.Add(current.ToString()); current.Clear(); inToken = false; }
                    continue;
                }
                current.Append(c);
                inToken = true;
            }
            if (quote != '\0')
            {
                error = $"Missing closing quote ({quote}).";
                return false;
            }
            if (inToken) tokens.Add(current.ToString());
            return true;
        }

        /// <summary>Joins tokens into a Windows command line (MSVCRT quoting rules, which Python/yt-dlp uses).</summary>
        public static string JoinForCommandLine(IEnumerable<string> tokens)
            => string.Join(" ", tokens.Select(QuoteArgument));

        private static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return arg;
            StringBuilder sb = new("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes); backslashes = 0; sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
