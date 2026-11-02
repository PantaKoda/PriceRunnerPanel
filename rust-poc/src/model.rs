//! The data the app keeps, in the same JSON shape as the .NET app's state.json (PascalCase names, so a watchlist
//! written by either app loads in the other).

use chrono::{DateTime, NaiveDate, Utc};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// One product row from a PriceRunner search.
#[derive(Clone, Debug)]
pub struct SearchHit {
    pub id: String,
    pub name: String,
    pub description: Option<String>,
    pub category: Option<String>,
    pub url: String,
    pub image_path: Option<String>,
    pub lowest_price: Option<f64>,
    pub currency: String,
    pub recent_drop_percent: Option<f64>,
    pub old_price: Option<f64>,
    pub rating: Option<f64>,
    pub rating_count: i64,
    pub store_count: i64,
    pub out_of_stock: bool,
    pub condition: Option<String>,
}

/// What to list from PriceRunner's deals.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DealFilter {
    pub categories: Vec<i64>,
    pub merchants: Vec<i64>,
    pub brands: Vec<i64>,
    pub min_drop_percent: i64,
    pub max_drop_percent: i64,
}

pub struct DealsPage {
    pub total: i64,
    pub hits: Vec<SearchHit>,
}

#[derive(Clone, Debug)]
pub struct DealFacetOption {
    pub id: i64,
    pub name: String,
    pub count: i64,
}

pub struct DealFacets {
    pub categories: Vec<DealFacetOption>,
    pub merchants: Vec<DealFacetOption>,
    pub brands: Vec<DealFacetOption>,
}

/// The current offers for one product.
pub struct PriceSnapshot {
    pub lowest_in_stock: Option<f64>,
    pub lowest_overall: Option<f64>,
    pub currency: String,
    pub offer_count: i64,
    pub cheapest_merchant: Option<String>,
    pub cheapest_offer_url: Option<String>,
    pub cheapest_condition: Option<String>,
    pub cheapest_offer_name: Option<String>,
}

impl PriceSnapshot {
    pub fn tracked_price(&self) -> Option<f64> {
        self.lowest_in_stock.or(self.lowest_overall)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct PricePoint {
    pub at: DateTime<Utc>,
    pub price: f64,
}

/// PriceRunner's lowest price on one day, and the store that had it.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct MarketPoint {
    pub day: NaiveDate,
    pub price: f64,
    pub merchant: Option<String>,
}

pub struct ProductGroup {
    pub key: String,
    pub name: String,
    pub variants: HashMap<String, String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PinnedProduct {
    pub id: String,
    pub name: String,
    pub url: String,
    pub image_path: Option<String>,
    pub category: Option<String>,
    pub currency: String,
    pub pinned_at: DateTime<Utc>,
    pub pinned_price: f64,
    pub current_price: Option<f64>,
    pub lowest_seen: Option<f64>,
    pub lowest_seen_at: Option<DateTime<Utc>>,
    pub highest_seen: Option<f64>,
    pub target_price: Option<f64>,
    pub last_checked: Option<DateTime<Utc>>,
    pub last_error: Option<String>,
    pub cheapest_merchant: Option<String>,
    pub cheapest_offer_url: Option<String>,
    pub offer_count: i64,
    pub cheapest_condition: Option<String>,
    pub cheapest_looks_used_title: Option<String>,
    pub group_key: Option<String>,
    pub group_name: Option<String>,
    pub variant: Option<String>,
    pub group_resolved: bool,
    pub history: Vec<PricePoint>,
    pub market_history: Vec<MarketPoint>,
    pub market_history_fetched: Option<DateTime<Utc>>,
    pub reported_drop_percent: Option<f64>,
    pub reported_old_price: Option<f64>,
    pub reported_drop_fetched: Option<DateTime<Utc>>,
}

impl Default for PinnedProduct {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            url: String::new(),
            image_path: None,
            category: None,
            currency: "SEK".into(),
            pinned_at: Utc::now(),
            pinned_price: 0.0,
            current_price: None,
            lowest_seen: None,
            lowest_seen_at: None,
            highest_seen: None,
            target_price: None,
            last_checked: None,
            last_error: None,
            cheapest_merchant: None,
            cheapest_offer_url: None,
            offer_count: 0,
            cheapest_condition: None,
            cheapest_looks_used_title: None,
            group_key: None,
            group_name: None,
            variant: None,
            group_resolved: false,
            history: Vec::new(),
            market_history: Vec::new(),
            market_history_fetched: None,
            reported_drop_percent: None,
            reported_old_price: None,
            reported_drop_fetched: None,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppSettings {
    pub refresh_minutes: i64,
    pub start_with_windows: bool,
    pub start_menu_shortcut_created: bool,
    pub notified_update: Option<String>,
    pub min_drop_percent: f64,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self { refresh_minutes: 60, start_with_windows: true, start_menu_shortcut_created: false, notified_update: None, min_drop_percent: 1.0 }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppState {
    pub settings: AppSettings,
    pub pins: Vec<PinnedProduct>,
    pub last_refresh: Option<DateTime<Utc>>,
}
