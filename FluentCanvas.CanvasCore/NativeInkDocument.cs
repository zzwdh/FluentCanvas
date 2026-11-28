using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FluentCanvas.Helpers;

internal static class NativeInkDocument
{
    internal const string EntryName = "ink.fcink";
    internal const int MaxPayloadBytes = 256 * 1024 * 1024;
    internal const int Overhead = 16 + 32;
    private const int BlockSize = 256 * 1024, MaxBlocks = 4096;
    internal const int MaxFileBytes = MaxPayloadBytes + Overhead + MaxBlocks * 16;
    private static ReadOnlySpan<byte> Magic => "FCINK\r\n\u001a"u8;

    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fc_document_pack(byte[] raw, nuint length, [Out] byte[] dest, nuint capacity, out nuint size);
    [DllImport("fluent_canvas_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fc_document_unpack(byte[] packed, nuint length, ref byte dest, nuint raw);

    internal static long Write(Stream output, FrozenInk page) => Write(output, checked((int)page.Length),
        (index, _) => page.RecordLength(index), (offset, buffer, count) => page.Read(offset, buffer, count));

    internal static void Write(Stream output, byte[] records) => Write(output, records.Length,
        (_, offset) => NativeStrokeCodec.RecordSize(records.AsSpan(offset)),
        (offset, buffer, count) => Buffer.BlockCopy(records, offset, buffer, 0, count));

    private static long Write(Stream output, int length, Func<int, int, int> recordSize, Action<int, byte[], int> read)
    {
        if (length < 0 || length > MaxPayloadBytes) throw Invalid("page length");
        Span<byte> header = stackalloc byte[16];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], length);
        output.Write(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] raw = ArrayPool<byte>.Shared.Rent(BlockSize);
        byte[] columns = ArrayPool<byte>.Shared.Rent(BlockSize * 2);
        using var compressed = new MemoryStream();
        try
        {
            int offset = 0, buffered = 0, index = 0;
            long bytesWritten = Overhead;
            void Flush(int count, bool wholeRecords)
            {
                if (count == 0) return;
                read(offset, raw, count);
                hash.AppendData(raw, 0, count);
                byte[] source = raw;
                int format = 1, schemaLength = count;
                if (wholeRecords && fc_document_pack(raw, (nuint)count, columns, (nuint)columns.Length, out var packed) == 0)
                {
                    source = columns;
                    schemaLength = checked((int)packed);
                    format = 2;
                }
                compressed.SetLength(0);
                using (var codec = new DeflateStream(compressed, CompressionLevel.Fastest, true))
                    codec.Write(source, 0, schemaLength);
                var stored = compressed.GetBuffer();
                int storedLength = checked((int)compressed.Length);
                if (storedLength >= count)
                {
                    stored = raw;
                    format = 0;
                    storedLength = schemaLength = count;
                }
                Span<byte> descriptor = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(descriptor, count);
                BinaryPrimitives.WriteInt32LittleEndian(descriptor[4..], schemaLength);
                BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], storedLength);
                BinaryPrimitives.WriteInt32LittleEndian(descriptor[12..], format);
                output.Write(descriptor);
                output.Write(stored, 0, storedLength);
                bytesWritten += descriptor.Length + storedLength;
                offset += count;
            }
            while (offset + buffered < length)
            {
                int n = recordSize(index++, offset + buffered);
                if (n <= 0 || n > length - offset - buffered) throw Invalid("record length");
                if (n > BlockSize - buffered)
                {
                    Flush(buffered, true);
                    buffered = 0;
                }
                if (n <= BlockSize) buffered += n;
                else
                {
                    // A single long stroke may span blocks. Its raw bytes are independently compressed.
                    while (n > 0)
                    {
                        int part = Math.Min(n, BlockSize);
                        Flush(part, false);
                        n -= part;
                    }
                }
            }
            Flush(buffered, true);
            output.Write(hash.GetHashAndReset());
            return bytesWritten;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(columns);
        }
    }

    internal static byte[] Read(Stream input, long? entryLength = null, uint? zipCrc = null)
    {
        uint crc = uint.MaxValue;
        long consumed = 0;
        void ReadBytes(Span<byte> dest)
        {
            input.ReadExactly(dest);
            consumed += dest.Length;
            if (zipCrc.HasValue) crc = CanvasDocument.UpdateCrc(crc, dest);
        }
        Span<byte> header = stackalloc byte[16];
        ReadBytes(header);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (!header[..8].SequenceEqual(Magic) || version is not (1 or 2) || size < 0 || size > MaxPayloadBytes)
            throw Invalid("header");
        long? wireLength = entryLength ?? (input.CanSeek ? input.Length - input.Position + 16 : null);
        if (wireLength is < Overhead or > MaxFileBytes || version == 1 && wireLength.HasValue && wireLength != (long)size + Overhead)
            throw Invalid("file length");
        var records = GC.AllocateUninitializedArray<byte>(size);
        if (version == 1) ReadBytes(records);
        else if (size > 0)
        {
            var compressed = ArrayPool<byte>.Shared.Rent(BlockSize);
            var columns = ArrayPool<byte>.Shared.Rent(BlockSize * 2);
            try
            {
                for (int offset = 0, blocks = 0; offset < size;)
                {
                    ReadBytes(header);
                    int n = BinaryPrimitives.ReadInt32LittleEndian(header);
                    int schema = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
                    int stored = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
                    int mode = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
                    if (++blocks > MaxBlocks || n <= 0 || n > BlockSize || n > size - offset ||
                        stored <= 0 || stored > n || schema <= 0 || schema > 2 * BlockSize || mode is < 0 or > 2 ||
                        mode != 2 && schema != n || mode == 0 && stored != n)
                        throw Invalid("block header");
                    if (mode == 0) ReadBytes(records.AsSpan(offset, n));
                    else
                    {
                        ReadBytes(compressed.AsSpan(0, stored));
                        using var source = new MemoryStream(compressed, 0, stored, false);
                        using var codec = new DeflateStream(source, CompressionMode.Decompress);
                        codec.ReadExactly(mode == 1 ? records.AsSpan(offset, n) : columns.AsSpan(0, schema));
                        if (codec.ReadByte() != -1 || mode == 2 &&
                            fc_document_unpack(columns, (nuint)schema, ref records[offset], (nuint)n) != 0)
                            throw Invalid("block data");
                    }
                    offset += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
                ArrayPool<byte>.Shared.Return(columns);
            }
        }
        Span<byte> checksum = stackalloc byte[32];
        ReadBytes(checksum);
        if (input.ReadByte() != -1 || wireLength.HasValue && wireLength != consumed ||
            zipCrc.HasValue && ~crc != zipCrc || !SHA256.HashData(records).AsSpan().SequenceEqual(checksum))
            throw Invalid("checksum or size");
        return records;
    }
    private static InvalidDataException Invalid(string field) => new($"Invalid ink document {field}.");
}
