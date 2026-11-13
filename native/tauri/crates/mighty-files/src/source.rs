//! A source preview's lines and colours, and its find bar (C# `SourceLines`, `SourceHighlighter`, WorkspaceFiles.cs:733-884; C#
//! `SourceFind`, SourceFind.cs). Every offset is in UTF-16 units, as C# and the page count them.

use std::collections::HashSet;

use crate::classify::SourceLanguage;
use crate::utf16;

/// C# `SourceLines.WrapThreshold`: longer lines make the source view wrap instead of scrolling sideways.
pub const WRAP_THRESHOLD: usize = 5_000;

/// C# `SourceLines.Scan`: where lines start (`\n`, `\r`, `\r\n` as one break, U+2028 or U+2029, as macOS counts them) and the longest
/// line's length.
pub fn scan(text: &str) -> (Vec<usize>, usize) {
    let mut starts = vec![0];
    let (mut longest, mut offset, mut line_start, mut previous) = (0usize, 0usize, 0usize, 0u16);
    for unit in text.encode_utf16() {
        offset += 1;
        if unit == b'\n' as u16 && previous == b'\r' as u16 {
            *starts.last_mut().expect("a start") = offset;
            line_start = offset;
        } else if unit == b'\n' as u16 || unit == b'\r' as u16 || unit == 0x2028 || unit == 0x2029 {
            longest = longest.max(offset - 1 - line_start);
            starts.push(offset);
            line_start = offset;
        }
        previous = unit;
    }
    (starts, longest.max(offset - line_start))
}

/// C# `SourceTokenKind`, numbered as the page's `[kind, start, length]` triples read them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TokenKind {
    Keyword = 0,
    String = 1,
    Comment = 2,
    Number = 3,
}

impl TokenKind {
    pub fn name(self) -> &'static str {
        match self {
            TokenKind::Keyword => "keyword",
            TokenKind::String => "string",
            TokenKind::Comment => "comment",
            TokenKind::Number => "number",
        }
    }
}

/// C# `SourceToken`: a coloured span in UTF-16 offsets.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Token {
    pub kind: TokenKind,
    pub location: usize,
    pub length: usize,
}

/// C# `SourceHighlighter.MaximumUnits`: only this many UTF-16 units are scanned.
pub const MAXIMUM_UNITS: usize = 400_000;

struct Rules {
    line_comments: &'static [&'static str],
    block_comment: Option<(&'static str, &'static str)>,
    quotes: &'static [u16],
    multiline_quotes: &'static [u16],
    triple_quotes: bool,
    keywords: HashSet<&'static str>,
    case_insensitive: bool,
}

const SLASH: &[&str] = &["//"];
const HASH: &[&str] = &["#"];
const C_BLOCK: Option<(&str, &str)> = Some(("/*", "*/"));
const DQ: u16 = b'"' as u16;
const SQ: u16 = b'\'' as u16;
const BT: u16 = b'`' as u16;

fn words(text: &'static str) -> HashSet<&'static str> {
    text.split(' ').filter(|w| !w.is_empty()).collect()
}

fn rule(
    line_comments: &'static [&'static str],
    block_comment: Option<(&'static str, &'static str)>,
    quotes: &'static [u16],
    multiline_quotes: &'static [u16],
    triple_quotes: bool,
    keywords: HashSet<&'static str>,
) -> Rules {
    Rules { line_comments, block_comment, quotes, multiline_quotes, triple_quotes, keywords, case_insensitive: false }
}

fn rules(language: SourceLanguage) -> Rules {
    use SourceLanguage::*;
    match language {
        Swift => rule(
            SLASH,
            C_BLOCK,
            &[DQ],
            &[],
            true,
            words(
                "actor as associatedtype async await break case catch class continue default defer deinit do else enum extension fallthrough false fileprivate final for func guard if import in init inout internal is lazy let mutating nil nonisolated open operator override private protocol public repeat rethrows return self Self some static struct subscript super switch throw throws true try typealias var weak where while any",
            ),
        ),
        C => rule(
            SLASH,
            C_BLOCK,
            &[DQ, SQ],
            &[],
            false,
            words(
                "auto bool break case char class const constexpr continue default delete do double else enum explicit extern false float for friend goto if inline int long namespace new nullptr operator private protected public register return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while NULL nil YES NO self super id",
            ),
        ),
        JavaScript => rule(
            SLASH,
            C_BLOCK,
            &[DQ, SQ, BT],
            &[BT],
            false,
            words(
                "abstract as async await break case catch class const continue debugger declare default delete do else enum export extends false finally for from function get if implements import in instanceof interface keyof let new null of private protected public readonly return set static super switch this throw true try type typeof undefined var void while yield",
            ),
        ),
        Python => rule(
            HASH,
            None,
            &[DQ, SQ],
            &[],
            true,
            words(
                "False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return self try while with yield match case",
            ),
        ),
        Kotlin | Gradle => rule(
            SLASH,
            C_BLOCK,
            &[DQ, SQ],
            &[],
            true,
            words(
                "abstract annotation as break by catch class companion const constructor continue data do else enum false final finally for fun if import in init inline interface internal is lateinit null object open operator override package private protected public return sealed super suspend this throw true try typealias val var when while def apply plugins dependencies implementation",
            ),
        ),
        Java => rule(
            SLASH,
            C_BLOCK,
            &[DQ, SQ],
            &[],
            true,
            words(
                "abstract boolean break byte case catch char class const continue default do double else enum extends false final finally float for if implements import instanceof int interface long native new null package private protected public record return short static super switch synchronized this throw throws transient true try var void volatile while",
            ),
        ),
        Go => rule(
            SLASH,
            C_BLOCK,
            &[DQ, SQ, BT],
            &[BT],
            false,
            words(
                "break case chan const continue default defer else fallthrough false for func go goto if import interface iota map nil package range return select struct switch true type var",
            ),
        ),
        // '\'' is also a lifetime marker, so only double-quoted strings.
        Rust => rule(
            SLASH,
            C_BLOCK,
            &[DQ],
            &[DQ],
            false,
            words(
                "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while",
            ),
        ),
        Shell | Dockerfile | Makefile => {
            let mut keywords = words(
                "if then else elif fi for while until do done case esac in function return export local readonly source echo exit set unset",
            );
            if language == Dockerfile {
                keywords.extend(words(
                    "from run cmd label expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell as",
                ));
            }
            if language == Makefile {
                keywords.extend(words("ifeq ifneq ifdef ifndef endif include define endef override"));
            }
            Rules { case_insensitive: language == Dockerfile, ..rule(HASH, None, &[DQ, SQ], &[DQ, SQ], false, keywords) }
        }
        Json => rule(&[], None, &[DQ], &[], false, words("true false null")),
        Yaml => rule(HASH, None, &[DQ, SQ], &[], false, words("true false null yes no on off")),
        Toml => rule(HASH, None, &[DQ, SQ], &[], true, words("true false")),
        Xml | Html => rule(&[], Some(("<!--", "-->")), &[DQ], &[], false, HashSet::new()),
        Css => rule(&[], C_BLOCK, &[DQ, SQ], &[], false, words("important inherit initial unset none auto")),
        Sql => Rules {
            case_insensitive: true,
            ..rule(
                &["--"],
                C_BLOCK,
                &[SQ, DQ],
                &[],
                false,
                words(
                    "add all alter and as asc begin between by case check column commit constraint create database default delete desc distinct drop else end exists foreign from group having if in index inner insert into is join key left like limit not null on or order outer primary references right rollback select set table then union unique update values view when where with",
                ),
            )
        },
        Plain => rule(&[], None, &[], &[], false, HashSet::new()),
    }
}

fn is_identifier_start(unit: u16) -> bool {
    matches!(unit, 0x41..=0x5A | 0x61..=0x7A | 0x5F)
}

fn is_digit(unit: u16) -> bool {
    (0x30..=0x39).contains(&unit)
}

fn is_identifier(unit: u16) -> bool {
    is_identifier_start(unit) || is_digit(unit)
}

fn is_space(unit: u16) -> bool {
    matches!(unit, 0x20 | 0x09 | 0x0A | 0x0D)
}

/// C# `SourceHighlighter.Tokens`: the single-pass highlighter of macOS SourceHighlighter.swift (comments, strings, numbers,
/// keywords); it never fails. Only the first `maximum_units` UTF-16 units are scanned.
pub fn tokens(text: &str, language: SourceLanguage, maximum_units: usize) -> Vec<Token> {
    let mut result = Vec::new();
    if language == SourceLanguage::Plain {
        return result;
    }
    let rules = rules(language);
    let all = utf16::units(text);
    let units = &all[..all.len().min(maximum_units)];
    let count = units.len();
    let matches = |pattern: &[u16], at: usize| at + pattern.len() <= count && units[at..at + pattern.len()] == *pattern;
    let line_end = |at: usize| utf16::index_of_unit(units, b'\n' as u16, at).unwrap_or(count);
    let block = rules.block_comment.map(|(open, close)| (utf16::units(open), utf16::units(close)));
    let line_comments: Vec<Vec<u16>> = rules.line_comments.iter().map(|m| utf16::units(m)).collect();
    let triple = utf16::units("\"\"\"");
    let mut add = |kind: TokenKind, start: usize, end: usize| {
        if end > start {
            result.push(Token { kind, location: start, length: end - start });
        }
    };
    let mut index = 0;
    while index < count {
        let unit = units[index];
        if let Some((open, close)) = &block
            && matches(open, index)
        {
            let end = utf16::index_of(units, close, index + open.len()).map_or(count, |at| at + close.len());
            add(TokenKind::Comment, index, end);
            index = end;
            continue;
        }
        let mut commented = false;
        for marker in &line_comments {
            if !matches(marker, index) {
                continue;
            }
            // "#" starts a comment only at a word boundary ("$#", "a#b" do not).
            if *marker == [b'#' as u16] && index > 0 && !is_space(units[index - 1]) {
                break;
            }
            let end = line_end(index);
            add(TokenKind::Comment, index, end);
            index = end;
            commented = true;
            break;
        }
        if commented {
            continue;
        }
        if rules.triple_quotes && unit == DQ && matches(&triple, index) {
            let end = utf16::index_of(units, &triple, index + 3).map_or(count, |at| at + 3);
            add(TokenKind::String, index, end);
            index = end;
            continue;
        }
        if rules.quotes.contains(&unit) {
            let mut cursor = index + 1;
            while cursor < count {
                let current = units[cursor];
                if current == b'\\' as u16 {
                    cursor += 2;
                    continue;
                }
                if current == unit {
                    cursor += 1;
                    break;
                }
                if current == b'\n' as u16 && !rules.multiline_quotes.contains(&unit) {
                    break;
                }
                cursor += 1;
            }
            let end = cursor.min(count);
            add(TokenKind::String, index, end);
            index = end;
            continue;
        }
        if is_digit(unit) && (index == 0 || !is_identifier(units[index - 1])) {
            let mut cursor = index + 1;
            while cursor < count
                && (is_identifier(units[cursor]) || units[cursor] == b'.' as u16 && cursor + 1 < count && is_digit(units[cursor + 1]))
            {
                cursor += 1;
            }
            add(TokenKind::Number, index, cursor);
            index = cursor;
            continue;
        }
        if is_identifier_start(unit) {
            let mut cursor = index + 1;
            while cursor < count && is_identifier(units[cursor]) {
                cursor += 1;
            }
            // Identifiers are ASCII, so the units are the word's bytes.
            let word: String = units[index..cursor].iter().map(|u| *u as u8 as char).collect();
            let word = if rules.case_insensitive { word.to_ascii_lowercase() } else { word };
            if rules.keywords.contains(word.as_str()) {
                add(TokenKind::Keyword, index, cursor);
            }
            index = cursor;
            continue;
        }
        index += 1;
    }
    result
}

/// C# `SourceFind.MaximumMatches`: only the first ones are marked.
pub const MAXIMUM_MATCHES: usize = 1_000;

/// C# `SourceFind.Matches`: where `query` occurs in `text` (UTF-16 offsets), ignoring case as the find bar's default "contains" does,
/// in order, without overlaps, at most [`MAXIMUM_MATCHES`].
pub fn find_matches(text: &str, query: &str) -> Vec<usize> {
    let mut found = Vec::new();
    let (text, query) = (utf16::units(text), utf16::units(query));
    if query.is_empty() || query.len() > text.len() {
        return found;
    }
    let upper = |units: &[u16]| units.iter().map(|u| utf16::upper_unit(*u)).collect::<Vec<u16>>();
    let (text, query) = (upper(&text), upper(&query));
    let mut at = utf16::index_of(&text, &query, 0);
    while let Some(hit) = at {
        if found.len() == MAXIMUM_MATCHES {
            break;
        }
        found.push(hit);
        at = if hit + query.len() >= text.len() { None } else { utf16::index_of(&text, &query, hit + query.len()) };
    }
    found
}

/// C# `SourceFind.Step`: the match to go to from `current` (an index into `matches`, or None for none yet, when the first at or
/// after `from` is taken), forward or back, wrapping round; None with no match.
pub fn find_step(matches: &[usize], current: Option<usize>, forward: bool, from: usize) -> Option<usize> {
    let count = matches.len();
    if count == 0 {
        return None;
    }
    match current.filter(|c| *c < count) {
        None => {
            let next = matches.iter().take_while(|m| **m < from).count();
            Some(if forward { next % count } else { (next + count - 1) % count })
        }
        Some(current) => Some(if forward { (current + 1) % count } else { (current + count - 1) % count }),
    }
}
