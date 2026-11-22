using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Shell;

/// <summary>
/// Runs one call on its own background STA thread and reports the result as a task. For shell work
/// that is apartment-threaded and may show modal UI or block for a long time (<c>IFileOperation</c>,
/// a shell context menu): one thread per call, so a stuck call never holds up the next one (unlike
/// <see cref="StaWorkerPool"/>, whose workers are shared).
/// <para>
/// The task never faults because of <c>body</c>: every exception goes to <c>onError</c>, whose
/// value becomes the result (the caller logs there). It faults only when <c>onError</c> itself throws.
/// Continuations run on the thread pool, never on the STA thread.
/// </para>
/// </summary>
internal static class StaThread
{
    /// <summary>
    /// Dedicated background STA thread, no message loop of ours (<c>IFileOperation</c> pumps its own
    /// dialogs).
    /// </summary>
    public static Task<T> Run<T>(string name, Func<T> body, Func<Exception, T> onError)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onError);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(name, completion, onError, () =>
        {
            T value;
            try
            {
                value = body();
            }
            catch (Exception ex)
            {
                Fail(completion, ex, onError);
                return;
            }

            completion.TrySetResult(value);
        });
        return completion.Task;
    }

    /// <summary>
    /// Same, but <c>body</c> runs with a <see cref="Dispatcher"/> on that thread, so it may create an
    /// <c>HwndSource</c>, add hooks and pump with <see cref="Dispatcher.PushFrame"/>. Nothing pumps
    /// unless <c>body</c> does (a frame, or a modal loop of the system such as a popup menu).
    /// <para>
    /// An exception from <c>body</c>, or one thrown by a dispatcher callback while <c>body</c> runs
    /// (a window hook, a timer, a posted operation), goes to <c>onError</c>. A callback's exception
    /// is marked handled, so it does not escape through the window procedure; the frames pushed at
    /// that moment are asked to exit, <c>body</c> runs to its end (its <c>finally</c> blocks
    /// included) and its return value is dropped. The dispatcher is shut down before the task
    /// completes, which also disposes the <c>HwndSource</c>s of the thread.
    /// </para>
    /// </summary>
    public static Task<T> RunPumped<T>(string name, Func<Dispatcher, T> body, Func<Exception, T> onError)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onError);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(name, completion, onError, () =>
        {
            // Everything below runs on the new thread; CurrentDispatcher creates its dispatcher.
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            Exception? fault = null;
            bool bodyEnded = false;
            dispatcher.UnhandledException += (_, e) =>
            {
                // Unhandled, it would unwind through the native window procedure.
                e.Handled = true;
                if (bodyEnded)
                {
                    // The result is already decided: a failure of the teardown must not replace it.
                    AppLog.Warn($"{name}: a callback failed while the dispatcher was shutting down.", e.Exception);
                    return;
                }

                fault ??= e.Exception;

                // The failed callback may be the one that would have ended a pushed frame.
                Dispatcher.ExitAllFrames();
            };

            T value = default!;
            Exception? thrown = null;
            try
            {
                value = body(dispatcher);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            bodyEnded = true;
            try
            {
                dispatcher.InvokeShutdown();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"{name}: the dispatcher did not shut down cleanly.", ex);
            }

            // The callback's exception came first; one thrown by body after it is likely a consequence.
            Exception? error = fault ?? thrown;
            if (error is not null)
            {
                Fail(completion, error, onError);
                return;
            }

            completion.TrySetResult(value);
        });
        return completion.Task;
    }

    private static void Start<T>(string name, TaskCompletionSource<T> completion, Func<Exception, T> onError, Action work)
    {
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // Not expected (work reports its own failures): never leave the caller waiting.
                Fail(completion, ex, onError);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            // The thread could not be created (out of memory).
            Fail(completion, ex, onError);
        }
    }

    private static void Fail<T>(TaskCompletionSource<T> completion, Exception error, Func<Exception, T> onError)
    {
        T value;
        try
        {
            value = onError(error);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            return;
        }

        completion.TrySetResult(value);
    }
}
