using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using IceTube.Configuration;
using IceTube.Logging;
using IceTube.Models;

namespace IceTube.Services
{
    public sealed class YtDlpStreamResolver : IStreamResolver
    {
        private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(90);
        private readonly AppSettings _settings;
        private readonly ToolLocator _tools;
        private readonly ProcessRunner _runner;

        public YtDlpStreamResolver(AppSettings settings, ToolLocator tools)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _runner = new ProcessRunner();
        }

        public async Task<VideoInfo> ResolveAsync(string url, CancellationToken cancellationToken)
        {
            Uri sourceUri;
            if (!TryValidateYouTubeUrl(url, out sourceUri))
            {
                throw new StreamResolutionException("请输入有效的 YouTube 视频网址（youtube.com 或 youtu.be）。");
            }

            EnsureToolExists(_tools.YtDlpPath, "找不到 yt-dlp.exe");
            EnsureToolExists(_tools.FfmpegPath, "找不到 ffmpeg.exe");
            EnsureToolExists(_tools.FfprobePath, "找不到 ffprobe.exe");
            EnsureToolExists(_tools.JavaScriptRuntimePath, "找不到 QuickJS 运行时 qjs.exe");

            List<string> arguments = new List<string>
            {
                "--ignore-config",
                "--no-playlist",
                "--dump-single-json",
                "--socket-timeout", "20",
                "--retries", "2",
                "--ffmpeg-location", _tools.FfmpegDirectory,
                "--js-runtimes", "quickjs:" + _tools.JavaScriptRuntimePath,
                "--",
                sourceUri.AbsoluteUri
            };

            LogService.Info("Resolving a YouTube URL with yt-dlp.");
            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(_tools.YtDlpPath, arguments, ResolveTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                throw new StreamResolutionException("解析超时。请检查网络后重试。", ex);
            }
            catch (Exception ex)
            {
                throw new StreamResolutionException("无法启动 yt-dlp。请确认发布目录完整，且安全软件没有阻止它。", ex);
            }

            if (result.ExitCode != 0)
            {
                string message = TranslateYtDlpError(result.StandardError);
                LogService.Error("yt-dlp returned exit code " + result.ExitCode + ".", null);
                throw new StreamResolutionException(message);
            }

            try
            {
                return ParseAndSelect(result.StandardOutput, sourceUri.AbsoluteUri);
            }
            catch (StreamResolutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new StreamResolutionException("yt-dlp 返回了无法识别的视频信息。请尝试更新 yt-dlp。", ex);
            }
        }

        private VideoInfo ParseAndSelect(string json, string sourceUrl)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new StreamResolutionException("yt-dlp 没有返回视频信息。");
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new StreamResolutionException("yt-dlp 返回的视频信息格式无效。");

            IList formats = GetList(root, "formats");
            if (formats == null || formats.Count == 0)
            {
                throw new StreamResolutionException("视频没有可用格式，可能已删除、设为私人或受到地区限制。");
            }

            List<FormatCandidate> candidates = formats
                .Cast<object>()
                .Select(item => FormatCandidate.From(item as Dictionary<string, object>))
                .Where(item => item != null)
                .ToList();

            FormatCandidate video = candidates
                .Where(IsAllowedVideo)
                .OrderByDescending(item => item.Height)
                .ThenByDescending(item => item.HasAudio)
                .ThenByDescending(item => item.IsMp4)
                .ThenByDescending(item => item.Fps)
                .ThenBy(item => item.VideoBitrate > 0 ? item.VideoBitrate : double.MaxValue)
                .FirstOrDefault();

            if (video == null)
            {
                throw new StreamResolutionException(
                    "找不到 H.264/AVC、" + _settings.MaxHeight + "p 以下且不超过 " +
                    _settings.MaxFps + "fps 的格式。IceTube 不会退回 VP9 或 AV1。");
            }

            FormatCandidate audio = null;
            if (!video.HasAudio)
            {
                audio = candidates
                    .Where(item => item.IsAudioOnly)
                    .OrderByDescending(item => item.IsAac)
                    .ThenByDescending(item => item.AudioBitrate > 0 && item.AudioBitrate <= 160)
                    .ThenByDescending(item => item.AudioBitrate)
                    .FirstOrDefault();

                if (audio == null)
                {
                    throw new StreamResolutionException("找到了兼容视频流，但没有找到可播放的音频流。");
                }
            }

            string userAgent = FirstNonEmpty(video.UserAgent, audio == null ? null : audio.UserAgent, GetHeader(root, "User-Agent"));
            string referer = FirstNonEmpty(video.Referer, audio == null ? null : audio.Referer, GetHeader(root, "Referer"));

            VideoInfo info = new VideoInfo
            {
                Title = GetString(root, "title", "Untitled video"),
                VideoId = GetString(root, "id", string.Empty),
                SourceUrl = sourceUrl,
                DurationSeconds = GetDouble(root, "duration"),
                Width = video.Width,
                Height = video.Height,
                Fps = video.Fps,
                VideoCodec = video.VideoCodec,
                AudioCodec = video.HasAudio ? video.AudioCodec : audio.AudioCodec,
                FormatId = video.FormatId + (audio == null ? string.Empty : "+" + audio.FormatId),
                VideoStreamUrl = video.Url,
                AudioStreamUrl = video.HasAudio ? null : audio.Url,
                UserAgent = userAgent,
                Referer = referer
            };

            LogService.Info("Selected format " + info.FormatId + ": " + info.DisplayFormat + ".");
            return info;
        }

        private bool IsAllowedVideo(FormatCandidate candidate)
        {
            if (!candidate.HasVideo || string.IsNullOrWhiteSpace(candidate.Url)) return false;
            if (candidate.Height <= 0 || candidate.Height > _settings.MaxHeight) return false;
            if (candidate.Fps > _settings.MaxFps + 0.01) return false;

            string codec = candidate.VideoCodec ?? string.Empty;
            string[] preferences = (_settings.CodecPreference ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            return preferences.Any(preference =>
                codec.IndexOf(preference.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string TranslateYtDlpError(string error)
        {
            string text = (error ?? string.Empty).ToLowerInvariant();
            if (text.Contains("private video")) return "该视频是私人视频，无法播放。";
            if (text.Contains("not available in your country") || text.Contains("geo restricted"))
                return "该视频在当前地区不可用。";
            if (text.Contains("video unavailable") || text.Contains("this video is unavailable"))
                return "视频不存在、已删除或当前不可用。";
            if (text.Contains("timed out") || text.Contains("unable to download") || text.Contains("network"))
                return "网络请求失败。请检查网络连接后重试。";
            if (text.Contains("javascript runtime") || text.Contains("js runtime") || text.Contains("quickjs"))
                return "YouTube 解析需要 QuickJS，但当前运行时不可用或版本不兼容。";
            return "yt-dlp 无法解析该视频。请检查网址、网络或更新 yt-dlp。";
        }

        private static IList GetList(IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary.TryGetValue(key, out value) ? value as IList : null;
        }

        private static string GetString(IDictionary<string, object> dictionary, string key, string fallback)
        {
            if (dictionary == null) return fallback;
            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }

        private static double GetDouble(IDictionary<string, object> dictionary, string key)
        {
            if (dictionary == null) return 0;
            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null) return 0;
            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static int GetInt(IDictionary<string, object> dictionary, string key)
        {
            return (int)Math.Round(GetDouble(dictionary, key));
        }

        private static string GetHeader(IDictionary<string, object> dictionary, string name)
        {
            object raw;
            if (dictionary == null || !dictionary.TryGetValue("http_headers", out raw)) return null;
            Dictionary<string, object> headers = raw as Dictionary<string, object>;
            return GetString(headers, name, null);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private sealed class FormatCandidate
        {
            public string FormatId { get; private set; }
            public string Url { get; private set; }
            public string VideoCodec { get; private set; }
            public string AudioCodec { get; private set; }
            public int Width { get; private set; }
            public int Height { get; private set; }
            public double Fps { get; private set; }
            public double VideoBitrate { get; private set; }
            public double AudioBitrate { get; private set; }
            public bool IsMp4 { get; private set; }
            public string UserAgent { get; private set; }
            public string Referer { get; private set; }

            public bool HasVideo => !string.IsNullOrWhiteSpace(VideoCodec) &&
                                    !VideoCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec) &&
                                    !AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            public bool IsAudioOnly => !HasVideo && HasAudio && !string.IsNullOrWhiteSpace(Url);
            public bool IsAac => (AudioCodec ?? string.Empty).IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (AudioCodec ?? string.Empty).IndexOf("aac", StringComparison.OrdinalIgnoreCase) >= 0;

            public static FormatCandidate From(Dictionary<string, object> dictionary)
            {
                if (dictionary == null) return null;
                return new FormatCandidate
                {
                    FormatId = GetString(dictionary, "format_id", "unknown"),
                    Url = GetString(dictionary, "url", null),
                    VideoCodec = GetString(dictionary, "vcodec", "none"),
                    AudioCodec = GetString(dictionary, "acodec", "none"),
                    Width = GetInt(dictionary, "width"),
                    Height = GetInt(dictionary, "height"),
                    Fps = GetDouble(dictionary, "fps"),
                    VideoBitrate = GetDouble(dictionary, "vbr"),
                    AudioBitrate = GetDouble(dictionary, "abr"),
                    IsMp4 = GetString(dictionary, "ext", string.Empty).Equals("mp4", StringComparison.OrdinalIgnoreCase),
                    UserAgent = GetHeader(dictionary, "User-Agent"),
                    Referer = GetHeader(dictionary, "Referer")
                };
            }
        }

        private static bool TryValidateYouTubeUrl(string url, out Uri uri)
        {
            uri = null;
            Uri parsed;
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                return false;
            }

            string host = parsed.DnsSafeHost;
            bool allowed = host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                           host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                           host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
                           host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
                           host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);
            if (!allowed) return false;

            uri = parsed;
            return true;
        }

        private static void EnsureToolExists(string path, string message)
        {
            if (!File.Exists(path))
            {
                throw new StreamResolutionException(message + "。请确认完整复制了 IceTube 发布目录。\r\n" + path);
            }
        }
    }
}
