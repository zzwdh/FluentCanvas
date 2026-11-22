using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FluentCanvas.Helpers
{
    internal sealed class PowerPointInkStorage(string rootPath, string recoveryRoot)
    {
        private const string LegacyRoot = "Auto Saved - Presentations";
        private readonly string root = Path.Combine(rootPath, LegacyRoot);
        private readonly string recovery = Path.Combine(recoveryRoot, "PowerPoint Recovery");

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
            WriteWithRecovery(deckKey, "Position", bytes);
        }

        internal bool WriteSlide(string deckKey, int slideId, byte[] bytes) =>
            WriteWithRecovery(deckKey, SlideFileName(slideId), bytes);

        internal string GetLegacyFolder(string deckKey, string name, int slideCount)
        {
            var legacyName = name + "_" + slideCount.ToString(CultureInfo.InvariantCulture);
            if (!CanvasDocument.IsAssetName(legacyName) || File.Exists(LegacyMarker(deckKey))) return null;
            var path = Path.Combine(root, legacyName);
            return Directory.Exists(path) ? path : null;
        }

        internal int ImportLegacy(string deckKey, string legacyFolder, int[] slideIds, Func<int, bool> hasPendingInk = null)
        {
            int imported = 0;
            var directory = Directory.CreateDirectory(DeckFolder(deckKey));
            for (int index = 0; index < slideIds.Length; index++)
            {
                int slideId = slideIds[index];
                var destination = SlidePath(deckKey, slideId);
                if (hasPendingInk?.Invoke(slideId) == true || File.Exists(LatestSlidePath(deckKey, slideId))) continue;

                string basename = (index + 1).ToString("D4", CultureInfo.InvariantCulture);
                var source = Path.Combine(legacyFolder, basename + ".icstk");
                if (!File.Exists(source)) source = Path.Combine(legacyFolder, basename + ".icart");
                if (!File.Exists(source)) continue;
                using var ink = CanvasDocument.ReadSlideInk(source);
                var temporary = Path.Combine(directory.FullName, "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew))
                    {
                        ink.WriteTo(output);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, destination);
                    imported++;
                }
                finally { File.Delete(temporary); }
            }
            AtomicFile.Write(LegacyMarker(deckKey), _ => { });
            return imported;
        }

        private string DeckFolder(string deckKey) => Path.Combine(root, "by-document", deckKey);
        private string LegacyMarker(string deckKey) => Path.Combine(DeckFolder(deckKey), "LegacyImported");
        private string RecoveryFile(string deckKey, string name) => Path.Combine(recovery, deckKey, name);
        private static string SlideFileName(int slideId) => slideId.ToString(CultureInfo.InvariantCulture) + ".icstk";

        private string LatestFile(string deckKey, string name)
        {
            var primary = Path.Combine(DeckFolder(deckKey), name);
            var backup = RecoveryFile(deckKey, name);
            return File.Exists(backup) && (!File.Exists(primary) || File.GetLastWriteTimeUtc(backup) > File.GetLastWriteTimeUtc(primary))
                ? backup : primary;
        }

        private bool WriteWithRecovery(string deckKey, string name, byte[] bytes)
        {
            var primary = Path.Combine(DeckFolder(deckKey), name);
            var backup = RecoveryFile(deckKey, name);
            try { Write(primary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write(backup);
                return true;
            }

            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return false;

            void Write(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicFile.Write(path, stream => stream.Write(bytes));
            }
        }
    }
}
