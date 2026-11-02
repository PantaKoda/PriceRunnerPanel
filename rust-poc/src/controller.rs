//! The panel's state and behaviour (MainViewModel.cs + PinnedItemViewModel.cs): search PriceRunner, pin products,
//! check pinned prices on a schedule and notify on drops. Everything here runs on the UI thread; network work
//! happens on short-lived worker threads that post their results back with `post`.

use crate::analysis::{self, Insight};
use crate::api::Client;
use crate::format;
use crate::images::{Decoded, Loader};
use crate::model::*;
use crate::store::Store;
use crate::tracker::{self, PriceCheck};
use crate::{deals, win, App, PinCard, PinGroup, ResultRow};
use chrono::{Days, Duration, Local, Utc};
use slint::{Image, Model, ModelRc, Rgba8Pixel, SharedPixelBuffer, Timer, TimerMode, VecModel, Weak};
use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};
use std::rc::Rc;
use std::time::Duration as StdDuration;

const SEARCH_DEBOUNCE: StdDuration = StdDuration::from_millis(450);
/// Pause between products during a full check, to stay a polite client.
const BETWEEN_CHECKS: StdDuration = StdDuration::from_millis(1500);
/// Days of PriceRunner's history the sparkline shows.
const SPARKLINE_DAYS: u64 = 90;
pub const INTERVALS: [i64; 7] = [15, 30, 60, 120, 240, 480, 1440];
pub const VERSION_TEXT: &str = "v0.4.7-rs";
pub const RELEASES_URL: &str = "https://github.com/PantaKoda/PriceRunnerPanel/releases";

thread_local! {
    static CTRL: RefCell<Option<Rc<Controller>>> = const { RefCell::new(None) };
}

pub fn install(ctrl: Rc<Controller>) {
    CTRL.with(|c| *c.borrow_mut() = Some(ctrl));
}

pub fn with(f: impl FnOnce(&Rc<Controller>)) {
    let ctrl = CTRL.with(|c| c.borrow().clone());
    if let Some(c) = ctrl {
        f(&c);
    }
}

/// Runs `f` on the UI thread with the controller; called from worker threads.
pub fn post(ui: &Weak<App>, f: impl FnOnce(&Rc<Controller>) + Send + 'static) {
    let _ = ui.upgrade_in_event_loop(move |_| with(f));
}

pub struct Controller {
    pub ui: Weak<App>,
    pub client: Client,
    pub store: Store,
    pub state: RefCell<AppState>,
    pub images: Loader,
    image_cache: RefCell<HashMap<String, Image>>,
    pub results: Rc<VecModel<ResultRow>>,
    result_hits: RefCell<Vec<SearchHit>>,
    groups: Rc<VecModel<PinGroup>>,
    checking_ids: RefCell<HashSet<String>>,
    checking_all: Cell<bool>,
    search_gen: Cell<u64>,
    search_query: RefCell<String>,
    search_timer: Timer,
    pub deals: RefCell<deals::Deals>,
    pub footer: RefCell<String>,
    pub autostart_item: RefCell<Option<tray_icon::menu::CheckMenuItem>>,
}

pub struct CheckOutcome {
    pub pin: PinnedProduct,
    pub check: Option<PriceCheck>,
    pub notify: bool,
    /// The first check after pinning sets the baseline from the live offers.
    pub baseline: bool,
    pub groups_changed: bool,
}

impl Controller {
    pub fn new(ui: Weak<App>, client: Client, store: Store, state: AppState, images: Loader) -> Rc<Self> {
        let ctrl = Rc::new(Self {
            ui,
            client,
            store,
            state: RefCell::new(state),
            images,
            image_cache: RefCell::new(HashMap::new()),
            results: Rc::new(VecModel::default()),
            result_hits: RefCell::new(Vec::new()),
            groups: Rc::new(VecModel::default()),
            checking_ids: RefCell::new(HashSet::new()),
            checking_all: Cell::new(false),
            search_gen: Cell::new(0),
            search_query: RefCell::new(String::new()),
            search_timer: Timer::default(),
            deals: RefCell::new(deals::Deals::default()),
            footer: RefCell::new(String::new()),
            autostart_item: RefCell::new(None),
        });
        let app = ctrl.ui.unwrap();
        app.set_results(ModelRc::from(ctrl.results.clone()));
        app.set_groups(ModelRc::from(ctrl.groups.clone()));
        app.set_deals(ModelRc::from(ctrl.deals.borrow().rows.clone()));
        app.set_version_text(VERSION_TEXT.into());
        app.set_search_status("Type a product name — results come from pricerunner.se".into());
        let minutes = ctrl.state.borrow().settings.refresh_minutes;
        app.set_interval_index(INTERVALS.iter().position(|m| *m == minutes).unwrap_or(2) as i32);
        let has_pins = !ctrl.state.borrow().pins.is_empty();
        app.set_tab(if has_pins { 2 } else { 0 });
        let keep: Vec<String> = ctrl.state.borrow().pins.iter().filter_map(|p| Client::image_url(p.image_path.as_deref(), 112)).collect();
        ctrl.images.prune(&keep);
        ctrl.refresh_watchlist();
        ctrl.update_status();
        ctrl
    }

    pub fn app(&self) -> App {
        self.ui.unwrap()
    }

    // ----- pictures -----

    /// The cached picture, or an empty one while it is fetched.
    fn image_for(&self, image_path: Option<&str>) -> Image {
        let Some(url) = Client::image_url(image_path, 112) else { return Image::default() };
        if let Some(img) = self.image_cache.borrow().get(&url) {
            return img.clone();
        }
        self.images.request(&url);
        Image::default()
    }

    pub fn on_image_ready(&self, decoded: Decoded) {
        let buffer = SharedPixelBuffer::<Rgba8Pixel>::clone_from_slice(&decoded.rgba, decoded.width, decoded.height);
        let image = Image::from_rgba8(buffer);
        self.image_cache.borrow_mut().insert(decoded.url.clone(), image.clone());
        // Rows that were waiting for it.
        let hits = self.result_hits.borrow();
        for (i, hit) in hits.iter().enumerate() {
            if Client::image_url(hit.image_path.as_deref(), 112).as_deref() == Some(decoded.url.as_str()) {
                if let Some(mut row) = self.results.row_data(i) {
                    row.image = image.clone();
                    self.results.set_row_data(i, row);
                }
            }
        }
        drop(hits);
        self.deals_image_ready(&decoded.url, &image);
        if self.state.borrow().pins.iter().any(|p| Client::image_url(p.image_path.as_deref(), 112).as_deref() == Some(decoded.url.as_str())) {
            self.refresh_watchlist();
        }
    }

    /// Lets go of pictures in memory (the files stay), except those the watchlist still shows.
    pub fn release_images<'a>(&self, paths: impl Iterator<Item = Option<&'a str>>) {
        let state = self.state.borrow();
        let mut cache = self.image_cache.borrow_mut();
        for path in paths {
            if let Some(url) = Client::image_url(path, 112) {
                if !state.pins.iter().any(|p| p.image_path.as_deref() == path) {
                    cache.remove(&url);
                }
            }
        }
    }

    // ----- search -----

    pub fn on_search_edited(&self, text: &str) {
        *self.search_query.borrow_mut() = text.to_string();
        let query = text.to_string();
        self.search_timer.start(TimerMode::SingleShot, SEARCH_DEBOUNCE, move || with(|c| c.run_search(&query)));
    }

    pub fn run_search(&self, query: &str) {
        self.search_timer.stop();
        let gen = self.search_gen.get() + 1;
        self.search_gen.set(gen);
        let query = query.trim().to_string();
        let app = self.app();
        if query.chars().count() < 2 {
            if query.is_empty() {
                self.clear_results();
                app.set_search_status("Type a product name — results come from pricerunner.se".into());
            }
            app.set_searching(false);
            return;
        }
        app.set_searching(true);
        app.set_search_status(format!("Searching for “{query}”…").into());
        let (client, ui) = (self.client.clone(), self.ui.clone());
        std::thread::spawn(move || {
            let result = client.search(&query);
            post(&ui, move |c| c.on_search_done(gen, query, result));
        });
    }

    fn on_search_done(&self, gen: u64, query: String, result: crate::api::ApiResult<Vec<SearchHit>>) {
        if gen != self.search_gen.get() {
            return;
        }
        let app = self.app();
        app.set_searching(false);
        match result {
            Ok(hits) => {
                self.clear_results();
                let rows: Vec<ResultRow> = hits.iter().map(|h| self.result_row(h, None, None)).collect();
                self.results.set_vec(rows);
                app.set_search_status(
                    if hits.is_empty() { format!("No products found for “{query}”") } else { format!("{} products  ·  pin one to track its price", hits.len()) }.into(),
                );
                *self.result_hits.borrow_mut() = hits;
            }
            Err(e) => app.set_search_status(if e.timed_out { "PriceRunner did not answer in time".into() } else { format!("Search failed: {}", e.message).into() }),
        }
    }

    fn clear_results(&self) {
        let hits = std::mem::take(&mut *self.result_hits.borrow_mut());
        self.release_images(hits.iter().map(|h| h.image_path.as_deref()));
        self.results.set_vec(Vec::new());
    }

    /// A product row for the search or Black Friday tab.
    pub fn result_row(&self, hit: &SearchHit, insight: Option<&Insight>, store_text: Option<&str>) -> ResultRow {
        let mut parts = Vec::new();
        if let Some(c) = &hit.category {
            parts.push(c.clone());
        }
        if hit.store_count > 0 {
            parts.push(if hit.store_count == 1 { "1 store".into() } else { format!("{} stores", hit.store_count) });
        }
        if let (Some(r), true) = (hit.rating, hit.rating_count > 0) {
            parts.push(format!("★ {r:.1} ({})", hit.rating_count));
        }
        if hit.out_of_stock {
            parts.push("out of stock".into());
        }
        let has_drop = hit.recent_drop_percent.map_or(false, |p| p > 0.5);
        let insight = insight.cloned().unwrap_or(Insight::NONE);
        ResultRow {
            id: hit.id.as_str().into(),
            name: hit.name.as_str().into(),
            detail: parts.join("  ·  ").into(),
            description: hit.description.clone().unwrap_or_default().into(),
            price: format::price(hit.lowest_price, &hit.currency).into(),
            old_price: if has_drop && hit.old_price.is_some() { format!("was {}", format::price(hit.old_price, &hit.currency)).into() } else { "".into() },
            drop_text: if has_drop { format!("▼ {}", format::percent(hit.recent_drop_percent.unwrap())).into() } else { "".into() },
            condition: format::condition(hit.condition.as_deref()).unwrap_or("").into(),
            store_text: store_text.unwrap_or("").into(),
            verdict: insight.label().into(),
            verdict_kind: insight.chip_kind(),
            insight: if insight.has_verdict() || insight.year_low.is_some() { insight.summary(&hit.currency).into() } else { "".into() },
            pinned: self.state.borrow().pins.iter().any(|p| p.id == hit.id),
            image: self.image_for(hit.image_path.as_deref()),
        }
    }

    /// A product can be listed on both the search and the Black Friday tab; both show its pin.
    fn show_pinned(&self, id: &str, pinned: bool) {
        for i in 0..self.results.row_count() {
            if let Some(mut row) = self.results.row_data(i) {
                if row.id.as_str() == id {
                    row.pinned = pinned;
                    self.results.set_row_data(i, row);
                }
            }
        }
        self.deals_show_pinned(id, pinned);
    }

    // ----- pins -----

    pub fn toggle_pin(&self, id: &str) {
        if self.state.borrow().pins.iter().any(|p| p.id == id) {
            self.unpin(id);
            return;
        }
        let hit = self.result_hits.borrow().iter().find(|h| h.id == id).cloned().or_else(|| self.deals.borrow().items.iter().find(|h| h.id == id).cloned());
        let Some(hit) = hit else { return };
        let now = Utc::now();
        let product = PinnedProduct {
            id: hit.id.clone(),
            name: hit.name.clone(),
            url: hit.url.clone(),
            image_path: hit.image_path.clone(),
            category: hit.category.clone(),
            currency: hit.currency.clone(),
            pinned_at: now,
            pinned_price: hit.lowest_price.unwrap_or(0.0),
            current_price: hit.lowest_price,
            lowest_seen: hit.lowest_price,
            highest_seen: hit.lowest_price,
            lowest_seen_at: Some(now),
            reported_drop_percent: hit.recent_drop_percent,
            reported_old_price: hit.old_price,
            reported_drop_fetched: Some(now),
            history: hit.lowest_price.map(|p| vec![PricePoint { at: now, price: p }]).unwrap_or_default(),
            ..Default::default()
        };
        self.state.borrow_mut().pins.insert(0, product.clone());
        self.show_pinned(id, true);
        self.set_footer(format!("Pinned {}", hit.name));
        self.save();
        self.refresh_watchlist();
        self.update_status();
        self.check_pins(vec![product], false, true);
    }

    /// Stops tracking and forgets the product: its data, picture and notifications.
    pub fn unpin(&self, id: &str) {
        let removed = {
            let mut state = self.state.borrow_mut();
            let Some(i) = state.pins.iter().position(|p| p.id == id) else { return };
            state.pins.remove(i)
        };
        self.show_pinned(id, false);
        if !self.state.borrow().pins.iter().any(|p| p.image_path == removed.image_path) {
            if let Some(url) = Client::image_url(removed.image_path.as_deref(), 112) {
                self.images.delete(&url);
                self.image_cache.borrow_mut().remove(&url);
            }
        }
        self.set_footer(format!("Unpinned {} and deleted its data", removed.name));
        self.save();
        self.refresh_watchlist();
        self.update_status();
    }

    pub fn save_target(&self, id: &str, text: &str) {
        let cleaned: String = text.replace(' ', "").replace('\u{00A0}', "").replace("kr", "").replace("KR", "").replace(',', ".");
        let target = if cleaned.is_empty() {
            None
        } else {
            match cleaned.parse::<f64>() {
                Ok(v) if v > 0.0 => Some(v),
                _ => {
                    self.refresh_watchlist();
                    return;
                }
            }
        };
        let (changed, currency) = {
            let mut state = self.state.borrow_mut();
            let Some(pin) = state.pins.iter_mut().find(|p| p.id == id) else { return };
            if pin.target_price == target {
                (false, pin.currency.clone())
            } else {
                pin.target_price = target;
                (true, pin.currency.clone())
            }
        };
        if changed {
            self.set_footer(match target {
                Some(t) => format!("Alert set at {}", format::price(Some(t), &currency)),
                None => "Target alert removed".into(),
            });
            self.save();
            self.refresh_watchlist();
        }
    }

    pub fn set_interval(&self, index: i32) {
        let minutes = INTERVALS.get(index as usize).copied().unwrap_or(60);
        if self.state.borrow().settings.refresh_minutes != minutes {
            self.state.borrow_mut().settings.refresh_minutes = minutes;
            self.save();
            self.update_status();
        }
    }

    pub fn start_with_windows(&self) -> bool {
        self.state.borrow().settings.start_with_windows
    }

    /// Turns sign-in startup on or off. Returns the resulting state (unchanged if Windows refused).
    pub fn toggle_start_with_windows(&self) -> bool {
        let enabled = !self.start_with_windows();
        if !win::set_startup(enabled) {
            self.set_footer("Windows refused to change the startup setting".into());
            return self.start_with_windows();
        }
        self.state.borrow_mut().settings.start_with_windows = enabled;
        self.set_footer(if enabled { "Starts with Windows (hidden in the tray)".into() } else { "No longer starts with Windows".into() });
        self.save();
        if let Some(item) = self.autostart_item.borrow().as_ref() {
            item.set_checked(enabled);
        }
        enabled
    }

    pub fn open_product(&self, id: &str) {
        let url = self.state.borrow().pins.iter().find(|p| p.id == id).map(|p| p.url.clone())
            .or_else(|| self.result_hits.borrow().iter().find(|h| h.id == id).map(|h| h.url.clone()))
            .or_else(|| self.deals.borrow().items.iter().find(|h| h.id == id).map(|h| h.url.clone()));
        if let Some(url) = url {
            self.open_url(&Client::product_page_url(&url));
        }
    }

    pub fn open_store(&self, id: &str) {
        let url = self.state.borrow().pins.iter().find(|p| p.id == id).and_then(|p| p.cheapest_offer_url.clone());
        if let Some(url) = url {
            self.open_url(&url);
        }
    }

    pub fn open_url(&self, url: &str) {
        if !win::open_url(url) {
            self.set_footer("Could not open the browser".into());
        }
    }

    // ----- checks -----

    fn is_due(&self) -> bool {
        let state = self.state.borrow();
        !state.pins.is_empty() && state.last_refresh.map_or(true, |last| Utc::now() - last >= Duration::minutes(state.settings.refresh_minutes))
    }

    /// The tray and the window's first show call this: checks right away when the last check is stale.
    pub fn start(&self) {
        if self.is_due() {
            self.check_all();
        } else {
            let missing: Vec<PinnedProduct> = self.state.borrow().pins.iter().filter(|p| !p.group_resolved).cloned().collect();
            if !missing.is_empty() {
                let (client, ui) = (self.client.clone(), self.ui.clone());
                std::thread::spawn(move || {
                    for mut pin in missing {
                        if tracker::resolve_group(&client, &mut pin) {
                            post(&ui, move |c| c.on_group_resolved(pin));
                        }
                    }
                });
            }
        }
    }

    fn on_group_resolved(&self, resolved: PinnedProduct) {
        if let Some(pin) = self.state.borrow_mut().pins.iter_mut().find(|p| p.id == resolved.id) {
            pin.group_key = resolved.group_key;
            pin.group_name = resolved.group_name;
            pin.variant = resolved.variant;
            pin.group_resolved = true;
        }
        self.save();
        self.refresh_watchlist();
    }

    pub fn check_all(&self) {
        if self.checking_all.get() || self.state.borrow().pins.is_empty() {
            return;
        }
        self.checking_all.set(true);
        self.app().set_checking(true);
        let pins = self.state.borrow().pins.clone();
        self.check_pins(pins, true, false);
    }

    pub fn check_one(&self, id: &str) {
        let pin = self.state.borrow().pins.iter().find(|p| p.id == id).cloned();
        if let Some(pin) = pin {
            self.check_pins(vec![pin], true, false);
        }
    }

    /// Checks the given pins one after another on a worker thread.
    fn check_pins(&self, pins: Vec<PinnedProduct>, notify: bool, baseline: bool) {
        let (client, ui) = (self.client.clone(), self.ui.clone());
        let all = pins.len() > 1 || self.checking_all.get();
        let total = pins.len();
        std::thread::Builder::new()
            .name("price-check".into())
            .spawn(move || {
                for (i, mut pin) in pins.into_iter().enumerate() {
                    let id = pin.id.clone();
                    post(&ui, move |c| c.on_check_started(all, i, total, &id));
                    let groups_changed = tracker::resolve_group(&client, &mut pin);
                    let check = tracker::check(&client, &mut pin);
                    post(&ui, move |c| c.on_check_result(CheckOutcome { pin, check, notify, baseline, groups_changed }));
                    if i + 1 < total {
                        std::thread::sleep(BETWEEN_CHECKS);
                    }
                }
                if all {
                    post(&ui, |c| c.on_all_checked());
                }
            })
            .expect("check thread");
    }

    fn on_check_started(&self, all: bool, index: usize, total: usize, id: &str) {
        if all {
            self.app().set_status_text(format!("CHECKING {}/{}", index + 1, total).into());
        }
        self.checking_ids.borrow_mut().insert(id.to_string());
        self.refresh_watchlist();
    }

    fn on_check_result(&self, outcome: CheckOutcome) {
        self.checking_ids.borrow_mut().remove(&outcome.pin.id);
        let still_pinned = {
            let mut state = self.state.borrow_mut();
            match state.pins.iter_mut().find(|p| p.id == outcome.pin.id) {
                Some(existing) => {
                    let target = existing.target_price;
                    *existing = outcome.pin.clone();
                    existing.target_price = target;
                    if outcome.baseline {
                        if let (Some(_), Some(live)) = (&outcome.check, existing.current_price) {
                            existing.pinned_price = live;
                            existing.lowest_seen = Some(live);
                            existing.highest_seen = Some(live);
                            existing.history = vec![PricePoint { at: Utc::now(), price: live }];
                        }
                    }
                    true
                }
                None => false,
            }
        };
        if !still_pinned {
            return;
        }
        if let (true, Some(check)) = (outcome.notify, &outcome.check) {
            self.notify(&outcome.pin, check);
        }
        if outcome.groups_changed || outcome.baseline {
            self.save();
        }
        self.refresh_watchlist();
        if !self.checking_all.get() {
            self.save();
            self.update_status();
        }
    }

    fn on_all_checked(&self) {
        self.state.borrow_mut().last_refresh = Some(Utc::now());
        self.save();
        self.checking_all.set(false);
        self.app().set_checking(false);
        self.update_status();
        self.refresh_watchlist();
    }

    fn notify(&self, pin: &PinnedProduct, check: &PriceCheck) {
        let insight = analysis::analyze(pin.current_price, &pin.market_history, Local::now().date_naive());
        let price = format::price(Some(check.current), &pin.currency);
        let where_ = check.merchant.as_deref().filter(|m| !m.is_empty()).map(|m| format!(" at {m}")).unwrap_or_default();
        // With PriceRunner's history, "new low" means below anything in the past year, not just since pinning.
        let is_new_low = check.drop_percent().is_some() && insight.year_low.as_ref().map_or(check.is_new_low, |l| check.current < l.price);
        let title = if check.reached_target {
            "🎯 Target price reached".to_string()
        } else if insight.verdict == analysis::Verdict::Inflated && check.drop_percent().is_some() {
            self.set_footer(format!("Ignored −{} on {}: it only undoes a recent price rise", format::percent(check.drop_percent().unwrap()), pin.name));
            return;
        } else if is_new_low {
            if insight.year_low.is_some() { "⬇ Lowest price in a year".into() } else { "⬇ New lowest price".into() }
        } else if let Some(drop) = check.drop_percent().filter(|d| *d >= self.state.borrow().settings.min_drop_percent) {
            format!("⬇ Price drop −{}", format::percent(drop))
        } else {
            return;
        };
        let was = check.previous.map(|p| format!(" (was {})", format::price(Some(p), &pin.currency))).unwrap_or_default();
        let context = if insight.has_verdict() {
            format!("\n{}  ·  {}", insight.label().trim_start_matches(['✓', ' ']), insight.summary(&pin.currency))
        } else if insight.year_low.is_some() {
            format!("\n{}", insight.summary(&pin.currency))
        } else {
            String::new()
        };
        self.set_footer(format!("{title}: {}", pin.name));
        win::toast(&title, &format!("{}\n{price}{where_}{was}{context}", pin.name), Some(Client::product_page_url(&pin.url)));
    }

    // ----- clock, status, footer -----

    /// Every 30 seconds: a due check, or just the "Checked … ago" texts.
    pub fn tick(&self) {
        if self.is_due() {
            self.check_all();
        } else if !self.checking_all.get() {
            self.refresh_watchlist();
            self.update_status();
        }
    }

    pub fn update_status(&self) {
        if self.checking_all.get() {
            return;
        }
        let app = self.app();
        let state = self.state.borrow();
        if state.pins.is_empty() {
            app.set_status_text("NOTHING PINNED".into());
            return;
        }
        let next = state.last_refresh.unwrap_or_else(Utc::now) + Duration::minutes(state.settings.refresh_minutes) - Utc::now();
        app.set_status_text(format!("{} PINNED · NEXT {}", state.pins.len(), format::countdown(next.num_seconds())).into());
        let footer = self.footer.borrow().clone();
        if footer.is_empty() || footer.starts_with("Last check") {
            drop(state);
            self.set_footer(format!("Last check {}", format::ago(self.state.borrow().last_refresh, Utc::now())));
        }
    }

    pub fn set_footer(&self, text: String) {
        self.app().set_footer_text(text.as_str().into());
        *self.footer.borrow_mut() = text;
    }

    pub fn save(&self) {
        self.store.save(&self.state.borrow());
    }

    // ----- watchlist -----

    /// Rebuilds the watchlist as shown: pins grouped so the variants of one product share a box.
    pub fn refresh_watchlist(&self) {
        let state = self.state.borrow();
        let checking = self.checking_ids.borrow();
        let today = Local::now().date_naive();
        let clusters = analysis::cluster(&state.pins);
        let mut groups = Vec::with_capacity(clusters.len());
        for cluster in clusters {
            let is_group = cluster.members.len() > 1;
            let items: Vec<PinCard> = cluster
                .members
                .iter()
                .map(|(i, variant)| {
                    let pin = &state.pins[*i];
                    let label = if is_group { Some(variant.clone().unwrap_or_else(|| pin.name.clone())) } else { None };
                    self.pin_card(pin, label, checking.contains(&pin.id), today)
                })
                .collect();
            let cheapest = cluster
                .members
                .iter()
                .filter_map(|(i, v)| state.pins[*i].current_price.map(|p| (p, *i, v.clone())))
                .min_by(|a, b| a.0.partial_cmp(&b.0).unwrap());
            let summary = cheapest
                .map(|(p, i, v)| format!("Cheapest: {} · {}", v.unwrap_or_else(|| state.pins[i].name.clone()), format::price(Some(p), &state.pins[i].currency)))
                .unwrap_or_default();
            groups.push(PinGroup {
                title: cluster.title.into(),
                is_group,
                count_text: format!("{} VARIANTS", cluster.members.len()).into(),
                summary: summary.into(),
                items: ModelRc::new(VecModel::from(items)),
            });
        }
        drop(checking);
        let app = self.app();
        app.set_has_pins(!state.pins.is_empty());
        app.set_watchlist_label(if state.pins.is_empty() { "WATCHLIST".into() } else { format!("WATCHLIST · {}", state.pins.len()).into() });
        drop(state);
        self.groups.set_vec(groups);
    }

    fn pin_card(&self, pin: &PinnedProduct, variant: Option<String>, checking: bool, today: chrono::NaiveDate) -> PinCard {
        let insight = analysis::analyze(pin.current_price, &pin.market_history, today);
        let cur = &pin.currency;
        let now = pin.current_price;
        let (change_text, trend) = match now {
            Some(n) if pin.pinned_price > 0.0 && n != pin.pinned_price => {
                let delta = n - pin.pinned_price;
                let pct = delta.abs() / pin.pinned_price * 100.0;
                (format!("{} {} ({})", if delta < 0.0 { "▼" } else { "▲" }, format::price(Some(delta.abs()), cur), format::percent(pct)), if delta < 0.0 { 1 } else { 2 })
            }
            _ => ("± 0 since pinned".to_string(), 0),
        };
        let min = |a: Option<f64>, b: Option<f64>| match (a, b) {
            (Some(x), Some(y)) => Some(x.min(y)),
            _ => a.or(b),
        };
        let max = |a: Option<f64>, b: Option<f64>| match (a, b) {
            (Some(x), Some(y)) => Some(x.max(y)),
            _ => a.or(b),
        };
        let has_year = insight.year_low.is_some();
        let low = min(insight.year_low.as_ref().map(|p| p.price), pin.lowest_seen);
        let high = max(insight.year_high.as_ref().map(|p| p.price), pin.highest_seen);
        let at_lowest = matches!((now, low, high), (Some(n), Some(l), Some(h)) if n <= l && h > n);
        let below_target = matches!((pin.target_price, now), (Some(t), Some(n)) if n <= t);
        let deal_position = match (now, low, high) {
            (Some(n), Some(l), Some(h)) if h > l => ((n - l) / (h - l)) as f32,
            _ => -1.0,
        };
        let store_text = match (&pin.cheapest_merchant, pin.offer_count) {
            (Some(m), _) if !m.is_empty() => format!("Cheapest at {m}  ·  {} offers", pin.offer_count),
            (_, n) if n > 0 => format!("{n} offers"),
            _ => pin.category.clone().unwrap_or_default(),
        };
        let target_hint = match pin.target_price {
            Some(_) if below_target => "Target reached!".to_string(),
            Some(t) => format!("{} to go", format::price(now.map(|n| n - t), cur)),
            None => "Alert when the price is at or below".to_string(),
        };

        // The last 90 days of PriceRunner's history and today's price; our own checks until that is fetched.
        let values: Vec<f64> = if pin.market_history.is_empty() {
            pin.history.iter().map(|p| p.price).collect()
        } else {
            let from = today - Days::new(SPARKLINE_DAYS);
            let mut v: Vec<f64> = pin.market_history.iter().filter(|p| p.day >= from && p.day < today).map(|p| p.price).collect();
            v.extend(now);
            v
        };
        let spark = sparkline(&values);

        PinCard {
            id: pin.id.as_str().into(),
            name: pin.name.as_str().into(),
            image: self.image_for(pin.image_path.as_deref()),
            price: format::price(now.or(Some(pin.pinned_price)), cur).into(),
            change_text: change_text.into(),
            trend,
            store_text: store_text.into(),
            insight: if has_year { insight.summary(cur).into() } else { "".into() },
            variant: variant.unwrap_or_default().into(),
            verdict: insight.label().into(),
            verdict_kind: insight.chip_kind(),
            condition: format::condition(pin.cheapest_condition.as_deref()).unwrap_or("").into(),
            looks_used: pin.cheapest_looks_used_title.is_some(),
            reported_drop: pin.reported_drop_percent.filter(|p| *p > 0.5).map(|p| format!("PRICERUNNER −{}", format::percent(p))).unwrap_or_default().into(),
            at_lowest,
            lowest_chip: if has_year { "1-YEAR LOW".into() } else { "LOWEST SEEN".into() },
            below_target,
            spark_line: spark.line.into(),
            spark_area: spark.area.into(),
            spark_low_x: spark.low.0,
            spark_low_y: spark.low.1,
            spark_has_low: spark.has_low,
            spark_last_x: spark.last.0,
            spark_last_y: spark.last.1,
            spark_empty: values.is_empty(),
            deal_position,
            low_text: format!("{} {}", if has_year { "1y low" } else { "Low" }, format::price(low, cur)).into(),
            high_text: format!("{} {}", if has_year { "1y high" } else { "High" }, format::price(high, cur)).into(),
            target_input: pin.target_price.map(|t| trim_zeros(t)).unwrap_or_default().into(),
            target_hint: target_hint.into(),
            checked_text: format!("Checked {}", format::ago(pin.last_checked, Utc::now())).into(),
            error_text: pin.last_error.clone().unwrap_or_default().into(),
            has_store_link: pin.cheapest_offer_url.as_deref().map_or(false, |u| !u.is_empty()),
            checking,
        }
    }
}

fn trim_zeros(v: f64) -> String {
    let s = format!("{v:.2}");
    s.trim_end_matches('0').trim_end_matches('.').to_string()
}

struct Spark {
    line: String,
    area: String,
    low: (f32, f32),
    has_low: bool,
    last: (f32, f32),
}

/// A step line (prices hold until they change) in a 0..1000 by 0..40 box, like Sparkline.cs draws it.
fn sparkline(values: &[f64]) -> Spark {
    const W: f64 = 1000.0;
    const H: f64 = 40.0;
    const PAD_X: f64 = 17.0;
    const PAD_Y: f64 = 7.0;
    if values.is_empty() {
        return Spark { line: String::new(), area: String::new(), low: (0.0, 0.0), has_low: false, last: (0.0, 0.0) };
    }
    let series: Vec<f64> = if values.len() == 1 { vec![values[0], values[0]] } else { values.to_vec() };
    let min = series.iter().cloned().fold(f64::INFINITY, f64::min);
    let max = series.iter().cloned().fold(f64::NEG_INFINITY, f64::max);
    let range = max - min;
    let n = series.len();
    let x = |i: usize| PAD_X + (W - 2.0 * PAD_X) * i as f64 / (n - 1) as f64;
    let y = |v: f64| if range < 1e-9 { H / 2.0 } else { PAD_Y + (H - 2.0 * PAD_Y) * (1.0 - (v - min) / range) };
    let mut line = format!("M {:.1} {:.2}", x(0), y(series[0]));
    let mut area = format!("M {:.1} {H} L {:.1} {:.2}", x(0), x(0), y(series[0]));
    for i in 1..n {
        let seg = format!(" L {:.1} {:.2} L {:.1} {:.2}", x(i), y(series[i - 1]), x(i), y(series[i]));
        line += &seg;
        area += &seg;
    }
    area += &format!(" L {:.1} {H} Z", x(n - 1));
    let low_index = if values.len() == 1 { 0 } else { (0..n).find(|i| series[*i] <= min).unwrap_or(0) };
    Spark {
        line,
        area,
        low: ((x(low_index) / W) as f32, y(min) as f32),
        has_low: range > 1e-9,
        last: ((x(n - 1) / W) as f32, y(series[n - 1]) as f32),
    }
}
