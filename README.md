# wordcraft-vba

Run Word VBA macros against [WordCraft](https://github.com/storytold/wordcraft). VBA is transpiled to VB.NET, compiled in memory with Roslyn, checked against an allow-list, and run against a Word object model implemented over WordCraft's command protocol.

**Status:** spike. The 30 tests pass against real WordCraft 0.10 (headless `wordcraft-cli mcp`) and against the in-memory `FakeEngine`.

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
dotnet tests/WordCraft.Vba.Tests/bin/Debug/net8.0/WordCraft.Vba.Tests.dll   # 24 tests on FakeEngine
WCVBA_CLI=/path/to/wordcraft-cli dotnet tests/WordCraft.Vba.Tests/bin/Debug/net8.0/WordCraft.Vba.Tests.dll   # same tests on WordCraft

dotnet run --project src/WordCraft.Vba -- transpile samples/Cleanup.bas
dotnet run --project src/WordCraft.Vba -- check samples/Evil.bas            # rejected
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --fake "Acme  widgets|Section 1"

# macro-enabled documents: modules are read straight from the file
dotnet run --project src/WordCraft.Vba -- list report.docm
dotnet run --project src/WordCraft.Vba -- run report.docm --macro Main --cli /path/to/wordcraft-cli --save out.docm
dotnet run --project src/WordCraft.Vba -- pack samples/Cleanup.bas --into plain.docx --out report.docm   # test documents

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

## Macro-enabled documents

`Ole/` reads the VBA project with no dependencies, from the published specs:
- `CompoundFileReader` / `CompoundFileWriter` ([MS-CFB]): FAT, DIFAT, mini stream, red-black directory. Input is treated as hostile.
- `OvbaCompression` ([MS-OVBA] 2.4.1): decompression and the encoder.
- `VbaProjectReader`: the `dir` stream, module kinds from `PROJECT`, the project's code page. Sources: .docm/.dotm (`word/vbaProject.bin`, located through the package relationships), .doc/.dot (the `Macros` storage), or a bare vbaProject.bin.
- `VbaPackage`: writes a vbaProject.bin (source only; `_VBA_PROJECT` version 0xFFFF tells the host to compile) and turns a .docx into a .docm. It writes no protection records (CMG/DPB/GC), so it's used for test documents and hasn't been checked against Word yet.

WordCraft keeps the VBA project when it saves a .docm; a test checks that.

## Security

`MacroSecurity` walks the bound syntax tree. It rejects:
- any symbol, or expression of a type, outside the macro, the object model and a small slice of the base library
- late-bound member names that aren't members of the object model
- `Shell`, `CreateObject`, `Environ`, `CallByName`, file I/O, `Declare`, reflection and `GetType`

.NET has no in-process sandbox, so this is defence in depth, not the boundary. The boundary should be the runner process inside an OS sandbox (AppContainer, Landlock/seccomp, macOS sandbox), with the WordCraft connection as its only permission. That part is not built yet.

## Known gaps

- Only the headless transport (`--cli`) has been tested; the control channel to a running window (`--connect`) has not.
- `Property Get/Let/Set`, UserForms (skipped with a warning), events (`Document_Open`, `AutoOpen` don't run on open). Writing edited macros back into a document.
- Headers, footers, notes, fields as Word counts them (WordCraft shows a field's result), tables (`Tables`, `Cells`), `Font.Size`/`Name` from styles (only explicit run formatting is read), `Find` with formatting.
- `(1 To n)` arrays become 0-based arrays one element longer: `LBound` is 0 and `For Each` sees an extra empty first element.
- `Debug.Print` doesn't pad numbers the way VBA does.
