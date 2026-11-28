using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FluentCanvas.Helpers
{
    internal sealed class PowerPointInkStorage(string rootPath, string recoveryRoot)
    {
        private const string PresentationsFolder = "Auto Saved - Presentations";
        private readonly string root = Path.Combine(rootPath, PresentationsFolder);
        private readonly string recovery = Path.Combine(recoveryRoot, "PowerPoint Recovery");

        // Remember an unreadable original until an atomic replacement preserves it.
        private readonly ConcurrentDictionary<string, object> unread = new(StringComparer.OrdinalIgnoreCase);

        internal void PreserveUnreadSlide(string deck, int slide)
        {
            unread[SlidePath(deck, slide)] = new object();
            unread[RecoveryFile(deck, SlideFileName(slide))] = new object();
        }

        internal static string GetDeckKey(string fullName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
            // Windows paths are case-insensitive. Cloud identities may have case-sensitive paths.
            string identity = Path.IsPathFullyQualified(fullName)
                ? Path.GetFullPath(fullName).ToUpperInvariant()
                : new Uri(fullName, UriKind.Absolute).AbsoluteUri;
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        internal string SlidePath(string deckKey, int slideId) =>
            Path.Combine(DeckFolder(deckKey), SlideFileName(slideId));

        internal string LatestSlidePath(string deckKey, int slideId) => LatestFile(deckKey, SlideFileName(slideId));

        internal int? ReadLastSlide(string deckKey)
        {
            var path = LatestFile(deckKey, "Position");
            if (!File.Exists(path)) return null;
            return int.TryParse(File.ReadAllText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id > 0 ? id : null;
        }

        internal void SaveLastSlide(string deckKey, int slideId)
        {
            var bytes = Encoding.UTF8.GetBytes(slideId.ToString(CultureInfo.InvariantCulture));
            WriteWithRecovery(deckKey, "Position", stream => stream.Write(bytes));
        }

        internal bool WriteSlide(string deckKey, int slideId, FrozenInk ink) =>
            WriteWithRecovery(deckKey, SlideFileName(slideId), stream => NativeInkDocument.Write(stream, ink));

        internal byte[] ReadSlide(string deckKey, int slideId)
        {
            var path = LatestSlidePath(deckKey, slideId);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return NativeInkDocument.Read(stream, stream.Length);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private string DeckFolder(string deckKey) => Path.Combine(root, "by-document", deckKey);
        private string RecoveryFile(string deckKey, string name) => Path.Combine(recovery, deckKey, name);
        private static string SlideFileName(int slideId) => slideId.ToString(CultureInfo.InvariantCulture) + ".fcink";

        private string LatestFile(string deckKey, string name)
        {
            var primary = Path.Combine(DeckFolder(deckKey), name);
            var backup = RecoveryFile(deckKey, name);
            return File.Exists(backup) && (!File.Exists(primary) || File.GetLastWriteTimeUtc(backup) > File.GetLastWriteTimeUtc(primary))
                ? backup : primary;
        }

        private static string PreservedPath(string path) => path + "." + Guid.NewGuid().ToString("N") + ".bak";

        private bool WriteWithRecovery(string deckKey, string name, Action<Stream> persist)
        {
            var primary = Path.Combine(DeckFolder(deckKey), name);
            var backup = RecoveryFile(deckKey, name);
            try { Write(primary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write(backup);
                return true;
            }

            try
            {
                if (unread.TryGetValue(backup, out var marker))
                {
                    if (File.Exists(backup)) File.Move(backup, PreservedPath(backup));
                    unread.TryRemove(new KeyValuePair<string, object>(backup, marker));
                }
                else if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return false;

            void Write(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                unread.TryGetValue(path, out var marker);
                AtomicFile.Write(path, persist, marker == null ? null : PreservedPath(path));
                if (marker != null) unread.TryRemove(new KeyValuePair<string, object>(path, marker));
            }
        }
    }
}
