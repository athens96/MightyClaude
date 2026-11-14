//! C# `SystemSqlite.ReadOnly` (SystemSqlite.cs:63): a query on a CLI's database, read-only — through `mode=ro` (read through the
//! writer's WAL while the CLI runs) or as an `immutable` file, whichever writes nothing beside it first ([`read_only`]) — with a 250 ms
//! busy timeout, every row's first columns as text.

use std::path::Path;

use rusqlite::types::ValueRef;
use rusqlite::{Connection, OpenFlags};

use crate::files;

/// A `file:` URI of an absolute path, as `new Uri(Path.GetFullPath(path)).AbsoluteUri` writes it, with `query` after it.
fn uri(path: &Path, query: &str) -> Option<String> {
    let full = mighty_core::workspace_files::full_path(path)?;
    let text = full.to_string_lossy().replace('\\', "/");
    let mut encoded = String::new();
    for byte in text.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' | b'/' | b':' => encoded.push(byte as char),
            other => encoded.push_str(&format!("%{other:02X}")),
        }
    }
    let prefix = if encoded.starts_with('/') { "file://" } else { "file:///" };
    Some(format!("{prefix}{encoded}?{query}"))
}

fn as_text(value: ValueRef) -> Option<String> {
    match value {
        ValueRef::Null => None,
        ValueRef::Integer(n) => Some(n.to_string()),
        // `sqlite3_column_text` of a REAL: SQLite's own "%!.15g".
        ValueRef::Real(f) => Some(real_text(f)),
        ValueRef::Text(t) | ValueRef::Blob(t) => Some(String::from_utf8_lossy(t).into_owned()),
    }
}

/// SQLite's text of a REAL (`%!.15g`): 15 significant digits, a ".0" kept on a whole number.
fn real_text(value: f64) -> String {
    if !value.is_finite() {
        return if value.is_nan() {
            String::new()
        } else if value > 0.0 {
            "Inf".into()
        } else {
            "-Inf".into()
        };
    }
    let mut text = format!("{value:.14e}");
    if let Some((mantissa, exponent)) = text.split_once('e') {
        let exponent: i32 = exponent.parse().unwrap_or(0);
        if (-4..15).contains(&exponent) {
            let decimals = (14 - exponent).max(0) as usize;
            text = format!("{value:.decimals$}");
            if text.contains('.') {
                text = text.trim_end_matches('0').to_string();
                if text.ends_with('.') {
                    text.push('0');
                }
            } else {
                text.push_str(".0");
            }
        } else {
            let mantissa = mantissa.trim_end_matches('0').trim_end_matches('.');
            text = format!("{mantissa}e{}{:02}", if exponent < 0 { '-' } else { '+' }, exponent.abs());
        }
    }
    text
}

fn query(uri: &str, sql: &str, columns: usize, parameters: &[String]) -> Option<Vec<Vec<Option<String>>>> {
    let flags = OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_URI | OpenFlags::SQLITE_OPEN_NO_MUTEX;
    let connection = Connection::open_with_flags(uri, flags).ok()?;
    connection.busy_timeout(std::time::Duration::from_millis(250)).ok()?;
    let mut statement = connection.prepare(sql).ok()?;
    let mut rows = statement.query(rusqlite::params_from_iter(parameters.iter())).ok()?;
    let mut out = vec![];
    loop {
        match rows.next() {
            Ok(Some(row)) => {
                let mut values = Vec::with_capacity(columns);
                for column in 0..columns {
                    values.push(row.get_ref(column).ok().and_then(as_text));
                }
                out.push(values);
            }
            Ok(None) => return Some(out),
            Err(_) => return None,
        }
    }
}

/// `SystemSqlite.ReadOnly`: `sql` on the database file at `path`; None when the file is no regular file or neither way read the query to
/// its end. `parameters` are bound in order to the statement's `?` placeholders, as text.
///
/// Nothing is written beside the CLI's database: `mode=ro` on a WAL database whose `-wal` and `-shm` are not there (the CLI is not
/// running) makes them and leaves them behind, so such a file is read as `immutable` first — the file alone is all of it then — and
/// `mode=ro` (its shared memory opened read-only, `readonly_shm`) comes first only while the CLI's `-wal` or `-shm` is there, to read
/// through what it has not checkpointed yet.
pub fn read_only(path: &Path, sql: &str, columns: usize, parameters: &[String]) -> Option<Vec<Vec<Option<String>>>> {
    if !files::regular(path) {
        return None;
    }
    let beside = |suffix: &str| {
        let mut name = path.as_os_str().to_os_string();
        name.push(suffix);
        std::fs::symlink_metadata(std::path::PathBuf::from(name)).is_ok()
    };
    let modes =
        if beside("-wal") || beside("-shm") { ["mode=ro&readonly_shm=1", "immutable=1"] } else { ["immutable=1", "mode=ro&readonly_shm=1"] };
    for mode in modes {
        if let Some(rows) = uri(path, mode).and_then(|uri| query(&uri, sql, columns, parameters)) {
            return Some(rows);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A CLI's WAL database read while the CLI is not running gains no `-wal` or `-shm` file beside it (W4 review C12): the shared
    /// memory is opened read-only (`readonly_shm`), and without it the file is read as immutable.
    #[test]
    fn a_wal_database_is_read_without_writing_beside_it() {
        let folder = std::env::temp_dir().join(format!("mighty-sqlite-{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        let path = folder.join("state.db");
        {
            let connection = Connection::open(&path).unwrap();
            connection.pragma_update(None, "journal_mode", "WAL").unwrap();
            connection.execute_batch("CREATE TABLE t(v TEXT); INSERT INTO t VALUES ('one'), ('two');").unwrap();
        }
        let side = |suffix: &str| folder.join(format!("state.db{suffix}"));
        assert!(!side("-wal").exists() && !side("-shm").exists(), "the writer's close took its files away");
        let rows = read_only(&path, "SELECT v FROM t ORDER BY v", 1, &[]).expect("read");
        assert_eq!(rows, vec![vec![Some("one".to_string())], vec![Some("two".to_string())]]);
        assert!(!side("-wal").exists() && !side("-shm").exists(), "nothing written beside the CLI's database");
        // While the CLI has it open (its -wal and -shm there), the reader reads through them, the rows not yet checkpointed too.
        let writer = Connection::open(&path).unwrap();
        writer.pragma_update(None, "wal_autocheckpoint", 0).unwrap();
        writer.execute("INSERT INTO t VALUES ('three')", []).unwrap();
        let rows = read_only(&path, "SELECT count(*) FROM t", 1, &[]).expect("read while written");
        assert_eq!(rows, vec![vec![Some("3".to_string())]]);
        drop(writer);
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn reals_read_as_sqlite_writes_them() {
        assert_eq!(real_text(1.0), "1.0");
        assert_eq!(real_text(1762767001.5), "1762767001.5");
        assert_eq!(real_text(0.25), "0.25");
    }
}
