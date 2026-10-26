# Scribe, the writing assistant

Scribe suggests rewrites and new text using a language model that runs on the user's own computer. Nothing is sent to a cloud service. Suggestions go into the document as **tracked changes** by "Scribe", so they are accepted or rejected on the Review tab like a colleague's edits.

Under the hood Scribe runs on [SharedAI](https://github.com/tomblanchard312/SharedAI), the per-user local model service. That is an implementation detail: the interface only ever says "Scribe", and never shows service names, socket paths or environment variables. The name is one constant, `wordcraft_brand::ASSISTANT_NAME` (with the `assistant_name!` macro for command labels).

## Using it

1. Choose **Review › Scribe**. The first time, Nisaba starts the model service in the background ("Starting Scribe…").
2. Models come from the one folder every app shares, `~/.local/share/SharedAI/models`: the model service registers any GGUF put there by itself (see SharedAI's `docs/MODELS.md`). Scribe keeps no models of its own. With none yet, the pane says so and offers **Open Models Folder**, then picks one up within seconds of it appearing. Scribe prefers a general instruct or chat model over ones made for documents, embeddings or images; with several, a **Model** box appears. If the service refuses a model because its file has gone (moved or deleted), Scribe says it is getting its model ready, sets that model aside for a minute and picks another or waits for the service to repair it; a file moved into the shared folder takes over its old registration.
3. Select some text and choose **Improve**, **Shorten**, **Fix grammar** or **More formal**, or type a request and choose **Send** (Ctrl+Enter, or Cmd+Enter on macOS). **Continue** writes the next paragraph after the selection.
4. The reply streams into the pane; **Stop** ends it early.
5. **Replace** swaps the selection for the suggestion, **Insert** adds it after the selection, **Copy** puts it on the clipboard and **Discard** drops it.

Replace targets the text selected when the request was made, as long as the document hasn't changed since; otherwise the current selection.

Reasoning models' thinking (`<think>…</think>`) is never shown or applied: the pane says "Thinking…" until the answer starts, and the request asks such models to skip thinking (`/no_think`). Failures read plainly ("Scribe is busy. Try again in a moment.", "That's more text than Scribe can read at once. Select less."); the technical detail goes to Nisaba's log.

## The model service

Nisaba finds the service, starting it when nothing is listening:

1. a socket set with `assistant.settings {"socket": …}` (for packagers and scripts; not in the interface)
2. otherwise the shared per-user socket, `SHAREDAI_SOCKET` or `$XDG_RUNTIME_DIR/sharedai/ipc.sock`, so every app on the desktop shares one service
3. inside a Flatpak, a host service on that shared socket when one is running; otherwise one on a private socket, `$XDG_RUNTIME_DIR/scribe/ipc.sock` in the sandbox, which still uses the shared models folder and registry in `~/.local/share/SharedAI` (`--filesystem=xdg-data/SharedAI`)

To start it, Nisaba looks for `sharedai-daemon` in `SHAREDAI_DAEMON`, then in its private helper folder `<prefix>/lib/nisaba/` (where the packages put it), then next to the program, then on `PATH`. The daemon keeps running after Nisaba quits (it unloads idle models after 10 minutes), so other apps and the next start reuse it.

Packaging: `SHAREDAI_BIN_DIR=<folder with sharedai-daemon and sharedai-worker> packaging/linux/package.sh` ships them in `usr/lib/nisaba/` in every Linux format; the Flatpak bundle scripts install them in `/app/lib/nisaba/`.

## For scripts and agents

| Command | Params | Does |
|---|---|---|
| `view.assistantPane` | `{"value"?: bool}` | Shows or hides the pane |
| `assistant.settings` | `{"socket"?: string, "model"?: string}` | Sets and returns the service socket and model (`""` means the default) |
| `assistant.apply` | `{"text": string, "mode"?: "replace" \| "after"}` | Puts `text` into the document as tracked changes by "Scribe", in one undo step. The writer's Track Changes setting and author name are left as they were. |
| `ui.assistant.addModel` | `{"path": string}` | Copies a GGUF file into the shared models folder, registers and selects it (in the background) |

An MCP agent with its own model can call `assistant.apply` to propose edits for review the same way.

## How it's built

- `crates/sharedai`: a small blocking client for SharedAI protocol v2 (negotiate, list and register models, chat streaming, cancel), written from the published wire contract with `std` and `sha2`. It also finds and starts the daemon. Frames are size-limited (64 KiB for v1, 4 MiB for v2) and chunk order is checked. Unavailable on Windows and in the browser build.
- `crates/engine/src/cmd/assistant.rs`: the commands above.
- `crates/ui-egui/src/assistant_pane.rs`: the pane. Starting the service, adding a model and writing run on worker threads and report back over a channel, so the window never waits.

Not yet: SharedAI's Windows named-pipe transport, conversation history across requests, a document-wide "ask about this document" mode, downloading models (the Flatpak has no network access by design), and using the GPU from the bundled CPU worker.
