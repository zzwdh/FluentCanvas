using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace FluentCanvas.Helpers
{
    internal static class SettingsStorage
    {
        private static readonly JsonSerializerSettings JsonOptions = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            CheckAdditionalContent = true
        };

        // The file used by the original UI is authoritative. Some older MSIX
        // installations also have a (possibly older) LocalSettings snapshot.
        public static Settings Read(string path, Func<string> readLegacy)
        {
            Exception invalid = null;
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    return Parse(File.ReadAllText(candidate));
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

        public static void Write(string path, Settings settings)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings, Formatting.Indented));
            AtomicFile.Write(path, stream => stream.Write(bytes), path + ".bak");
        }
    }
}
