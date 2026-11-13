//! The workspace's tree as the files pane shows it (C# `FilePaneTree`, the logic of macOS FilePaneModel without the view; C#
//! `FilePaneDrawing`; WorkspaceFiles.cs:886-1025): loaded listings, opened folders, captions, and the name filter that searches only
//! the root and the opened folders (never the disk), capped at 2,000 results. One tree per workspace lives while the app runs, so a
//! closed and reopened pane shows what it showed before.

use std::collections::{HashMap, HashSet};
use std::path::Path;

use mighty_core::locale::Locale;

use crate::listing::{self, FileEntry, ListError, Listing};
use crate::utf16;

/// C# `FilePaneTree.MaximumFilterResults`.
pub const MAXIMUM_FILTER_RESULTS: usize = 2_000;

/// C# `FilePaneTree.Row`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Row {
    pub entry: FileEntry,
    pub depth: usize,
    pub is_expanded: bool,
    pub caption: Option<String>,
}

/// C# `FilePaneTree`.
#[derive(Debug, Clone, Default)]
pub struct FilePaneTree {
    pub children: HashMap<String, Vec<FileEntry>>,
    pub truncated: HashSet<String>,
    pub folder_errors: HashMap<String, String>,
    pub expanded: HashSet<String>,
    pub selected_path: Option<String>,
    /// The file last previewed, shown again when a closed pane reopens.
    pub preview_path: Option<String>,
    pub filter: String,
}

fn ordinal_sorted(paths: &HashSet<String>) -> Vec<&String> {
    let mut list: Vec<&String> = paths.iter().collect();
    list.sort_by(|a, b| utf16::ordinal(a, b));
    list
}

impl FilePaneTree {
    fn needle(&self) -> &str {
        self.filter.trim()
    }

    /// C# `IsFiltering`.
    pub fn is_filtering(&self) -> bool {
        !self.needle().is_empty()
    }

    /// C# `Apply`: one folder's listing, or the reason it could not be read.
    pub fn apply(&mut self, path: &str, listing: Result<Listing, ListError>, locale: &Locale) {
        match listing {
            Ok(listing) => {
                self.children.insert(path.into(), listing.entries);
                self.folder_errors.remove(path);
                if listing.truncated {
                    self.truncated.insert(path.into());
                } else {
                    self.truncated.remove(path);
                }
            }
            Err(error) => {
                self.children.insert(path.into(), vec![]);
                let text =
                    if error == ListError::OutsideRoot { locale.get("files.preview.missing") } else { locale.get("files.tree.unreadable") };
                self.folder_errors.insert(path.into(), text);
            }
        }
    }

    /// C# `Load`: one folder through the listing, applied.
    pub fn load(&mut self, path: &str, root: &Path, locale: &Locale) {
        self.apply(path, listing::list(path, root, listing::MAXIMUM_ENUMERATED_NAMES), locale);
    }

    /// C# `RefreshTargets`: the root and the opened folders are read again; listings of closed folders are dropped.
    pub fn refresh_targets(&mut self) -> Vec<String> {
        let stale: Vec<String> = self.children.keys().filter(|path| !path.is_empty() && !self.expanded.contains(*path)).cloned().collect();
        for path in stale {
            self.children.remove(&path);
            self.truncated.remove(&path);
            self.folder_errors.remove(&path);
        }
        std::iter::once(String::new()).chain(ordinal_sorted(&self.expanded).into_iter().cloned()).collect()
    }

    /// C# `Rows`: the rows the tree shows, and whether the filter stopped at its cap.
    pub fn rows(&self, locale: &Locale) -> (Vec<Row>, bool) {
        let needle = self.needle();
        if !needle.is_empty() {
            // C#'s CurrentCultureIgnoreCase: both sides folded the same way.
            let needle = needle.to_lowercase();
            let mut found = Vec::new();
            let mut paths = vec![String::new()];
            paths.extend(ordinal_sorted(&self.expanded).into_iter().cloned());
            for path in &paths {
                for entry in self.children.get(path).map(Vec::as_slice).unwrap_or(&[]) {
                    if !entry.name.to_lowercase().contains(&needle) {
                        continue;
                    }
                    if found.len() == MAXIMUM_FILTER_RESULTS {
                        return (found, true);
                    }
                    found.push(Row { entry: entry.clone(), depth: 0, is_expanded: false, caption: None });
                }
            }
            return (found, false);
        }
        let mut rows = Vec::new();
        self.visit("", 0, &mut rows, locale);
        (rows, false)
    }

    fn visit(&self, path: &str, depth: usize, rows: &mut Vec<Row>, locale: &Locale) {
        for entry in self.children.get(path).map(Vec::as_slice).unwrap_or(&[]) {
            let open = entry.is_directory && self.expanded.contains(&entry.relative_path);
            rows.push(Row {
                entry: entry.clone(),
                depth,
                is_expanded: open,
                caption: if open { self.caption(&entry.relative_path, locale) } else { None },
            });
            if open && depth < 64 {
                self.visit(&entry.relative_path, depth + 1, rows, locale);
            }
        }
    }

    /// C# `Caption`: an opened folder's note (its error, empty, truncated), or none.
    pub fn caption(&self, path: &str, locale: &Locale) -> Option<String> {
        if let Some(error) = self.folder_errors.get(path) {
            return Some(error.clone());
        }
        if self.children.get(path).is_some_and(Vec::is_empty) {
            return Some(locale.get("files.tree.empty"));
        }
        if self.truncated.contains(path) {
            return Some(locale.get_with("files.tree.truncated", &[("count", &listing::MAXIMUM_ENTRIES_PER_FOLDER.to_string())]));
        }
        None
    }

    /// C# `Reveal`: opening a folder found by the filter clears the filter and opens the folder and every folder above it, so it shows
    /// in the tree (macOS Return/→ on a filter result).
    pub fn reveal(&mut self, path: &str) {
        self.filter.clear();
        let parts: Vec<&str> = path.split('/').collect();
        for index in 1..=parts.len() {
            self.expanded.insert(parts[..index].join("/"));
        }
        self.selected_path = Some(path.into());
    }
}

/// C# `FilePaneDrawing.MaximumTreeRows`: how many rows the pane draws at once.
pub const MAXIMUM_TREE_ROWS: usize = 2_000;
/// C# `FilePaneDrawing.MaximumSourceUnits`: 256 KB of UTF-16 units drawn (the reader itself stops at 1 MB).
pub const MAXIMUM_SOURCE_UNITS: usize = 262_144;

/// C# `FilePaneDrawing.TreeRows`: the first [`MAXIMUM_TREE_ROWS`] rows, and whether any were left out.
pub fn tree_rows<T: Clone>(rows: &[T]) -> (Vec<T>, bool) {
    if rows.len() <= MAXIMUM_TREE_ROWS { (rows.to_vec(), false) } else { (rows[..MAXIMUM_TREE_ROWS].to_vec(), true) }
}

/// C# `FilePaneDrawing.SourceText`: all of a short text; otherwise up to the last line break before the cap (so no line is cut), or
/// the cap itself when one line is longer, never between the halves of a surrogate pair.
pub fn source_text(text: &str) -> (String, bool) {
    let units = utf16::units(text);
    if units.len() <= MAXIMUM_SOURCE_UNITS {
        return (text.to_string(), false);
    }
    if let Some(cut) = units[..MAXIMUM_SOURCE_UNITS].iter().rposition(|u| *u == b'\n' as u16).filter(|cut| *cut > 0) {
        let kept = utf16::string(&units[..cut]);
        return (kept.trim_end_matches('\r').to_string(), true);
    }
    let mut cut = MAXIMUM_SOURCE_UNITS;
    if utf16::is_high_surrogate(units[cut - 1]) {
        cut -= 1;
    }
    (utf16::string(&units[..cut]), true)
}
