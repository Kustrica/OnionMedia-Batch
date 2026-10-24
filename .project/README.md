# .project — maintainer & AI-agent notes for OnionMedia-Batch

This folder documents how this fork is built, released and extended. It is meant for humans **and** AI coding agents.

| File | Topic |
|---|---|
| [AGENTS.md](AGENTS.md) | Rules / architecture map for AI agents working on this repo |
| [BUILD-RELEASE.md](BUILD-RELEASE.md) | Toolchain, dependencies, building, packaging a release |
| [CERTIFICATE.md](CERTIFICATE.md) | Self-signed certificate, the "Basic Constraints" installer error and its workaround |
| [INSTALL.md](INSTALL.md) | How end users install a release (install.bat) |
| [YT-DLP.md](YT-DLP.md) | yt-dlp auto-update, Cloudflare/impersonation, web-page scanning (fap-nation & co.) |
| [CUSTOM-ARGS.md](CUSTOM-ARGS.md) | The "yt-dlp args" button / custom parameters |
| [COMPATIBILITY.md](COMPATIBILITY.md) | Universal / Compact / Original output modes (iPhone-compatible downloads) |
| [RELEASE-CHECKLIST.md](RELEASE-CHECKLIST.md) | Per-release steps, incl. **Per-release commands (from owner)** |
| [RELEASE-NOTES-1.2.26.md](RELEASE-NOTES-1.2.26.md) | Tag / title / body of the v1.2.26 GitHub release |

Release build: `.\build_release.ps1` in the repo root → `AppPackages\OnionMedia_<version>\` and `AppPackages\OnionMedia_<version>.zip`.
