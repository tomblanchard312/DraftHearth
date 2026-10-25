using System.Reflection;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel;
using WordCraft.ObjectModel.Model;
using WordCraft.Vba;
using WordCraft.Vba.Testing;

// A dependency-free test runner (the build has no NuGet access): every public static void
// method on Tests is a test.
int passed = 0, failed = 0;
foreach (var m in new[] { typeof(Tests), typeof(DocmTests) }.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)).Where(m => m.ReturnType == typeof(void)))
{
    if (args.Length > 0 && !args.Any(a => m.Name.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
    try
    {
        m.Invoke(null, null);
        passed++;
        Console.WriteLine($"  ok   {m.Name}");
    }
    catch (TargetInvocationException e)
    {
        failed++;
        Console.WriteLine($"  FAIL {m.Name}: {e.InnerException?.Message}");
    }
    finally { Probe.DisposeAll(); }
}
Console.WriteLine(Probe.Real ? $"engine: WordCraft ({Probe.Cli})" : "engine: FakeEngine");
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static class Assert
{
    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what ?? "value"}: expected <{Show(expected)}> but got <{Show(actual)}>");
    }

    public static void Equal(string[] expected, string[] actual, string? what = null) =>
        Equal(string.Join(" | ", expected), string.Join(" | ", actual), what ?? "paragraphs");

    public static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    public static T Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T e) { return e; }
        catch (Exception e) { throw new Exception($"expected {typeof(T).Name} but got {e.GetType().Name}: {e.Message}"); }
        throw new Exception($"expected {typeof(T).Name}");
    }

    static string Show<T>(T v) => v?.ToString()?.Replace("\r", "\\r").Replace("\v", "\\v") ?? "null";
}

static class Tests
{
    static (Probe Engine, Application App) Open(params string[] paragraphs)
    {
        var probe = Probe.Create(paragraphs);
        return (probe, new Application(probe.Engine));
    }

    static Probe RunVba(string source, string macro, params string[] paragraphs)
    {
        var (engine, app) = Open(paragraphs);
        var result = MacroCompiler.Compile([new VbaModule("Module1", VbaModuleKind.Standard, source)]);
        Assert.True(result.Success, "compile: " + string.Join("; ", result.Diagnostics));
        using var project = result.Project!;
        project.Run(app, macro);
        return engine;
    }

    static List<string> Output(Action run)
    {
        var lines = new List<string>();
        VbaHost.Output = lines.Add;
        try { run(); } finally { VbaHost.Output = Console.WriteLine; }
        return lines;
    }

    // ---- story map ----------------------------------------------------------------------------

    public static void StoryMap_positions_round_trip_through_utf8_offsets()
    {
        var (_, app) = Open("Café 😀 bar", "Second");
        var doc = app.ActiveDocument;
        var map = doc.Map;
        Assert.Equal(11 + 1 + 6 + 1, map.Length, "length counts UTF-16 units plus a mark per paragraph");
        Assert.Equal("Café 😀 bar\rSecond\r", map.Text);
        for (int c = 0; c <= map.Length; c++)
        {
            if (c == 6) continue; // between the emoji's surrogates: Word never addresses it
            int back = map.FromPos(map.ToPos(c));
            int expected = c == map.Length ? map.Paras[^1].Mark : c; // past the last mark maps to the end of the last paragraph
            Assert.Equal(expected, back, $"position {c}");
        }
        Assert.Equal(6, map.ToPos(5)["off"]!.GetValue<int>(), "é is two bytes");
        Assert.Equal(11, map.ToPos(8)["off"]!.GetValue<int>(), "the emoji is four bytes");
        var second = map.ToPos(12);
        Assert.Equal(1, second["path"]![0]!.GetValue<int>(), "position 12 is the second paragraph");
        Assert.Equal(0, second["off"]!.GetValue<int>());
    }

    public static void StoryMap_reads_tables_with_row_end_marks()
    {
        var inspect = JsonNode.Parse("""
            {"blocks": [
              {"index": 0, "type": "paragraph", "style": "Normal", "len": 3, "text": "Top"},
              {"index": 1, "type": "table", "rows": 1, "cols": 2, "cells": [["A", "B\nC"]]},
              {"index": 2, "type": "paragraph", "style": "Normal", "len": 3, "text": "End"}],
             "selection": {"anchor": {"story": "body", "path": [2], "off": 1}, "focus": {"story": "body", "path": [2], "off": 1}}}
            """)!;
        var map = StoryMap.Build(inspect, _ => null);
        Assert.Equal("Top\rA\rB\rC\r\rEnd\r", map.Text);
        Assert.Equal(12, map.SelAnchor, "selection maps into the paragraph after the table");
        Assert.Equal("[1,0,1,1]", map.ToPos(8)["path"]!.ToJsonString(), "cell paragraph path");
    }

    // ---- transpiler ---------------------------------------------------------------------------

    static string Vb(string vba) => Transpiler.Transpile(new VbaModule("M", VbaModuleKind.Standard, vba)).VbSource;

    public static void Transpiler_gives_untyped_dim_items_their_own_type()
    {
        var vb = Vb("Dim a, b As Long, c As Integer, s As String * 10");
        Assert.True(vb.Contains("Dim a As Object, b As Integer, c As Short, s As String"), vb);
    }

    public static void Transpiler_adds_parentheses_to_sub_calls_only()
    {
        Assert.Equal("Foo(a, b)", Transpiler.FixCall("Foo a, b"));
        Assert.Equal("x = 5", Transpiler.FixCall("x = 5"));
        Assert.Equal("obj.Item(1) = 3", Transpiler.FixCall("obj.Item(1) = 3"));
        Assert.Equal("Foo (x)", Transpiler.FixCall("Foo (x)"));
        Assert.Equal("Foo((a), b)", Transpiler.FixCall("Foo (a), b"));
        Assert.Equal(".Execute(Replace:=wdReplaceAll)", Transpiler.FixCall(".Execute Replace:=wdReplaceAll"));
        Assert.Equal("ActiveDocument.Paragraphs(1).Range.Select", Transpiler.FixCall("ActiveDocument.Paragraphs(1).Range.Select"));
        Assert.Equal("Next i", Transpiler.FixCall("Next i"));
        var vb = Vb("If x > 1 Then Foo a, b Else Bar c");
        Assert.True(vb.Contains("If x > 1 Then Foo(a, b) Else Bar(c)"), vb);
    }

    public static void Transpiler_maps_signatures_bounds_and_blocks()
    {
        var vb = Vb("""
            Function Total(items, Optional ByVal n As Long, ParamArray rest()) As Currency
                Dim arr(1 To 5, 0 To 2) As Integer
                ReDim Preserve arr(1 To 5, 0 To 3)
                While n < 3
                    Set obj = Nothing
                    n = n + 1
                Wend
            End Function
            Private Type Point
                X As Long
                Y As Long
            End Type
            """);
        Assert.True(vb.Contains("Public Function Total(ByRef items As Object, Optional ByVal n As Integer = Nothing, ByVal ParamArray rest() As Object) As Decimal"), vb);
        Assert.True(vb.Contains("Dim arr(5, 2) As Short"), vb);
        Assert.True(vb.Contains("ReDim Preserve arr(5, 3)"), vb);
        Assert.True(vb.Contains("End While") && vb.Contains("obj = Nothing"), vb);
        Assert.True(vb.Contains("Private Structure Point") && vb.Contains("Public X As Integer") && vb.Contains("End Structure"), vb);
    }

    public static void Transpiler_leaves_strings_and_member_names_alone()
    {
        var vb = Vb("""
            MsgBox "Format the Date with MsgBox"
            .Format = False
            d = Date
            s = Left$(t, 2) & String$(3, "-")
            """);
        Assert.True(vb.Contains("VbaHost.MsgBox(\"Format the Date with MsgBox\")"), vb);
        Assert.True(vb.Contains(".Format = False"), vb);
        Assert.True(vb.Contains("d = Today"), vb);
        Assert.True(vb.Contains("s = Left(t, 2) & StrDup(3, \"-\")"), vb);
    }

    public static void Transpiler_refuses_native_calls()
    {
        var e = Assert.Throws<TranspileException>(() => Vb("Private Declare PtrSafe Function GetTickCount Lib \"kernel32\" () As Long"));
        Assert.True(e.Message.Contains("native Windows"), e.Message);
        Assert.Throws<TranspileException>(() => Vb("Open \"c:\\x.txt\" For Output As #1"));
    }

    // ---- object model on the fake engine ------------------------------------------------------------

    public static void Range_text_spans_paragraph_marks()
    {
        var (_, app) = Open("One", "Two", "Three");
        var doc = app.ActiveDocument;
        Assert.Equal("One\rTwo\rThree\r", doc.Content.Text);
        Assert.Equal("ne\rT", doc.Range(1, 5).Text);
        Assert.Equal(3, doc.Paragraphs.Count);
        Assert.Equal("Two\r", doc.Paragraphs[2].Range.Text);
    }

    public static void Range_edits_shift_other_live_ranges_and_keep_the_selection()
    {
        var (engine, app) = Open("Hello world", "Second line");
        var doc = app.ActiveDocument;
        var later = doc.Range(12, 18); // "Second"
        app.Selection.SetRange(6, 11);  // "world"
        var first = doc.Range(0, 5);
        first.Text = "Goodbye";
        Assert.Equal("Goodbye world", engine.Paragraphs[0]);
        Assert.Equal((0, 7), (first.Start, first.End), "the edited range covers the new text");
        Assert.Equal("Second", later.Text, "a range after the edit moved with its text");
        Assert.Equal("world", app.Selection.Text, "the selection was put back on the same text");
    }

    public static void InsertAfter_and_InsertParagraphAfter_grow_the_range()
    {
        var (engine, app) = Open("Alpha");
        var r = app.ActiveDocument.Range(0, 5);
        r.InsertAfter(" beta");
        Assert.Equal("Alpha beta", r.Text);
        r.InsertParagraphAfter();
        r.InsertAfter("Gamma");
        Assert.Equal(["Alpha beta", "Gamma"], engine.Paragraphs);
        Assert.Equal("Alpha beta\rGamma", r.Text);
    }

    public static void Font_setters_format_only_the_range_and_getters_report_mixed()
    {
        var (engine, app) = Open("plain bold plain");
        var doc = app.ActiveDocument;
        var r = doc.Range(6, 10);
        r.Font.Bold = true;
        r.Font.Color = 255; // RGB(255, 0, 0)
        Assert.Equal(true, engine.CharProps(0, 6)["bold"]!.GetValue<bool>());
        Assert.Equal("[255,0,0]", engine.CharProps(0, 9)["color"]!["Rgb"]!.ToJsonString(), "WordCraft reads colours back as {Rgb: [r, g, b]}");
        Assert.True(engine.CharProps(0, 10)["bold"] is null, "the character after the range is untouched");
        Assert.Equal(-1, (int)r.Font.Bold);
        Assert.Equal(0, (int)doc.Range(0, 5).Font.Bold);
        Assert.Equal(WdConstants.wdUndefined, (int)doc.Range(0, 10).Font.Bold, "mixed formatting");
        Assert.Equal(255, (int)r.Font.Color);
        r.Font.Bold = WdConstants.wdToggle;
        Assert.Equal(0, (int)r.Font.Bold, "wdToggle flips it");
    }

    public static void Find_loop_redefines_the_range_on_each_match()
    {
        var (engine, app) = Open("cat dog cat", "bird cat");
        var r = app.ActiveDocument.Content;
        int hits = 0;
        r.Find.Text = "cat";
        while (r.Find.Execute())
        {
            hits++;
            Assert.Equal("cat", r.Text);
            r.Font.Italic = true;
            r.Collapse(WdConstants.wdCollapseEnd);
        }
        Assert.Equal(3, hits);
        Assert.True(engine.CharProps(1, 5)["italic"]?.GetValue<bool>() == true, "the match in the second paragraph");
        Assert.True(engine.CharProps(0, 4)["italic"] is null, "dog stays upright");
    }

    public static void Find_loop_without_collapse_still_advances()
    {
        var (_, app) = Open("a1 a2 a3");
        var r = app.ActiveDocument.Content;
        var seen = new List<int>();
        r.Find.Text = "a";
        while (r.Find.Execute()) seen.Add(r.Start);
        Assert.Equal("0,3,6", string.Join(",", seen));
    }

    public static void ReplaceAll_in_a_range_stays_inside_it()
    {
        var (engine, app) = Open("x x x", "x x");
        var r = app.ActiveDocument.Range(2, 5); // the last two x's of paragraph one
        r.Find.Execute(FindText: "x", ReplaceWith: "y", Replace: WdConstants.wdReplaceAll);
        Assert.Equal(["x y y", "x x"], engine.Paragraphs);
        Assert.True(!engine.Log.Contains("edit.replaceAll"), "a partial range is replaced match by match");
    }

    public static void ReplaceAll_over_the_document_uses_the_engine()
    {
        var (engine, app) = Open("teh cat", "teh end");
        app.Selection.SetRange(0, 3);
        var f = app.Selection.Find;
        f.Text = "teh";
        f.Replacement.Text = "the";
        f.Wrap = WdConstants.wdFindContinue;
        app.Selection.Collapse();
        Assert.True(f.Execute(Replace: WdConstants.wdReplaceAll), "found");
        Assert.Equal(["the cat", "the end"], engine.Paragraphs);
        Assert.True(engine.Log.Contains("edit.replaceAll"), "one engine Replace All (one Undo step)");
    }

    public static void Wildcards_and_caret_codes_follow_Word()
    {
        var (engine, app) = Open("Call 555-1234 or 555-9876.");
        var r = app.ActiveDocument.Content;
        r.Find.Execute(FindText: "([0-9]{3})-([0-9]{4})", MatchWildcards: true, ReplaceWith: @"(\1) \2", Replace: WdConstants.wdReplaceOne);
        Assert.Equal("Call (555) 1234 or 555-9876.", engine.Paragraphs[0]);
        var all = app.ActiveDocument.Content;
        all.Find.Execute(FindText: " or ", ReplaceWith: "^p", Replace: WdConstants.wdReplaceOne);
        Assert.Equal(["Call (555) 1234", "555-9876."], engine.Paragraphs);
        var w = app.ActiveDocument.Content;
        Assert.True(w.Find.Execute(FindText: "<[0-9]@>", MatchWildcards: true) && w.Text == "555", "word-boundary wildcards");
    }

    public static void Selection_typing_and_movement()
    {
        var (engine, app) = Open("");
        var s = app.Selection;
        s.TypeText("Line one");
        s.TypeParagraph();
        s.TypeText("Line two");
        Assert.Equal(["Line one", "Line two"], engine.Paragraphs);
        s.HomeKey(WdConstants.wdStory);
        s.EndKey(WdConstants.wdLine, WdConstants.wdExtend);
        Assert.Equal("Line one", s.Text);
        s.Font.Bold = true;
        Assert.True(engine.CharProps(0, 0)["bold"]?.GetValue<bool>() == true, "selection font");
        s.MoveRight(WdConstants.wdCharacter, 1);
        Assert.Equal(s.Start, s.End);
    }

    public static void Paragraph_styles_and_formats()
    {
        var (engine, app) = Open("Title text", "Body");
        var doc = app.ActiveDocument;
        doc.Paragraphs[1].Style = WdConstants.wdStyleHeading1;
        doc.Paragraphs[2].Alignment = WdConstants.wdAlignParagraphCenter;
        doc.Paragraphs[2].Format.LineSpacingRule = WdConstants.wdLineSpaceDouble;
        Assert.Equal("Heading1", engine.StyleOf(0));
        Assert.Equal("Heading 1", (string)doc.Paragraphs[1].Style);
        Assert.Equal("center", engine.ParaProps(1)["align"]!.GetValue<string>());
        Assert.Equal(1, (int)doc.Paragraphs[2].Alignment);
        Assert.Equal(2.0, engine.ParaProps(1)["lineSpacing"]!["value"]!.GetValue<double>());
        Assert.True(engine.ParaProps(0)["align"] is null, "the first paragraph keeps its alignment");
    }

    public static void Emoji_before_a_match_does_not_shift_formatting()
    {
        var (engine, app) = Open("😀😀 Acme");
        var r = app.ActiveDocument.Content;
        Assert.True(r.Find.Execute(FindText: "Acme"), "found");
        r.Font.Bold = true;
        Assert.True(engine.CharProps(0, 5)["bold"]?.GetValue<bool>() == true && engine.CharProps(0, 4)["bold"] is null, "bytes and UTF-16 kept apart");
    }

    // ---- VBA end to end -------------------------------------------------------------------------------

    public static void Vba_sample_project_runs()
    {
        var (engine, app) = Open("Introduction to Acme  widgets", "Section 1 Overview", "The Acme team  ships. acme is not Acme.");
        var result = MacroCompiler.Compile([VbaModule.FromFile(Path.Combine(AppContext.BaseDirectory, "samples", "Cleanup.bas"))]);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        using var project = result.Project!;
        var output = Output(() => project.Run(app, "Main"));
        Assert.Equal("Introduction to Acme widgets", engine.Paragraphs[0]);
        Assert.Equal("Heading1", engine.StyleOf(1));
        Assert.True(output.Any(l => l.Contains("Bolded") && l.Contains('3')), string.Join(" | ", output));
        Assert.True(output.Contains("Signed October 24, 2026"), "VBA date codes: mmmm is the month name");
        Assert.True(engine.CharProps(0, 16)["bold"]?.GetValue<bool>() == true, "Acme in the first paragraph is bold");
        Assert.True(engine.CharProps(2, 22)["bold"] is null, "lower-case acme is not");
        Assert.Equal("Tom, Engineer", engine.Paragraphs[^1]);
    }

    public static void Vba_late_binding_reaches_the_object_model()
    {
        var engine = RunVba("""
            Sub Go()
                Dim d, r
                Set d = ActiveDocument
                Set r = d.Range(0, 5)
                r.Text = "HELLO"
                d.Paragraphs(2).Range.InsertBefore "> "
            End Sub
            """, "Go", "hello there", "quoted");
        Assert.Equal(["HELLO there", "> quoted"], engine.Paragraphs);
    }

    public static void Vba_on_error_resume_next_skips_object_model_errors()
    {
        var output = new List<string>();
        VbaHost.Output = output.Add;
        try
        {
            RunVba("""
                Sub Go()
                    On Error Resume Next
                    Dim p
                    Set p = ActiveDocument.Paragraphs(99)
                    Debug.Print "after: "; Err.Number <> 0
                    Err.Clear
                    On Error GoTo Handler
                    Set p = ActiveDocument.Paragraphs(99)
                    Exit Sub
                Handler:
                    Debug.Print "handled"
                End Sub
                """, "Go", "only one");
        }
        finally { VbaHost.Output = Console.WriteLine; }
        Assert.Equal("after: True|handled", string.Join("|", output));
    }

    public static void Vba_language_basics_behave_like_vba()
    {
        var output = new List<string>();
        VbaHost.Output = output.Add;
        try
        {
            RunVba("""
                Function Twice(ByRef n As Integer) As Integer
                    n = n * 2
                    Twice = n
                End Function
                Sub Go()
                    Dim c As New Collection, i As Integer, a(1 To 3) As Long, total
                    c.Add "x" : c.Add "y"
                    Debug.Print c(1); c.Count
                    i = 4
                    Twice i
                    Debug.Print i
                    For i = 1 To 3: a(i) = i * 10: Next i
                    For Each v In a: total = total + v: Next
                    Debug.Print total; UBound(a)
                    Debug.Print Format(1234.5, "#,##0.00"); " "; Format(DateSerial(2026, 1, 5), "dd/mm/yyyy hh:nn")
                    Select Case total
                        Case 0 To 10: Debug.Print "small"
                        Case Is > 50: Debug.Print "big"
                    End Select
                End Sub
                """, "Go", "");
        }
        finally { VbaHost.Output = Console.WriteLine; }
        Assert.Equal("x2|8|603|1,234.50 05/01/2026 00:00|big", string.Join("|", output));
    }

    public static void Vba_security_rejects_escapes()
    {
        string[] bad =
        [
            "Sub A()\n Shell \"calc\"\nEnd Sub",
            "Sub A()\n Kill \"x\"\nEnd Sub",
            "Sub A()\n Debug.Print Environ(\"PATH\")\nEnd Sub",
            "Sub A()\n Dim o\n Set o = CreateObject(\"WScript.Shell\")\nEnd Sub",
            "Sub A()\n Dim o As Object\n o = \"x\"\n Debug.Print o.GetType().Assembly.Location\nEnd Sub",
            "Sub A()\n Debug.Print GetType(String).Assembly.FullName\nEnd Sub",
            "Sub A()\n Debug.Print System.IO.File.ReadAllText(\"/etc/passwd\")\nEnd Sub",
            "Sub A()\n Debug.Print CallByName(ActiveDocument, \"GetType\", 1)\nEnd Sub",
            "Sub A()\n Dim x\n x = ActiveDocument\n x.Engine.Execute \"file.saveAs\"\nEnd Sub",
        ];
        foreach (var src in bad)
        {
            var r = MacroCompiler.Compile([new VbaModule("M", VbaModuleKind.Standard, src)]);
            Assert.True(!r.Success, "should be rejected: " + src.Replace("\n", " "));
        }
    }
}

static class DocmTests
{
    static readonly string Cleanup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "Cleanup.bas"));

    public static void Ovba_compression_round_trips_and_shrinks_text()
    {
        var rnd = new Random(7);
        foreach (var size in new[] { 0, 1, 7, 100, 4095, 4096, 4097, 9000, 20000 })
        {
            var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, size / 20 + 1).Select(i => $"Sub M{i % 13}()\r\n  x = {i}\r\n")).Substring(0, size));
            var packed = WordCraft.Vba.Ole.OvbaCompression.Compress(text);
            Assert.Equal(Convert.ToBase64String(text), Convert.ToBase64String(WordCraft.Vba.Ole.OvbaCompression.Decompress(packed)), $"text of {size}");
            if (size > 1000) Assert.True(packed.Length < text.Length / 2, $"compressed {size} to {packed.Length}");
            var noise = new byte[size];
            rnd.NextBytes(noise);
            // Incompressible data goes in raw chunks of exactly 4096 bytes, so a short last chunk gains zero padding.
            var back = WordCraft.Vba.Ole.OvbaCompression.Decompress(WordCraft.Vba.Ole.OvbaCompression.Compress(noise));
            Assert.Equal(Convert.ToBase64String(noise), Convert.ToBase64String(back[..size]), $"noise of {size}");
            Assert.True(back.Skip(size).All(b => b == 0), "padding is zeros");
        }
    }

    public static void Ovba_decodes_a_copy_token_by_hand()
    {
        // "aaaaaa": a literal 'a', then a copy token (offset 1, length 5) = 0x0002 with flag bit 1 set.
        // Chunk: header 0xB003 (compressed, signature 0b011, size 4 + 2 - 3), flags 0x02, 'a', 02 00.
        var got = WordCraft.Vba.Ole.OvbaCompression.Decompress(Convert.FromHexString("0103B002610200"));
        Assert.Equal("aaaaaa", System.Text.Encoding.ASCII.GetString(got));
        Assert.Equal("0103B002610200", Convert.ToHexString(WordCraft.Vba.Ole.OvbaCompression.Compress("aaaaaa"u8)), "and the encoder makes the same bytes");
    }

    public static void Compound_file_round_trips_many_streams()
    {
        var w = new WordCraft.Vba.Ole.CompoundFileWriter();
        var big = new byte[10_000];
        new Random(1).NextBytes(big);
        w.AddStream("PROJECT", System.Text.Encoding.ASCII.GetBytes("Module=A"));
        w.AddStream("VBA/dir", [1, 2, 3]);
        for (int i = 0; i < 30; i++) w.AddStream($"VBA/Module{i}", System.Text.Encoding.ASCII.GetBytes(new string((char)('a' + i % 26), 100 + i * 37)));
        w.AddStream("VBA/Big", big);
        var r = new WordCraft.Vba.Ole.CompoundFileReader(w.ToArray());
        Assert.Equal(32, r.Find("VBA")!.Children.Count, "every sibling is reachable");
        Assert.Equal(new string('e', 100 + 4 * 37), System.Text.Encoding.ASCII.GetString(r.Read("VBA/module4")), "mini stream (case-insensitive name)");
        Assert.Equal(Convert.ToBase64String(big), Convert.ToBase64String(r.Read("VBA/Big")), "regular sectors");
    }

    public static void Hostile_compound_files_fail_cleanly()
    {
        var good = new WordCraft.Vba.Ole.CompoundFileWriter();
        good.AddStream("VBA/dir", new byte[5000]);
        var bytes = good.ToArray();
        var rnd = new Random(3);
        for (int i = 0; i < 300; i++)
        {
            var bad = (byte[])bytes.Clone();
            for (int k = 0; k < 8; k++) bad[rnd.Next(bad.Length)] = (byte)rnd.Next(256);
            try { var r = new WordCraft.Vba.Ole.CompoundFileReader(bad); if (r.Find("VBA/dir") is { } e && !e.IsStorage) r.Read(e); }
            catch (WordCraft.Vba.Ole.CompoundFileException) { }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException) { throw new Exception($"mutation {i}: {e.GetType().Name}: {e.Message}"); }
        }
    }

    static string MakeDocm(params VbaModule[] modules)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wcvba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var docx = Path.Combine(dir, "plain.docx");
        // A real .docx: WordCraft's own when testing against it, otherwise a minimal package.
        if (Probe.Real)
        {
            var txt = Path.Combine(dir, "in.txt");
            File.WriteAllText(txt, "Introduction to Acme  widgets\nSection 1 Overview\nThe Acme team  ships.");
            System.Diagnostics.Process.Start(Probe.Cli!, ["convert", txt, docx])!.WaitForExit();
        }
        else MinimalDocx(docx);
        var docm = Path.Combine(dir, "macros.docm");
        WordCraft.Vba.Ole.VbaPackage.MakeMacroEnabled(docx, WordCraft.Vba.Ole.VbaPackage.BuildProject(modules), docm);
        return docm;
    }

    static void MinimalDocx(string path)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Put(string name, string xml) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(xml); }
        Put("[Content_Types].xml", """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
        Put("_rels/.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
        Put("word/_rels/document.xml.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>""");
        Put("word/document.xml", """<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Hi</w:t></w:r></w:p></w:body></w:document>""");
    }

    public static void Docm_modules_read_back_with_their_kinds()
    {
        var docm = MakeDocm(
            new VbaModule("Cleanup", VbaModuleKind.Standard, Cleanup),
            new VbaModule("ThisDocument", VbaModuleKind.Document, "Private Sub Document_Open()\r\n    Debug.Print \"opened\"\r\nEnd Sub\r\n"),
            new VbaModule("Counter", VbaModuleKind.Class, "Public Count As Long\r\nPublic Sub Bump()\r\n    Count = Count + 1\r\nEnd Sub\r\n"));
        var modules = WordCraft.Vba.Ole.VbaProjectReader.ReadFile(docm);
        Assert.Equal("Cleanup:Standard ThisDocument:Document Counter:Class", string.Join(" ", modules.Select(m => $"{m.Name}:{m.Kind}")));
        Assert.True(modules[0].Source.Contains("Selection.Find.Execute Replace:=wdReplaceAll"), "source survives");
        var compiled = MacroCompiler.Compile(modules);
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        compiled.Project!.Dispose();
    }

    public static void Docm_macros_run_on_the_document_and_survive_a_save()
    {
        var docm = MakeDocm(new VbaModule("Cleanup", VbaModuleKind.Standard, Cleanup));
        var modules = WordCraft.Vba.Ole.VbaProjectReader.ReadFile(docm);
        var (engine, app) = (Probe.Create(), (Application?)null);
        app = new Application(engine.Engine);
        if (!Probe.Real) return; // the fake engine can't open a .docx; the reader is covered above
        app.Documents.Open(docm);
        using var project = MacroCompiler.Compile(modules).Project!;
        VbaHost.Output = _ => { };
        try { project.Run(app, "FixDoubleSpaces"); } finally { VbaHost.Output = Console.WriteLine; }
        Assert.Equal("Introduction to Acme widgets", engine.Paragraphs[0]);
        var saved = Path.ChangeExtension(docm, ".saved.docm");
        app.ActiveDocument.SaveAs2(saved);
        var again = WordCraft.Vba.Ole.VbaProjectReader.ReadFile(saved);
        Assert.Equal("Cleanup", again.Single().Name, "WordCraft kept the VBA project when it saved");
    }
}
