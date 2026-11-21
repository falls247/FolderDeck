using System.IO.Compression;
using System.Text;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;

namespace FolderDeck.Core.Tests;

public sealed class PreviewTests : IDisposable
{
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fd-preview-" + Guid.NewGuid().ToString("N"));

    public PreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPG", true)]
    [InlineData("scan.tiff", true)]
    [InlineData("deck.pptx", true)]
    [InlineData("deck.PPSX", true)]
    [InlineData("notes.docx", true)]
    [InlineData("book.xlsx", true)]
    [InlineData("readme.txt", true)]
    [InlineData("old.doc", false)]
    [InlineData("old.xls", false)]
    [InlineData("setup.exe", false)]
    [InlineData("noext", false)]
    public void Previewable_files_are_images_office_files_and_text(string name, bool expected) =>
        Assert.Equal(expected, PreviewKinds.IsPreviewable(name));

    [Theory]
    [InlineData("a.png", PreviewKind.Image)]
    [InlineData("scan.TIFF", PreviewKind.Image)]
    [InlineData("deck.pptx", PreviewKind.Presentation)]
    [InlineData("notes.docx", PreviewKind.WordDocument)]
    [InlineData("macro.docm", PreviewKind.WordDocument)]
    [InlineData("template.DOTX", PreviewKind.WordDocument)]
    [InlineData("book.xlsx", PreviewKind.Spreadsheet)]
    [InlineData("macro.XLSM", PreviewKind.Spreadsheet)]
    [InlineData("template.xltx", PreviewKind.Spreadsheet)]
    [InlineData("readme.txt", PreviewKind.Text)]
    [InlineData("app.log", PreviewKind.Text)]
    [InlineData("data.CSV", PreviewKind.Text)]
    [InlineData("Program.cs", PreviewKind.Text)]
    [InlineData("build.ps1", PreviewKind.Text)]
    [InlineData("MainWindow.xaml", PreviewKind.Text)]
    [InlineData(".gitignore", PreviewKind.Text)]
    [InlineData("old.doc", PreviewKind.None)]
    [InlineData("old.xls", PreviewKind.None)]
    [InlineData("binary.xlsb", PreviewKind.None)]
    [InlineData("movie.mp4", PreviewKind.None)]
    [InlineData("setup.exe", PreviewKind.None)]
    [InlineData("noext", PreviewKind.None)]
    [InlineData("dot.", PreviewKind.None)]
    public void Kind_of_a_file_follows_its_extension(string name, PreviewKind expected) =>
        Assert.Equal(expected, PreviewKinds.KindOf(name));

    [Theory]
    [InlineData("clip.mp4", true, false)]
    [InlineData("clip.MKV", true, false)]
    [InlineData("clip.m2ts", true, false)]
    [InlineData("tool.exe", false, true)]
    [InlineData("lib.dll", false, true)]
    [InlineData("link.lnk", false, true)]
    [InlineData("site.url", false, true)]
    [InlineData("setup.msi", false, true)]
    [InlineData("doc.pdf", false, false)]
    public void Videos_and_programs_are_told_apart(string name, bool video, bool neverPreviewed)
    {
        Assert.Equal(video, PreviewKinds.IsVideo(name));
        Assert.Equal(neverPreviewed, PreviewKinds.IsNeverPreviewed(name));
    }

    [Theory]
    [InlineData("deck.pptx", true)]
    [InlineData("notes.docx", true)]
    [InlineData("book.xlsm", true)]
    [InlineData("readme.txt", false)]
    [InlineData("a.png", false)]
    public void Open_xml_files_are_presentations_documents_and_workbooks(string name, bool expected) =>
        Assert.Equal(expected, PreviewKinds.IsOpenXml(name));

    [Fact]
    public void Embedded_thumbnail_follows_the_package_relationship()
    {
        string path = MakePackage("deck.pptx", ("_rels/.rels", Rels("docProps/cover%20image.jpeg")), ("docProps/cover image.jpeg", FakeJpeg));
        Assert.Equal(FakeJpeg, OfficeThumbnail.ReadEmbedded(path));
    }

    [Fact]
    public void Embedded_thumbnail_accepts_an_absolute_target_and_other_case()
    {
        string path = MakePackage("deck.pptx", ("_rels/.rels", Rels("/DocProps/Thumbnail.JPEG")), ("docProps/thumbnail.jpeg", FakeJpeg));
        Assert.Equal(FakeJpeg, OfficeThumbnail.ReadEmbedded(path));
    }

    [Fact]
    public void Embedded_thumbnail_falls_back_to_the_usual_part_name()
    {
        string path = MakePackage("deck.pptx", ("docProps/thumbnail.jpeg", FakeJpeg));
        Assert.Equal(FakeJpeg, OfficeThumbnail.ReadEmbedded(path));
    }

    [Fact]
    public void Wmf_thumbnails_and_missing_or_broken_files_give_null()
    {
        string wmf = MakePackage("old.pptx", ("_rels/.rels", Rels("docProps/thumbnail.wmf")), ("docProps/thumbnail.wmf", FakeJpeg));
        Assert.Null(OfficeThumbnail.ReadEmbedded(wmf));

        string none = MakePackage("none.pptx", ("ppt/presentation.xml", Encoding.UTF8.GetBytes("<p/>")));
        Assert.Null(OfficeThumbnail.ReadEmbedded(none));

        string broken = Path.Combine(_dir, "broken.pptx");
        File.WriteAllText(broken, "not a zip");
        Assert.Null(OfficeThumbnail.ReadEmbedded(broken));

        Assert.Null(OfficeThumbnail.ReadEmbedded(Path.Combine(_dir, "missing.pptx")));
    }

    [Fact]
    public void Embedded_thumbnail_can_be_read_while_another_program_has_the_file_open()
    {
        string path = MakePackage("open.pptx", ("docProps/thumbnail.jpeg", FakeJpeg));
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write);
        Assert.Equal(FakeJpeg, OfficeThumbnail.ReadEmbedded(path));
    }

    [Fact]
    public void Embedded_thumbnail_of_a_cloud_only_file_is_not_read()
    {
        string path = MakePackage("cloud.pptx", ("docProps/thumbnail.jpeg", FakeJpeg));
        File.SetAttributes(path, FileAttributes.Archive | FileAttributes.Offline);
        try
        {
            Assert.Null(OfficeThumbnail.ReadEmbedded(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(FakeJpeg, OfficeThumbnail.ReadEmbedded(path));
    }

    [Fact]
    public void Local_missing_and_invalid_paths_are_not_cloud_only()
    {
        string local = Path.Combine(_dir, "local.txt");
        File.WriteAllText(local, "here");
        Assert.False(FileContent.IsCloudOnly(local));
        Assert.False(FileContent.IsCloudOnly(_dir));
        Assert.False(FileContent.IsCloudOnly(Path.Combine(_dir, "missing.txt")));
        Assert.False(FileContent.IsCloudOnly(string.Empty));
        Assert.False(FileContent.IsCloudOnly("bad\0path"));
    }

    [Theory]
    [InlineData(0x400000 | 0x400 | 0x20, true)] // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (OneDrive online-only)
    [InlineData(0x40000, true)] // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData((int)FileAttributes.Offline, true)]
    [InlineData((int)(FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.SparseFile), false)] // hydrated placeholder
    [InlineData(0x80000 | 0x20, false)] // FILE_ATTRIBUTE_PINNED ("always keep on this device")
    [InlineData((int)FileAttributes.Normal, false)]
    public void Cloud_only_files_are_told_by_their_attributes(int attributes, bool expected) =>
        Assert.Equal(expected, FileContent.IsCloudOnly((FileAttributes)attributes));

    [Fact]
    public void Session_marker_round_trips_and_is_gone_after_a_clean_exit()
    {
        var marker = new SessionMarker(_dir);
        Assert.Null(marker.ReadLeftover());

        var info = new SessionInfo
        {
            Pid = 4242,
            Version = "0.1.0.0",
            StartedAt = new DateTimeOffset(2026, 11, 21, 16, 0, 0, TimeSpan.FromHours(9)),
            LastAliveAt = new DateTimeOffset(2026, 11, 21, 16, 5, 0, TimeSpan.FromHours(9)),
            WorkingSetMb = 180,
            ManagedHeapMb = 40,
        };
        marker.Write(info);
        Assert.Equal(info, marker.ReadLeftover());

        marker.Delete();
        Assert.Null(marker.ReadLeftover());
    }

    [Fact]
    public void Unreadable_session_marker_still_counts_as_an_unclean_exit()
    {
        var marker = new SessionMarker(_dir);
        File.WriteAllText(marker.FilePath, "{ broken");
        var leftover = marker.ReadLeftover();
        Assert.NotNull(leftover);
        Assert.Equal(0, leftover.Pid);
    }

    private static byte[] Rels(string target) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        $"<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"{target}\"/>" +
        "</Relationships>");

    private string MakePackage(string name, params (string Entry, byte[] Data)[] parts)
    {
        string path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in parts)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(data);
        }

        return path;
    }
}
