using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace FluentCanvas.Helpers
{
    internal static class CanvasDocument
    {
        internal const int MaxEntries = 512;
        internal const int MaxXmlBytes = 32 * 1024 * 1024;
        private const int MaxInkBytes = 32 * 1024 * 1024;
        private const int MaxAssetBytes = 256 * 1024 * 1024;
        private const int MaxTotalBytes = 512 * 1024 * 1024;
        private const long MaxImagePixels = 32L * 1024 * 1024;
        private const long MaxDocumentPixels = 64L * 1024 * 1024;
        private const string Assets = "File Dependency/";
        private static readonly uint[] CrcTable = CreateCrcTable();

        internal static void Save(string path, InkCanvas canvas)
        {
            AtomicFile.Write(path, output =>
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
                long total = 0, pixels = 0;
                void Add(string name, Stream data, long limit)
                {
                    total = checked(total + data.Length);
                    if (data.Length > limit || total > MaxTotalBytes) throw TooLarge();
                    using var target = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                    data.Position = 0;
                    data.CopyTo(target);
                }
                using (var ink = new MemoryStream())
                {
                    canvas.Strokes.Save(ink);
                    Add("strokes.icstk", ink, MaxInkBytes);
                }

                var root = new XElement(CanvasDocumentXml.Presentation + "InkCanvas");
                int index = 0;
                foreach (UIElement child in canvas.Children)
                {
                    if (++index > MaxEntries - 2) throw TooLarge();
                    string name = Assets + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    if (child is Image image && image.Source is BitmapSource source)
                    {
                        CheckImageSize(source.PixelWidth, source.PixelHeight, ref pixels);
                        name += ".png";
                        using var data = new MemoryStream();
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(source));
                        encoder.Save(data);
                        Add(name, data, MaxAssetBytes);
                        root.Add(CanvasDocumentXml.WriteElement(image, name, image.Stretch));
                    }
                    else if (child is MediaElement media && media.Source?.IsAbsoluteUri == true && media.Source.IsFile)
                    {
                        var extension = Path.GetExtension(media.Source.LocalPath).ToLowerInvariant();
                        using var data = File.OpenRead(media.Source.LocalPath);
                        ValidateMedia(data, extension);
                        name += extension;
                        Add(name, data, MaxAssetBytes);
                        root.Add(CanvasDocumentXml.WriteElement(media, name, media.Stretch));
                    }
                    else throw new InvalidDataException("当前画布包含未完成的笔迹或无法保存的元素。");
                }
                using var xml = new MemoryStream();
                root.Save(xml);
                Add("elements.xaml", xml, MaxXmlBytes);
            });
        }

        internal static void LoadInto(string path, InkCanvas canvas, TimeMachine history)
        {
            using var staged = new Import();
            using (var file = File.OpenRead(path))
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".icstk":
                        if (file.Length > MaxInkBytes) throw TooLarge();
                        staged.Strokes = new StrokeCollection(file);
                        break;
                    case ".icart":
                        if (HasLegacySlideHeader(file))
                        {
                            if (file.Length > MaxInkBytes) throw TooLarge();
                            staged.Strokes = new StrokeCollection(file);
                        }
                        else ReadArchive(file, staged);
                        break;
                    default: throw new InvalidDataException("不支持的作品文件格式。");
                }
            }
            staged.Apply(canvas, history);
        }

        private static bool HasLegacySlideHeader(Stream file)
        {
            int header = file.ReadByte();
            file.Position = 0;
            return header == 0;
        }

        internal static MemoryStream ReadSlideInk(string path)
        {
            using var file = File.OpenRead(path);
            MemoryStream data;
            if (HasLegacySlideHeader(file))
            {
                if (file.Length > MaxInkBytes) throw TooLarge();
                data = new MemoryStream((int)file.Length);
                file.CopyTo(data);
                data.Position = 0;
            }
            else
            {
                CheckDirectory(file);
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                var entry = zip.GetEntry("strokes.icstk") ?? throw new InvalidDataException("幻灯片作品缺少笔迹。");
                if (entry.Length > MaxInkBytes) throw TooLarge();
                data = ReadEntry(entry);
            }
            _ = new StrokeCollection(data);
            data.Position = 0;
            return data;
        }

        private static void ReadArchive(FileStream file, Import staged)
        {
            // Bound the central directory before ZipArchive allocates its entry collection.
            CheckDirectory(file);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                long limit;
                if (name.Equals("strokes.icstk", StringComparison.OrdinalIgnoreCase)) limit = MaxInkBytes;
                else if (name.Equals("elements.xaml", StringComparison.OrdinalIgnoreCase)) limit = MaxXmlBytes;
                else if (name.Equals(Assets, StringComparison.OrdinalIgnoreCase)) limit = 0;
                else if (name.StartsWith(Assets, StringComparison.OrdinalIgnoreCase) && IsAssetName(name.Substring(Assets.Length))) limit = MaxAssetBytes;
                else throw new InvalidDataException("作品包含不支持的文件路径。");

                total = checked(total + entry.Length);
                if (entry.Length > limit || total > MaxTotalBytes || entries.Count >= MaxEntries) throw TooLarge();
                if (!entries.TryAdd(name, entry)) throw new InvalidDataException("作品中存在重名的文件，无法确定正确的资源。");
            }
            if (!entries.TryGetValue("strokes.icstk", out var strokes) || !entries.TryGetValue("elements.xaml", out var xml))
                throw new InvalidDataException("作品缺少笔迹或元素清单。");
            using (var ink = ReadEntry(strokes)) staged.Strokes = new StrokeCollection(ink);
            List<CanvasDocumentXml.Element> elements;
            using (var description = ReadEntry(xml)) elements = CanvasDocumentXml.Read(description);

            long pixels = 0;
            foreach (var element in elements)
            {
                if (!entries.TryGetValue(element.Asset, out var asset)) throw new InvalidDataException("作品缺少引用的图片或媒体文件。");
                FrameworkElement visual;
                if (element.IsMedia)
                {
                    string local = staged.StageMedia(asset);
                    var media = new MediaElement
                    {
                        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                        Source = new Uri(local), Stretch = element.Stretch
                    };
                    media.Loaded += async (_, _) =>
                    {
                        media.Play();
                        await Task.Delay(100);
                        media.Pause();
                    };
                    visual = media;
                }
                else
                {
                    using var image = ReadEntry(asset);
                    // Inspect encoded dimensions before requesting a decoded pixel buffer.
                    var decoder = BitmapDecoder.Create(image, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
                    var frame = decoder.Frames[0];
                    CheckImageSize(frame.PixelWidth, frame.PixelHeight, ref pixels);
                    image.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = image;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    visual = new Image { Source = bitmap, Stretch = element.Stretch };
                }
                element.Apply(visual);
                staged.Children.Add(visual);
            }
        }

        internal static bool IsAssetName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && name != "." && name != ".." &&
                name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
        }

        private static void CheckImageSize(int width, int height, ref long total)
        {
            long pixels = (long)width * height;
            total = checked(total + pixels);
            if (width <= 0 || height <= 0 || pixels > MaxImagePixels || total > MaxDocumentPixels) throw TooLarge();
        }

        private static void CheckDirectory(FileStream file)
        {
            if (file.Length > MaxTotalBytes + 4L * 1024 * 1024 || file.Length < 22) throw new InvalidDataException("作品包的长度无效。");
            var tail = new byte[(int)Math.Min(file.Length, 22 + ushort.MaxValue)];
            file.Position = file.Length - tail.Length;
            file.ReadExactly(tail);
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                var footer = tail.AsSpan(i);
                if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054b50 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(footer[20..]) != footer.Length - 22) continue;
                long size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
                long offset = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
                if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != 0 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(footer[10..]) > MaxEntries || size > 1024 * 1024 ||
                    offset + size != file.Length - footer.Length) throw TooLarge();
                file.Position = 0;
                return;
            }
            throw new InvalidDataException("作品包目录损坏。");
        }

        private static MemoryStream ReadEntry(ZipArchiveEntry entry)
        {
            var result = new MemoryStream();
            CopyEntry(entry, result);
            result.Position = 0;
            return result;
        }

        private static void CopyEntry(ZipArchiveEntry entry, Stream target)
        {
            using var input = entry.Open();
            byte[] buffer = new byte[81920];
            long remaining = entry.Length;
            uint crc = uint.MaxValue;
            while (remaining > 0)
            {
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new InvalidDataException("作品中的文件内容不完整。");
                for (int i = 0; i < read; i++) crc = CrcTable[(byte)(crc ^ buffer[i])] ^ (crc >> 8);
                target.Write(buffer, 0, read);
                remaining -= read;
            }
            if (input.ReadByte() != -1) throw TooLarge();
            if (~crc != entry.Crc32) throw new InvalidDataException("作品中的文件校验失败。");
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
                table[i] = value;
            }
            return table;
        }

        private static void ValidateMedia(Stream data, string extension)
        {
            Span<byte> header = stackalloc byte[16];
            data.ReadExactly(header);
            bool valid = extension switch
            {
                ".mp4" => header[4..8].SequenceEqual("ftyp"u8),
                ".avi" => header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8),
                ".wmv" => header.SequenceEqual(new byte[] { 0x30,0x26,0xb2,0x75,0x8e,0x66,0xcf,0x11,0xa6,0xd9,0x00,0xaa,0x00,0x62,0xce,0x6c }),
                _ => false
            };
            if (!valid) throw new InvalidDataException("媒体资源的类型或文件头无效。");
        }

        private static InvalidDataException TooLarge() => new InvalidDataException("作品的文件数量、解压大小或图片尺寸超过限制。");

        private sealed class Import : IDisposable
        {
            internal StrokeCollection Strokes;
            internal readonly List<FrameworkElement> Children = new();
            private string mediaFolder;
            private bool applied;
            private readonly Dictionary<string, string> mediaPaths = new(StringComparer.OrdinalIgnoreCase);

            internal string StageMedia(ZipArchiveEntry entry)
            {
                if (mediaPaths.TryGetValue(entry.FullName, out var existing)) return existing;
                mediaFolder ??= Directory.CreateTempSubdirectory("FluentCanvas-Media-").FullName;
                var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension is not (".mp4" or ".avi" or ".wmv")) throw new InvalidDataException("不支持的媒体类型。");
                string path = Path.Combine(mediaFolder, Guid.NewGuid().ToString("N") + extension);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                CopyEntry(entry, output);
                output.Position = 0;
                ValidateMedia(output, extension);
                mediaPaths.Add(entry.FullName, path);
                return path;
            }

            internal void Apply(InkCanvas canvas, TimeMachine history)
            {
                var entries = new List<TimeMachineHistory>();
                if (Strokes.Count > 0) entries.Add(new TimeMachineHistory(Strokes, TimeMachineHistoryType.UserInput, false));
                entries.AddRange(Children.Select(child => new TimeMachineHistory(child, TimeMachineHistoryType.ElementInsert, false)));
                // Keep original identities. Undo records refer to these same stroke and element instances.
                var oldStrokes = new StrokeCollection(canvas.Strokes);
                var oldChildren = canvas.Children.Cast<UIElement>().ToArray();
                try
                {
                    canvas.Children.Clear();
                    canvas.Strokes.Clear();
                    if (Strokes.Count > 0) canvas.Strokes.Add(Strokes);
                    foreach (var child in Children) canvas.Children.Add(child);
                    history.ImportTimeMachineHistory(entries.ToArray());
                    applied = true;
                }
                catch
                {
                    canvas.Children.Clear();
                    canvas.Strokes.Clear();
                    if (oldStrokes.Count > 0) canvas.Strokes.Add(oldStrokes);
                    foreach (var child in oldChildren) canvas.Children.Add(child);
                    throw;
                }
                // Whiteboard pages and undo records can retain media after another file opens.
                // Keep these random, app-owned temporary files alive for the session.
                string folder = mediaFolder;
                if (folder != null) AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveMedia(folder);
            }

            public void Dispose()
            {
                if (!applied) RemoveMedia(mediaFolder);
            }

            private static void RemoveMedia(string folder)
            {
                try { if (folder != null) Directory.Delete(folder, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
