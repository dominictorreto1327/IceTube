using System;
using System.IO;
using System.Linq;
using System.Text;

namespace IceTube.Logging
{
    internal static class LogService
    {
        private const int MaxLogFiles = 5;
        private const long MaxLogSizeBytes = 256 * 1024;
        private static readonly object Sync = new object();
        private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
                    if (exception != null) line += Environment.NewLine + exception;
                    File.AppendAllText(CurrentLogPath(), line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never crash the player launcher.
            }
        }

        private static string CurrentLogPath()
        {
            return Path.Combine(LogDirectory, "icetube-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }

        private static void RotateIfNeeded()
        {
            string current = CurrentLogPath();
            if (File.Exists(current) && new FileInfo(current).Length > MaxLogSizeBytes)
            {
                string archived = Path.Combine(LogDirectory, "icetube-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.Move(current, archived);
            }

            FileInfo[] files = new DirectoryInfo(LogDirectory)
                .GetFiles("icetube-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            foreach (FileInfo file in files.Skip(MaxLogFiles)) file.Delete();
        }
    }
}
