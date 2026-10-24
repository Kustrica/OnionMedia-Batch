# Notes for AI agents

## Ground rules (from the owner)
- Work **locally**. Do not connect GitHub, do not push, do not commit unless the owner explicitly asks.
- The owner runs commands one by one with approval → batch shell commands.
- Keep CRLF line endings; keep existing BOMs. UI text is English (resource `.resw` files exist for de/en/es; new strings are currently hardcoded English).

## Solution layout
- `OnionMedia/OnionMedia.Core` — platform-independent logic (net6.0 + net6.0-windows). ViewModels, models, yt-dlp/ffmpeg logic.
- `OnionMedia/OnionMedia` — WinUI 3 app (WindowsAppSDK 1.1.4), views, dialogs, DI (`ServiceProvider.cs`, Jab).
- `OnionMedia/OnionMedia (Package)` — MSIX packaging project (`.wapproj`), sideload script patch.
- `YoutubeDLSharp-master` — vendored YoutubeDLSharp (yt-dlp wrapper). Default options in `YoutubeDL.GetDownloadOptions()`.
- `setup_dependencies.ps1` — downloads ffmpeg/ffprobe (BtbN), latest yt-dlp, deno into `ExternalBinaries\ffmpeg+yt-dlp\binaries`.

## Downloader pipeline (Core/Classes)
1. **Fetch**: `DownloaderMethods.FetchVideoDataAsync(url)` — the only entry point used by the ViewModel.
   - `YtDlpOptions.Apply()` adds cookies (`CookieOptionsBuilder`), `--impersonate chrome` for hosts that showed an anti-bot block, custom args, `--js-runtimes deno:` when yt-dlp runs from LocalState.
   - Anti-bot (403/Cloudflare) or network errors → one retry with impersonation toggled; host remembered for the session.
   - If yt-dlp fails or returns a *generic* playlist without formats → `WebPageVideoResolver.TryResolveAsync()` scans the page HTML (download buttons, `<video>/<source>`, JSON-LD, og:video, iframes, loose URLs), filters ads, probes candidates with ffprobe (`MediaProbe`), clusters variants of the same video by duration and builds a `VideoData` with formats `web{i}-{h}p`. Embedded players are resolved via yt-dlp with `--referer`.
2. **Plan/display**: `FormatPlanner` builds `-f`/`-S` strings and predicts the chosen format; `StreamItemModel.UpdatePlannedFormat/SetActualFormat/SetFinalFormat` fill the chips in the queue.
3. **Download**: `DownloadStreamAsync` → `GetDownloadSourceUrl` (web items: variant below the quality ceiling, or the embed URL) → `RunVideoDownloadAndConversionAsync` / audio path. Retries toggle impersonation on anti-bot/network errors.
4. **Compatibility pass**: `EnsureCompatibleAsync` (ffmpeg) → MP4, H.264 or HEVC(hvc1), 8-bit 4:2:0, AAC, `+faststart`, cover re-embedded. Copies streams if already compatible.

## Quality ceiling
`FormatPlanner.BuildFormatSelector(cap)` = `bv*[height<=cap]+ba/b[height<=cap]/bv*+ba/b` and `-S res:cap` (closest above the cap if nothing fits). Custom `-f` is **blocked** in custom args so the ceiling can't be bypassed; user `-S` fields are merged in front of ours. Web items pick the variant with `FormatPlanner.PickWebFormat`.

## Testing without the UI
A console harness (not in the repo) can reference `OnionMedia.Core` (net6.0) and provide stub `IPathProvider`, `IDispatcherService`, `ISettingsService`, `IStringResourceService`, `IDataCollectionProvider<LibraryInfo>` via `IoC.Default.InitializeServices(...)`, then call `DownloaderMethods.FetchVideoDataAsync` / `DownloadStreamAsync`.
