//! Nisaba release links. The upstream installer assumes ArtCraft bundle/package identities.

use crate::WordApp;

const RELEASES: &str = "https://github.com/tomblanchard312/wordcraft-Nisaba/releases";

#[derive(Default)]
pub(crate) struct Runtime {
    pub(crate) manual_window_open: bool,
    pub(crate) status_message: Option<String>,
}

impl WordApp {
    // shortcut: upstream's installer hardcodes ArtCraft bundle IDs; use our release page until
    // craft-update supports Nisaba's identity on every platform.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn set_startup_update_check(&mut self, _on: bool) {}
}

pub(crate) fn check(app: &mut WordApp) {
    app.updates_rt.manual_window_open = true;
    app.updates_rt.status_message = Some(tl!("Open Nisaba releases to check for a newer version.").into());
}

pub(crate) fn tick(_app: &mut WordApp, _ctx: &egui::Context) {}

pub(crate) fn show(app: &mut WordApp, ctx: &egui::Context) {
    if !app.updates_rt.manual_window_open {
        return;
    }
    let mut open = true;
    let mut close = false;
    egui::Window::new(tl!("Check for Updates"))
        .open(&mut open)
        .resizable(false)
        .collapsible(false)
        .anchor(egui::Align2::CENTER_CENTER, egui::vec2(0.0, 0.0))
        .show(ctx, |ui| {
            if let Some(msg) = &app.updates_rt.status_message {
                ui.label(msg);
            }
            ui.hyperlink_to(tl!("Nisaba releases"), RELEASES);
            ui.add_space(8.0);
            if ui.button(tl!("Close")).clicked() {
                close = true;
            }
        });
    if !open || close {
        app.updates_rt.manual_window_open = false;
        app.updates_rt.status_message = None;
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn updates_use_nisaba_releases() {
        assert_eq!(super::RELEASES, format!("{}/releases", wordcraft_brand::HOMEPAGE.replace("wordcraft-nisaba", "wordcraft-Nisaba")));
    }
}
