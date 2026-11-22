#nullable enable
using FluentCanvas.Localization;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCanvas.Services
{
    internal static class SettingsPipe
    {
        internal const string Prefix = "FluentCanvas.Settings.";
        private const int MaxMessageBytes = 64 * 1024;

        internal static async Task<WinUiSettingsResponse> RequestAsync(
            string pipeName, WinUiSettingsRequest request, CancellationToken cancellation)
        {
            if (pipeName == null || !pipeName.StartsWith(Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(pipeName.AsSpan(Prefix.Length), "N", out _))
            {
                throw new InvalidOperationException(UiText.Get("Settings.InvalidSession"));
            }

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            // The system's startup consent dialog can wait for the user.
            if (request.Setting is not (WinUiSetting.RunAtStartup or WinUiSetting.PrepareStoreUpdate))
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(5_000, deadline.Token).ConfigureAwait(false);
            await WriteAsync(client, request, deadline.Token).ConfigureAwait(false);
            return await ReadAsync<WinUiSettingsResponse>(client, deadline.Token).ConfigureAwait(false);
        }

        internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation) where T : class
        {
            byte[] header = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException();
            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException();
        }

        internal static async Task WriteAsync<T>(Stream stream, T packet, CancellationToken cancellation)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);
            if (payload.Length > MaxMessageBytes) throw new InvalidDataException();
            byte[] header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellation).ConfigureAwait(false);
        }
    }
}
