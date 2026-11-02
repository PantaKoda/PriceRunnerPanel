//! Swedish price formatting ("4 272 kr") and short relative times, as in Format.cs.

use chrono::{DateTime, Utc};

const NBSP: char = '\u{00A0}';

fn group(int_part: u64) -> String {
    let digits = int_part.to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (i, ch) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(NBSP);
        }
        out.push(ch);
    }
    out
}

/// "4 272 kr", "1 234,50 kr", or "—" for no price.
pub fn price(amount: Option<f64>, currency: &str) -> String {
    let Some(value) = amount else { return "—".into() };
    let negative = value < 0.0;
    let abs = value.abs();
    let number = if (abs - abs.trunc()).abs() < 1e-9 {
        group(abs.round() as u64)
    } else {
        let cents = (abs * 100.0).round() as u64;
        format!("{},{:02}", group(cents / 100), cents % 100)
    };
    let number = if negative { format!("−{number}") } else { number };
    if currency == "SEK" { format!("{number} kr") } else { format!("{number} {currency}") }
}

/// PriceRunner's words for a condition that isn't new; None for new or unknown.
pub fn condition(condition: Option<&str>) -> Option<&'static str> {
    match condition {
        Some("REFURBISHED") => Some("RENOVERAD"),
        Some("USED") => Some("BEGAGNAD"),
        _ => None,
    }
}

/// Stores also sell used items with no label saying so, but their titles give it away.
pub fn looks_used(offer_title: Option<&str>) -> bool {
    let Some(title) = offer_title else { return false };
    let lower = title.to_lowercase();
    lower
        .split(|c: char| !c.is_alphanumeric())
        .any(|w| matches!(w, "begagnad" | "beg" | "renoverad" | "refurbished" | "skick" | "nyskick"))
}

pub fn count(value: i64) -> String {
    group(value.max(0) as u64)
}

/// "12,5 %" (one decimal at most).
pub fn percent(value: f64) -> String {
    let rounded = (value * 10.0).round() / 10.0;
    if (rounded - rounded.trunc()).abs() < 1e-9 {
        format!("{} %", rounded.trunc() as i64)
    } else {
        format!("{} %", format!("{rounded:.1}").replace('.', ","))
    }
}

pub fn ago(when: Option<DateTime<Utc>>, now: DateTime<Utc>) -> String {
    let Some(at) = when else { return "never".into() };
    let secs = (now - at).num_seconds().max(0);
    match secs {
        s if s < 60 => "just now".into(),
        s if s < 3600 => format!("{} min ago", s / 60),
        s if s < 86400 => format!("{} h ago", s / 3600),
        s => format!("{} d ago", s / 86400),
    }
}

pub fn interval(minutes: i64) -> String {
    if minutes < 60 {
        format!("{minutes} min")
    } else if minutes % 60 == 0 {
        format!("{} h", minutes / 60)
    } else {
        format!("{} h", (minutes as f64 / 60.0 * 10.0).round() / 10.0).replace('.', ",")
    }
}

/// "2,4H" / "35M" / "NOW" for the header's next-check countdown.
pub fn countdown(secs: i64) -> String {
    if secs <= 0 {
        "NOW".into()
    } else if secs < 3600 {
        format!("{}M", (secs + 59) / 60)
    } else {
        let hours = (secs as f64 / 3600.0 * 10.0).round() / 10.0;
        if (hours - hours.trunc()).abs() < 1e-9 { format!("{}H", hours as i64) } else { format!("{hours:.1}H").replace('.', ",") }
    }
}

/// "27 Aug" (invariant month names, as the .NET app writes them).
pub fn day(date: chrono::NaiveDate) -> String {
    date.format("%-d %b").to_string()
}
