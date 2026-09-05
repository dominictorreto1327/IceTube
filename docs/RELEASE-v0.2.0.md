# IceTube v0.2.0 — 内嵌播放器 / Embedded Player

## 中文

- 将 mpv 视频窗口嵌入 IceTube 主界面，播放时不再弹出独立播放器。
- 固定 16:9 播放区域，空闲、停止或播放结束后显示黑屏。
- 视频按原比例适配：4:3 和竖屏视频留左右黑边，超宽视频留上下黑边。不会拉伸或裁切视频；源文件自带黑边不自动去除。
- 支持调整主窗口大小和最大化，播放区域保持 16:9。
- 保留 v0.1.1 的解析、格式选择、软件解码和本机网络转发逻辑。

完整解压 `IceTube-v0.2.0-win81-x64.zip`，运行其中的 `IceTube.exe`。环境要求与 v0.1.1 相同，所有工具均随包提供。建议使用新文件夹进行 L460 实机测试。

## English

- Embedded the mpv video window in IceTube; playback no longer opens a separate player window.
- The persistent 16:9 viewport stays black when idle, stopped, or finished.
- Videos fit at their original aspect ratio: side bars for 4:3/portrait content and top/bottom bars for ultrawide content. No stretching or cropping; bars encoded in the source are preserved.
- Resizing and maximizing the main window preserve the 16:9 viewport.
- Video resolution, format selection, software decoding, and local media forwarding remain unchanged from v0.1.1.

Extract the entire `IceTube-v0.2.0-win81-x64.zip` archive and run `IceTube.exe`. Requirements are unchanged from v0.1.1, and all tools are bundled. Use a new folder for testing on the L460.
