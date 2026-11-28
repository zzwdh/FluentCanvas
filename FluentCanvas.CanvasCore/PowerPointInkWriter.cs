using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCanvas.Helpers;

internal sealed class PowerPointInkWriter(Action<PowerPointInkWriter.Result> report)
{
    private const int RetryBatchSize = 8;
    private readonly object gate = new();
    private readonly Dictionary<WriteKey, WriteRequest> pending = new();
    private readonly LinkedList<WriteKey> ready = new();
    private readonly Dictionary<WriteKey, LinkedListNode<WriteKey>> queued = new();
    private readonly HashSet<WriteKey> failed = new();
    private Task worker = Task.CompletedTask;
    private bool running;
    private bool closed;

    private readonly record struct WriteKey(string Deck, int? Slide);
    // Pending, worker and read-back leases may overlap when a newer revision replaces a queued write.
    private sealed class WriteRequest(PowerPointInkStorage storage, FrozenInk ink, int lastSlide)
    {
        private int owners = 1;
        internal PowerPointInkStorage Storage => storage;
        internal FrozenInk Ink => ink;
        internal int LastSlide => lastSlide;
        internal void Retain() => Interlocked.Increment(ref owners);
        internal void Release()
        {
            if (Interlocked.Decrement(ref owners) == 0) ink?.Dispose();
        }
    }
    internal sealed record Result(bool IsPosition, bool UsedRecovery, Exception Error);

    internal bool HasPendingWrites
    {
        get { lock (gate) return pending.Count != 0; }
    }

    internal Task<byte[]> ReadSlideAsync(PowerPointInkStorage storage, string deck, int slide)
    {
        WriteRequest request;
        lock (gate)
        {
            pending.TryGetValue(new WriteKey(deck, slide), out request);
            request?.Retain();
        }
        return Task.Run(() =>
        {
            try { return request == null ? storage.ReadSlide(deck, slide) : request.Ink.ExportInk(); }
            finally { request?.Release(); }
        });
    }
    internal int? PendingLastSlide(string deck)
    {
        lock (gate) return pending.GetValueOrDefault(new WriteKey(deck, null))?.LastSlide;
    }

    internal void SaveNativePage(PowerPointInkStorage storage, string deck, int slide, FrozenInk ink) =>
        Enqueue(new WriteKey(deck, slide), new WriteRequest(storage, ink, 0));

    internal void SaveLastSlide(PowerPointInkStorage storage, string deck, int slide) =>
        Enqueue(new WriteKey(deck, null), new WriteRequest(storage, null, slide));

    private void Enqueue(WriteKey key, WriteRequest request)
    {
        lock (gate)
        {
            if (closed)
            {
                request.Release();
                throw new InvalidOperationException("The presentation ink writer has closed.");
            }
            // One frozen revision per dirty slide. A slow drive never blocks slide navigation.
            if (pending.TryGetValue(key, out var superseded)) superseded.Release();
            pending[key] = request;
            failed.Remove(key);
            if (queued.Remove(key, out var previous)) ready.Remove(previous);
            queued.Add(key, ready.AddLast(key));

            // Give previous failures another chance after fresh writes have been queued.
            foreach (var retry in failed.Take(RetryBatchSize).ToArray())
            {
                queued.Add(retry, ready.AddLast(retry));
                failed.Remove(retry);
            }
            StartWorker();
        }
    }

    private void StartWorker()
    {
        if (running || ready.Count == 0) return;
        running = true;
        worker = Task.Run(WritePending);
    }

    private void WritePending()
    {
        while (true)
        {
            WriteKey key;
            WriteRequest request;
            lock (gate)
            {
                if (ready.First == null)
                {
                    running = false;
                    return;
                }
                key = ready.First.Value;
                ready.RemoveFirst();
                queued.Remove(key);
                request = pending[key];
                request.Retain();
            }

            bool usedRecovery = false;
            Exception error = null;
            try
            {
                if (key.Slide is int slide)
                {
                    usedRecovery = request.Storage.WriteSlide(key.Deck, slide, request.Ink);
                }
                else
                    request.Storage.SaveLastSlide(key.Deck, request.LastSlide);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                ArgumentException or UnauthorizedAccessException)
            {
                error = ex;
            }

            bool isLatest;
            lock (gate)
            {
                isLatest = pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, request);
                if (isLatest)
                {
                    if (error == null)
                    {
                        pending.Remove(key);
                        request.Release();
                    }
                    else failed.Add(key);
                }
            }
            request.Release();
            if (isLatest && (error != null || usedRecovery)) report(new Result(key.Slide == null, usedRecovery, error));
        }
    }

    internal Task WhenIdle()
    {
        lock (gate) return worker;
    }

    internal void Close()
    {
        lock (gate) closed = true;
        FlushAsync().GetAwaiter().GetResult();
    }

    internal async Task<bool> FlushAsync()
    {
        await WhenIdle().ConfigureAwait(false);
        // Retry retained saves once and report whether it is safe to finish an update.
        Task work;
        lock (gate)
        {
            foreach (var key in failed) queued.Add(key, ready.AddLast(key));
            failed.Clear();
            StartWorker();
            work = worker;
        }
        await work.ConfigureAwait(false);
        return !HasPendingWrites;
    }
}
