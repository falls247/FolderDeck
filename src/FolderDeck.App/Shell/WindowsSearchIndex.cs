using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;

namespace FolderDeck.Shell;

/// <summary>
/// Asks the Windows Search index (the provider <c>Search.CollatorDSO</c> through ADO, late bound:
/// no package) for names below a folder, for <see cref="DeepSearch"/>. Only what the indexer holds
/// is found (by default the user's libraries and the Start menu), in a moment; the walk of
/// <see cref="DeepSearch"/> finds the rest. Blocks: pool threads only. When the index cannot be
/// asked (the WSearch service is off), it is not asked again for a while.
/// </summary>
public static class WindowsSearchIndex
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    /// <summary>After a failure the index is left alone this long (each search would fail and log again).</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private static long s_unavailableUntil;

    public static IReadOnlyList<string> Find(string root, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref s_unavailableUntil)
            || Type.GetTypeFromProgID("ADODB.Connection") is not { } connectionType)
        {
            return [];
        }

        dynamic? connection = null;
        dynamic? records = null;
        var found = new List<string>();
        try
        {
            connection = Activator.CreateInstance(connectionType)!;
            connection.CommandTimeout = 10;
            connection.Open(ConnectionString);
            records = connection.Execute(DeepSearch.IndexQuery(root, terms, DeepSearch.MaxResults));
            while (!(bool)records.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (records.Fields.Item(0).Value is string path && path.Length > 0)
                {
                    found.Add(path);
                }

                records.MoveNext();
            }
        }
        catch (COMException ex)
        {
            Interlocked.Exchange(ref s_unavailableUntil, Environment.TickCount64 + (long)RetryAfter.TotalMilliseconds);
            AppLog.Warn($"Windows Search could not be asked (0x{ex.HResult:X8}); searching by walking the folders only for {RetryAfter.TotalMinutes:F0} min", ex);
            return [];
        }
        finally
        {
            Release(records, close: true);
            Release(connection, close: true);
        }

        return found;
    }

    private static void Release(dynamic? com, bool close)
    {
        if (com is null)
        {
            return;
        }

        try
        {
            if (close && (int)com.State != 0)
            {
                com.Close();
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)com);
        }
    }
}
