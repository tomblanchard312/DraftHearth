# VBA code editor

> **Last updated:** 2026-10-28 · **Status:** desktop VBA workspace with project explorer, document designer, contextual chat and separate local completion; GUI macro execution remains open.

Open **Developer › Code Editor** (Alt+F11). The resizable editor stays beside the live document. Open a UTF-8 `.bas` or `.cls` file, choose **Document modules** to read the current document's embedded VBA project, or choose **New module**. The module selector marks unsaved source with `*`.

VBA keywords, strings, numbers and comments have distinct colors. **Save module…** writes source through the same atomic-save helper as document saves. **Save .docm…** updates this document’s VBA source project and atomically saves the document. The helper preserves unedited modules, project/reference records, form/designer streams, empty storages and OLE directory metadata. It removes compiled caches according to MS-OVBA so source and cached code cannot diverge. Editing a signed project requires explicit confirmation to remove its invalidated signature. A no-op source update preserves the original project bytes. **Revert changes** restores the last saved/imported source. Closing the editor retains its buffers; closing the application requires saving or reverting dirty modules. Switching documents retains the open source buffers.

**Check** and **Transpile** call the existing optional VBA/.NET component on a background worker. The helper reads embedded project sources through its `modules-json` command. Source files are limited to 2 MiB, embedded projects to 16 MiB, and helper jobs to 90 seconds. Temporary source folders are private on Unix and removed after each job. The helper can be installed as `wcvba`, supplied through `NISABA_VBA`, or run from this checkout with the installed .NET SDK.

## Project explorer, designer and chat

The workspace has **Code**, **Designer** and **Chat** tabs beside a project explorer. The explorer
groups the current document's embedded document modules, standard modules, classes and forms;
it also keeps modules from other open documents accessible. Imported source paths appear on hover,
and unsaved sources carry `*`. Embedded modules load automatically when opening the workspace for a document with VBA. You can also reload them, open source files or add a module there.
This is a source/project view, not a recursive filesystem browser or a new solution file format.

**Designer** uses the existing live document page as its surface. The **Controls toolbox** at the top uses original icon-only buttons, with names in tooltips and accessibility labels. It exposes text fields, rich text, check boxes, drop-down lists, combo boxes, date pickers, pictures, building blocks and repeating sections. Drag a control onto the visible page to insert it at the nearest body-text position, or click to insert at the document caret. Design Mode and Control Properties use the existing Developer commands. Controls follow document text flow; this is not absolute-position UserForm placement. Dropping outside a visible page cancels insertion. Select paragraph nodes (including
nested table-cell paragraphs), inspect their properties, change formatting/styles, replace selected
text or insert paragraphs and 2×2 tables. Tables and drawing-object counts are visible. Edits use
normal document commands and undo. This is a document designer; visual VBA UserForm design remains open.

**Chat** uses the local model selected in Scribe, independently of the Phi-4 completion action.
Ask how to automate the document or request code. Each request supplies bounded body text (up to
8000 characters, 128 paragraph nodes), bounded selected-module source and recent conversation.
Headers/footers and notes are not included. Switching documents clears the conversation.
Completed replies are shown for review; incomplete reasoning output is refused and completed reasoning sections are removed before code extraction; a complete fenced VBA snippet can be inserted at the code cursor
captured when the request was sent. Insertion refuses changed document revisions or module source.
Generated code is never automatically run. Use **Check** afterward, then save the source or `.docm`.

## Local completion

Download **Phi-4 mini** through Scribe's **Download a model** catalog first. Place the code caret and choose **Suggest code**. Inference uses the same local CPU backend as Scribe. It receives bounded source before and after the caret, supported object-model names and the selected document text. Review the suggested code, then choose **Insert completion** or **Discard completion**. A suggestion is refused if its target source changed while inference ran. **Cancel** stops the background job between native operations.

## Document model

Enable **Document model** for a live view of body paragraphs, including paragraphs in nested table cells. Select a node to select its text in the document. Bold, Italic and Inspect properties use the normal engine commands and document undo behavior. Traversal is bounded to keep large or deeply nested documents responsive. The existing .NET Word object-model facade is described in [the VBA component README](../vba/README.md).

## VBA access to form controls

Macros can use the existing main-body controls through `ActiveDocument.ContentControls`,
`SelectContentControlsByTag` and `SelectContentControlsByTitle`. Collections support `Count`,
one-based indexing, string ID lookup and `For Each`. Each control exposes `ID`, `Title`, `Tag`,
`Range`, `Checked` (check boxes), `LockContents` and `LockContentControl`.

```vb
Sub FillCustomer()
    ActiveDocument.SelectContentControlsByTag("customer")(1).Range.Text = "Cedar Harbor"
    ActiveDocument.SelectContentControlsByTag("approved")(1).Checked = True
End Sub
```

Controls are resolved by ID on access, with a document-identity guard. Missing, duplicate or
removed IDs and stale document references are refused; duplicate titles/tags remain valid
collections. Text edits and check-box updates use the normal engine commands, locks and undo.
`document.contentControls` is a pure query returning body control metadata and content positions;
more than 4096 controls produces a truncated response which the VBA bridge refuses.

This implements a subset of the [published Word content-control API](https://learn.microsoft.com/en-us/office/vba/word/concepts/working-with-word/working-with-content-controls).
Headers/footers, macro-created controls, list-entry/date APIs and control events are not implemented
in this facade. GUI execution and UserForms remain separate work; use the existing CLI runner.

## Current limits

Source that cannot be encoded in the existing project’s code page is refused without changing the original. Unsupported directory layouts, module-kind changes, new document modules and new form designers are refused. Existing form/designer data is preserved; there is no visual form designer. There is no GUI macro-execution button: the existing CLI runtime and its security model remain separate. UserForms, debugging, full IntelliSense, project-reference editing and automatic completion on every keystroke are outside this initial workbench. Completion quality depends on Phi-4; generated suggestions require review.

## Commands

`vba.editor {value?: bool}` toggles the pane. `ui.vba.workspace {tab?: "code"|"designer"|"chat"}` selects a view and reports conversation/proposal state. `ui.vba.chat {prompt}` requests contextual help or code; `ui.vba.acceptChat` inserts reviewed proposed code. UI/control clients can use `ui.vba.modules`, `ui.vba.add {name?}`, `ui.vba.import {path}`, `ui.vba.select {module}`, `ui.vba.setSource {module?, source}`, `ui.vba.save {module?, path?}`, `ui.vba.revert {module?}`, `ui.vba.saveDocument {path, removeSignature?}`, `ui.vba.loadDocument`, `ui.vba.check`, `ui.vba.transpile`, `ui.vba.complete`, `ui.vba.acceptCompletion` and `ui.vba.dom {value?: bool}`. `vba.setProject {project: base64, document?, revision?, removeSignature?}` applies a prepared binary project as one undoable engine command; it guards against stale documents and invalidated signatures. The `ui.*` commands require the desktop UI/control bridge.

## Clean-room compatibility

See the [implementation provenance record](implementation-provenance.md) for
public references, the targeted inspection findings and their limits.

The implementation and assets are original. Interoperability work uses published specifications and documented Word APIs, with synthetic source and document fixtures. No Microsoft application code or assets are imported. File preservation and macro execution compatibility are separate: the current transpiler/object model still has the limitations listed in [the VBA README](../vba/README.md).

Format references: [MS-OVBA](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ovba/ef7087ac-3974-4452-aab2-7dba2214d239) and [MS-CFB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/53989ce4-7b05-4f8d-829b-d08d6148375b). Synthetic round-trip tests cover source replacement, adding standard modules, preserved opaque records/designers/OLE metadata and encoding refusal. Native Microsoft Word execution and repair-free round trips have not been validated.

## Validation on 2026-10-26

- Form automation: 37 .NET tests pass on the real headless engine; FakeEngine passes 35 with the two real-only form tests explicitly skipped. Coverage includes Unicode field values, check-box state, duplicate titles/tags, stable IDs, metadata edits, locks, missing/removed controls, document switching, undo and DOCX/DOCM preservation.
- An embedded `FillCustomer.Main` macro filled the customer field and ticked approval in a synthetic `.docm`; XML inspection confirmed both values and a byte-identical VBA project after saving. Eleven engine control tests, Clippy and the 18-crate WASM check passed. Native Word execution remains unverified.

- A real local chat test read the company name Cedar Harbor from the document, generated the requested VBA, inserted it on review and passed compilation. Its macro executed through the real headless engine and saved exactly the expected text.
- A real completion filled a missing string expression with `"Hello, World!"`, preserving surrounding source exactly. The completed macro compiled, executed and saved exactly the expected text.
- The first chat attempt mixed context labels into the suggested text. Requests now delimit document and module context as JSON data; the same task passed on retest. Six focused regressions, Clippy and the desktop build passed. These are headless UI/control and runtime checks, not live desktop or native Word validation.

## Validation on 2026-10-25

- An actual headless pointer-drag regression moves a Text field from the toolbox onto the page, verifies one insertion and one undo step, and checks both DOCX and DOCM save/reopen preservation. The controls toolbox is shown in the inspected dark-theme designer screenshot.

- A real local chat request produced a complete `CountParagraphs` VBA procedure, the review action inserted it into the source module, and the optional VBA checker compiled it successfully. The chat-model setting and Phi-4 completion action remain independent.
- Workspace validation: six focused tests plus the real optional-helper test passed; Clippy, formatting, assets, layering and the 18-crate WASM check passed. Headless screenshots cover dark-theme Code, Designer and Chat views; opening a saved `.docm` automatically loaded its embedded Cleanup module in the explorer.

- Real local Phi-4 mini completion generated an expression, inserted it at the requested code caret, and the resulting VBA passed the existing checker.
- A headless editor session loaded embedded modules, changed a module, added one, saved `.docm`, and reopened both sources with the document text preserved. Exporting `.bas` preserved the source exactly.
- 35 .NET tests pass against both FakeEngine and the real Linux headless DraftHearth engine. Embedded Cleanup macros execute, save/reopen with text and formatting preserved, and execute again from saved source; separate CLI runs preserve the binary VBA project exactly.
- 35 .NET synthetic/FakeEngine tests, four editor tests (including the optional real helper check), and the undo/signature engine regression passed. Formatting, Clippy, asset attribution, dependency layering and the 18-crate WASM check passed.
- Full workspace validation is still blocked by four font/layout tests outside this change. Native Word, native Windows/macOS runtime behavior, form-design editing and GUI macro execution were not validated.

## Revision history

| Date | Change |
|---|---|
| 2026-10-26 | Connected VBA to body form controls with stable IDs, text/check-box access, metadata and lock properties; fixed chained collection assignments and tested embedded DOCM execution. |
| 2026-10-26 | Tested real chat and completion through insertion, compilation and execution; separated document/source metadata in chat requests after finding context leakage. |
| 2026-10-25 | Form controls toolbox uses the existing original control icons, preserving tooltips, accessible names and drag/drop. |
| 2026-10-25 | Restored access to the existing Developer form controls in the designer and added drag/drop insertion onto the page, Design Mode and Properties. |
| 2026-10-25 | Added a grouped project explorer, document designer/toolbox and local document-aware VBA chat; Phi-4 completion stays in Code. |
| 2026-10-25 | Validated headless macro execution, saved-document formatting, preserved project bytes and rerunning saved embedded source; desktop GUI/native Word execution remain open. |
| 2026-10-25 | Added the source workbench, .bas/.docm saving, project preservation, syntax colors, live document inspection and tested local Phi-4 completion. |
