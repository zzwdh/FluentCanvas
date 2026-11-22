using FluentCanvas.Services;
using Sentry;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace FluentCanvas;

internal static class ErrorReportingTests
{
    private const string PrivateText = "unique-private-classroom-marker";

    internal static void Run(string profile, Action<bool, string> check)
    {
        string disabledMarker = Path.Combine(profile, "error-reporting.disabled");
        var envelopes = new ConcurrentQueue<string>();
        int transportCreations = 0;
        HttpMessageHandler CreateHandler()
        {
            transportCreations++;
            return new EnvelopeRecorder(envelopes);
        }

        try
        {
            File.WriteAllText(disabledMarker, "");
            ErrorReporting.Initialize(CreateHandler);
            check(!ErrorReporting.Enabled && CaptureTestError() == SentryId.Empty && transportCreations == 0,
                "An opted-out profile neither initializes Sentry nor creates a transport");

            ErrorReporting.SetEnabled(true, CreateHandler);
            check(ErrorReporting.Enabled && !File.Exists(disabledMarker), "Enabling reporting removes the saved opt-out marker");
            SentrySdk.Logger.LogWarning(PrivateText);
            SentrySdk.Metrics.EmitCounter(PrivateText, 1);
            CheckScrubbedEvent(check, envelopes);

            ErrorReporting.SetEnabled(false);
            ErrorReporting.Initialize(CreateHandler);
            check(!ErrorReporting.Enabled && File.Exists(disabledMarker) && CaptureTestError() == SentryId.Empty &&
                transportCreations == 1 && envelopes.IsEmpty, "Disabling reporting persists through another initialization without new events");

            ErrorReporting.SetEnabled(true, CreateHandler);
            CheckScrubbedEvent(check, envelopes);
            check(transportCreations == 2, "Re-enabling reporting creates exactly one new transport");

            ErrorReporting.Close();
            ErrorReporting.Initialize(CreateHandler);
            check(ErrorReporting.Enabled, "A profile with no opt-out marker enables reporting on startup");
            CheckScrubbedEvent(check, envelopes);
        }
        finally
        {
            ErrorReporting.Close();
            File.Delete(disabledMarker);
        }
    }

    private static void CheckScrubbedEvent(Action<bool, string> check, ConcurrentQueue<string> envelopes)
    {
        var id = CaptureTestError();
        SentrySdk.Flush(TimeSpan.FromSeconds(5));
        check(id != SentryId.Empty && envelopes.Count == 1, "Exactly one error envelope is sent without logs or metrics");
        if (!envelopes.TryDequeue(out var envelope)) throw new InvalidOperationException("The error envelope was not recorded.");

        using var reader = new StringReader(envelope);
        _ = reader.ReadLine(); // Envelope header.
        using var item = JsonDocument.Parse(reader.ReadLine());
        using var report = JsonDocument.Parse(reader.ReadLine());
        var value = report.RootElement;
        var tags = value.GetProperty("tags");
        check(item.RootElement.GetProperty("type").GetString() == "event" &&
            string.IsNullOrWhiteSpace(reader.ReadToEnd()) &&
            value.GetProperty("exception").GetProperty("values")[0].GetProperty("type").GetString() == typeof(InvalidOperationException).FullName &&
            tags.TryGetProperty("architecture", out _) && tags.TryGetProperty("windows.build", out _) &&
            tags.EnumerateObject().Count() == 2 && !envelope.Contains(PrivateText, StringComparison.Ordinal) &&
            !value.TryGetProperty("user", out _) && !value.TryGetProperty("server_name", out _),
            "Serialized error contains allowed metadata and excludes exception messages, personal tags, and logs");
    }

    private static SentryId CaptureTestError()
    {
        try { throw new InvalidOperationException($@"C:\Student files\{PrivateText}.icart"); }
        catch (InvalidOperationException error)
        {
            error.Data["classroom"] = PrivateText;
            return ErrorReporting.Capture(error);
        }
    }

    private sealed class EnvelopeRecorder(ConcurrentQueue<string> envelopes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var content = await request.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var decompressed = request.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(content, CompressionMode.Decompress) : null;
            using var reader = new StreamReader(decompressed ?? content);
            envelopes.Enqueue(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

internal static class App
{
    internal static bool IsPackaged => false;
}
