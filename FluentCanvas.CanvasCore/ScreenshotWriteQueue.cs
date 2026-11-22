using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCanvas.Helpers;

// Enqueue and Drain are called by the owning window's dispatcher. Only detached
// pixel buffers and destinations are used by its worker.
internal sealed class ScreenshotWriteQueue
{
    private readonly SemaphoreSlim capacity = new(3, 3);
    private Task tail = Task.CompletedTask;

    internal Task Enqueue(Action write) => Enqueue(() =>
    {
        write();
        return true;
    });

    internal Task<TResult> Enqueue<TResult>(Func<TResult> write)
    {
        // Capture first, then apply backpressure if encoding cannot keep up. This
        // keeps every screenshot and limits the number of detached bitmaps in memory.
        capacity.Wait();
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        tail = tail.ContinueWith(_ =>
        {
            try { completion.SetResult(write()); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { capacity.Release(); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return completion.Task;
    }

    internal Task WhenIdle() => tail;

    internal void Drain() => WhenIdle().GetAwaiter().GetResult();
}
