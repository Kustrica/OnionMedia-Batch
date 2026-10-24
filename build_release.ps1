<#
  OnionMedia-Batch release build.
  Builds the sideloading package (Release, x64) and produces exactly what goes on the GitHub release:
    OnionMedia\OnionMedia (Package)\AppPackages\OnionMedia_<version>\      (folder, no "_Test" suffix)
    OnionMedia\OnionMedia (Package)\AppPackages\OnionMedia_<version>.zip   (release asset, files at the zip root)
  <version> comes from Package.appxmanifest (1.2.26.0 -> 1.2.26).
  Usage (PowerShell, repo root):
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build_release.ps1
#>
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root     = $PSScriptRoot
$pkgProj  = Join-Path $root 'OnionMedia\OnionMedia (Package)\OnionMedia (Package).wapproj'
$manifest = Join-Path $root 'OnionMedia\OnionMedia (Package)\Package.appxmanifest'
$pkgDir   = Join-Path $root 'OnionMedia\OnionMedia (Package)\AppPackages'

[xml]$xml = Get-Content -LiteralPath $manifest
$version  = $xml.Package.Identity.Version            # e.g. 1.2.26.0
$short    = $version -replace '\.0$', ''             # e.g. 1.2.26
$name     = "OnionMedia_$short"
$outDir   = Join-Path $pkgDir $name
$zipPath  = Join-Path $pkgDir "$name.zip"
$testDir  = Join-Path $pkgDir "OnionMedia (Package)_$($version)_Test"
Write-Host "OnionMedia release $short -> $outDir"

if (-not $SkipBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msb = $null
    if (Test-Path $vswhere) { $msb = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
    if (-not $msb) { throw 'MSBuild not found (install Visual Studio with the MSIX packaging workload).' }

    foreach ($d in @($outDir, $testDir)) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    & $msb $pkgProj /restore /m /nologo /v:m /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Always `
        /p:AppxBundlePlatforms=x64 /p:UapAppxPackageBuildMode=SideloadOnly /p:GenerateAppInstallerFile=False "/p:AppxPackageTestDir=$outDir\\"  # double backslash: a single "\" before the closing quote would escape it
    # NETSDK1047 restore warnings from a side project can make MSBuild return non-zero although the bundle is built,
    # so the result is checked by looking at the output instead of the exit code.
}

# Older VS versions ignore AppxPackageTestDir -> move the "_Test" folder.
if (-not (Get-ChildItem -LiteralPath $outDir -Filter *.msixbundle -ErrorAction SilentlyContinue) -and (Test-Path $testDir)) {
    if (Test-Path $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    Move-Item -LiteralPath $testDir -Destination $outDir
}
if (-not (Get-ChildItem -LiteralPath $outDir -Filter *.msixbundle -ErrorAction SilentlyContinue)) { throw "No .msixbundle in $outDir - the build failed (see log above)." }

# Make sure the installer scripts are patched and install.bat is present (the wapproj target normally does this).
$addApp = Join-Path $outDir 'Add-AppDevPackage.ps1'
if (-not (Test-Path (Join-Path $outDir 'install.bat')) -or (Select-String -LiteralPath $addApp -Pattern '^\s*CheckCertificateRestrictions\s*$' -Quiet)) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'OnionMedia\OnionMedia (Package)\Patch-SideloadScripts.ps1') -TestDir $outDir -RepoRoot $root
}

$required = @('install.bat', 'Install.ps1', 'Add-AppDevPackage.ps1', 'Add-AppDevPackage.resources', 'Dependencies')
$missing  = $required | Where-Object { -not (Test-Path (Join-Path $outDir $_)) }
if (-not (Get-ChildItem -LiteralPath $outDir -Filter *.cer)) { $missing += '*.cer' }
if ($missing) { throw "Release folder is incomplete, missing: $($missing -join ', ')" }

if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
# Entries are written with "/" separators like the previous release zips
# (Compress-Archive / ZipFile in Windows PowerShell 5.1 write "\", which breaks some unzip tools).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
$za = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path -LiteralPath $outDir).Path.TrimEnd('\')
    Get-ChildItem -LiteralPath $base -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $za.Dispose(); $fs.Dispose() }
$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Release folder: $outDir"
Write-Host "Release zip:    $zipPath ($size MB)"
