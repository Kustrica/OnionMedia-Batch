# Setup Dependencies Script for OnionMedia
# Downloads FFmpeg and yt-dlp if they are missing.

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$binariesDir = Join-Path $scriptDir "OnionMedia\OnionMedia\ExternalBinaries\ffmpeg+yt-dlp\binaries"

# Create directory if it doesn't exist
if (-not (Test-Path $binariesDir)) {
    New-Item -ItemType Directory -Path $binariesDir -Force
}

# --- FFmpeg & FFprobe ---
$ffmpegPath = Join-Path $binariesDir "ffmpeg.exe"
$ffprobePath = Join-Path $binariesDir "ffprobe.exe"

function Is-Placeholder($path) {
    if (-not (Test-Path $path)) { return $true }
    $fileInfo = Get-Item $path
    # If file is smaller than 1MB, treat it as a placeholder/dummy
    if ($fileInfo.Length -lt 1MB) { return $true }
    return $false
}

if ((Is-Placeholder $ffmpegPath) -or (Is-Placeholder $ffprobePath)) {
    Write-Host "FFmpeg or FFprobe not found (or is a placeholder). Downloading..." -ForegroundColor Cyan

    # https://github.com/BtbN/FFmpeg-Builds/releases
    $ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
    $zipPath = Join-Path $env:TEMP "ffmpeg-master-latest-win64-gpl.zip"
    $extractPath = Join-Path $env:TEMP "ffmpeg_extract"
    
    try {
        Write-Host "Downloading FFmpeg from GitHub (BtbN)..."
        Invoke-WebRequest -Uri $ffmpegUrl -OutFile $zipPath
        
        Write-Host "Extracting FFmpeg..."
        if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
        Expand-Archive -Path $zipPath -DestinationPath $extractPath
        
        # Find the bin folder inside the extracted folder
        $binFolder = Get-ChildItem -Path $extractPath -Recurse -Directory | Where-Object { $_.Name -eq "bin" } | Select-Object -First 1
        
        if ($binFolder) {
            Copy-Item -Path (Join-Path $binFolder.FullName "ffmpeg.exe") -Destination $binariesDir -Force
            Copy-Item -Path (Join-Path $binFolder.FullName "ffprobe.exe") -Destination $binariesDir -Force
            Write-Host "FFmpeg and FFprobe installed successfully." -ForegroundColor Green
        } else {
            Write-Error "Could not find 'bin' folder in downloaded FFmpeg archive."
        }
    }
    catch {
        Write-Error "Failed to download or install FFmpeg: $_"
    }
    finally {
        # Cleanup
        if ($zipPath -and (Test-Path $zipPath)) { Remove-Item $zipPath -Force }
        if ($extractPath -and (Test-Path $extractPath)) { Remove-Item $extractPath -Recurse -Force }
    }
} else {
    Write-Host "FFmpeg and FFprobe are already present." -ForegroundColor Green
}

# --- yt-dlp ---
$ytdlpPath = Join-Path $binariesDir "yt-dlp.exe"
# Always check for update or download if missing
# Note: Since the user might want to keep a stable version, we only download if missing. 
# --- yt-dlp ---
$ytdlpPath = Join-Path $binariesDir "yt-dlp.exe"

if (Is-Placeholder $ytdlpPath) {
    Write-Host "yt-dlp not found (or is a placeholder). Downloading..." -ForegroundColor Cyan
    $ytdlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
    
    try {
        Invoke-WebRequest -Uri $ytdlpUrl -OutFile $ytdlpPath
        Write-Host "yt-dlp installed successfully." -ForegroundColor Green
    }
    catch {
        Write-Error "Failed to download yt-dlp: $_"
    }
} else {
    Write-Host "yt-dlp is already present." -ForegroundColor Green
}

# --- Deno (Optional JS engine for yt-dlp) ---
$denoPath = Join-Path $binariesDir "deno.exe"

if (Is-Placeholder $denoPath) {
    Write-Host "Deno not found (or is a placeholder). Downloading..." -ForegroundColor Cyan
    # Deno is distributed as a zip file
    $denoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip"
    $denoZipPath = Join-Path $env:TEMP "deno.zip"
    
    try {
        Invoke-WebRequest -Uri $denoUrl -OutFile $denoZipPath
        
        Write-Host "Extracting Deno..."
        $denoExtractPath = Join-Path $env:TEMP "deno_extract"
        if (Test-Path $denoExtractPath) { Remove-Item $denoExtractPath -Recurse -Force }
        Expand-Archive -Path $denoZipPath -DestinationPath $denoExtractPath
        
        $denoExe = Join-Path $denoExtractPath "deno.exe"
        if (Test-Path $denoExe) {
            Copy-Item -Path $denoExe -Destination $binariesDir -Force
            Write-Host "Deno installed successfully." -ForegroundColor Green
        } else {
             Write-Error "Could not find 'deno.exe' in downloaded archive."
        }
    }
    catch {
        Write-Error "Failed to download Deno: $_"
    }
    finally {
        if (Test-Path $denoZipPath) { Remove-Item $denoZipPath -Force }
        if (Test-Path $denoExtractPath) { Remove-Item $denoExtractPath -Recurse -Force }
    }
} else {
    Write-Host "Deno is already present." -ForegroundColor Green
}

Write-Host "Dependency setup complete." -ForegroundColor Green
