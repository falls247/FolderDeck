using System.Globalization;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Services;

/// <summary>
/// Puts a UI thread that stops answering into <c>app.log</c> (a hang writes nothing by itself: the
/// thread that would log is the one that is stuck). A background thread posts a no-op to the UI
/// thread every second at the highest priority; when none has run for 5 s it logs a warning with
/// what the app was doing (<see cref="Note"/>), again at 30 s, 2 min and 10 min, and once more when
/// the UI thread answers again (how long it was gone). The lines start with <c>lifecycle: the UI
/// thread</c>; <c>tools\analyze-log.ps1</c> counts them per session.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly long[] ReportAfterMs = [5_000, 30_000, 120_000, 600_000];

    private static volatile string s_activity = "starting";

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private long _answeredAt = Environment.TickCount64;
    private long _answers; // pings the UI thread ran
    private int _pinging;

    public UiWatchdog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Watch) { IsBackground = true, Name = "FolderDeck UI watchdog" };
    }

    /// <summary>
    /// What the UI is doing now ("loading E:\写真", "showing E:\写真: 12,000 items"): quoted when the UI
    /// thread stops answering. Cheap (one field write); call it on the UI thread.
    /// </summary>
    public static void Note(string activity) => s_activity = activity;

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromMilliseconds(500)); // its last line (an answer after a report) reaches the log
    }

    private void Watch()
    {
        long lastLoop = Environment.TickCount64;
        int reported = 0;        // how many of ReportAfterMs were logged for the current silence
        long silentSince = 0;    // the tick of the last answer before the current silence
        long answersAtReport = 0;
        while (!_stop.Wait(Interval))
        {
            if (_dispatcher.HasShutdownStarted)
            {
                break;
            }

            long now = Environment.TickCount64;

            // This thread itself was held up far longer than its interval (the PC slept, the process
            // was suspended or a debugger stopped it): that time says nothing about the UI. Not while
            // a silence is reported, though: the UI may still be stuck after the gap.
            if (now - lastLoop > Interval.TotalMilliseconds * 5 && reported == 0)
            {
                Interlocked.Exchange(ref _answeredAt, now);
            }

            lastLoop = now;
            if (reported > 0 && Interlocked.Read(ref _answers) > answersAtReport)
            {
                LogAnswered(silentSince);
                reported = 0;
            }

            long answeredAt = Interlocked.Read(ref _answeredAt);

            long silent = now - answeredAt;
            if (reported < ReportAfterMs.Length && silent >= ReportAfterMs[reported])
            {
                if (reported == 0)
                {
                    silentSince = answeredAt;
                    answersAtReport = Interlocked.Read(ref _answers);
                    var since = DateTimeOffset.Now.AddMilliseconds(-silent);
                    AppLog.Warn($"lifecycle: the UI thread has not answered for {silent / 1000} s (since {since:HH:mm:ss.fff}); last activity: {s_activity}");
                }
                else
                {
                    AppLog.Warn($"lifecycle: the UI thread has still not answered after {silent / 1000} s; last activity: {s_activity}");
                }

                reported++;
            }

            // One ping at a time: a stuck UI thread does not pile up a queue of them.
            if (Interlocked.CompareExchange(ref _pinging, 1, 0) == 0)
            {
                try
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                    {
                        Interlocked.Exchange(ref _answeredAt, Environment.TickCount64);
                        Interlocked.Increment(ref _answers);
                        Volatile.Write(ref _pinging, 0);
                    });
                }
                catch (InvalidOperationException)
                {
                    break; // the dispatcher has shut down
                }
            }
        }

        // Closing right after the UI came back: say so, or the log reads as a hang to the end.
        if (reported > 0 && Interlocked.Read(ref _answers) > answersAtReport)
        {
            LogAnswered(silentSince);
        }
    }

    private void LogAnswered(long silentSince)
    {
        string seconds = ((Interlocked.Read(ref _answeredAt) - silentSince) / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
        AppLog.Warn($"lifecycle: the UI thread answered again after {seconds} s; last activity: {s_activity}");
    }
}
