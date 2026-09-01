using System.Diagnostics;

namespace Memo.Infrastructure;

internal static class UiExceptionReporter
{
    internal static void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Unhandled WPF dispatcher exception: {0}", exception);
        Trace.Flush();
    }
}

internal static class TaskObservation
{
    internal static void Observe(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = ObserveAsync(task);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            UiExceptionReporter.Report(exception);
        }
    }
}
