using System.IO.Compression;
using System.Text;
using FolderDeck.Core.Listing;

namespace FolderDeck.Core.Tests;

public sealed class OfficeTextTests : IDisposable
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocumentType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string RelTypeBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fd-office-" + Guid.NewGuid().ToString("N"));

    public OfficeTextTests() => Directory.CreateDirectory(_dir);

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

    // ---- Word ---------------------------------------------------------------------------------

    [Fact]
    public void Word_paragraphs_keep_tabs_and_breaks_and_flag_headings()
    {
        string path = MakeDocx(
            P("Report", style: "Title") +
            P("Introduction", style: "Heading1") +
            "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t><w:cr/><w:t>d</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>tab stops are not tabs</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr><w:r><w:t>Outlined</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:outlineLvl w:val=\"9\"/></w:pPr><w:r><w:t xml:space=\"preserve\">Body </w:t></w:r><w:r><w:t>text</w:t></w:r></w:p>" +
            P("見出しです", style: "見出し1"));

        var excerpt = OfficeText.ReadWord(path);

        Assert.NotNull(excerpt);
        Assert.False(excerpt.Truncated);
        Assert.Equal(["Report", "Introduction", "a\tb\nc\nd", "tab stops are not tabs", "Outlined", "Body text", "見出しです"], excerpt.Paragraphs.Select(p => p.Text));
        Assert.Equal([true, true, false, false, true, false, true], excerpt.Paragraphs.Select(p => p.IsHeading));
    }

    [Fact]
    public void Word_leaves_out_deleted_text_field_codes_text_boxes_and_ruby()
    {
        string path = MakeDocx(
            "<w:p><w:r><w:t xml:space=\"preserve\">Keep </w:t></w:r><w:del w:id=\"1\"><w:r><w:delText>gone</w:delText></w:r></w:del>" +
            "<w:ins w:id=\"2\"><w:r><w:t>new</w:t></w:r></w:ins></w:p>" +
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>7</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:t>HIDDEN</w:t></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "<w:r><w:t>shown</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
            "<w:p><w:r><w:t>Before</w:t></w:r><w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>BOX</w:t></w:r></w:p>" +
            "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>" +
            "<w:p><w:r><w:ruby><w:rt><w:r><w:t>かんじ</w:t></w:r></w:rt><w:rubyBase><w:r><w:t>漢字</w:t></w:r></w:rubyBase></w:ruby></w:r></w:p>" +
            "<w:p><w:pPr><w:pStyle w:val=\"Heading2\"/><w:pPrChange w:id=\"3\"><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr></w:pPrChange>" +
            "<w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>Changed</w:t></w:r></w:p>");

        var excerpt = OfficeText.ReadWord(path);

        Assert.NotNull(excerpt);
        Assert.Equal(["Keep new", "7", "shown", "Before", "漢字", "Changed"], excerpt.Paragraphs.Select(p => p.Text));
        Assert.True(excerpt.Paragraphs[^1].IsHeading);
    }

    [Fact]
    public void Word_blank_paragraphs_collapse_to_one_and_never_lead_or_trail()
    {
        string path = MakeDocx("<w:p/><w:p><w:r><w:t> </w:t></w:r></w:p>" + P("A") + "<w:p/><w:p/><w:p><w:r><w:br/></w:r></w:p>" + P("B") + "<w:p/>");

        var excerpt = OfficeText.ReadWord(path);

        Assert.NotNull(excerpt);
        Assert.Equal(["A", string.Empty, "B"], excerpt.Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void Word_stops_at_the_paragraph_limit()
    {
        string path = MakeDocx(string.Concat(Enumerable.Range(1, 50).Select(i => P("line " + i))));

        var excerpt = OfficeText.ReadWord(path, maxParagraphs: 5);

        Assert.NotNull(excerpt);
        Assert.True(excerpt.Truncated);
        Assert.Equal(["line 1", "line 2", "line 3", "line 4", "line 5"], excerpt.Paragraphs.Select(p => p.Text));

        var exact = OfficeText.ReadWord(MakeDocx(P("1") + P("2") + P("3"), name: "exact.docx"), maxParagraphs: 3);
        Assert.NotNull(exact);
        Assert.False(exact.Truncated);
        Assert.Equal(3, exact.Paragraphs.Count);
    }

    [Fact]
    public void Word_stops_at_the_character_limit()
    {
        string path = MakeDocx(P(new string('x', 30)) + P("yyyyyy") + P("never"));

        var excerpt = OfficeText.ReadWord(path, maxChars: 33);

        Assert.NotNull(excerpt);
        Assert.True(excerpt.Truncated);
        Assert.Equal([new string('x', 30), "yyy"], excerpt.Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void Word_heading_style_ids_are_resolved_through_styles_xml()
    {
        // Japanese Word names the heading styles "1", "2"…; their names stay "heading 1"…
        string styles =
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"a\"><w:name w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"a\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"10\"><w:name w:val=\"My chapter\"/><w:basedOn w:val=\"1\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Big\"><w:name w:val=\"Big\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Note\"><w:name w:val=\"heading 9\"/></w:style>";
        string path = MakeDocx(P("章", style: "1") + P("節", style: "10") + P("大", style: "Big") + P("本文", style: "a") + P("注", style: "Note"), styles: styles);

        var excerpt = OfficeText.ReadWord(path);

        Assert.NotNull(excerpt);
        Assert.Equal([true, true, true, false, false], excerpt.Paragraphs.Select(p => p.IsHeading));
    }

    [Fact]
    public void Word_main_part_comes_from_the_package_relationships()
    {
        string path = MakePackage(
            "renamed.docx",
            ("_rels/.rels", Rels(("rId1", OfficeDocumentType, "/word/main.xml"))),
            ("word/main.xml", Document(P("from main"))),
            ("word/document.xml", Document(P("not this"))));
        Assert.Equal(["from main"], OfficeText.ReadWord(path)!.Paragraphs.Select(p => p.Text));

        string noRels = MakePackage("norels.docx", ("word/document.xml", Document(P("fallback"))));
        Assert.Equal(["fallback"], OfficeText.ReadWord(noRels)!.Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void Word_empty_body_gives_an_empty_excerpt()
    {
        var excerpt = OfficeText.ReadWord(MakeDocx("<w:p/><w:sectPr><w:pgSz w:w=\"11906\"/></w:sectPr>"));
        Assert.NotNull(excerpt);
        Assert.True(excerpt.IsEmpty);
        Assert.False(excerpt.Truncated);
    }

    [Fact]
    public void Word_broken_missing_or_locked_files()
    {
        string broken = Path.Combine(_dir, "broken.docx");
        File.WriteAllText(broken, "not a zip");
        Assert.Null(OfficeText.ReadWord(broken));
        Assert.Null(OfficeText.ReadWord(Path.Combine(_dir, "missing.docx")));
        Assert.Null(OfficeText.ReadWord(MakePackage("nodoc.docx", ("docProps/app.xml", Encoding.UTF8.GetBytes("<a/>")))));

        string badXml = MakePackage("badxml.docx", ("word/document.xml", Encoding.UTF8.GetBytes("<w:document xmlns:w=\"" + WordNs + "\"><w:body><w:p>")));
        Assert.Null(OfficeText.ReadWord(badXml));

        string open = MakeDocx(P("still readable"), name: "open.docx");
        using var held = new FileStream(open, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write);
        Assert.Equal(["still readable"], OfficeText.ReadWord(open)!.Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void Word_cancellation_gives_null()
    {
        string path = MakeDocx(P("text"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(OfficeText.ReadWord(path, cancellationToken: cts.Token));
    }

    // ---- Excel --------------------------------------------------------------------------------

    [Fact]
    public void Sheet_shows_shared_and_inline_strings_numbers_booleans_and_errors()
    {
        string sst =
            "<sst xmlns=\"" + SheetNs + "\" count=\"4\" uniqueCount=\"4\">" +
            "<si><t>Name</t></si>" +
            "<si><r><t>Ri</t></r><r><rPr><b/></rPr><t>ch</t></r></si>" +
            "<si><t>漢字</t><rPh sb=\"0\" eb=\"2\"><t>カンジ</t></rPh><phoneticPr fontId=\"1\"/></si>" +
            "<si><t>line_x000D_" + "\n" + "break</t></si>" +
            "</sst>";
        string data =
            Row(1, C("A1", "0", "s"), C("B1", "1", "s"), C("C1", "2", "s"), C("D1", "3", "s")) +
            Row(2, C("A2", "42"), C("B2", "0.30000000000000004"), C("C2", "-1.5E-3"), C("D2", "1E+15"), C("E2", "1.5E+25")) +
            Row(3, C("A3", "1", "b"), C("B3", "0", "b"), C("C3", "#DIV/0!", "e"),
                "<c r=\"D3\" t=\"inlineStr\"><is><t>inline</t></is></c>",
                "<c r=\"E3\" t=\"str\"><f>CONCAT(\"a\",\"b\")</f><v>ab</v></c>");
        string path = MakeXlsx("values.xlsx", [("Data", null, data)], sharedStrings: sst);

        var sheet = OfficeText.ReadSheet(path);

        Assert.NotNull(sheet);
        Assert.Equal("Data", sheet.SheetName);
        Assert.Equal(1, sheet.SheetCount);
        Assert.False(sheet.Truncated);
        Assert.Equal(3, sheet.Rows.Count);
        Assert.Equal(5, sheet.ColumnCount);
        Assert.Equal(["Name", "Rich", "漢字", "line break", string.Empty], Enumerable.Range(0, 5).Select(c => sheet.TextAt(0, c)));
        Assert.Equal(["42", "0.3", "-0.0015", "1000000000000000", "1.5E+25"], Enumerable.Range(0, 5).Select(c => sheet.TextAt(1, c)));
        Assert.Equal(["TRUE", "FALSE", "#DIV/0!", "inline", "ab"], Enumerable.Range(0, 5).Select(c => sheet.TextAt(2, c)));
        Assert.True(sheet.Rows[1][0].IsNumber);
        Assert.False(sheet.Rows[0][0].IsNumber);
    }

    [Fact]
    public void Sheet_formats_built_in_and_custom_dates()
    {
        string styles =
            "<styleSheet xmlns=\"" + SheetNs + "\">" +
            "<numFmts count=\"7\">" +
            "<numFmt numFmtId=\"164\" formatCode=\"yyyy&quot;年&quot;m&quot;月&quot;d&quot;日&quot;\"/>" +
            "<numFmt numFmtId=\"165\" formatCode=\"0.00&quot;d&quot;\"/>" +
            "<numFmt numFmtId=\"166\" formatCode=\"[Red]#,##0;[Blue]\\-#,##0\"/>" +
            "<numFmt numFmtId=\"167\" formatCode=\"h:mm:ss\"/>" +
            "<numFmt numFmtId=\"168\" formatCode=\"[h]:mm\"/>" +
            "<numFmt numFmtId=\"169\" formatCode=\"[$-411]yyyy/mm/dd\\ hh:mm\"/>" +
            "<numFmt numFmtId=\"170\" formatCode=\"0.0E+00\"/>" +
            "</numFmts>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"22\"/></cellStyleXfs>" +
            "<cellXfs count=\"11\">" +
            "<xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"22\"/><xf numFmtId=\"164\"/><xf numFmtId=\"165\"/>" +
            "<xf numFmtId=\"166\"/><xf numFmtId=\"167\"/><xf numFmtId=\"168\"/><xf numFmtId=\"169\"><alignment horizontal=\"left\"/></xf>" +
            "<xf numFmtId=\"20\"/><xf numFmtId=\"170\"/>" +
            "</cellXfs>" +
            "<dxfs count=\"1\"><dxf><numFmt numFmtId=\"14\" formatCode=\"0.00\"/></dxf></dxfs>" +
            "</styleSheet>";
        string data =
            Row(1, C("A1", "45000", style: 1), C("B1", "45000.5", style: 2), C("C1", "45000", style: 3), C("D1", "2.5", style: 4),
                C("E1", "1234", style: 5), C("F1", "0.75", style: 6), C("G1", "1.5", style: 7), C("H1", "45000.25", style: 8),
                C("I1", "0.54166666666666663", style: 9), C("J1", "12345", style: 10), C("K1", "45000", style: 0)) +
            Row(2, C("A2", "1", style: 1), C("B2", "59", style: 1), C("C2", "61", style: 1), C("D2", "-5", style: 1));
        string path = MakeXlsx("dates.xlsx", [("Dates", null, data)], styles: styles);

        var sheet = OfficeText.ReadSheet(path);

        Assert.NotNull(sheet);
        Assert.Equal(
            ["2023/03/15", "2023/03/15 12:00", "2023/03/15", "2.5", "1234", "18:00:00", "36:00", "2023/03/15 06:00", "13:00", "12345", "45000"],
            Enumerable.Range(0, 11).Select(c => sheet.TextAt(0, c)));
        Assert.Equal(["1900/01/01", "1900/02/28", "1900/03/01", "-5"], Enumerable.Range(0, 4).Select(c => sheet.TextAt(1, c)));
        Assert.True(sheet.Rows[0][0].IsNumber);
    }

    [Fact]
    public void Sheet_honours_the_1904_date_system()
    {
        string styles = "<styleSheet xmlns=\"" + SheetNs + "\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>";
        string data = Row(1, C("A1", "0", style: 1), C("B1", "43462", style: 1));
        string path = MakeXlsx("mac.xlsx", [("Mac", null, data)], styles: styles, date1904: true);

        var sheet = OfficeText.ReadSheet(path);

        Assert.NotNull(sheet);
        Assert.Equal(["1904/01/01", "2022/12/29"], Enumerable.Range(0, 2).Select(c => sheet.TextAt(0, c)));
    }

    [Fact]
    public void Sheet_places_sparse_cells_and_trims_trailing_blanks()
    {
        string data =
            "<row r=\"2\"><c r=\"B2\"><v>x</v></c><c r=\"E2\" s=\"3\"/></row>" +
            "<row r=\"3\"><c><v>1</v></c><c><v>2</v></c></row>" +
            "<row><c r=\"C4\" t=\"inlineStr\"><is><t></t></is></c></row>" +
            "<row r=\"5\" spans=\"1:9\" ht=\"20\" customHeight=\"1\"/>";
        string path = MakeXlsx("sparse.xlsx", [("S", null, data)]);

        var sheet = OfficeText.ReadSheet(path);

        Assert.NotNull(sheet);
        Assert.Equal(3, sheet.Rows.Count); // row 4 holds only an empty string
        Assert.Equal(2, sheet.ColumnCount);
        Assert.Equal(string.Empty, sheet.TextAt(0, 0));
        Assert.Equal(string.Empty, sheet.TextAt(1, 0));
        Assert.Equal("x", sheet.TextAt(1, 1));
        Assert.Equal(["1", "2"], Enumerable.Range(0, 2).Select(c => sheet.TextAt(2, c)));
        Assert.All(sheet.Rows, row => Assert.Equal(2, row.Count));
        Assert.False(sheet.Truncated);
    }

    [Fact]
    public void Sheet_stops_at_the_row_and_column_limits()
    {
        var rows = new StringBuilder();
        for (int r = 1; r <= 40; r++)
        {
            rows.Append(Row(r, Enumerable.Range(0, 15).Select(c => C(SheetExcerpt.ColumnName(c) + r, (r * 100 + c).ToString())).ToArray()));
        }

        var sheet = OfficeText.ReadSheet(MakeXlsx("big.xlsx", [("Big", null, rows.ToString())]), maxRows: 30, maxCols: 12);

        Assert.NotNull(sheet);
        Assert.True(sheet.Truncated);
        Assert.Equal(30, sheet.Rows.Count);
        Assert.Equal(12, sheet.ColumnCount);
        Assert.Equal("3011", sheet.TextAt(29, 11));

        // Only a column past the limit has content.
        string wide = Row(1, C("A1", "1"), C("M1", "far"));
        var cut = OfficeText.ReadSheet(MakeXlsx("wide.xlsx", [("W", null, wide)]), maxRows: 30, maxCols: 12);
        Assert.NotNull(cut);
        Assert.True(cut.Truncated);
        Assert.Equal(1, cut.ColumnCount);

        // Formatting-only rows after the data do not count as more content.
        string exact = Row(1, C("A1", "1"), C("B1", "2")) + Row(2, C("A2", "3")) + "<row r=\"3\" s=\"1\" customFormat=\"1\"/><row r=\"9\" ht=\"30\"/>";
        var fits = OfficeText.ReadSheet(MakeXlsx("exact.xlsx", [("E", null, exact)]), maxRows: 2, maxCols: 2);
        Assert.NotNull(fits);
        Assert.False(fits.Truncated);
        Assert.Equal(2, fits.Rows.Count);
    }

    [Fact]
    public void Sheet_rows_past_the_limit_count_only_when_a_cell_has_a_value()
    {
        // Styled cells without a value (borders, fills) past the limit are not more content…
        string data = Row(1, C("A1", "1"), C("B1", "2")) + Row(2, C("A2", "3"));
        string styled = data + "<row r=\"3\"><c r=\"A3\" s=\"1\"/><c r=\"B3\" s=\"1\"/><c r=\"C3\" t=\"inlineStr\"><is><t></t></is></c></row>"
            + "<row r=\"4\" s=\"1\" customFormat=\"1\"><c r=\"A4\" s=\"2\"/></row>";
        var fits = OfficeText.ReadSheet(MakeXlsx("styled.xlsx", [("S", null, styled)]), maxRows: 2, maxCols: 2);
        Assert.NotNull(fits);
        Assert.False(fits.Truncated);
        Assert.Equal(2, fits.Rows.Count);
        Assert.Equal("3", fits.TextAt(1, 0));

        // …but a value after them is.
        string more = styled + Row(5, C("B5", "late"));
        var cut = OfficeText.ReadSheet(MakeXlsx("more.xlsx", [("M", null, more)]), maxRows: 2, maxCols: 2);
        Assert.NotNull(cut);
        Assert.True(cut.Truncated);
        Assert.Equal(2, cut.Rows.Count);
    }

    [Fact]
    public void Sheet_row_numbers_at_the_int_limit_do_not_wrap()
    {
        // The row after r="2147483647" has no number of its own: it must not become row -2147483648.
        string data = Row(1, C("A1", "a", "str")) + "<row r=\"2147483647\"/><row><c><v>1</v></c></row>";
        var sheet = OfficeText.ReadSheet(MakeXlsx("overflow.xlsx", [("O", null, data)]));

        Assert.NotNull(sheet);
        Assert.Single(sheet.Rows);
        Assert.Equal("a", sheet.TextAt(0, 0));
        Assert.True(sheet.Truncated);

        // Within a row, a cell without a reference after the last column counts on from it.
        string wide = Row(1, C("A1", "a", "str"), "<c r=\"XFD1\"><v>far</v></c><c><v>further</v></c>");
        var cut = OfficeText.ReadSheet(MakeXlsx("wide-overflow.xlsx", [("W", null, wide)]));
        Assert.NotNull(cut);
        Assert.Equal("a", cut.TextAt(0, 0));
        Assert.True(cut.Truncated);
    }

    [Fact]
    public void Cloud_only_documents_and_workbooks_are_not_opened()
    {
        string docx = MakeDocx(P("Hello"));
        string xlsx = MakeXlsx("cloud.xlsx", [("C", null, Row(1, C("A1", "1")))]);
        foreach (string path in new[] { docx, xlsx })
        {
            File.SetAttributes(path, FileAttributes.Archive | FileAttributes.Offline);
        }

        try
        {
            Assert.Null(OfficeText.ReadWord(docx));
            Assert.Null(OfficeText.ReadSheet(xlsx));
        }
        finally
        {
            File.SetAttributes(docx, FileAttributes.Normal);
            File.SetAttributes(xlsx, FileAttributes.Normal);
        }

        Assert.Equal("Hello", OfficeText.ReadWord(docx)?.Paragraphs.Single().Text);
        Assert.Equal("1", OfficeText.ReadSheet(xlsx)?.TextAt(0, 0));
    }

    [Fact]
    public void Sheet_without_shared_strings_or_styles_still_shows_the_rest()
    {
        string data = Row(1, C("A1", "0", "s"), C("B1", "5", style: 3));
        var sheet = OfficeText.ReadSheet(MakeXlsx("bare.xlsx", [("Bare", null, data)]));

        Assert.NotNull(sheet);
        Assert.Equal(string.Empty, sheet.TextAt(0, 0));
        Assert.Equal("5", sheet.TextAt(0, 1));
    }

    [Fact]
    public void Sheet_is_the_first_visible_worksheet_in_workbook_order()
    {
        // The workbook lists "Zeta" (sheet2.xml) first; part numbers do not decide the order.
        string path = MakeXlsx(
            "order.xlsx",
            [("Hidden", "hidden", Row(1, C("A1", "h", "inlineStr"))), ("Zeta", null, Row(1, InlineCell("A1", "Z"))), ("Alpha", null, Row(1, InlineCell("A1", "A")))]);

        var sheet = OfficeText.ReadSheet(path);

        Assert.NotNull(sheet);
        Assert.Equal("Zeta", sheet.SheetName);
        Assert.Equal(3, sheet.SheetCount);
        Assert.Equal("Z", sheet.TextAt(0, 0));
    }

    [Fact]
    public void Sheet_broken_missing_empty_and_oversized_files()
    {
        string broken = Path.Combine(_dir, "broken.xlsx");
        File.WriteAllText(broken, "PK not really");
        Assert.Null(OfficeText.ReadSheet(broken));
        Assert.Null(OfficeText.ReadSheet(Path.Combine(_dir, "missing.xlsx")));
        Assert.Null(OfficeText.ReadSheet(MakePackage("nowb.xlsx", ("docProps/app.xml", Encoding.UTF8.GetBytes("<a/>")))));

        var empty = OfficeText.ReadSheet(MakeXlsx("empty.xlsx", [("Empty", null, string.Empty)]));
        Assert.NotNull(empty);
        Assert.True(empty.IsEmpty);
        Assert.Equal("Empty", empty.SheetName);

        // A part that expands past the budget (a zip bomb) is not read to the end.
        string bomb = Path.Combine(_dir, "bomb.xlsx");
        using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "_rels/.rels", Rels(("rId1", OfficeDocumentType, "xl/workbook.xml")));
            WriteEntry(zip, "xl/workbook.xml", Workbook([("Bomb", null)], date1904: false));
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", Rels(("rId1", RelTypeBase + "worksheet", "worksheets/sheet1.xml")));
            using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
            stream.Write(Encoding.UTF8.GetBytes("<worksheet xmlns=\"" + SheetNs + "\">"));
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)' ');
            for (int i = 0; i < 34; i++)
            {
                stream.Write(spaces);
            }

            stream.Write(Encoding.UTF8.GetBytes("<sheetData>" + Row(1, C("A1", "1")) + "</sheetData></worksheet>"));
        }

        Assert.Null(OfficeText.ReadSheet(bomb));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Column_names_are_letters(int index, string expected) => Assert.Equal(expected, SheetExcerpt.ColumnName(index));

    [Theory]
    [InlineData("A1", 1)]
    [InlineData("c7", 3)]
    [InlineData("AA10", 27)]
    [InlineData("XFD1048576", 16384)]
    [InlineData("12", null)]
    [InlineData("", null)]
    public void Cell_references_give_their_column(string reference, int? expected) => Assert.Equal(expected, SheetReader.ColumnOf(reference));

    [Theory]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(100.0, "100")]
    [InlineData(-2.50, "-2.5")]
    [InlineData(1e-7, "0.0000001")]
    [InlineData(123456789012.5, "123456789012.5")]
    [InlineData(1.5e25, "1.5E+25")]
    [InlineData(1.0 / 3, "0.333333333333333")]
    public void General_numbers_are_plain(double value, string expected) => Assert.Equal(expected, SheetFormat.General(value));

    [Theory]
    [InlineData("yyyy/mm/dd", (int)DateParts.Date)]
    [InlineData("m/d/yy h:mm", (int)(DateParts.Date | DateParts.Time))]
    [InlineData("h:mm:ss AM/PM", (int)(DateParts.Time | DateParts.Seconds))]
    [InlineData("[h]:mm:ss", (int)(DateParts.Time | DateParts.Seconds | DateParts.Elapsed))]
    [InlineData("[$-411]ggge\"年\"m\"月\"d\"日\"", (int)DateParts.Date)]
    [InlineData("mmm", (int)DateParts.None)]
    [InlineData("0.00\"days\"", (int)DateParts.None)]
    [InlineData("#,##0\\ \"h\"", (int)DateParts.None)]
    [InlineData("[Red]0.00;[Blue]-0.00", (int)DateParts.None)]
    [InlineData("0.00E+00", (int)DateParts.None)]
    [InlineData("General", (int)DateParts.None)]
    [InlineData("@", (int)DateParts.None)]
    [InlineData("_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)", (int)DateParts.None)]
    public void Date_tokens_of_custom_formats(string code, int expected) => Assert.Equal((DateParts)expected, SheetFormat.DatePartsOf(code));

    // ---- helpers ------------------------------------------------------------------------------

    private static string P(string text, string? style = null) =>
        "<w:p>" + (style is null ? string.Empty : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>") +
        $"<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static byte[] Document(string body) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        $"<w:document xmlns:w=\"{WordNs}\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>{body}</w:body></w:document>");

    private string MakeDocx(string body, string? styles = null, string name = "doc.docx")
    {
        var parts = new List<(string, byte[])>
        {
            ("_rels/.rels", Rels(("rId1", OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", Document(body)),
        };
        if (styles is not null)
        {
            parts.Add(("word/_rels/document.xml.rels", Rels(("rId1", RelTypeBase + "styles", "styles.xml"))));
            parts.Add(("word/styles.xml", Encoding.UTF8.GetBytes($"<w:styles xmlns:w=\"{WordNs}\">{styles}</w:styles>")));
        }

        return MakePackage(name, [.. parts]);
    }

    private static string C(string reference, string value, string? type = null, int style = 0) =>
        $"<c r=\"{reference}\"" + (type is null ? string.Empty : $" t=\"{type}\"") + (style == 0 ? string.Empty : $" s=\"{style}\"") + $"><v>{value}</v></c>";

    private static string InlineCell(string reference, string text) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string Row(int r, params string[] cells) => $"<row r=\"{r}\">" + string.Concat(cells) + "</row>";

    private static byte[] Workbook(IReadOnlyList<(string Name, string? State)> sheets, bool date1904)
    {
        var xml = new StringBuilder($"<workbook xmlns=\"{SheetNs}\" xmlns:r=\"{RelNs}\">");
        xml.Append(date1904 ? "<workbookPr date1904=\"1\"/>" : "<workbookPr defaultThemeVersion=\"166925\"/>");
        xml.Append("<sheets>");
        for (int i = 0; i < sheets.Count; i++)
        {
            string state = sheets[i].State is { } s ? $" state=\"{s}\"" : string.Empty;
            xml.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{i + 1}\"{state} r:id=\"rId{i + 1}\"/>");
        }

        xml.Append("</sheets><calcPr calcId=\"191029\"/></workbook>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    /// <summary>A workbook whose sheet i (workbook order) is stored as worksheets/sheet{n - i}.xml.</summary>
    private string MakeXlsx(string name, IReadOnlyList<(string Name, string? State, string Data)> sheets, string? sharedStrings = null, string? styles = null, bool date1904 = false)
    {
        var parts = new List<(string, byte[])>
        {
            ("_rels/.rels", Rels(("rId1", OfficeDocumentType, "xl/workbook.xml"))),
            ("xl/workbook.xml", Workbook([.. sheets.Select(s => (s.Name, s.State))], date1904)),
        };

        var rels = new List<(string, string, string)>();
        for (int i = 0; i < sheets.Count; i++)
        {
            string file = $"worksheets/sheet{sheets.Count - i}.xml";
            rels.Add(($"rId{i + 1}", RelTypeBase + "worksheet", file));
            parts.Add(("xl/" + file, Encoding.UTF8.GetBytes($"<worksheet xmlns=\"{SheetNs}\"><sheetData>{sheets[i].Data}</sheetData></worksheet>")));
        }

        if (sharedStrings is not null)
        {
            rels.Add(("rIdS", RelTypeBase + "sharedStrings", "sharedStrings.xml"));
            parts.Add(("xl/sharedStrings.xml", Encoding.UTF8.GetBytes(sharedStrings)));
        }

        if (styles is not null)
        {
            rels.Add(("rIdT", RelTypeBase + "styles", "/xl/styles.xml"));
            parts.Add(("xl/styles.xml", Encoding.UTF8.GetBytes(styles)));
        }

        parts.Add(("xl/_rels/workbook.xml.rels", Rels([.. rels])));
        return MakePackage(name, [.. parts]);
    }

    private static byte[] Rels(params (string Id, string Type, string Target)[] relationships) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        $"<Relationships xmlns=\"{PackageRelNs}\">" +
        string.Concat(relationships.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{r.Target}\"/>")) +
        "</Relationships>");

    private string MakePackage(string name, params (string Entry, byte[] Data)[] parts)
    {
        string path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in parts)
        {
            WriteEntry(zip, entry, data);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive zip, string entry, byte[] data)
    {
        using var stream = zip.CreateEntry(entry).Open();
        stream.Write(data);
    }
}
