# IceTube

IceTube is a lightweight YouTube client designed for very old Windows PCs.

目标硬件是 Acer Veriton L460、Intel Celeron E3300、4 GB RAM、Windows 8.1 x64。IceTube 不追求最高画质；它强制较低分辨率、优先 H.264、避开 VP9/AV1、不加载完整 YouTube 网页，以减少 CPU 和内存压力。

## v0.2.0

mpv 播放区域现在内嵌于 IceTube 主窗口，启动、停止和播放结束时均保留黑色播放区。播放区域始终为 16:9，支持窗口缩放和最大化。视频按原始显示比例等比放大或缩小至最大可容纳尺寸：4:3、竖屏视频只在左右留黑边，超宽视频只在上下留黑边；不会拉伸、裁切或额外缩小为四面黑边。视频文件本身已经包含的黑边不会被自动裁切。

黑色播放区域常驻，mpv 进程仍按需启动，空闲时释放解码器和网络连接，以保持老电脑的低资源占用。解析、H.264 选择、480p/30fps 上限和 v0.1.1 的本机媒体代理保持不变。

The mpv viewport is now embedded in the IceTube window and remains black when idle, stopped, or finished. It always stays 16:9 when resized or maximized. Videos fit at their original display aspect ratio without stretching or cropping, using side bars for portrait/4:3 content and top/bottom bars for ultrawide content. Black bars already encoded in the source are not removed. The viewport remains present while the mpv process and network connections are released when idle. Resolution and media transport are unchanged from v0.1.1.

```text
输入 YouTube URL
        ↓
yt-dlp + QuickJS 解析
        ↓
选择 H.264/AVC MP4 ≤480p、≤30fps
        ↓
.NET HTTPS → 本机 127.0.0.1 媒体代理 → mpv
```

IceTube 不转码也不把视频保存到磁盘。对普通公开视频，程序优先选择单文件 H.264/AAC 流；如果 YouTube 只提供视频、音频分离格式，两路数据也都由 .NET 网络层读取。mpv 始终只连接 IceTube 在 `127.0.0.1` 上建立的临时本机媒体代理，不再直接连接 YouTube 媒体 CDN。代理随播放启动、随停止退出，并转发 HTTP Range 请求。

v0.1.1 修复了 v0.1 在视频、音频分离格式下仍让 mpv 直接联网，导致旧电脑只出现黑色窗口、稍后以代码 2 退出的问题；同时强制 TLS 1.2、关闭硬件解码以避开老显卡驱动误判，并确保每次播放都写入 `logs/mpv-last.log`。

## 使用 L460 测试版

1. 将整个 `IceTube-v0.2.0-win81-x64` 文件夹复制到 L460，不能只复制 `IceTube.exe`。
2. 确认 Windows 8.1 已安装所有更新（包括 Universal C Runtime / KB2999226）和 [.NET Framework 4.8 Runtime](https://dotnet.microsoft.com/download/dotnet-framework/net48)。
3. 双击 `IceTube.exe`，粘贴普通公开视频 URL，点击“播放”，视频在主窗口的黑色播放区域内显示。
4. UI 显示的当前格式应为 H.264、不高于 480p、不高于 30fps。
5. 在任务管理器观察 `IceTube.exe` 与 `mpv.exe` 的 CPU、内存和丢帧表现，并与浏览器播放同一视频比较。

首次实机测试建议使用短小的普通公开视频。关闭 IceTube 后，确认任务管理器中没有残留 `mpv.exe` 或 `yt-dlp.exe`。

## 目录

```text
IceTube.exe
IceTube.exe.config
tools/
  yt-dlp/yt-dlp.exe
  mpv/mpv.exe
  mpv/d3dcompiler_43.dll
  ffmpeg/ffmpeg.exe
  ffmpeg/ffprobe.exe
  js-runtime/qjs.exe
data/
cache/
logs/
```

所有工具路径都相对于 `IceTube.exe`，不依赖系统 PATH、注册表、Git、Visual Studio、Python 或开发机绝对路径。首次启动会在 `data/settings.json` 写入默认 L460 配置。IceTube 日志最多保留 5 个、单个约 256 KB；mpv 最近一次运行信息覆盖写入 `logs/mpv-last.log`。

## 构建

项目明确使用 C# WinForms 与 .NET Framework 4.8，目标平台 x64。打开 `IceTube.sln` 后选择 `Release | x64` 构建即可；项目没有 NuGet 依赖。

开发环境需要 Visual Studio 2022 的“.NET 桌面开发”、.NET Framework 4.8 SDK 与 Targeting Pack。第三方工具版本和来源见 [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)。

运行 `tests/Run-EmbeddedPlaybackSmoke.ps1` 可生成不同画幅的测试片，自动验证真实主窗体、mpv 子窗口归属、缩放、分离音频、连续换片、取消解析、停止、播放结束和关闭清理。需要可运行 Windows 桌面应用的环境及完整 `tools` 目录；测试结果保存在 `tests/bin`。完成后重新运行 `scripts/Build-Release.ps1` 可生成不含测试程序和日志的干净发布目录。

## Known Issues

- v0.1.1 已由用户确认在 L460 正常播放；v0.2.0 的内嵌显示仍需要在该实机复测，开发机测试不能代替旧显卡驱动的结果。
- 若 Windows 8.1 缺少系统更新，mpv 或 QuickJS 可能因 Universal C Runtime 不完整而无法启动；先运行 Windows Update，不要从 DLL 下载站单独补文件。
- 固定的 `yt-dlp 2026.08.19` 是官方 Windows 可执行文件切换到 Windows 10+ 之前的版本。不要在 L460 上盲目更新；YouTube 改版后它可能需要替换。
- QuickJS-NG 很轻，但上游没有对 Windows 8.1 作正式兼容承诺。若 `qjs.exe` 在 L460 无法启动，解析层可替换，不能据此假定整个方案已失败。
- mpv 使用老显卡兼容优先的 `direct3d` 输出，并允许回退。Intel GMA 3100 通常无法硬解 H.264，实际解码仍主要依赖双核 CPU。
- 拖动进度会触发新的 Range 网络请求，在慢速网络上可能重新缓冲。
- FFmpeg 仅供 yt-dlp 发现、合流或容器处理；IceTube 的在线播放路径不调用实时视频转码。
- 私人视频、地区限制、登录后可见内容和某些 YouTube 挑战可能无法解析；本版本不提供登录或 Cookie 导入。
- 由于固定旧版本以换取 Windows 8.1 兼容性，这些工具不会自动获得后续安全更新。只播放可信 URL，并在维护版本中按实机结果更新依赖。

## v0.2.0 范围

本版本只验证“URL → yt-dlp → H.264 480p → mpv”。没有搜索、推荐、订阅、历史、下载、登录、评论、播放列表或内嵌浏览器。
