using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using IceTube.Logging;
using IceTube.Models;

namespace IceTube.Services
{
    public sealed class PlayerExitedEventArgs : EventArgs
    {
        public PlayerExitedEventArgs(int exitCode, bool wasStopped)
        {
            ExitCode = exitCode;
            WasStopped = wasStopped;
        }

        public int ExitCode { get; private set; }
        public bool WasStopped { get; private set; }
    }

    public sealed class PlayerService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly ToolLocator _tools;
        private Process _process;
        private ProcessJob _job;
        private bool _stopRequested;
        private bool _disposed;

        public PlayerService(ToolLocator tools)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        }

        public event EventHandler<PlayerExitedEventArgs> PlayerExited;

        public bool IsPlaying
        {
            get
            {
                lock (_sync)
                {
                    return _process != null && !_process.HasExited;
                }
            }
        }

        public void Play(VideoInfo video)
        {
            if (video == null) throw new ArgumentNullException(nameof(video));
            if (string.IsNullOrWhiteSpace(video.VideoStreamUrl))
                throw new InvalidOperationException("没有可播放的视频流。");
            if (!File.Exists(_tools.MpvPath))
                throw new FileNotFoundException("找不到 mpv.exe。请确认完整复制了 IceTube 发布目录。", _tools.MpvPath);

            Stop();

            List<string> arguments = new List<string>
            {
                "--no-config",
                "--force-window=yes",
                "--terminal=no",
                "--profile=fast",
                "--vo=direct3d,gpu,",
                "--hwdec=auto-safe",
                "--vd-lavc-threads=2",
                "--framedrop=vo",
                "--video-sync=audio",
                "--cache=yes",
                "--demuxer-max-bytes=20MiB",
                "--cache-pause=no",
                "--title=IceTube - " + SanitizeTitle(video.Title)
            };

            if (!string.IsNullOrWhiteSpace(video.UserAgent))
                arguments.Add("--user-agent=" + video.UserAgent);
            if (!string.IsNullOrWhiteSpace(video.Referer))
                arguments.Add("--referrer=" + video.Referer);
            if (!string.IsNullOrWhiteSpace(video.AudioStreamUrl))
                arguments.Add("--audio-file=" + video.AudioStreamUrl);

            arguments.Add("--");
            arguments.Add(video.VideoStreamUrl);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _tools.MpvPath,
                Arguments = WindowsCommandLine.Join(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _tools.BaseDirectory
            };

            Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            ProcessJob job = new ProcessJob();
            process.Exited += OnProcessExited;

            lock (_sync)
            {
                ThrowIfDisposed();
                _stopRequested = false;
                _process = process;
                _job = job;
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("mpv 没有启动。");
                    job.TryAssign(process);
                }
                catch
                {
                    _process = null;
                    _job = null;
                    process.Dispose();
                    job.Dispose();
                    throw;
                }
            }

            LogService.Info("mpv started for format " + video.FormatId + ".");
        }

        public void Stop()
        {
            Process process;
            ProcessJob job;
            lock (_sync)
            {
                process = _process;
                job = _job;
                if (process == null) return;
                _stopRequested = true;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2000)) process.Kill();
                }
            }
            catch
            {
                // Closing the job handle below is the final process-tree fallback.
            }
            finally
            {
                job?.Dispose();
                ClearProcess(process);
            }
        }

        private void OnProcessExited(object sender, EventArgs eventArgs)
        {
            Process process = sender as Process;
            if (process == null) return;

            int exitCode = -1;
            bool stopped;
            try { exitCode = process.ExitCode; } catch { }

            lock (_sync) stopped = _stopRequested;
            ClearProcess(process);
            LogService.Info("mpv exited with code " + exitCode + ".");
            PlayerExited?.Invoke(this, new PlayerExitedEventArgs(exitCode, stopped));
        }

        private void ClearProcess(Process process)
        {
            ProcessJob job = null;
            lock (_sync)
            {
                if (!ReferenceEquals(_process, process)) return;
                _process = null;
                job = _job;
                _job = null;
            }

            job?.Dispose();
            process.Dispose();
        }

        private static string SanitizeTitle(string title)
        {
            string value = string.IsNullOrWhiteSpace(title) ? "Video" : title.Trim();
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= 100 ? value : value.Substring(0, 100);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlayerService));
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
