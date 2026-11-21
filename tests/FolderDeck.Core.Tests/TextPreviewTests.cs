using System.Text;
using FolderDeck.Core.Listing;

namespace FolderDeck.Core.Tests;

public sealed class TextPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fd-text-" + Guid.NewGuid().ToString("N"));

    public TextPreviewTests()
    {
        Directory.CreateDirectory(_dir);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Encoding ShiftJis => Encoding.GetEncoding(932);

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

    [Fact]
    public void Utf8_with_bom()
    {
        var excerpt = TextPreview.Read(Write("bom.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo\r\nworld\n")]));

        Assert.NotNull(excerpt);
        Assert.Equal(["héllo", "world"], excerpt.Lines);
        Assert.Equal("UTF-8", excerpt.EncodingName);
        Assert.False(excerpt.Truncated);
    }

    [Fact]
    public void Utf8_japanese_without_bom()
    {
        var excerpt = TextPreview.Read(Write("ja.md", Encoding.UTF8.GetBytes("# 日本語のメモ\n\n本文です。\r最後")));

        Assert.NotNull(excerpt);
        Assert.Equal(["# 日本語のメモ", string.Empty, "本文です。", "最後"], excerpt.Lines);
        Assert.Equal("UTF-8", excerpt.EncodingName);
    }

    [Fact]
    public void Utf16_with_bom_little_and_big_endian()
    {
        var le = TextPreview.Read(Write("le.reg", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("テスト\r\nline2")]));
        Assert.NotNull(le);
        Assert.Equal(["テスト", "line2"], le.Lines);
        Assert.Equal("UTF-16 LE", le.EncodingName);

        var be = TextPreview.Read(Write("be.txt", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("ビッグ\nエンディアン")]));
        Assert.NotNull(be);
        Assert.Equal(["ビッグ", "エンディアン"], be.Lines);
        Assert.Equal("UTF-16 BE", be.EncodingName);
    }

    [Fact]
    public void Shift_jis_japanese()
    {
        var excerpt = TextPreview.Read(Write("sjis.txt", ShiftJis.GetBytes("日本語テキスト\r\n二行目：ｶﾀｶﾅ")));

        Assert.NotNull(excerpt);
        Assert.Equal(["日本語テキスト", "二行目：ｶﾀｶﾅ"], excerpt.Lines);
        Assert.Equal("Shift_JIS", excerpt.EncodingName);
    }

    [Fact]
    public void Binary_content_gives_null()
    {
        Assert.Null(TextPreview.Read(Write("nul.log", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x41, 0x42])));

        byte[] controls = [.. Enumerable.Range(0, 200).Select(i => (byte)(i % 2 == 0 ? 0x01 : 0x41))];
        Assert.Null(TextPreview.Read(Write("controls.txt", controls)));

        Assert.Null(TextPreview.Read(Write("utf16nul.txt", [0xFF, 0xFE, 0x41, 0x00, 0x00, 0x00, 0x42, 0x00])));
    }

    [Fact]
    public void Escape_sequences_and_a_few_controls_are_still_text()
    {
        var excerpt = TextPreview.Read(Write("color.log", Encoding.UTF8.GetBytes("\u001b[32mOK\u001b[0m done\fnext\n")));

        Assert.NotNull(excerpt);
        Assert.Equal(["[32mOK[0m donenext"], excerpt.Lines);
    }

    [Fact]
    public void Tabs_expand_and_long_lines_are_cut()
    {
        string longLine = new('x', 300);
        var excerpt = TextPreview.Read(Write("code.cs", Encoding.UTF8.GetBytes("a\tb\n\tx\nabcd\te\n" + longLine)));

        Assert.NotNull(excerpt);
        Assert.Equal("a   b", excerpt.Lines[0]);
        Assert.Equal("    x", excerpt.Lines[1]);
        Assert.Equal("abcd    e", excerpt.Lines[2]);
        Assert.Equal(new string('x', TextPreview.MaxLineChars) + "…", excerpt.Lines[3]);
    }

    [Fact]
    public void Line_limit()
    {
        string text = string.Join("\n", Enumerable.Range(1, 100).Select(i => "line " + i));

        var excerpt = TextPreview.Read(Write("many.txt", Encoding.UTF8.GetBytes(text)), maxLines: 10);

        Assert.NotNull(excerpt);
        Assert.True(excerpt.Truncated);
        Assert.Equal(10, excerpt.Lines.Count);
        Assert.Equal("line 10", excerpt.Lines[^1]);

        var exact = TextPreview.Read(Write("ten.txt", Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(1, 10)) + "\n")), maxLines: 10);
        Assert.NotNull(exact);
        Assert.False(exact.Truncated);
        Assert.Equal(10, exact.Lines.Count);
    }

    [Fact]
    public void Byte_limit_never_splits_a_character()
    {
        // 10 ASCII bytes + あ (3 bytes each): 17 bytes end one byte into the third あ.
        var utf8 = TextPreview.Read(Write("cut.txt", Encoding.UTF8.GetBytes("abcdefghij" + "あいうえお")), maxBytes: 17);
        Assert.NotNull(utf8);
        Assert.True(utf8.Truncated);
        Assert.Equal("UTF-8", utf8.EncodingName);
        Assert.Equal(["abcdefghijあい"], utf8.Lines);

        // Shift_JIS: 4 + 11 bytes, then the lead byte of あ at the cut.
        var sjis = TextPreview.Read(Write("cut-sjis.txt", ShiftJis.GetBytes("日本abcdefghijkあいう")), maxBytes: 16);
        Assert.NotNull(sjis);
        Assert.True(sjis.Truncated);
        Assert.Equal("Shift_JIS", sjis.EncodingName);
        Assert.Equal(["日本abcdefghijk"], sjis.Lines);

        // UTF-16: a surrogate pair cut in half is dropped.
        byte[] emoji = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(new string('a', 6) + "😀b")];
        var utf16 = TextPreview.Read(Write("cut-16.txt", emoji), maxBytes: 16);
        Assert.NotNull(utf16);
        Assert.Equal(["aaaaaa"], utf16.Lines);
        Assert.DoesNotContain('�', utf16.Lines[0]);
    }

    [Fact]
    public void Empty_missing_and_shared_files()
    {
        var empty = TextPreview.Read(Write("empty.txt", []));
        Assert.NotNull(empty);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.Truncated);

        Assert.Null(TextPreview.Read(Path.Combine(_dir, "missing.txt")));

        string log = Write("app.log", Encoding.UTF8.GetBytes("first\nsecond\n"));
        using var writer = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal(["first", "second"], TextPreview.Read(log)!.Lines);
    }

    [Fact]
    public void Cloud_only_files_are_not_read()
    {
        string path = Write("cloud.txt", Encoding.UTF8.GetBytes("only in the cloud\n"));
        File.SetAttributes(path, FileAttributes.Archive | FileAttributes.Offline);
        try
        {
            Assert.Null(TextPreview.Read(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(["only in the cloud"], TextPreview.Read(path)!.Lines);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
