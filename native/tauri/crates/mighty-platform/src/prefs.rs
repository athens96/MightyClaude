//! The app's settings outside the state file: Windows values under `HKCU\Software\MightyCode`, the Mac's UserDefaults domain
//! `dev.mightycode.native`. Both use the same names (`delegation.enabled`, `setup.neverShow`, `textScale`, `textFontFamily`, ...).

use crate::Result;

/// One stored value.
#[derive(Debug, Clone, PartialEq)]
pub enum PrefValue {
    /// A REG_DWORD / REG_QWORD, or a UserDefaults integer.
    Int(i64),
    /// A REG_SZ / REG_EXPAND_SZ (unexpanded), or a UserDefaults string.
    Text(String),
    /// A UserDefaults boolean (Windows stores switches as DWORD 0/1).
    Bool(bool),
    /// A value of a kind the app never writes (binary, multi-string, a dictionary): present, but not readable as a setting.
    Other,
}

impl PrefValue {
    /// C# `DelegationSwitch.IsOn` for a present value: zero, or the text `0` or `false` (any case, trimmed), is off; anything else
    /// is on. (A missing value's meaning is the caller's: the delegation switch reads it as on.)
    pub fn is_on(&self) -> bool {
        match self {
            PrefValue::Int(number) => *number != 0,
            PrefValue::Bool(on) => *on,
            PrefValue::Text(text) => {
                let word = text.trim();
                word != "0" && !word.eq_ignore_ascii_case("false")
            }
            PrefValue::Other => true,
        }
    }
}

/// Contract: `get` of a missing key or a missing store is `Ok(None)`; `Err` only when the OS refused the read. Writes take effect
/// for the next `get` at once.
pub trait PrefsStore: Send + Sync {
    fn get(&self, key: &str) -> Result<Option<PrefValue>>;
    fn set(&self, key: &str, value: PrefValue) -> Result<()>;
    fn remove(&self, key: &str) -> Result<()>;
}

#[cfg(test)]
mod tests {
    use super::PrefValue;

    #[test]
    fn switches_read_as_csharp_reads_them() {
        assert!(PrefValue::Int(1).is_on());
        assert!(!PrefValue::Int(0).is_on());
        assert!(!PrefValue::Text(" FALSE ".into()).is_on());
        assert!(!PrefValue::Text("0".into()).is_on());
        assert!(PrefValue::Text("yes".into()).is_on());
        assert!(!PrefValue::Bool(false).is_on());
        assert!(PrefValue::Other.is_on());
    }
}
