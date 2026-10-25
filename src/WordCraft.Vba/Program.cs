using WordCraft.ObjectModel;
using WordCraft.ObjectModel.Transport;
using WordCraft.Vba;
using WordCraft.Vba.Testing;

const string Usage = """
    wcvba — run Word VBA macros against WordCraft

      wcvba transpile <module.bas|.cls>...          print the VB.NET translation
      wcvba check     <module.bas|.cls>...          compile and apply the macro security rules
      wcvba run       <module.bas|.cls>... --macro <[Module.]Name> <engine>

    engines:
      --connect <port>                 a running window (wordcraft --control <port>)
      --cli <wordcraft-cli> [--doc <in>] [--save <out>]
                                       a headless session (wordcraft-cli mcp)
      --fake "<para 1>|<para 2>"       the in-memory test engine; prints the result

    --unsafe    skip the security rules (trusted macros only)
    """;

if (args.Length < 2) { Console.Error.WriteLine(Usage); return 2; }

var verb = args[0];
var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var modules = files.Select(VbaModule.FromFile).ToList();

switch (verb)
{
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
            if (Opt("--doc") is string doc) app.Documents.Open(doc);
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
