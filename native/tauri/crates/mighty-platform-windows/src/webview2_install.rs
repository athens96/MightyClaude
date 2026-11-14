//! The WebView2 Evergreen runtime's per-user install (C# `WebView2BootstrapperInstaller` and `WinTrustVerify`,
//! MainWindow.Browser.cs:118-213): Microsoft's bootstrapper downloaded over HTTPS, its Authenticode signature checked (valid, its
//! signer's common name exactly Microsoft Corporation and its chain ending in a Microsoft root), then run as the user with
//! `/silent /install`, as a plain child (no elevation asked, as C#'s `UseShellExecute = false`). Between the last check and the start
//! the file is held open with no one else allowed to write it or replace it, so what runs is what was checked. Nothing here runs
//! until the user asks (`browserInstall`).

use std::fs::File;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::path::Path;

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows_sys::Win32::Security::Cryptography::{
    CERT_CHAIN_CONTEXT, CERT_CHAIN_POLICY_MICROSOFT_ROOT, CERT_CHAIN_POLICY_PARA, CERT_CHAIN_POLICY_STATUS, CERT_CONTEXT,
    CERT_NAME_ATTR_TYPE, CertGetNameStringW, CertVerifyCertificateChainPolicy,
    MICROSOFT_ROOT_CERT_CHAIN_POLICY_CHECK_APPLICATION_ROOT_FLAG, MICROSOFT_ROOT_CERT_CHAIN_POLICY_DISABLE_FLIGHT_ROOT_FLAG,
    szOID_COMMON_NAME,
};
use windows_sys::Win32::Security::WinTrust::{
    WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_FILE_INFO, WTD_CACHE_ONLY_URL_RETRIEVAL, WTD_CHOICE_FILE, WTD_REVOKE_NONE,
    WTD_STATEACTION_CLOSE, WTD_STATEACTION_VERIFY, WTD_UI_NONE, WTHelperGetProvSignerFromChain, WTHelperProvDataFromStateData,
    WinVerifyTrust,
};
use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_READ;
use windows_sys::Win32::System::Com::Urlmon::URLDownloadToFileW;
use windows_sys::Win32::System::Threading::{
    CREATE_NO_WINDOW, CreateProcessW, GetExitCodeProcess, INFINITE, PROCESS_INFORMATION, STARTUPINFOW, WaitForSingleObject,
};

/// Microsoft's per-user bootstrapper.
const BOOTSTRAPPER_URL: &str = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
/// The signer's common name, exactly.
const MICROSOFT: &str = "Microsoft Corporation";

fn wide(text: &std::ffi::OsStr) -> Vec<u16> {
    text.encode_wide().chain(std::iter::once(0)).collect()
}

/// `DownloadBootstrapperAsync`: the bootstrapper written to `path`. urlmon is COM: the download runs on a thread of its own in the
/// multithreaded apartment.
pub fn download_bootstrapper(path: &Path) -> Result<(), String> {
    use windows_sys::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
    let (url, file) = (wide(BOOTSTRAPPER_URL.as_ref()), wide(path.as_os_str()));
    let result = std::thread::scope(|scope| {
        scope
            .spawn(|| {
                // SAFETY: this thread's own apartment, left again below.
                let initialized = unsafe { CoInitializeEx(std::ptr::null(), COINIT_MULTITHREADED as u32) } >= 0;
                // SAFETY: two NUL-terminated strings that live across the call; no caller object and no progress callback.
                let result = unsafe { URLDownloadToFileW(std::ptr::null_mut(), url.as_ptr(), file.as_ptr(), 0, std::ptr::null_mut()) };
                if initialized {
                    // SAFETY: paired with the CoInitializeEx above on this thread.
                    unsafe { CoUninitialize() };
                }
                result
            })
            .join()
    })
    .map_err(|_| "the download panicked".to_string())?;
    if result == 0 { Ok(()) } else { Err(std::io::Error::from_raw_os_error(result).to_string()) }
}

/// The file opened for reading with no one else allowed to write, delete or rename it while it is open.
fn held(path: &Path) -> std::io::Result<File> {
    std::fs::OpenOptions::new().read(true).share_mode(FILE_SHARE_READ).open(path)
}

/// `WinTrustVerify.IsMicrosoftSigned`: a valid Authenticode signature whose signer's common name is exactly Microsoft Corporation and
/// whose chain ends in a Microsoft root (`CERT_CHAIN_POLICY_MICROSOFT_ROOT`); false for anything else. C# accepted any subject that
/// contains "Microsoft Corporation".
pub fn is_microsoft_signed(path: &Path) -> bool {
    held(path).is_ok_and(|file| signed_by_microsoft(path, &file))
}

/// [`is_microsoft_signed`] for the file `file` holds open (WinVerifyTrust reads it through the handle).
fn signed_by_microsoft(path: &Path, file: &File) -> bool {
    let file_path = wide(path.as_os_str());
    let mut info = WINTRUST_FILE_INFO {
        cbStruct: std::mem::size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: file_path.as_ptr(),
        hFile: file.as_raw_handle() as HANDLE,
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
    data.Anonymous.pFile = &mut info;
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    // SAFETY: the structures above live across the calls; the state the verify leaves is read, then closed with the same data.
    unsafe {
        let trusted = WinVerifyTrust(std::ptr::null_mut(), &mut action, (&mut data as *mut WINTRUST_DATA).cast()) == 0;
        let microsoft = trusted && signer_is_microsoft(data.hWVTStateData);
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(std::ptr::null_mut(), &mut action, (&mut data as *mut WINTRUST_DATA).cast());
        microsoft
    }
}

/// Whether the signer of a verify's state is Microsoft: its certificate's common name is exactly [`MICROSOFT`] and its chain passes
/// the Microsoft root policy.
///
/// # Safety
/// `state` is the `hWVTStateData` a successful `WTD_STATEACTION_VERIFY` left, not yet closed.
unsafe fn signer_is_microsoft(state: HANDLE) -> bool {
    unsafe {
        let provider = WTHelperProvDataFromStateData(state);
        if provider.is_null() {
            return false;
        }
        let signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if signer.is_null() || (*signer).csCertChain == 0 || (*signer).pasCertChain.is_null() || (*signer).pChainContext.is_null() {
            return false;
        }
        let certificate = (*(*signer).pasCertChain).pCert;
        !certificate.is_null() && is_microsoft_name(common_name(certificate).as_deref()) && microsoft_root((*signer).pChainContext)
    }
}

/// The certificate's subject common name.
///
/// # Safety
/// A valid certificate context.
unsafe fn common_name(certificate: *const CERT_CONTEXT) -> Option<String> {
    let mut name = [0u16; 512];
    // SAFETY: the certificate and a buffer of the length given; the attribute's OID is a static string.
    let written = unsafe {
        CertGetNameStringW(certificate, CERT_NAME_ATTR_TYPE, 0, szOID_COMMON_NAME as *const _, name.as_mut_ptr(), name.len() as u32)
    };
    (written > 1).then(|| String::from_utf16_lossy(&name[..written as usize - 1]))
}

/// Whether the chain ends in one of Microsoft's own roots (the product roots, and the application root "Microsoft Root Certificate
/// Authority 2011" that the bootstrapper's and the runtime's signatures end in; never a flight or test root).
///
/// # Safety
/// A valid chain context.
unsafe fn microsoft_root(chain: *const CERT_CHAIN_CONTEXT) -> bool {
    let para = CERT_CHAIN_POLICY_PARA {
        cbSize: std::mem::size_of::<CERT_CHAIN_POLICY_PARA>() as u32,
        dwFlags: MICROSOFT_ROOT_CERT_CHAIN_POLICY_CHECK_APPLICATION_ROOT_FLAG | MICROSOFT_ROOT_CERT_CHAIN_POLICY_DISABLE_FLIGHT_ROOT_FLAG,
        ..Default::default()
    };
    let mut status = CERT_CHAIN_POLICY_STATUS { cbSize: std::mem::size_of::<CERT_CHAIN_POLICY_STATUS>() as u32, ..Default::default() };
    // SAFETY: the chain and the two structures, which live across the call.
    let checked = unsafe { CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_MICROSOFT_ROOT, chain, &para, &mut status) } != 0;
    checked && status.dwError == 0
}

/// The signer's name is Microsoft's: exactly, not one that only contains it ("Not Microsoft Corporation Ltd").
fn is_microsoft_name(name: Option<&str>) -> bool {
    name == Some(MICROSOFT)
}

/// `RunInstallerAsync`: the bootstrapper checked once more through a handle that keeps it from being written or replaced, started as
/// a plain child of the user (`/silent /install`, no window, no handle inherited, no elevation asked), the handle let go once it
/// started, and its exit code once it ended.
pub fn run_installer(path: &Path) -> Result<i32, String> {
    let file = held(path).map_err(|e| e.to_string())?;
    if !signed_by_microsoft(path, &file) {
        return Err("the installer is no longer the one Microsoft signed".into());
    }
    let application = wide(path.as_os_str());
    let mut command: Vec<u16> = format!("\"{}\" /silent /install", path.display()).encode_utf16().chain(std::iter::once(0)).collect();
    let startup = STARTUPINFOW { cb: std::mem::size_of::<STARTUPINFOW>() as u32, ..Default::default() };
    let mut process = PROCESS_INFORMATION::default();
    // SAFETY: the strings live across the call (the command line is writable, as CreateProcessW wants); nothing is inherited; the
    // process and thread handles it returns are closed here.
    unsafe {
        let started = CreateProcessW(
            application.as_ptr(),
            command.as_mut_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            CREATE_NO_WINDOW,
            std::ptr::null(),
            std::ptr::null(),
            &startup,
            &mut process,
        ) != 0;
        let error = std::io::Error::last_os_error();
        drop(file);
        if !started {
            return Err(error.to_string());
        }
        CloseHandle(process.hThread);
        let ended = WaitForSingleObject(process.hProcess, INFINITE) == WAIT_OBJECT_0;
        let mut code = 0u32;
        let read = GetExitCodeProcess(process.hProcess, &mut code) != 0;
        CloseHandle(process.hProcess);
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
        assert!(run_installer(&file).is_err(), "an unsigned file is never started");
        std::fs::remove_file(&file).unwrap();
        assert!(!is_microsoft_signed(Path::new(r"C:\no\such\file.exe")));
    }

    /// Only the exact name: a signer whose name merely contains Microsoft Corporation is someone else.
    #[test]
    fn only_microsoft_corporation_itself_is_microsoft() {
        assert!(is_microsoft_name(Some("Microsoft Corporation")));
        for other in ["Not Microsoft Corporation Ltd", "microsoft corporation", "Microsoft Corporation ", "Microsoft"] {
            assert!(!is_microsoft_name(Some(other)), "{other}");
        }
        assert!(!is_microsoft_name(None));
    }

    /// The installed WebView2 runtime's own program carries Microsoft's embedded signature (the bootstrapper's signer and roots); a
    /// program signed by anyone else is refused, where one is installed.
    #[test]
    fn microsofts_own_program_is_trusted_and_others_are_not() {
        let roots = [r"C:\Program Files (x86)\Microsoft\EdgeWebView\Application", r"C:\Program Files\Microsoft\EdgeWebView\Application"];
        let runtime = roots
            .iter()
            .filter_map(|root| std::fs::read_dir(root).ok())
            .flat_map(|versions| versions.flatten().map(|v| v.path().join("msedgewebview2.exe")))
            .find(|program| program.is_file());
        match runtime {
            Some(program) => assert!(is_microsoft_signed(&program), "{}", program.display()),
            None => eprintln!("  (no WebView2 runtime program here)"),
        }
        let others = [r"C:\Program Files\Bandizip\Bandizip.exe", r"C:\Program Files\Google\Chrome\Application\chrome.exe"];
        for other in others.iter().map(Path::new).filter(|p| p.is_file()) {
            assert!(!is_microsoft_signed(other), "{}", other.display());
        }
    }
}
