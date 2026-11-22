using FluentCanvas.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;

internal static class WpsBridgeTests
{
    internal static void Run(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "wps", "v1")).FullName;
        var bridge = new WpsBridgeTransport(directory);
        var instance = Guid.NewGuid();
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var state = new WpsBridgeTransport.State(1, instance, session, 258, 2, 3, true,
            true, true, true, Guid.Empty, false);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string stateFile = Path.Combine(directory, $"state-{instance:N}.json");
        File.WriteAllText(stateFile, JsonSerializer.Serialize(state, json));
        Check(bridge.Read(now) == (state, false), "Fresh WPS state lists the one live show.");
        Check(bridge.Read(now.AddSeconds(3)) == (null, false), "A crashed or stale WPS client is ignored.");
        File.WriteAllText(stateFile, JsonSerializer.Serialize(state with { InstanceId = Guid.NewGuid() }, json));
        Check(bridge.Read(now) == (null, false), "State must belong to its named mailbox.");
        File.WriteAllText(stateFile, JsonSerializer.Serialize(state, json));

        var command = new WpsBridgeTransport.Command(1, instance, session, Guid.NewGuid(), "previous",
            now.AddSeconds(1).ToUnixTimeMilliseconds());
        string mailbox = Path.Combine(directory, $"command-{instance:N}.json");
        bool failed = false;
        try { bridge.Write(command); }
        catch (IOException) { failed = true; }
        Check(failed && !File.Exists(mailbox), "The packaged app must never create the WPS mailbox.");
        File.WriteAllText(mailbox, new string(' ', 2048));
        try
        {
            bridge.Write(command, new CancellationToken(canceled: true));
            throw new InvalidOperationException("A disabled connection must cancel the queued write.");
        }
        catch (OperationCanceledException)
        {
            Check(File.ReadAllText(mailbox).Length == 2048, "Cancellation leaves the existing mailbox unchanged.");
        }
        bridge.Write(command);
        Check(JsonSerializer.Deserialize<WpsBridgeTransport.Command>(File.ReadAllText(mailbox), json) == command,
            "Writes replace the existing mailbox contents and identify the exact show.");

        var other = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, $"state-{other:N}.json"),
            JsonSerializer.Serialize(state with { InstanceId = other }, json));
        Check(bridge.Read(now) == (null, true), "Two eligible WPS processes are ambiguous.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
