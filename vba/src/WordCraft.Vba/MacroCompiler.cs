using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using WordCraft.ObjectModel;

namespace WordCraft.Vba;

public sealed record MacroDiagnostic(string Module, int Line, string Message, bool IsError)
{
    public override string ToString() => $"{Module}{(Line > 0 ? $" line {Line}" : "")}: {(IsError ? "error" : "warning")}: {Message}";
}

/// <summary>A macro project compiled in memory, loaded into its own unloadable context.</summary>
public sealed class CompiledProject : IDisposable
{
    readonly AssemblyLoadContext _context;
    readonly Assembly _assembly;

    internal CompiledProject(AssemblyLoadContext context, Assembly assembly, IReadOnlyList<TranspiledModule> modules)
    {
        _context = context;
        _assembly = assembly;
        Modules = modules;
    }

    public IReadOnlyList<TranspiledModule> Modules { get; }

    /// <summary>Public parameterless Subs, as Tools › Macros lists them ("Module.Name").</summary>
    public IEnumerable<string> Macros =>
        _assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void))
                .Select(m => $"{t.Name}.{m.Name}"));

    /// <summary>Runs "Name" or "Module.Name" against a Word application (case-insensitive, as in VBA).</summary>
    public object? Run(Application app, string macro, params object?[] args)
    {
        var (moduleName, name) = macro.Contains('.') ? (macro[..macro.IndexOf('.')], macro[(macro.IndexOf('.') + 1)..]) : ((string?)null, macro);
        var method = _assembly.GetTypes()
            .Where(t => moduleName is null || t.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException($"No macro named {macro}. Available: {string.Join(", ", Macros)}");

        WordGlobals.Current = app;
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public void Dispose() => _context.Unload();
}

public static class MacroCompiler
{
    public sealed record Result(CompiledProject? Project, IReadOnlyList<MacroDiagnostic> Diagnostics)
    {
        public bool Success => Project is not null;
    }

    static readonly string[] FrameworkAssemblies =
    [
        "System.Private.CoreLib", "System.Runtime", "Microsoft.VisualBasic.Core", "System.Collections",
        "System.Collections.NonGeneric", "System.Linq", "System.ComponentModel.Primitives", "netstandard",
    ];

    public static Result Compile(IEnumerable<VbaModule> modules, bool enforceSecurity = true)
    {
        var diagnostics = new List<MacroDiagnostic>();
        var transpiled = new List<TranspiledModule>();
        foreach (var module in modules)
        {
            if (module.Kind == VbaModuleKind.Form)
            {
                diagnostics.Add(new MacroDiagnostic(module.Name, 0, "UserForms aren't supported yet; the form's code was skipped.", false));
                continue;
            }
            try
            {
                var t = Transpiler.Transpile(module);
                transpiled.Add(t);
                diagnostics.AddRange(t.Warnings.Select(w => new MacroDiagnostic(module.Name, 0, w, false)));
            }
            catch (TranspileException e)
            {
                diagnostics.Add(new MacroDiagnostic(e.Module, e.Line, e.Message[(e.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..], true));
            }
        }
        if (diagnostics.Any(d => d.IsError)) return new Result(null, diagnostics);

        var parse = new VisualBasicParseOptions(LanguageVersion.Latest);
        var trees = transpiled.Select(t => VisualBasicSyntaxTree.ParseText(t.VbSource, parse, path: t.Name)).ToList();
        trees.Add(VisualBasicSyntaxTree.ParseText(GlobalsModule, parse, path: "(globals)"));
        var options = new VisualBasicCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                globalImports: GlobalImport.Parse(
                    "Microsoft.VisualBasic", "WordCraft.ObjectModel",
                    "WordCraft.ObjectModel.WdConstants", "Word = WordCraft.ObjectModel"),
                optionStrict: OptionStrict.Off,
                optionExplicit: false,
                optionInfer: false)
            .WithOptimizationLevel(OptimizationLevel.Release);

        var compilation = VisualBasicCompilation.Create($"Macros_{Guid.NewGuid():N}", trees, References(), options);
        var compileDiagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (enforceSecurity && compileDiagnostics.Count == 0)
            compileDiagnostics.AddRange(MacroSecurity.Check(compilation));
        diagnostics.AddRange(compileDiagnostics.Select(d => Map(d, transpiled)).DistinctBy(d => d.ToString()));
        if (diagnostics.Any(d => d.IsError)) return new Result(null, diagnostics);

        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success)
        {
            diagnostics.AddRange(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => Map(d, transpiled)));
            return new Result(null, diagnostics);
        }
        pe.Position = 0;
        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        var assembly = context.LoadFromStream(pe);
        return new Result(new CompiledProject(context, assembly, transpiled), diagnostics);
    }

    /// <summary>
    /// Word's globals as a module in the macro's own namespace. Module members bind before
    /// imports, so <c>Selection</c> means the property while <c>As Selection</c> still names the type.
    /// </summary>
    const string GlobalsModule = """
        Option Strict On
        Public Module WordGlobalsModule
            Public ReadOnly Property Application As WordCraft.ObjectModel.Application
                Get
                    Return WordCraft.ObjectModel.WordGlobals.Application
                End Get
            End Property
            Public ReadOnly Property ActiveDocument As WordCraft.ObjectModel.Document
                Get
                    Return WordCraft.ObjectModel.WordGlobals.ActiveDocument
                End Get
            End Property
            Public ReadOnly Property Selection As WordCraft.ObjectModel.Selection
                Get
                    Return WordCraft.ObjectModel.WordGlobals.Selection
                End Get
            End Property
            Public ReadOnly Property Documents As WordCraft.ObjectModel.Documents
                Get
                    Return WordCraft.ObjectModel.WordGlobals.Documents
                End Get
            End Property
        End Module
        """;

    static IEnumerable<MetadataReference> References()
    {
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var name in FrameworkAssemblies)
        {
            var path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path)) yield return MetadataReference.CreateFromFile(path);
        }
        yield return MetadataReference.CreateFromFile(typeof(Application).Assembly.Location);
    }

    static MacroDiagnostic Map(Diagnostic d, IReadOnlyList<TranspiledModule> modules)
    {
        var span = d.Location.GetLineSpan();
        var module = modules.FirstOrDefault(m => m.Name == span.Path);
        int vbLine = span.StartLinePosition.Line;
        int line = module is not null && vbLine >= 0 && vbLine < module.LineMap.Count ? module.LineMap[vbLine] : 0;
        return new MacroDiagnostic(module?.Name ?? span.Path, line, d.GetMessage(), d.Severity == DiagnosticSeverity.Error);
    }
}
