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
    
    $ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.zip"
    $zipPath = Join-Path $env:TEMP "ffmpeg-release-full.zip"
    
    try {
        Write-Host "Downloading FFmpeg from $ffmpegUrl..."
        Invoke-WebRequest -Uri $ffmpegUrl -OutFile $zipPath
        
        Write-Host "Extracting FFmpeg..."
        $extractPath = Join-Path $env:TEMP "ffmpeg_extract"
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
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
    }
} else {
    Write-Host "FFmpeg and FFprobe are already present." -ForegroundColor Green
}

# --- yt-dlp ---
$ytdlpPath = Join-Path $binariesDir "yt-dlp.exe"
# Always check for update or download if missing
# Note: Since the user might want to keep a stable version, we only download if missing. 
# But for "auto install" usually implies "ensure it exists".
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

Write-Host "Dependency setup complete." -ForegroundColor Green
