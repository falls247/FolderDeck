using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Tests;

public class CardShapeTests
{
    [Theory]
    [InlineData(null, "portrait")]
    [InlineData("\"bogus\"", "portrait")]
    [InlineData("\" Square \"", "square")]
    [InlineData("\"DETAILS\"", "details")]
    public void Load_NormalizesCardShape_AndSavesItUnderCardShapeKey(string? json, string expected)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, json is null ? """{"version":1}""" : $$"""{"version":1,"card_shape":{{json}}}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(expected, store.Current.CardShape);

        store.Update(s => s.CardShape = CardShapes.ToSetting(CardShape.Square));
        store.Flush();
        Assert.Contains("\"card_shape\": \"square\"", File.ReadAllText(file));
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(CardShape.Square, CardShapes.FromSetting(reloaded.Current.CardShape));

        // the details list is the third value of the same key, and the only one that shows no cards
        Assert.Equal(CardShapes.Details, CardShapes.ToSetting(CardShape.Details));
        Assert.False(CardShapes.IsCards(CardShape.Details));
        Assert.True(CardShapes.IsCards(CardShape.Portrait) && CardShapes.IsCards(CardShape.Square));
    }

    [Theory]
    [InlineData(CardShape.Portrait, 200, 266.67)]
    [InlineData(CardShape.Square, 200, 200)]
    [InlineData(CardShape.Square, 123.45, 123.45)]
    public void ThumbHeight_IsThreeToFourForPortrait_AndOneToOneForSquare(CardShape shape, double width, double expected)
    {
        Assert.Equal(expected, CardShapes.ThumbHeight(width, shape), precision: 2);
    }
}
