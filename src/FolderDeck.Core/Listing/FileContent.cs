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
}
