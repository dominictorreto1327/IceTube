$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixtures = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $fixtures | Out-Null
$ffmpeg = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
foreach ($item in @(@('wide','640x360'), @('classic','480x360'), @('portrait','270x480'), @('cinema','640x270'), @('small','320x180')))
{
    & $ffmpeg -hide_banner -loglevel error -y -f lavfi -i "color=c=0x20c060:s=$($item[1]):r=25" -f lavfi -i 'sine=frequency=440:sample_rate=44100' -t 10 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -movflags +faststart (Join-Path $fixtures "$($item[0]).mp4")
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed' }
}
& $ffmpeg -hide_banner -loglevel error -y -i (Join-Path $fixtures 'wide.mp4') -an -c:v copy (Join-Path $fixtures 'video.mp4')
& $ffmpeg -hide_banner -loglevel error -y -i (Join-Path $fixtures 'wide.mp4') -vn -c:a copy (Join-Path $fixtures 'audio.m4a')
& (Join-Path $root 'scripts\Build-Release.ps1')
$release = Join-Path $root 'release\IceTube-v0.2.0-win81-x64'
$csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
& $csc /nologo /target:exe /platform:x64 "/out:$release\EmbeddedPlaybackSmoke.exe" "/reference:$release\IceTube.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'EmbeddedPlaybackSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Smoke test compile failed' }
& (Join-Path $release 'EmbeddedPlaybackSmoke.exe') $fixtures
if ($LASTEXITCODE -ne 0) { throw 'Embedded playback smoke test failed; see tests/bin/results.txt' }
