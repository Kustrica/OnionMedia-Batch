# Release checklist

1. `git pull`, make sure the working tree is clean.
2. `.\setup_dependencies.ps1` (fresh yt-dlp, ffmpeg, deno).
3. Bump `Version` in `OnionMedia\OnionMedia (Package)\Package.appxmanifest`.
4. Debug build compile check (BUILD-RELEASE.md §2), quick manual test:
   - YouTube video (Universal mode → plays on iPhone), cookies from browser, a fap-nation page, the "yt-dlp args" dialog (Validate/Test).
5. `.\build_release.ps1` (BUILD-RELEASE.md §3) → `AppPackages\OnionMedia_<version>\` + `AppPackages\OnionMedia_<version>.zip`.
   Check the log for `OnionMedia: patched` and the "Release zip:" line.
6. Test install on a clean user/PC: unzip, run `install.bat` (no "Basic Constraints" error).
7. Create the GitHub release (tag `<version>`, title `OnionMedia Batch – <headline> v<version>`), paste
   `.project/RELEASE-NOTES-<version>.md`, attach `OnionMedia_<version>.zip`.
8. Commit/push only when the owner says so.

## Per-release commands (from owner)
> Placeholder — the owner will add the exact commands they use for every release (build, signing, zipping, upload, tagging).
>
> ```powershell
> # TODO (owner): e.g.
> # cd E:\Projects\Programming\OnionMedia-Batch
> # ...
> ```
