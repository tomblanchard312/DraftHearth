# Nisaba

**A fast, open-source word processor written in Rust, for documents, writing and design.** The
ribbon, styles, tables, track changes, references and mail merge; it reads and writes .docx, opens
.odt, .rtf, .html, .md and .txt, exports PDF, and can be driven by AI agents over MCP.

Nisaba is a fork of [WordCraft](https://github.com/storytold/wordcraft) by the ArtCraft team and
community. It follows WordCraft closely and adds what a desktop built around local AI needs:

- **A built-in assistant** through [SharedAI](https://github.com/tomblanchard312/SharedAI), the
  per-user local model service: rewrite and draft without anything leaving the machine. Suggestions
  arrive as tracked changes you accept or reject (Review › Assistant; see [docs/assistant.md](docs/assistant.md)).
- **Word VBA macros** (optional, .NET): macros from .docm/.doc files or exported modules run against Nisaba's command protocol, behind an allow-list ([vba/](vba/README.md)).
- **A FreeBSD and KDE Plasma home**: packaging and defaults for a FreeBSD desktop distribution,
  alongside the Linux, macOS, Windows and web builds WordCraft already has.

Nisaba keeps WordCraft's internal names (crates, commands, environment variables) so upstream
improvements merge cleanly; the product's name, app id and settings folders come from
[`crates/brand`](crates/brand/src/lib.rs).

## Build and run

```sh
cargo run --release -p wordcraft -- --sample          # the desktop app with the sample document
cargo run --release -p wordcraft-cli -- convert in.docx out.pdf
```

Packages install the programs as `nisaba` and `nisaba-cli`. Settings live in `~/.config/nisaba`
(macOS `~/Library/Application Support/Nisaba`, Windows `%APPDATA%\Nisaba`), separate from an
installed WordCraft.

Contributor rules (never crash, no Microsoft assets, clean room, quality gates) are WordCraft's and
apply here unchanged: see [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md). The detailed feature,
format and parity documentation is in [docs/](docs/).

## License and credits

MIT OR Apache-2.0, at your option. WordCraft is copyright (c) 2026 ArtCraft Team and the WordCraft
contributors; Nisaba's changes are copyright (c) 2026 Tom Blanchard. See [NOTICE](NOTICE) and
[ATTRIBUTION.md](ATTRIBUTION.md) for bundled material. Nisaba is not made, sponsored or endorsed by
the ArtCraft Team, and contains none of the ArtCraft marks.

The Nisaba icon is original artwork for this repository: a clay tablet with wedge-shaped marks and
a reed stylus, after Nisaba, the Sumerian goddess of writing and patron of scribes.
