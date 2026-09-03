using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace IceTube.Configuration
{
    public sealed class AppSettings
    {
        public int MaxHeight { get; set; }
        public int MaxFps { get; set; }
        public string CodecPreference { get; set; }
        public string YtDlpPath { get; set; }
        public string MpvPath { get; set; }
        public string FfmpegPath { get; set; }
        public string FfprobePath { get; set; }
        public string JavaScriptRuntimePath { get; set; }

        public static AppSettings CreateDefaults()
        {
            return new AppSettings
            {
                MaxHeight = 480,
                MaxFps = 30,
                CodecPreference = "avc1,h264",
                YtDlpPath = @"tools\yt-dlp\yt-dlp.exe",
                MpvPath = @"tools\mpv\mpv.exe",
                FfmpegPath = @"tools\ffmpeg\ffmpeg.exe",
                FfprobePath = @"tools\ffmpeg\ffprobe.exe",
                JavaScriptRuntimePath = @"tools\js-runtime\qjs.exe"
            };
        }

        public static AppSettings LoadOrCreate(string baseDirectory)
        {
            string dataDirectory = Path.Combine(baseDirectory, "data");
            string settingsPath = Path.Combine(dataDirectory, "settings.json");
            Directory.CreateDirectory(dataDirectory);

            if (!File.Exists(settingsPath))
            {
                AppSettings defaults = CreateDefaults();
                defaults.Save(settingsPath);
                return defaults;
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                AppSettings loaded = serializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath, Encoding.UTF8));
                return Normalize(loaded ?? CreateDefaults());
            }
            catch
            {
                string backupPath = settingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(settingsPath, backupPath, true);
                AppSettings defaults = CreateDefaults();
                defaults.Save(settingsPath);
                return defaults;
            }
        }

        private static AppSettings Normalize(AppSettings settings)
        {
            AppSettings defaults = CreateDefaults();
            settings.MaxHeight = settings.MaxHeight > 0 && settings.MaxHeight <= 2160 ? settings.MaxHeight : defaults.MaxHeight;
            settings.MaxFps = settings.MaxFps > 0 && settings.MaxFps <= 240 ? settings.MaxFps : defaults.MaxFps;
            settings.CodecPreference = ValueOrDefault(settings.CodecPreference, defaults.CodecPreference);
            settings.YtDlpPath = ValueOrDefault(settings.YtDlpPath, defaults.YtDlpPath);
            settings.MpvPath = ValueOrDefault(settings.MpvPath, defaults.MpvPath);
            settings.FfmpegPath = ValueOrDefault(settings.FfmpegPath, defaults.FfmpegPath);
            settings.FfprobePath = ValueOrDefault(settings.FfprobePath, defaults.FfprobePath);
            settings.JavaScriptRuntimePath = ValueOrDefault(settings.JavaScriptRuntimePath, defaults.JavaScriptRuntimePath);
            return settings;
        }

        private static string ValueOrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private void Save(string settingsPath)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(this);
            File.WriteAllText(settingsPath, PrettyPrint(json), new UTF8Encoding(false));
        }

        private static string PrettyPrint(string json)
        {
            StringBuilder output = new StringBuilder();
            bool quoted = false;
            bool escaped = false;
            int indent = 0;

            foreach (char character in json)
            {
                if (character == '\\' && !escaped)
                {
                    escaped = true;
                    output.Append(character);
                    continue;
                }

                if (character == '"' && !escaped)
                {
                    quoted = !quoted;
                }

                escaped = false;
                if (quoted)
                {
                    output.Append(character);
                    continue;
                }

                switch (character)
                {
                    case '{':
                    case '[':
                        output.Append(character).AppendLine();
                        indent++;
                        output.Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        output.AppendLine();
                        indent--;
                        output.Append(new string(' ', indent * 2)).Append(character);
                        break;
                    case ',':
                        output.Append(character).AppendLine();
                        output.Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        output.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(character)) output.Append(character);
                        break;
                }
            }

            return output.ToString();
        }
    }
}
