//! The line diff of the source preview's 변경사항 view (C# `FileDiff`, FileDiff.cs; M/MightyCore/FileDiff.swift;
//! native/contracts/fixtures/file-diff.json): CRLF compared as LF; lines compared by their exact characters (no Unicode
//! normalization) and their line break; Myers' shortest edit script after the common start and end are set aside, each run of changes
//! written removals first; 3 lines of context, hunks whose changes stand at most 6 equal lines apart merged. Each side is at most 1 MiB
//! (UTF-8) and 20,000 lines and holds no NUL, else there is no diff; an edit script longer than 2,000 reads as the whole middle
//! replaced. At most 5,000 hunk lines are written, then `truncated`.

use std::collections::HashMap;

use serde_json::{Map, Value, json};

pub const CONTEXT: usize = 3;
pub const MAXIMUM_SIDE_BYTES: usize = 1_048_576;
pub const MAXIMUM_SIDE_LINES: usize = 20_000;
pub const MAXIMUM_OUTPUT_LINES: usize = 5_000;
/// Past this many edits Myers' trace (its square in memory) is not kept: the middle is shown as removed then added.
pub const MAXIMUM_EDIT_DISTANCE: usize = 2_000;

/// C# `FileDiffLine`: `" "` context, `"+"` added, `"-"` removed; `no_newline` marks the file's last line when it has no line break.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiffLine {
    pub op: char,
    pub text: String,
    pub no_newline: bool,
}

/// C# `FileDiffHunk`: a side with no lines starts at the line before it (0 at the top of the file), as git writes it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Hunk {
    pub old_start: usize,
    pub old_lines: usize,
    pub new_start: usize,
    pub new_lines: usize,
    pub lines: Vec<DiffLine>,
}

impl Hunk {
    /// The hunk as `Wire.Json` writes it (`noNewline` only when true).
    pub fn to_json(&self) -> Value {
        let lines: Vec<Value> = self
            .lines
            .iter()
            .map(|line| {
                let mut out = Map::new();
                out.insert("op".into(), json!(line.op.to_string()));
                out.insert("text".into(), json!(line.text));
                if line.no_newline {
                    out.insert("noNewline".into(), json!(true));
                }
                Value::Object(out)
            })
            .collect();
        json!({"oldStart": self.old_start, "oldLines": self.old_lines, "newStart": self.new_start, "newLines": self.new_lines, "lines": lines})
    }

    /// A hunk as the fixture writes it.
    pub fn from_json(value: &Value) -> Option<Hunk> {
        let number = |key: &str| value.get(key).and_then(Value::as_u64).map(|n| n as usize);
        let lines = value.get("lines")?.as_array()?;
        Some(Hunk {
            old_start: number("oldStart")?,
            old_lines: number("oldLines")?,
            new_start: number("newStart")?,
            new_lines: number("newLines")?,
            lines: lines
                .iter()
                .map(|line| {
                    Some(DiffLine {
                        op: line.get("op")?.as_str()?.chars().next()?,
                        text: line.get("text")?.as_str()?.to_string(),
                        no_newline: line.get("noNewline").and_then(Value::as_bool).unwrap_or(false),
                    })
                })
                .collect::<Option<Vec<_>>>()?,
        })
    }
}

/// C# `FileDiffResult`: `added` and `removed` count the whole change even when `truncated` cut the hunks; `unchanged` (and no hunks)
/// when both sides are the same text once CRLF reads as LF.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiffResult {
    pub hunks: Vec<Hunk>,
    pub added: usize,
    pub removed: usize,
    pub truncated: bool,
    pub unchanged: bool,
}

/// A line as compared: its text and whether a line break ends it.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct Line {
    text: String,
    terminated: bool,
}

fn lines(text: &str) -> Vec<Line> {
    let normalized = text.replace("\r\n", "\n");
    let mut out = Vec::new();
    let mut rest = normalized.as_str();
    while !rest.is_empty() {
        match rest.find('\n') {
            Some(end) => {
                out.push(Line { text: rest[..end].to_string(), terminated: true });
                rest = &rest[end + 1..];
            }
            None => {
                out.push(Line { text: rest.to_string(), terminated: false });
                break;
            }
        }
    }
    out
}

fn fits(text: &str) -> bool {
    !text.contains('\0') && text.len() <= MAXIMUM_SIDE_BYTES
}

/// C# `FileDiff.Compute`: the diff of `old` to `new`, or None when either side is over the caps or holds a NUL.
pub fn compute(old: &str, new: &str) -> Option<DiffResult> {
    if !fits(old) || !fits(new) {
        return None;
    }
    let (a, b) = (lines(old), lines(new));
    if a.len() > MAXIMUM_SIDE_LINES || b.len() > MAXIMUM_SIDE_LINES {
        return None;
    }
    // Every distinct line (text and break) gets a number; Myers compares numbers.
    let mut ids: HashMap<&Line, usize> = HashMap::new();
    let mut id = |line| {
        let next = ids.len();
        *ids.entry(line).or_insert(next)
    };
    let x: Vec<usize> = a.iter().map(&mut id).collect();
    let y: Vec<usize> = b.iter().map(&mut id).collect();
    if x == y {
        return Some(DiffResult { hunks: vec![], added: 0, removed: 0, truncated: false, unchanged: true });
    }
    let ops = script(&x, &y);
    // Removals before additions in each run of changes.
    let mut ordered: Vec<(char, usize, usize)> = Vec::with_capacity(ops.len());
    let mut i = 0;
    while i < ops.len() {
        if ops[i].0 == ' ' {
            ordered.push(ops[i]);
            i += 1;
            continue;
        }
        let mut end = i;
        while end < ops.len() && ops[end].0 != ' ' {
            end += 1;
        }
        ordered.extend(ops[i..end].iter().filter(|o| o.0 == '-'));
        ordered.extend(ops[i..end].iter().filter(|o| o.0 == '+'));
        i = end;
    }
    let added = ordered.iter().filter(|o| o.0 == '+').count();
    let removed = ordered.iter().filter(|o| o.0 == '-').count();
    // Change runs grouped into hunks: a gap of at most 2 × context equal lines joins two.
    let mut groups: Vec<(usize, usize)> = Vec::new();
    let mut i = 0;
    while i < ordered.len() {
        if ordered[i].0 == ' ' {
            i += 1;
            continue;
        }
        let mut end = i;
        while end + 1 < ordered.len() && ordered[end + 1].0 != ' ' {
            end += 1;
        }
        match groups.last_mut() {
            Some(last) if i - last.1 - 1 <= 2 * CONTEXT => last.1 = end,
            _ => groups.push((i, end)),
        }
        i = end + 1;
    }
    let mut hunks = Vec::new();
    let (mut written, mut truncated) = (0usize, false);
    // Lines of each side before the next hunk, carried from one hunk to the next.
    let (mut scanned, mut old_before, mut new_before) = (0usize, 0usize, 0usize);
    for (first, last) in groups {
        let start = first.saturating_sub(CONTEXT);
        let stop = (ordered.len() - 1).min(last + CONTEXT);
        let mut count = stop - start + 1;
        if written + count > MAXIMUM_OUTPUT_LINES {
            count = MAXIMUM_OUTPUT_LINES.saturating_sub(written);
            truncated = true;
        }
        if count == 0 {
            break;
        }
        while scanned < start {
            if ordered[scanned].0 != '+' {
                old_before += 1;
            }
            if ordered[scanned].0 != '-' {
                new_before += 1;
            }
            scanned += 1;
        }
        let mut hunk_lines = Vec::with_capacity(count);
        let (mut old_lines, mut new_lines) = (0, 0);
        for &(op, old_index, new_index) in &ordered[start..start + count] {
            let line = if op == '+' { &b[new_index] } else { &a[old_index] };
            if op != '+' {
                old_lines += 1;
            }
            if op != '-' {
                new_lines += 1;
            }
            hunk_lines.push(DiffLine { op, text: line.text.clone(), no_newline: !line.terminated });
        }
        hunks.push(Hunk {
            old_start: if old_lines == 0 { old_before } else { old_before + 1 },
            old_lines,
            new_start: if new_lines == 0 { new_before } else { new_before + 1 },
            new_lines,
            lines: hunk_lines,
        });
        written += count;
        if truncated {
            break;
        }
    }
    Some(DiffResult { hunks, added, removed, truncated, unchanged: false })
}

/// C# `FileDiff.Cut`: the first `lines` hunk lines, a hunk cut short counting only the lines it keeps (a reply too big for its frame).
pub fn cut(hunks: &[Hunk], mut lines: usize) -> Vec<Hunk> {
    let mut kept = Vec::new();
    for hunk in hunks {
        if lines == 0 {
            break;
        }
        if hunk.lines.len() <= lines {
            kept.push(hunk.clone());
            lines -= hunk.lines.len();
            continue;
        }
        let part: Vec<DiffLine> = hunk.lines[..lines].to_vec();
        let old_lines = part.iter().filter(|l| l.op != '+').count();
        let new_lines = part.iter().filter(|l| l.op != '-').count();
        // A start of a side with no lines is the line before it, as for a whole hunk.
        let old_start = if hunk.old_lines == 0 { hunk.old_start } else { hunk.old_start - 1 };
        let new_start = if hunk.new_lines == 0 { hunk.new_start } else { hunk.new_start - 1 };
        kept.push(Hunk {
            old_start: if old_lines == 0 { old_start } else { old_start + 1 },
            old_lines,
            new_start: if new_lines == 0 { new_start } else { new_start + 1 },
            new_lines,
            lines: part,
        });
        break;
    }
    kept
}

/// The edit script in order: `' '` (both indexes), `'-'` (old index), `'+'` (new index).
fn script(a: &[usize], b: &[usize]) -> Vec<(char, usize, usize)> {
    let mut prefix = 0;
    while prefix < a.len() && prefix < b.len() && a[prefix] == b[prefix] {
        prefix += 1;
    }
    let mut suffix = 0;
    while suffix < a.len() - prefix && suffix < b.len() - prefix && a[a.len() - 1 - suffix] == b[b.len() - 1 - suffix] {
        suffix += 1;
    }
    let mut ops = Vec::with_capacity(a.len() + b.len());
    ops.extend((0..prefix).map(|i| (' ', i, i)));
    ops.extend(middle(a, prefix, a.len() - suffix, b, prefix, b.len() - suffix));
    ops.extend((1..=suffix).rev().map(|i| (' ', a.len() - i, b.len() - i)));
    ops
}

/// The furthest x reachable on diagonal k at step d from step d-1's row (`previous`, indexed k + d - 1, -1 where unreached): down
/// from k+1 (an addition) or right from k-1 (a removal), the one that reaches further, down on a tie; never off the grid. None when
/// neither move is possible.
fn step(previous: &[i64], d: i64, k: i64, n: i64, m: i64) -> Option<(i64, bool)> {
    let mut down = None;
    let mut right = None;
    if k < d - 1 {
        let from_above = previous[(k + 1 + d - 1) as usize];
        if from_above >= 0 && from_above - k <= m {
            down = Some(from_above);
        }
    }
    if k > -(d - 1) {
        let from_left = previous[(k - 1 + d - 1) as usize];
        if from_left >= 0 && from_left < n {
            right = Some(from_left + 1);
        }
    }
    match (down, right) {
        (None, None) => None,
        (down, Some(r)) if down.is_none_or(|dd| r > dd) => Some((r, false)),
        (Some(dd), _) => Some((dd, true)),
        (None, Some(r)) => Some((r, false)),
    }
}

fn middle(a: &[usize], a_start: usize, a_end: usize, b: &[usize], b_start: usize, b_end: usize) -> Vec<(char, usize, usize)> {
    let (n, m) = (a_end - a_start, b_end - b_start);
    let replaced = || -> Vec<(char, usize, usize)> {
        (0..n).map(|i| ('-', a_start + i, usize::MAX)).chain((0..m).map(|j| ('+', usize::MAX, b_start + j))).collect()
    };
    if n == 0 || m == 0 {
        return replaced();
    }
    let (ni, mi) = (n as i64, m as i64);
    let mut trace: Vec<Vec<i64>> = Vec::new();
    let mut end: Option<(i64, i64)> = None;
    let mut d = 0i64;
    while d <= (ni + mi).min(MAXIMUM_EDIT_DISTANCE as i64) && end.is_none() {
        let mut row = vec![-1i64; (2 * d + 1) as usize];
        let mut k = -d;
        while k <= d {
            let x0 = if d == 0 { Some(0) } else { step(&trace[(d - 1) as usize], d, k, ni, mi).map(|(x, _)| x) };
            if let Some(mut x) = x0 {
                let mut y = x - k;
                if (0..=mi).contains(&y) {
                    while x < ni && y < mi && a[a_start + x as usize] == b[b_start + y as usize] {
                        x += 1;
                        y += 1;
                    }
                    row[(k + d) as usize] = x;
                    if x == ni && y == mi {
                        end = Some((d, k));
                        break;
                    }
                }
            }
            k += 2;
        }
        trace.push(row);
        d += 1;
    }
    let Some((end_d, end_k)) = end else { return replaced() };
    // Walk back from (n, m): each step is a snake of equal lines after one edit.
    let mut reversed = Vec::with_capacity(n + m);
    let (mut cx, mut cy, mut ck) = (ni, mi, end_k);
    let mut d = end_d;
    while d > 0 {
        let (_, down) = step(&trace[(d - 1) as usize], d, ck, ni, mi).expect("a step the forward pass took");
        let pk = if down { ck + 1 } else { ck - 1 };
        let px = trace[(d - 1) as usize][(pk + d - 1) as usize];
        let py = px - pk;
        let (sx, sy) = if down { (px, py + 1) } else { (px + 1, py) };
        while cx > sx && cy > sy {
            cx -= 1;
            cy -= 1;
            reversed.push((' ', a_start + cx as usize, b_start + cy as usize));
        }
        reversed.push(if down { ('+', usize::MAX, b_start + py as usize) } else { ('-', a_start + px as usize, usize::MAX) });
        cx = px;
        cy = py;
        ck = pk;
        d -= 1;
    }
    while cx > 0 && cy > 0 {
        cx -= 1;
        cy -= 1;
        reversed.push((' ', a_start + cx as usize, b_start + cy as usize));
    }
    reversed.reverse();
    reversed
}
