using System.Text.Json;

namespace FolderDeck.Core.Infrastructure;

/// <summary>What <see cref="SessionMarker"/> records about the running session.</summary>
public sealed record SessionInfo
{
    public int Pid { get; init; }

    public string Version { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Refreshed about once a minute while the UI thread is responsive.</summary>
    public DateTimeOffset LastAliveAt { get; init; }

    public long WorkingSetMb { get; init; }

    public long ManagedHeapMb { get; init; }
}

/// <summary>
/// "A session is running" record (<c>logs\session.json</c>). Written at start, refreshed while
/// the app runs, removed on a clean exit. If it is still there at the next start, the previous
/// session ended without running its exit code: a crash, a forced kill (taskkill /F, Task
/// Manager), a power loss. Never throws.
/// </summary>
public sealed class SessionMarker
{
    public const string FileName = "session.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SessionMarker(string logDirectory)
    {
        FilePath = Path.Combine(logDirectory, FileName);
    }

    public string FilePath { get; }

    /// <summary>The record a previous session left behind, or null when it exited cleanly.</summary>
    /// <remarks>An unreadable record still means an unclean exit: it is returned with only <c>Pid = 0</c>.</remarks>
    public SessionInfo? ReadLeftover()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(FilePath), Options) ?? new SessionInfo();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new SessionInfo();
        }
    }

    public void Write(SessionInfo info)
    {
        try
        {
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(info, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: losing the record only loses the "previous session crashed" hint.
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
