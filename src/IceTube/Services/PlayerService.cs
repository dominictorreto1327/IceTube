using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
        private CancellationTokenSource _streamCancellation;
        private HttpWebRequest _activeRequest;
        private string _streamError;
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
            bool useManagedTransport = string.IsNullOrWhiteSpace(video.AudioStreamUrl);

            List<string> arguments = new List<string>
            {
                "--no-config",
                "--force-window=immediate",
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

            if (useManagedTransport)
            {
                // The .NET HTTP stack remains reliable on systems where libavformat cannot
                // reach YouTube's media CDN. Bytes are streamed to mpv without transcoding.
                string logDirectory = Path.Combine(_tools.BaseDirectory, "logs");
                Directory.CreateDirectory(logDirectory);
                arguments.Add("--demuxer-lavf-format=mp4");
                arguments.Add("--log-file=" + Path.Combine(logDirectory, "mpv-last.log"));
                arguments.Add("--");
                arguments.Add("-");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(video.UserAgent))
                    arguments.Add("--user-agent=" + video.UserAgent);
                if (!string.IsNullOrWhiteSpace(video.Referer))
                    arguments.Add("--referrer=" + video.Referer);
                arguments.Add("--audio-file=" + video.AudioStreamUrl);
                arguments.Add("--");
                arguments.Add(video.VideoStreamUrl);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _tools.MpvPath,
                Arguments = WindowsCommandLine.Join(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = useManagedTransport,
                WorkingDirectory = _tools.BaseDirectory
            };

            Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            ProcessJob job = new ProcessJob();
            CancellationTokenSource streamCancellation = useManagedTransport ? new CancellationTokenSource() : null;
            process.Exited += OnProcessExited;

            lock (_sync)
            {
                ThrowIfDisposed();
                _stopRequested = false;
                _streamError = null;
                _process = process;
                _job = job;
                _streamCancellation = streamCancellation;
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("mpv 没有启动。");
                    job.TryAssign(process);
                }
                catch
                {
                    _process = null;
                    _job = null;
                    _streamCancellation = null;
                    process.Dispose();
                    job.Dispose();
                    streamCancellation?.Dispose();
                    throw;
                }
            }

            if (useManagedTransport)
            {
                _ = PumpStreamAsync(video, process, streamCancellation.Token);
            }

            LogService.Info("mpv started for format " + video.FormatId + ".");
        }

        public void Stop()
        {
            Process process;
            CancellationTokenSource cancellation;
            HttpWebRequest request;
            lock (_sync)
            {
                process = _process;
                if (process == null) return;
                _stopRequested = true;
                cancellation = _streamCancellation;
                request = _activeRequest;
            }

            try
            {
                cancellation?.Cancel();
                try { request?.Abort(); } catch { }
                try { process.StandardInput.Close(); } catch { }
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
                stopped = _stopRequested;
                streamError = _streamError;
            }
            ClearProcess(process);
            LogService.Info("mpv exited with code " + exitCode + ".");
            PlayerExited?.Invoke(this, new PlayerExitedEventArgs(exitCode, stopped, streamError));
        }

        private void ClearProcess(Process process)
        {
            ProcessJob job = null;
            CancellationTokenSource cancellation = null;
            HttpWebRequest request = null;
            lock (_sync)
            {
                if (!ReferenceEquals(_process, process)) return;
                _process = null;
                job = _job;
                _job = null;
                cancellation = _streamCancellation;
                _streamCancellation = null;
                request = _activeRequest;
                _activeRequest = null;
            }

            try { cancellation?.Cancel(); } catch { }
            try { request?.Abort(); } catch { }
            cancellation?.Dispose();
            job?.Dispose();
            process.Dispose();
        }

        private async Task PumpStreamAsync(VideoInfo video, Process process, CancellationToken cancellationToken)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(video.VideoStreamUrl);
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.KeepAlive = true;
            if (!string.IsNullOrWhiteSpace(video.UserAgent)) request.UserAgent = video.UserAgent;
            if (!string.IsNullOrWhiteSpace(video.Referer)) request.Referer = video.Referer;

            lock (_sync)
            {
                if (!ReferenceEquals(_process, process))
                {
                    request.Abort();
                    return;
                }
                _activeRequest = request;
            }

            try
            {
                using (cancellationToken.Register(request.Abort))
                using (WebResponse response = await request.GetResponseAsync().ConfigureAwait(false))
                using (Stream input = response.GetResponseStream())
                using (Stream output = process.StandardInput.BaseStream)
                {
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                bool report;
                lock (_sync)
                {
                    report = ReferenceEquals(_process, process) && !_stopRequested && !cancellationToken.IsCancellationRequested;
                    if (report) _streamError = "媒体网络传输失败。请检查网络后重试。";
                }

                if (report)
                {
                    LogService.Error("Managed media transport failed.", ex);
                    try { if (!process.HasExited) process.Kill(); } catch { }
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_activeRequest, request)) _activeRequest = null;
                }
            }
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
