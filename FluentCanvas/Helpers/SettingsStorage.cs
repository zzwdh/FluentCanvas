using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace FluentCanvas.Helpers
{
    internal sealed class SettingsStorage(string path)
    {
        private readonly object gate = new();
        private bool recoveredFromBackup;

        private static readonly JsonSerializerSettings JsonOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            CheckAdditionalContent = true
        };

        // The file used by the original UI is authoritative. Some older MSIX
        // installations also have a (possibly older) LocalSettings snapshot.
        public Settings Read(Func<string> readLegacy)
        {
            lock (gate) return ReadCore(readLegacy);
        }

        private Settings ReadCore(Func<string> readLegacy)
        {
            Exception invalid = null;
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    var settings = Parse(File.ReadAllText(candidate));
                    recoveredFromBackup = candidate != path;
                    return settings;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (JsonException ex) { invalid = ex; }
            }

            var legacy = readLegacy?.Invoke();
            if (legacy != null)
                return Parse(legacy);
            if (invalid != null)
                throw new InvalidDataException("设置文件损坏。原文件已保留。", invalid);
            return null;
        }

        private static Settings Parse(string json) =>
            JsonConvert.DeserializeObject<Settings>(json, JsonOptions)
                ?? throw new JsonSerializationException("设置内容为空。");

        public void Write(Settings settings)
        {
            lock (gate)
            {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings, Formatting.Indented));
                // After recovery the backup is the last readable generation. Rotate again
                // only once the replacement primary has been committed successfully.
                string backupPath = recoveredFromBackup ? null : path + ".bak";
                AtomicFile.Write(path, stream => stream.Write(bytes), backupPath);
                recoveredFromBackup = false;
            }
        }
    }
}
