using System;
using System.IO;

namespace FluentCanvas.Helpers
{
    internal static class DelAutoSavedFiles
    {
        private static readonly string[] AutoSaveFolders =
        {
            "Auto Saved - Annotation Strokes", "Auto Saved - BlackBoard Strokes",
            "Auto Saved - Presentations", "Auto Saved - Screenshots", "Auto Saved - PPT Screenshots"
        };

        public static void DeleteFilesOlder(string directoryPath, int daysThreshold)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Path.IsPathFullyQualified(directoryPath) || daysThreshold <= 0)
                return;

            var cutoff = DateTime.UtcNow.AddDays(-Math.Min(daysThreshold, 36500));
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string name in AutoSaveFolders)
            {
                var folder = new DirectoryInfo(Path.Combine(directoryPath, name));
                try
                {
                    if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var file in folder.EnumerateFiles("*", options))
                    {
                        if (file.CreationTimeUtc >= cutoff || file.LastWriteTimeUtc >= cutoff) continue;
                        if (file.Extension.Equals(".icstk", StringComparison.OrdinalIgnoreCase) ||
                            file.Extension.Equals(".icart", StringComparison.OrdinalIgnoreCase) ||
                            file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                            file.Name.Equals("Position", StringComparison.OrdinalIgnoreCase))
                            file.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogHelper.WriteLogToFile($"Auto save cleanup: {ex.Message}", LogHelper.LogType.Error);
                }
            }
        }
    }
}
