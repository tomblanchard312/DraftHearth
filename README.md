# wordcraft-vba

Run Word VBA macros against [WordCraft](https://github.com/storytold/wordcraft). VBA is transpiled to VB.NET, compiled in memory with Roslyn, checked against an allow-list, and run against a Word object model implemented over WordCraft's command protocol.

**Status:** spike. The object model has been tested against an in-memory engine that mimics WordCraft's command shapes; it has not yet been run against a real WordCraft build.

```
VBA (.bas/.cls) ──► Transpiler ──► VB.NET ──► Roslyn (Option Strict Off) ──► MacroSecurity ──► run
                                                                                              │
              Word object model (Application, Document, Range, Selection, Find, Font…)  ◄─────┘
                                │  WordCraft command ids, JSON
            ┌───────────────────┴───────────────────┐
     ControlChannelEngine                    McpStdioEngine
     (running window, --control PORT)        (headless, wordcraft-cli mcp)
```

## Quick start

Requires the .NET 8 SDK. No NuGet packages: the VB compiler comes from the SDK (`RoslynDir` overrides it).

```sh
dotnet build
dotnet tests/WordCraft.Vba.Tests/bin/Debug/net8.0/WordCraft.Vba.Tests.dll   # 24 tests

dotnet run --project src/WordCraft.Vba -- transpile samples/Cleanup.bas
dotnet run --project src/WordCraft.Vba -- check samples/Evil.bas            # rejected
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --fake "Acme  widgets|Section 1"

# real WordCraft
wordcraft --control 7981 &
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --connect 7981
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --cli /path/to/wordcraft-cli --doc in.docx --save out.docx
```

## How the object model maps to WordCraft

Word addresses a story as one string of UTF-16 characters, with each paragraph mark taking one position. WordCraft addresses `{"story": "body", "path": [block, row, cell, block], "off": utf8Byte}`. `StoryMap` translates between the two. It is rebuilt from `document.inspect` when the text changes; formatting-only commands just mark it stale.

| Word | WordCraft |
|---|---|
| `Range.Text = …` | `select.range`, `text.delete`, `text.insert {raw: true}` / `text.newParagraph` / `text.lineBreak`, then the user's selection is restored |
| `Range.Font.*` | `select.range`, `format.set {props: CharProps}`, then the selection is restored; getters read the runs from `document.inspect` |
| `Range.ParagraphFormat.*`, `.Style` | `para.set {props: ParaProps}`, `para.style` |
| `Selection.TypeText` | `text.insert` without `raw`, so AutoCorrect applies as when typing in Word |
| `Selection.HomeKey/EndKey/MoveRight…` | `caret.*` with `extend` |
| `Find.Execute` | matched in .NET over the story text (ranges, `Do While .Execute` loops, wrap) |
| Replace All over the whole document | `edit.replaceAll`: WordCraft's own Word-syntax engine, one Undo step, honours Track Changes |
| `Documents.Open`, `SaveAs2`, `Save`, `Undo` | `file.open`, `file.saveAs`, `file.save`, `edit.undo` |

Ranges stay live: an edit through any range shifts the other ranges, and the selection, the way Word does.

## Transpiler

Covers: `Integer`/`Long`/`Currency`/`Variant` widths, ByRef by default, Optional without defaults (`IsMissing`), `ParamArray`, `Dim a, b As T`, `(1 To n)` bounds, `Set`/`Let`, `Wend`, `Type` → `Structure`, calls without parentheses (including inside single-line `If`), `Debug.Print`, `$` functions, `Date`/`Time`, `MsgBox`/`InputBox`/`Format` routed to the host, and VBA date codes in `Format` (`mm` is the month, `nn` minutes). `On Error Resume Next` / `GoTo` are kept as written, since VB.NET supports them.

## Security

`MacroSecurity` walks the bound syntax tree. It rejects:
- any symbol, or expression of a type, outside the macro, the object model and a small slice of the base library
- late-bound member names that aren't members of the object model
- `Shell`, `CreateObject`, `Environ`, `CallByName`, file I/O, `Declare`, reflection and `GetType`

.NET has no in-process sandbox, so this is defence in depth, not the boundary. The boundary should be the runner process inside an OS sandbox (AppContainer, Landlock/seccomp, macOS sandbox), with the WordCraft connection as its only permission. That part is not built yet.

## Known gaps

- Not yet run against a real WordCraft build.
- `Property Get/Let/Set`, UserForms, events (`Document_Open`, `AutoOpen` auto-run), reading `vbaProject.bin` from .docm.
- Headers, footers, notes, fields as Word counts them (WordCraft shows a field's result), tables (`Tables`, `Cells`), `Font.Size`/`Name` from styles (only explicit run formatting is read), `Find` with formatting.
- `(1 To n)` arrays become 0-based arrays one element longer: `LBound` is 0 and `For Each` sees an extra empty first element.
- `Debug.Print` doesn't pad numbers the way VBA does.
