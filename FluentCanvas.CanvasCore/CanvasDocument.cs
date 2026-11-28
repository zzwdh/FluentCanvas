using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
            using var snapshot = Capture(canvas);
            snapshot.Save(path);
        }

        internal static Snapshot Capture(InkCanvas canvas) => Capture(canvas.Strokes, canvas.Children.Cast<UIElement>());

        internal static Snapshot Capture(StrokeCollection strokes, IEnumerable<UIElement> children)
        {
            using var page = NativeInkPage.Create();
            page.Append(strokes);
            return Capture(FrozenInk.Capture(page), children);
        }

        internal static Snapshot Capture(FrozenInk ink, IEnumerable<UIElement> children)
        {
            try { return CaptureAssets(ink, children); }
            catch { ink.Dispose(); throw; }
        }

        private static Snapshot CaptureAssets(FrozenInk ink, IEnumerable<UIElement> children)
        {
            var description = new XElement(CanvasDocumentXml.Presentation + "InkCanvas");
            ink.WritePageId(description);
            var assets = new List<Asset>();
            long pixels = 0;
            foreach (var child in children)
            {
                if (assets.Count >= MaxEntries - 2) throw TooLarge();
                string name = Assets + (assets.Count + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                if (child is Image image && image.Source is BitmapSource source)
                {
                    CheckImageSize(source.PixelWidth, source.PixelHeight, ref pixels);
                    name += ".png";
                    assets.Add(new Asset(name, source.IsFrozen ? source : (BitmapSource)source.GetCurrentValueAsFrozen(), null));
                    description.Add(CanvasDocumentXml.WriteElement(image, name, image.Stretch, ink.SceneFor(image)));
                }
                else if (child is MediaElement media && media.Source?.IsAbsoluteUri == true && media.Source.IsFile)
                {
                    string mediaPath = media.Source.LocalPath;
                    name += Path.GetExtension(mediaPath).ToLowerInvariant();
                    assets.Add(new Asset(name, null, mediaPath, CanvasDocumentMedia.GetOwner(media)));
                    description.Add(CanvasDocumentXml.WriteElement(media, name, media.Stretch, ink.SceneFor(media)));
                }
                else throw new InvalidDataException("当前画布包含未完成的笔迹或无法保存的元素。");
            }
            return new Snapshot(ink, description, assets);
        }

        internal sealed record Asset(string Name, BitmapSource Image, string MediaPath, CanvasDocumentMedia MediaOwner = null);

        private sealed record EncodedImage(byte[] Data, string Name);
        private static readonly ConditionalWeakTable<BitmapSource, Lazy<EncodedImage>> encodedImages = new();

        private static EncodedImage EncodePng(BitmapSource source)
        {
            using var data = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(data);
            if (data.Length > MaxAssetBytes) throw TooLarge();
            var bytes = data.ToArray();
            return new(bytes, Assets + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".png");
        }

        internal sealed class Snapshot(FrozenInk ink, XElement description, List<Asset> assets) : IDisposable
        {
            public void Dispose() => ink.Dispose();

            internal void Save(string path)
            {
                AtomicFile.Write(path, output =>
                {
                    using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
                    long total;
                    using (var target = zip.CreateEntry(NativeInkDocument.EntryName, CompressionLevel.NoCompression).Open())
                        total = Math.Max(ink.Length, NativeInkDocument.Write(target, ink));
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    var names = new Dictionary<string, string>(StringComparer.Ordinal);

                    void Add(string name, Stream data, long limit, CompressionLevel compression)
                    {
                        total = checked(total + data.Length);
                        if (data.Length > limit || total > MaxTotalBytes) throw TooLarge();
                        using var target = zip.CreateEntry(name, compression).Open();
                        data.Position = 0;
                        data.CopyTo(target);
                    }

                    foreach (var asset in assets)
                    {
                        if (asset.Image == null)
                        {
                            using (var media = File.OpenRead(asset.MediaPath))
                            {
                                if (media.Length > MaxAssetBytes) throw TooLarge();
                                string extension = Path.GetExtension(asset.Name);
                                ValidateMedia(media, extension);
                                media.Position = 0;
                                string name = Assets + Convert.ToHexStringLower(SHA256.HashData(media)) + extension;
                                names.Add(asset.Name, name);
                                if (written.Add(name)) Add(name, media, MaxAssetBytes, CompressionLevel.NoCompression);
                            }
                            GC.KeepAlive(asset.MediaOwner);
                            continue;
                        }
                        var image = encodedImages.GetValue(asset.Image, source => new(() => EncodePng(source))).Value;
                        names.Add(asset.Name, image.Name);
                        if (written.Add(image.Name))
                        {
                            using var data = new MemoryStream(image.Data, false);
                            Add(image.Name, data, MaxAssetBytes, CompressionLevel.NoCompression);
                        }
                    }
                    using var xml = new MemoryStream();
                    var manifest = new XElement(description);
                    foreach (var source in manifest.Elements().Attributes("Source"))
                        source.Value = names[source.Value];
                    manifest.Save(xml);
                    Add("elements.xaml", xml, MaxXmlBytes, CompressionLevel.Fastest);
                });
            }
        }

        internal static void LoadInto(string path, InkCanvas canvas, TimeMachine history)
        {
            using var staged = Read(path);
            staged.Apply(canvas, history);
        }

        internal static Import Read(string path)
        {
            var staged = new Import();
            try
            {
                using var file = File.OpenRead(path);
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
                return staged;
            }
            catch
            {
                staged.Dispose();
                throw;
            }
        }

        private static bool HasLegacySlideHeader(Stream file)
        {
            int header = file.ReadByte();
            file.Position = 0;
            return header == 0;
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
                if (name.Equals(NativeInkDocument.EntryName, StringComparison.OrdinalIgnoreCase))
                    limit = NativeInkDocument.MaxFileBytes;
                else if (name.Equals("strokes.icstk", StringComparison.OrdinalIgnoreCase)) limit = MaxInkBytes;
                else if (name.Equals("elements.xaml", StringComparison.OrdinalIgnoreCase)) limit = MaxXmlBytes;
                else if (name.Equals(Assets, StringComparison.OrdinalIgnoreCase)) limit = 0;
                else if (name.StartsWith(Assets, StringComparison.OrdinalIgnoreCase) && IsAssetName(name.Substring(Assets.Length))) limit = MaxAssetBytes;
                else throw new InvalidDataException("作品包含不支持的文件路径。");

                total = checked(total + entry.Length);
                if (entry.Length > limit || total > MaxTotalBytes || entries.Count >= MaxEntries) throw TooLarge();
                if (!entries.TryAdd(name, entry)) throw new InvalidDataException("作品中存在重名的文件，无法确定正确的资源。");
            }
            bool native = entries.TryGetValue(NativeInkDocument.EntryName, out var nativeEntry);
            bool legacy = entries.TryGetValue("strokes.icstk", out var strokes);
            if (native == legacy || !entries.TryGetValue("elements.xaml", out var xml))
                throw new InvalidDataException("作品缺少笔迹或元素清单。");
            if (native)
            {
                using var input = nativeEntry.Open();
                var records = NativeInkDocument.Read(input, nativeEntry.Length, nativeEntry.Crc32);
                if (total - nativeEntry.Length + records.Length > MaxTotalBytes) throw TooLarge();
                staged.LoadNative(records);
            }
            else
            {
                using var ink = ReadEntry(strokes);
                staged.Strokes = new StrokeCollection(ink);
            }
            List<CanvasDocumentXml.Element> elements;
            using (var description = ReadEntry(xml))
            {
                elements = CanvasDocumentXml.Read(description, out var pageId);
                staged.SetPageId(pageId);
            }

            long pixels = 0;
            var images = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in elements)
            {
                if (!entries.TryGetValue(element.Asset, out var asset)) throw new InvalidDataException("作品缺少引用的图片或媒体文件。");
                if (element.IsMedia)
                {
                    staged.Elements.Add(new StagedElement(element, null, staged.StageMedia(asset)));
                }
                else
                {
                    if (!images.TryGetValue(asset.FullName, out var bitmap))
                    {
                        using var image = ReadEntry(asset);
                        bitmap = DecodeImage(image, ref pixels, false);
                        images.Add(asset.FullName, bitmap);
                        if (asset.Name.Length == 68 && asset.Name.EndsWith(".png", StringComparison.Ordinal) &&
                            image.GetBuffer().AsSpan(0, (int)image.Length).StartsWith(new byte[] {137,80,78,71,13,10,26,10}))
                        {
                            var data = image.ToArray();
                            string canonical = Assets + Convert.ToHexStringLower(SHA256.HashData(data)) + ".png";
                            if (canonical == asset.FullName)
                                encodedImages.Add(bitmap, new(() => new(data, canonical)));
                        }
                    }
                    else CheckImageSize(bitmap.PixelWidth, bitmap.PixelHeight, ref pixels);
                    staged.Elements.Add(new StagedElement(element, bitmap, null));
                }
            }
        }

        internal static BitmapSource ReadImageForImport(string path, bool compress)
        {
            using var image = File.OpenRead(path);
            if (image.Length > MaxAssetBytes) throw TooLarge();
            long pixels = 0;
            return DecodeImage(image, ref pixels, compress);
        }

        private static BitmapSource DecodeImage(Stream image, ref long totalPixels, bool compress)
        {
            // Inspect encoded dimensions before allocating the decoded pixel buffer.
            var frame = BitmapDecoder.Create(image, BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnDemand).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            CheckImageSize(width, height, ref totalPixels);
            image.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = image;
            if (compress && (width > 1920 || height > 1080))
            {
                double scale = Math.Min(1920.0 / width, 1080.0 / height);
                bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Floor(width * scale));
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        internal static void CopyVideoForImport(string source, string destination)
        {
            using var media = File.OpenRead(source);
            if (media.Length > MaxAssetBytes) throw TooLarge();
            ValidateMedia(media, Path.GetExtension(source).ToLowerInvariant());
            media.Position = 0;
            AtomicFile.Write(destination, media.CopyTo);
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
            var result = new MemoryStream((int)Math.Min(entry.Length, 128 * 1024));
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
                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                target.Write(buffer, 0, read);
                remaining -= read;
            }
            if (input.ReadByte() != -1) throw TooLarge();
            if (~crc != entry.Crc32) throw new InvalidDataException("作品中的文件校验失败。");
        }

        internal static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes) crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
            return crc;
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

        internal sealed record StagedElement(CanvasDocumentXml.Element Description, BitmapSource Image, string MediaPath)
        {
            internal FrameworkElement CreateVisual(CanvasDocumentMedia mediaOwner)
            {
                FrameworkElement visual;
                if (MediaPath == null)
                {
                    visual = new Image { Source = Image, Stretch = Description.Stretch };
                }
                else
                {
                    var media = new MediaElement
                    {
                        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                        Source = new Uri(MediaPath), Stretch = Description.Stretch
                    };
                    mediaOwner?.Attach(media);
                    media.Loaded += async (_, _) =>
                    {
                        media.Play();
                        await Task.Delay(100);
                        if (media.IsLoaded) media.Pause();
                    };
                    visual = media;
                }
                Description.Apply(visual);
                return visual;
            }
        }

        internal sealed class Import : IDisposable
        {
            internal StrokeCollection Strokes;
            private NativeInkPage nativePage;
            internal void SetPageId(Guid? id)
            {
                if (id is Guid identity) nativePage?.SetPageId(identity);
            }

            internal void LoadNative(byte[] records)
            {
                nativePage = NativeInkPage.LoadDocument(records, out Strokes, undoable: true);
            }
            internal readonly List<StagedElement> Elements = new();
            private CanvasDocumentMedia mediaFolder;
            private bool applied;
            private readonly Dictionary<string, string> mediaPaths = new(StringComparer.OrdinalIgnoreCase);

            internal string StageMedia(ZipArchiveEntry entry)
            {
                if (mediaPaths.TryGetValue(entry.FullName, out var existing)) return existing;
                mediaFolder ??= CanvasDocumentMedia.Create();
                var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension is not (".mp4" or ".avi" or ".wmv")) throw new InvalidDataException("不支持的媒体类型。");
                string path = Path.Combine(mediaFolder.Path, Guid.NewGuid().ToString("N") + extension);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                CopyEntry(entry, output);
                output.Position = 0;
                ValidateMedia(output, extension);
                mediaPaths.Add(entry.FullName, path);
                return path;
            }

            internal void Apply(InkCanvas canvas, TimeMachine history)
            {
                var children = Elements.Select(element => element.CreateVisual(mediaFolder)).ToArray();
                var entries = new List<TimeMachineHistory>();
                if (Strokes.Count > 0) entries.Add(new TimeMachineHistory(Strokes, TimeMachineHistoryType.UserInput, false));
                entries.AddRange(children.Select(child => new TimeMachineHistory(child, TimeMachineHistoryType.ElementInsert, false)));
                // Keep original identities. Undo records refer to these same stroke and element instances.
                var oldStrokes = new StrokeCollection(canvas.Strokes);
                var oldChildren = canvas.Children.Cast<UIElement>().ToArray();
                try
                {
                    canvas.Children.Clear();
                    canvas.Strokes.Clear();
                    if (Strokes.Count > 0) canvas.Strokes.Add(Strokes);
                    foreach (var child in children) canvas.Children.Add(child);
                    if (nativePage == null) history.ImportTimeMachineHistory(entries.ToArray());
                    else
                    {
                        history.ImportNativePage(nativePage, entries.ToArray());
                        nativePage = null;
                    }
                    history.MarkSaved();
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
            }

            public void Dispose()
            {
                nativePage?.Dispose();
                nativePage = null;
                if (!applied) mediaFolder?.Discard();
                mediaFolder = null;
            }
        }
    }
}
