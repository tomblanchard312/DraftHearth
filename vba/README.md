# Nisaba VBA

Run Word VBA macros against Nisaba (or upstream [WordCraft](https://github.com/storytold/wordcraft)). VBA is transpiled to VB.NET, compiled in memory with Roslyn, checked against an allow-list, and run against a Word object model implemented over the app's command protocol.

This folder was the separate `wordcraft-vba` repository; its history is kept in Nisaba's. It is an optional .NET component: `cargo` doesn't build it and `cargo xtask ci` doesn't run it. The C# namespaces keep their `WordCraft.*` names, as Nisaba's Rust crates do. Licence: MIT OR Apache-2.0, like the rest of the repository.

**Status:** spike. Nisaba now has a [side-by-side VBA source editor](../docs/vba-editor.md) with syntax colors, embedded-module browsing, local Phi-4 completion and a live document-model pane. The current synthetic suite passes 37 tests on the real headless Nisaba engine; FakeEngine passes 35 with two real-only form tests explicitly skipped. It covers the transpiler, object model, macro execution and project preservation. Native Word runtime and repair-free round-trip validation remain open.

```
VBA (.bas/.cls) ──► Transpiler ──► VB.NET ──► Roslyn (Option Strict Off) ──► MacroSecurity ──► run
                                                                                              │
              Word object model (Application, Document, Range, Selection, Find, Font…)  ◄─────┘
                                │  WordCraft command ids, JSON
            ┌───────────────────┴───────────────────┐
     ControlChannelEngine                    McpStdioEngine
     (running window, --control PORT)        (headless, nisaba-cli mcp)
```

## Quick start

Requires a .NET 8 or newer SDK. There are no package dependencies: the VB compiler comes from the SDK (`RoslynDir` overrides it), so the projects target the .NET that SDK ships (`net8.0` with SDK 8, `net10.0` with SDK 10) and run on it.

```sh
cd vba
dotnet build
dotnet run --project tests/WordCraft.Vba.Tests   # on FakeEngine
cargo build --release -p wordcraft-cli --manifest-path ../Cargo.toml
WCVBA_CLI=../target/release/wordcraft-cli dotnet run --project tests/WordCraft.Vba.Tests   # same tests on Nisaba

dotnet run --project src/WordCraft.Vba -- transpile samples/Cleanup.bas
dotnet run --project src/WordCraft.Vba -- check samples/Evil.bas            # rejected
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --fake "Acme  widgets|Section 1"

# macro-enabled documents: modules are read straight from the file
dotnet run --project src/WordCraft.Vba -- list report.docm
dotnet run --project src/WordCraft.Vba -- run report.docm --macro Main --cli nisaba-cli --save out.docm
dotnet run --project src/WordCraft.Vba -- pack samples/Cleanup.bas --into plain.docx --out report.docm   # test documents

# a running Nisaba window
nisaba --control 7981 &
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --connect 7981
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --cli nisaba-cli --doc in.docx --save out.docx
```

`--connect` reads the window's key from `WORDCRAFT_CONTROL_KEY`, else from `control-key.local-<port>` in Nisaba's settings folder (`~/.config/nisaba`, or `Nisaba` under Application Support / `%APPDATA%`), then WordCraft's. `WORDCRAFT_PROFILE_DIR` overrides both, as it does for the app. `Application.Name` is "Nisaba".

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
| `ContentControls`, `SelectContentControlsByTag/Title`, `.Range.Text`, `.Checked` | `document.contentControls`, normal range editing and ID-targeted Developer commands; main body only |
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

## Form controls

`ActiveDocument.ContentControls` supports one-based and ID indexing, enumeration and Count.
`SelectContentControlsByTag` and `SelectContentControlsByTitle` filter collections (including
multiple controls with the same tag/title). `ContentControl.Range.Text`, `Checked`, `Title`, `Tag`,
`LockContents` and `LockContentControl` operate on existing body controls. IDs are re-resolved on
access; ambiguous/deleted controls and document switches are refused. These APIs follow the
[published Word API](https://learn.microsoft.com/en-us/office/vba/word/concepts/working-with-word/working-with-content-controls).
Control creation, events, drop-down/date members, headers and UserForms remain open. See the
[workspace guide](../docs/vba-editor.md#vba-access-to-form-controls) for an example and validation.

## Known gaps

- Only the headless transport (`--cli`) has been tested; the control channel to a running window (`--connect`) has not.
- Developer › Code Editor supports source editing and checking; macro execution still uses this command line tool.
- Shipping it by default means shipping .NET (on FreeBSD, the `lang/dotnet` port).
- `Property Get/Let/Set`, UserForms (skipped with a warning), events (`Document_Open`, `AutoOpen` don't run on open). The editor writes changed source back to supported `.docm` projects; unsupported directory layouts and lossy code-page conversions are refused. Native Word round trips are not yet validated.
- Headers, footers, notes, fields as Word counts them (WordCraft shows a field's result), tables (`Tables`, `Cells`), `Font.Size`/`Name` from styles (only explicit run formatting is read), `Find` with formatting.
- `(1 To n)` arrays become 0-based arrays one element longer: `LBound` is 0 and `For Each` sees an extra empty first element.
- `Debug.Print` doesn't pad numbers the way VBA does.

## Validation on 2026-10-25

The .NET solution builds with zero warnings/errors. All 35 tests pass against the real Linux
headless Nisaba engine (`WCVBA_CLI=target/debug/wordcraft-cli`), and the FakeEngine suite also
passes. The DOCM execution regression runs the full Cleanup macro, saves/reopens the document,
checks text, bold and heading style, recompiles the preserved source and runs it again.

A separate command-line run of embedded `Cleanup.Main` saved
`/tmp/nisaba-macro-executed.docm`; rerunning `Cleanup.FixDoubleSpaces` from that saved document
produced `/tmp/nisaba-macro-reexecuted.docm`. Direct package inspection confirmed the expected
text, heading style, bold formatting, macro-enabled content type and byte-identical VBA project
in both outputs. These are original synthetic fixtures. Native Word execution, desktop control
transport and OS sandbox confinement were not tested; GUI macro execution remains unavailable.

## Revision history

- 2026-10-26: Main-body form control facade, ID/document guards, text and check-box edits, metadata/lock properties; 37 real-engine tests pass. Chained collection property assignments now transpile correctly.

- 2026-10-25: Validated all 35 tests on the real headless engine; strengthened the embedded-macro regression to execute, save, reopen, recompile and execute again with formatting assertions.

- 2026-10-25: Added the `modules-json` bridge for the desktop source editor. Added `.docm` source writeback with project/designer/OLE metadata preservation and explicit invalidated-signature removal. GUI macro execution remains open.
