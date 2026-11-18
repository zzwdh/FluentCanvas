using FluentCanvas.Helpers;
using Sentry;
using Sentry.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCanvas.Services;

internal static class ErrorReporting
{
    private const string Dsn = "https://17baf480201bf87ad6fda6896ff575e9@o4512193823244288.ingest.us.sentry.io/4512452430266368";
    private static readonly object Gate = new();
    private static IDisposable client;
    private static int deliveryStatus;
    private static string DisabledMarker => Path.Combine(StorageHelper.UserDataPath, "error-reporting.disabled");
    private static bool IsVerification => Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--verify-sentry" });
    internal static bool Enabled => SentrySdk.IsEnabled;

    internal static void Initialize()
    {
        try
        {
            lock (Gate)
            {
                if (client != null) return;
                try { File.GetAttributes(DisabledMarker); return; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                client = SentrySdk.Init(options =>
                {
                    options.Dsn = Dsn;
                    options.Release = $"FluentCanvas@{Assembly.GetExecutingAssembly().GetName().Version}";
                    options.Environment = IsVerification ? "verification" : App.IsPackaged ? "packaged" : "desktop";
                    options.SendDefaultPii = false;
                    options.AutoSessionTracking = false;
                    options.EnableLogs = false;
                    options.MaxBreadcrumbs = 0;
                    options.SendClientReports = false;
                    options.ReportAssembliesMode = ReportAssembliesMode.None;
                    options.DisableDiagnosticSourceIntegration();
                    options.DisableSystemDiagnosticsMetricsIntegration();
                    options.DisableAppDomainUnhandledExceptionCapture();
                    options.DisableUnobservedTaskExceptionCapture();
                    options.DisableWinUiUnhandledExceptionIntegration();
                    options.SetBeforeSend(evt =>
                    {
                        evt.Contexts.Clear();
                        evt.ServerName = null;
                        evt.User = null;
                        evt.SentryThreads = null;
                        evt.DebugImages = null;
                        foreach (var tag in evt.Tags.Keys.ToArray()) evt.UnsetTag(tag);
                        evt.SetTag("architecture", RuntimeInformation.ProcessArchitecture.ToString());
                        evt.SetTag("windows.build", Environment.OSVersion.Version.Build.ToString());
                        return evt;
                    });
                    if (IsVerification) options.CreateHttpMessageHandler = () => new DeliveryProbe();
                });
                AppDomain.CurrentDomain.UnhandledException += OnFatal;
                TaskScheduler.UnobservedTaskException += OnUnobserved;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Error reporting initialization failed ({0}).", ex.GetType().Name);
            if (IsVerification) Console.Error.WriteLine(ex);
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            if (enabled)
            {
                File.Delete(DisabledMarker);
                Initialize();
                if (!Enabled) throw new InvalidOperationException("错误上报服务未能启动。");
            }
            else
            {
                Directory.CreateDirectory(StorageHelper.UserDataPath);
                using (File.Open(DisabledMarker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
                Close();
            }
        }
    }

    // Construct the wire exception from metadata only. Exception.Message, Data and file paths
    // can contain a document name, classroom text or a Windows account name.
    internal static SentryId Capture(Exception error)
    {
        if (!Enabled || error == null) return SentryId.Empty;
        try
        {
            var chain = new List<SentryException>();
            for (var current = error; current != null && chain.Count < 8; current = current.InnerException)
            {
                var frames = new StackTrace(current, false).GetFrames()
                    .Take(100).Reverse().Select(frame => frame.GetMethod()).OfType<MethodBase>()
                    .Select(method => new SentryStackFrame
                    {
                        Module = method.DeclaringType?.FullName,
                        Function = method.Name,
                        InApp = method.DeclaringType?.Assembly == typeof(App).Assembly
                    }).ToList();
                chain.Add(new SentryException
                {
                    Type = current.GetType().FullName,
                    Stacktrace = new SentryStackTrace { Frames = frames }
                });
            }
            chain.Reverse();
            return SentrySdk.CaptureEvent(new SentryEvent { SentryExceptions = chain, Level = SentryLevel.Error });
        }
        catch { return SentryId.Empty; }
    }

    private static void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs args) => Capture(args.Exception);
    private static void OnFatal(object sender, UnhandledExceptionEventArgs args)
    {
        Capture(args.ExceptionObject as Exception);
        try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); } catch { }
    }

    internal static void Close()
    {
        lock (Gate)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnFatal;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            try { client?.Dispose(); }
            finally { client = null; }
        }
    }

    internal static int VerifyDelivery()
    {
        if (!IsVerification || !Enabled)
        {
            WriteVerificationResult($"Verification={IsVerification}, SDK enabled={Enabled}.");
            return 2;
        }
        SentryId eventId;
        try { throw new InvalidOperationException("FluentCanvas delivery check"); }
        catch (InvalidOperationException error)
        {
            eventId = Capture(error);
            if (eventId == SentryId.Empty) return 3;
        }
        SentrySdk.Flush(TimeSpan.FromSeconds(15));
        var status = Volatile.Read(ref deliveryStatus);
        WriteVerificationResult($"Sentry verification event={eventId} http={status}");
        return status is >= 200 and < 300 ? 0 : 4;
    }

    private static void WriteVerificationResult(string result)
    {
        Console.WriteLine(result);
        LogHelper.NewLog(result);
    }

    private sealed class DeliveryProbe : DelegatingHandler
    {
        internal DeliveryProbe() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token).ConfigureAwait(false);
            Volatile.Write(ref deliveryStatus, (int)response.StatusCode);
            return response;
        }
    }
}
