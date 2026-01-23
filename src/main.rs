// Prevents console window from appearing with GUI app in release builds
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod assets;
mod dialogs;
mod fonts;
mod icons;
mod settings;
mod settings_dialog;
mod terminal_schemes;
mod text_input;
mod theme;
mod ui;
mod updater;

fn main() {
    app::run();
}
