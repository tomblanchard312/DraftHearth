# Scribe, the writing assistant

> **Last updated:** 2026-10-26 · **Change:** approved Scribe artwork embedded in the ribbon and assistant pane; safer suggestion targeting and caret continuation retained.

Scribe suggests rewrites and new text using a language model that runs on the user's own computer. Nothing is sent to a cloud service. Suggestions go into the document as **tracked changes** by "Scribe", so they are accepted or rejected on the Review tab like a colleague's edits.

Scribe’s blue folded-page S and wedge-cut reed icon is embedded in the Review ribbon and the
assistant pane. The original transparent artwork is recorded in [ATTRIBUTION.md](../ATTRIBUTION.md).

Scribe runs GGUF chat models directly in Nisaba using llama.cpp on the CPU. Download a catalog model or choose a local model file to get started. The assistant name remains `wordcraft_brand::ASSISTANT_NAME`.

## Using it

1. Choose **Review › Scribe**. The first time, Nisaba lists its local model files in the background ("Starting Scribe…").
2. With no model yet, the pane says "Scribe needs a language model before it can write" and offers **Choose Model File…**. Choose a GGUF chat model compatible with the bundled runtime, or download a catalog model under **Settings**. Nisaba copies the file into its models folder (`~/.local/share/nisaba/models`, private to the user), and selects it. Existing copied GGUF models appear automatically.
3. Select some text and choose **Improve**, **Shorten**, **Fix grammar** or **More formal**, or type a request and choose **Send** (Ctrl+Enter, or Cmd+Enter on macOS). **Continue** writes a concise next paragraph after the selection, or uses text before the document caret when nothing is selected. On an empty body paragraph it can use the previous paragraph.
4. The final answer streams into the pane; internal reasoning prefixes are withheld. **Stop** ends generation. Stopped, failed, empty or length-limited results cannot be applied; visible draft text can still be copied.
5. **Replace** swaps the selection for the suggestion, **Insert** adds it after the selection, **Copy** puts it on the clipboard and **Discard** drops it.

Replace targets the text selected when the request was made, even if only the selection moved. Any document edit or switch makes the suggestion stale and requires a fresh request. Scribe never falls back to replacing a different selection. Selections over 24,000 characters are refused rather than silently truncated. Requests are limited to 4,000 characters.

Model selection, downloads and file import are grouped under **Settings**. Routine writing controls show the task rather than model names. An explicitly selected existing external model path is retained during discovery. Failures read plainly ("Scribe is busy. Try again in a moment.", "That's more text than Scribe can read at once. Select less."); the technical detail goes to Nisaba's log.

## Downloading models

Like FileExplorer, **Download a model** lists fixed models with their size, license and Hugging Face page. Qwen3 4B, Gemma 3 4B, Phi-4 mini and Llama 3.2 3B are pinned to exact revisions, sizes and SHA-256 hashes. Downloading shows progress and supports Cancel; only a verified file is installed and selected. Incomplete downloads restart from the beginning. Catalog models are checked again before inference.

For a gated repository, accept its access terms on Hugging Face and enter a read token. It is held only for the download, never saved; `HF_TOKEN` is also supported. Flatpak builds allow network access for these downloads. Inference stays local. Llama 4 is not in the small-model catalog.

## Local inference

Models stay in the app's data folder. Inference runs on a worker thread with an 8,192-token context, at most 1,024 generated tokens per UI request and a six-minute decode limit. Stop is checked between prompt batches and generated tokens; loading a model and native decode calls must finish before cancellation takes effect. Models are loaded for each request and released afterward.

Old socket preferences remain readable for compatibility and are ignored. Previously registered models outside Nisaba's models folder must be chosen again. Browser builds keep the assistant disabled.

## For scripts and agents

| Command | Params | Does |
|---|---|---|
| `view.assistantPane` | `{"value"?: bool}` | Shows or hides the pane |
| `assistant.settings` | `{"socket"?: string, "model"?: string}` | Sets and returns the GGUF model path and legacy socket preference (ignored) |
| `assistant.apply` | `{"text": string, "mode"?: "replace" \| "after"}` | Puts `text` into the document as tracked changes by "Scribe", in one undo step. The writer's Track Changes setting and author name are left as they were. |
| `ui.assistant.downloadModel` | `{"model": "qwen3-4b" \| "gemma3-4b" \| "phi4-mini" \| "llama3.2-3b"}` | Downloads and selects a pinned model |
| `ui.assistant.addModel` | `{"path": string}` | Adds a GGUF model file, as Choose Model File… does (in the background) |
| `ui.assistant.ask` | `{"action"?: "improve" \| "shorten" \| "grammar" \| "formal" \| "continue", "prompt"?: string}` | Starts the same writing request as the pane |
| `ui.assistant.state` | `{}` | Returns busy/ready state, visible reply and status |
| `ui.assistant.apply` | `{"mode"?: "replace" \| "after"}` | Applies a complete suggestion to its captured, unchanged document target |
| `ui.assistant.stop` / `ui.assistant.discard` | `{}` | Stops generation or discards the suggestion |

An MCP agent with its own model can call `assistant.apply` to propose edits for review the same way.

## Validation on 2026-10-26

Nine Scribe UI/state tests and six engine application tests pass. Real local inference corrected
subject/verb agreement while preserving shipment details; Shorten retained the date, capacity,
hours, cost and volunteer caveat. Suggestions were applied as Scribe revisions; rejecting the
grammar rewrite restored the exact source. Continue worked with no selection and preserved the
original as a separate paragraph. The first continuation was overly verbose; the prompt now asks
for concise prose matched to the source. Style and instruction adherence still need broader evaluation.

Headless dark-theme screenshots were inspected. Clippy and the 18-crate WASM check passed.
These are local headless UI/runtime checks, not native GUI or cross-platform acceptance.

## How it's built

- `crates/sharedai/src/local.rs`: bounded in-process CPU inference with llama.cpp; the old protocol client remains available but the pane no longer calls it.
- `crates/engine/src/cmd/assistant.rs`: settings and tracked-change application.
- `crates/ui-egui/src/assistant_pane.rs`: local model discovery, import, streaming and cancellation on worker threads.

Not yet: GPU acceleration, model caching between requests or conversation history.

## Revision history

| Date | Change |
|---|---|
| 2026-10-26 | Embedded the approved blue S/reed-tip icon, cached per UI context; inspected light/dark headless renders. |
| 2026-10-26 | Guarded stale/partial suggestions and oversized selections; separated source data in prompts, added concise caret continuation, grouped model setup in Settings and verified real writing/review flows. |
| 2026-10-25 | Replaced the daemon dependency with in-process CPU GGUF inference and a checksum-pinned Hugging Face download catalog. |
