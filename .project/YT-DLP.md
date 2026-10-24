# yt-dlp: updates, impersonation, web pages

## Auto update
- The MSIX install folder is read-only. On start (`App.OnLaunched` → `YtDlpUpdater.EnsureUpToDateAsync()`), the bundled
  `yt-dlp.exe` is copied to `%LOCALAPPDATA%\Packages\<package>\LocalState\yt-dlp\yt-dlp.exe` and `yt-dlp -U` runs **at most once
  per 24 h** (setting `AutoUpdateYtDlp`, default on; toggle in the "yt-dlp args" dialog). A newer bundled version (app update) replaces
  the copy. `PathProvider.YtDlPath` prefers the LocalState copy.
- Manual: "yt-dlp args" dialog → **Update yt-dlp** (needs access to github.com; works with or without VPN as long as GitHub is reachable).
- Because yt-dlp then runs outside the binaries folder, `--js-runtimes deno:<bundled deno.exe>` is passed automatically (YouTube JS challenges).
- Release builds should still bundle a fresh yt-dlp (`setup_dependencies.ps1` always downloads the latest).

## Cloudflare / anti-bot (fap-nation.org & co.)
- Plain yt-dlp gets `HTTP Error 403` from Cloudflare → the app used to show "No video was found".
- Now: on 403/429/503/"Cloudflare"/"impersonat" errors the fetch is retried with `--impersonate chrome` (curl_cffi is inside the
  standalone yt-dlp.exe); the host is remembered for the session. Downloads alternate impersonation on retry (some CDNs fail with
  impersonation, e.g. SSL errors), so it works with and without VPN.
- Cookies ("Cookies from browser"/file/paste) are applied to every call as before.
- `--external-downloader-args` now carries the downloader name (`ffmpeg:...`) → no more "given without specifying name" warning.

## Pages with or without a download button
`WebPageVideoResolver` (used when yt-dlp fails or returns a generic playlist):
- HTML is fetched via yt-dlp `--write-pages` (same TLS impersonation/cookies).
- Candidates: download buttons/links to media, `<video>/<source>`, `data-*` media attributes, JSON `contentUrl/embedUrl`,
  `og:video`, iframes (players), loose media URLs (only if nothing better exists).
- Ads are filtered (banner sizes, ad hosts, decorative muted/looping videos, clips < 15 s); remaining files are probed with ffprobe
  (browser UA + Referer) and grouped into quality variants of the same video (same duration).
- Embedded players are passed to yt-dlp with `--referer <page>`.
- Note: ffprobe (Windows schannel TLS) cannot open some hosts (e.g. fappingstream.com) → that variant shows only "1080p" until it is
  downloaded; the download itself goes through yt-dlp and works.

If a new site breaks: open the item's log (right click → View log), check the `[OnionMedia]` lines; HTML dumps can be made with
`yt-dlp --impersonate chrome --write-pages --skip-download <url>`.
