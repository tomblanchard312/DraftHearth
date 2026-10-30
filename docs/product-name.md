# Qanloom product name

DraftHearth (previously Nisaba) is now **Qanloom**, effective 2026-10-30.
The modern coined name is inspired by the Mesopotamian reed stylus.

Window titles, About/help, exports, VBA Application.Name, translated product labels,
installer display names, desktop entries and project links use Qanloom.
The approved blue-and-gold document-and-stylus icon is retained with a new wordmark.

The GitHub repository is `tomblanchard312/Qanloom`. Existing `Nisaba` app IDs,
settings/recovery folders, environment variables, command namespaces, upgrade GUIDs,
file-association ProgIds and registry identifiers stay stable (including the existing
`RegisteredApplications` value name `DraftHearth`; its displayed name is Qanloom). Saved documents and
Enki model data are not moved. Upgrades retain remembered/custom installation paths.
New Windows install folders and macOS bundles use Qanloom.

Local launchers use `qanloom`; the previous `drafthearth` launcher remains an alias.
Original upstream copyrights, licences and attribution are retained.

## Validation

All nine `cargo xtask ci` gates pass, including the workspace tests, interface audit
and WASM. The desktop and CLI build, the complete self-contained VBA runtime stages
successfully, and 37 real-engine VBA tests pass. The packaged helper reports
`Application.Name = Qanloom`. Windows packaging-script checks and package metadata
parse checks pass; foreign native installer execution remains unverified.

An isolated native Linux window reports `Document1 - Qanloom`. Its `_NET_WM_ICON`
pixels match the approved blue-and-gold artwork exactly, and its `WM_CLASS` matches
the stable desktop-entry identity. Enki generation, compiler-checked replacement,
Undo and the unsaved-VBA close prompt pass in that window. Existing user windows
and documents are left untouched.
