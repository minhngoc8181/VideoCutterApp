# Build ban Release va dong goi:
#   dist\VideoCutter\      (exe, config, ffmpeg.exe, ffprobe.exe, libmpv-2.dll, README.md)
#   dist\VideoCutter.zip
# Khong kem VideoCutter.settings / VideoCutter.log (du lieu ca nhan).
# Chay:  powershell -ExecutionPolicy Bypass -File .\make-dist.ps1 [-NoBuild]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$tools = Join-Path $root 'tools'
if (-not ((Test-Path "$tools\ffmpeg.exe") -and (Test-Path "$tools\ffprobe.exe") -and (Test-Path "$tools\libmpv-2.dll"))) {
    Write-Host "Thieu FFmpeg / libmpv trong tools\ -> tai ve..." -ForegroundColor Yellow
    & (Join-Path $root 'download-deps.ps1')
}

if (-not $NoBuild) {
    dotnet build "$root\VideoCutterApp.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build that bai" }
}

$bin  = Join-Path $root 'bin\Release\net48'
$dist = Join-Path $root 'dist'
$pkg  = Join-Path $dist 'VideoCutter'
$zip  = Join-Path $dist 'VideoCutter.zip'

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Path $pkg -Force | Out-Null

$files = 'VideoCutter.exe', 'VideoCutter.exe.config', 'ffmpeg.exe', 'ffprobe.exe', 'libmpv-2.dll', 'README.md'
foreach ($f in $files) {
    $src = Join-Path $bin $f
    if (-not (Test-Path $src)) { throw "Thieu file: $src" }
    Copy-Item $src $pkg
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "Da dong goi xong:" -ForegroundColor Green
Get-ChildItem $pkg | Format-Table Name, Length -AutoSize
Write-Host "Zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
