using FluentCanvas;
using FluentCanvas.Helpers;
using FluentCanvas.Services;

var root = Path.Combine(Path.GetTempPath(), "FluentCanvas-Lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
StorageHelper.UserDataFolder = root;
int count = 0;
void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    count++;
    Console.WriteLine($"PASS {description}");
}
try
{
    var store = new LegacySettingsService();
    Check(!store.Load(), "Empty profile is recognized");
    store.Legacy = """{"canvas":{"inkWidth":7},"automation":null}""";
    Check(store.Load() && store.Model.Canvas.InkWidth == 7 && store.Model.Automation != null, "Reads legacy LocalSettings including old null sections");

    store.Model.Canvas.InkWidth = 9;
    store.Save();
    var next = new LegacySettingsService { Legacy = store.Legacy };
    Check(next.Load() && next.Model.Canvas.InkWidth == 9, "Restart picks the last file save over stale LocalSettings");
    next.Model.Canvas.InkWidth = 12;
    next.Save();

    string path = Path.Combine(root, "Settings.json");
    Check(File.Exists(path + ".bak"), "Successful replacement keeps a recovery copy");
    using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool failed = false;
        next.Model.Canvas.InkWidth = 15;
        try { next.Save(); } catch (IOException) { failed = true; }
        Check(failed, "Locked destination reports a save failure");
    }
    Check(next.Load() && next.Model.Canvas.InkWidth == 12, "Failed save preserves previous settings");
    Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Failed save removes staging file");

    File.WriteAllText(path, "partial");
    Check(next.Load() && next.Model.Canvas.InkWidth == 9, "Corrupt primary recovers from last valid file");
    File.WriteAllText(path + ".bak", "broken");
    Check(next.Load() && next.Model.Canvas.InkWidth == 7, "Corrupt files can still recover the legacy source");
    next.Legacy = null;
    bool reported = false;
    try { next.Load(); } catch (InvalidDataException) { reported = true; }
    Check(reported && File.ReadAllText(path) == "partial", "Unrecoverable file stays available for manual recovery");

    var paths = new[] { @"C:\教室 素材\第一课.icart", @"D:\" + new string('a', 200) + @"\" + new string('b', 90) + ".ICSTK", @"C:\two.icart" };
    string argsText = string.Join(" ", paths.Select(p => "\"" + p + "\""));
    Check(InkActivation.FromCommandLine(@" ""C:\Apps Here\FluentCanvas.exe"" " + argsText).SequenceEqual(paths), "Redirected Unicode, spaced and long paths preserve order");
    Check(InkActivation.FromCommandLine(argsText).SequenceEqual(paths), "Redirected argument string can omit the executable");
    Check(InkActivation.SelectFiles(new[] { "-m", paths[0], "ignored.txt", paths[0] }).SequenceEqual(new[] { paths[0], paths[0] }), "Cold launch preserves separate activations");

    var autosave = Path.Combine(root, "Auto Saved - Screenshots", "2026-01-01", "screen.png");
    var userSave = Path.Combine(root, "User Saved - Annotation Strokes", "lesson.icart");
    var unrelated = Path.Combine(root, "课程", "照片.png");
    foreach (var f in new[] { autosave, userSave, unrelated })
    {
        Directory.CreateDirectory(Path.GetDirectoryName(f));
        File.WriteAllText(f, "test");
        File.SetCreationTimeUtc(f, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-20));
    }
    DelAutoSavedFiles.DeleteFilesOlder(root, 7);
    Check(!File.Exists(autosave) && File.Exists(userSave) && File.Exists(unrelated), "Retention only removes files in known autosave folders");
    Console.WriteLine($"{count} lifecycle checks passed.");
}
finally { Directory.Delete(root, true); }

sealed class LegacySettingsService : LocalSettingsService
{
    public string Legacy { get; set; }
    protected override string ReadLegacySettings() => Legacy;
}

namespace FluentCanvas.Helpers
{
    // Substitute the OS-selected user folder. File IO, selection of the
    // settings source and all save/load code above are the application's.
    internal static class StorageHelper
    {
        public static string UserDataFolder { get; set; }
        public static string GetDefaultStorageRoot() => UserDataFolder;
        public static string NormalizeStorageLocation(string path) => path ?? UserDataFolder;
    }

    internal static class LogHelper
    {
        public enum LogType { Error }
        public static void WriteLogToFile(string message, LogType type) => Console.Error.WriteLine(message);
    }
}
