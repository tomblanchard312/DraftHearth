//! Review › Assistant: suggestions from a model running on this machine through SharedAI.
//!
//! The pane sends the selection and an instruction to the SharedAI daemon on a worker thread
//! (`wordcraft_sharedai`), streams the reply into the pane as it is generated, and puts an
//! accepted reply into the document with `assistant.apply`, which records it as tracked changes
//! by "Nisaba Assistant". Nothing leaves the machine: SharedAI serves local models only.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, TryRecvError};

use egui::Ui;
use serde_json::json;

use crate::WordApp;
use crate::theme::{regular, semibold};

/// What the writer asks for. `None` is the free-form prompt.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Action {
    Improve,
    Shorten,
    Grammar,
    Formal,
    Continue,
}

impl Action {
    pub const ALL: [Action; 5] = [Action::Improve, Action::Shorten, Action::Grammar, Action::Formal, Action::Continue];

    pub fn label(self) -> &'static str {
        match self {
            Action::Improve => "Improve",
            Action::Shorten => "Shorten",
            Action::Grammar => "Fix grammar",
            Action::Formal => "More formal",
            Action::Continue => "Continue",
        }
    }

    fn instruction(self) -> &'static str {
        match self {
            Action::Improve => "Rewrite the text so it reads more clearly and smoothly.",
            Action::Shorten => "Rewrite the text to about half its length, keeping every key point.",
            Action::Grammar => "Correct the spelling, grammar and punctuation of the text. Change nothing else.",
            Action::Formal => "Rewrite the text in a more formal, professional tone.",
            Action::Continue => "Write the next paragraph that follows the text, in the same voice. Reply with the new paragraph only.",
        }
    }

    /// Continue adds text after the selection; the others replace it.
    pub fn mode(self) -> &'static str {
        if self == Action::Continue { "after" } else { "replace" }
    }
}

const SYSTEM: &str = "You are the writing assistant in a word processor. Reply with only the requested text: \
no preamble, no explanation, no quotation marks around it. Keep the author's meaning and language, \
and keep paragraph breaks as blank lines.";

/// The largest selection sent (characters); the daemon's context is the real limit.
pub const MAX_CONTEXT_CHARS: usize = 24_000;

/// The chat messages for an action or a free-form request about `text`.
pub fn messages(action: Option<Action>, prompt: &str, text: &str) -> Vec<wordcraft_sharedai::Message> {
    use wordcraft_sharedai::Message;
    let text: String = text.chars().take(MAX_CONTEXT_CHARS).collect();
    let ask = match action {
        Some(a) => a.instruction().to_string(),
        None => prompt.trim().to_string(),
    };
    let user = if text.trim().is_empty() { ask } else { format!("{ask}\n\nText:\n{text}") };
    vec![Message::new("system", SYSTEM), Message::new("user", user)]
}

/// A reply ready to apply: surrounding blank space and one pair of wrapping quotes removed.
pub fn cleaned(reply: &str) -> String {
    let t = reply.trim();
    for (open, close) in [('"', '"'), ('\u{201C}', '\u{201D}')] {
        if let Some(inner) = t.strip_prefix(open).and_then(|r| r.strip_suffix(close))
            && !inner.contains(open)
            && !inner.contains(close)
        {
            return inner.trim().to_string();
        }
    }
    t.to_string()
}

enum Msg {
    Models(Result<Vec<String>, String>),
    Chunk(String),
    Done(Result<String, String>),
}

struct Job {
    rx: Receiver<Msg>,
    cancel: Arc<AtomicBool>,
    streaming: bool,
}

/// What the request was made on, so Replace hits that text if the document hasn't changed.
#[derive(Clone)]
struct Origin {
    doc: (u64, u64),
    sel: wordcraft_engine::Selection,
    had_text: bool,
    mode: &'static str,
}

#[derive(Default)]
pub struct State {
    prompt: String,
    reply: String,
    status: String,
    models: Vec<String>,
    asked_models: bool,
    job: Option<Job>,
    origin: Option<Origin>,
}

impl State {
    pub fn busy(&self) -> bool {
        self.job.as_ref().is_some_and(|j| j.streaming)
    }

    fn stop(&mut self) {
        if let Some(j) = &self.job {
            j.cancel.store(true, Ordering::Relaxed);
        }
    }

    fn poll(&mut self, prefs_model: &str) -> Option<String> {
        let mut chosen = None;
        let Some(job) = &mut self.job else { return None };
        loop {
            match job.rx.try_recv() {
                Ok(Msg::Chunk(t)) => self.reply.push_str(&t),
                Ok(Msg::Done(r)) => {
                    self.status = match r {
                        Ok(s) => s,
                        Err(e) => e,
                    };
                    self.job = None;
                    break;
                }
                Ok(Msg::Models(r)) => {
                    match r {
                        Ok(m) => {
                            self.status = if m.is_empty() { tl!("SharedAI has no models registered.").to_string() } else { String::new() };
                            if prefs_model.is_empty()
                                && let Some(first) = m.first()
                            {
                                chosen = Some(first.clone());
                            }
                            self.models = m;
                        }
                        Err(e) => self.status = e,
                    }
                    self.job = None;
                    break;
                }
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    self.job = None;
                    break;
                }
            }
        }
        chosen
    }
}

#[cfg(not(target_arch = "wasm32"))]
fn spawn(work: impl FnOnce(std::sync::mpsc::Sender<Msg>, Arc<AtomicBool>) + Send + 'static, streaming: bool) -> Result<Job, String> {
    let (tx, rx) = std::sync::mpsc::channel();
    let cancel = Arc::new(AtomicBool::new(false));
    let c = cancel.clone();
    std::thread::Builder::new().name("assistant".into()).spawn(move || work(tx, c)).map_err(|e| e.to_string())?;
    Ok(Job { rx, cancel, streaming })
}

#[cfg(target_arch = "wasm32")]
fn spawn(_work: impl FnOnce(std::sync::mpsc::Sender<Msg>, Arc<AtomicBool>) + Send + 'static, _streaming: bool) -> Result<Job, String> {
    Err(tl!("The Assistant needs the desktop app.").to_string())
}

fn refresh_models(app: &mut WordApp) {
    let socket = app.session.prefs.assistant_socket.clone();
    let job = spawn(
        move |tx, _| {
            let r = wordcraft_sharedai::endpoint(&socket)
                .and_then(|e| wordcraft_sharedai::list_models(&e, wordcraft_brand::EXECUTABLE))
                .map_err(|e| e.to_string());
            let _ = tx.send(Msg::Models(r));
        },
        false,
    );
    app.assistant.asked_models = true;
    match job {
        Ok(j) => app.assistant.job = Some(j),
        Err(e) => app.assistant.status = e,
    }
}

fn ask(app: &mut WordApp, action: Option<Action>) {
    let text = app.session.selected_text();
    if text.trim().is_empty() && action.is_some() {
        app.assistant.status = tl!("Select the text first.").to_string();
        return;
    }
    let model = app.session.prefs.assistant_model.clone();
    if model.is_empty() {
        app.assistant.status = tl!("Choose a model first.").to_string();
        return;
    }
    let msgs = messages(action, &app.assistant.prompt, &text);
    let socket = app.session.prefs.assistant_socket.clone();
    app.assistant.origin = Some(Origin {
        doc: (app.session.document_id(), app.session.rev()),
        sel: app.session.sel.clone(),
        had_text: !text.trim().is_empty(),
        mode: action.map(Action::mode).unwrap_or(if text.trim().is_empty() { "after" } else { "replace" }),
    });
    app.assistant.reply.clear();
    app.assistant.status = tl!("Writing…").to_string();
    let job = spawn(
        move |tx, cancel| {
            let chunks = tx.clone();
            let r = wordcraft_sharedai::endpoint(&socket).and_then(|e| {
                wordcraft_sharedai::chat(
                    &e,
                    wordcraft_brand::EXECUTABLE,
                    &model,
                    &msgs,
                    &wordcraft_sharedai::Params { max_tokens: Some(1024), temperature: Some(0.4), seed: None },
                    wordcraft_sharedai::Timeouts::default(),
                    &cancel,
                    |t| {
                        let _ = chunks.send(Msg::Chunk(t.to_string()));
                    },
                )
            });
            let done = match r {
                Ok(s) if s.reason == "length" => Ok("Stopped at the length limit.".to_string()),
                Ok(_) => Ok(String::new()),
                Err(wordcraft_sharedai::Error::Cancelled) => Ok("Stopped.".to_string()),
                Err(e) => Err(e.to_string()),
            };
            let _ = tx.send(Msg::Done(done));
        },
        true,
    );
    match job {
        Ok(j) => app.assistant.job = Some(j),
        Err(e) => app.assistant.status = e,
    }
}

fn apply(app: &mut WordApp, mode: &str) {
    let text = cleaned(&app.assistant.reply);
    if let Some(o) = &app.assistant.origin
        && o.doc == (app.session.document_id(), app.session.rev())
    {
        app.session.sel = o.sel.clone();
    }
    match app.run("assistant.apply", json!({"text": text, "mode": mode})) {
        Ok(_) => {
            app.assistant.reply.clear();
            app.assistant.origin = None;
            app.assistant.status = tl!("Added as tracked changes. Accept or reject them on the Review tab.").to_string();
        }
        Err(e) => app.assistant.status = e.to_string(),
    }
}

pub fn pane(app: &mut WordApp, ui: &mut Ui) {
    if crate::panes::header(ui, "Assistant") {
        app.assistant.stop();
        let _ = app.run("view.assistantPane", json!({"value": false}));
        return;
    }
    let model_now = app.session.prefs.assistant_model.clone();
    if let Some(m) = app.assistant.poll(&model_now) {
        let _ = app.run("assistant.settings", json!({"model": m}));
    }
    if app.assistant.job.is_some() {
        ui.ctx().request_repaint_after(std::time::Duration::from_millis(50));
    }
    if !app.assistant.asked_models && app.assistant.job.is_none() {
        refresh_models(app);
    }
    let busy = app.assistant.job.is_some();

    ui.label(egui::RichText::new(tl!("Runs on this computer with SharedAI. Suggestions arrive as tracked changes.")).small().weak());
    ui.add_space(6.0);

    // Model.
    ui.horizontal(|ui| {
        ui.label(tl!("Model"));
        let mut model = app.session.prefs.assistant_model.clone();
        let shown = if model.is_empty() { tl!("None").to_string() } else { model.clone() };
        egui::ComboBox::from_id_salt("assistant_model").selected_text(shown).show_ui(ui, |ui| {
            for m in &app.assistant.models {
                ui.selectable_value(&mut model, m.clone(), m);
            }
        });
        if model != app.session.prefs.assistant_model {
            let _ = app.run("assistant.settings", json!({"model": model}));
        }
        if ui.add_enabled(!busy, egui::Button::new(tl!("Refresh"))).clicked() {
            refresh_models(app);
        }
    });
    egui::CollapsingHeader::new(egui::RichText::new(tl!("Connection")).font(regular(12.5))).id_salt("assistant_conn").show(ui, |ui| {
        let mut socket = app.session.prefs.assistant_socket.clone();
        let hint = wordcraft_sharedai::endpoint("").map(|p| p.display().to_string()).unwrap_or_default();
        let r = ui.add(egui::TextEdit::singleline(&mut socket).hint_text(hint).desired_width(f32::INFINITY));
        if r.changed() {
            let _ = app.run("assistant.settings", json!({"socket": socket}));
        }
        ui.label(egui::RichText::new(tl!("Leave empty to use SHAREDAI_SOCKET or the desktop default.")).small().weak());
    });
    ui.add_space(6.0);

    // Quick actions on the selection.
    ui.label(egui::RichText::new(tl!("With the selection")).font(semibold(13.0)));
    let mut picked: Option<Option<Action>> = None;
    ui.horizontal_wrapped(|ui| {
        for a in Action::ALL {
            if ui.add_enabled(!busy, egui::Button::new(tl!(a.label()))).clicked() {
                picked = Some(Some(a));
            }
        }
    });
    ui.add_space(4.0);
    let r = ui.add(
        egui::TextEdit::multiline(&mut app.assistant.prompt)
            .hint_text(tl!("Ask for a rewrite, or describe what to write…"))
            .desired_rows(3)
            .desired_width(f32::INFINITY),
    );
    let enter = r.has_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter) && i.modifiers.command);
    ui.horizontal(|ui| {
        let can_send = !busy && !app.assistant.prompt.trim().is_empty();
        if ui.add_enabled(can_send, egui::Button::new(tl!("Send"))).clicked() || (enter && can_send) {
            picked = Some(None);
        }
        if app.assistant.busy() && ui.button(tl!("Stop")).clicked() {
            app.assistant.stop();
        }
    });
    if let Some(action) = picked {
        ask(app, action);
    }

    if !app.assistant.status.is_empty() {
        ui.add_space(4.0);
        ui.label(egui::RichText::new(app.assistant.status.as_str()).small());
    }

    if !app.assistant.reply.is_empty() {
        ui.add_space(6.0);
        ui.separator();
        egui::ScrollArea::vertical().max_height(ui.available_height() - 40.0).stick_to_bottom(true).show(ui, |ui| {
            ui.label(egui::RichText::new(app.assistant.reply.as_str()).font(regular(13.0)));
        });
        if !app.assistant.busy() {
            let (had_text, mode) = app.assistant.origin.as_ref().map(|o| (o.had_text, o.mode)).unwrap_or((false, "after"));
            ui.horizontal_wrapped(|ui| {
                if had_text && mode == "replace" && ui.button(tl!("Replace")).clicked() {
                    apply(app, "replace");
                } else if ui.button(tl!("Insert")).clicked() {
                    apply(app, "after");
                } else if ui.button(tl!("Copy")).clicked() {
                    ui.ctx().copy_text(cleaned(&app.assistant.reply));
                } else if ui.button(tl!("Discard")).clicked() {
                    app.assistant.reply.clear();
                    app.assistant.status.clear();
                }
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn messages_carry_the_instruction_and_the_selection() {
        let m = messages(Some(Action::Shorten), "", "A long sentence.");
        assert_eq!(m.len(), 2);
        assert_eq!(m[0].role, "system");
        assert!(m[1].content.starts_with("Rewrite the text to about half"));
        assert!(m[1].content.ends_with("Text:\nA long sentence."));
        let free = messages(None, "  Write a haiku about rain ", "");
        assert_eq!(free[1].content, "Write a haiku about rain");
        let huge = messages(Some(Action::Improve), "", &"é".repeat(MAX_CONTEXT_CHARS + 50));
        assert!(huge[1].content.chars().count() <= MAX_CONTEXT_CHARS + 100);
    }

    #[test]
    fn replies_are_cleaned_of_wrapping_quotes_only() {
        assert_eq!(cleaned("  \"Plain words.\"\n"), "Plain words.");
        assert_eq!(cleaned("\u{201C}Curly.\u{201D}"), "Curly.");
        assert_eq!(cleaned("\"One\" and \"two\""), "\"One\" and \"two\"");
        assert_eq!(cleaned("No quotes."), "No quotes.");
    }

    #[test]
    fn continue_inserts_after_and_the_rest_replace() {
        for a in Action::ALL {
            assert_eq!(a.mode(), if a == Action::Continue { "after" } else { "replace" });
        }
    }
}
