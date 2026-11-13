//! The WebView2 Evergreen runtime's per-user install (C# `WebView2BootstrapperInstaller` and `WinTrustVerify`,
//! MainWindow.Browser.cs:118-213): Microsoft's bootstrapper downloaded over HTTPS, its Authenticode signature checked (valid, signed
//! by Microsoft Corporation), then run as the user with `/silent /install`. Nothing here runs until the user asks (`browserInstall`).

use std::os::windows::ffi::OsStrExt;
use std::path::Path;

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows_sys::Win32::Security::Cryptography::{CERT_NAME_SIMPLE_DISPLAY_TYPE, CertGetNameStringW};
use windows_sys::Win32::Security::WinTrust::{
    WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_FILE_INFO, WTD_CACHE_ONLY_URL_RETRIEVAL, WTD_CHOICE_FILE, WTD_REVOKE_NONE,
    WTD_STATEACTION_CLOSE, WTD_STATEACTION_VERIFY, WTD_UI_NONE, WTHelperGetProvSignerFromChain, WTHelperProvDataFromStateData,
    WinVerifyTrust,
};
use windows_sys::Win32::System::Com::Urlmon::URLDownloadToFileW;
use windows_sys::Win32::System::Threading::{GetExitCodeProcess, INFINITE, WaitForSingleObject};
use windows_sys::Win32::UI::Shell::{SEE_MASK_NOASYNC, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW};
use windows_sys::Win32::UI::WindowsAndMessaging::SW_HIDE;

/// Microsoft's per-user bootstrapper.
const BOOTSTRAPPER_URL: &str = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

fn wide(text: &std::ffi::OsStr) -> Vec<u16> {
    text.encode_wide().chain(std::iter::once(0)).collect()
}

/// `DownloadBootstrapperAsync`: the bootstrapper written to `path`.
pub fn download_bootstrapper(path: &Path) -> Result<(), String> {
    let (url, file) = (wide(BOOTSTRAPPER_URL.as_ref()), wide(path.as_os_str()));
    // SAFETY: two NUL-terminated strings that live across the call; no caller object and no progress callback.
    let result = unsafe { URLDownloadToFileW(std::ptr::null_mut(), url.as_ptr(), file.as_ptr(), 0, std::ptr::null_mut()) };
    if result == 0 { Ok(()) } else { Err(std::io::Error::from_raw_os_error(result).to_string()) }
}

/// `WinTrustVerify.IsMicrosoftSigned`: a valid Authenticode signature whose signer is Microsoft Corporation; false for anything else.
pub fn is_microsoft_signed(path: &Path) -> bool {
    let file_path = wide(path.as_os_str());
    let mut file = WINTRUST_FILE_INFO {
        cbStruct: std::mem::size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: file_path.as_ptr(),
        ..WINTRUST_FILE_INFO::default()
    };
    let mut data = WINTRUST_DATA {
        cbStruct: std::mem::size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_NONE,
        dwUnionChoice: WTD_CHOICE_FILE,
        dwStateAction: WTD_STATEACTION_VERIFY,
        dwProvFlags: WTD_CACHE_ONLY_URL_RETRIEVAL,
        ..WINTRUST_DATA::default()
    };
    data.Anonymous.pFile = &mut file;
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    // SAFETY: the structures above live across the calls; the state the verify leaves is read, then closed with the same data.
    unsafe {
        let trusted = WinVerifyTrust(std::ptr::null_mut(), &mut action, (&mut data as *mut WINTRUST_DATA).cast()) == 0;
        let signer = if trusted { signer_name(data.hWVTStateData) } else { None };
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(std::ptr::null_mut(), &mut action, (&mut data as *mut WINTRUST_DATA).cast());
        trusted && signer.is_some_and(|name| name.to_lowercase().contains("microsoft corporation"))
    }
}

/// The signing certificate's name (its simple display name, the subject's common name) from a verify's state.
///
/// # Safety
/// `state` is the `hWVTStateData` a successful `WTD_STATEACTION_VERIFY` left, not yet closed.
unsafe fn signer_name(state: HANDLE) -> Option<String> {
    unsafe {
        let provider = WTHelperProvDataFromStateData(state);
        if provider.is_null() {
            return None;
        }
        let signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if signer.is_null() || (*signer).csCertChain == 0 || (*signer).pasCertChain.is_null() {
            return None;
        }
        let certificate = (*(*signer).pasCertChain).pCert;
        if certificate.is_null() {
            return None;
        }
        let mut name = [0u16; 512];
        let written =
            CertGetNameStringW(certificate, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, std::ptr::null(), name.as_mut_ptr(), name.len() as u32);
        (written > 1).then(|| String::from_utf16_lossy(&name[..written as usize - 1]))
    }
}

/// `RunInstallerAsync`: the bootstrapper run as the user (`/silent /install`, no elevation asked), its exit code once it ended.
pub fn run_installer(path: &Path) -> Result<i32, String> {
    let (file, parameters) = (wide(path.as_os_str()), wide("/silent /install".as_ref()));
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
        lpFile: file.as_ptr(),
        lpParameters: parameters.as_ptr(),
        nShow: SW_HIDE,
        ..unsafe { std::mem::zeroed() }
    };
    // SAFETY: the strings live across the call; the process handle it returns is waited on and closed here.
    unsafe {
        if ShellExecuteExW(&mut info) == 0 || info.hProcess.is_null() {
            return Err(std::io::Error::last_os_error().to_string());
        }
        let ended = WaitForSingleObject(info.hProcess, INFINITE) == WAIT_OBJECT_0;
        let mut code = 0u32;
        let read = GetExitCodeProcess(info.hProcess, &mut code) != 0;
        CloseHandle(info.hProcess);
        if ended && read { Ok(code as i32) } else { Err("the installer did not report how it ended".into()) }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A file Windows itself ships is Microsoft-signed through its catalog only, not embedded: this check reads the embedded
    /// signature, as C#'s does; an unsigned file is never trusted.
    #[test]
    fn an_unsigned_file_is_never_trusted() {
        let file = std::env::temp_dir().join(format!("mighty-unsigned-{}.exe", std::process::id()));
        std::fs::write(&file, b"MZ not a program").unwrap();
        assert!(!is_microsoft_signed(&file));
        std::fs::remove_file(&file).unwrap();
        assert!(!is_microsoft_signed(Path::new(r"C:\no\such\file.exe")));
    }
}
