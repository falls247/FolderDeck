using FolderDeck.Core.Layout;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Tests;

public class AppThemeTests
{
    [Theory]
    [InlineData("system", AppTheme.System)]
    [InlineData("light", AppTheme.Light)]
    [InlineData(" Dark ", AppTheme.Dark)]
    [InlineData("blue", AppTheme.System)]
    [InlineData(null, AppTheme.System)]
    public void FromSetting_UnknownIsSystem_AndRoundTrips(string? value, AppTheme expected)
    {
        Assert.Equal(expected, AppThemes.FromSetting(value));
        Assert.Equal(expected, AppThemes.FromSetting(AppThemes.ToSetting(expected)));
    }

    [Fact]
    public void IsDark_FixedChoiceIgnoresWindows_SystemFollowsIt()
    {
        Assert.False(AppThemes.IsDark(AppTheme.Light, systemIsDark: true));
        Assert.True(AppThemes.IsDark(AppTheme.Dark, systemIsDark: false));
        Assert.True(AppThemes.IsDark(AppTheme.System, systemIsDark: true));
        Assert.False(AppThemes.IsDark(AppTheme.System, systemIsDark: false));
    }

    [Fact]
    public void Settings_Theme_DefaultsSystem_InvalidReplaced_RoundTrips()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":1,"theme":"sepia"}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(AppThemes.System, store.Current.Theme);
        Assert.Equal(AppThemes.System, new AppSettings().Theme);

        store.Update(s => s.Theme = AppThemes.Dark);
        store.Flush();
        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(AppThemes.Dark, reloaded.Current.Theme);
    }
}
