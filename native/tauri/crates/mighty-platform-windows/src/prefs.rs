//! The app's settings in the registry, `HKCU\Software\MightyCode` (C# `DelegationSwitch`, `SetupProbe`, `TextAppearance`):
//! REG_DWORD / REG_QWORD as numbers, REG_SZ / REG_EXPAND_SZ as text (unexpanded). Reads only for now: writing is W6's (settings).

use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;

use mighty_platform::{PlatformError, PrefValue, PrefsStore, Result};
use windows_sys::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_MORE_DATA};
use windows_sys::Win32::System::Registry::{
    HKEY_CURRENT_USER, REG_DWORD, REG_EXPAND_SZ, REG_QWORD, REG_SZ, RRF_NOEXPAND, RRF_RT_ANY, RegGetValueW,
};

/// The app's key under HKEY_CURRENT_USER.
pub const APP_KEY: &str = r"Software\MightyCode";

#[derive(Debug, Clone)]
pub struct RegistryPrefs {
    key: String,
}

fn wide(text: &str) -> Vec<u16> {
    std::ffi::OsStr::new(text).encode_wide().chain(std::iter::once(0)).collect()
}

impl RegistryPrefs {
    /// `HKCU\Software\MightyCode`.
    pub fn app() -> RegistryPrefs {
        RegistryPrefs::under(APP_KEY)
    }

    /// Another key under HKEY_CURRENT_USER (tests).
    pub fn under(key: &str) -> RegistryPrefs {
        RegistryPrefs { key: key.to_string() }
    }

    /// The value's type and bytes, or None when the key or the value is missing.
    fn raw(&self, name: &str) -> Result<Option<(u32, Vec<u8>)>> {
        let (key, value) = (wide(&self.key), wide(name));
        let mut buffer: Vec<u8> = vec![0; 256];
        loop {
            let mut kind = 0u32;
            let mut size = buffer.len() as u32;
            // SAFETY: the strings are NUL-terminated; `buffer` holds `size` writable bytes.
            let status = unsafe {
                RegGetValueW(
                    HKEY_CURRENT_USER,
                    key.as_ptr(),
                    value.as_ptr(),
                    RRF_RT_ANY | RRF_NOEXPAND,
                    &mut kind,
                    buffer.as_mut_ptr() as *mut c_void,
                    &mut size,
                )
            };
            match status {
                0 => {
                    buffer.truncate(size as usize);
                    return Ok(Some((kind, buffer)));
                }
                ERROR_FILE_NOT_FOUND => return Ok(None),
                ERROR_MORE_DATA => buffer.resize(size as usize + 2, 0),
                code => return Err(PlatformError::Refused(format!("registry: {}", std::io::Error::from_raw_os_error(code as i32)))),
            }
        }
    }
}

/// A registry value as the app reads it. REG_DWORD is a signed 32-bit number, as .NET's `RegistryKey.GetValue` gives it.
fn decode(kind: u32, bytes: &[u8]) -> PrefValue {
    match kind {
        REG_DWORD if bytes.len() == 4 => PrefValue::Int(i64::from(i32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))),
        REG_QWORD if bytes.len() == 8 => PrefValue::Int(i64::from_le_bytes(bytes.try_into().unwrap_or([0; 8]))),
        REG_SZ | REG_EXPAND_SZ => {
            let units: Vec<u16> = bytes.as_chunks::<2>().0.iter().map(|pair| u16::from_le_bytes(*pair)).collect();
            let end = units.iter().position(|unit| *unit == 0).unwrap_or(units.len());
            PrefValue::Text(String::from_utf16_lossy(&units[..end]))
        }
        _ => PrefValue::Other,
    }
}

impl PrefsStore for RegistryPrefs {
    fn get(&self, key: &str) -> Result<Option<PrefValue>> {
        Ok(self.raw(key)?.map(|(kind, bytes)| decode(kind, &bytes)))
    }

    fn set(&self, _key: &str, _value: PrefValue) -> Result<()> {
        Err(PlatformError::unsupported("PrefsStore", "set"))
    }

    fn remove(&self, _key: &str) -> Result<()> {
        Err(PlatformError::unsupported("PrefsStore", "remove"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows_sys::Win32::System::Registry::{RegDeleteTreeW, RegSetKeyValueW};

    /// Writes a value under a key of the test's own (never the app's).
    fn put(key: &str, name: &str, kind: u32, bytes: &[u8]) {
        let (key, name) = (wide(key), wide(name));
        // SAFETY: NUL-terminated strings and a byte slice of the given length.
        let status = unsafe {
            RegSetKeyValueW(HKEY_CURRENT_USER, key.as_ptr(), name.as_ptr(), kind, bytes.as_ptr() as *const c_void, bytes.len() as u32)
        };
        assert_eq!(status, 0);
    }

    #[test]
    fn values_read_as_the_csharp_app_reads_them() {
        let key = format!(r"Software\MightyCode-test-{}", std::process::id());
        let prefs = RegistryPrefs::under(&key);
        assert_eq!(prefs.get("delegation.enabled").unwrap(), None, "a missing key reads as no value");
        put(&key, "delegation.enabled", REG_DWORD, &0u32.to_le_bytes());
        put(&key, "big", REG_QWORD, &(-5i64).to_le_bytes());
        put(&key, "textFontFamily", REG_SZ, &"Pretendard\0".encode_utf16().flat_map(u16::to_le_bytes).collect::<Vec<u8>>());
        put(&key, "blob", 3, &[1, 2, 3]);
        assert_eq!(prefs.get("delegation.enabled").unwrap(), Some(PrefValue::Int(0)));
        assert!(!prefs.get("delegation.enabled").unwrap().unwrap().is_on());
        assert_eq!(prefs.get("big").unwrap(), Some(PrefValue::Int(-5)));
        assert_eq!(prefs.get("textFontFamily").unwrap(), Some(PrefValue::Text("Pretendard".into())));
        assert_eq!(prefs.get("blob").unwrap(), Some(PrefValue::Other));
        assert_eq!(prefs.get("missing").unwrap(), None);
        assert!(prefs.set("x", PrefValue::Int(1)).unwrap_err().is_unsupported());
        let key = wide(&key);
        // SAFETY: a NUL-terminated key name under HKCU that only this test made.
        unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, key.as_ptr()) };
        assert_eq!(decode(REG_DWORD, &u32::MAX.to_le_bytes()), PrefValue::Int(-1), "a DWORD is signed, as .NET reads it");
    }
}
