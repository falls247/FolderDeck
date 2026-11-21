using System.Text;

namespace FolderDeck.Core.Infrastructure;

/// <summary>
/// Minimal file log (<c>logs\app.log</c>, rotated to <c>app.log.1</c> above 5 MB).
/// Never throws. File contents are never logged, only paths and exceptions.
/// </summary>
/// <remarks>
/// Line format (parsed by <c>tools\analyze-log.ps1</c>):
/// <c>yyyy-MM-dd HH:mm:ss.fff +09:00 [LEVEL] [p&lt;pid&gt;/t&lt;thread&gt;] message</c>.
/// Start, close requests and exit are written as <c>lifecycle: …</c> messages.
/// </remarks>
public static class AppLog
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly int Pid = Environment.ProcessId;
    private static string? _file;

    public static string? FilePath => _file;

    public static void Initialize(string logDirectory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                _file = Path.Combine(logDirectory, "app.log");
            }
            catch
            {
                _file = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        lock (Gate)
        {
            if (_file is null)
            {
                return;
            }

            try
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_file, _file + ".1", overwrite: true);
                }

                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
                sb.Append(" [").Append(level).Append("] ");
                sb.Append("[p").Append(Pid).Append("/t").Append(Environment.CurrentManagedThreadId).Append("] ");
                sb.Append(message);
                if (ex is not null)
                {
                    sb.AppendLine();
                    sb.Append(ex);
                }

                sb.AppendLine();
                File.AppendAllText(_file, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
