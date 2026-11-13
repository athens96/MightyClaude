//! .NET's `DateTimeOffset` as the state uses it: `Wire.Now()` (`DateTimeOffset.UtcNow.ToString("O")`), `AgentRunTiming.Parse`
//! (`DateTimeOffset.TryParse(value, InvariantCulture, RoundtripKind)`) and the `"O"` text of a parsed time.
//!
//! The parser takes the ISO 8601 forms the apps write and read (`2026-11-13`, `2026-11-13T10:00`, `…:00`, `….1234567`, with `Z`,
//! `±HH:MM`, `±HHMM` or `±HH`, or none: local time); .NET's parser also takes culture forms (`11/13/2026`, month names), which this
//! does not.

use std::sync::Mutex;

use chrono::{Datelike, Local, NaiveDate, NaiveDateTime, Offset, TimeZone, Timelike, Utc};

/// .NET ticks (100 ns) from 0001-01-01 to 1970-01-01.
const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;
const TICKS_PER_SECOND: i64 = 10_000_000;

/// A `DateTimeOffset`: the local clock reading in .NET ticks and its offset from UTC in minutes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DateTimeOffset {
    pub local_ticks: i64,
    pub offset_minutes: i32,
}

impl DateTimeOffset {
    pub fn from_utc_ticks(ticks: i64) -> Self {
        DateTimeOffset { local_ticks: ticks, offset_minutes: 0 }
    }

    /// The instant in UTC ticks, which comparisons use.
    pub fn utc_ticks(&self) -> i64 {
        self.local_ticks - self.offset_minutes as i64 * 60 * TICKS_PER_SECOND
    }

    pub fn now() -> Self {
        let now = clock_now();
        DateTimeOffset::from_utc_ticks(UNIX_EPOCH_TICKS + now.timestamp() * TICKS_PER_SECOND + now.timestamp_subsec_nanos() as i64 / 100)
    }

    /// `ToString("O")`: `yyyy-MM-ddTHH:mm:ss.fffffff±HH:mm`.
    pub fn to_o(&self) -> String {
        let ticks = self.local_ticks;
        let seconds = ticks.div_euclid(TICKS_PER_SECOND);
        let fraction = ticks.rem_euclid(TICKS_PER_SECOND);
        let days = seconds.div_euclid(86_400);
        let in_day = seconds.rem_euclid(86_400);
        let date = NaiveDate::from_ymd_opt(1, 1, 1).and_then(|d| d.checked_add_days(chrono::Days::new(days as u64)));
        let (y, m, d) = date.map(|d| (d.year(), d.month(), d.day())).unwrap_or((1, 1, 1));
        let sign = if self.offset_minutes < 0 { '-' } else { '+' };
        let offset = self.offset_minutes.unsigned_abs();
        format!(
            "{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}.{fraction:07}{sign}{:02}:{:02}",
            in_day / 3600,
            in_day / 60 % 60,
            in_day % 60,
            offset / 60,
            offset % 60
        )
    }

    /// The time as UTC (`UtcDateTime`).
    pub fn to_chrono(&self) -> chrono::DateTime<Utc> {
        let ticks = self.utc_ticks() - UNIX_EPOCH_TICKS;
        Utc.timestamp_opt(ticks.div_euclid(TICKS_PER_SECOND), (ticks.rem_euclid(TICKS_PER_SECOND) * 100) as u32)
            .single()
            .unwrap_or_default()
    }
}

impl PartialOrd for DateTimeOffset {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.utc_ticks().cmp(&other.utc_ticks()))
    }
}

static FIXED_NOW: Mutex<Option<chrono::DateTime<Utc>>> = Mutex::new(None);

/// The clock every "now" reads; a test may fix it.
pub fn clock_now() -> chrono::DateTime<Utc> {
    FIXED_NOW.lock().ok().and_then(|now| *now).unwrap_or_else(Utc::now)
}

/// Fixes (or with None frees) the clock, for tests and the parity tool.
pub fn set_clock(now: Option<chrono::DateTime<Utc>>) {
    if let Ok(mut fixed) = FIXED_NOW.lock() {
        *fixed = now;
    }
}

/// `Wire.Now()`.
pub fn now_o() -> String {
    DateTimeOffset::now().to_o()
}

fn digits(b: &[u8], at: &mut usize, count: usize) -> Option<u32> {
    let slice = b.get(*at..*at + count)?;
    if !slice.iter().all(u8::is_ascii_digit) {
        return None;
    }
    *at += count;
    Some(slice.iter().fold(0, |n, d| n * 10 + (d - b'0') as u32))
}

/// `AgentRunTiming.Parse`: 1–80 characters that read as a date and time (see the module comment for the forms).
pub fn parse(value: Option<&str>) -> Option<DateTimeOffset> {
    let value = value?;
    let length = value.encode_utf16().count();
    if length == 0 || length > 80 {
        return None;
    }
    parse_iso(value.trim())
}

fn parse_iso(text: &str) -> Option<DateTimeOffset> {
    let b = text.as_bytes();
    let mut at = 0;
    let year = digits(b, &mut at, 4)?;
    (b.get(at) == Some(&b'-')).then_some(())?;
    at += 1;
    let month = digits(b, &mut at, 2)?;
    (b.get(at) == Some(&b'-')).then_some(())?;
    at += 1;
    let day = digits(b, &mut at, 2)?;
    let date = NaiveDate::from_ymd_opt(year as i32, month, day)?;
    let (mut hour, mut minute, mut second, mut fraction) = (0, 0, 0, 0i64);
    if matches!(b.get(at), Some(b'T' | b't' | b' ')) {
        at += 1;
        hour = digits(b, &mut at, 2)?;
        (b.get(at) == Some(&b':')).then_some(())?;
        at += 1;
        minute = digits(b, &mut at, 2)?;
        if b.get(at) == Some(&b':') {
            at += 1;
            second = digits(b, &mut at, 2)?;
            if b.get(at) == Some(&b'.') {
                at += 1;
                let start = at;
                while b.get(at).is_some_and(u8::is_ascii_digit) {
                    at += 1;
                }
                let all = &text[start..at];
                if all.is_empty() {
                    return None;
                }
                // Seven digits are ticks; .NET rounds what lies beyond them.
                let mut seven: String = all.chars().take(7).collect();
                while seven.len() < 7 {
                    seven.push('0');
                }
                fraction = seven.parse().ok()?;
                if all.len() > 7 && all.as_bytes()[7] >= b'5' {
                    fraction += 1;
                }
            }
        }
    }
    if hour > 23 || minute > 59 || second > 59 {
        return None;
    }
    let offset = match b.get(at) {
        None => None,
        Some(b'Z' | b'z') => {
            at += 1;
            Some(0)
        }
        Some(sign @ (b'+' | b'-')) => {
            let negative = *sign == b'-';
            at += 1;
            let hours = digits(b, &mut at, 2)? as i32;
            let minutes = if b.get(at) == Some(&b':') {
                at += 1;
                digits(b, &mut at, 2)? as i32
            } else {
                digits(b, &mut at, 2).unwrap_or(0) as i32
            };
            if hours > 14 || minutes > 59 {
                return None;
            }
            let total = hours * 60 + minutes;
            Some(if negative { -total } else { total })
        }
        Some(_) => return None,
    };
    if at != b.len() {
        return None;
    }
    let days = date.signed_duration_since(NaiveDate::from_ymd_opt(1, 1, 1)?).num_days();
    let local_ticks =
        days * 86_400 * TICKS_PER_SECOND + (hour as i64 * 3600 + minute as i64 * 60 + second as i64) * TICKS_PER_SECOND + fraction;
    let offset_minutes = match offset {
        Some(minutes) => minutes,
        None => {
            // No offset: the local time zone's at that time (DateTimeOffset.TryParse's assumption).
            let naive = NaiveDateTime::new(date, chrono::NaiveTime::from_hms_opt(hour, minute, second)?);
            Local.from_local_datetime(&naive).earliest().map(|t| t.offset().fix().local_minus_utc() / 60).unwrap_or(0)
        }
    };
    Some(DateTimeOffset { local_ticks, offset_minutes })
}

/// A `chrono` time as .NET ticks since 0001-01-01.
pub fn ticks_of(time: chrono::DateTime<Utc>) -> i64 {
    UNIX_EPOCH_TICKS + time.timestamp() * TICKS_PER_SECOND + time.nanosecond() as i64 / 100
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn o_format_has_seven_digits_and_the_offset() {
        let t = parse(Some("2026-11-13T10:20:30.1234567Z")).unwrap();
        assert_eq!(t.to_o(), "2026-11-13T10:20:30.1234567+00:00");
        let t = parse(Some("2026-11-13T10:20:30+09:00")).unwrap();
        assert_eq!(t.to_o(), "2026-11-13T10:20:30.0000000+09:00");
        assert_eq!(DateTimeOffset::from_utc_ticks(0).to_o(), "0001-01-01T00:00:00.0000000+00:00");
    }

    #[test]
    fn comparisons_use_the_instant() {
        let a = parse(Some("2026-11-13T10:00:00+09:00")).unwrap();
        let b = parse(Some("2026-11-13T01:00:00Z")).unwrap();
        assert_eq!(a.utc_ticks(), b.utc_ticks());
        assert!(parse(Some("not a date")).is_none());
        assert!(parse(Some("")).is_none());
        assert!(parse(Some("2026-02-30T00:00:00Z")).is_none());
    }
}
