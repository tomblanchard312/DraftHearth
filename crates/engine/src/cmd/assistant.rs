//! Nisaba's Assistant: put a model's suggestion into the document as tracked changes.
//!
//! The model runs elsewhere (the SharedAI daemon, driven by the Assistant pane in the UI); the
//! engine only applies the result, so scripts, the CLI and MCP agents get the same behaviour.
//! Suggestions always land as revisions by a separate author, so the writer reviews them with
//! Review › Accept / Reject like any colleague's edits.

use crate::{CmdError, CmdResult, CommandSpec, Selection, Session, p};
use serde_json::{Value, json};

/// The revision author for applied suggestions.
pub fn author() -> String {
    format!("{} Assistant", wordcraft_brand::NAME)
}

/// The largest suggestion accepted in one call (a long chapter, not a whole book).
pub const MAX_TEXT_BYTES: usize = 1 << 20;

pub fn specs() -> Vec<CommandSpec> {
    vec![
        CommandSpec::new("view.assistantPane", "Assistant", "Review › Assistant", |s, v| {
            let cur = s.view.assistant_pane;
            s.view.assistant_pane = p::bool(v, "value").unwrap_or(!cur);
            Ok(json!({"value": s.view.assistant_pane}))
        })
        .params(r#"{"value"?: bool}"#)
        .pure(),
        CommandSpec::new("assistant.settings", "Assistant Settings", "Review › Assistant", settings)
            .params(r#"{"socket"?: string (SharedAI socket path; "" for the default), "model"?: string (model id; "" for the first registered)}"#)
            .pure(),
        CommandSpec::new("assistant.apply", "Apply Suggestion", "Review › Assistant", apply).params(
            r#"{"text": string (plain text; blank lines become paragraphs), "mode"?: "replace" (the selection, default) | "after" (inserted after the selection)}"#,
        ),
    ]
}

/// The SharedAI socket and model the Assistant pane uses (saved with the other preferences).
fn settings(s: &mut Session, v: &Value) -> CmdResult {
    let clean = |t: &str| t.trim().chars().filter(|c| !c.is_control()).take(1024).collect::<String>();
    if let Some(t) = p::str(v, "socket") {
        s.prefs.assistant_socket = clean(t);
    }
    if let Some(t) = p::str(v, "model") {
        s.prefs.assistant_model = clean(t);
    }
    Ok(json!({"socket": s.prefs.assistant_socket, "model": s.prefs.assistant_model}))
}

fn apply(s: &mut Session, v: &Value) -> CmdResult {
    let text = p::str(v, "text").ok_or_else(|| CmdError::Params("text: the suggestion to apply".into()))?;
    if text.len() > MAX_TEXT_BYTES {
        return Err(CmdError::Params(format!("text: at most {} KiB", MAX_TEXT_BYTES >> 10)));
    }
    let text = text.replace("\r\n", "\n");
    if text.trim().is_empty() {
        return Err(CmdError::Params("text: the suggestion is empty".into()));
    }
    let after = match p::str(v, "mode") {
        None | Some("replace") => false,
        Some("after") => true,
        Some(_) => return Err(CmdError::Params(r#"mode: "replace" | "after""#.into())),
    };
    if after {
        let (_, end) = s.sel.ordered();
        s.sel = Selection::caret(end);
    }
    let (track, writer) = (s.doc.settings.track_changes, std::mem::replace(&mut s.author, author()));
    s.doc.settings.track_changes = true;
    let r = s.run("edit.pasteSpecial", &json!({"as": "text", "text": text}));
    s.doc.settings.track_changes = track;
    s.author = writer;
    r.map(|_| json!({"applied": true, "author": author(), "mode": if after { "after" } else { "replace" }}))
}

#[cfg(test)]
mod tests {
    use super::*;
    use wordcraft_doc::RevisionKind;

    fn session(text: &str) -> Session {
        let mut s = Session::new(wordcraft_doc::Document::new());
        s.run("text.insert", &json!({"text": text})).unwrap();
        s
    }

    #[test]
    fn a_rewrite_replaces_the_selection_as_tracked_changes_by_the_assistant() {
        let mut s = session("The quick brown fox.");
        s.run("select.text", &json!({"text": "quick brown"})).unwrap();
        s.run("assistant.apply", &json!({"text": "slow grey"})).unwrap();
        assert!(!s.doc.settings.track_changes, "the writer's Track Changes setting is kept");
        assert_ne!(s.author, author(), "the writer's name is put back");
        let mine: Vec<_> = s.doc.revisions.iter().filter(|r| r.author == author()).map(|r| r.kind).collect();
        assert!(mine.iter().any(|k| matches!(k, RevisionKind::Insert)), "{mine:?}");
        assert!(mine.iter().any(|k| matches!(k, RevisionKind::Delete)), "{mine:?}");
        s.run("review.rejectAll", &json!({})).unwrap();
        assert_eq!(s.doc.plain_text(wordcraft_doc::StoryRef::Body).trim_end(), "The quick brown fox.");
    }

    #[test]
    fn accepting_gives_the_suggestion_and_undo_is_one_step() {
        let mut s = session("Hello wrld.");
        s.run("select.text", &json!({"text": "wrld"})).unwrap();
        s.run("assistant.apply", &json!({"text": "world"})).unwrap();
        s.run("edit.undo", &json!({})).unwrap();
        assert_eq!(s.doc.plain_text(wordcraft_doc::StoryRef::Body).trim_end(), "Hello wrld.", "one undo removes the whole suggestion");
        assert!(s.doc.revisions.iter().all(|r| r.author != author()) || !s.doc.settings.track_changes);
        s.run("edit.redo", &json!({})).unwrap();
        s.run("review.acceptAll", &json!({})).unwrap();
        assert_eq!(s.doc.plain_text(wordcraft_doc::StoryRef::Body).trim_end(), "Hello world.");
    }

    #[test]
    fn after_mode_keeps_the_selection_and_paragraphs_split() {
        let mut s = session("First.");
        s.run("select.text", &json!({"text": "First."})).unwrap();
        s.run("assistant.apply", &json!({"text": "\nSecond.\nThird.", "mode": "after"})).unwrap();
        s.run("review.acceptAll", &json!({})).unwrap();
        let body = s.doc.plain_text(wordcraft_doc::StoryRef::Body);
        assert!(body.starts_with("First."), "{body:?}");
        assert!(body.contains("Second.") && body.contains("Third."), "{body:?}");
    }

    #[test]
    fn bad_params_are_refused_without_changing_anything() {
        let mut s = session("Keep me.");
        for v in [json!({}), json!({"text": "  "}), json!({"text": "x", "mode": "sideways"}), json!({"text": "x".repeat(MAX_TEXT_BYTES + 1)})] {
            assert!(s.run("assistant.apply", &v).is_err(), "{v}");
        }
        assert!(s.doc.revisions.is_empty());
        assert_eq!(s.doc.plain_text(wordcraft_doc::StoryRef::Body).trim_end(), "Keep me.");
    }

    #[test]
    fn settings_are_kept_clean() {
        let mut s = Session::new(wordcraft_doc::Document::new());
        let r = s.run("assistant.settings", &json!({"socket": " /run/user/1/sharedai/ipc.sock\n", "model": "llama-3.2-3b"})).unwrap();
        assert_eq!(r, json!({"socket": "/run/user/1/sharedai/ipc.sock", "model": "llama-3.2-3b"}));
        assert_eq!(s.run("assistant.settings", &json!({})).unwrap(), r);
    }

    #[test]
    fn the_pane_toggles() {
        let mut s = Session::new(wordcraft_doc::Document::new());
        assert_eq!(s.run("view.assistantPane", &json!({})).unwrap(), json!({"value": true}));
        assert_eq!(s.run("view.assistantPane", &json!({"value": false})).unwrap(), json!({"value": false}));
    }
}
