using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Tests;

public class AddressInputTests
{
    private const string Shown = @"E:\仕事\2026";

    private static Func<string, PathKind> Disk(params string[] entries)
    {
        // "dir\" marks a folder, anything else a file.
        var map = entries.ToDictionary(e => e.TrimEnd('\\'), e => e.EndsWith('\\') ? PathKind.Folder : PathKind.File, StringComparer.OrdinalIgnoreCase);
        return path => map.TryGetValue(path.TrimEnd('\\'), out var kind) ? kind : PathKind.Missing;
    }

    private static readonly Func<string, PathKind> Sample = Disk(
        @"E:\", @"E:\仕事\", Shown + @"\", Shown + @"\資料\", Shown + @"\資料\図\", Shown + @"\cmd\", Shown + @"\code\", Shown + @"\read me.md", Shown + @"\setup files\", Shown + @"\bin\tool.exe",
        @"E:\Windows\", @"C:\Program Files\Tool\tool.exe");

    [Theory]
    [InlineData(@"E:\仕事", AddressAction.OpenFolder, @"E:\仕事")]
    [InlineData("  \"E:\\仕事\\\"  ", AddressAction.OpenFolder, @"E:\仕事")]   // pasted with quotes and a trailing separator
    [InlineData("E:", AddressAction.OpenFolder, @"E:\")]                       // the drive's root, not its current directory
    [InlineData("..", AddressAction.OpenFolder, @"E:\仕事")]
    [InlineData(@"資料\図", AddressAction.OpenFolder, Shown + @"\資料\図")]
    [InlineData(@".\資料", AddressAction.OpenFolder, Shown + @"\資料")]
    [InlineData(@"\Windows", AddressAction.OpenFolder, @"E:\Windows")]         // the shown folder's drive
    [InlineData("資料", AddressAction.OpenFolder, Shown + @"\資料")]            // a plain name of a subfolder
    [InlineData("read me.md", AddressAction.OpenFile, Shown + @"\read me.md")]
    [InlineData(@"E:\仕事\無い", AddressAction.NotFound, @"E:\仕事\無い")]
    [InlineData(@"..\無い", AddressAction.NotFound, @"E:\仕事\無い")]
    [InlineData(@"setup files\readme.txt", AddressAction.NotFound, Shown + @"\setup files\readme.txt")] // a mistyped path, not "setup" with arguments
    [InlineData(@"\\?\UNC\nas\share\x", AddressAction.NotFound, @"\\nas\share\x")]                    // the plain form of an extended-length path
    [InlineData("", AddressAction.None, null)]
    public void Resolve_PathsAndNames(string typed, AddressAction action, string? path)
    {
        var target = AddressInput.Resolve(typed, Shown, Sample);
        Assert.Equal(action, target.Action);
        Assert.Equal(path, target.Path);
    }

    [Theory]
    [InlineData("cmd", "cmd", "")]                          // a terminal's name wins over the subfolder "cmd"
    [InlineData("cmd /k dir", "cmd", "/k dir")]             // a separator in the arguments: still a command
    [InlineData("wsl -d Ubuntu", "wsl", "-d Ubuntu")]
    [InlineData("code .", "code", ".")]                     // not the subfolder "code" (" ." would be dropped from a name)
    [InlineData(@"code src\a.cs", "code", @"src\a.cs")]
    [InlineData(@"bin\tool.exe --version", Shown + @"\bin\tool.exe", "--version")] // a program below the folder
    [InlineData("shell:Downloads", "shell:Downloads", "")]  // left to the shell, never a relative path
    [InlineData(@"C:\Program Files\Tool\tool.exe --help", @"C:\Program Files\Tool\tool.exe", "--help")]
    [InlineData("\"C:\\Program Files\\Tool\\tool.exe\" -v x", @"C:\Program Files\Tool\tool.exe", "-v x")]
    public void Resolve_Commands(string typed, string program, string arguments)
    {
        var target = AddressInput.Resolve(typed, Shown, Sample);
        Assert.Equal(AddressAction.Run, target.Action);
        Assert.Equal(program, target.Program);
        Assert.Equal(arguments, target.Arguments);
    }

    [Fact]
    public void Resolve_WithoutShownFolder_NamesAreCommands()
    {
        // The top level of 最近の履歴: nothing is relative to it.
        Assert.Equal(AddressAction.Run, AddressInput.Resolve("資料", null, Sample).Action);
        Assert.Equal(AddressAction.OpenFolder, AddressInput.Resolve(@"E:\仕事", null, Sample).Action);
    }
}

public class TerminalCommandTests
{
    private const string System32 = @"C:\Windows\System32";

    [Theory]
    [InlineData("CMD.exe", TerminalKind.Cmd)]
    [InlineData("pwsh", TerminalKind.Pwsh)]
    [InlineData("wt", TerminalKind.WindowsTerminal)]
    [InlineData(@"C:\tools\cmd.exe", TerminalKind.None)]
    [InlineData("notepad", TerminalKind.None)]
    public void KindOf_BareNamesOnly(string program, TerminalKind kind) =>
        Assert.Equal(kind, TerminalCommand.KindOf(program));

    [Theory]
    [InlineData(@"E:\仕事\a b", "", @"--cd ""E:\仕事\a b""", @"E:\仕事\a b", false)]
    [InlineData(@"E:\", "", @"--cd E:\", @"E:\", false)]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\my dir", "", @"-d Ubuntu --cd ""/home/me/my dir""", System32, false)]
    [InlineData(@"\\wsl$\Debian", "-e ls", @"-d Debian --cd / -e ls", System32, false)]
    [InlineData(@"\\server\share\x", "", "--cd ~", System32, true)]
    public void Build_Wsl(string folder, string arguments, string expected, string workingDirectory, bool note)
    {
        var spec = TerminalCommand.Build("wsl", arguments, folder, System32);
        Assert.Equal(@"C:\Windows\System32\wsl.exe", spec.FileName);
        Assert.Equal(expected, spec.Arguments);
        Assert.Equal(workingDirectory, spec.WorkingDirectory);
        Assert.Equal(note, spec.Note is not null);
    }

    [Fact]
    public void Build_Cmd_MapsADriveForAUncFolder()
    {
        var unc = TerminalCommand.Build("cmd", "", @"\\server\share\a b", System32);
        Assert.Equal(@"/k pushd ""\\server\share\a b""", unc.Arguments);
        Assert.Equal(System32, unc.WorkingDirectory);
        Assert.Equal(TerminalCommand.CmdUncArgumentsRefusal, TerminalCommand.Build("cmd", "/c dir", @"\\?\UNC\server\share", System32).Refusal);

        // Always quoted for cmd (& ^ would be its own), and nothing that would run outside the folder.
        Assert.Equal(@"/k pushd ""\\nas\share\R&D""", TerminalCommand.Build("cmd", "", @"\\nas\share\R&D", System32).Arguments);
        Assert.Equal(TerminalCommand.CmdUncArgumentsRefusal, TerminalCommand.Build("cmd", "/c del *.log", @"\\nas\share\x", System32).Refusal);
        Assert.Equal(TerminalCommand.CmdUncPercentRefusal, TerminalCommand.Build("cmd", "", @"\\nas\share\100%", System32).Refusal);

        var local = TerminalCommand.Build("cmd.exe", "/k dir", @"E:\仕事", System32);
        Assert.Equal(("/k dir", @"E:\仕事"), (local.Arguments, local.WorkingDirectory));

        // wt and pwsh are not found by name in the folder (the shell looks in the working directory first).
        var wt = TerminalCommand.Build("wt", "", @"E:\a;b c", System32);
        Assert.Equal((@"-d ""E:\a\;b c""", System32), (wt.Arguments, wt.WorkingDirectory));
        var pwsh = TerminalCommand.Build("pwsh", "-NoLogo", @"E:\a b", System32, locate: _ => null);
        Assert.Equal(("pwsh.exe", @"-WorkingDirectory ""E:\a b"" -NoLogo", System32), (pwsh.FileName, pwsh.Arguments, pwsh.WorkingDirectory));
        var found = TerminalCommand.Build("pwsh", "", @"E:\a b", System32, locate: _ => @"C:\Program Files\PowerShell\7\pwsh.exe");
        Assert.Equal((@"C:\Program Files\PowerShell\7\pwsh.exe", @"E:\a b"), (found.FileName, found.WorkingDirectory));

        var other = TerminalCommand.Build("code", ".", @"E:\仕事", System32);
        Assert.Equal(("code", ".", @"E:\仕事"), (other.FileName, other.Arguments, other.WorkingDirectory));
    }

    [Theory]
    [InlineData(@"E:\plain", @"E:\plain")]
    [InlineData(@"C:\a b\", @"""C:\a b\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData(@"a\""b c", @"""a\\\""b c""")]
    [InlineData("", @"""""")]
    public void Quote_FollowsCommandLineToArgvRules(string argument, string expected) =>
        Assert.Equal(expected, TerminalCommand.Quote(argument));
}

public class OpenInCommandTests
{
    private const string System32 = @"C:\Windows\System32";
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    // An elevated process starts in System32 whatever the working directory: the folder is on the command line.
    [Theory]
    [InlineData(null, false, @"E:\it's a b", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"-NoExit -Command ""Set-Location -LiteralPath 'E:\it''s a b'""")]
    [InlineData(Pwsh, true, @"E:\it's a b", Pwsh, @"-NoExit -Command ""Set-Location -LiteralPath 'E:\it''s a b'""")]
    [InlineData(Pwsh, false, "E:\\a\u2019b$x", Pwsh, "-NoExit -Command \"Set-Location -LiteralPath 'E:\\a\u2019\u2019b$x'\"")]
    public void Build_PowerShell_PassesTheFolderOnTheCommandLine(string? pwsh, bool elevated, string folder, string exe, string arguments)
    {
        var spec = OpenInCommand.Build(OpenInTool.PowerShell, elevated, folder, OpenInPrograms.None with { Pwsh = pwsh }, System32);
        Assert.Equal((exe, arguments, System32), (spec!.FileName, spec.Arguments, spec.WorkingDirectory));
    }

    [Fact]
    public void Build_TerminalCodeWsl_TheProgramsFound_ElseCmdOrNothing()
    {
        var none = OpenInPrograms.None;
        Assert.Null(OpenInCommand.Build(OpenInTool.Code, false, @"E:\a", none, System32));
        Assert.Null(OpenInCommand.Build(OpenInTool.Wsl, false, @"E:\a", none, System32));

        var all = new OpenInPrograms(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", null, @"C:\VS Code\Code.exe", Wsl: true);
        var wt = OpenInCommand.Build(OpenInTool.Terminal, true, @"E:\a;b c\", all, System32)!;
        Assert.Equal((all.WindowsTerminal, @"-d ""E:\a\;b c\\""", System32), (wt.FileName, wt.Arguments, wt.WorkingDirectory));
        var code = OpenInCommand.Build(OpenInTool.Code, false, @"E:\仕事\a b", all, System32)!;
        Assert.Equal((all.Code, @"""E:\仕事\a b""", System32), (code.FileName, code.Arguments, code.WorkingDirectory));
        var wsl = OpenInCommand.Build(OpenInTool.Wsl, false, @"\\wsl.localhost\Ubuntu\home\me", all, System32)!;
        Assert.Equal((@"C:\Windows\System32\wsl.exe", "-d Ubuntu --cd /home/me"), (wsl.FileName, wsl.Arguments));

        // Without Windows Terminal: cmd, in the folder; elevated through pushd (it would start in System32).
        var cmd = OpenInCommand.Build(OpenInTool.Terminal, false, @"E:\a b", none, System32)!;
        Assert.Equal((@"C:\Windows\System32\cmd.exe", "", @"E:\a b"), (cmd.FileName, cmd.Arguments, cmd.WorkingDirectory));
        var admin = OpenInCommand.Build(OpenInTool.Terminal, true, @"\\nas\share\R&D", none, System32)!;
        Assert.Equal((@"C:\Windows\System32\cmd.exe", @"/k pushd ""\\nas\share\R&D""", System32), (admin.FileName, admin.Arguments, admin.WorkingDirectory));
        Assert.Equal(OpenInCommand.CmdPercentRefusal, OpenInCommand.Build(OpenInTool.Terminal, true, @"E:\100%", none, System32)!.Refusal);
    }
}

public class PathRebaseTests
{
    [Theory]
    [InlineData(@"E:\A", @"E:\A2")]
    [InlineData(@"E:\A\B\C", @"E:\A2\B\C")]
    [InlineData(@"E:\AB", null)]   // only a name that starts the same
    [InlineData(@"E:\X", null)]
    public void Rebase_SelfAndBelowOnly(string path, string? expected) =>
        Assert.Equal(expected, PathRebase.Rebase(path, @"E:\A", @"E:\A2"));

    [Fact]
    public void RebaseLocation_RootOrFolderBelowIt()
    {
        // The root itself (or above it) moved: the rest of the way stays.
        Assert.Equal((@"E:\P2\Root", @"a\b"), PathRebase.RebaseLocation(@"E:\P\Root", @"a\b", @"E:\P", @"E:\P2"));

        // A folder below the root was renamed: the root stays.
        Assert.Equal((@"E:\Root", @"a2\b"), PathRebase.RebaseLocation(@"E:\Root", @"a\b", @"E:\Root\a", @"E:\Root\a2"));

        // Moved out of the root: it becomes the root.
        Assert.Equal((@"F:\b", ""), PathRebase.RebaseLocation(@"E:\Root", @"a\b", @"E:\Root\a\b", @"F:\b"));

        // Elsewhere, or a top level (no root): not affected.
        Assert.Null(PathRebase.RebaseLocation(@"E:\Root", "c", @"E:\Root\a", @"E:\Root\a2"));
        Assert.Null(PathRebase.RebaseLocation("", "", @"E:\Root", @"E:\Root2"));
    }

    [Theory]
    [InlineData(@"E:\A\B\C", @"E:\A2\B\C", @"E:\A", @"E:\A2")]      // an ancestor renamed in place
    [InlineData(@"E:\P\C", @"E:\P\C2", @"E:\P\C", @"E:\P\C2")]      // the folder itself
    [InlineData(@"E:\X\C", @"E:\Y\Z\C", @"E:\X\C", @"E:\Y\Z\C")]    // a move: nothing wider
    [InlineData(@"E:\P\A\B", @"E:\Q\A\B", @"E:\P", @"E:\Q")]        // looks like a rename of P: the caller checks the disk
    public void WidestRename(string oldPath, string newPath, string wideOld, string wideNew) =>
        Assert.Equal((wideOld, wideNew), PathRebase.WidestRename(oldPath, newPath));
}

public class FolderIdentityTests
{
    [Fact]
    public void Locate_FollowsRenameAncestorRenameAndMove()
    {
        using var tmp = new TempDir();
        string folder = tmp.MakeDir("A", "B");
        string id = FolderIdentity.Capture(folder)!;
        Assert.StartsWith("v1:", id);
        Assert.Null(FolderIdentity.Capture(tmp.MakeFile("file.txt"))); // files have no identity here

        // Renamed in place: found among the old parent's folders.
        Directory.Move(folder, tmp.Combine("A", "B2"));
        Assert.Equal(tmp.Combine("A", "B2"), FolderIdentity.Locate(id, folder));

        // Its parent renamed too: the old parent is gone, found by its id.
        Directory.Move(tmp.Combine("A"), tmp.Combine("A2"));
        Assert.Equal(tmp.Combine("A2", "B2"), FolderIdentity.Locate(id, tmp.Combine("A", "B2")), ignoreCase: true);

        // Moved elsewhere on the volume.
        tmp.MakeDir("M");
        Directory.Move(tmp.Combine("A2", "B2"), tmp.Combine("M", "B3"));
        Assert.Equal(tmp.Combine("M", "B3"), FolderIdentity.Locate(id, tmp.Combine("A2", "B2")), ignoreCase: true);
    }

    [Fact]
    public void Locate_ANewFolderOfTheSameNameIsNotTheOldOne()
    {
        using var tmp = new TempDir();
        string folder = tmp.MakeDir("A");
        string id = FolderIdentity.Capture(folder)!;
        Directory.Delete(folder);
        tmp.MakeDir("A2");

        Assert.Null(FolderIdentity.Locate(id, folder));
        Assert.Null(FolderIdentity.Locate("v1:bogus", folder));

        // The 64-bit file index (WSL shares) is a kind of its own: never equal to a 128-bit id of the same numbers.
        Assert.True(FolderKey.TryParse("i1:0000000000000000:000000000000000000000000000adf4d:01dd7cbe4a099ecb", out var index));
        Assert.True(FolderKey.TryParse(index.ToString().Replace("i1:", "v1:"), out var extended));
        Assert.False(index.Extended);
        Assert.NotEqual(index, extended);
        Assert.Equal("i1:0000000000000000:000000000000000000000000000adf4d:01dd7cbe4a099ecb", index.ToString());
        Assert.False(FolderIdentity.IsUsable(@"C:\$Recycle.Bin\S-1-5-21\$R0AB12C"));
        Assert.True(FolderIdentity.IsUsable(@"C:\Users\me\Recycle"));
    }
}
