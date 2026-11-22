using FolderDeck.Core.Layout;
using FolderDeck.Core.Text;

namespace FolderDeck.Core.Tests;

public class DetailsColumnsTests
{
    [Fact]
    public void Normalize_FillsDefaults_Clamps_AndDropsUnknownKeys()
    {
        var defaults = DetailsColumns.Normalize(null);
        Assert.Equal(3, defaults.Count);
        Assert.Equal(150, defaults[DetailsColumns.Modified]);
        Assert.Equal(170, defaults[DetailsColumns.Type]);
        Assert.Equal(96, defaults[DetailsColumns.Size]);

        var normalized = DetailsColumns.Normalize(new Dictionary<string, double>
        {
            [DetailsColumns.Modified] = 10,     // below Min
            [" Type "] = 5000,                  // hand-edited key, above Max
            [DetailsColumns.Size] = double.NaN, // garbage -> default
            ["name"] = 300,                     // unknown key
        });
        Assert.Equal(3, normalized.Count);
        Assert.Equal(DetailsColumns.Min, normalized[DetailsColumns.Modified]);
        Assert.Equal(DetailsColumns.Max, normalized[DetailsColumns.Type]);
        Assert.Equal(96, normalized[DetailsColumns.Size]);
        Assert.False(normalized.ContainsKey("name"));
    }

    [Fact]
    public void Fit_KeepsStoredWidths_WhenTheNameColumnHasRoom()
    {
        var widths = new Dictionary<string, double>
        {
            [DetailsColumns.Modified] = 200,
            [DetailsColumns.Type] = 100,
            [DetailsColumns.Size] = 80,
        };

        // 200 + 100 + 80 = 380, and 500 - 380 leaves exactly the minimum name width.
        var fitted = DetailsColumns.Fit(500, widths);
        Assert.Equal(200, fitted[DetailsColumns.Modified]);
        Assert.Equal(100, fitted[DetailsColumns.Type]);
        Assert.Equal(80, fitted[DetailsColumns.Size]);
    }

    [Fact]
    public void Fit_ShrinksColumnsToLeaveTheNameWidth_ButNeverBelowMin()
    {
        // Defaults sum to 416; 400 wide leaves 280 for the columns: each gives up half of what it has above Min.
        var fitted = DetailsColumns.Fit(400, DetailsColumns.Defaults);
        Assert.Equal(99, fitted[DetailsColumns.Modified]);
        Assert.Equal(109, fitted[DetailsColumns.Type]);
        Assert.Equal(72, fitted[DetailsColumns.Size]);
        Assert.Equal(400 - 120, fitted.Values.Sum());

        var narrow = DetailsColumns.Fit(100, DetailsColumns.Defaults);
        Assert.All(narrow.Values, width => Assert.Equal(DetailsColumns.Min, width));

        // With no name minimum the same 416 px are enough for the stored widths.
        var custom = DetailsColumns.Fit(416, DetailsColumns.Defaults, minName: 0);
        Assert.Equal(150, custom[DetailsColumns.Modified]);
        Assert.Equal(96, custom[DetailsColumns.Size]);
    }

    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1025L, "2 KB")]
    [InlineData(1263616L, "1,234 KB")]
    [InlineData(long.MaxValue, "9,007,199,254,740,992 KB")]
    public void FileSizeKb_RoundsUpToWholeKilobytes_WithThousandsSeparators(long bytes, string expected)
    {
        Assert.Equal(expected, DetailsText.FileSizeKb(bytes));
    }

    [Theory]
    [InlineData(".jpg", false, "JPG ファイル")]
    [InlineData("Docx", false, "DOCX ファイル")]
    [InlineData("", false, "ファイル")]
    [InlineData(".", false, "ファイル")]
    [InlineData(".d", true, "ファイル フォルダー")]
    public void TypeFallback_NamesTheExtension_OrTheFolder(string extension, bool isFolder, string expected)
    {
        Assert.Equal(expected, DetailsText.TypeFallback(extension, isFolder));
    }
}
