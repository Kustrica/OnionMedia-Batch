# Custom yt-dlp parameters ("yt-dlp args" button)

Location: Media Downloader page, right of Download/Remove under "Current playlist". The icon/text turn accent-colored while active.

Dialog:
- **Apply custom parameters** toggle, multi-line text box (quotes for values with spaces, `#` comment lines).
- Live syntax check (InfoBar): errors block saving while enabled; warnings for options OnionMedia already sets (cookies, recode/merge).
- **Validate** — runs `yt-dlp --ignore-config <args>` with no URL, so yt-dlp itself parses them (success = it only complains about the missing URL; unknown options / bad values = rejected). `--version` is not used because yt-dlp exits on it before reporting unknown options.
- **Test with selected video** — `yt-dlp --simulate -v <args> <url>` (with cookies/impersonation), output in the log box. If the site answers with a Cloudflare/anti-bot 403 it is retried automatically with `--impersonate chrome` (same as real downloads).
- **Update yt-dlp** — forced `yt-dlp -U`; checkbox for automatic daily updates.
- Saved in app settings (`UseCustomYtDlpArgs`, `CustomYtDlpArgs`), applied to every info fetch and download (not to thumbnail downloads).

Blocked options (managed by the app): `-o/-P`, `--exec`, `-j/-J/--dump-*`, `--print`, `-s/--simulate/--skip-download`, `-q`,
`--no-progress`, `--newline`, `-a`, `-U`, `--version`, `--config-locations`, **`-f/--format`** (use `-S`; the Max Quality ceiling must keep working).
User `-S` fields are merged in front of OnionMedia's sort string (single `-S`).

Examples: `--impersonate chrome`, `-S vcodec:h264`, `--proxy socks5://127.0.0.1:1080`, `--sleep-requests 1`,
`--add-header "Referer:https://example.com/"`, `--extractor-args "youtube:player_client=default"`.
Implementation: `Core/Classes/YtDlpOptions.cs` (`YtDlpCustomArgs`), `Views/Dialogs/CustomArgsDialog.xaml(.cs)`.
