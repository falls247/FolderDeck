using System.Collections.Concurrent;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

/// <summary>
/// Files that are previewable because Windows can draw a thumbnail of them (videos, PDFs with a
/// reader installed, design files with their app's handler…): their extension has an
/// <c>IThumbnailProvider</c> registered, or it is a common video format. Programs and shortcuts
/// never count. Answers are cached per extension; safe on any thread.
/// </summary>
internal static class ShellPreviewSupport
{
    /// <summary>IID of <c>IThumbnailProvider</c>: the shell extension that draws thumbnails.</summary>
    private const string ThumbnailProviderIid = "{e357fccd-a995-4576-b01f-234630154e96}";

    private static readonly ConcurrentDictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the shell can make a picture of files named like <paramref name="name"/>.</summary>
    public static bool HasThumbnail(string name)
    {
        string ext = PathTools.ExtensionOf(name);
        if (ext.Length < 2 || PreviewKinds.IsNeverPreviewed(name))
        {
            return false;
        }

        return PreviewKinds.IsVideo(name) || Known.GetOrAdd(ext, HasThumbnailProvider);
    }

    /// <summary>A file row previews: a kind the app reads itself, or a shell thumbnail.</summary>
    public static bool IsPreviewable(string name) => PreviewKinds.IsPreviewable(name) || HasThumbnail(name);

    private static bool HasThumbnailProvider(string ext)
    {
        try
        {
            Span<char> clsid = stackalloc char[64];
            uint length = (uint)clsid.Length;
            var hr = PInvoke.AssocQueryString(ASSOCF.ASSOCF_NONE, ASSOCSTR.ASSOCSTR_SHELLEXTENSION, ext, ThumbnailProviderIid, clsid, ref length);
            return hr.Succeeded && length > 1;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return false;
        }
    }
}
