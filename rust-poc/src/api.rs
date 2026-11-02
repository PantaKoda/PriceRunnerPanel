//! Talks to the public JSON endpoints the pricerunner.se web app uses, as PriceRunnerClient.cs does. Read-only and
//! anonymous; one blocking HTTP agent shared by every worker thread.

use crate::model::*;
use chrono::{DateTime, NaiveDate, Utc};
use serde_json::Value;
use std::collections::{BTreeMap, HashMap};
use std::time::Duration;

pub const SITE: &str = "https://www.pricerunner.se";
pub const DEALS_PAGE_SIZE: i64 = 32;
/// The most deals PriceRunner returns in one request (200 is refused).
pub const DEALS_MAX_PAGE_SIZE: i64 = 100;
const MARKET: &str = "SE";
const IMAGE_HOST: &str = "https://owp.klarna.com";
const USER_AGENT: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";

#[derive(Debug, Clone)]
pub struct ApiError {
    pub message: String,
    pub timed_out: bool,
}

impl ApiError {
    fn new(message: impl Into<String>) -> Self {
        Self { message: message.into(), timed_out: false }
    }
}

impl From<ureq::Error> for ApiError {
    fn from(e: ureq::Error) -> Self {
        match e {
            ureq::Error::StatusCode(code) => ApiError::new(format!("PriceRunner answered {code}")),
            ureq::Error::Timeout(_) => ApiError { message: "Timed out".into(), timed_out: true },
            other => ApiError::new(other.to_string()),
        }
    }
}

impl From<serde_json::Error> for ApiError {
    fn from(e: serde_json::Error) -> Self {
        ApiError::new(format!("Unexpected data: {e}"))
    }
}

pub type ApiResult<T> = Result<T, ApiError>;

#[derive(Clone)]
pub struct Client {
    agent: ureq::Agent,
}

impl Client {
    pub fn new() -> Self {
        let config = ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_secs(20)))
            .user_agent(USER_AGENT)
            .build();
        Self { agent: config.into() }
    }

    /// A product's page on pricerunner.se, kept on that host whatever the data says.
    pub fn product_page_url(relative_url: &str) -> String {
        if relative_url.starts_with('/') && !relative_url.starts_with("//") {
            format!("{SITE}{relative_url}")
        } else if relative_url.starts_with("https://www.pricerunner.se/") && !relative_url.contains('@') {
            relative_url.to_string()
        } else {
            SITE.to_string()
        }
    }

    /// Thumbnail URL for an image path such as `/product/3216619431/Name.jpg`.
    pub fn image_url(image_path: Option<&str>, size: u32) -> Option<String> {
        let path = image_path?;
        if path.is_empty() {
            return None;
        }
        let rest = path.strip_prefix("/product").unwrap_or(path);
        Some(format!("{IMAGE_HOST}/product/{size}x{size}{rest}?ph=true"))
    }

    pub fn search(&self, query: &str) -> ApiResult<Vec<SearchHit>> {
        let url = format!("/se/api/search-edge-rest/public/search/v6/{MARKET}?q={}&facetsSize=10", encode(query));
        let doc = self.get_json(&url)?;
        Ok(doc.get("products").and_then(Value::as_array).map(|p| parse_hits(p)).unwrap_or_default())
    }

    /// Current deals, newest drops first.
    pub fn deals(&self, filter: &DealFilter, offset: i64, size: i64) -> ApiResult<DealsPage> {
        let url = format!(
            "/se/api/deals-edge-rest/public/search/deals/products/v3/{MARKET}?{}&sorting=LATESTPRICEDROP_DESC,GLOBALRANK_ASC&size={size}&offset={offset}",
            deal_filters(filter)
        );
        let doc = self.get_json(&url)?;
        let hits = doc.get("products").and_then(Value::as_array).map(|p| parse_hits(p)).unwrap_or_default();
        Ok(DealsPage { total: int(&doc, "totalHits"), hits })
    }

    pub fn deal_facets(&self, filter: &DealFilter) -> ApiResult<DealFacets> {
        let url = format!("/se/api/deals-edge-rest/public/search/deals/facets/{MARKET}?{}&size={DEALS_PAGE_SIZE}", deal_filters(filter));
        let doc = self.get_json(&url)?;
        let facets = obj(&doc, "facets");
        Ok(DealFacets {
            categories: parse_facet(obj(facets, "CATEGORY")),
            merchants: parse_facet(obj(facets, "MERCHANT_DEAL")),
            brands: parse_facet(obj(facets, "BRAND")),
        })
    }

    /// What each store asks for a product: its cheapest offer in stock, else its cheapest.
    pub fn store_prices(&self, product_id: &str) -> ApiResult<HashMap<i64, f64>> {
        let doc = self.get_json(&offers_url(product_id, "NEW,UNKNOWN"))?;
        let mut best: HashMap<i64, (f64, bool)> = HashMap::new();
        if let Some(offers) = doc.get("offers").and_then(Value::as_array) {
            for offer in offers {
                let (Some((price, _)), Some(store)) = (offer_price(offer), str_(offer, "merchantId").and_then(|s| s.parse::<i64>().ok())) else { continue };
                let in_stock = str_(offer, "stockStatus") == Some("IN_STOCK");
                let better = match best.get(&store) {
                    None => true,
                    Some(&(current, current_stock)) => (in_stock && !current_stock) || (in_stock == current_stock && price < current),
                };
                if better {
                    best.insert(store, (price, in_stock));
                }
            }
        }
        Ok(best.into_iter().map(|(k, v)| (k, v.0)).collect())
    }

    /// A product's current price from new offers; a refurbished listing has none, so then its used offers.
    pub fn price(&self, product_id: &str) -> ApiResult<PriceSnapshot> {
        let snapshot = self.read_price(&offers_url(product_id, "NEW,UNKNOWN"))?;
        if snapshot.offer_count > 0 {
            Ok(snapshot)
        } else {
            self.read_price(&offers_url(product_id, "USED,REFURBISHED"))
        }
    }

    fn read_price(&self, url: &str) -> ApiResult<PriceSnapshot> {
        let doc = self.get_json(url)?;
        let mut merchant_names: HashMap<String, String> = HashMap::new();
        if let Some(merchants) = doc.get("merchants").and_then(Value::as_object) {
            for (id, m) in merchants {
                if let Some(name) = str_(m, "name") {
                    merchant_names.insert(id.clone(), name.to_string());
                }
            }
        }

        let mut best: Option<(f64, bool, &Value)> = None;
        let mut lowest_overall: Option<f64> = None;
        let mut count = 0;
        let mut currency = "SEK".to_string();
        if let Some(offers) = doc.get("offers").and_then(Value::as_array) {
            for offer in offers {
                let Some((price, cur)) = offer_price(offer) else { continue };
                count += 1;
                currency = cur;
                lowest_overall = Some(lowest_overall.map_or(price, |l| l.min(price)));
                let in_stock = str_(offer, "stockStatus") == Some("IN_STOCK");
                let better = match best {
                    None => true,
                    Some((bp, bs, _)) => (in_stock && !bs) || (in_stock == bs && price < bp),
                };
                if better {
                    best = Some((price, in_stock, offer));
                }
            }
        }

        let lowest_in_stock = match best {
            Some((p, true, _)) => Some(p),
            _ => None,
        };
        let (mut merchant, mut offer_url, mut condition, mut offer_name) = (None, None, None, None);
        if let Some((_, _, chosen)) = best {
            offer_url = str_(chosen, "url").map(str::to_string);
            condition = offer_condition(chosen);
            offer_name = str_(chosen, "name").map(str::to_string);
            if let Some(id) = str_(chosen, "merchantId") {
                merchant = merchant_names.get(id).cloned();
            }
        }
        Ok(PriceSnapshot {
            lowest_in_stock,
            lowest_overall,
            currency,
            offer_count: count,
            cheapest_merchant: merchant,
            cheapest_offer_url: offer_url,
            cheapest_condition: condition,
            cheapest_offer_name: offer_name,
        })
    }

    /// The product's lowest price per day over the past year, oldest first.
    pub fn price_history(&self, product_id: &str) -> ApiResult<Vec<MarketPoint>> {
        let url = format!(
            "/se/api/product-information-edge-rest/public/pricehistory/product/{}/{MARKET}/DAY?merchantId=&selectedInterval=ONE_YEAR&filter=NATIONAL",
            encode(product_id)
        );
        let doc = self.get_json(&url)?;
        let mut days: BTreeMap<NaiveDate, MarketPoint> = BTreeMap::new();
        if let Some(history) = doc.get("history").and_then(Value::as_array) {
            for point in history {
                let Some(ts) = str_(point, "timestamp") else { continue };
                if ts.len() < 10 {
                    continue;
                }
                let Ok(day) = NaiveDate::parse_from_str(&ts[..10], "%Y-%m-%d") else { continue };
                let Some(price) = dec(point, "price") else { continue };
                if price <= 0.0 {
                    continue;
                }
                if days.get(&day).map_or(true, |e| price < e.price) {
                    days.insert(day, MarketPoint { day, price, merchant: str_(point, "merchantName").map(str::to_string) });
                }
            }
        }
        Ok(days.into_values().collect())
    }

    /// The product's variant group (e.g. its colours), or None when PriceRunner lists it on its own.
    pub fn product_group(&self, product_id: &str, product_url: &str) -> ApiResult<Option<ProductGroup>> {
        // Product URLs look like /pl/94-3410100348/...; 94 is the category the endpoint expects before the id.
        let category = product_url
            .find("/pl/")
            .map(|i| product_url[i + 4..].chars().take_while(|c| c.is_ascii_digit()).collect::<String>())
            .filter(|c| !c.is_empty())
            .unwrap_or_else(|| "1".into());
        let url = format!("/se/api/product-detail-edge-rest/public/product-detail/v0/initial/{MARKET}/{category}/{}?languageCode=sv", encode(product_id));
        let doc = self.get_json(&url)?;
        let group = obj(&doc, "productGroup");
        let Some(members) = group.get("members").and_then(Value::as_array) else { return Ok(None) };
        if members.len() < 2 {
            return Ok(None);
        }
        let mut variants = HashMap::new();
        let mut bases = Vec::new();
        for member in members {
            let (Some(id), Some(name)) = (str_(member, "id"), str_(member, "name")) else { continue };
            let label = member
                .get("attributeValues")
                .and_then(Value::as_array)
                .map(|v| v.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(" / "))
                .unwrap_or_default();
            variants.insert(id.to_string(), if label.is_empty() { name.to_string() } else { label.clone() });
            bases.push(if label.is_empty() { name.to_string() } else { crate::analysis::trim_separators(&remove_ci(name, &label)) });
        }
        if variants.len() < 2 {
            return Ok(None);
        }
        let distinct = bases.iter().map(|b| b.to_lowercase()).collect::<std::collections::HashSet<_>>().len();
        let base_name = if distinct == 1 { bases[0].clone() } else { crate::analysis::common_prefix(&bases) };
        let mut keys: Vec<&String> = variants.keys().collect();
        keys.sort_by(|a, b| a.len().cmp(&b.len()).then_with(|| a.cmp(b)));
        let key = format!("pg:{}", keys[0]);
        Ok(Some(ProductGroup { key, name: if base_name.is_empty() { bases[0].clone() } else { base_name }, variants }))
    }

    /// Fetches a thumbnail's bytes.
    pub fn get_bytes(&self, url: &str) -> ApiResult<Vec<u8>> {
        let mut response = self.agent.get(url).call()?;
        Ok(response.body_mut().read_to_vec()?)
    }

    fn get_json(&self, path: &str) -> ApiResult<Value> {
        let mut response = self
            .agent
            .get(format!("{SITE}{path}"))
            .header("Accept", "application/json")
            .header("Accept-Language", "sv-SE,sv;q=0.9,en;q=0.8")
            .header("x-client-service", "owp")
            .call()?;
        if let Some(mime) = response.body().mime_type() {
            if !mime.to_lowercase().contains("json") {
                return Err(ApiError::new("PriceRunner returned a web page instead of data (possibly rate limited)"));
            }
        }
        let text = response.body_mut().read_to_string()?;
        Ok(serde_json::from_str(&text)?)
    }
}

fn remove_ci(text: &str, part: &str) -> String {
    let lower = text.to_lowercase();
    match lower.find(&part.to_lowercase()) {
        Some(i) => format!("{}{}", &text[..i], &text[i + part.len()..]),
        None => text.to_string(),
    }
}

fn encode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

fn offers_url(product_id: &str, conditions: &str) -> String {
    format!(
        "/se/api/product-detail-edge-rest/public/product-detail/v0/offers/{MARKET}/{}?af_ORIGIN=NATIONAL&af_ITEM_CONDITION={conditions}&af_STOCK_STATUS=IN_STOCK,BACKORDER,PREORDER,SPECIAL_ORDER,COLLECT_IN_STORE,UNKNOWN",
        encode(product_id)
    )
}

fn deal_filters(filter: &DealFilter) -> String {
    // PriceRunner writes a range of drops as negative percentages, largest drop first: 30-50 % is -50_-30.
    let mut s = format!("af_PRICE_DROP=-{}_-{}", filter.max_drop_percent, filter.min_drop_percent);
    let join = |v: &Vec<i64>| v.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(",");
    if !filter.categories.is_empty() {
        s += &format!("&af_CATEGORY={}", join(&filter.categories));
    }
    if !filter.merchants.is_empty() {
        s += &format!("&af_MERCHANT_DEAL={}", join(&filter.merchants));
    }
    if !filter.brands.is_empty() {
        s += &format!("&af_BRAND={}", join(&filter.brands));
    }
    s
}

fn parse_hits(products: &[Value]) -> Vec<SearchHit> {
    let mut hits = Vec::with_capacity(products.len());
    for p in products {
        let (Some(id), Some(name), Some(url)) = (str_(p, "id"), str_(p, "name"), str_(p, "url")) else { continue };
        let lowest = money(p, "lowestPrice");
        let rating = obj(p, "rating");
        let drop = obj(p, "priceDrop");
        hits.push(SearchHit {
            id: id.into(),
            name: name.into(),
            description: str_(p, "description").map(Into::into),
            category: str_(obj(p, "category"), "name").map(Into::into),
            url: url.into(),
            image_path: str_(obj(p, "image"), "path").map(Into::into),
            lowest_price: lowest.as_ref().map(|m| m.0),
            currency: lowest.map(|m| m.1).unwrap_or_else(|| "SEK".into()),
            recent_drop_percent: dec(drop, "percent"),
            old_price: money(drop, "oldPrice").map(|m| m.0),
            rating: dec(rating, "average"),
            rating_count: int(rating, "count"),
            store_count: int(obj(p, "previewMerchants"), "count"),
            out_of_stock: p.get("outOfStock").and_then(Value::as_bool).unwrap_or(false),
            condition: str_(p, "condition").map(Into::into),
        });
    }
    hits
}

/// The popular choices in PriceRunner's order ("rankedCounts"), then all the others, largest first.
fn parse_facet(container: &Value) -> Vec<DealFacetOption> {
    let facet = obj(container, "facet");
    let mut options = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for (list, sort) in [("rankedCounts", false), ("counts", true)] {
        let Some(counts) = facet.get(list).and_then(Value::as_array) else { continue };
        let mut parsed: Vec<DealFacetOption> = counts
            .iter()
            .filter_map(|c| {
                let name = str_(c, "optionValue")?;
                let id = int(c, "optionId");
                (id > 0).then(|| DealFacetOption { id, name: name.into(), count: int(c, "count") })
            })
            .collect();
        if sort {
            parsed.sort_by(|a, b| b.count.cmp(&a.count));
        }
        options.extend(parsed.into_iter().filter(|o| seen.insert(o.id)));
    }
    options
}

/// What an offer costs now: a current campaign price for everyone, else the regular price.
fn offer_price(offer: &Value) -> Option<(f64, String)> {
    let campaign = obj(offer, "campaignPrice");
    if let Some(sale) = money(campaign, "price") {
        if !offer.get("campaignMembership").map_or(false, Value::is_object) && is_current(obj(campaign, "salePriceEffectiveDate")) {
            return Some(sale);
        }
    }
    money(offer, "price")
}

fn is_current(period: &Value) -> bool {
    let now = Utc::now();
    let date = |name: &str| str_(period, name).and_then(|s| DateTime::parse_from_rfc3339(s).ok()).map(|d| d.with_timezone(&Utc));
    !date("start").map_or(false, |s| s > now) && !date("end").map_or(false, |e| e < now)
}

fn offer_condition(offer: &Value) -> Option<String> {
    obj(offer, "labels")
        .get("attributeLabels")
        .and_then(Value::as_array)?
        .iter()
        .find(|a| str_(a, "name") == Some("itemCondition"))
        .and_then(|a| str_(a, "value"))
        .map(Into::into)
}

static NULL: Value = Value::Null;

fn obj<'a>(element: &'a Value, name: &str) -> &'a Value {
    match element.get(name) {
        Some(v) if v.is_object() => v,
        _ => &NULL,
    }
}

fn str_<'a>(element: &'a Value, name: &str) -> Option<&'a str> {
    element.get(name).and_then(Value::as_str)
}

fn dec(element: &Value, name: &str) -> Option<f64> {
    match element.get(name)? {
        Value::Number(n) => n.as_f64(),
        Value::String(s) => s.parse().ok(),
        _ => None,
    }
}

fn int(element: &Value, name: &str) -> i64 {
    dec(element, name).unwrap_or(0.0) as i64
}

fn money(element: &Value, name: &str) -> Option<(f64, String)> {
    let m = obj(element, name);
    Some((dec(m, "amount")?, str_(m, "currency").unwrap_or("SEK").into()))
}
