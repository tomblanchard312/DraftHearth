# Upstream integration

> **Last updated:** 2026-10-26 · **Status:** integration validated locally; all eight CI gates pass after the font/pagination test follow-up.

This integration brings WordCraft `438775ce` into Nisaba, starting from `fa787519`.
At the recorded upstream tip, Nisaba was 296 commits behind and 12 ahead. The existing local
Scribe/VBA work was checkpointed as `8a3fc5a8` before merging. The checkpoint is retained on
`codex/pre-upstream-20261026`; `codex/upstream-sync` holds the integration and local `main`
is fast-forwarded to the same merge after validation.

## Resolutions

- Adopt upstream's egui/eframe 0.37 migration, multi-document/native-window handling, input-method
  improvements, format/security fixes and shared-library pins.
- Register Scribe and the VBA editor with the new task-pane host, including docking and closing.
  Keep inference jobs and unsaved source buffers app-wide so window switches and closes retain them.
  Suggestions continue to guard their captured document and revision before application.
- Preserve Nisaba's name, application IDs, settings, original icons, local inference and macro helper.
  Keep upstream's new Windows per-user installer support and notice packaging, using Nisaba's
  existing upgrade/component GUIDs and package naming.
- Help's update menu links to Nisaba releases. Upstream's automatic installer assumes ArtCraft
  bundle/package IDs, so automatic checking/installing is disabled until it supports this fork's IDs.
- Preserve Nisaba's README and compatibility notes alongside upstream's progress entries.

## Validation

- Initial Linux workspace run: **4,618 passed, four failed, seven ignored**, with the long interface
  audit run separately. All four failures also reproduce using test binaries built before this
  merge (font fallback for mixed Thai/Latin, two Thai layout cases, and the multiple-spaced page-end case).
  These test assumptions were corrected in the follow-up below.
- Final UI suite: **1,008 passed**, three ignored; the optional real VBA-helper check also passes separately.
  Includes pane docking/closing, unsaved module retention across windows, stale suggestions,
  accessibility and real pointer drag/drop with DOCX/DOCM preservation.
- .NET build: no warnings/errors. **37 tests passed** against the merged real headless CLI engine.
- Real local Scribe inference corrected agreement while retaining shipment facts; the saved DOCX
  contains Scribe-authored insertion/deletion revisions. Dark-theme Scribe and designer screenshots
  were inspected using the headless renderer.
- Formatting, Clippy across all targets, 49 asset attributions, 22-crate layering, 18-crate WASM
  checks and workspace doctests pass. Packaging shell syntax, WiX XML and fork GUID checks pass.
- Updated an upstream IME test to compare against its captured document ID, matching the new
  process-wide ID allocator. Added the designer field's accessible label for egui 0.37 and kept
  Unicode test data escaped so it is not mistaken for interface glyphs.
- The pseudo-language interface audit passes after translating workspace/update labels and
  treating exact fork identity/legal metadata as data. The build lacks optional craft-i18n
  catalogs, so the per-language missing-translation report is skipped.

Local Linux/headless checks do not establish native Windows/macOS/FreeBSD, Microsoft Word,
installer execution or hosted CI acceptance. No release or remote push is part of this integration.

## Font and pagination follow-up

The three Thai failures assumed that any installed Thai font also covered the entire mixed
Thai/Latin sample. This host has Thai-only fonts, so a single covering face is unavailable.
The tests now check whole-run selection when possible and verify character coverage through
fallback otherwise. Existing deterministic synthetic-font tests still require selection of a
complete installed face. Thai handling remains enabled; no new tests are skipped or ignored.

The pagination failure used a short sample and host-dependent default font metrics: at 1.15
spacing the paragraph fitted on one page. The fixture now uses bundled Source Sans 3 at 12 pt
and enough text to span pages, retaining all last-line/next-line boundary assertions.

`cargo xtask ci` passes all eight steps: formatting, Clippy, workspace tests (**4,623 passed,
zero failed, seven existing ignored**), interface audit, doctests, assets, layers and WASM.
These are test-only corrections; production rendering behavior and parity estimates are unchanged.

## Revision history

| Date | Change |
|---|---|
| 2026-10-26 | Resolve inherited test assumptions; full CI passes without disabling Thai. |
| 2026-10-26 | Record upstream integration, recoverable checkpoint, resolutions and local validation results. |
