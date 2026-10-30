# Enki speech SDK snapshot

Client, protocol and optional consumer-side desktop controls from tomblanchard312/Enki revision `b19684050373882fc7ebbe13331ba2c57a4553cb`. This follows the existing public client snapshot convention; no daemon, native inference worker, credentials or models are included.

Source logic is unchanged; Rust formatting follows the containing workspace. Manifests spell out their inherited version, edition, license and dependency fields so consumers do not need the Enki workspace. The original `UNLICENSED` metadata is retained; the containing app license does not replace it. Copyright holders retain their rights.

Refresh all three directories together from a tested Enki revision. Keep the daemon updated separately for speech protocol versions 4 and 5. Consumer apps pin the Qanloom commit containing this snapshot.
