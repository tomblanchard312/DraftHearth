# DraftHearth VBA

Run Word VBA macros against DraftHearth (or upstream [WordCraft](https://github.com/storytold/wordcraft)). VBA is transpiled to VB.NET, compiled in memory with Roslyn, checked against an allow-list, and run against a Word object model implemented over the app's command protocol.

This folder was the separate `wordcraft-vba` repository; its history is kept in DraftHearth's. It is an optional .NET component: `cargo` doesn't build it and `cargo xtask ci` doesn't run it. The C# namespaces keep their `WordCraft.*` names, as DraftHearth's Rust crates do. Licence: MIT OR Apache-2.0, like the rest of the repository.

**Status:** early, usable workbench and a limited macro runtime. The desktop editor supports `.bas`/`.cls` source editing, existing-module browsing and `.docm` source updates, with a document designer, icon-based controls toolbox, local coding chat and separately reviewed local code completion. The optional CLI helper transpiles, checks and runs a supported subset of VBA against DraftHearth's document engine. This is not full Word VBA compatibility. Native Microsoft Word execution and repair-free round-trip validation remain unverified. See the [workspace guide](../docs/vba-editor.md) for UI behavior and boundaries.

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

Building requires a .NET SDK; the portable Roslyn compiler is pinned to Microsoft.CodeAnalysis.VisualBasic 5.9.0 (`RoslynDir` can override it for development). Projects target the .NET version bundled with the build SDK. Native packages include a self-contained `wcvba` helper, runtime and notices, so users do not need a development SDK. The desktop editor prefers that bundled helper; `NISABA_VBA` remains an explicit override. Macro execution is currently exposed by the CLI, not by a GUI Run button.

```sh
cd vba
dotnet build
dotnet run --project tests/WordCraft.Vba.Tests   # synthetic tests, including FakeEngine
cargo build --release -p wordcraft-cli --manifest-path ../Cargo.toml
WCVBA_CLI=../target/release/wordcraft-cli dotnet run --project tests/WordCraft.Vba.Tests   # engine-backed tests on DraftHearth

dotnet run --project src/WordCraft.Vba -- transpile samples/Cleanup.bas
dotnet run --project src/WordCraft.Vba -- check samples/Evil.bas            # rejected
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --fake "Acme  widgets|Section 1"

# macro-enabled documents: modules are read straight from the file
dotnet run --project src/WordCraft.Vba -- list report.docm
dotnet run --project src/WordCraft.Vba -- run report.docm --macro Main --cli ../target/release/wordcraft-cli --save out.docm
dotnet run --project src/WordCraft.Vba -- pack samples/Cleanup.bas --into plain.docx --out report.docm   # test documents

# a running DraftHearth window
nisaba --control 7981 &
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --connect 7981
dotnet run --project src/WordCraft.Vba -- run samples/Cleanup.bas --macro Main --cli ../target/release/wordcraft-cli --doc in.docx --save out.docx
```

The helper's executable is named `wcvba` when installed. A command-line engine path passed to
`--cli` must support DraftHearth's MCP stdio protocol (the workspace build names it `wordcraft-cli`).
For a headless run, use `--cli`; `--connect` attaches to a running desktop control channel. The
optional `--unsafe` switch bypasses the macro allow-list and should only be used for trusted code.

`--connect` reads the window's key from `WORDCRAFT_CONTROL_KEY`, else from `control-key.local-<port>` in DraftHearth's settings folder (`~/.config/nisaba`, or `DraftHearth` under Application Support / `%APPDATA%`), then WordCraft's. `WORDCRAFT_PROFILE_DIR` overrides both, as it does for the app. `Application.Name` is "DraftHearth".

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

DraftHearth preserves the VBA project when saving a `.docm`. The editor can update supported existing
module sources and save them with the document, preserving unedited modules and opaque project
records. Unsupported project layouts and lossy code-page conversions are refused. No-op updates
preserve project bytes. Editing signed projects requires explicit confirmation to remove the now
invalid signature. Adding new project modules or changing module kinds is not supported by the
editor. Synthetic tests cover these preservation rules; native Word compatibility has not been
validated.

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

## Desktop workspace and current limits

- Open **Developer › Code Editor** (Alt+F11) for the side-by-side **Code**, **Designer** and **Chat** workspace. The project explorer lists embedded modules and open/imported source modules; syntax colors distinguish VBA tokens. Save a module as `.bas`/`.cls` or save supported edited project sources with the document as `.docm`.
- The **Designer** shows and edits the live document surface. Its icon-only toolbox inserts supported body content controls by drag-and-drop or at the caret. It is not a visual VBA UserForm designer. The document-model pane displays/selects body paragraphs, including nested table-cell paragraphs.
- **Chat** uses the model selected in Scribe and sends bounded document and module context. **Suggest code** uses the local completion model configured through Scribe. Both show proposals for review; generated VBA is not automatically inserted or executed. Completion insertion is guarded against source changes while inference runs.
- The UI can **Check** and **Transpile** through the optional helper. Macro execution remains a separate CLI action; the GUI has no Run button. The headless CLI path is tested; the desktop `--connect` execution path has not been validated in the current test record.
- Shipping the runtime by default also requires shipping .NET (on FreeBSD, the `lang/dotnet` port).
- `Property Get/Let/Set`, UserForms (skipped with a warning), events (`Document_Open`, `AutoOpen` do not run on open), debugging, full IntelliSense and project-reference editing are not implemented. Headers, footers, notes, full field semantics and table/cell object-model collections are also incomplete. `Font.Size`/`Name` getters read explicit run formatting, not style-derived values; `Find` with formatting is unsupported.
- `(1 To n)` arrays become 0-based arrays one element longer: `LBound` is 0 and `For Each` sees an extra empty first element. `Debug.Print` does not pad numbers the way VBA does.
- The allow-list is defense in depth, not an OS sandbox. Macro execution should be limited to trusted code until the runner is isolated. Native Word, Windows/macOS runtime, signature behavior in Word, and repair-free round trips are not validated.

## Validation record

The last recorded VBA runtime validation (2026-10-26) reports 37 tests passing against the real
Linux headless DraftHearth engine. The FakeEngine suite reports 35 passing with two engine-only form
tests skipped. Coverage includes transpilation, object-model behavior, macro execution and project
preservation. A synthetic embedded macro updated a body text control and checkbox. The broader
workspace guide records local chat/completion insertion and compilation checks, plus designer drag
and drop exercised through the headless UI/control harness; these are not live desktop GUI tests.

The macro regression runs an embedded synthetic macro, saves and reopens the document, checks text
and formatting, then recompiles preserved source and executes it again. These checks use original
synthetic fixtures. They do not establish native Word execution or repair-free compatibility in
Microsoft Word. The runtime is not OS-sandboxed.

## Revision history

- 2026-10-28: Updated this guide to match the current editor, designer, local assistant, helper and CLI runtime boundaries; clarified validation scope and unsupported features.

- 2026-10-26: Main-body form control facade, ID/document guards, text and check-box edits, metadata/lock properties; 37 real-engine tests pass. Chained collection property assignments now transpile correctly.

- 2026-10-25: Validated all 35 tests on the real headless engine; strengthened the embedded-macro regression to execute, save, reopen, recompile and execute again with formatting assertions.

- 2026-10-25: Added the `modules-json` bridge for the desktop source editor. Added `.docm` source writeback with project/designer/OLE metadata preservation and explicit invalidated-signature removal. GUI macro execution remains open.
