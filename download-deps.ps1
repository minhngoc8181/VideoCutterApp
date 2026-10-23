# Tai cac thanh phan ngoai (khong dua len GitHub vi moi file > 100 MB) vao thu muc tools\:
#   tools\ffmpeg.exe, tools\ffprobe.exe  - FFmpeg "essentials" build cua gyan.dev
#   tools\libmpv-2.dll                   - libmpv 64-bit cua shinchiro (mpv-winbuild-cmake)
#
# Chay:  powershell -ExecutionPolicy Bypass -File .\download-deps.ps1 [-Force]
#   -Force        tai lai ke ca khi file da co
#   -FfmpegUrl    dung link .zip FFmpeg khac (vd. ban co dinh phien ban)
#   -MpvUrl       dung link .7z libmpv khac
param(
    [switch]$Force,
    [string]$FfmpegUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip',
    [string]$MpvUrl = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest nhanh hon nhieu khi tat thanh tien do
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$root  = $PSScriptRoot
$tools = Join-Path $root 'tools'
$work  = Join-Path $root '_deps\download'
New-Item -ItemType Directory -Path $tools, $work -Force | Out-Null

function Get-File([string]$url, [string]$out) {
    Write-Host "  Tai: $url"
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -Headers @{ 'User-Agent' = 'VideoCutter-download-deps' }
    Write-Host ("  Xong: {0:N1} MB" -f ((Get-Item $out).Length / 1MB))
}

function Get-LatestGitHubAsset([string]$repo, [string]$pattern) {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing `
        -Headers @{ 'User-Agent' = 'VideoCutter-download-deps' }
    $asset = $rel.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
    if (-not $asset) { throw "Khong tim thay file khop '$pattern' trong release moi nhat cua $repo" }
    return $asset.browser_download_url
}

function Expand-Any([string]$archive, [string]$dest) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $7z = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if (-not $7z) {
        foreach ($p in "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") {
            if ($p -and (Test-Path $p)) { $7z = $p; break }
        }
    }
    if ($7z) {
        & $7z x $archive "-o$dest" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip giai nen that bai: $archive" }
        return
    }
    # tar.exe (bsdtar/libarchive) co san tren Windows 10 1803+ doc duoc .zip va (ban moi) .7z
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($tar) {
        & $tar.Source -xf $archive -C $dest
        if ($LASTEXITCODE -eq 0) { return }
    }
    if ($archive -like '*.zip') { Expand-Archive -Path $archive -DestinationPath $dest -Force; return }
    throw "Khong giai nen duoc $archive. Hay cai 7-Zip (https://www.7-zip.org) roi chay lai."
}

function Copy-Found([string]$dir, [string]$name) {
    $f = Get-ChildItem $dir -Recurse -Filter $name | Select-Object -First 1
    if (-not $f) { throw "Khong thay $name trong goi da tai" }
    Copy-Item $f.FullName (Join-Path $tools $name) -Force
    Write-Host "  -> tools\$name" -ForegroundColor Green
}

# ---------------- FFmpeg ----------------
$needFf = $Force -or -not (Test-Path "$tools\ffmpeg.exe") -or -not (Test-Path "$tools\ffprobe.exe")
if ($needFf) {
    Write-Host "[1/2] FFmpeg (gyan.dev essentials build)" -ForegroundColor Cyan
    $zip = Join-Path $work 'ffmpeg.zip'
    try { Get-File $FfmpegUrl $zip }
    catch {
        Write-Warning "Khong tai duoc tu $FfmpegUrl ($($_.Exception.Message)). Thu ban sao tren GitHub (GyanD/codexffmpeg)..."
        Get-File (Get-LatestGitHubAsset 'GyanD/codexffmpeg' '^ffmpeg-[\d.]+-essentials_build\.zip$') $zip
    }
    $x = Join-Path $work 'ffmpeg'
    Expand-Any $zip $x
    Copy-Found $x 'ffmpeg.exe'
    Copy-Found $x 'ffprobe.exe'
} else { Write-Host "[1/2] FFmpeg: da co (dung -Force de tai lai)" }

# ---------------- libmpv ----------------
if ($Force -or -not (Test-Path "$tools\libmpv-2.dll")) {
    Write-Host "[2/2] libmpv (shinchiro mpv-winbuild-cmake, x86_64)" -ForegroundColor Cyan
    if (-not $MpvUrl) {
        # Ban x86_64 thuong (khong phai -v3) de chay duoc tren moi CPU 64-bit
        $MpvUrl = Get-LatestGitHubAsset 'shinchiro/mpv-winbuild-cmake' '^mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z$'
    }
    $a = Join-Path $work 'mpv-dev.7z'
    Get-File $MpvUrl $a
    $x = Join-Path $work 'mpv'
    Expand-Any $a $x
    Copy-Found $x 'libmpv-2.dll'
} else { Write-Host "[2/2] libmpv: da co (dung -Force de tai lai)" }

# ---------------- Kiem tra ----------------
Write-Host ""
$v = (& "$tools\ffmpeg.exe" -hide_banner -version | Select-Object -First 1)
Write-Host "FFmpeg : $v"
Write-Host "libmpv : $((Get-Item "$tools\libmpv-2.dll").VersionInfo.FileVersion)"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Da san sang. Build: dotnet build -c Release" -ForegroundColor Green
