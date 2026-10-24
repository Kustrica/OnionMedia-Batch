# Release v1.2.26 (draft)

- **Tag:** `1.2.26`
- **Title:** `OnionMedia Batch – Cloudflare, Custom Args & iPhone Compatibility v1.2.26`
- **Asset:** `OnionMedia_1.2.26.zip` (built by `build_release.ps1`, in `OnionMedia\OnionMedia (Package)\AppPackages\`)

## Body (paste into GitHub)

## What's new
- Cloudflare Support: Sites behind Cloudflare / anti-bot protection now work. OnionMedia switches on yt-dlp browser impersonation automatically, retries the request and remembers the site.
- Video Discovery on Web Pages: If yt-dlp finds nothing, the page itself is scanned (download buttons, players, embeds, KVS players used by many tube sites) and all found qualities are offered.
- Updated yt-dlp + Auto-Update: yt-dlp 2026.10.21 is included and now updates itself automatically (at most once a day). You can also update it manually from the "yt-dlp args" window, which shows the installed version and its release date.
- iPhone-Compatible Videos: New "Compatibility" selector next to Max Quality on the download page (also in Settings → Video compatibility) – Original (default, saved as the site delivers it, fastest), Universal (converted to H.264 + AAC in MP4 when needed, plays everywhere incl. iPhone, takes longer when converting) or Compact (HEVC/H.265, smaller files).
- Quality Ceiling for All Sources: "Max Quality" now applies to every site, not only YouTube.
- Custom yt-dlp Parameters: New "yt-dlp args" button with a catalog of popular options (short description, value picker, Copy and Add), validation, a test run on the selected video and an output log. The button lights up when custom parameters are active, just like Cookies. Click outside the window to close it (same as Cancel).
- Expanding Playlist with Info: The playlist grows to the window height when more than 3 videos are added. Every item shows duration, resolution and size, then bitrate, codecs, container and the site it comes from.
- Download Counter: While downloading, the text above the progress bar shows how many videos are done, e.g. "Downloaded: 3 / 12" (failed or cancelled ones in brackets), just like "Imported: X / Y" during import. Next to "Current playlist" you see how many videos the playlist holds, e.g. "12 items".
- Final Format in the Playlist: The chips show the file you will get. Anything that changes is shown as "result (original)": "1080p60 (2160p60)" when Max Quality limits the resolution, "H.264 (AV1)" / "MP4 (WEBM)" when the video is converted, "0:45 (5:40)" when Short video trims it. Hover a card to see the original format (Source).
- Quality Cap: Max Quality always picks the best version at or below the limit. If a site has no version in between (e.g. only 720p streams and a 4K original), the card shows "720p (2160p)" and the tooltip lists what is available.
- Wider Playlist: The page is split about 40/60 so the playlist has more room for titles and info; buttons line up with the page margins.
- Easier Playlist Editing: Select several videos with Ctrl/Shift + click and remove them with the Delete key or Remove. Every card has a small ✕ button and "Remove from playlist" in the right-click menu (now works anywhere on the card). After removing, the next video is selected instead of jumping to the end.
- Faster Adding: Slow or dead links no longer hold up adding videos (shorter timeouts, parallel probing, no retries for unsupported or removed links).
- Installer Fix: Removed the certificate restriction check ("Basic Constraints" error). The installer is now always in English, closes by itself after a successful installation and only stays open when there is an error.
- Thumbnail Fix: "Download thumbnail" now saves a real JPG/PNG (WebP is converted), works with playlist/mix links (`&list=RD...`) and cleans up file names (invisible "blank" letters, emoji, overlong titles) so Windows Photos can open them.
- Fixed Crash: "Nullable object must have a value" no longer appears for videos without duration info.
- Better Video Info: Videos where the site reports only the height or only the width are handled (the missing value is estimated or measured), so Max Quality and the item info work for them too.

## Installation Guide:
1. Download release (`OnionMedia_1.2.26.zip`) and extract it.
2. Open the extracted folder (e.g., `OnionMedia_1.2.26`).
3. Double-click on `install.bat`.
4. A PowerShell window will open (always in English). Confirm the certificate installation if asked (type 'Y' / allow administrator rights). The window closes by itself after a successful installation; if something goes wrong it stays open and shows the error.
5. After successful installation you can find the app in the **Start Menu** by searching for "OnionMedia".

**Note:** You may be prompted to enable **Developer Mode** in Windows Settings. If the Settings app opens, simply toggle "Developer Mode" to **On**, then return to the PowerShell window to continue installation.
