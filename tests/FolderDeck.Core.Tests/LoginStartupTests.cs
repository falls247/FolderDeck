using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Tests;

public class LoginStartupTests
{
    private const string Exe = @"C:\Program Files\Folder Deck\FolderDeck.exe";

    [Fact]
    public void BuildCommand_QuotesTheExe_AndReadsBackAsRegistered()
    {
        string command = LoginStartup.BuildCommand(Exe);

        Assert.Equal("\"C:\\Program Files\\Folder Deck\\FolderDeck.exe\" --background", command);
        Assert.Equal(LoginStartupState.Registered, LoginStartup.StateOf(command, Exe));
        Assert.True(LoginStartup.IsBackgroundLaunch(["--data-dir", "x", "--Background"]));
        Assert.False(LoginStartup.IsBackgroundLaunch(["--data-dir", "x"]));
    }

    [Theory]
    [InlineData(null, LoginStartupState.NotRegistered)]
    [InlineData("  ", LoginStartupState.NotRegistered)]
    [InlineData("\"D:\\old\\FolderDeck.exe\" --background", LoginStartupState.Other)] // the exe was moved
    [InlineData("\"C:\\Program Files\\Folder Deck\\FolderDeck.exe\"", LoginStartupState.Other)] // would start with a window
    [InlineData("c:\\program files\\folder deck\\folderdeck.EXE --background", LoginStartupState.Registered)] // typed by hand
    [InlineData("\"unterminated --background", LoginStartupState.Other)]
    public void StateOf_ComparesTheExeAndTheArgument(string? value, LoginStartupState expected)
    {
        Assert.Equal(expected, LoginStartup.StateOf(value, Exe));
    }

    [Theory]
    [InlineData("""{"version":2}""", false, false)]
    [InlineData("""{"version":2,"start_at_login":true,"keep_running_on_close":true}""", true, true)]
    public void ResidentSettings_AreOffByDefault_AndKeptFromTheFile(string json, bool login, bool keep)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, json);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(login, store.Current.StartAtLogin);
        Assert.Equal(keep, store.Current.KeepRunningOnClose);

        store.Update(s => s.KeepRunningOnClose = !keep);
        store.Flush();
        Assert.Contains($"\"keep_running_on_close\": {(!keep).ToString().ToLowerInvariant()}", File.ReadAllText(file));
    }
}
