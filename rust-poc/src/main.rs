#![windows_subsystem = "windows"]
//! PriceRunner Panel, Rust proof of concept: a tray panel that searches pricerunner.se, pins products and checks
//! their prices on a schedule. Slint UI drawn on the CPU; blocking HTTP on worker threads; no async runtime.

mod analysis;
mod api;
mod controller;
mod deals;
mod format;
mod images;
mod model;
mod store;
mod tracker;
mod win;

slint::include_modules!();

use controller::{post, with, Controller};
use slint::winit_030::winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::winit_030::WinitWindowAccessor;
use slint::{ComponentHandle, Timer, TimerMode};
use std::rc::Rc;
use std::time::Duration;
use tray_icon::menu::{CheckMenuItem, Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let at_login = args.iter().any(|a| a == win::AT_LOGIN);
    let instance = match win::single_instance(at_login) {
        win::Instance::First(i) => i,
        win::Instance::AlreadyRunning => return,
    };
    win::register_app_id();

    let store = store::Store::new();
    let state = store.load();
    let client = api::Client::new();
    let ui = App::new().expect("UI");
    let weak = ui.as_weak();
    let images = images::Loader::new(client.clone(), store.data_dir.join("images"), {
        let weak = weak.clone();
        move |decoded| post(&weak, move |c| c.on_image_ready(decoded))
    });
    let ctrl = Controller::new(weak.clone(), client, store, state, images);
    controller::install(ctrl.clone());
    wire(&ui, &ctrl);

    // Tray icon and menu. Events arrive on the message loop; they are handed to the UI thread's controller.
    let icon_rgba = image::load_from_memory(include_bytes!("../assets/icon.png")).expect("icon").into_rgba8();
    let tray_icon = tray_icon::Icon::from_rgba(icon_rgba.to_vec(), icon_rgba.width(), icon_rgba.height()).expect("tray icon");
    let menu = Menu::new();
    let open_item = MenuItem::with_id("open", "Open panel", true, None);
    let check_item = MenuItem::with_id("check", "Check prices now", true, None);
    let updates_item = MenuItem::with_id("updates", "Check for updates…", true, None);
    let autostart = CheckMenuItem::with_id("autostart", "Start with Windows", true, ctrl.start_with_windows(), None);
    let quit_item = MenuItem::with_id("quit", "Quit PriceRunner Panel", true, None);
    let _ = menu.append_items(&[&open_item, &check_item, &updates_item, &PredefinedMenuItem::separator(), &autostart, &PredefinedMenuItem::separator(), &quit_item]);
    *ctrl.autostart_item.borrow_mut() = Some(autostart);
    let _tray = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("PriceRunner Panel (Rust)")
        .with_icon(tray_icon)
        .with_menu_on_left_click(false)
        .build()
        .expect("tray");
    {
        let weak = weak.clone();
        TrayIconEvent::set_event_handler(Some(move |event: TrayIconEvent| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                let _ = weak.upgrade_in_event_loop(|ui| toggle_window(&ui));
            }
        }));
    }
    {
        let weak = weak.clone();
        MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
            let id = event.id().0.clone();
            let _ = weak.upgrade_in_event_loop(move |ui| match id.as_str() {
                "open" => show_window(&ui),
                "check" => with(|c| c.check_all()),
                "updates" => with(|c| check_for_updates(c)),
                "autostart" => with(|c| {
                    c.toggle_start_with_windows();
                }),
                "quit" => {
                    let _ = slint::quit_event_loop();
                }
                _ => {}
            });
        }));
    }

    // A second launch asks for the window.
    {
        let weak = weak.clone();
        instance.listen(move || {
            let _ = weak.upgrade_in_event_loop(|ui| show_window(&ui));
        });
    }

    // Closing the window (Alt+F4) hides it to the tray so tracking continues; Quit in the tray menu ends the app.
    ui.window().on_close_requested(|| slint::CloseRequestResponse::HideWindow);

    // Started by Windows at sign-in: stay in the tray; tracking runs either way.
    if !at_login {
        show_window(&ui);
    }
    ctrl.start();

    test_hooks(&ui);

    let tick = Timer::default();
    tick.start(TimerMode::Repeated, Duration::from_secs(30), || with(|c| c.tick()));

    slint::run_event_loop_until_quit().expect("event loop");
    ctrl.save();
}

/// Connects the window's callbacks to the controller.
fn wire(ui: &App, ctrl: &Rc<Controller>) {
    let weak = ui.as_weak();
    ui.on_drag_window({
        let weak = weak.clone();
        move || {
            weak.unwrap().window().with_winit_window(|w| {
                let _ = w.drag_window();
            });
        }
    });
    ui.on_resize_window({
        let weak = weak.clone();
        move || {
            weak.unwrap().window().with_winit_window(|w| {
                let _ = w.drag_resize_window(slint::winit_030::winit::window::ResizeDirection::SouthEast);
            });
        }
    });
    ui.on_minimize({
        let weak = weak.clone();
        move || weak.unwrap().window().set_minimized(true)
    });
    ui.on_hide_window({
        let weak = weak.clone();
        move || {
            let _ = weak.unwrap().window().hide();
        }
    });
    ui.on_check_all(|| with(|c| c.check_all()));
    ui.on_check_one(|id| with(|c| c.check_one(&id)));
    ui.on_unpin(|id| with(|c| c.unpin(&id)));
    ui.on_open_product(|id| with(|c| c.open_product(&id)));
    ui.on_open_store(|id| with(|c| c.open_store(&id)));
    ui.on_save_target(|id, text| with(|c| c.save_target(&id, &text)));
    ui.on_interval_changed(|i| with(|c| c.set_interval(i)));
    ui.on_search_edited(|t| with(|c| c.on_search_edited(&t)));
    ui.on_search_now(|t| with(|c| c.run_search(&t)));
    ui.on_toggle_pin(|id| with(|c| c.toggle_pin(&id)));
    ui.on_check_for_updates(|| with(check_for_updates));
    ui.on_show_tab({
        let weak = weak.clone();
        move |tab| {
            weak.unwrap().set_tab(tab);
            if tab == 1 {
                with(|c| c.deals_opened());
            }
        }
    });
    ui.on_deals_search_edited(|t| with(|c| c.deals_search_edited(&t)));
    ui.on_deals_refresh(|| with(|c| c.deals_refresh()));
    ui.on_deals_load_more(|| with(|c| c.deals_load_more()));
    ui.on_deals_clear_filters(|| with(|c| c.deals_clear_filters()));
    ui.on_filter_search(|kind, t| with(|c| c.filter_search(kind, &t)));
    ui.on_filter_toggled(|kind, id, on| with(|c| c.filter_toggled(kind, id as i64, on)));
    ui.on_filter_clear(|kind| with(|c| c.filter_clear(kind)));
    ui.on_drop_edited(|min, max| with(|c| c.drop_edited(&min, &max)));
    ui.on_drop_preset(|min| with(|c| c.drop_preset(min as i64)));
    let _ = ctrl;
}

/// No self-updater in the proof of concept: the releases page opens instead.
fn check_for_updates(c: &Rc<Controller>) {
    c.set_footer("The Rust proof of concept has no updater; opening the releases page".into());
    c.open_url(controller::RELEASES_URL);
}

fn show_window(ui: &App) {
    let window = ui.window();
    let first = !window.is_visible();
    let _ = window.show();
    if window.is_minimized() {
        window.set_minimized(false);
    }
    window.with_winit_window(|w| {
        if first {
            // Windows 11 rounds the frameless window's corners and gives it a shadow.
            if let Ok(handle) = w.window_handle() {
                if let RawWindowHandle::Win32(h) = handle.as_raw() {
                    win::round_corners(h.hwnd.get() as _);
                }
            }
            if let Some(monitor) = w.current_monitor().or_else(|| w.primary_monitor()) {
                let (ms, mp, ws) = (monitor.size(), monitor.position(), w.outer_size());
                if ws.width > 0 && ms.width > ws.width {
                    w.set_outer_position(slint::winit_030::winit::dpi::PhysicalPosition::new(
                        mp.x + (ms.width as i32 - ws.width as i32) / 2,
                        mp.y + (ms.height as i32 - ws.height as i32) / 2,
                    ));
                }
            }
        }
        w.focus_window();
    });
}

fn toggle_window(ui: &App) {
    let window = ui.window();
    if window.is_visible() && !window.is_minimized() {
        let _ = window.hide();
    } else {
        show_window(ui);
    }
}

/// Scripted runs for measurements: PRP_TEST_TAB (0/1/2), PRP_TEST_QUERY (a search), PRP_TEST_DEALS_PAGES (pages to load).
fn test_hooks(ui: &App) {
    let env = |name: &str| std::env::var(name).ok().filter(|v| !v.is_empty());
    if let Some(tab) = env("PRP_TEST_TAB").and_then(|t| t.parse::<i32>().ok()) {
        ui.invoke_show_tab(tab);
    }
    if let Some(query) = env("PRP_TEST_QUERY") {
        ui.invoke_show_tab(0);
        with(|c| c.run_search(&query));
    }
    if let Some((w, h)) = env("PRP_TEST_SIZE").and_then(|s| s.split_once('x').and_then(|(w, h)| Some((w.parse::<f32>().ok()?, h.parse::<f32>().ok()?)))) {
        ui.window().set_size(slint::LogicalSize::new(w, h));
    }
    let every = env("PRP_TEST_PAGE_EVERY_MS").and_then(|t| t.parse::<u64>().ok()).unwrap_or(1500);
    if let Some(pages) = env("PRP_TEST_DEALS_PAGES").and_then(|t| t.parse::<i32>().ok()) {
        let loaded = std::rc::Rc::new(std::cell::Cell::new(1));
        let weak = ui.as_weak();
        let timer = Box::leak(Box::new(Timer::default()));
        timer.start(TimerMode::Repeated, Duration::from_millis(every), move || {
            let ui = weak.unwrap();
            if loaded.get() < pages && ui.get_can_load_more() {
                loaded.set(loaded.get() + 1);
                with(|c| c.deals_load_more());
            }
        });
    }
}
