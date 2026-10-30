# Upstream integration

> **Last updated:** 2026-10-30 · **Status:** Integration through `5b5810fe`, with fork branding/Enki/VBA retained and patched ZIP/Tokio dependencies. Current validation is recorded in the 2026-10-30 section below; older sections describe earlier refreshes.


## 2026-10-30 integration

WordCraft `5b5810fe1f6f815acc3fda27619f61d9bc08b615` brings 54 upstream commits
onto DraftHearth `ff0eea9b`. All upstream history is retained. Twelve conflicted files
were reconciled, keeping both projects' contributor entries and progress records.

- Keep DraftHearth's name, app IDs, original icons, release links and hidden Discord default.
  Do not re-enable the upstream Windows self-updater or its WordCraft identity. The unused
  `craft-update` workspace dependency and its new upstream-only icon constant are omitted.
- Adopt upstream's optional saved-author preference, including a deliberately blank name.
- Snapshot the existing Enki image client/protocol under `vendor/`, retaining its original
  `UNLICENSED` designation. GitHub/clean-clone builds no longer require a sibling Enki
  source checkout; the daemon repository and installed application are untouched.
- Retain the Enki client, Scribe formatting safeguards, compiler-checked VBA module replacement,
  undo, unsaved-source close prompt, Ninimma picker and complete runtime staging.
- Include upstream's DOCX preservation, table copy/cut, font-size, Arabic Backspace,
  chart fallback fonts, parser bounds, connector performance and Flatpak printing fixes.

### Dependency investigation

The fresh RustSec audit initially reports three ZIP extraction advisories
(`RUSTSEC-2026-0367`, `RUSTSEC-2026-0368`, `RUSTSEC-2026-0369`) against `zip 3.0.0`.
DraftHearth reads bounded entries into memory rather than calling `ZipArchive::extract`
or `ZipStreamReader::extract`; the affected filesystem extraction APIs have no callers
in this workspace. Nevertheless, move the shared ZIP dependency to patched `9.1.3`
and validate the document import/export regression suites. Update Tokio from `1.53.2`
to `1.53.3` for `RUSTSEC-2026-0386` (AsyncFd I/O safety). Retain upstream's
`ctutils 0.4.7` security patch and Windows clipboard update.

Unmaintained transitive dependencies (`proc-macro-error`, `zbus_names`, `zvariant`,
`zvariant_derive`) have no patched versions listed by RustSec. These maintenance warnings
remain visible; they are not suppressed. Upstream's SonarCloud critical findings examined
in this review were cognitive-complexity warnings, not confirmed runtime failures.
RustSec results do not prove that every SonarCloud dependency-risk condition is resolved.

The ODT inflation-budget fixture now falsifies both ZIP headers; ZIP 9 rejects output
beyond the declared size before yielding payload bytes. The test checks that rejection,
retains declared-size refusal and repeated-use accounting, and confirms a legitimate
following picture remains readable. Production size limits are unchanged.

### Validation of this integration

- `cargo xtask ci`: all nine gates pass (formatting, runtime packaging, Clippy,
  workspace tests, interface audit, doctests, asset attribution, layers and WASM).
- Debug desktop and CLI binaries build successfully with the patched dependencies.
- VBA compatibility runner against the rebuilt real headless CLI: 37 passed,
  zero failed, zero skipped, including content-control edits and DOCM preservation.
- Isolated native Linux window with the existing Enki daemon: a generated module
  passes the bundled compiler, Replace module removes the previous source, Undo
  restores it, and closing dirty VBA shows Save / Don't Save / Cancel. Cancel keeps
  the source; Save opens the picker without exiting. The screenshot was inspected.
- A clean staged source export, with no sibling Enki checkout, resolves both Enki
  packages inside the repository. This is a dependency-resolution check, not a
  second complete clean-room compilation.
- Fresh RustSec database: zero known vulnerabilities, four unmaintained-package
  warnings; no advisory suppression was added.
- Existing fork/upstream contributor entries are preserved exactly. Enki source
  matches the previous local snapshot after repository formatting; the daemon
  repository and current installed app were not changed.

Native Windows/macOS/FreeBSD execution and installer acceptance were not rerun.
Hosted checks are separate from the local evidence above.

This integration brings WordCraft `438775ce` into DraftHearth, starting from `fa787519`.
At the recorded upstream tip, DraftHearth was 296 commits behind and 12 ahead. The existing local
Scribe/VBA work was checkpointed as `8a3fc5a8` before merging. The checkpoint is retained on
`codex/pre-upstream-20261026`; `codex/upstream-sync` holds the integration and local `main`
is fast-forwarded to the same merge after validation.

## Resolutions

- Adopt upstream's egui/eframe 0.37 migration, multi-document/native-window handling, input-method
  improvements, format/security fixes and shared-library pins.
- Register Scribe and the VBA editor with the new task-pane host, including docking and closing.
  Keep inference jobs and unsaved source buffers app-wide so window switches and closes retain them.
  Suggestions continue to guard their captured document and revision before application.
- Preserve DraftHearth's name, application IDs, settings, original icons, local inference and macro helper.
  Keep upstream's new Windows per-user installer support and notice packaging, using DraftHearth's
  existing upgrade/component GUIDs and package naming.
- Help's update menu links to DraftHearth releases. Upstream's automatic installer assumes ArtCraft
  bundle/package IDs, so automatic checking/installing is disabled until it supports this fork's IDs.
- Preserve DraftHearth's README and compatibility notes alongside upstream's progress entries.

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

## Upstream refresh

On 2026-10-26, fetched and merged the four commits added after upstream `2c99a427`, through
`02120c84` (merge commit `3abf9321`). Upstream's `tests_linear.rs` import fix overlapped local
changes; the merged test keeps its explicit imports and the local deterministic `proof_text`
step-count regression check, with matching instrumentation in `para.rs`.

`cargo xtask ci` passes all eight gates on this Linux host when bindgen is given GCC 13's include
directory (`BINDGEN_EXTRA_CLANG_ARGS=-I/usr/lib/gcc/x86_64-linux-gnu/13/include`). The optional
translation report is skipped because the `craft-i18n` catalog checkout is not installed.

## Additional upstream refresh

On 2026-10-26, merged the next three upstream commits after `02120c84`, through `f54ea731`
(merge commit `553545df`). This adds upstream's pinned nextest configuration and CI version check.
`origin/main` was inspected but left out of this merge because it also contains separate Scribe
backend/UI changes; the existing local FreeBSD and packaging work remains in the working tree.

## WordCraft modularization refresh

On 2026-10-28, merged WordCraft upstream through `c4fafc215490a0186ee750454b1ce73a9823daed`,
179 commits beyond the earlier shared point `f54ea731`. The merge commit is
`0a8734327440c57ff234cedc15ffff0def31d960`. This update modularizes large parts of the UI, engine,
document model, formats, layout, fonts and PDF code; adds rendering/layout fixes and stricter
checks; and changes Windows installer scope support. VBA and Scribe were ported into the new UI
structure, while DraftHearth identity and existing local work were retained.

The branch is 19 commits ahead of `upstream/main` and 500 commits ahead / 5 behind `origin/main`
at this recorded state. `origin/main` was not merged. Local verification after the refresh includes
`cargo fmt --all -- --check`, `git diff --check`, workspace `cargo check`, focused VBA/assistant/layout
tests (14 passed, one ignored), and the PowerShell packaging-script test. The full UI test run was
interrupted; a Windows MSI build was unavailable because WiX was not installed. These results do
not establish the full workspace CI, native Windows packaging, or platform acceptance.

## Revision history

| Date | Change |
|---|---|
| 2026-10-28 | Merge WordCraft through `c4fafc21`; record refreshed architecture, conflict ports and verification limits. |
| 2026-10-26 | Refresh through upstream `f54ea731`; all eight local CI gates pass. |
| 2026-10-26 | Refresh through upstream `02120c84`; all eight local CI gates pass. |
| 2026-10-26 | Resolve inherited test assumptions; full CI passes without disabling Thai. |
| 2026-10-26 | Record upstream integration, recoverable checkpoint, resolutions and local validation results. |
