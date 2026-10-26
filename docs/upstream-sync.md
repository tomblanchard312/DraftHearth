# Upstream integration

> **Last updated:** 2026-10-26 · **Status:** integration validated locally; four pre-existing font/layout failures keep full CI open.

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

- Full Linux workspace run: **4,618 passed, four failed, seven ignored**, with the long interface
  audit run separately. All four failures also reproduce using test binaries built before this
  merge (font fallback for mixed Thai/Latin, two Thai layout cases, and the multiple-spaced page-end case).
  They remain open; `cargo xtask ci` therefore does not pass.
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

## Revision history

| Date | Change |
|---|---|
| 2026-10-26 | Record upstream integration, recoverable checkpoint, resolutions and local validation results. |
