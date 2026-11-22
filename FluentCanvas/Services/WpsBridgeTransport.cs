using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace FluentCanvas.Services;

internal sealed class WpsBridgeTransport(string root)
{
    internal const int Protocol = 1;
    private const int MaximumMessageBytes = 2048;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static WpsBridgeTransport ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fluent Canvas WPS Bridge", "v1"));

    internal sealed record State(
        int Version, Guid InstanceId, Guid SessionId, int SlideId, int SlideIndex, int SlideCount,
        bool Active, bool CanNext, bool CanPrevious, bool CanExit, Guid AckId, bool AckSucceeded);

    internal sealed record Command(
        int Version, Guid InstanceId, Guid SessionId, Guid Id, string Action, long ExpiresAt);

    internal (State Active, bool Ambiguous) Read(DateTimeOffset now)
    {
        if (!Directory.Exists(root)) return default;
        try
        {
            var directory = new DirectoryInfo(root);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (directory.Parent.Attributes & FileAttributes.ReparsePoint) != 0) return default;

            State current = null;
            foreach (var file in directory.EnumerateFiles("state-*.json"))
            {
                var state = ReadState(file, now);
                if (state == null) continue;
                if (current != null) return (null, true);
                current = state;
            }
            return (current, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static State ReadState(FileInfo file, DateTimeOffset now)
    {
        if (file.Name.Length != 43 || !Guid.TryParseExact(file.Name.AsSpan(6, 32), "N", out var instance))
            return null;
        try
        {
            DateTimeOffset written = file.LastWriteTimeUtc;
            if (written > now + Lifetime || now - written > Lifetime ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            using var input = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length is < 1 or > MaximumMessageBytes) return null;
            Span<byte> data = stackalloc byte[MaximumMessageBytes];
            int length = input.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            var content = data[..length];
            if (content.StartsWith("\uFEFF"u8)) content = content[3..];
            var state = JsonSerializer.Deserialize<State>(content, Json);
            if (state == null || state.Version != Protocol || state.InstanceId != instance ||
                state.SessionId == Guid.Empty || state.SlideId < 1 || state.SlideIndex < 1 ||
                state.SlideIndex > state.SlideCount)
                return null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Write(Command command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The add-in creates its mailbox in the real shared AppData before reporting state.
        // Opening an existing file preserves visibility across a packaged and unpackaged process.
        var file = new FileInfo(Path.Combine(root, $"command-{command.InstanceId:N}.json"));
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Directory.Parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Invalid WPS command mailbox.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(command, Json);
        using var output = file.Open(FileMode.Open, FileAccess.Write, FileShare.Read);
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(bytes);
        output.SetLength(bytes.Length);
    }
}
