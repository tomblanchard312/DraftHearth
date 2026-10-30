using WordCraft.ObjectModel;
using WordCraft.ObjectModel.Transport;
using WordCraft.Vba;
using WordCraft.Vba.Testing;

const string Usage = """
    wcvba — run VBA macros against Qanloom (based on WordCraft)

      wcvba list      <file>...                     the VBA modules in a .docm/.dotm/.doc or vbaProject.bin
      wcvba transpile <file>...                     print the VB.NET translation
      wcvba check     <file>...                     compile and apply the macro security rules
      wcvba run       <file>... --macro <[Module.]Name> <engine>
      wcvba pack      <module.bas|.cls>... --into <in.docx> --out <out.docm>
                                                    build a macro-enabled copy of a document (for testing)

    <file> is an exported module (.bas, .cls) or a macro-enabled document. Running a document's
    own macros opens that document unless --doc names another one.

    engines:
      --connect <port>                 a running window (qanloom --control <port>)
      --cli <qanloom-cli> [--doc <in>] [--save <out>]
                                       a headless session (qanloom-cli mcp; wordcraft-cli in a cargo build)
      --fake "<para 1>|<para 2>"       the in-memory test engine; prints the result

    --unsafe    skip the security rules (trusted macros only)
    """;

if (args.Length < 2) { Console.Error.WriteLine(Usage); return 2; }

var verb = args[0];
var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
static bool IsDocument(string f) => Path.GetExtension(f).ToLowerInvariant() is ".docm" or ".dotm" or ".doc" or ".dot" or ".bin";
// The editor supplies bounded JSON and a binary snapshot; this path never runs a macro.
if (verb == "update-project")
{
    var input = files.Single();
    if (new FileInfo(input).Length > 32 * 1024 * 1024) throw new InvalidOperationException("Editor request is too large.");
    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(input));
    var root = json.RootElement;
    var original = Convert.FromBase64String(root.GetProperty("original").GetString() ?? "");
    if (original.Length > 16 * 1024 * 1024) throw new InvalidOperationException("VBA project is too large.");
    var edited = root.GetProperty("modules").EnumerateArray().Select(m => new VbaModule(
        m.GetProperty("name").GetString() ?? "Module",
        Enum.Parse<VbaModuleKind>(m.GetProperty("kind").GetString() ?? "Standard"),
        m.GetProperty("source").GetString() ?? "")).ToList();
    var updated = WordCraft.Vba.Ole.VbaProjectEditor.Update(original, edited);
    Console.WriteLine(Convert.ToBase64String(updated));
    return 0;
}

var modules = files.SelectMany(f => IsDocument(f) ? WordCraft.Vba.Ole.VbaProjectReader.ReadFile(f) : [VbaModule.FromFile(f)]).ToList();
var docFile = files.FirstOrDefault(f => IsDocument(f) && !f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));

switch (verb)
{
    case "pack":
    {
        var into = Opt("--into") ?? throw new ArgumentException("--into <in.docx> is required");
        var output = Opt("--out") ?? Path.ChangeExtension(into, ".docm");
        WordCraft.Vba.Ole.VbaPackage.MakeMacroEnabled(into, WordCraft.Vba.Ole.VbaPackage.BuildProject(modules), output);
        Console.WriteLine($"wrote {output} ({modules.Count} modules)");
        return 0;
    }

    case "modules-json":
        if (modules.Count > 256 || modules.Sum(m => (long)m.Source.Length) > 2 * 1024 * 1024)
            throw new InvalidOperationException("VBA sources exceed the editor limit.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(modules.Select(m => new { name = m.Name, kind = m.Kind.ToString(), source = m.Source })));
        return 0;

    case "list":
        foreach (var m in modules)
            Console.WriteLine($"{m.Kind,-9} {m.Name,-24} {m.Source.Split('\n').Length,5} lines");
        return 0;

    case "transpile":
        foreach (var m in modules)
        {
            try
            {
                var t = Transpiler.Transpile(m);
                Console.WriteLine($"' ===== {m.Name} =====");
                Console.Write(t.VbSource);
                foreach (var w in t.Warnings) Console.Error.WriteLine($"warning: {w}");
            }
            catch (TranspileException e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
        }
        return 0;

    case "check":
    case "run":
    {
        var result = MacroCompiler.Compile(modules, enforceSecurity: !args.Contains("--unsafe"));
        foreach (var d in result.Diagnostics) Console.Error.WriteLine(d);
        if (!result.Success) return 1;
        using var project = result.Project!;
        if (verb == "check")
        {
            Console.WriteLine("OK. Macros: " + string.Join(", ", project.Macros));
            return 0;
        }

        var macro = Opt("--macro") ?? project.Macros.FirstOrDefault() ?? throw new InvalidOperationException("No macro to run.");
        IEngine engine;
        FakeEngine? fake = null;
        if (Opt("--connect") is string port) engine = ControlChannelEngine.Connect(int.Parse(port));
        else if (Opt("--cli") is string cli) engine = new McpStdioEngine(cli);
        else engine = fake = new FakeEngine((Opt("--fake") ?? "").Split('|'));

        using (engine)
        {
            var app = new Application(engine);
            if ((Opt("--doc") ?? (fake is null ? docFile : null)) is string doc) app.Documents.Open(doc);
            try
            {
                project.Run(app, macro);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"Run-time error in {macro}: {e.Message}");
                return 1;
            }
            if (Opt("--save") is string save) app.ActiveDocument.SaveAs2(save);
            if (fake is not null)
            {
                Console.WriteLine("--- document ---");
                foreach (var p in fake.Paragraphs.Select((t, i) => (t, i)))
                    Console.WriteLine($"[{fake.StyleOf(p.i)}] {p.t}");
            }
        }
        return 0;
    }

    default:
        Console.Error.WriteLine(Usage);
        return 2;
}
