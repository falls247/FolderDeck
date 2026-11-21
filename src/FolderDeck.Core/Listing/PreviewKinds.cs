namespace FolderDeck.Core.Listing;

/// <summary>
/// Which file rows of the overlay / flyouts show a preview when the pointer rests on them:
/// images, and PowerPoint presentations (their cover, i.e. the first slide).
/// </summary>
public static class PreviewKinds
{
    private static readonly HashSet<string> ExtraImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jfif", ".tif", ".tiff", ".heic", ".heif", ".avif",
    };

    /// <summary>Open XML presentations: they carry a thumbnail of the first slide (<c>docProps/thumbnail.*</c>).</summary>
    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm",
    };

    public static bool IsImage(string name) =>
        CoverFinder.IsImageName(name) || ExtraImageExtensions.Contains(Path.GetExtension(name));

    public static bool IsPresentation(string name) => PresentationExtensions.Contains(Path.GetExtension(name));

    public static bool IsPreviewable(string name) => IsImage(name) || IsPresentation(name);
}
