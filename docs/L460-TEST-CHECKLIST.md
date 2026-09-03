# Acer Veriton L460 test checklist

记录测试日期、Windows 更新状态、显卡驱动版本和显示分辨率。

- [ ] `IceTube.exe` 能启动，状态显示 `Ready`
- [ ] 公开视频能够解析，标题正确显示
- [ ] 当前格式显示 H.264/AVC、≤480p、≤30fps
- [ ] mpv 能同时播放画面和声音
- [ ] 连续播放 10 分钟，无明显音画不同步
- [ ] 记录 IceTube 空闲内存
- [ ] 记录 mpv 播放时平均/峰值 CPU 和内存
- [ ] 记录是否持续丢帧、卡顿或音频中断
- [ ] 用同一视频、同一画质与浏览器播放做主观对比
- [ ] 点击“停止”后 mpv 退出
- [ ] 关闭 IceTube 后没有残留 `mpv.exe`、`yt-dlp.exe`、`ffmpeg.exe` 或 `qjs.exe`

若 QuickJS、yt-dlp、FFmpeg 或 mpv 无法启动，请保留 `logs` 文件夹和 Windows 错误信息，不要先替换成来源不明的旧程序。
