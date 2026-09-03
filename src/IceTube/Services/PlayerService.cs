using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IceTube.Logging;
using IceTube.Models;

namespace IceTube.Services
{
    public sealed class PlayerExitedEventArgs : EventArgs
    {
        public PlayerExitedEventArgs(int exitCode, bool wasStopped, string errorMessage)
        {
            ExitCode = exitCode;
            WasStopped = wasStopped;
            ErrorMessage = errorMessage;
        }

        public int ExitCode { get; private set; }
        public bool WasStopped { get; private set; }
        public string ErrorMessage { get; private set; }
    }

    public sealed class PlayerService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly ToolLocator _tools;
        private Process _process;
        private ProcessJob _job;
        private LocalMediaProxy _mediaProxy;
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
            string logDirectory = Path.Combine(_tools.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            string logPath = Path.Combine("logs", "mpv-last.log");

            LocalMediaProxy mediaProxy = new LocalMediaProxy(video);
            mediaProxy.Start();

            List<string> arguments = new List<string>
            {
                "--no-config",
                "--force-window=immediate",
                "--terminal=no",
                "--profile=fast",
                "--vo=direct3d,gpu,",
                "--hwdec=no",
                "--vd-lavc-threads=2",
                "--framedrop=vo",
                "--video-sync=audio",
                "--cache=yes",
                "--demuxer-max-bytes=20MiB",
                "--cache-pause=yes",
                "--log-file=" + logPath,
                "--title=IceTube - " + SanitizeTitle(video.Title)
            };

            if (!string.IsNullOrWhiteSpace(mediaProxy.AudioUrl))
            {
                arguments.Add("--audio-file=" + mediaProxy.AudioUrl);
            }
            arguments.Add("--");
            arguments.Add(mediaProxy.VideoUrl);

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
                _mediaProxy = mediaProxy;
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("mpv 没有启动。");
                    job.TryAssign(process);
                }
                catch
                {
                    _process = null;
                    _job = null;
                    _mediaProxy = null;
                    process.Dispose();
                    job.Dispose();
                    mediaProxy.Dispose();
                    throw;
                }
            }

            LogService.Info("mpv started for format " + video.FormatId + ".");
        }

        public void Stop()
        {
            Process process;
            lock (_sync)
            {
                process = _process;
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
                ClearProcess(process);
            }
        }

        private void OnProcessExited(object sender, EventArgs eventArgs)
        {
            Process process = sender as Process;
            if (process == null) return;

            int exitCode = -1;
            bool stopped;
            string streamError;
            try { exitCode = process.ExitCode; } catch { }

            lock (_sync)
            {
                if (!ReferenceEquals(_process, process)) return;
                stopped = _stopRequested;
                streamError = _mediaProxy == null ? null : _mediaProxy.LastError;
            }
            ClearProcess(process);
            LogService.Info("mpv exited with code " + exitCode + ".");
            PlayerExited?.Invoke(this, new PlayerExitedEventArgs(exitCode, stopped, streamError));
        }

        private void ClearProcess(Process process)
        {
            ProcessJob job = null;
            LocalMediaProxy mediaProxy = null;
            lock (_sync)
            {
                if (!ReferenceEquals(_process, process)) return;
                _process = null;
                job = _job;
                _job = null;
                mediaProxy = _mediaProxy;
                _mediaProxy = null;
            }

            mediaProxy?.Dispose();
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
