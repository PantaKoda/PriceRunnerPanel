//! Judging a price against PriceRunner's daily history (DealAnalysis.cs) and grouping pinned variants
//! (ProductGrouping.cs).

use crate::format;
use crate::model::{MarketPoint, PinnedProduct};
use chrono::{Days, NaiveDate};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Verdict {
    Unknown,
    Deal,
    Good,
    Typical,
    Inflated,
    High,
}

/// How today's price compares with the product's past year on PriceRunner.
#[derive(Clone, Debug)]
pub struct Insight {
    pub verdict: Verdict,
    pub typical: Option<f64>,
    pub vs_typical_percent: Option<f64>,
    pub year_low: Option<MarketPoint>,
    pub year_high: Option<MarketPoint>,
    pub recent_peak: Option<MarketPoint>,
}

impl Insight {
    pub const NONE: Insight = Insight { verdict: Verdict::Unknown, typical: None, vs_typical_percent: None, year_low: None, year_high: None, recent_peak: None };

    pub fn has_verdict(&self) -> bool {
        self.verdict != Verdict::Unknown
    }

    pub fn label(&self) -> &'static str {
        match self.verdict {
            Verdict::Deal => "✓ REAL DEAL",
            Verdict::Good => "GOOD PRICE",
            Verdict::Typical => "TYPICAL PRICE",
            Verdict::Inflated => "⚠ INFLATED DROP",
            Verdict::High => "ABOVE TYPICAL",
            Verdict::Unknown => "",
        }
    }

    /// Chip kind for the UI: 1 down (good), 3 warn (inflated), 2 up (high), 0 plain.
    pub fn chip_kind(&self) -> i32 {
        match self.verdict {
            Verdict::Deal | Verdict::Good => 1,
            Verdict::Inflated => 3,
            Verdict::High => 2,
            _ => 0,
        }
    }

    /// "Typical 4 490 kr  ·  1y low 3 069 kr (CDON, 27 Aug)".
    pub fn summary(&self, currency: &str) -> String {
        let mut parts = Vec::new();
        if let Some(t) = self.typical {
            parts.push(format!("Typical {}", format::price(Some(t), currency)));
        }
        if let Some(low) = &self.year_low {
            let where_ = low.merchant.as_deref().filter(|m| !m.is_empty()).map(|m| format!("{m}, ")).unwrap_or_default();
            parts.push(format!("1y low {} ({}{})", format::price(Some(low.price), currency), where_, format::day(low.day)));
        }
        parts.join("  ·  ")
    }
}

pub const TYPICAL_FROM_DAYS: u64 = 77;
pub const TYPICAL_TO_DAYS: u64 = 21;
pub const RECENT_DAYS: u64 = 30;
const MIN_TYPICAL_DAYS: usize = 14;

pub fn analyze(current: Option<f64>, history: &[MarketPoint], today: NaiveDate) -> Insight {
    let past: Vec<&MarketPoint> = history.iter().filter(|p| p.day < today).collect();
    if past.is_empty() {
        return Insight::NONE;
    }
    let min_by = |v: &[&MarketPoint]| v.iter().copied().min_by(|a, b| a.price.partial_cmp(&b.price).unwrap()).cloned();
    let max_by = |v: &[&MarketPoint]| v.iter().copied().max_by(|a, b| a.price.partial_cmp(&b.price).unwrap()).cloned();
    let year_low = min_by(&past);
    let year_high = max_by(&past);
    let recent_from = today - Days::new(RECENT_DAYS);
    let recent: Vec<&MarketPoint> = past.iter().copied().filter(|p| p.day >= recent_from).collect();
    let recent_peak = if recent.is_empty() { None } else { max_by(&recent) };

    let from = today - Days::new(TYPICAL_FROM_DAYS);
    let to = today - Days::new(TYPICAL_TO_DAYS);
    let mut window: Vec<f64> = past.iter().filter(|p| p.day >= from && p.day < to).map(|p| p.price).collect();
    if window.len() < MIN_TYPICAL_DAYS {
        window = past.iter().filter(|p| p.day < to).map(|p| p.price).collect();
    }
    let (Some(price), true) = (current, window.len() >= MIN_TYPICAL_DAYS) else {
        return Insight { verdict: Verdict::Unknown, typical: None, vs_typical_percent: None, year_low, year_high, recent_peak };
    };
    let typical = median(&mut window);
    let vs = (price - typical) / typical * 100.0;
    let verdict = judge(price, typical, vs, year_low.as_ref().map(|l| l.price).unwrap_or(price), recent_peak.as_ref().map(|p| p.price));
    Insight { verdict, typical: Some(typical), vs_typical_percent: Some(vs), year_low, year_high, recent_peak }
}

fn judge(price: f64, typical: f64, vs: f64, year_low: f64, recent_peak: Option<f64>) -> Verdict {
    if vs <= -12.0 || (vs <= -3.0 && price <= year_low * 1.02) {
        return Verdict::Deal;
    }
    if let Some(peak) = recent_peak {
        if peak >= typical * 1.05 && price < peak && vs > -3.0 {
            return Verdict::Inflated;
        }
    }
    if vs <= -5.0 {
        Verdict::Good
    } else if vs >= 5.0 {
        Verdict::High
    } else {
        Verdict::Typical
    }
}

fn median(values: &mut [f64]) -> f64 {
    values.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let m = values.len() / 2;
    if values.len() % 2 == 1 { values[m] } else { (values[m - 1] + values[m]) / 2.0 }
}

/// One box on the watchlist: a product and the variants of it that are pinned (pin index, variant label).
pub struct Cluster {
    pub title: String,
    pub members: Vec<(usize, Option<String>)>,
}

const TRIM_CHARS: &[char] = &[' ', '-', '–', '—', ',', '/', '(', ')', '\u{00A0}'];

pub fn trim_separators(text: &str) -> String {
    text.trim_matches(TRIM_CHARS).to_string()
}

/// The words all names start with.
pub fn common_prefix(names: &[String]) -> String {
    let Some(first) = names.first() else { return String::new() };
    let words: Vec<&str> = first.split_whitespace().collect();
    let mut count = words.len();
    for name in &names[1..] {
        let other: Vec<&str> = name.split_whitespace().collect();
        let mut shared = 0;
        while shared < count.min(other.len()) && words[shared].eq_ignore_ascii_case(other[shared]) {
            shared += 1;
        }
        count = shared;
    }
    trim_separators(&words[..count].join(" "))
}

fn starts_with_words(name: &str, prefix: &str) -> bool {
    !prefix.is_empty()
        && name.len() >= prefix.len()
        && name.is_char_boundary(prefix.len())
        && name[..prefix.len()].eq_ignore_ascii_case(prefix)
        && (name.len() == prefix.len() || !name[prefix.len()..].chars().next().map_or(false, |c| c.is_alphanumeric()))
}

/// Groups pinned products that are the same product in different variants; everything else stands alone.
pub fn cluster(pins: &[PinnedProduct]) -> Vec<Cluster> {
    struct Work {
        key: String,
        title: String,
        category: Option<String>,
        members: Vec<(usize, Option<String>)>,
        first: usize,
    }
    let mut clusters: Vec<Work> = Vec::new();
    for (i, pin) in pins.iter().enumerate() {
        let Some(key) = &pin.group_key else { continue };
        let index = match clusters.iter().position(|c| &c.key == key) {
            Some(i) => i,
            None => {
                clusters.push(Work { key: key.clone(), title: pin.group_name.clone().unwrap_or_else(|| pin.name.clone()), category: pin.category.clone(), members: vec![], first: i });
                clusters.len() - 1
            }
        };
        clusters[index].members.push((i, pin.variant.clone()));
    }
    for (i, pin) in pins.iter().enumerate() {
        if pin.group_key.is_some() {
            continue;
        }
        match clusters.iter().position(|c| c.category == pin.category && starts_with_words(&pin.name, &c.title)) {
            Some(index) => {
                let rest = trim_separators(&pin.name[clusters[index].title.len()..]);
                clusters[index].members.push((i, if rest.is_empty() { None } else { Some(rest) }));
                clusters[index].first = clusters[index].first.min(i);
            }
            None => clusters.push(Work { key: format!("id:{}", pin.id), title: pin.name.clone(), category: pin.category.clone(), members: vec![(i, None)], first: i }),
        }
    }
    clusters.sort_by_key(|c| c.first);
    clusters
        .into_iter()
        .map(|mut c| {
            c.members.sort_by_key(|m| m.0);
            Cluster { title: c.title, members: c.members }
        })
        .collect()
}
