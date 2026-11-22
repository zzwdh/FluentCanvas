#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Controls;

namespace FluentCanvas.Helpers;

internal sealed class CanvasDocumentMedia
{
    private static readonly ConditionalWeakTable<MediaElement, CanvasDocumentMedia> Owners = new();
    private static readonly ConcurrentDictionary<string, WeakReference<CanvasDocumentMedia>> Folders = new();
    private static readonly Timer Cleanup;

    static CanvasDocumentMedia()
    {
        Cleanup = new Timer(_ => SweepUnused(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Cleanup.Dispose();
            foreach (var folder in Folders.Keys) Delete(folder);
        };
    }

    private CanvasDocumentMedia(string path) => Path = path;

    internal string Path { get; }

    internal static CanvasDocumentMedia Create()
    {
        SweepUnused();
        var storage = new CanvasDocumentMedia(Directory.CreateTempSubdirectory("FluentCanvas-Media-").FullName);
        Folders[storage.Path] = new WeakReference<CanvasDocumentMedia>(storage);
        return storage;
    }

    internal static CanvasDocumentMedia? GetOwner(MediaElement element) => Owners.TryGetValue(element, out var owner) ? owner : null;

    internal void Attach(MediaElement element) => Owners.Add(element, this);

    internal void Discard() => Delete(Path);

    internal static void SweepUnused()
    {
        foreach (var folder in Folders)
        {
            if (!folder.Value.TryGetTarget(out _)) Delete(folder.Key);
        }
    }

    private static void Delete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        Folders.TryRemove(folder, out _);
    }
}
