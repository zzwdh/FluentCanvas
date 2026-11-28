using System.Threading.Tasks;

namespace FluentCanvas.Helpers;

// Dispatcher-owned. External file activation waits for the exact slide read it observed.
internal sealed class PowerPointSlideLoad
{
    private TaskCompletionSource<bool> current;
    internal bool Pending => current != null;

    internal Task<bool> Begin()
    {
        End(false);
        current = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return current.Task;
    }

    internal Task<bool> WaitAsync() => current?.Task ?? Task.FromResult(true);
    internal bool IsCurrent(Task<bool> read) => ReferenceEquals(current?.Task, read);
    internal void End(bool installed)
    {
        var previous = current;
        current = null;
        previous?.TrySetResult(installed);
    }
}
