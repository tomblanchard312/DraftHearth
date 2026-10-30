# Enki image client snapshot

`enki-client` and `enki-protocol` contain the client/protocol sources used by Qanloom's
existing Enki image integration. The source is copied without behavior changes (repository formatting applied) from the local
`tomblanchard312/Enki` checkout on 2026-10-30 (base commit `48abad7`, including local
image/protocol changes not yet published there). No daemon or inference implementation
is copied. The package manifests use explicit versions instead of inheriting Enki's
workspace, and the protocol path points to this snapshot.

Both packages retain Enki's existing `UNLICENSED` designation. Qanloom's MIT/Apache
licenses do not relicense this snapshot. No license grant is introduced by this copy.
Keep these files in sync with an explicitly reviewed Enki client revision; a clean clone
must not depend on an unpublished sibling checkout.
