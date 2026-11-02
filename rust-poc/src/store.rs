//! Pins, history and settings in one JSON file. Writes go to a temporary file first and replace the old one.

use crate::model::AppState;
use std::path::{Path, PathBuf};

pub struct Store {
    pub data_dir: PathBuf,
    pub file: PathBuf,
}

/// %LOCALAPPDATA%\PriceRunnerPanel-rs (its own folder, so the .NET app's data is never touched), or
/// PRICERUNNERPANEL_DATA when set (testing).
pub fn data_directory() -> PathBuf {
    if let Some(custom) = std::env::var_os("PRICERUNNERPANEL_DATA").filter(|v| !v.is_empty()) {
        return PathBuf::from(custom);
    }
    let local = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
    local.join("PriceRunnerPanel-rs")
}

impl Store {
    pub fn new() -> Self {
        let data_dir = data_directory();
        let _ = std::fs::create_dir_all(&data_dir);
        let file = data_dir.join("state.json");
        Self { data_dir, file }
    }

    pub fn load(&self) -> AppState {
        match std::fs::read_to_string(&self.file) {
            Ok(text) => match serde_json::from_str::<AppState>(&text) {
                Ok(state) => state,
                Err(e) => {
                    log(&format!("state.json unreadable ({e}); starting empty"));
                    let broken = self.file.with_extension(format!("json.broken-{}", chrono::Local::now().format("%Y%m%d%H%M%S")));
                    let _ = std::fs::rename(&self.file, broken);
                    AppState::default()
                }
            },
            Err(_) => AppState::default(),
        }
    }

    pub fn save(&self, state: &AppState) {
        let Ok(json) = serde_json::to_string_pretty(state) else { return };
        let temp = self.file.with_extension("json.tmp");
        if std::fs::write(&temp, json).is_ok() {
            if std::fs::rename(&temp, &self.file).is_err() {
                // rename over an existing file can fail on Windows while the file is read; fall back to a copy
                let _ = std::fs::copy(&temp, &self.file);
                let _ = std::fs::remove_file(&temp);
            }
        }
    }
}

/// Appends a line to logs\app.log in the data folder. Never fails loudly.
pub fn log(message: &str) {
    let dir = data_directory().join("logs");
    let _ = std::fs::create_dir_all(&dir);
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(dir.join("app.log")) {
        use std::io::Write;
        let _ = writeln!(f, "{} {message}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S %:z"));
    }
}

pub fn exe_path() -> PathBuf {
    std::env::current_exe().unwrap_or_else(|_| Path::new("pricerunner-panel.exe").to_path_buf())
}
