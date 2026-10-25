//! Review › Scribe ([`wordcraft_brand::ASSISTANT_NAME`]): writing suggestions from a model that
//! runs on this computer.
//!
//! Behind the name is a local SharedAI daemon, which the pane starts when it isn't running and
//! talks to on worker threads (`wordcraft_sharedai`). Users never see the service: no socket
//! paths, no service names, only the assistant's name and plain messages. The reply streams into
//! the pane, and an accepted one goes into the document through `assistant.apply` as tracked
//! changes by the assistant. Nothing leaves the machine.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, TryRecvError};

use egui::Ui;
use serde_json::{Value, json};

use crate::WordApp;
use crate::theme::{regular, semibold};
use wordcraft_brand::ASSISTANT_NAME as NAME;

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
    Added(Result<String, String>),
}

/// What the background job is doing, for the pane's wording.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Work {
    Starting,
    Adding,
    Writing,
}

struct Job {
    rx: Receiver<Msg>,
    cancel: Arc<AtomicBool>,
    work: Work,
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
    /// The model list has been fetched at least once (successfully or not).
    listed: bool,
    /// The service couldn't be reached or started; the pane offers Try Again.
    unavailable: bool,
    asked_models: bool,
    job: Option<Job>,
    origin: Option<Origin>,
}

impl State {
    pub fn busy(&self) -> bool {
        self.job.as_ref().is_some_and(|j| j.work == Work::Writing)
    }

    fn stop(&mut self) {
        if let Some(j) = &self.job {
            j.cancel.store(true, Ordering::Relaxed);
        }
    }

    /// Take what the background job sent. Returns a model to select, when one was just added or
    /// none was chosen yet.
    fn poll(&mut self, prefs_model: &str) -> Option<String> {
        let mut chosen = None;
        let Some(job) = &mut self.job else { return None };
        loop {
            match job.rx.try_recv() {
                Ok(Msg::Chunk(t)) => self.reply.push_str(&t),
                Ok(Msg::Done(r)) => {
                    self.status = r.unwrap_or_else(|e| e);
                    self.job = None;
                    break;
                }
                Ok(Msg::Models(r)) => {
                    self.listed = true;
                    match r {
                        Ok(m) => {
                            self.unavailable = false;
                            self.status.clear();
                            if !m.iter().any(|x| x == prefs_model)
                                && let Some(first) = m.first()
                            {
                                chosen = Some(first.clone());
                            }
                            self.models = m;
                        }
                        Err(e) => {
                            self.unavailable = true;
                            self.status = e;
                        }
                    }
                    self.job = None;
                    break;
                }
                Ok(Msg::Added(r)) => {
                    match r {
                        Ok(id) => {
                            self.unavailable = false;
                            if !self.models.contains(&id) {
                                self.models.push(id.clone());
                            }
                            self.status = crate::i18n::fmt(tl!("{name} is ready."), &[("name", NAME)]);
                            chosen = Some(id);
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
fn spawn(work: Work, f: impl FnOnce(std::sync::mpsc::Sender<Msg>, Arc<AtomicBool>) + Send + 'static) -> Result<Job, String> {
    let (tx, rx) = std::sync::mpsc::channel();
    let cancel = Arc::new(AtomicBool::new(false));
    let c = cancel.clone();
    std::thread::Builder::new().name("scribe".into()).spawn(move || f(tx, c)).map_err(|e| {
        log::warn!("assistant: cannot start a worker thread: {e}");
        unavailable_text()
    })?;
    Ok(Job { rx, cancel, work })
}

#[cfg(target_arch = "wasm32")]
fn spawn(_work: Work, _f: impl FnOnce(std::sync::mpsc::Sender<Msg>, Arc<AtomicBool>) + Send + 'static) -> Result<Job, String> {
    Err(crate::i18n::fmt(tl!("{name} needs the desktop app."), &[("name", NAME)]))
}

fn unavailable_text() -> String {
    crate::i18n::fmt(tl!("{name} isn't available right now."), &[("name", NAME)])
}

/// A plain message for a failure; the technical detail goes to the log only.
pub fn friendly(e: &wordcraft_sharedai::Error) -> String {
    use wordcraft_sharedai::Error as E;
    log::warn!("assistant: {e}");
    let f = |s: &str| crate::i18n::fmt(s, &[("name", NAME)]);
    match e {
        E::Cancelled => tl!("Stopped.").to_string(),
        E::Busy { .. } => f(tl!("{name} is busy. Try again in a moment.")),
        E::Timeout => f(tl!("{name} stopped responding. Try again.")),
        E::Unsupported => f(tl!("{name} isn't available on this system.")),
        E::NotInstalled => f(tl!("{name} isn't installed with this copy of Nisaba.")),
        E::Incompatible => f(tl!("{name} needs an update.")),
        E::Invalid(_) => f(tl!("{name} couldn't take that request.")),
        E::Registry(_) => tl!("That model couldn't be added.").to_string(),
        E::Inference { kind, .. } => match kind.as_str() {
            "context_overflow" => f(tl!("That's more text than {name} can read at once. Select less.")),
            "model_not_found" | "model_rejected" => f(tl!("{name}'s model is missing or has changed. Choose the model file again.")),
            "load_failed" => f(tl!("{name} couldn't load its model. It may need more memory, or another model file.")),
            "worker_crashed" => f(tl!("{name} stopped unexpectedly. Try again.")),
            _ => f(tl!("{name} ran into a problem. Try again.")),
        },
        E::NoEndpoint | E::Io(_) | E::StartFailed(_) | E::Protocol(_) => unavailable_text(),
    }
}

/// The model service to use, started if it isn't running. A socket set by a packager or script
/// (`assistant.settings`) wins; otherwise the shared per-user service. Inside a Flatpak, where
/// that service can't be created, the app runs a private one unless a host service already
/// listens.
fn service(socket_pref: &str) -> Result<PathBuf, wordcraft_sharedai::Error> {
    if !socket_pref.trim().is_empty() {
        let ep = PathBuf::from(socket_pref.trim());
        wordcraft_sharedai::ensure_running(&ep, Some(&ep))?;
        return Ok(ep);
    }
    let shared = wordcraft_sharedai::endpoint("");
    if Path::new("/.flatpak-info").exists() {
        if let Ok(ep) = &shared
            && wordcraft_sharedai::is_listening(ep)
        {
            return Ok(ep.clone());
        }
        let dir = std::env::var_os("XDG_RUNTIME_DIR").map(PathBuf::from).ok_or(wordcraft_sharedai::Error::NoEndpoint)?;
        let ep = dir.join("scribe").join("ipc.sock");
        wordcraft_sharedai::ensure_running(&ep, Some(&ep))?;
        return Ok(ep);
    }
    let ep = shared?;
    wordcraft_sharedai::ensure_running(&ep, None)?;
    Ok(ep)
}

/// Where added model files are kept: `$XDG_DATA_HOME/nisaba/models` (inside a Flatpak, the app's
/// own data folder).
fn models_dir() -> Option<PathBuf> {
    let data = std::env::var_os("XDG_DATA_HOME")
        .filter(|d| !d.is_empty())
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local").join("share")))?;
    Some(data.join(wordcraft_brand::SETTINGS_DIR_XDG).join("models"))
}

/// Copy `src` into the models folder (private to the user) unless it is already there.
fn keep_model_file(src: &Path) -> Result<PathBuf, wordcraft_sharedai::Error> {
    let dir = models_dir().ok_or(wordcraft_sharedai::Error::NoEndpoint)?;
    std::fs::create_dir_all(&dir)?;
    let src = src.canonicalize()?;
    let dir = dir.canonicalize()?;
    if src.parent() == Some(dir.as_path()) {
        return Ok(src);
    }
    let name = src.file_name().ok_or_else(|| wordcraft_sharedai::Error::Invalid("not a file".into()))?;
    let dst = dir.join(name);
    let tmp = dir.join(format!(".{}.part", name.to_string_lossy()));
    std::fs::copy(&src, &tmp)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&tmp, std::fs::Permissions::from_mode(0o600))?;
    }
    std::fs::rename(&tmp, &dst)?;
    Ok(dst)
}

fn refresh_models(app: &mut WordApp) {
    let socket = app.session.prefs.assistant_socket.clone();
    let job = spawn(Work::Starting, move |tx, _| {
        let r = service(&socket).and_then(|ep| wordcraft_sharedai::list_models(&ep, wordcraft_brand::EXECUTABLE)).map_err(|e| friendly(&e));
        let _ = tx.send(Msg::Models(r));
    });
    app.assistant.asked_models = true;
    match job {
        Ok(j) => app.assistant.job = Some(j),
        Err(e) => {
            app.assistant.unavailable = true;
            app.assistant.status = e;
        }
    }
}

/// Add the model file at `path`: copy it into Nisaba's models folder, register it with its
/// checksum, and select it. Runs in the background; the pane reports when it's ready. Used by
/// the pane's Choose Model File… and by `ui.assistant.addModel {path}`.
pub fn add_model(app: &mut WordApp, path: &str) -> Result<Value, String> {
    if app.assistant.job.is_some() {
        return Err(crate::i18n::fmt(tl!("{name} is busy. Try again in a moment."), &[("name", NAME)]));
    }
    let src = PathBuf::from(path);
    if !src.is_file() {
        return Err(tl!("That model file can't be found.").to_string());
    }
    let socket = app.session.prefs.assistant_socket.clone();
    let job = spawn(Work::Adding, move |tx, _| {
        let r = service(&socket)
            .and_then(|ep| {
                let kept = keep_model_file(&src)?;
                let id = wordcraft_sharedai::model_id(&kept);
                wordcraft_sharedai::register_file(&ep, wordcraft_brand::EXECUTABLE, &id, &kept)?;
                Ok(id)
            })
            .map_err(|e| match e {
                wordcraft_sharedai::Error::Io(_) => {
                    log::warn!("assistant: adding {}: {e}", src.display());
                    tl!("That model file couldn't be copied. Check there is enough disk space.").to_string()
                }
                e => friendly(&e),
            });
        let _ = tx.send(Msg::Added(r));
    })?;
    app.assistant.job = Some(job);
    app.assistant.status.clear();
    Ok(json!({"adding": path}))
}

fn choose_model_file(app: &mut WordApp) {
    let _ = app.ask_file(crate::file_dialogs::FileDialogRequest::Open { purpose: "model".into() }, crate::file_dialogs::AfterPick::AssistantModel);
}

fn ask(app: &mut WordApp, action: Option<Action>) {
    let text = app.session.selected_text();
    if text.trim().is_empty() && action.is_some() {
        app.assistant.status = tl!("Select the text first.").to_string();
        return;
    }
    let model = app.session.prefs.assistant_model.clone();
    if model.is_empty() {
        app.assistant.status = tl!("Choose a model file first.").to_string();
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
    app.assistant.status.clear();
    let job = spawn(Work::Writing, move |tx, cancel| {
        let chunks = tx.clone();
        let r = service(&socket).and_then(|ep| {
            wordcraft_sharedai::chat(
                &ep,
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
            Ok(s) if s.reason == "length" => Ok(tl!("Stopped at the length limit.").to_string()),
            Ok(_) => Ok(String::new()),
            Err(e) => Err(friendly(&e)),
        };
        let _ = tx.send(Msg::Done(done));
    });
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

fn working_label(ui: &mut Ui, text: &str) {
    ui.horizontal(|ui| {
        ui.add(egui::Spinner::new().size(12.0));
        ui.label(egui::RichText::new(text).small());
    });
}

pub fn pane(app: &mut WordApp, ui: &mut Ui) {
    if crate::panes::header(ui, NAME) {
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
    let work = app.assistant.job.as_ref().map(|j| j.work);
    let busy = work.is_some();

    ui.label(
        egui::RichText::new(tl!("Works privately on this computer. Suggestions arrive as tracked changes you can accept or reject.")).small().weak(),
    );
    ui.add_space(8.0);

    match work {
        Some(Work::Starting) => {
            working_label(ui, &crate::i18n::fmt(tl!("Starting {name}…"), &[("name", NAME)]));
            return;
        }
        Some(Work::Adding) => {
            working_label(ui, tl!("Adding the model. Large files take a minute…"));
            return;
        }
        _ => {}
    }

    // Couldn't reach or start the service.
    if app.assistant.unavailable {
        ui.label(egui::RichText::new(app.assistant.status.as_str()).small());
        ui.add_space(4.0);
        if ui.button(tl!("Try Again")).clicked() {
            app.assistant.status.clear();
            refresh_models(app);
        }
        return;
    }

    // First run: no model yet.
    if app.assistant.listed && app.assistant.models.is_empty() {
        ui.label(crate::i18n::fmt(tl!("{name} needs a language model before it can write."), &[("name", NAME)]));
        ui.add_space(6.0);
        if ui.button(tl!("Choose Model File…")).clicked() {
            choose_model_file(app);
        }
        ui.add_space(4.0);
        ui.label(egui::RichText::new(tl!("Any GGUF chat model works, for example Qwen2.5 1.5B Instruct.")).small().weak());
        if !app.assistant.status.is_empty() {
            ui.add_space(4.0);
            ui.label(egui::RichText::new(app.assistant.status.as_str()).small());
        }
        return;
    }

    // Model choice, only when there is a choice to make.
    if app.assistant.models.len() > 1 {
        ui.horizontal(|ui| {
            ui.label(tl!("Model"));
            let mut model = app.session.prefs.assistant_model.clone();
            egui::ComboBox::from_id_salt("assistant_model").selected_text(model.clone()).show_ui(ui, |ui| {
                for m in &app.assistant.models {
                    ui.selectable_value(&mut model, m.clone(), m);
                }
            });
            if model != app.session.prefs.assistant_model {
                let _ = app.run("assistant.settings", json!({"model": model}));
            }
        });
        ui.add_space(6.0);
    }

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

    if work == Some(Work::Writing) && app.assistant.reply.is_empty() {
        ui.add_space(4.0);
        working_label(ui, &crate::i18n::fmt(tl!("{name} is writing…"), &[("name", NAME)]));
    } else if !app.assistant.status.is_empty() {
        ui.add_space(4.0);
        ui.label(egui::RichText::new(app.assistant.status.as_str()).small());
    }

    if !app.assistant.reply.is_empty() {
        ui.add_space(6.0);
        ui.separator();
        egui::ScrollArea::vertical().max_height(ui.available_height() - 64.0).stick_to_bottom(true).show(ui, |ui| {
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

    ui.add_space(10.0);
    if ui.add_enabled(!busy, egui::Button::new(egui::RichText::new(tl!("Add Another Model…")).small()).frame(false)).clicked() {
        choose_model_file(app);
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
    fn failures_read_plainly_and_never_name_the_service() {
        use wordcraft_sharedai::Error as E;
        let errors = [
            E::NoEndpoint,
            E::NotInstalled,
            E::StartFailed("exit 1".into()),
            E::Io(std::io::Error::from(std::io::ErrorKind::NotFound)),
            E::Protocol("frame".into()),
            E::Busy { retry_after: std::time::Duration::from_secs(1) },
            E::Inference { kind: "context_overflow".into(), message: "n_ctx".into() },
            E::Inference { kind: "model_rejected".into(), message: "sha256".into() },
            E::Registry("capacity".into()),
            E::Timeout,
        ];
        for e in &errors {
            let m = friendly(e);
            for hidden in ["SharedAI", "SHAREDAI", "socket", "daemon", "ipc.sock", "os error"] {
                assert!(!m.contains(hidden), "{e:?} -> {m:?}");
            }
            assert!(!m.is_empty());
        }
        assert!(friendly(&E::NotInstalled).contains(NAME));
    }

    #[test]
    fn continue_inserts_after_and_the_rest_replace() {
        for a in Action::ALL {
            assert_eq!(a.mode(), if a == Action::Continue { "after" } else { "replace" });
        }
    }
}
