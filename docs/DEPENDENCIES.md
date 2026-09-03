# Third-party dependencies

IceTube v0.1 pins portable tools instead of reading them from PATH. The release bundle contains only the files needed at runtime.

| Component | Pinned version/build | Upstream source | Windows 8.1 status |
|---|---|---|---|
| yt-dlp | 2026.08.19 x64 Windows executable | https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19 | Official executable predates the announced Windows 10+ cutoff; L460 test still required |
| QuickJS-NG | 0.16.2 x86_64 | https://github.com/quickjs-ng/quickjs/releases/tag/v0.16.2 | Supported by yt-dlp EJS; upstream does not promise Windows 8.1 compatibility |
| FFmpeg | 7.0 essentials build by Gyan | https://github.com/GyanD/codexffmpeg/releases/tag/7.0 | Pinned before Gyan's stated Windows 7/8 cutoff |
| mpv | 2024-05-19 x86_64 build `875378f` by shinchiro | https://sourceforge.net/projects/mpv-player-windows/files/64bit/ | Archived build chosen for old Windows/GPU compatibility; not a formal Win8.1 guarantee |

The mpv archive's matching `d3dcompiler_43.dll` is shipped beside `mpv.exe` for its legacy renderer fallback. On Windows 8.1, install all Windows updates, including Microsoft's Universal C Runtime update (KB2999226). Binary header inspection confirms the bundled x64 executables target Windows subsystem versions no newer than 6.0; mpv's imported `PathCchCanonicalizeEx` is documented by Microsoft as available from Windows 8. The final authority remains a test on the L460 itself.

yt-dlp's official EJS documentation supports QuickJS and QuickJS-NG when enabled through `--js-runtimes quickjs:<path>`. IceTube passes an explicit path to `tools/js-runtime/qjs.exe`; it does not require PATH or Node.js.

## Integrity

SHA-256 values for the exact files shipped in the release are recorded in `SHA256SUMS.txt` at the release root. Recompute with:

```powershell
Get-FileHash .\tools\yt-dlp\yt-dlp.exe, .\tools\mpv\mpv.exe, .\tools\mpv\d3dcompiler_43.dll, .\tools\ffmpeg\ffmpeg.exe, .\tools\ffmpeg\ffprobe.exe, .\tools\js-runtime\qjs.exe -Algorithm SHA256
```

The project does not claim ownership of these tools. They retain their respective upstream licenses.
