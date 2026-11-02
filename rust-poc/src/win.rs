//! Windows integration: single instance, rounded frameless window, toasts, sign-in startup, opening links.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HWND};
use windows_sys::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND};
use windows_sys::Win32::System::Threading::{CreateEventW, CreateMutexW, SetEvent, WaitForSingleObject, INFINITE};
use windows_sys::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID;

/// Distinct from the .NET app's names, so both can run side by side on one machine.
const INSTANCE_NAME: &str = "Local\\PriceRunnerPanelRs.SingleInstance";
const SHOW_SIGNAL_NAME: &str = "Local\\PriceRunnerPanelRs.Show";
pub const APP_USER_MODEL_ID: &str = "PriceRunnerPanel.Rust";
pub const AT_LOGIN: &str = "--startup";
const RUN_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const RUN_VALUE: &str = "PriceRunnerPanelRs";

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

pub struct SingleInstance {
    _mutex: HANDLE,
    signal: HANDLE,
}

pub enum Instance {
    First(SingleInstance),
    AlreadyRunning,
}

/// One instance per user: a second launch shows the running panel and exits.
pub fn single_instance(at_login: bool) -> Instance {
    unsafe {
        let mutex = CreateMutexW(std::ptr::null(), 1, wide(INSTANCE_NAME).as_ptr());
        let already = std::io::Error::last_os_error().raw_os_error() == Some(183); // ERROR_ALREADY_EXISTS
        let signal = CreateEventW(std::ptr::null(), 0, 0, wide(SHOW_SIGNAL_NAME).as_ptr());
        if already {
            if !at_login && !signal.is_null() {
                SetEvent(signal);
            }
            CloseHandle(signal);
            CloseHandle(mutex);
            return Instance::AlreadyRunning;
        }
        Instance::First(SingleInstance { _mutex: mutex, signal })
    }
}

impl SingleInstance {
    /// Calls `on_show` (from a background thread) each time another launch asks for the window.
    pub fn listen(&self, on_show: impl Fn() + Send + 'static) {
        let signal = self.signal as usize;
        std::thread::Builder::new()
            .name("show-signal".into())
            .spawn(move || loop {
                unsafe {
                    if WaitForSingleObject(signal as HANDLE, INFINITE) != 0 {
                        return;
                    }
                }
                on_show();
            })
            .expect("signal thread");
    }
}

/// Windows 11 rounds the corners of a frameless window and draws its shadow, so the panel keeps its look
/// without a transparent window (which the CPU renderer cannot provide).
pub fn round_corners(hwnd: HWND) {
    let pref: i32 = DWMWCP_ROUND;
    unsafe {
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE as u32, &pref as *const i32 as *const _, 4);
    }
}

/// Windows needs an app identity for toasts: registered per user, no admin rights.
pub fn register_app_id() {
    unsafe {
        SetCurrentProcessExplicitAppUserModelID(wide(APP_USER_MODEL_ID).as_ptr());
    }
    let hkcu = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    if let Ok((key, _)) = hkcu.create_subkey(format!("Software\\Classes\\AppUserModelId\\{APP_USER_MODEL_ID}")) {
        let _ = key.set_value("DisplayName", &"PriceRunner Panel (Rust)");
    }
}

/// A Windows toast; clicking it opens `url`.
pub fn toast(title: &str, body: &str, url: Option<String>) -> bool {
    let mut toast = tauri_winrt_notification::Toast::new(APP_USER_MODEL_ID).title(title).text1(body);
    if let Some(url) = url {
        toast = toast.on_activated(move |_| {
            open_url(&url);
            Ok(())
        });
    }
    toast.show().is_ok()
}

/// Only plain https links reach the browser.
pub fn open_url(url: &str) -> bool {
    if !url.starts_with("https://") || url[8..].split('/').next().map_or(true, |host| host.contains('@') || host.is_empty()) {
        return false;
    }
    open::that_detached(url).is_ok()
}

pub fn startup_command() -> String {
    format!("\"{}\" {AT_LOGIN}", crate::store::exe_path().display())
}

/// Starts the panel at sign-in through the per-user Run key. Returns false if Windows refused.
pub fn set_startup(enabled: bool) -> bool {
    let hkcu = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let Ok((key, _)) = hkcu.create_subkey(RUN_KEY) else { return false };
    if enabled {
        key.set_value(RUN_VALUE, &startup_command()).is_ok()
    } else {
        match key.delete_value(RUN_VALUE) {
            Ok(()) => true,
            Err(e) => e.kind() == std::io::ErrorKind::NotFound,
        }
    }
}
