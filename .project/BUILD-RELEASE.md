# Building and releasing

## Toolchain
- Windows 10/11, Visual Studio 2022+ (tested with VS 18 Community) with ".NET desktop development" + "Windows App SDK / MSIX packaging" workloads.
- .NET SDK (any recent; project targets net6.0-windows10.0.19041 — the .NET 6 runtime is bundled self-contained in the package build).
- Windows SDK 10.0.19041+.

## 1. Dependencies (binaries are git-ignored except yt-dlp.exe)
```powershell
cd <repo>
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup_dependencies.ps1
```
Downloads `ffmpeg.exe`, `ffprobe.exe` (BtbN GPL build), latest `yt-dlp.exe`, `deno.exe` into
`OnionMedia\OnionMedia\ExternalBinaries\ffmpeg+yt-dlp\binaries`. Without them the build fails with MSB3030 (file not found).

## 2. Debug build (compile check)
```powershell
$msb = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find MSBuild\**\Bin\MSBuild.exe
& $msb OnionMedia\OnionMedia\OnionMedia.csproj /restore /m /p:Configuration=Debug /p:Platform=x64 /p:RuntimeIdentifier=win10-x64
```

## 3. Release package (sideloading) — use `build_release.ps1`
```powershell
cd <repo>
powershell -NoProfile -ExecutionPolicy Bypass -File .\build_release.ps1
```
The script reads the version from `Package.appxmanifest` (e.g. `1.2.26.0` → `1.2.26`), builds Release/x64 sideload-only and produces:

| Output | Path |
|---|---|
| Release folder (no `_Test` suffix) | `OnionMedia\OnionMedia (Package)\AppPackages\OnionMedia_<version>\` |
| Release asset | `OnionMedia\OnionMedia (Package)\AppPackages\OnionMedia_<version>.zip` |

It passes `/p:AppxPackageTestDir=...\OnionMedia_<version>\` (falls back to renaming the VS `..._Test` folder), checks that
`install.bat`, `Install.ps1`, patched `Add-AppDevPackage.ps1` (+ resources), the `.cer`, the `.msixbundle` and `Dependencies\`
are present, then zips the folder **contents** (files at the zip root – same layout as the previous releases).

The wapproj target **OnionMediaPatchSideloadScripts** (runs after `_AddWindowsInstallScriptToTestLayout`) executes
`OnionMedia (Package)\Patch-SideloadScripts.ps1` which disables `CheckCertificateRestrictions`, strips the invalid signature
block and copies `install.bat`. The build log shows `OnionMedia: patched ...`; `build_release.ps1` re-runs the patch if needed.

MSBuild may print two `NETSDK1047` restore errors (win10-x64 assets of YoutubeDLSharp/Core during the package restore). They do
not stop the bundle from being built; the script checks the output instead of the exit code.

## 4. Publish (naming convention)
- Zip asset: `OnionMedia_<version>.zip` (e.g. `OnionMedia_1.2.26.zip`), extracted folder name `OnionMedia_<version>`.
- Tag: `<version>` (the last release used `1.2.25`; older ones used `v1.2.24` – pick one style and keep it).
- Title: `OnionMedia Batch – <short headline> v<version>`.
- Body: `## What's new` bullet list + the standard `## Installation Guide:` block (see `RELEASE-NOTES-<version>.md`).
See RELEASE-CHECKLIST.md.
