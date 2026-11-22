using FolderDeck.Core.Transfer;

namespace FolderDeck.Core.Tests;

public class FileClipboardTests
{
    private static readonly string LongPath = @"E:\" + new string('長', 150) + @"\" + new string('b', 150) + @"\写真.png";

    // ---- CF_HDROP -------------------------------------------------------------------------------

    [Fact]
    public void HDrop_RoundTrip_JapaneseNames_AndALongPath()
    {
        string[] paths = [@"E:\資料\見積書 2026.xlsx", @"\\server\共有\フォルダー", LongPath];
        Assert.True(LongPath.Length > 260);

        var decoded = FileClipboard.DecodeHDrop(FileClipboard.EncodeHDrop(paths));

        Assert.Equal(paths, decoded.ToArray());
    }

    [Fact]
    public void EncodeHDrop_WritesTheDropFilesHeader_AndADoubleNul()
    {
        byte[] expected =
        [
            0x14, 0x00, 0x00, 0x00,                         // pFiles = 20
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // pt
            0x00, 0x00, 0x00, 0x00,                         // fNC
            0x01, 0x00, 0x00, 0x00,                         // fWide
            0x45, 0x00, 0x3A, 0x00, 0x5C, 0x00, 0x61, 0x00, // E:\a
            0x00, 0x00,                                     // end of the path
            0x00, 0x00,                                     // end of the list
        ];

        Assert.Equal(expected, FileClipboard.EncodeHDrop([@"E:\a"]));
    }

    [Fact]
    public void DecodeHDrop_Garbage_IsEmpty()
    {
        byte[] valid = FileClipboard.EncodeHDrop([@"E:\a.txt"]);

        Assert.Empty(FileClipboard.DecodeHDrop([]));
        Assert.Empty(FileClipboard.DecodeHDrop(new byte[10]));                 // shorter than the header
        Assert.Empty(FileClipboard.DecodeHDrop(new byte[64]));                 // pFiles = 0 points into the header
        Assert.Empty(FileClipboard.DecodeHDrop(Enumerable.Repeat((byte)0xFF, 64).ToArray())); // pFiles past the end
        Assert.Empty(FileClipboard.DecodeHDrop(valid.AsSpan(0, valid.Length - 6)));           // cut inside the name
    }

    // ---- Preferred DropEffect -------------------------------------------------------------------

    [Fact]
    public void DropEffect_Move_IsTwo()
    {
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00 }, FileClipboard.EncodeDropEffect(TransferKind.Move));
        Assert.Equal(new byte[] { 0x05, 0x00, 0x00, 0x00 }, FileClipboard.EncodeDropEffect(TransferKind.Copy));
        Assert.Equal(TransferKind.Move, FileClipboard.DecodeDropEffect(new byte[] { 0x02, 0x00, 0x00, 0x00 }));
    }

    [Theory]
    [InlineData(1)] // copy
    [InlineData(3)] // copy | move
    [InlineData(5)] // copy | link (what Explorer writes for a copy)
    [InlineData(7)] // copy | move | link
    public void DropEffect_WithTheCopyBit_IsCopy(int effect)
    {
        Assert.Equal(TransferKind.Copy, FileClipboard.DecodeDropEffect(new byte[] { (byte)effect, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void DropEffect_ShortBuffer_IsCopy()
    {
        Assert.Equal(TransferKind.Copy, FileClipboard.DecodeDropEffect(new byte[] { 0x02, 0x00 }));
        Assert.Equal(TransferKind.Copy, FileClipboard.DecodeDropEffect([]));
    }
}
