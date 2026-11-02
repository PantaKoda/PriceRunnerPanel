//! Checks a pinned product's price and folds the result into its history (PriceTracker.cs). Runs on a worker
//! thread with its own copy of the pin; the UI thread merges the result back.

use crate::api::{ApiResult, Client};
use crate::format;
use crate::model::{PinnedProduct, PricePoint};
use chrono::{Duration, Utc};

const MAX_HISTORY_POINTS: usize = 1000;
const MARKET_HISTORY_AGE_HOURS: i64 = 12;

/// What one price check found, compared with what we knew before it.
#[derive(Clone, Debug)]
pub struct PriceCheck {
    pub previous: Option<f64>,
    pub current: f64,
    pub is_new_low: bool,
    pub reached_target: bool,
    pub merchant: Option<String>,
}

impl PriceCheck {
    pub fn drop_percent(&self) -> Option<f64> {
        match self.previous {
            Some(p) if p > 0.0 && self.current < p => Some((p - self.current) / p * 100.0),
            _ => None,
        }
    }
}

/// Looks the variant group up once; a failure is retried with the next check.
pub fn resolve_group(client: &Client, pin: &mut PinnedProduct) -> bool {
    if pin.group_resolved {
        return false;
    }
    match client.product_group(&pin.id, &pin.url) {
        Ok(group) => {
            pin.group_key = group.as_ref().map(|g| g.key.clone());
            pin.group_name = group.as_ref().map(|g| g.name.clone());
            pin.variant = group.as_ref().and_then(|g| g.variants.get(&pin.id).cloned());
            pin.group_resolved = true;
            true
        }
        Err(_) => false,
    }
}

pub fn check(client: &Client, pin: &mut PinnedProduct) -> Option<PriceCheck> {
    let snapshot = match client.price(&pin.id) {
        Ok(s) => s,
        Err(e) => {
            pin.last_error = Some(if e.timed_out { "Timed out".into() } else { e.message });
            pin.last_checked = Some(Utc::now());
            return None;
        }
    };
    let now = Utc::now();
    pin.last_checked = Some(now);
    let Some(price) = snapshot.tracked_price() else {
        pin.last_error = Some("No store offers this product right now".into());
        pin.offer_count = 0;
        return None;
    };

    let previous = pin.current_price;
    let is_new_low = pin.lowest_seen.map_or(false, |low| price < low);
    let reached_target = pin.target_price.map_or(false, |t| price <= t && previous.map_or(true, |p| p > t));

    pin.last_error = None;
    pin.current_price = Some(price);
    pin.currency = snapshot.currency.clone();
    pin.offer_count = snapshot.offer_count;
    pin.cheapest_merchant = snapshot.cheapest_merchant.clone();
    pin.cheapest_offer_url = snapshot.cheapest_offer_url.clone();
    pin.cheapest_condition = if format::condition(snapshot.cheapest_condition.as_deref()).is_some() { snapshot.cheapest_condition.clone() } else { None };
    pin.cheapest_looks_used_title =
        if pin.cheapest_condition.is_none() && format::looks_used(snapshot.cheapest_offer_name.as_deref()) { snapshot.cheapest_offer_name.clone() } else { None };
    if pin.lowest_seen.map_or(true, |l| price < l) {
        pin.lowest_seen = Some(price);
        pin.lowest_seen_at = Some(now);
    }
    if pin.highest_seen.map_or(true, |h| price > h) {
        pin.highest_seen = Some(price);
    }
    if pin.history.last().map_or(true, |p| p.price != price) {
        pin.history.push(PricePoint { at: now, price });
        if pin.history.len() > MAX_HISTORY_POINTS {
            let extra = pin.history.len() - MAX_HISTORY_POINTS;
            pin.history.drain(..extra);
        }
    }

    refresh_market_history(client, pin);
    refresh_reported_drop(client, pin);
    Some(PriceCheck { previous, current: price, is_new_low, reached_target, merchant: snapshot.cheapest_merchant })
}

fn fresh(fetched: Option<chrono::DateTime<Utc>>) -> bool {
    fetched.map_or(false, |f| Utc::now() - f < Duration::hours(MARKET_HISTORY_AGE_HOURS))
}

/// PriceRunner's year of daily prices changes once a day; fetched at most every 12 hours.
fn refresh_market_history(client: &Client, pin: &mut PinnedProduct) {
    if fresh(pin.market_history_fetched) {
        return;
    }
    if let Ok(history) = client.price_history(&pin.id) {
        pin.market_history = history;
        pin.market_history_fetched = Some(Utc::now());
    }
}

/// The drop PriceRunner itself shows for the product, read from its row in a search for its name.
fn refresh_reported_drop(client: &Client, pin: &mut PinnedProduct) {
    if fresh(pin.reported_drop_fetched) {
        return;
    }
    let hits: ApiResult<_> = client.search(&pin.name);
    if let Ok(hits) = hits {
        if let Some(hit) = hits.iter().find(|h| h.id == pin.id) {
            pin.reported_drop_percent = hit.recent_drop_percent;
            pin.reported_old_price = hit.old_price;
            pin.reported_drop_fetched = Some(Utc::now());
        }
    }
}
