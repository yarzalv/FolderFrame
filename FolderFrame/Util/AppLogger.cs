using System;
using System.IO;

namespace FolderFrame.Util
{
    public static class AppLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot.log");

        private static readonly string _appDataLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderFrame", "boot.log");

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                WriteSafe(_logPath, line);
                WriteSafe(_appDataLogPath, line);
            }
        }

        private static void WriteSafe(string path, string text)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.Write(text);
            }
            catch { }
        }
    }
}
