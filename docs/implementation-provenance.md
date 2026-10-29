# VBA and assistant implementation provenance

Status: targeted source inspection, 2026-10-26. This records the evidence available
for the VBA and local assistant additions.

## Public implementation references

| Area | Public basis | Repository evidence |
|---|---|---|
| VBA project storage, module records and source compression | Microsoft's published [MS-OVBA specification](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ovba/575462ba-bf67-4190-9fac-c275523c75fc) | [`vba/src/WordCraft.Vba/Ole/`](../vba/src/WordCraft.Vba/Ole/), including specification-section references in the compression and project-editing code |
| OLE compound-file storage | Microsoft's published [MS-CFB specification](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/53989ce4-7b05-4f8d-829b-d08d6148375b) | [`CompoundFile.cs`](../vba/src/WordCraft.Vba/Ole/CompoundFile.cs) |
| Word document automation interface | The publicly documented [Word VBA object model](https://learn.microsoft.com/en-us/office/vba/api/overview/word) and [content-control APIs](https://learn.microsoft.com/en-us/office/vba/word/concepts/working-with-word/working-with-content-controls) | [`vba/src/WordCraft.ObjectModel/Om/`](../vba/src/WordCraft.ObjectModel/Om/) maps supported operations onto this application's command protocol |
| VBA compilation | The publicly distributed .NET SDK and [Roslyn compiler](https://github.com/dotnet/roslyn) | [`Transpiler.cs`](../vba/src/WordCraft.Vba/Transpiler.cs), [`MacroCompiler.cs`](../vba/src/WordCraft.Vba/MacroCompiler.cs) and the SDK references in the VBA project files |
| Source editor and document designer | Original application code using the repository's document model, command registry and egui interface | [`vba_editor.rs`](../crates/ui-egui/src/vba_editor.rs), [`vba_workspace.rs`](../crates/ui-egui/src/vba_workspace.rs) and the [workspace guide](vba-editor.md) |
| Local assistant inference and downloads | Publicly available llama.cpp bindings and Hugging Face model distribution interfaces | [`local.rs`](../crates/sharedai/src/local.rs), [`models.rs`](../crates/sharedai/src/models.rs) and [`assistant_pane.rs`](../crates/ui-egui/src/assistant_pane.rs) |

The VBA tests and samples use synthetic document content and example macros. See
[`vba/tests/`](../vba/tests/) and [`vba/samples/`](../vba/samples/).
Asset sources and licences are recorded separately in [`ATTRIBUTION.md`](../ATTRIBUTION.md).

## Inspection findings and limits

The targeted inspection covered the VBA runtime and object-model sources, their
samples and tests, the VBA editor/workspace, local inference/download code, and
the associated documentation and asset attribution. The inspected work is
consistent with implementation from public format specifications, documented
APIs and original application code. The feature requests supplied for this work
described desired behavior.

This finding is an observation about the inspected material, not proof of the
origin of every line. Inherited repository history and all third-party
dependencies have not received a complete provenance audit. Specification and
API names identify interoperability targets; compatibility claims still require
behavioral testing. In particular, synthetic tests do not establish native Word
execution compatibility or repair-free round trips.

## Maintaining this record

For future interoperability changes, cite the public specification or API used,
keep implementation and fixtures original or appropriately licensed, and record
asset provenance in `ATTRIBUTION.md`. Follow the clean-room rules in
[`AGENTS.md`](../AGENTS.md), and update this note when new evidence changes the
scope or findings.

## Revision history

- 2026-10-26: Recorded the targeted inspection, public references and evidence limits.
