using System.Text.Json.Nodes;
using WordCraft.ObjectModel;
using WordCraft.Vba;

static class ContentControlTests
{
    static (Probe Engine, Application App) Form()
    {
        if (!Probe.Real) throw new TestSkippedException("Requires WCVBA_CLI; FakeEngine does not implement content controls.");
        var engine = Probe.Create("Form");
        engine.Engine.Execute("caret.docEnd");
        foreach (var control in new[] { ("developer.plainText", "Name", "name"), ("developer.checkBox", "Name", "approved"), ("developer.plainText", "Extra", "name") })
        {
            engine.Engine.Execute("text.newParagraph");
            engine.Engine.Execute(control.Item1, new JsonObject { ["title"] = control.Item2, ["tag"] = control.Item3 });
            engine.Engine.Execute("caret.docEnd");
        }
        return (engine, new Application(engine.Engine));
    }

    public static void Content_controls_macros_fill_text_tick_boxes_and_preserve_values_on_save()
    {
        var (engine, app) = Form();
        var doc = app.ActiveDocument;
        Assert.Equal(3, doc.ContentControls.Count);
        Assert.Equal(2, doc.SelectContentControlsByTag("name").Count);
        Assert.Equal(2, doc.SelectContentControlsByTitle("Name").Count);
        var name = doc.SelectContentControlsByTag("name")[1];
        var approved = doc.SelectContentControlsByTag("approved")[1];
        string id = approved.ID;
        var result = MacroCompiler.Compile([new VbaModule("FillForm", VbaModuleKind.Standard, """
            Sub Main()
                Dim field As ContentControl
                Set field = ActiveDocument.SelectContentControlsByTag("name")(1)
                field.Range.Text = "Café 🙂 Cedar Harbor"
                ActiveDocument.SelectContentControlsByTag("approved")(1).Checked = True
                field.Title = "Customer name"
                field.Tag = "customer"
            End Sub
            """)]);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        using var project = result.Project!;
        project.Run(app, "Main");
        Assert.Equal("Café 🙂 Cedar Harbor", name.Range.Text);
        Assert.True(doc.ContentControls[id].Checked, "stable ID still finds checkbox after Unicode text shifts positions");
        Assert.Equal("Customer name", name.Title);
        Assert.Equal("customer", name.Tag);
        Assert.Equal(1, doc.SelectContentControlsByTag("name").Count);
        int count = 0;
        foreach (ContentControl control in doc.ContentControls) { Assert.True(control.ID.Length > 0, "stable ID"); count++; }
        Assert.Equal(3, count);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nisaba-form-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string extension in new[] { "docx", "docm" })
            {
                var path = System.IO.Path.Combine(dir, "filled." + extension);
                app.ActiveDocument.SaveAs2(path);
                var opened = app.Documents.Open(path);
                Assert.Equal("Café 🙂 Cedar Harbor", opened.SelectContentControlsByTag("customer")[1].Range.Text);
                Assert.True(opened.ContentControls[id].Checked, "checked state survives " + extension);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    public static void Content_controls_locks_missing_ids_and_removed_controls_fail_cleanly()
    {
        var (engine, app) = Form();
        var doc = app.ActiveDocument;
        var name = doc.ContentControls[1];
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = doc.ContentControls[0]);
        Assert.Throws<ArgumentException>(() => _ = doc.ContentControls["missing"]);
        Assert.Throws<InvalidOperationException>(() => _ = name.Checked);
        name.LockContents = true;
        string before = name.Range.Text;
        Assert.Throws<WordCraft.ObjectModel.Transport.EngineException>(() => name.Range.Text = "forbidden");
        Assert.Equal(before, name.Range.Text);
        name.LockContents = false;
        var approved = doc.SelectContentControlsByTag("approved")[1];
        approved.Checked = true;
        doc.Undo();
        Assert.True(!approved.Checked, "checkbox edit is undoable");
        engine.Engine.Execute("developer.removeControl", new JsonObject { ["control"] = "approved" });
        Assert.Throws<InvalidOperationException>(() => _ = approved.Checked);
        var collection = doc.ContentControls;
        app.Documents.Add();
        engine.Engine.Execute("developer.plainText", new JsonObject { ["tag"] = "replacement" });
        Assert.Throws<InvalidOperationException>(() => _ = collection.Count);
        Assert.Throws<InvalidOperationException>(() => name.Tag = "must not change another document");
        Assert.Equal("replacement", app.ActiveDocument.ContentControls[1].Tag);
    }
}
