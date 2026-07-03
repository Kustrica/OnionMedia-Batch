# Cookie Support Implementation Plan — OnionMedia-Batch

> This document is a COMPLETE, step-by-step implementation guide for adding full cookie
> support to the Media Downloader (yt-dlp) in OnionMedia-Batch.
> It is written for an AI executor that must follow it LITERALLY.
> All code, comments, UI text and identifiers MUST stay in ENGLISH (project language).
> Do NOT invent new UI styles. Reuse existing components. Keep changes localized.

---

## 0. Project facts you MUST know before editing

- Solution: `e:\Projects\OnionMedia-Batch\OnionMedia.sln`
- Two main projects:
  - `OnionMedia.Core` (cross-platform logic, MVVM view models, models, services interfaces)
    Path: `e:\Projects\OnionMedia-Batch\OnionMedia\OnionMedia.Core\`
  - `OnionMedia` (WinUI 3 UI layer, XAML, service implementations)
    Path: `e:\Projects\OnionMedia-Batch\OnionMedia\OnionMedia\`
- yt-dlp wrapper library: `YoutubeDLSharp` (source in `YoutubeDLSharp-master\`).
  It has NO typed cookie options. Cookies MUST be passed via `OptionSet.AddCustomOption(...)`
  (already used across the project for other flags — this is the sanctioned API, NOT a hack).
- Dependency injection is source-generated via attributes in
  `OnionMedia\OnionMedia\ServiceProvider.cs` (e.g. `[Singleton(typeof(IX), typeof(X))]`).
- Settings persistence: `ISettingsService` wraps `ApplicationData.Current.LocalSettings`.
  Access via the singleton `AppSettings.Instance`. DO NOT create any new settings mechanism.
- Localization: `.resw` files under `OnionMedia\OnionMedia\Strings\<lang>\DownloaderPage.resw`.
  Languages present (ALL must be updated): `en-us`, `de-de`, `es`, `nl`.
  XAML controls localize via `x:Uid="/DownloaderPage/<key>"` which maps to resw entry
  `<key>.Text` / `<key>.Content` / `<key>.ToolTipService.ToolTip` etc.

### yt-dlp cookie facts (verified against official yt-dlp docs)

- Browser cookies: `--cookies-from-browser BROWSER[+KEYRING][:PROFILE][::CONTAINER]`
  Supported BROWSER keys (FIXED set, case-insensitive):
  `brave, chrome, chromium, edge, firefox, opera, safari, vivaldi, whale`
- File cookies: `--cookies FILE` (Netscape/Mozilla format).
- On Windows the Netscape file MUST use CRLF line endings, otherwise yt-dlp
  can throw HTTP 400 Bad Request.
- yt-dlp accepts ONLY ONE cookie source per run. Browser AND file cannot be combined.
- Browser forks (Opera GX, Arc, LibreWolf, Zen, Thorium, Ungoogled Chromium,
  Edge Beta/Dev/Canary, Waterfox, Floorp) do NOT have their own keys — yt-dlp
  reads them through the base key (chrome / firefox / edge). So the menu lists the
  base keys, and mentions forks in parentheses. A "Custom key…" entry lets the user
  type any key manually (future-proofing without hardcoding).

---

## THREE integration points for cookies (ALL must be covered)

1. Metadata fetch: `DownloaderMethods.downloadClient.RunVideoDataFetch(url, ct, flat, overrideOptions)`
   Called in `YouTubeDownloaderViewModel.cs` at lines ~300, ~600, ~801.
   This method ALREADY accepts an `OptionSet overrideOptions` parameter.
2. Download: `OptionSet ytOptions` in `DownloaderMethods.DownloadStreamAsync` (~line 68).
3. Thumbnail: `OptionSet options` in `DownloaderMethods.DownloadThumbnailAsync` (~line 233).

The SAME builder (`CookieOptionsBuilder`) is applied at all three points to avoid duplication.

---

# STEP-02 — Core: create enums

- [ ] Create file: `OnionMedia\OnionMedia.Core\Enums\CookieEnums.cs`

```csharp
/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System.ComponentModel.DataAnnotations;

namespace OnionMedia.Core.Enums
{
    /// <summary>
    /// The source from which cookies are read for yt-dlp.
    /// </summary>
    public enum CookieSource
    {
        None,
        Browser,
        File,
        Pasted
    }

    /// <summary>
    /// Browser keys supported by yt-dlp for --cookies-from-browser.
    /// The Display name shows the base browser plus known forks that share the same key.
    /// The enum NAME (lowercased) is the exact key passed to yt-dlp.
    /// <see cref="Custom"/> means the user typed a custom key (stored separately).
    /// </summary>
    public enum CookieBrowser
    {
        [Display(Name = "Chrome (+ Arc, Thorium, Ungoogled…)")]
        Chrome,
        [Display(Name = "Brave")]
        Brave,
        [Display(Name = "Chromium")]
        Chromium,
        [Display(Name = "Edge (+ Beta, Dev, Canary)")]
        Edge,
        [Display(Name = "Firefox (+ LibreWolf, Waterfox, Floorp, Zen…)")]
        Firefox,
        [Display(Name = "Opera (+ Opera GX)")]
        Opera,
        [Display(Name = "Vivaldi")]
        Vivaldi,
        [Display(Name = "Whale")]
        Whale,
        [Display(Name = "Safari")]
        Safari,
        [Display(Name = "Custom key…")]
        Custom
    }
}
```

- [ ] NOTE: The yt-dlp key for a non-custom browser is `browser.ToString().ToLowerInvariant()`.
      For `Custom`, use the string stored in `AppSettings.CookieCustomBrowserKey`.

---

# STEP-03 — Core: add persisted settings to AppSettings

File: `OnionMedia\OnionMedia.Core\Models\AppSettings.cs`

### 3.1 In the private constructor `AppSettings()`, AFTER the existing `startPageType` block
(right before the closing `}` of the constructor), ADD:

```csharp
            cookieSource = ParseEnum<CookieSource>(settingsService.GetSetting("cookieSource"));
            cookieBrowser = ParseEnum<CookieBrowser>(settingsService.GetSetting("cookieBrowser"));
            cookieCustomBrowserKey = settingsService.GetSetting("cookieCustomBrowserKey") as string ?? string.Empty;
            cookieFilePath = settingsService.GetSetting("cookieFilePath") as string ?? string.Empty;
            pastedCookies = settingsService.GetSetting("pastedCookies") as string ?? string.Empty;
```

### 3.2 Add the public properties (place them near the other settings properties,
e.g. right AFTER the `VideoAddMode` property block and BEFORE the private helper methods
`SetSetting` / `ValidateSettingOrSetToDefault` / `ParseEnum`):

```csharp
        //Cookie settings
        public CookieSource CookieSource
        {
            get => cookieSource;
            set
            {
                if (SetProperty(ref cookieSource, value))
                    settingsService.SetSetting("cookieSource", value.ToString());
            }
        }
        private CookieSource cookieSource;

        public CookieBrowser CookieBrowser
        {
            get => cookieBrowser;
            set
            {
                if (SetProperty(ref cookieBrowser, value))
                    settingsService.SetSetting("cookieBrowser", value.ToString());
            }
        }
        private CookieBrowser cookieBrowser;

        public string CookieCustomBrowserKey
        {
            get => cookieCustomBrowserKey;
            set => SetSetting(ref cookieCustomBrowserKey, value, "cookieCustomBrowserKey");
        }
        private string cookieCustomBrowserKey;

        public string CookieFilePath
        {
            get => cookieFilePath;
            set => SetSetting(ref cookieFilePath, value, "cookieFilePath");
        }
        private string cookieFilePath;

        public string PastedCookies
        {
            get => pastedCookies;
            set => SetSetting(ref pastedCookies, value, "pastedCookies");
        }
        private string pastedCookies;
```

### 3.3 Ensure the `using` list at the top of the file already contains
`using OnionMedia.Core.Enums;` — it does. No change needed.

---

# STEP-04 — Core: create CookieOptionsBuilder

- [ ] Create file: `OnionMedia\OnionMedia.Core\Classes\CookieOptionsBuilder.cs`

Responsibilities:
- `Apply(OptionSet options)` — attach the correct cookie flag(s) to a yt-dlp OptionSet
  according to `AppSettings.Instance`. Returns an `IDisposable` cleanup token that deletes
  any temporary file created for pasted cookies (call `.Dispose()` in a `finally`).
- Parses cookie domains from file / pasted content (for UI display).

```csharp
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
            var settings = AppSettings.Instance;

            switch (settings.CookieSource)
            {
                case CookieSource.Browser:
                    string key = GetBrowserKey();
                    if (!string.IsNullOrWhiteSpace(key))
                        options.AddCustomOption("--cookies-from-browser", key);
                    return NoCleanup;

                case CookieSource.File:
                    if (!string.IsNullOrWhiteSpace(settings.CookieFilePath) && File.Exists(settings.CookieFilePath))
                        options.AddCustomOption("--cookies", settings.CookieFilePath);
                    return NoCleanup;

                case CookieSource.Pasted:
                    if (string.IsNullOrWhiteSpace(settings.PastedCookies))
                        return NoCleanup;
                    string tempFile = CreateTempCookieFile(settings.PastedCookies);
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
            var settings = AppSettings.Instance;
            if (settings.CookieBrowser == CookieBrowser.Custom)
                return (settings.CookieCustomBrowserKey ?? string.Empty).Trim();
            return settings.CookieBrowser.ToString().ToLowerInvariant();
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
```

---

# STEP-05 — Core: apply cookies in DownloaderMethods

File: `OnionMedia\OnionMedia.Core\Classes\DownloaderMethods.cs`

### 5.1 `DownloadStreamAsync`
Locate (near line 68):

```csharp
            OptionSet ytOptions = new() { RestrictFilenames = true };
```

Immediately AFTER that line, ADD:

```csharp
            IDisposable cookieCleanup = CookieOptionsBuilder.Apply(ytOptions);
```

Then find the outer `finally` at the very END of the method. There is a
`finally { stream.Moving = false; }` at the end. CHANGE it to also dispose the cleanup:

```csharp
            finally
            {
                stream.Moving = false;
                cookieCleanup.Dispose();
            }
```

IMPORTANT: `cookieCleanup` is declared before the first `try`. If the method has an
earlier `try/catch` that can `return` early (the big catch block after the download),
those early returns would skip the final `finally`. To be safe, the SAME
`cookieCleanup.Dispose();` must ALSO be reachable on every early `return` path.
The cleanest approach: wrap the WHOLE body after the declaration in a single
`try { ... } finally { cookieCleanup.Dispose(); }`. If that is too invasive, instead
call `CookieOptionsBuilder.Apply` returning a token and dispose it inside EACH early
`return` branch of the first catch block. Prefer the outer try/finally wrapper.

RECOMMENDED minimal-risk edit:
- Declare `IDisposable cookieCleanup = CookieOptionsBuilder.Apply(ytOptions);` right after
  the OptionSet creation.
- Because the existing early `return`s live inside a `catch` block, ADD `cookieCleanup.Dispose();`
  as the first statement inside each of those `return` branches is error-prone.
  Instead, change the last `finally { stream.Moving = false; }` to dispose the token AND
  move nothing else. The early returns inside the first catch happen BEFORE the tag-saving
  try, but they are still inside the method, so the final `finally` DOES run for them only
  if they are inside a try that the finally covers. They are NOT.
  Therefore: also add `cookieCleanup.Dispose();` right before each `return;` inside the
  FIRST catch block (the one with `default`, `TaskCanceledException`, `HttpRequestException`,
  `SecurityException`, `IOException`). For the `IOException` branch that does `throw;`,
  add `cookieCleanup.Dispose();` before the `NotEnoughSpaceException.ThrowIfNotEnoughSpace(...)`
  line as well.

> Executor note: The pasted-cookie temp file is tiny; disposing it multiple times is safe
> (CleanupToken guards with File.Exists). If in doubt, add `cookieCleanup.Dispose();`
> defensively before every `return;`/`throw;` and in the final `finally`. Double-dispose
> is harmless here.

### 5.2 `DownloadThumbnailAsync`
Locate (near line 233):

```csharp
            OptionSet options = new() { WriteThumbnail = true, SkipDownload = true, Output = filePath };
            options.AddCustomOption("--convert-thumbnails", imageFormat);
            options.AddCustomOption("--ppa", "ThumbnailsConvertor:-q:v 1");
            string newFilename = $"{filePath}.{imageFormat}";
            await downloadClient.RunWithOptions(new[] { videoUrl }, options, ct);
```

CHANGE to:

```csharp
            OptionSet options = new() { WriteThumbnail = true, SkipDownload = true, Output = filePath };
            options.AddCustomOption("--convert-thumbnails", imageFormat);
            options.AddCustomOption("--ppa", "ThumbnailsConvertor:-q:v 1");
            using var cookieCleanup = CookieOptionsBuilder.Apply(options);
            string newFilename = $"{filePath}.{imageFormat}";
            await downloadClient.RunWithOptions(new[] { videoUrl }, options, ct);
```

(The `using var` disposes automatically at method end.)

---

# STEP-06 — Core: apply cookies to metadata fetch (RunVideoDataFetch)

File: `OnionMedia\OnionMedia.Core\ViewModels\YouTubeDownloaderViewModel.cs`

There are THREE `RunVideoDataFetch` calls. Add a small private helper and use it.

### 6.1 Add helper method inside the class (place it near other private helpers,
e.g. just above `DownloadVideosAsync`):

```csharp
        /// <summary>
        /// Builds an OptionSet carrying the current cookie configuration for metadata fetches.
        /// Returns (options, cleanup). Always dispose the cleanup token after the fetch.
        /// </summary>
        private static (YoutubeDLSharp.Options.OptionSet options, System.IDisposable cleanup) BuildCookieFetchOptions()
        {
            var options = new YoutubeDLSharp.Options.OptionSet();
            var cleanup = OnionMedia.Core.Classes.CookieOptionsBuilder.Apply(options);
            return (options, cleanup);
        }
```

### 6.2 Update the three call sites.

Call site A (~line 300):
```csharp
                var data = await DownloaderMethods.downloadClient.RunVideoDataFetch(videolink);
```
CHANGE to:
```csharp
                var (cookieOpts, cookieCleanup) = BuildCookieFetchOptions();
                RunResult<VideoData> data;
                try { data = await DownloaderMethods.downloadClient.RunVideoDataFetch(videolink, overrideOptions: cookieOpts); }
                finally { cookieCleanup.Dispose(); }
```
> If the surrounding code already declares `var data = ...` and uses `data` later, keep the
> variable name `data`. Ensure the type is `RunResult<VideoData>` (it already is used that way).
> If the local `data` was `var`, replace with explicit `RunResult<VideoData> data;` declared
> before the try as shown.

Call site B (~line 600):
```csharp
                                data = await DownloaderMethods.downloadClient.RunVideoDataFetch(url);
```
CHANGE to:
```csharp
                                var (cookieOptsB, cookieCleanupB) = BuildCookieFetchOptions();
                                try { data = await DownloaderMethods.downloadClient.RunVideoDataFetch(url, overrideOptions: cookieOptsB); }
                                finally { cookieCleanupB.Dispose(); }
```

Call site C (~line 801):
```csharp
                        var video = await DownloaderMethods.downloadClient.RunVideoDataFetch(url, ct: cToken);
```
CHANGE to:
```csharp
                        var (cookieOptsC, cookieCleanupC) = BuildCookieFetchOptions();
                        RunResult<VideoData> video;
                        try { video = await DownloaderMethods.downloadClient.RunVideoDataFetch(url, ct: cToken, overrideOptions: cookieOptsC); }
                        finally { cookieCleanupC.Dispose(); }
```

> The file already has `using YoutubeDLSharp;` and `using YoutubeDLSharp.Metadata;` so
> `RunResult<VideoData>` compiles. If `OptionSet` is not imported, the fully-qualified
> `YoutubeDLSharp.Options.OptionSet` in the helper avoids needing a new using.

---

# STEP-07 — Core: ICookieViewerDialog interface

- [ ] Create file: `OnionMedia\OnionMedia.Core\Services\ICookieViewerDialog.cs`

```csharp
/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;

namespace OnionMedia.Core.Services
{
    /// <summary>
    /// Shows a read-only viewer for cookie content with select-all / copy support.
    /// </summary>
    public interface ICookieViewerDialog
    {
        /// <param name="title">Dialog title.</param>
        /// <param name="content">The cookie text to display (read-only).</param>
        Task ShowCookiesAsync(string title, string content);
    }
}
```

---

# STEP-08 — Core: ViewModel cookie state and commands

File: `OnionMedia\OnionMedia.Core\ViewModels\YouTubeDownloaderViewModel.cs`

### 8.1 Constructor dependency
The class uses constructor injection. ADD a new parameter `ICookieViewerDialog cookieViewerDialog`
to the constructor signature and store it:

- In the constructor parameter list, append `, ICookieViewerDialog cookieViewerDialog`.
- Add field: `private readonly ICookieViewerDialog cookieViewerDialog;`
- In the constructor body add:
  `this.cookieViewerDialog = cookieViewerDialog ?? throw new ArgumentNullException(nameof(cookieViewerDialog));`

> The `using OnionMedia.Core.Services;` is already present.

### 8.2 Add cookie UI state + commands. Place this region near the other `[ICommand]` methods.

```csharp
        // ===== Cookie support =====

        public static CookieBrowser[] CookieBrowsers { get; } = Enum.GetValues<CookieBrowser>().ToArray();

        /// <summary>True when any cookie source is active (used to highlight the button icon).</summary>
        public bool CookiesEnabled => AppSettings.Instance.CookieSource != CookieSource.None;

        /// <summary>Short one-line status, e.g. "Cookies: Disabled", "Browser: Firefox".</summary>
        public string CurrentCookieStatusText
        {
            get
            {
                var s = AppSettings.Instance;
                switch (s.CookieSource)
                {
                    case CookieSource.Browser:
                        string key = CookieOptionsBuilder.GetBrowserKey();
                        return string.IsNullOrWhiteSpace(key)
                            ? "cookieStatusBrowser".GetLocalized("DownloaderPage")
                            : $"{"cookieStatusBrowser".GetLocalized("DownloaderPage")}: {key}";
                    case CookieSource.File:
                        return "cookieStatusFile".GetLocalized("DownloaderPage");
                    case CookieSource.Pasted:
                        return "cookieStatusPasted".GetLocalized("DownloaderPage");
                    default:
                        return "cookieStatusDisabled".GetLocalized("DownloaderPage");
                }
            }
        }

        /// <summary>Multi-line details (source / browser / file / domains) for the flyout.</summary>
        public string CurrentCookieDetails
        {
            get
            {
                var s = AppSettings.Instance;
                var sb = new StringBuilder();
                switch (s.CookieSource)
                {
                    case CookieSource.Browser:
                        sb.AppendLine($"Source: Browser");
                        sb.AppendLine($"Browser: {CookieOptionsBuilder.GetBrowserKey()}");
                        break;
                    case CookieSource.File:
                        sb.AppendLine($"Source: File");
                        sb.AppendLine($"File: {s.CookieFilePath}");
                        AppendDomains(sb, SafeReadFile(s.CookieFilePath));
                        break;
                    case CookieSource.Pasted:
                        sb.AppendLine($"Source: Pasted");
                        AppendDomains(sb, s.PastedCookies);
                        break;
                    default:
                        sb.Append("cookieStatusDisabled".GetLocalized("DownloaderPage"));
                        break;
                }
                return sb.ToString().TrimEnd();
            }
        }

        private static void AppendDomains(StringBuilder sb, string netscapeContent)
        {
            var domains = CookieOptionsBuilder.ParseDomains(netscapeContent);
            if (domains.Count > 0)
                sb.AppendLine($"Domain(s): {string.Join(", ", domains)}");
        }

        private static string SafeReadFile(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
            catch { return string.Empty; }
        }

        private void RaiseCookieStatusChanged()
        {
            OnPropertyChanged(nameof(CookiesEnabled));
            OnPropertyChanged(nameof(CurrentCookieStatusText));
            OnPropertyChanged(nameof(CurrentCookieDetails));
        }

        [ICommand]
        private void DisableCookies()
        {
            AppSettings.Instance.CookieSource = CookieSource.None;
            RaiseCookieStatusChanged();
        }

        [ICommand]
        private void UseBrowserCookies(string browserName)
        {
            if (!Enum.TryParse<CookieBrowser>(browserName, out var browser)) return;

            if (browser == CookieBrowser.Custom)
            {
                // Custom key is set via UseCustomBrowserKey; just switch mode if a key already exists.
                if (string.IsNullOrWhiteSpace(AppSettings.Instance.CookieCustomBrowserKey)) return;
            }
            AppSettings.Instance.CookieBrowser = browser;
            AppSettings.Instance.CookieSource = CookieSource.Browser;
            RaiseCookieStatusChanged();
        }

        [ICommand]
        private async Task UseCustomBrowserKey()
        {
            // Reuse the existing paste dialog pattern: ask via an interaction dialog is not enough
            // for text input, so we accept the value from clipboard OR a simple input dialog.
            // Simplest robust approach: use the clipboard if it holds a short token, otherwise
            // show an info dialog telling the user to type the key in settings is NOT desired.
            // Instead we open a lightweight text input via dialogService is unavailable, so:
            // Read from clipboard as the custom key source.
            var key = await ClipboardService.GetTextAsync();
            if (string.IsNullOrWhiteSpace(key)) return;
            key = key.Trim();
            AppSettings.Instance.CookieCustomBrowserKey = key;
            AppSettings.Instance.CookieBrowser = CookieBrowser.Custom;
            AppSettings.Instance.CookieSource = CookieSource.Browser;
            RaiseCookieStatusChanged();
        }

        [ICommand]
        private async Task UseCookieFile()
        {
            var path = await dialogService.ShowSingleFilePickerDialogAsync(DirectoryLocation.Documents);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            AppSettings.Instance.CookieFilePath = path;
            AppSettings.Instance.CookieSource = CookieSource.File;
            RaiseCookieStatusChanged();
        }

        [ICommand]
        private async Task PasteCookies()
        {
            var text = await ClipboardService.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;
            AppSettings.Instance.PastedCookies = text;
            AppSettings.Instance.CookieSource = CookieSource.Pasted;
            RaiseCookieStatusChanged();
        }

        [ICommand]
        private async Task OpenCookieViewer()
        {
            var s = AppSettings.Instance;
            string content = s.CookieSource switch
            {
                CookieSource.File => SafeReadFile(s.CookieFilePath),
                CookieSource.Pasted => s.PastedCookies,
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(content)) return;
            await cookieViewerDialog.ShowCookiesAsync(
                "cookieViewerTitle".GetLocalized("DownloaderPage"), content);
        }
```

### 8.3 Required usings in this file
Ensure these usings exist at the top (most already do — add only the missing ones):
```csharp
using OnionMedia.Core.Enums;   // CookieSource, CookieBrowser (already present)
using OnionMedia.Core.Classes; // CookieOptionsBuilder (already present)
using OnionMedia.Core.Services;// ICookieViewerDialog, DirectoryLocation (already present)
using System.Text;             // StringBuilder (already present)
using System.IO;               // File (already present)
using TextCopy;                // ClipboardService (already present)
```
`.GetLocalized(...)` comes from `OnionMedia.Core.Extensions.StringExtensions` — verify that
`using OnionMedia.Core.Extensions;` is present (it is used already for other strings; if the
`GetLocalized(this string, string resourceFile)` overload does not exist, check
`StringExtensions.cs` for the correct signature and adapt the calls to match it exactly).

> IMPORTANT: Before using `.GetLocalized("DownloaderPage")`, OPEN
> `OnionMedia\OnionMedia.Core\Extensions\StringExtensions.cs` and confirm the exact
> `GetLocalized` overloads. Use the same overload other code in the ViewModel already uses.
> If the existing code calls `.GetLocalized()` with no args for DownloaderPage keys, then
> keys resolve against a default resource — match that convention instead.

---

# STEP-09 — UI: CookieViewerDialog + service + DI

### 9.1 Create `OnionMedia\OnionMedia\Views\Dialogs\CookieViewerDialog.xaml`

Model it on the existing dialogs in the same folder (e.g. PlaylistSelectorDialog.xaml).
Use ONLY existing styles/controls. Keep it simple and read-only.

```xml
<?xml version="1.0" encoding="utf-8"?>
<ContentDialog
    x:Class="OnionMedia.Views.Dialogs.CookieViewerDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d"
    x:Uid="/DownloaderPage/cookieViewerDialog"
    PrimaryButtonText="Copy"
    CloseButtonText="Close"
    DefaultButton="Primary"
    PrimaryButtonClick="OnCopyClick">
    <Grid MinWidth="480" MinHeight="360">
        <TextBox x:Name="cookieTextBox"
                 IsReadOnly="True"
                 AcceptsReturn="True"
                 TextWrapping="NoWrap"
                 FontFamily="Consolas"
                 ScrollViewer.HorizontalScrollBarVisibility="Auto"
                 ScrollViewer.VerticalScrollBarVisibility="Auto"
                 Height="360"/>
    </Grid>
</ContentDialog>
```

### 9.2 Create `OnionMedia\OnionMedia\Views\Dialogs\CookieViewerDialog.xaml.cs`

```csharp
/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.UI.Xaml.Controls;
using TextCopy;

namespace OnionMedia.Views.Dialogs
{
    public sealed partial class CookieViewerDialog : ContentDialog
    {
        public CookieViewerDialog(string title, string content)
        {
            InitializeComponent();
            Title = title;
            cookieTextBox.Text = content ?? string.Empty;
        }

        private void OnCopyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Prevent the dialog from closing on Copy so the user can copy and keep viewing.
            args.Cancel = true;
            cookieTextBox.SelectAll();
            ClipboardService.SetText(cookieTextBox.Text ?? string.Empty);
        }
    }
}
```

### 9.3 Create the service implementation
`OnionMedia\OnionMedia\Services\CookieViewerDialogService.cs`

```csharp
/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;
using OnionMedia.Core.Services;
using OnionMedia.Views.Dialogs;

namespace OnionMedia.Services
{
    sealed class CookieViewerDialogService : ICookieViewerDialog
    {
        public async Task ShowCookiesAsync(string title, string content)
        {
            var dlg = new CookieViewerDialog(title, content) { XamlRoot = UIResources.XamlRoot };
            await dlg.ShowAsync();
        }
    }
}
```

> `UIResources.XamlRoot` is the same pattern used by DownloaderDialogService. Confirm it exists
> in `OnionMedia\OnionMedia\UIResources.cs`. If the property name differs, use the exact one
> used by the other dialog services in `OnionMedia\OnionMedia\Services\`.

### 9.4 Register in DI
File: `OnionMedia\OnionMedia\ServiceProvider.cs`
Add near the other dialog registrations (after line with `IDownloaderDialogService`):

```csharp
[Singleton(typeof(ICookieViewerDialog), typeof(CookieViewerDialogService))]
```

---

# STEP-10 — UI: "Use Cookies" button + menu on the downloader page

File: `OnionMedia\OnionMedia\Views\YouTubeDownloaderPage.xaml`

The existing bottom-right button (reference to mirror):
```xml
        <Button VerticalAlignment="Bottom"
                HorizontalAlignment="Right"
                Margin="20"
                Click="OpenVideoFolder_Click"
                Width="45" Height="40"
                ToolTipService.ToolTip="Open Video Folder">
            <SymbolIcon Symbol="OpenLocal"/>
        </Button>
```

ADD a mirrored button in the BOTTOM-LEFT corner. Insert it right BEFORE that existing
button (still inside the root `<Grid x:Name="mainFrame">`, as a sibling), so both sit
at the bottom, symmetrical:

```xml
        <!-- Cookie source button (bottom-left, mirrors "Open Video Folder" bottom-right) -->
        <Button VerticalAlignment="Bottom"
                HorizontalAlignment="Left"
                Margin="20"
                Height="40"
                x:Uid="/DownloaderPage/cookieButton"
                ToolTipService.ToolTip="Use Cookies">
            <StackPanel Orientation="Horizontal" Spacing="6">
                <!-- Cookie glyph from Segoe Fluent Icons. If unavailable, fallback below. -->
                <FontIcon FontFamily="{StaticResource SymbolThemeFontFamily}"
                          Glyph="&#xE7BA;"
                          Foreground="{ThemeResource SystemAccentColorBrush}"
                          Visibility="{x:Bind ViewModel.CookiesEnabled, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}"/>
                <FontIcon FontFamily="{StaticResource SymbolThemeFontFamily}"
                          Glyph="&#xE7BA;"
                          Visibility="{x:Bind ViewModel.CookiesEnabled, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=True}"/>
                <TextBlock Text="{x:Bind ViewModel.CurrentCookieStatusText, Mode=OneWay}" VerticalAlignment="Center"/>
            </StackPanel>
            <Button.Flyout>
                <MenuFlyout Placement="Top">
                    <MenuFlyoutItem Text="Don't use cookies"
                                    x:Uid="/DownloaderPage/cookieMenuNone"
                                    Command="{x:Bind ViewModel.DisableCookiesCommand}">
                        <MenuFlyoutItem.Icon><SymbolIcon Symbol="Cancel"/></MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutSubItem Text="Browser cookies" x:Uid="/DownloaderPage/cookieMenuBrowser">
                        <MenuFlyoutSubItem.Icon><SymbolIcon Symbol="World"/></MenuFlyoutSubItem.Icon>
                        <MenuFlyoutItem Text="Chrome"   Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Chrome"/>
                        <MenuFlyoutItem Text="Brave"    Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Brave"/>
                        <MenuFlyoutItem Text="Chromium" Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Chromium"/>
                        <MenuFlyoutItem Text="Edge"     Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Edge"/>
                        <MenuFlyoutItem Text="Firefox"  Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Firefox"/>
                        <MenuFlyoutItem Text="Opera"    Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Opera"/>
                        <MenuFlyoutItem Text="Vivaldi"  Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Vivaldi"/>
                        <MenuFlyoutItem Text="Whale"    Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Whale"/>
                        <MenuFlyoutItem Text="Safari"   Command="{x:Bind ViewModel.UseBrowserCookiesCommand}" CommandParameter="Safari"/>
                        <MenuFlyoutSeparator/>
                        <MenuFlyoutItem Text="Custom key (from clipboard)…"
                                        x:Uid="/DownloaderPage/cookieMenuCustomKey"
                                        Command="{x:Bind ViewModel.UseCustomBrowserKeyCommand}"/>
                    </MenuFlyoutSubItem>
                    <MenuFlyoutItem Text="Load cookies from file…"
                                    x:Uid="/DownloaderPage/cookieMenuFile"
                                    Command="{x:Bind ViewModel.UseCookieFileCommand}">
                        <MenuFlyoutItem.Icon><SymbolIcon Symbol="OpenFile"/></MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Text="Paste cookies (from clipboard)"
                                    x:Uid="/DownloaderPage/cookieMenuPaste"
                                    Command="{x:Bind ViewModel.PasteCookiesCommand}">
                        <MenuFlyoutItem.Icon><SymbolIcon Symbol="Paste"/></MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutSeparator/>
                    <MenuFlyoutItem Text="View cookies…"
                                    x:Uid="/DownloaderPage/cookieMenuView"
                                    Command="{x:Bind ViewModel.OpenCookieViewerCommand}">
                        <MenuFlyoutItem.Icon><SymbolIcon Symbol="View"/></MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                </MenuFlyout>
            </Button.Flyout>
        </Button>
```

> Notes for the executor:
> - `SystemAccentColorBrush` may not exist as a ThemeResource key in this project. If XAML
>   fails to compile, replace `{ThemeResource SystemAccentColorBrush}` with
>   `{ThemeResource SystemControlHighlightAccentBrush}` or a `SolidColorBrush` built from
>   `{ThemeResource SystemAccentColor}`. Check how the donation `FontIcon` colors itself —
>   it uses `Foreground="{ThemeResource SystemAccentColor}"`; mirror THAT exact key.
> - The cookie glyph `&#xE7BA;` is a placeholder. Pick a real cookie/permission-like glyph
>   available in `SymbolThemeFontFamily`. If none fits, keep a neutral glyph and rely on the
>   accent color + status text for the "cookies active" indication.
> - Do NOT add code-behind for this button; it is fully data-bound via commands.
> - After editing XAML, the `x:Bind` requires the new ViewModel members to be `public`.
>   Generated command names: an `[ICommand] void DisableCookies()` yields `DisableCookiesCommand`,
>   `[ICommand] void UseBrowserCookies(string)` yields `UseBrowserCookiesCommand`, etc.

---

# STEP-11 — Localization (ALL 4 languages: en-us, de-de, es, nl)

For EACH language folder under `OnionMedia\OnionMedia\Strings\<lang>\DownloaderPage.resw`
add the following `<data>` entries INSIDE the root `<root>` element, next to the other
`<data>` entries (before the closing `</root>`). Keep the exact `name` keys; translate only
the `<value>`.

Keys to add (property suffix matters: `.Text` for TextBlock/MenuFlyoutItem,
`.ToolTipService.ToolTip` for the button tooltip, `.Title`/`.PrimaryButtonText`/`.CloseButtonText`
for the dialog):

Shared key list:
- `cookieButton.ToolTipService.ToolTip`
- `cookieMenuNone.Text`
- `cookieMenuBrowser.Text`
- `cookieMenuCustomKey.Text`
- `cookieMenuFile.Text`
- `cookieMenuPaste.Text`
- `cookieMenuView.Text`
- `cookieViewerDialog.Title`
- `cookieViewerDialog.PrimaryButtonText`
- `cookieViewerDialog.CloseButtonText`
- `cookieStatusDisabled` (plain string, used from code via GetLocalized)
- `cookieStatusBrowser`
- `cookieStatusFile`
- `cookieStatusPasted`
- `cookieViewerTitle`

### 11.1 en-us (English) — `Strings\en-us\DownloaderPage.resw`

```xml
  <data name="cookieButton.ToolTipService.ToolTip" xml:space="preserve"><value>Use cookies</value></data>
  <data name="cookieMenuNone.Text" xml:space="preserve"><value>Don't use cookies</value></data>
  <data name="cookieMenuBrowser.Text" xml:space="preserve"><value>Browser cookies</value></data>
  <data name="cookieMenuCustomKey.Text" xml:space="preserve"><value>Custom key (from clipboard)…</value></data>
  <data name="cookieMenuFile.Text" xml:space="preserve"><value>Load cookies from file…</value></data>
  <data name="cookieMenuPaste.Text" xml:space="preserve"><value>Paste cookies (from clipboard)</value></data>
  <data name="cookieMenuView.Text" xml:space="preserve"><value>View cookies…</value></data>
  <data name="cookieViewerDialog.Title" xml:space="preserve"><value>Cookies</value></data>
  <data name="cookieViewerDialog.PrimaryButtonText" xml:space="preserve"><value>Copy</value></data>
  <data name="cookieViewerDialog.CloseButtonText" xml:space="preserve"><value>Close</value></data>
  <data name="cookieStatusDisabled" xml:space="preserve"><value>Cookies: Disabled</value></data>
  <data name="cookieStatusBrowser" xml:space="preserve"><value>Browser</value></data>
  <data name="cookieStatusFile" xml:space="preserve"><value>Cookies file selected</value></data>
  <data name="cookieStatusPasted" xml:space="preserve"><value>Pasted cookies</value></data>
  <data name="cookieViewerTitle" xml:space="preserve"><value>Cookies</value></data>
```

### 11.2 de-de (German) — `Strings\de-de\DownloaderPage.resw`

```xml
  <data name="cookieButton.ToolTipService.ToolTip" xml:space="preserve"><value>Cookies verwenden</value></data>
  <data name="cookieMenuNone.Text" xml:space="preserve"><value>Keine Cookies verwenden</value></data>
  <data name="cookieMenuBrowser.Text" xml:space="preserve"><value>Browser-Cookies</value></data>
  <data name="cookieMenuCustomKey.Text" xml:space="preserve"><value>Eigener Schlüssel (aus Zwischenablage)…</value></data>
  <data name="cookieMenuFile.Text" xml:space="preserve"><value>Cookies aus Datei laden…</value></data>
  <data name="cookieMenuPaste.Text" xml:space="preserve"><value>Cookies einfügen (aus Zwischenablage)</value></data>
  <data name="cookieMenuView.Text" xml:space="preserve"><value>Cookies anzeigen…</value></data>
  <data name="cookieViewerDialog.Title" xml:space="preserve"><value>Cookies</value></data>
  <data name="cookieViewerDialog.PrimaryButtonText" xml:space="preserve"><value>Kopieren</value></data>
  <data name="cookieViewerDialog.CloseButtonText" xml:space="preserve"><value>Schließen</value></data>
  <data name="cookieStatusDisabled" xml:space="preserve"><value>Cookies: Deaktiviert</value></data>
  <data name="cookieStatusBrowser" xml:space="preserve"><value>Browser</value></data>
  <data name="cookieStatusFile" xml:space="preserve"><value>Cookie-Datei ausgewählt</value></data>
  <data name="cookieStatusPasted" xml:space="preserve"><value>Eingefügte Cookies</value></data>
  <data name="cookieViewerTitle" xml:space="preserve"><value>Cookies</value></data>
```

### 11.3 es (Spanish) — `Strings\es\DownloaderPage.resw`

```xml
  <data name="cookieButton.ToolTipService.ToolTip" xml:space="preserve"><value>Usar cookies</value></data>
  <data name="cookieMenuNone.Text" xml:space="preserve"><value>No usar cookies</value></data>
  <data name="cookieMenuBrowser.Text" xml:space="preserve"><value>Cookies del navegador</value></data>
  <data name="cookieMenuCustomKey.Text" xml:space="preserve"><value>Clave personalizada (desde el portapapeles)…</value></data>
  <data name="cookieMenuFile.Text" xml:space="preserve"><value>Cargar cookies desde archivo…</value></data>
  <data name="cookieMenuPaste.Text" xml:space="preserve"><value>Pegar cookies (desde el portapapeles)</value></data>
  <data name="cookieMenuView.Text" xml:space="preserve"><value>Ver cookies…</value></data>
  <data name="cookieViewerDialog.Title" xml:space="preserve"><value>Cookies</value></data>
  <data name="cookieViewerDialog.PrimaryButtonText" xml:space="preserve"><value>Copiar</value></data>
  <data name="cookieViewerDialog.CloseButtonText" xml:space="preserve"><value>Cerrar</value></data>
  <data name="cookieStatusDisabled" xml:space="preserve"><value>Cookies: Desactivadas</value></data>
  <data name="cookieStatusBrowser" xml:space="preserve"><value>Navegador</value></data>
  <data name="cookieStatusFile" xml:space="preserve"><value>Archivo de cookies seleccionado</value></data>
  <data name="cookieStatusPasted" xml:space="preserve"><value>Cookies pegadas</value></data>
  <data name="cookieViewerTitle" xml:space="preserve"><value>Cookies</value></data>
```

### 11.4 nl (Dutch) — `Strings\nl\DownloaderPage.resw`

```xml
  <data name="cookieButton.ToolTipService.ToolTip" xml:space="preserve"><value>Cookies gebruiken</value></data>
  <data name="cookieMenuNone.Text" xml:space="preserve"><value>Geen cookies gebruiken</value></data>
  <data name="cookieMenuBrowser.Text" xml:space="preserve"><value>Browsercookies</value></data>
  <data name="cookieMenuCustomKey.Text" xml:space="preserve"><value>Aangepaste sleutel (uit klembord)…</value></data>
  <data name="cookieMenuFile.Text" xml:space="preserve"><value>Cookies uit bestand laden…</value></data>
  <data name="cookieMenuPaste.Text" xml:space="preserve"><value>Cookies plakken (uit klembord)</value></data>
  <data name="cookieMenuView.Text" xml:space="preserve"><value>Cookies weergeven…</value></data>
  <data name="cookieViewerDialog.Title" xml:space="preserve"><value>Cookies</value></data>
  <data name="cookieViewerDialog.PrimaryButtonText" xml:space="preserve"><value>Kopiëren</value></data>
  <data name="cookieViewerDialog.CloseButtonText" xml:space="preserve"><value>Sluiten</value></data>
  <data name="cookieStatusDisabled" xml:space="preserve"><value>Cookies: Uitgeschakeld</value></data>
  <data name="cookieStatusBrowser" xml:space="preserve"><value>Browser</value></data>
  <data name="cookieStatusFile" xml:space="preserve"><value>Cookiebestand geselecteerd</value></data>
  <data name="cookieStatusPasted" xml:space="preserve"><value>Geplakte cookies</value></data>
  <data name="cookieViewerTitle" xml:space="preserve"><value>Cookies</value></data>
```

> LOCALIZATION RULES:
> - Insert each block just before `</root>` and after the last existing `<data>` element.
> - Do NOT duplicate keys. If a key already exists, skip it.
> - Keep the XML well-formed. The `…` (U+2026) character is fine inside resw values.
> - If the dialog uses `x:Uid="/DownloaderPage/cookieViewerDialog"`, the `.Title`,
>   `.PrimaryButtonText`, `.CloseButtonText` from resw OVERRIDE the values set in code-behind.
>   That is intended. The code-behind `Title = title;` is a fallback and can stay.
> - Confirm the resource file base name used by `GetLocalized` in code. If existing code calls
>   `"key".GetLocalized("DownloaderPage")`, the plain-string keys (cookieStatus*) must live in
>   `DownloaderPage.resw`. If the convention is different, place them accordingly.

---

# STEP-12 — Build & verify

### 12.1 Verify the exact `GetLocalized` signature FIRST
Open `OnionMedia\OnionMedia.Core\Extensions\StringExtensions.cs`. Find the `GetLocalized`
method(s). Match every `.GetLocalized(...)` call in the ViewModel to an existing overload.
If only `GetLocalized(this string)` exists (no resource-file argument), then:
- Either add the cookie status keys to the DEFAULT resource file that overload targets, OR
- Adjust the calls to the correct overload. Do NOT invent a new overload unless necessary.

### 12.2 Build
Run from `e:\Projects\OnionMedia-Batch`:
```
dotnet build OnionMedia.sln -c Debug
```
Fix compile errors in this priority order:
1. Missing usings (add them).
2. Wrong `GetLocalized` overload (adapt calls).
3. `x:Bind` command name mismatches (command is `<MethodName>Command`).
4. ThemeResource key not found (use the key the donation banner uses).

> If the project cannot build on this machine due to Windows/WinUI SDK constraints,
> at minimum ensure `OnionMedia.Core` compiles (it is the platform-independent part):
> ```
> dotnet build OnionMedia\OnionMedia.Core\OnionMedia.Core.csproj -c Debug
> ```

### 12.3 Manual test checklist (run the app)
- [ ] Cookies disabled → yt-dlp command contains NO `--cookies*` arguments (unchanged behavior).
- [ ] Browser cookies: each of chrome/brave/chromium/edge/firefox/opera/vivaldi/whale/safari
      produces `--cookies-from-browser <key>`.
- [ ] Custom key: copy a key to clipboard, choose "Custom key" → `--cookies-from-browser <thatkey>`.
- [ ] File: pick a cookies.txt → `--cookies <path>`; status shows "Cookies file selected".
- [ ] Pasted: paste Netscape text → a temp file is created with CRLF + Netscape header,
      `--cookies <temp>` is used, and the temp file is DELETED after the run.
- [ ] Cookie viewer opens for File and Pasted modes; Select-all + Copy works.
- [ ] Restart the app: the selected mode/browser/file/pasted content persist.
- [ ] Icon is accent-colored when cookies are active, neutral when disabled.
- [ ] Status text always reflects the active mode.
- [ ] Domains shown for file/pasted when parseable; nothing invented otherwise.
- [ ] Dark and light themes both look correct.
- [ ] Invalid cookie file / missing browser: yt-dlp fails gracefully; existing error handling
      (retry/log) still works; app does not crash.
- [ ] Layout: the cookie button in the bottom-left is symmetric with the bottom-right button.

---

# Known limitations (document in the final report)

- yt-dlp accepts only ONE cookie source per run. Combining multiple browsers/files in a single
  download is NOT possible (yt-dlp constraint). The UI enforces a single active source.
- Browser forks (Opera GX, Arc, LibreWolf, Zen, Thorium, Ungoogled Chromium, Edge Beta/Dev/Canary,
  Waterfox, Floorp) are read through their base key (chrome/firefox/edge). This is by design.
- The browser used to export a cookies FILE cannot be reliably detected, so it is not shown.
- Pasted cookies are stored in LocalSettings in plain text (consistent with how the app stores
  all other settings). This matches the existing settings architecture.

---

# Execution order (do NOT reorder)

1. STEP-02  Enums
2. STEP-03  AppSettings
3. STEP-04  CookieOptionsBuilder
4. STEP-07  ICookieViewerDialog (interface only; needed before ViewModel compiles)
5. STEP-05  DownloaderMethods integration
6. STEP-06  RunVideoDataFetch integration
7. STEP-08  ViewModel state + commands
8. STEP-09  Dialog + service + DI registration
9. STEP-10  XAML button + menu
10. STEP-11 Localization (all 4 languages)
11. STEP-12 Build & verify

After EACH step: save files, and after STEP-12 run the full build and checklist.
Report what changed, which files were touched, reused components, and any limitations.

---

## Files created (summary)
- `OnionMedia.Core\Enums\CookieEnums.cs`
- `OnionMedia.Core\Classes\CookieOptionsBuilder.cs`
- `OnionMedia.Core\Services\ICookieViewerDialog.cs`
- `OnionMedia\Views\Dialogs\CookieViewerDialog.xaml` (+ `.xaml.cs`)
- `OnionMedia\Services\CookieViewerDialogService.cs`

## Files modified (summary)
- `OnionMedia.Core\Models\AppSettings.cs`
- `OnionMedia.Core\Classes\DownloaderMethods.cs`
- `OnionMedia.Core\ViewModels\YouTubeDownloaderViewModel.cs`
- `OnionMedia\ServiceProvider.cs`
- `OnionMedia\Views\YouTubeDownloaderPage.xaml`
- `OnionMedia\Strings\en-us\DownloaderPage.resw`
- `OnionMedia\Strings\de-de\DownloaderPage.resw`
- `OnionMedia\Strings\es\DownloaderPage.resw`
- `OnionMedia\Strings\nl\DownloaderPage.resw`
- `setup_dependencies.ps1` (now always pulls the latest yt-dlp)

---
---

# APPENDIX A — Release Build Guide (GitHub, unsigned MSIX)

> This section is for the human maintainer (you). It explains how to build the release
> ZIP exactly like the previous releases (the `install.bat` + MSIX certificate flow).
> The package is signed with a **temporary self-signed certificate**; the end user
> "enters" / installs that certificate in the console via `install.bat`.

## A.0 Prerequisites (once)
- Windows 10/11 x64.
- Visual Studio 2022 with workloads:
  - ".NET Desktop Development"
  - "Universal Windows Platform development" (for MSIX / Windows App SDK packaging).
- Windows SDK 10.0.19041.0 (matches `TargetPlatformVersion` in the wapproj).
- .NET 6 SDK.

## A.1 Update dependencies (FFmpeg + latest yt-dlp)
Open PowerShell in the repo root `e:\Projects\OnionMedia-Batch` and run:
```powershell
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "& '.\setup_dependencies.ps1'"
```
This now ALWAYS downloads the latest `yt-dlp.exe` (plus FFmpeg/ffprobe/deno if missing)
into `OnionMedia\OnionMedia\ExternalBinaries\ffmpeg+yt-dlp\binaries`.

Verify the yt-dlp version that got bundled:
```powershell
& ".\OnionMedia\OnionMedia\ExternalBinaries\ffmpeg+yt-dlp\binaries\yt-dlp.exe" --version
```
Write down that version string — you will put it in the changelog (e.g. `2026.03.17`).

## A.2 Bump the app version
Edit `OnionMedia\OnionMedia (Package)\Package.appxmanifest`:
- Change `<Identity ... Version="1.2.24.0" />` to the new version, e.g. `Version="1.2.25.0"`.
  (Keep the 4-part format `MAJOR.MINOR.PATCH.0`.)

> The user-facing release tag will be `v1.2.25` and the ZIP `OnionMediaBatch_1.2.25.zip`,
> mirroring the previous release naming.

## A.3 Build the MSIX package (Visual Studio — recommended)
1. Open `OnionMedia.sln` in Visual Studio 2022.
2. Set the solution configuration to **Release** and platform to **x64**.
3. Set **OnionMedia (Package)** as the Startup Project.
4. Menu: **Build ▸ Clean Solution**, then **Build ▸ Build Solution** (fix any errors first).
5. Menu: **Project ▸ Publish ▸ Create App Packages…**
   - Select **Sideloading** (NOT Microsoft Store).
   - When asked about signing: choose **Yes, use the current certificate** or
     **Create** a new temporary self-signed test certificate. The wapproj already has
     `GenerateTemporaryStoreCertificate=True`, so a `.cer` will be produced automatically.
   - Select architecture **x64** only (matches `AppxBundlePlatforms=x64`).
   - Finish. VS produces an output folder like:
     `OnionMedia (Package)\AppPackages\OnionMedia (Package)_1.2.25.0_Test\`
     containing:
     - `OnionMedia (Package)_1.2.25.0_x64.msix` (or `.msixbundle`)
     - `OnionMedia (Package)_1.2.25.0_x64.cer`  (the certificate to trust)
     - `Install.ps1` (Microsoft's generated installer script)
     - `Dependencies\` (VCLibs / WindowsAppRuntime appx packages)

## A.4 Command-line alternative (MSBuild)
If you prefer CLI (Developer PowerShell for VS 2022), from repo root:
```powershell
msbuild "OnionMedia\OnionMedia (Package)\OnionMedia (Package).wapproj" `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxBundlePlatforms=x64 `
  /p:AppxPackageSigningEnabled=true `
  /p:GenerateTemporaryStoreCertificate=true `
  /restore
```
Output lands in `OnionMedia (Package)\AppPackages\...`.

## A.5 Create `install.bat` (the console certificate + app installer)
The end user double-clicks `install.bat`; it installs the temporary certificate into
the machine's Trusted People store (this is the "enter signature in the console" step),
then installs the MSIX. Place this `install.bat` NEXT TO the `.msix` and `.cer`
in the release folder.

Create `install.bat` with this content (ASCII, CRLF):
```bat
@echo off
setlocal enabledelayedexpansion
title OnionMedia Batch - Installer

echo ============================================
echo    OnionMedia Batch - Installation
echo ============================================
echo.

REM Elevate to admin if not already (needed to trust the certificate)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

pushd "%~dp0"

echo Installing the signing certificate (you may be asked to confirm)...
for %%f in ("*.cer") do (
    certutil -addstore -f "TrustedPeople" "%%f"
    certutil -addstore -f "Root" "%%f"
)

echo.
echo Installing OnionMedia Batch package...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pkg = Get-ChildItem -Path '.' -Filter '*.msix*' | Select-Object -First 1; Add-AppxPackage -Path $pkg.FullName -DependencyPath (Get-ChildItem -Path '.\Dependencies' -Recurse -Filter '*.appx','*.msix' -ErrorAction SilentlyContinue | ForEach-Object FullName)"

if %errorlevel% neq 0 (
    echo.
    echo Installation may have failed. If prompted, enable Developer Mode in Windows Settings and re-run.
) else (
    echo.
    echo Installation complete! Search for "OnionMedia" in the Start Menu.
)

echo.
pause
popd
```
> Notes:
> - `certutil -addstore` is what makes the unsigned/self-signed cert trusted — this is the
>   "type Y / confirm in console" step the user performs.
> - If `Dependencies` folder path differs, adjust the `-DependencyPath` glob. If there are no
>   dependencies to add, `Add-AppxPackage -Path $pkg.FullName` alone is enough.
> - Keep the exact filenames produced by the build; the script auto-discovers `*.cer` and `*.msix*`.

