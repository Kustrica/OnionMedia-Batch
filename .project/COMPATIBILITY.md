# Output compatibility (iPhone etc.)

Download page → "Compatibility" (next to Max Quality, ⓘ tooltip explains the modes) = Settings → "Video compatibility (downloads)"
(same AppSettings.VideoCompatibilityMode, stored as "videoCompatibilityMode2"; values saved by earlier test builds under
"videoCompatibilityMode" are ignored so everyone starts with Original). Changing it refreshes the chips of every queued item,
which show the FINAL codecs/container (tooltip: Source → Result, max available vs. downloaded resolution):
- **Original (default)** — no re-encoding: formats are merged/remuxed into MP4 (`--merge-output-format mp4`, `--remux-video mp4`;
  the old `--recode-video mp4` re-encoded WebM sources and was slow). AV1/VP9/Opus possible.
- **Universal** — MP4, H.264 8-bit 4:2:0 (High profile), AAC. yt-dlp prefers H.264/AAC formats (`-S ...,vcodec:h264,acodec:aac`);
  if only AV1/VP9 exist at the chosen resolution, the file is re-encoded (libx264 CRF 21 or the selected hardware encoder, with software
  fallback). Already compatible files are only remuxed (`+faststart`). Same approach as Tyrrrz/YoutubeDownloader (prefer muxable H.264 + AAC MP4).
- **Compact** — MP4, HEVC tagged `hvc1` (required by Apple players) + AAC; H.264 sources are kept. Smaller files, slower re-encode.

GIF mode and audio downloads are unaffected. The queue shows the planned format (duration, resolution, bitrate, codecs, container, size
and "→ MP4 · H.264 + AAC (re-encode from VP9)"), the actually selected yt-dlp format while downloading, and the real file info after saving.
