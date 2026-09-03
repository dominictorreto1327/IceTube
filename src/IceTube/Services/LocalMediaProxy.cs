using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IceTube.Logging;
using IceTube.Models;

namespace IceTube.Services
{
    // mpv's bundled FFmpeg cannot always negotiate HTTPS with YouTube's CDN on
    // old Windows installations. This tiny loopback proxy lets .NET perform the
    // remote HTTPS requests while mpv reads ordinary local HTTP streams. Unlike
    // the old stdin bridge, it can expose video and audio as separate streams.
    internal sealed class LocalMediaProxy : IDisposable
    {
        private const int HeaderLimit = 32 * 1024;
        private const int BufferSize = 64 * 1024;

        private readonly object _sync = new object();
        private readonly VideoInfo _video;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly List<HttpWebRequest> _requests = new List<HttpWebRequest>();
        private TcpListener _listener;
        private Task _acceptTask;
        private bool _disposed;
        private string _lastError;

        public LocalMediaProxy(VideoInfo video)
        {
            _video = video ?? throw new ArgumentNullException(nameof(video));
        }

        public string VideoUrl { get; private set; }
        public string AudioUrl { get; private set; }

        public string LastError
        {
            get
            {
                lock (_sync) return _lastError;
            }
        }

        public void Start()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LocalMediaProxy));
                if (_listener != null) throw new InvalidOperationException("媒体代理已经启动。");

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start(8);
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                VideoUrl = "http://127.0.0.1:" + port + "/video";
                AudioUrl = string.IsNullOrWhiteSpace(_video.AudioStreamUrl)
                    ? null
                    : "http://127.0.0.1:" + port + "/audio";
                _acceptTask = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
            }

            LogService.Info("Local media proxy started for " +
                            (AudioUrl == null ? "combined media." : "separate video and audio."));
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    throw;
                }

                lock (_sync)
                {
                    if (_disposed)
                    {
                        client.Close();
                        return;
                    }
                    _clients.Add(client);
                }

                _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                client.ReceiveTimeout = 60000;
                client.SendTimeout = 60000;
                using (client)
                using (NetworkStream output = client.GetStream())
                {
                    string requestText = await ReadHeadersAsync(output, cancellationToken).ConfigureAwait(false);
                    ParsedRequest localRequest = ParsedRequest.Parse(requestText);
                    if (localRequest == null ||
                        (!localRequest.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                         !localRequest.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                    {
                        await WriteSimpleResponseAsync(output, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    string route = localRequest.Path.Split('?')[0];
                    string remoteUrl = route.Equals("/video", StringComparison.OrdinalIgnoreCase)
                        ? _video.VideoStreamUrl
                        : route.Equals("/audio", StringComparison.OrdinalIgnoreCase)
                            ? _video.AudioStreamUrl
                            : null;
                    if (string.IsNullOrWhiteSpace(remoteUrl))
                    {
                        await WriteSimpleResponseAsync(output, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await ProxyRequestAsync(localRequest, route, remoteUrl, output, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal during stop/exit.
            }
            catch (IOException ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    LogService.Info("Local player connection closed: " + ex.Message);
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    RecordTransportError("本地媒体代理发生错误。", ex);
            }
            finally
            {
                lock (_sync) _clients.Remove(client);
            }
        }

        private async Task ProxyRequestAsync(
            ParsedRequest localRequest,
            string route,
            string remoteUrl,
            Stream output,
            CancellationToken cancellationToken)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(remoteUrl);
            request.Method = localRequest.Method;
            request.ProtocolVersion = HttpVersion.Version11;
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.KeepAlive = true;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Accept = "*/*";
            if (!string.IsNullOrWhiteSpace(_video.UserAgent)) request.UserAgent = _video.UserAgent;
            if (!string.IsNullOrWhiteSpace(_video.Referer)) request.Referer = _video.Referer;

            string range = localRequest.GetHeader("Range");
            ApplyRange(request, range);

            lock (_sync) _requests.Add(request);
            try
            {
                using (cancellationToken.Register(request.Abort))
                using (HttpWebResponse response =
                       (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    await WriteResponseHeadersAsync(output, response, cancellationToken).ConfigureAwait(false);
                    LogService.Info("Media proxy " + route + " returned HTTP " + (int)response.StatusCode +
                                    (string.IsNullOrWhiteSpace(range) ? "." : " for " + range + "."));

                    if (localRequest.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;
                    using (Stream input = response.GetResponseStream())
                    {
                        await CopyResponseAsync(input, output, route, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (WebException ex)
            {
                if (cancellationToken.IsCancellationRequested) return;
                RecordTransportError(BuildWebErrorMessage(ex), ex);
                try
                {
                    await WriteSimpleResponseAsync(output, 502, "Bad Gateway", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // mpv may already have closed the local connection.
                }
            }
            finally
            {
                lock (_sync) _requests.Remove(request);
            }
        }

        private static async Task CopyResponseAsync(
            Stream input,
            Stream output,
            string route,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[BufferSize];
            long total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                total += read;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            LogService.Info("Media proxy " + route + " completed after " + total + " bytes.");
        }

        private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024];
            MemoryStream collected = new MemoryStream();
            while (collected.Length < HeaderLimit)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                collected.Write(buffer, 0, read);
                byte[] bytes = collected.GetBuffer();
                int length = (int)collected.Length;
                for (int index = Math.Max(0, length - read - 3); index <= length - 4; index++)
                {
                    if (bytes[index] == 13 && bytes[index + 1] == 10 &&
                        bytes[index + 2] == 13 && bytes[index + 3] == 10)
                    {
                        return Encoding.ASCII.GetString(bytes, 0, index + 4);
                    }
                }
            }
            throw new InvalidDataException("本地播放器发送了无效的 HTTP 请求。");
        }

        private static async Task WriteResponseHeadersAsync(
            Stream output,
            HttpWebResponse response,
            CancellationToken cancellationToken)
        {
            StringBuilder headers = new StringBuilder();
            headers.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
                .Append(SafeHeaderValue(response.StatusDescription)).Append("\r\n");
            headers.Append("Content-Type: ").Append(SafeHeaderValue(
                string.IsNullOrWhiteSpace(response.ContentType) ? "application/octet-stream" : response.ContentType))
                .Append("\r\n");
            if (response.ContentLength >= 0)
                headers.Append("Content-Length: ").Append(response.ContentLength).Append("\r\n");
            AppendHeaderIfPresent(headers, "Content-Range", response.Headers["Content-Range"]);
            AppendHeaderIfPresent(headers, "Accept-Ranges", response.Headers["Accept-Ranges"]);
            headers.Append("Connection: close\r\n\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(headers.ToString());
            await output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteSimpleResponseAsync(
            Stream output,
            int statusCode,
            string reason,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + statusCode + " " + reason + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void AppendHeaderIfPresent(StringBuilder target, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                target.Append(name).Append(": ").Append(SafeHeaderValue(value)).Append("\r\n");
        }

        private static string SafeHeaderValue(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private static void ApplyRange(HttpWebRequest request, string range)
        {
            if (string.IsNullOrWhiteSpace(range) ||
                !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return;

            string value = range.Substring(6).Trim();
            string[] parts = value.Split('-');
            if (parts.Length != 2) return;
            long from;
            long to;
            if (long.TryParse(parts[0], out from))
            {
                if (long.TryParse(parts[1], out to)) request.AddRange(from, to);
                else request.AddRange(from);
            }
        }

        private void RecordTransportError(string message, Exception exception)
        {
            lock (_sync)
            {
                if (_disposed || _cancellation.IsCancellationRequested) return;
                _lastError = message;
            }
            LogService.Error("Media proxy transport failed.", exception);
        }

        private static string BuildWebErrorMessage(WebException exception)
        {
            if (exception.Status == WebExceptionStatus.Timeout)
                return "连接 YouTube 媒体服务器超时。";
            if (exception.Status == WebExceptionStatus.SecureChannelFailure ||
                exception.Status == WebExceptionStatus.TrustFailure)
                return "无法建立 HTTPS 安全连接。请检查 Windows 更新、系统时间和 TLS 1.2。";

            HttpWebResponse response = exception.Response as HttpWebResponse;
            if (response != null)
                return "YouTube 媒体服务器返回 HTTP " + (int)response.StatusCode + "。请重新解析后重试。";
            return "无法从 YouTube 媒体服务器接收数据（" + exception.Status + "）。";
        }

        public void Dispose()
        {
            TcpListener listener;
            TcpClient[] clients;
            HttpWebRequest[] requests;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _cancellation.Cancel();
                listener = _listener;
                _listener = null;
                clients = _clients.ToArray();
                _clients.Clear();
                requests = _requests.ToArray();
                _requests.Clear();
            }

            try { listener?.Stop(); } catch { }
            foreach (HttpWebRequest request in requests)
            {
                try { request.Abort(); } catch { }
            }
            foreach (TcpClient client in clients)
            {
                try { client.Close(); } catch { }
            }
            _cancellation.Dispose();
            LogService.Info("Local media proxy stopped.");
        }

        private sealed class ParsedRequest
        {
            private readonly Dictionary<string, string> _headers;

            private ParsedRequest(string method, string path, Dictionary<string, string> headers)
            {
                Method = method;
                Path = path;
                _headers = headers;
            }

            public string Method { get; private set; }
            public string Path { get; private set; }

            public string GetHeader(string name)
            {
                string value;
                return _headers.TryGetValue(name, out value) ? value : null;
            }

            public static ParsedRequest Parse(string text)
            {
                string[] lines = (text ?? string.Empty).Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return null;
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) return null;

                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 1; index < lines.Length; index++)
                {
                    int separator = lines[index].IndexOf(':');
                    if (separator <= 0) continue;
                    headers[lines[index].Substring(0, separator).Trim()] =
                        lines[index].Substring(separator + 1).Trim();
                }
                return new ParsedRequest(first[0], first[1], headers);
            }
        }
    }
}
