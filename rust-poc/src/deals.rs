//! The Black Friday tab (DealsViewModel.cs + DealFilterViewModel.cs): PriceRunner's current deals, filtered by
//! category, store and brand, searched by reading through them. Nothing here is saved.

use crate::analysis::{self, Insight};
use crate::api::{self, Client};
use crate::controller::{post, with, Controller};
use crate::format;
use crate::model::*;
use crate::{FilterOption, ResultRow};
use chrono::Local;
use slint::{Model, ModelRc, Timer, TimerMode, VecModel};
use std::collections::{HashMap, HashSet, VecDeque};
use std::rc::Rc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

const FILTER_DEBOUNCE: Duration = Duration::from_millis(400);
/// Deals change through the day: reopening the tab after this long fetches them again.
const STALE: Duration = Duration::from_secs(30 * 60);
/// Pause between requests when filling in listings, to stay a polite client.
const BETWEEN_DETAILS: Duration = Duration::from_millis(300);
pub const DEFAULT_MIN_DROP: i64 = 10;
pub const MAX_DROP_LIMIT: i64 = 90;
/// A search stops to ask after reading this many deals, or finding this many matches.
const SCAN_LIMIT: i64 = 2000;
const MATCH_LIMIT: usize = 100;
const TOP_COUNT: usize = 5;

/// One deals filter (category, store or brand): the choices PriceRunner lists and what is ticked.
#[derive(Default)]
pub struct Filter {
    pub options: Vec<DealFacetOption>,
    /// Ticked choices stay, also when they have no deals right now (then with count 0).
    pub selected: Vec<(i64, String)>,
    pub search: String,
}

impl Filter {
    fn is_selected(&self, id: i64) -> bool {
        self.selected.iter().any(|s| s.0 == id)
    }

    fn selected_ids(&self) -> Vec<i64> {
        self.selected.iter().map(|s| s.0).collect()
    }

    /// All choices as shown: ticked ones first (even unlisted), then PriceRunner's list.
    fn all(&self) -> Vec<DealFacetOption> {
        let listed: HashSet<i64> = self.options.iter().map(|o| o.id).collect();
        let mut all: Vec<DealFacetOption> = self.selected.iter().filter(|s| !listed.contains(&s.0)).map(|s| DealFacetOption { id: s.0, name: s.1.clone(), count: 0 }).collect();
        all.extend(self.options.iter().cloned());
        all
    }

    fn label(&self, title: &str) -> String {
        match self.selected.len() {
            0 => title.to_string(),
            1 => self.selected[0].1.clone(),
            n => format!("{title} · {n}"),
        }
    }

    fn find(&self, name: &str) -> Option<&DealFacetOption> {
        self.options.iter().find(|o| o.name.eq_ignore_ascii_case(name))
    }
}

pub struct Deals {
    pub filters: [Filter; 3],
    pub min_drop: String,
    pub max_drop: String,
    pub items: Vec<SearchHit>,
    pub rows: Rc<VecModel<ResultRow>>,
    pub insights: HashMap<String, Insight>,
    pub store_texts: HashMap<String, String>,
    total: i64,
    offset: i64,
    scanned: i64,
    pub text: String,
    scan_filter: Option<DealFilter>,
    scan_words: Vec<String>,
    loaded_at: Option<Instant>,
    loading: bool,
    gen: Arc<AtomicU64>,
    timer: Timer,
    pending_details: VecDeque<String>,
    details_running: bool,
}

impl Default for Deals {
    fn default() -> Self {
        Self {
            filters: Default::default(),
            min_drop: DEFAULT_MIN_DROP.to_string(),
            max_drop: MAX_DROP_LIMIT.to_string(),
            items: Vec::new(),
            rows: Rc::new(VecModel::default()),
            insights: HashMap::new(),
            store_texts: HashMap::new(),
            total: 0,
            offset: 0,
            scanned: 0,
            text: String::new(),
            scan_filter: None,
            scan_words: Vec::new(),
            loaded_at: None,
            loading: false,
            gen: Arc::new(AtomicU64::new(0)),
            timer: Timer::default(),
            pending_details: VecDeque::new(),
            details_running: false,
        }
    }
}

impl Deals {
    fn is_searching(&self) -> bool {
        !self.text.trim().is_empty()
    }

    fn drop_range(&self) -> (i64, i64) {
        let read = |t: &str, fallback: i64| t.replace('%', "").trim().parse::<i64>().map(|p| p.clamp(1, MAX_DROP_LIMIT)).unwrap_or(fallback);
        let (min, max) = (read(&self.min_drop, DEFAULT_MIN_DROP), read(&self.max_drop, MAX_DROP_LIMIT));
        if min <= max { (min, max) } else { (max, min) }
    }

    fn has_filters(&self) -> bool {
        self.filters.iter().any(|f| !f.selected.is_empty()) || self.drop_range() != (DEFAULT_MIN_DROP, MAX_DROP_LIMIT)
    }

    fn current_filter(&self) -> DealFilter {
        let (min, max) = self.drop_range();
        DealFilter { categories: self.filters[0].selected_ids(), merchants: self.filters[1].selected_ids(), brands: self.filters[2].selected_ids(), min_drop_percent: min, max_drop_percent: max }
    }

    fn can_load_more(&self) -> bool {
        !self.loading && if self.is_searching() { self.scanned < self.total } else { !self.items.is_empty() && (self.items.len() as i64) < self.total }
    }
}

pub struct Loaded {
    gen: u64,
    page: api::ApiResult<DealsPage>,
    facets: api::ApiResult<DealFacets>,
}

fn matches(hit: &SearchHit, word: &str) -> bool {
    let w = word.to_lowercase();
    hit.name.to_lowercase().contains(&w) || hit.description.as_deref().map_or(false, |d| d.to_lowercase().contains(&w)) || hit.category.as_deref().map_or(false, |c| c.to_lowercase().contains(&w))
}

fn failure(e: &api::ApiError) -> String {
    if e.timed_out { "PriceRunner did not answer in time".into() } else { format!("Could not fetch deals: {}", e.message) }
}

impl Controller {
    /// The tab was opened: fetches the deals the first time, and again when they have gone stale.
    pub fn deals_opened(&self) {
        let stale = self.deals.borrow().loaded_at.map_or(true, |at| at.elapsed() >= STALE);
        if stale {
            self.deals_reload(false);
        }
    }

    pub fn deals_search_edited(&self, text: &str) {
        self.deals.borrow_mut().text = text.to_string();
        self.app().set_load_more_text(if text.trim().is_empty() { "Show more deals".into() } else { "Search more deals".into() });
        self.deals_reload(true);
    }

    pub fn deals_refresh(&self) {
        self.deals_reload(false);
    }

    pub fn deals_clear_filters(&self) {
        {
            let mut d = self.deals.borrow_mut();
            for f in d.filters.iter_mut() {
                f.selected.clear();
                f.search.clear();
            }
            d.min_drop = DEFAULT_MIN_DROP.to_string();
            d.max_drop = MAX_DROP_LIMIT.to_string();
        }
        self.deals_refresh_filters();
        self.deals_reload(false);
    }

    pub fn filter_search(&self, kind: i32, text: &str) {
        self.deals.borrow_mut().filters[kind as usize].search = text.to_string();
        self.deals_refresh_filters();
    }

    pub fn filter_toggled(&self, kind: i32, id: i64, on: bool) {
        {
            let mut d = self.deals.borrow_mut();
            let f = &mut d.filters[kind as usize];
            if on {
                if !f.is_selected(id) {
                    if let Some(name) = f.options.iter().find(|o| o.id == id).map(|o| o.name.clone()) {
                        f.selected.push((id, name));
                    }
                }
            } else {
                f.selected.retain(|s| s.0 != id);
            }
        }
        self.deals_refresh_filters();
        self.deals_reload(true);
    }

    pub fn filter_clear(&self, kind: i32) {
        {
            let mut d = self.deals.borrow_mut();
            d.filters[kind as usize].selected.clear();
            d.filters[kind as usize].search.clear();
        }
        self.deals_refresh_filters();
        self.deals_reload(false);
    }

    pub fn drop_edited(&self, min: &str, max: &str) {
        {
            let mut d = self.deals.borrow_mut();
            d.min_drop = min.to_string();
            d.max_drop = max.to_string();
        }
        self.deals_refresh_filters();
        self.deals_reload(true);
    }

    pub fn drop_preset(&self, min: i64) {
        {
            let mut d = self.deals.borrow_mut();
            d.min_drop = min.to_string();
            d.max_drop = MAX_DROP_LIMIT.to_string();
        }
        let app = self.app();
        app.set_min_drop(min.to_string().into());
        app.set_max_drop(MAX_DROP_LIMIT.to_string().into());
        self.deals_refresh_filters();
        self.deals_reload(true);
    }

    /// Pushes the filters' choices, labels and chips to the UI.
    fn deals_refresh_filters(&self) {
        let d = self.deals.borrow();
        let app = self.app();
        let options = |f: &Filter, kind: i32| -> ModelRc<FilterOption> {
            let text = f.search.trim().to_lowercase();
            let list: Vec<FilterOption> = f
                .all()
                .into_iter()
                .filter(|o| text.is_empty() || o.name.to_lowercase().contains(&text))
                .map(|o| FilterOption { id: o.id as i32, name: o.name.as_str().into(), count_text: format::count(o.count).into(), selected: f.is_selected(o.id), kind })
                .collect();
            ModelRc::new(VecModel::from(list))
        };
        let top = |f: &Filter, kind: i32| -> ModelRc<FilterOption> {
            let mut list: Vec<DealFacetOption> = f.options.iter().filter(|o| o.count > 0).take(TOP_COUNT).cloned().collect();
            for o in f.all() {
                if f.is_selected(o.id) && !list.iter().any(|t| t.id == o.id) {
                    list.push(o);
                }
            }
            ModelRc::new(VecModel::from(
                list.into_iter().map(|o| FilterOption { id: o.id as i32, name: o.name.as_str().into(), count_text: format::count(o.count).into(), selected: f.is_selected(o.id), kind }).collect::<Vec<_>>(),
            ))
        };
        app.set_category_options(options(&d.filters[0], 0));
        app.set_store_options(options(&d.filters[1], 1));
        app.set_brand_options(options(&d.filters[2], 2));
        app.set_brand_top(top(&d.filters[2], 2));
        app.set_store_top(top(&d.filters[1], 1));
        app.set_category_label(d.filters[0].label("Category").into());
        app.set_store_label(d.filters[1].label("Store").into());
        app.set_category_active(!d.filters[0].selected.is_empty());
        app.set_store_active(!d.filters[1].selected.is_empty());
        app.set_category_has_selection(!d.filters[0].selected.is_empty());
        app.set_store_has_selection(!d.filters[1].selected.is_empty());
        app.set_brand_has_selection(!d.filters[2].selected.is_empty());
        let active: Vec<FilterOption> = (0..3)
            .flat_map(|k| d.filters[k].selected.iter().map(move |s| (k, s)))
            .map(|(k, s)| FilterOption { id: s.0 as i32, name: s.1.as_str().into(), count_text: "".into(), selected: true, kind: k as i32 })
            .collect();
        app.set_active_filters(ModelRc::new(VecModel::from(active)));
        app.set_has_filters(d.has_filters());
        let (min, max) = d.drop_range();
        app.set_active_preset(if max == MAX_DROP_LIMIT && [10, 20, 30, 50].contains(&min) { min as i32 } else { 0 });
    }

    fn deals_set_loading(&self, loading: bool) {
        self.deals.borrow_mut().loading = loading;
        let app = self.app();
        app.set_deals_loading(loading);
        app.set_can_load_more(self.deals.borrow().can_load_more());
    }

    pub fn deals_reload(&self, debounce: bool) {
        let gen = self.deals.borrow().gen.fetch_add(1, Ordering::SeqCst) + 1;
        self.deals.borrow_mut().pending_details.clear();
        self.deals_set_loading(true);
        if debounce {
            self.deals.borrow().timer.start(TimerMode::SingleShot, FILTER_DEBOUNCE, move || with(|c| c.deals_fetch(gen)));
        } else {
            self.deals.borrow().timer.stop();
            self.deals_fetch(gen);
        }
    }

    fn deals_fetch(&self, gen: u64) {
        if gen != self.deals.borrow().gen.load(Ordering::SeqCst) {
            return;
        }
        self.app().set_deals_status("Fetching deals…".into());
        let (filter, size) = {
            let d = self.deals.borrow();
            (d.current_filter(), if d.is_searching() { api::DEALS_MAX_PAGE_SIZE } else { api::DEALS_PAGE_SIZE })
        };
        let (client, ui) = (self.client.clone(), self.ui.clone());
        std::thread::spawn(move || {
            let f2 = filter.clone();
            let c2 = client.clone();
            let facets_thread = std::thread::spawn(move || c2.deal_facets(&f2));
            let page = client.deals(&filter, 0, size);
            let facets = facets_thread.join().unwrap_or_else(|_| Err(api::ApiError { message: "facets failed".into(), timed_out: false }));
            post(&ui, move |c| c.on_deals_loaded(Loaded { gen, page, facets }));
        });
    }

    fn on_deals_loaded(&self, loaded: Loaded) {
        if loaded.gen != self.deals.borrow().gen.load(Ordering::SeqCst) {
            return;
        }
        let (page, facets) = match (loaded.page, loaded.facets) {
            (Ok(p), Ok(f)) => (p, f),
            (Err(e), _) | (_, Err(e)) => {
                self.app().set_deals_status(failure(&e).into());
                self.deals_set_loading(false);
                return;
            }
        };
        {
            let mut d = self.deals.borrow_mut();
            d.filters[0].options = facets.categories;
            d.filters[1].options = facets.merchants;
            d.filters[2].options = facets.brands;
            // Ticked choices that PriceRunner no longer lists keep their names, with no count.
            let old = std::mem::take(&mut d.items);
            let picked = old.iter().map(|h| h.image_path.as_deref()).collect::<Vec<_>>();
            drop(d);
            self.release_images(picked.into_iter());
            let mut d = self.deals.borrow_mut();
            d.rows.set_vec(Vec::new());
            d.insights.clear();
            d.store_texts.clear();
            d.offset = 0;
            d.scanned = 0;
            d.total = page.total;
            d.loaded_at = Some(Instant::now());
        }
        self.deals_refresh_filters();
        let searching = self.deals.borrow().is_searching();
        if searching {
            let (filter, words, total) = {
                let d = self.deals.borrow();
                let filter = d.current_filter();
                let (f, w) = plan_search(&d, filter, page.total);
                (f, w, page.total)
            };
            {
                let mut d = self.deals.borrow_mut();
                d.scan_filter = Some(filter.clone());
                d.scan_words = words.clone();
            }
            let first = if self.deals.borrow().scan_filter.as_ref() != Some(&self.deals.borrow().current_filter()) { None } else { Some(page) };
            self.deals_scan(loaded.gen, first, total);
        } else {
            self.deals_append(page.hits);
            self.deals_show_count();
            self.deals_set_loading(false);
        }
    }

    /// Reads through the filtered deals on a worker thread, posting matches as it goes, and pauses after a
    /// stretch for the user to continue.
    fn deals_scan(&self, gen: u64, first: Option<DealsPage>, total: i64) {
        let (filter, words, scanned, found, gen_ref) = {
            let d = self.deals.borrow();
            (d.scan_filter.clone().unwrap_or_else(|| d.current_filter()), d.scan_words.clone(), d.scanned, d.items.len(), d.gen.clone())
        };
        let (client, ui) = (self.client.clone(), self.ui.clone());
        std::thread::spawn(move || {
            let stop_at = scanned + SCAN_LIMIT;
            let mut scanned = scanned;
            let mut total = total;
            let mut matched = 0usize;
            let mut first = first;
            let mut first_page = true;
            while scanned < total && scanned < stop_at && matched < MATCH_LIMIT {
                if gen_ref.load(Ordering::SeqCst) != gen {
                    return;
                }
                let page = match first.take() {
                    Some(p) => p,
                    None => {
                        if !first_page || scanned > 0 {
                            std::thread::sleep(BETWEEN_DETAILS);
                        }
                        match client.deals(&filter, scanned, api::DEALS_MAX_PAGE_SIZE) {
                            Ok(p) => p,
                            Err(e) => {
                                post(&ui, move |c| {
                                    c.app().set_deals_status(failure(&e).into());
                                    c.deals_set_loading(false);
                                });
                                return;
                            }
                        }
                    }
                };
                first_page = false;
                if page.hits.is_empty() {
                    total = scanned;
                    break;
                }
                total = page.total;
                scanned += page.hits.len() as i64;
                let hits: Vec<SearchHit> = page.hits.into_iter().filter(|h| words.iter().all(|w| matches(h, w))).collect();
                matched += hits.len();
                let (s, t) = (scanned, total);
                post(&ui, move |c| {
                    if c.deals.borrow().gen.load(Ordering::SeqCst) != gen {
                        return;
                    }
                    {
                        let mut d = c.deals.borrow_mut();
                        d.total = t;
                        d.scanned = s;
                    }
                    c.deals_append(hits);
                    let (n, s) = (c.deals.borrow().items.len(), c.deals.borrow().scanned);
                    c.app().set_deals_status(format!("Searching…  {} found in {} deals", format::count(n as i64), format::count(s)).into());
                });
            }
            let _ = found;
            let t = total;
            post(&ui, move |c| {
                if c.deals.borrow().gen.load(Ordering::SeqCst) != gen {
                    return;
                }
                c.deals.borrow_mut().total = t;
                c.deals_show_search_count();
                c.deals_set_loading(false);
            });
        });
    }

    pub fn deals_load_more(&self) {
        let (loading, searching, gen, offset, filter, total) = {
            let d = self.deals.borrow();
            (d.loading, d.is_searching(), d.gen.load(Ordering::SeqCst), d.offset, d.current_filter(), d.total)
        };
        if loading {
            return;
        }
        self.deals_set_loading(true);
        if searching {
            self.deals_scan(gen, None, total);
            return;
        }
        let offset = offset + api::DEALS_PAGE_SIZE;
        let (client, ui) = (self.client.clone(), self.ui.clone());
        std::thread::spawn(move || {
            let page = client.deals(&filter, offset, api::DEALS_PAGE_SIZE);
            post(&ui, move |c| {
                if c.deals.borrow().gen.load(Ordering::SeqCst) != gen {
                    return;
                }
                match page {
                    Ok(page) => {
                        {
                            let mut d = c.deals.borrow_mut();
                            d.offset = offset;
                            d.total = if page.hits.is_empty() { d.items.len() as i64 } else { page.total };
                        }
                        c.deals_append(page.hits);
                        c.deals_show_count();
                    }
                    Err(e) => c.app().set_deals_status(failure(&e).into()),
                }
                c.deals_set_loading(false);
            });
        });
    }

    fn deals_append(&self, hits: Vec<SearchHit>) {
        let picked: Vec<String> = self.deals.borrow().filters[1].selected.iter().map(|s| s.1.clone()).collect();
        let details = self.deals.borrow().has_filters() || self.deals.borrow().is_searching();
        let shown: HashSet<String> = self.deals.borrow().items.iter().map(|h| h.id.clone()).collect();
        let mut new_rows = Vec::new();
        for hit in hits.into_iter().filter(|h| !shown.contains(&h.id)) {
            let store_text = if picked.is_empty() { None } else { Some(format!("At {}…", picked.join(", "))) };
            new_rows.push(self.result_row(&hit, None, store_text.as_deref()));
            let mut d = self.deals.borrow_mut();
            if let Some(t) = store_text {
                d.store_texts.insert(hit.id.clone(), t);
            }
            if details {
                d.pending_details.push_back(hit.id.clone());
            }
            d.items.push(hit);
        }
        for row in new_rows {
            self.deals.borrow().rows.push(row);
        }
        self.deals_run_details();
    }

    /// Fills in what the deals list doesn't carry, only while a filter narrows the list: each product's past
    /// year for the verdict, and with stores picked, what they ask. One product at a time with a pause.
    fn deals_run_details(&self) {
        let batch: Vec<String> = {
            let mut d = self.deals.borrow_mut();
            if d.details_running || d.pending_details.is_empty() {
                return;
            }
            d.details_running = true;
            d.pending_details.drain(..).collect()
        };
        let (gen, gen_ref, picked) = {
            let d = self.deals.borrow();
            (d.gen.load(Ordering::SeqCst), d.gen.clone(), d.filters[1].selected.clone())
        };
        let (client, ui) = (self.client.clone(), self.ui.clone());
        std::thread::spawn(move || {
            for id in batch {
                if gen_ref.load(Ordering::SeqCst) != gen {
                    break;
                }
                if !picked.is_empty() {
                    let prices = client.store_prices(&id);
                    let (id2, picked2) = (id.clone(), picked.clone());
                    post(&ui, move |c| c.on_store_prices(gen, &id2, &picked2, prices));
                    std::thread::sleep(BETWEEN_DETAILS);
                }
                if let Ok(history) = client.price_history(&id) {
                    let id2 = id.clone();
                    post(&ui, move |c| c.on_deal_history(gen, &id2, history));
                }
                std::thread::sleep(BETWEEN_DETAILS);
            }
            post(&ui, move |c| {
                c.deals.borrow_mut().details_running = false;
                c.deals_run_details();
            });
        });
    }

    fn deals_update_row(&self, id: &str) {
        let d = self.deals.borrow();
        let Some(i) = d.items.iter().position(|h| h.id == id) else { return };
        let row = self.result_row(&d.items[i], d.insights.get(id), d.store_texts.get(id).map(String::as_str));
        d.rows.set_row_data(i, row);
    }

    fn on_store_prices(&self, gen: u64, id: &str, picked: &[(i64, String)], prices: api::ApiResult<HashMap<i64, f64>>) {
        if self.deals.borrow().gen.load(Ordering::SeqCst) != gen {
            return;
        }
        let names = picked.iter().map(|p| p.1.as_str()).collect::<Vec<_>>().join(", ");
        let text = match prices {
            Ok(prices) => {
                let mut best: Vec<(&str, f64)> = picked.iter().filter_map(|p| prices.get(&p.0).map(|price| (p.1.as_str(), *price))).collect();
                best.sort_by(|a, b| a.1.partial_cmp(&b.1).unwrap());
                let d = self.deals.borrow();
                let hit = d.items.iter().find(|h| h.id == id);
                match (best.first(), hit) {
                    (Some((name, price)), Some(hit)) => {
                        let is_lowest = hit.lowest_price.map_or(true, |l| *price <= l);
                        format!("At {name}: {}{}", format::price(Some(*price), &hit.currency), if is_lowest { " (lowest)" } else { "" })
                    }
                    _ => format!("No new offer at {names}"),
                }
            }
            Err(_) => format!("At {names}"),
        };
        self.deals.borrow_mut().store_texts.insert(id.to_string(), text);
        self.deals_update_row(id);
    }

    fn on_deal_history(&self, gen: u64, id: &str, history: Vec<MarketPoint>) {
        if self.deals.borrow().gen.load(Ordering::SeqCst) != gen {
            return;
        }
        let price = self.deals.borrow().items.iter().find(|h| h.id == id).and_then(|h| h.lowest_price);
        let insight = analysis::analyze(price, &history, Local::now().date_naive());
        self.deals.borrow_mut().insights.insert(id.to_string(), insight);
        self.deals_update_row(id);
    }

    fn deals_show_count(&self) {
        let total = self.deals.borrow().total;
        self.app().set_deals_status(
            if total == 0 { "No deals match these filters".into() } else { format!("{} deals  ·  pin one to track its price", if total >= 10000 { "10 000+".to_string() } else { format::count(total) }).into() },
        );
    }

    fn deals_show_search_count(&self) {
        let d = self.deals.borrow();
        let n = d.items.len();
        let found = match n {
            0 => "No matches".to_string(),
            1 => "1 match".to_string(),
            n => format!("{} matches", format::count(n as i64)),
        };
        let status = if d.scanned >= d.total {
            format!("{found} in all {} deals", format::count(d.scanned))
        } else {
            format!("{found} in the first {} of {} deals", format::count(d.scanned), if d.total >= 10000 { "10 000+".to_string() } else { format::count(d.total) })
        };
        drop(d);
        self.app().set_deals_status(status.into());
    }

    pub fn deals_show_pinned(&self, id: &str, pinned: bool) {
        let d = self.deals.borrow();
        for i in 0..d.rows.row_count() {
            if let Some(mut row) = d.rows.row_data(i) {
                if row.id.as_str() == id {
                    row.pinned = pinned;
                    d.rows.set_row_data(i, row);
                }
            }
        }
    }

    pub fn deals_image_ready(&self, url: &str, image: &slint::Image) {
        let d = self.deals.borrow();
        for (i, hit) in d.items.iter().enumerate() {
            if Client::image_url(hit.image_path.as_deref(), 112).as_deref() == Some(url) {
                if let Some(mut row) = d.rows.row_data(i) {
                    row.image = image.clone();
                    d.rows.set_row_data(i, row);
                }
            }
        }
    }
}

/// What a search reads, and what it looks for there: a list too long to read through is first narrowed by any
/// word that is exactly the name of a brand or category.
fn plan_search(d: &Deals, mut filter: DealFilter, total: i64) -> (DealFilter, Vec<String>) {
    let words: Vec<String> = d.text.split_whitespace().map(str::to_string).collect();
    if total <= SCAN_LIMIT {
        return (filter, words);
    }
    let mut left = Vec::new();
    for word in words {
        if filter.brands.is_empty() && d.filters[2].find(&word).is_some() {
            filter.brands = vec![d.filters[2].find(&word).unwrap().id];
        } else if filter.categories.is_empty() && d.filters[0].find(&word).is_some() {
            filter.categories = vec![d.filters[0].find(&word).unwrap().id];
        } else {
            left.push(word);
        }
    }
    (filter, left)
}
