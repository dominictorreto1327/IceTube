using System;
using System.Collections.Generic;
using System.IO;
using IceTube.Configuration;

namespace IceTube.Services
{
    public sealed class ToolLocator
    {
        private readonly string _baseDirectory;
        private readonly AppSettings _settings;

        public ToolLocator(string baseDirectory, AppSettings settings)
        {
            _baseDirectory = EnsureTrailingSeparator(Path.GetFullPath(baseDirectory));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public string BaseDirectory => _baseDirectory;
        public string YtDlpPath => ResolvePortablePath(_settings.YtDlpPath);
        public string MpvPath => ResolvePortablePath(_settings.MpvPath);
        public string FfmpegPath => ResolvePortablePath(_settings.FfmpegPath);
        public string FfprobePath => ResolvePortablePath(_settings.FfprobePath);
        public string JavaScriptRuntimePath => ResolvePortablePath(_settings.JavaScriptRuntimePath);
        public string FfmpegDirectory => Path.GetDirectoryName(FfmpegPath);

        public IList<string> GetMissingTools()
        {
            List<string> missing = new List<string>();
            AddIfMissing(missing, "yt-dlp", YtDlpPath);
            AddIfMissing(missing, "mpv", MpvPath);
            AddIfMissing(missing, "ffmpeg", FfmpegPath);
            AddIfMissing(missing, "ffprobe", FfprobePath);
            AddIfMissing(missing, "QuickJS", JavaScriptRuntimePath);
            return missing;
        }

        private string ResolvePortablePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException("工具路径必须是相对于 IceTube.exe 的路径。");
            }

            string fullPath = Path.GetFullPath(Path.Combine(_baseDirectory, relativePath));
            if (!fullPath.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("工具路径不能指向 IceTube 目录以外的位置。");
            }

            return fullPath;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static void AddIfMissing(ICollection<string> missing, string name, string path)
        {
            if (!File.Exists(path)) missing.Add(name);
        }
    }
}
