# Assistant (SharedAI)

Nisaba's Assistant suggests rewrites and new text using a model that runs on your own computer through [SharedAI](https://github.com/tomblanchard312/SharedAI). Nothing is sent to a cloud service. Suggestions go into the document as **tracked changes** by "Nisaba Assistant", so you accept or reject them on the Review tab like a colleague's edits.

## Using it

1. Start the SharedAI daemon and register a GGUF chat model (see SharedAI's `docs/INFERENCE.md` and `docs/REGISTRY.md`).
2. In Nisaba, choose **Review › Assistant**. The pane lists the daemon's models and picks the first one. Change it in the **Model** box.
3. Select some text and choose **Improve**, **Shorten**, **Fix grammar** or **More formal**, or type your own request and choose **Send** (Ctrl+Enter, or Cmd+Enter on macOS). **Continue** writes the next paragraph after the selection.
4. The reply streams into the pane. Choose **Stop** to end it early.
5. Choose **Replace** to swap the selection for the suggestion, or **Insert** to add it after the selection. **Copy** puts it on the clipboard. **Discard** drops it.

Replace targets the text you selected when you asked, as long as the document hasn't changed since. If it has, the current selection is used.

## Connection

The pane connects to the first of these that is set:

1. the socket path under **Connection** in the pane (saved with your preferences)
2. the `SHAREDAI_SOCKET` environment variable (the one the daemon itself reads)
3. `$XDG_RUNTIME_DIR/sharedai/ipc.sock`, the default on the Nisaba desktop

Messages in the pane say what went wrong: SharedAI not running, no models registered, the queue busy, or the model's own error (for example, a selection too long for its context).

## For scripts and agents

The engine part is ordinary commands, so the CLI, the control channel and MCP can use it too:

| Command | Params | Does |
|---|---|---|
| `view.assistantPane` | `{"value"?: bool}` | Shows or hides the pane |
| `assistant.settings` | `{"socket"?: string, "model"?: string}` | Sets and returns the socket and model (`""` means the default) |
| `assistant.apply` | `{"text": string, "mode"?: "replace" \| "after"}` | Puts `text` into the document as tracked changes by "Nisaba Assistant", in one undo step. Your own Track Changes setting and author name are left as they were. |

An MCP agent with its own model can call `assistant.apply` to propose edits for review in the same way.

## How it's built

- `crates/sharedai`: a small blocking client for SharedAI protocol v2 (`negotiate`, `list_models`, `chat` streaming, `cancel`), written from the published wire contract with `std` only. Frames are size-limited (64 KiB for v1, 4 MiB for v2) and chunk order is checked. It is unavailable on Windows and in the browser build, and says so.
- `crates/engine/src/cmd/assistant.rs`: the commands above.
- `crates/ui-egui/src/assistant_pane.rs`: the pane. Requests run on a worker thread and stream back over a channel, so the window never waits on the model.

Not yet: SharedAI's Windows named-pipe transport, conversation history across requests, and a document-wide "ask about this document" mode.
