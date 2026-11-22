using System;
using System.IO;

namespace FluentCanvas.Helpers
{
    class LogHelper
    {
        private const string LogFile = "Log.txt";

        internal static void TrimStartupLog()
        {
            try
            {
                var file = new FileInfo(Path.Combine(StorageHelper.UserDataFolder, LogFile));
                if (file.Exists && file.Length / 1024 > 512) file.Delete();
            }
            catch (Exception ex) { WriteLogToFile(ex.ToString(), LogType.Error); }
        }

        public static void NewLog(string str)
        {
            WriteLogToFile(str, LogType.Info);
        }

        public static void WriteLogToFile(string message, LogType type = LogType.Info)
        {
            try
            {
                string path = Path.Combine(StorageHelper.UserDataFolder, LogFile);
                using var writer = new StreamWriter(path, append: true);
                writer.WriteLine($"{DateTime.Now:O} [{type}] {message}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Logging is best effort, including while a tool process has the file open.
            }
        }
        public enum LogType
        {
            Info,
            Trace,
            Error,
            Event
        }
    }
}
