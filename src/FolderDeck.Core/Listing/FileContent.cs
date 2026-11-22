namespace FolderDeck.Core.Listing;

/// <summary>What can be told about a file's content from its metadata alone, without opening it.</summary>
public static class FileContent
{
    /// <summary><c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c>: opening the file fetches it from remote storage.</summary>
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;

    /// <summary><c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>: reading the file's data downloads it (OneDrive Files On-Demand).</summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private const FileAttributes CloudOnlyAttributes = RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline;

    /// <summary>
    /// The file's data is not on this machine (a cloud-only placeholder such as an OneDrive file that
    /// is not "always keep on this device", or an offline / archived file): reading it would download
    /// the whole file, so previews leave it alone. Only the attributes are read, which never hydrates
    /// the file. False when the attributes cannot be read (missing file, no access…). Never throws.
    /// </summary>
    public static bool IsCloudOnly(string path)
    {
        try
        {
            return IsCloudOnly(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsCloudOnly(FileAttributes attributes) => (attributes & CloudOnlyAttributes) != 0;

    /// <summary>
    /// A cloud file placeholder (Cloud Files API: OneDrive Files On-Demand and other sync apps) whose
    /// data its sync app downloads when the file is read, so that <see cref="DownloadAsync"/> can
    /// bring it here. A file that is only offline or recalled on open (archive storage) is not one:
    /// its recall can take very long. Only the attributes are read. Never throws.
    /// </summary>
    public static bool IsDownloadable(string path)
    {
        try
        {
            return IsDownloadable(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsDownloadable(FileAttributes attributes) => (attributes & RecallOnDataAccess) != 0;

    /// <summary>
    /// Reads the whole file, which makes the sync app of a cloud placeholder download it: it is then
    /// on this machine like any other file. Success is having read to the end (the attributes may
    /// lag behind). Asynchronous I/O, so no thread waits for the network. A cancelled token stops the
    /// read (the sync app is told the data is no longer wanted) and the call throws
    /// <see cref="OperationCanceledException"/>. Otherwise never throws: a file that cannot be read
    /// gives how it failed (<see cref="DownloadFailureOf"/>).
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        };

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var stream = new FileStream(path, options);
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }

            return DownloadResult.Done;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            // A read we cancelled may end with a sync app's error rather than "aborted": still ours,
            // not a refusal by Windows or the user.
            cancellationToken.ThrowIfCancellationRequested();
            return DownloadFailureOf(ex.HResult);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DownloadResult.Failed;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// How a cloud file's download failed, from the error the sync app returned (Cloud Files API,
    /// ERROR_CLOUD_FILE_*; .NET gives them as <see cref="IOException"/> with the HRESULT).
    /// </summary>
    public static DownloadResult DownloadFailureOf(int hresult)
    {
        if ((hresult & unchecked((int)0xFFFF0000)) != unchecked((int)0x80070000))
        {
            return DownloadResult.Failed;
        }

        return (hresult & 0xFFFF) switch
        {
            // REQUEST_ABORTED (what an app blocked in "Automatic file downloads" gets), ACCESS_DENIED,
            // REQUEST_CANCELED (the user cancelled it in Windows' download notification).
            393 or 395 or 398 => DownloadResult.Refused,

            // PROVIDER_NOT_RUNNING, NETWORK_UNAVAILABLE, PROVIDER_TERMINATED, REQUEST_TIMEOUT.
            362 or 388 or 404 or 426 => DownloadResult.Unreachable,
            _ => DownloadResult.Failed,
        };
    }
}

/// <summary>What became of <see cref="FileContent.DownloadAsync"/>.</summary>
public enum DownloadResult
{
    /// <summary>Read to the end: the file is on this machine.</summary>
    Done,

    /// <summary>Did not download (missing file, broken data, an error of the sync app…).</summary>
    Failed,

    /// <summary>Cannot download for now: offline, or the sync app is not running. Worth trying again later.</summary>
    Unreachable,

    /// <summary>
    /// Windows or the user refused it: the app is blocked in "Automatic file downloads", or the
    /// download was cancelled in Windows' notification. Not to be asked again.
    /// </summary>
    Refused,
}
