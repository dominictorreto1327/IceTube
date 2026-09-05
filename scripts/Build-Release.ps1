param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'IceTube.sln'
$output = Join-Path $root 'release\IceTube-v0.2.0-win81-x64'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'

if (-not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild 17 was not found.' }

& $msbuild $solution /t:Clean,Rebuild /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$expectedOutput = [IO.Path]::GetFullPath((Join-Path $root 'release\IceTube-v0.2.0-win81-x64'))
if ([IO.Path]::GetFullPath($output) -ne $expectedOutput) { throw 'Unexpected release output path.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$bin = Join-Path $root "src\IceTube\bin\$Configuration"
Copy-Item -LiteralPath (Join-Path $bin 'IceTube.exe'),(Join-Path $bin 'IceTube.exe.config') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $root 'tools') -Destination $output -Recurse

foreach ($directory in 'data','cache','logs')
{
    New-Item -ItemType Directory -Force -Path (Join-Path $output $directory) | Out-Null
}

$required = @(
    'tools\yt-dlp\yt-dlp.exe',
    'tools\mpv\mpv.exe',
    'tools\mpv\d3dcompiler_43.dll',
    'tools\ffmpeg\ffmpeg.exe',
    'tools\ffmpeg\ffprobe.exe',
    'tools\js-runtime\qjs.exe'
)

foreach ($relative in $required)
{
    if (-not (Test-Path -LiteralPath (Join-Path $output $relative)))
    {
        throw "Release dependency missing: $relative"
    }
}

$hashLines = foreach ($relative in $required)
{
    $hash = Get-FileHash -LiteralPath (Join-Path $output $relative) -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($relative.Replace('\', '/'))"
}
$hashLines | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "Release created: $output"
