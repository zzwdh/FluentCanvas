using System;
using System.IO;
using System.Text;
using System.Threading;
using FluentCanvas.Helpers;
using FluentCanvas.Localization;

namespace FluentCanvas.Classroom;

internal sealed class RosterStore(string directory)
{
    private const long MaximumRosterBytes = 2 * 1024 * 1024;
    private readonly string namesPath = Path.Combine(directory, "Names.txt");
    private readonly string replacementsPath = Path.Combine(directory, "Replace.txt");
    internal string DirectoryPath => directory;

    // The legacy format uses two independently replaced files. Participating tool processes
    // must take the same lock for reads and writes to see one consistent revision.
    internal ClassroomRoster Read() => WithRosterLock(ReadSnapshot);

    private ClassroomRoster ReadSnapshot() => ClassroomRoster.Parse(ReadText(namesPath), ReadText(replacementsPath));

    internal ClassroomRoster Save(ClassroomRoster original, string names, string replacements)
    {
        if (Encoding.UTF8.GetByteCount(names) > MaximumRosterBytes || Encoding.UTF8.GetByteCount(replacements) > MaximumRosterBytes)
            throw new InvalidDataException(UiText.Get("Actions.RosterSaveTooLarge"));
        var draft = ClassroomRoster.Parse(names, replacements);
        if (draft.InvalidRules > 0) throw new InvalidDataException(UiText.Get("Actions.InvalidReplacementRule"));

        return WithRosterLock(() =>
        {
            var live = ReadSnapshot();
            if (live.NamesText != original.NamesText || live.ReplacementsText != original.ReplacementsText)
                throw new IOException(UiText.Get("Actions.RosterChanged"));
            Directory.CreateDirectory(directory);
            bool replaceRules = replacements != live.ReplacementsText;
            byte[] originalRules = replaceRules && File.Exists(replacementsPath) ? File.ReadAllBytes(replacementsPath) : null;
            if (replaceRules) WriteText(replacementsPath, replacements);
            try
            {
                if (names != live.NamesText) WriteText(namesPath, names);
            }
            catch (Exception error) when (replaceRules && (error is IOException or UnauthorizedAccessException))
            {
                RestoreRules(replacementsPath, replacements, originalRules, error);
                throw;
            }
            return draft;
        });
    }

    private static ClassroomRoster WithRosterLock(Func<ClassroomRoster> operation)
    {
        using var mutex = new Mutex(false, "Local\\FluentCanvas.RosterWrite");
        bool locked;
        try { locked = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new IOException(UiText.Get("Actions.RosterLocked"));
        try { return operation(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static string ReadText(string path, bool allowMissing = true)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaximumRosterBytes) throw new InvalidDataException(UiText.Get("Actions.RosterReadTooLarge"));
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) when (allowMissing) { return ""; }
        catch (DirectoryNotFoundException) when (allowMissing) { return ""; }
    }

    private static void WriteText(string path, string text) => AtomicFile.Write(path, stream =>
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(text);
    });

    private static void RestoreRules(string path, string attempted, byte[] original, Exception saveError)
    {
        try
        {
            if (ReadText(path) != attempted) throw new IOException(UiText.Get("Actions.ReplacementRulesChanged"));
            if (original == null) File.Delete(path);
            else AtomicFile.Write(path, stream => stream.Write(original));
        }
        catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException(UiText.Get("Actions.RosterRollbackFailed"), new AggregateException(saveError, rollbackError));
        }
    }
}
