//! The files pane's ports on Windows (mighty-platform `files`): an open file's handle asked where it is (C#
//! `WorkspaceFiles.WindowsFileHandles`), a PDF's first page drawn by `Windows.Data.Pdf` (C# `WebFileImage`), the shell's file actions
//! (C# `explorer.exe /select`, `Launcher.LaunchFileAsync`, `DataPackage.SetBitmap`), and the user's date and decimal formats (C#
//! `CultureInfo.CurrentCulture`'s "g" and "0.#").

use std::ffi::OsStr;
use std::fs::File;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::io::AsRawHandle;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use mighty_platform::{ClipboardImage, Desktop, FileHandles, FilePorts, PdfPage, PdfRaster, PlatformError, Result};
use windows_sys::Win32::Foundation::{CloseHandle, FILETIME, HANDLE, INVALID_HANDLE_VALUE, SYSTEMTIME};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, FILE_TYPE_DISK, GetFileType,
    GetFinalPathNameByHandleW, OPEN_EXISTING,
};

fn wide(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(std::iter::once(0)).collect()
}

/// `MAX_PATH`: a path this long (in UTF-16 units, its NUL not counted) no longer reaches a plain Win32 call.
const MAX_PATH: usize = 260;

/// A path as a Win32 call takes it past `MAX_PATH`: one of 260 units or more gets `\\?\` (`\\?\UNC\` for a share), which lifts the
/// limit; a shorter one, or one Windows would still have to normalize (`.`, `..`, `/`), is left as it is, as the shell's own calls
/// expect it.
fn long_path(path: &Path) -> std::ffi::OsString {
    use std::path::{Component, Prefix};
    let text = path.as_os_str();
    let plain = !text.to_string_lossy().contains('/') && path.components().all(|c| !matches!(c, Component::CurDir | Component::ParentDir));
    if text.encode_wide().count() < MAX_PATH || !plain {
        return text.to_owned();
    }
    match path.components().next() {
        Some(Component::Prefix(prefix)) if matches!(prefix.kind(), Prefix::Disk(_)) => {
            let mut long = std::ffi::OsString::from(r"\\?\");
            long.push(text);
            long
        }
        Some(Component::Prefix(prefix)) if matches!(prefix.kind(), Prefix::UNC(..)) => {
            let units: Vec<u16> = r"\\?\UNC\".encode_utf16().chain(text.encode_wide().skip(2)).collect();
            std::ffi::OsString::from_wide(&units)
        }
        _ => text.to_owned(),
    }
}

/// A shell call's failure: a Win32 error wrapped as an HRESULT (`FACILITY_WIN32`, `0x8007xxxx`) is that error; any other HRESULT is
/// said as one (its value is no Win32 error number).
fn shell_error(result: i32) -> PlatformError {
    let code = result as u32;
    if code & 0xFFFF_0000 == 0x8007_0000 {
        PlatformError::Io(std::io::Error::from_raw_os_error((code & 0xFFFF) as i32))
    } else {
        PlatformError::Refused(format!("the shell refused (HRESULT 0x{code:08X})"))
    }
}

/// C# `WindowsFileHandles.FinalPath`: `GetFinalPathNameByHandleW` with `\\?\UNC\` written `\\` and `\\?\` taken off.
fn final_path(handle: HANDLE) -> Option<PathBuf> {
    let mut buffer = vec![0u16; 32_768];
    // SAFETY: an open handle and a buffer of the length given.
    let length = unsafe { GetFinalPathNameByHandleW(handle, buffer.as_mut_ptr(), buffer.len() as u32, 0) } as usize;
    if length == 0 || length >= buffer.len() {
        return None;
    }
    let value = String::from_utf16_lossy(&buffer[..length]);
    if let Some(rest) = value.strip_prefix(r"\\?\UNC\") {
        return Some(PathBuf::from(format!(r"\\{rest}")));
    }
    Some(PathBuf::from(value.strip_prefix(r"\\?\").unwrap_or(&value)))
}

/// The handle checks of `WorkspaceFiles.OpenFile` on Windows.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowsFileHandles;

impl FileHandles for WindowsFileHandles {
    fn asks_handles(&self) -> bool {
        true
    }

    fn is_disk(&self, file: &File) -> bool {
        // SAFETY: the file's own open handle.
        unsafe { GetFileType(file.as_raw_handle() as HANDLE) == FILE_TYPE_DISK }
    }

    fn final_path(&self, file: &File) -> Option<PathBuf> {
        final_path(file.as_raw_handle() as HANDLE)
    }

    /// C# `FinalDirectoryPath`: the folder opened with backup semantics (so a folder handle exists), no access asked, every share.
    fn final_directory_path(&self, path: &Path) -> Option<PathBuf> {
        let name = wide(&long_path(path));
        // SAFETY: a NUL-terminated path; the handle is closed below.
        let handle = unsafe {
            CreateFileW(
                name.as_ptr(),
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                std::ptr::null_mut(),
            )
        };
        if handle == INVALID_HANDLE_VALUE {
            return None;
        }
        let found = final_path(handle);
        // SAFETY: the handle CreateFileW opened.
        unsafe { CloseHandle(handle) };
        found
    }
}

/// A PDF's first page drawn by `Windows.Data.Pdf` (C# `PdfDocument.LoadFromStreamAsync`, `PdfPage.RenderToStreamAsync`), on a thread
/// of its own in the multithreaded apartment.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowsPdf;

fn pdf_error(error: windows::core::Error) -> PlatformError {
    PlatformError::Refused(format!("Windows.Data.Pdf: {error}"))
}

fn first_page(data: &[u8], size: &(dyn Fn(f64, f64) -> Option<(u32, u32)> + Sync)) -> windows::core::Result<Option<PdfPage>> {
    use windows::Data::Pdf::{PdfDocument, PdfPageRenderOptions};
    use windows::Storage::Streams::{DataReader, DataWriter, InMemoryRandomAccessStream};
    let stream = InMemoryRandomAccessStream::new()?;
    let writer = DataWriter::CreateDataWriter(&stream)?;
    writer.WriteBytes(data)?;
    writer.StoreAsync()?.get()?;
    writer.FlushAsync()?.get()?;
    writer.DetachStream()?;
    stream.Seek(0)?;
    let document = PdfDocument::LoadFromStreamAsync(&stream)?.get()?;
    if document.PageCount()? == 0 {
        return Ok(None);
    }
    let page = document.GetPage(0)?;
    let points = page.Size()?;
    let (width, height) = (points.Width as f64, points.Height as f64);
    let png = match size(width, height) {
        None => None,
        Some((pixels_wide, pixels_high)) => {
            let options = PdfPageRenderOptions::new()?;
            options.SetDestinationWidth(pixels_wide)?;
            options.SetDestinationHeight(pixels_high)?;
            let rendered = InMemoryRandomAccessStream::new()?;
            page.RenderWithOptionsToStreamAsync(&rendered, &options)?.get()?;
            let length = rendered.Size()? as u32;
            let reader = DataReader::CreateDataReader(&rendered.GetInputStreamAt(0)?)?;
            reader.LoadAsync(length)?.get()?;
            let mut bytes = vec![0u8; length as usize];
            reader.ReadBytes(&mut bytes)?;
            Some(bytes)
        }
    };
    let _ = page.Close();
    Ok(Some(PdfPage { width, height, png }))
}

impl PdfRaster for WindowsPdf {
    fn first_page(&self, data: &[u8], size: &(dyn Fn(f64, f64) -> Option<(u32, u32)> + Sync)) -> Result<Option<PdfPage>> {
        use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize};
        std::thread::scope(|scope| {
            scope
                .spawn(|| {
                    // SAFETY: this thread's own apartment, left again below.
                    let initialized = unsafe { RoInitialize(RO_INIT_MULTITHREADED) }.is_ok();
                    let page = first_page(data, size).map_err(pdf_error);
                    if initialized {
                        // SAFETY: paired with the RoInitialize above on this thread.
                        unsafe { RoUninitialize() };
                    }
                    page
                })
                .join()
                .unwrap_or_else(|_| Err(PlatformError::Refused("Windows.Data.Pdf panicked".into())))
        })
    }
}

/// Runs `work` on a thread of its own in a single-threaded apartment (the shell's calls want one).
fn in_apartment<T: Send>(work: impl FnOnce() -> T + Send) -> Option<T> {
    use windows_sys::Win32::System::Com::{COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE, CoInitializeEx, CoUninitialize};
    std::thread::scope(|scope| {
        scope
            .spawn(|| {
                // SAFETY: this thread's own apartment, left again below.
                let initialized =
                    unsafe { CoInitializeEx(std::ptr::null(), (COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) as u32) } >= 0;
                let value = work();
                if initialized {
                    // SAFETY: paired with the CoInitializeEx above on this thread.
                    unsafe { CoUninitialize() };
                }
                value
            })
            .join()
            .ok()
    })
}

/// File types whose content runs as code whatever opens them on a given computer: Excel loads an `.xll` add-in, Explorer runs a
/// `.search-ms` query against a remote share, a theme pack sets the cursors and sounds it carries, and the rest go to a runtime or a
/// launcher where one is installed (Java, Python, ClickOnce, WPF's PresentationHost, the troubleshooter, Remote Desktop).
const CODE_TYPES: [&str; 18] = [
    ".jar",
    ".application",
    ".appref-ms",
    ".xbap",
    ".xll",
    ".diagcab",
    ".rdp",
    ".theme",
    ".themepack",
    ".deskthemepack",
    ".search-ms",
    ".searchconnector-ms",
    ".library-ms",
    ".settingcontent-ms",
    ".py",
    ".pyw",
    ".pyz",
    ".pyzw",
];

/// The programs that run what they are handed as code (interpreters, script hosts, launchers, a remote session), by file name
/// without its extension: a type Windows hands to one of these is never opened, whatever its extension.
const LAUNCHERS: [&str; 26] = [
    "java",
    "javaw",
    "javaws",
    "py",
    "pyw",
    "python",
    "pythonw",
    "python3",
    "node",
    "wscript",
    "cscript",
    "mshta",
    "rundll32",
    "msdt",
    "powershell",
    "powershell_ise",
    "pwsh",
    "cmd",
    "presentationhost",
    "dfsvc",
    "dfshim",
    "mstsc",
    "regedit",
    "regsvr32",
    "msiexec",
    "wusa",
];

/// The program a handler names: a command line's program (quoted, else up to the first `.exe`/`.dll` that ends a word, else up to the
/// first blank) or a path as it is, by its file name without the extension, in lower case.
fn handler_program(handler: &str) -> String {
    let handler = handler.trim();
    let program = match handler.strip_prefix('"') {
        Some(rest) => rest.split('"').next().unwrap_or(""),
        None => {
            let lower = handler.to_ascii_lowercase();
            let ends = [".exe", ".dll"].iter().filter_map(|suffix| {
                lower
                    .match_indices(suffix)
                    .map(|(at, _)| at + suffix.len())
                    .find(|end| lower[*end..].is_empty() || lower[*end..].starts_with(' '))
            });
            match ends.min() {
                Some(end) => &handler[..end],
                None => handler.split(' ').next().unwrap_or(""),
            }
        }
    };
    Path::new(program).file_stem().map(|s| s.to_string_lossy().to_lowercase()).unwrap_or_default()
}

/// Whether opening a file of type `extension` (with its dot) with `handlers` (what Windows says opens it: its open verb's program and
/// command line) would run code: a type in [`CODE_TYPES`], or a handler in [`LAUNCHERS`].
pub fn opens_as_code(extension: &str, handlers: &[String]) -> bool {
    CODE_TYPES.iter().any(|t| t.eq_ignore_ascii_case(extension))
        || handlers.iter().any(|h| LAUNCHERS.contains(&handler_program(h).as_str()))
}

/// What Windows says opens a file of type `extension`: `AssocQueryStringW`'s executable and command line, for the `open` verb (which
/// the open asks for) and the type's default one, as the user's choices say. Empty when it says nothing.
pub fn handlers(extension: &str) -> Vec<String> {
    use windows_sys::Win32::UI::Shell::{ASSOCF_NOTRUNCATE, ASSOCSTR, ASSOCSTR_COMMAND, ASSOCSTR_EXECUTABLE, AssocQueryStringW};
    let extension = wide(OsStr::new(extension));
    let open = wide(OsStr::new("open"));
    let query = |what: ASSOCSTR, verb: *const u16| {
        let mut buffer = vec![0u16; 4096];
        let mut length = buffer.len() as u32;
        // SAFETY: NUL-terminated strings and a buffer of the length given; the length comes back with its NUL counted.
        let result = unsafe { AssocQueryStringW(ASSOCF_NOTRUNCATE, what, extension.as_ptr(), verb, buffer.as_mut_ptr(), &mut length) };
        (result == 0 && length > 1).then(|| String::from_utf16_lossy(&buffer[..length as usize - 1]))
    };
    // The association's COM objects want an apartment.
    in_apartment(|| {
        [
            (ASSOCSTR_EXECUTABLE, open.as_ptr()),
            (ASSOCSTR_COMMAND, open.as_ptr()),
            (ASSOCSTR_EXECUTABLE, std::ptr::null()),
            (ASSOCSTR_COMMAND, std::ptr::null()),
        ]
        .into_iter()
        .filter_map(|(what, verb)| query(what, verb))
        .collect()
    })
    .unwrap_or_default()
}

/// The type a file opens as: its name's last extension once the trailing dots and blanks Windows drops are gone (`a.jar.` opens as a
/// `.jar`), with its dot.
fn file_type(path: &Path) -> Option<String> {
    let name = path.file_name()?.to_string_lossy().trim_end_matches(['.', ' ']).to_string();
    let (stem, extension) = name.rsplit_once('.')?;
    (!stem.is_empty() || !extension.is_empty()).then(|| format!(".{extension}"))
}

/// Whether opening the file with its default app could run code; such a file is never opened. C#'s `Launcher.LaunchFileAsync` refuses
/// "file types that contain executable code, for example .exe, .msi, and .js" (its documentation; it takes no list); here that is a
/// type Windows calls dangerous (`AssocIsDangerous`: .exe, .bat, .lnk, .js, .msc, .url …), a type `PATHEXT` names, a type in
/// [`CODE_TYPES`], or one whose handler is in [`LAUNCHERS`] ([`handlers`], so a `.jar` with Java installed, a `.py` with Python, a
/// `.rdp`, `.xbap`, `.application` or `.themepack` are refused, which the launcher opens).
pub fn runs_code(path: &Path) -> bool {
    let Some(extension) = file_type(path) else { return false };
    let listed = std::env::var("PATHEXT").unwrap_or_default().split(';').any(|e| !e.is_empty() && e.eq_ignore_ascii_case(&extension));
    let name = wide(OsStr::new(&extension));
    // SAFETY: a NUL-terminated extension.
    listed
        || unsafe { windows_sys::Win32::UI::Shell::AssocIsDangerous(name.as_ptr()) } != 0
        || opens_as_code(&extension, &handlers(&extension))
}

/// The shell's file actions.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowsDesktop;

impl Desktop for WindowsDesktop {
    /// `SHOpenFolderAndSelectItems` (what `explorer.exe /select` does, without a child process of the app).
    fn reveal(&self, path: &Path) -> Result<()> {
        use windows_sys::Win32::UI::Shell::{ILFree, SHOpenFolderAndSelectItems, SHParseDisplayName};
        let name = wide(&long_path(path));
        let outcome = in_apartment(move || {
            let mut item = std::ptr::null_mut();
            // SAFETY: a NUL-terminated path; the item list is freed below.
            let parsed = unsafe { SHParseDisplayName(name.as_ptr(), std::ptr::null_mut(), &mut item, 0, std::ptr::null_mut()) };
            if parsed < 0 || item.is_null() {
                return parsed;
            }
            // SAFETY: the item list SHParseDisplayName made, freed once.
            let shown = unsafe { SHOpenFolderAndSelectItems(item, 0, std::ptr::null(), 0) };
            unsafe { ILFree(item) };
            shown
        });
        match outcome {
            Some(result) if result >= 0 => Ok(()),
            Some(result) => Err(shell_error(result)),
            None => Err(PlatformError::Refused("the shell call panicked".into())),
        }
    }

    /// The default app through `ShellExecuteExW` ("open", no error dialog); a type that can run code is refused first.
    fn open(&self, path: &Path) -> Result<bool> {
        use windows_sys::Win32::UI::Shell::{SEE_MASK_FLAG_NO_UI, SEE_MASK_NOASYNC, SHELLEXECUTEINFOW, ShellExecuteExW};
        use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
        if runs_code(path) || !path.is_file() {
            return Ok(false);
        }
        let (verb, file) = (wide(OsStr::new("open")), wide(&long_path(path)));
        let opened = in_apartment(move || {
            // SAFETY: an all-zero SHELLEXECUTEINFOW is its documented empty value.
            let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
            info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
            info.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
            info.lpVerb = verb.as_ptr();
            info.lpFile = file.as_ptr();
            info.nShow = SW_SHOWNORMAL;
            // SAFETY: the structure and the strings it points at live across the call.
            unsafe { ShellExecuteExW(&mut info) != 0 }
        });
        Ok(opened.unwrap_or(false))
    }

    /// The picture as `CF_DIBV5` (32-bit, straight alpha, sRGB) and the registered "PNG" format, as `DataPackage.SetBitmap` offers
    /// it; Windows makes `CF_DIB` and `CF_BITMAP` from the first. The clipboard keeps both after the app quits.
    fn copy_image(&self, image: &ClipboardImage) -> Result<()> {
        let dib = dib_v5(image);
        let png = image.png.clone();
        let owner = clipboard::main_window();
        let outcome = in_apartment(move || clipboard::put(owner, &dib, &png));
        match outcome {
            Some(Ok(())) => Ok(()),
            Some(Err(error)) => Err(PlatformError::Io(error)),
            None => Err(PlatformError::Refused("the clipboard call panicked".into())),
        }
    }
}

/// A `BITMAPV5HEADER` and the pixels, bottom row first, as BGRA.
fn dib_v5(image: &ClipboardImage) -> Vec<u8> {
    const HEADER: u32 = 124;
    let (width, height) = (image.width as usize, image.height as usize);
    let mut out = Vec::with_capacity(HEADER as usize + width * height * 4);
    let put32 = |out: &mut Vec<u8>, value: u32| out.extend_from_slice(&value.to_le_bytes());
    put32(&mut out, HEADER);
    put32(&mut out, image.width);
    put32(&mut out, image.height);
    out.extend_from_slice(&1u16.to_le_bytes());
    out.extend_from_slice(&32u16.to_le_bytes());
    put32(&mut out, 3); // BI_BITFIELDS
    put32(&mut out, (width * height * 4) as u32);
    put32(&mut out, 2835); // 72 DPI
    put32(&mut out, 2835);
    put32(&mut out, 0);
    put32(&mut out, 0);
    for mask in [0x00FF_0000u32, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000] {
        put32(&mut out, mask);
    }
    put32(&mut out, 0x7352_4742); // LCS_sRGB
    out.extend_from_slice(&[0u8; 36]); // endpoints
    for _ in 0..3 {
        put32(&mut out, 0); // gamma
    }
    put32(&mut out, 4); // LCS_GM_IMAGES
    for _ in 0..3 {
        put32(&mut out, 0); // profile data, size, reserved
    }
    for row in (0..height).rev() {
        for pixel in image.rgba[row * width * 4..(row + 1) * width * 4].as_chunks::<4>().0 {
            out.extend_from_slice(&[pixel[2], pixel[1], pixel[0], pixel[3]]);
        }
    }
    out
}

mod clipboard {
    use windows_sys::Win32::Foundation::GlobalFree;
    use windows_sys::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, OpenClipboard, RegisterClipboardFormatW, SetClipboardData,
    };
    use windows_sys::Win32::System::Memory::{GMEM_MOVEABLE, GlobalAlloc, GlobalLock, GlobalUnlock};
    use windows_sys::Win32::System::Ole::CF_DIBV5;

    /// The bytes in a movable global block the clipboard can own.
    fn global(bytes: &[u8]) -> std::io::Result<windows_sys::Win32::Foundation::HGLOBAL> {
        // SAFETY: a block of the bytes' length, locked while they are copied in.
        unsafe {
            let block = GlobalAlloc(GMEM_MOVEABLE, bytes.len().max(1));
            if block.is_null() {
                return Err(std::io::Error::last_os_error());
            }
            let target = GlobalLock(block) as *mut u8;
            if target.is_null() {
                GlobalFree(block);
                return Err(std::io::Error::last_os_error());
            }
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), target, bytes.len());
            GlobalUnlock(block);
            Ok(block)
        }
    }

    /// The app's main window, as a number (a window handle is not `Send`): a visible top-level window of this process that no other
    /// window owns; 0 in a process with none (the checks).
    pub fn main_window() -> isize {
        use windows_sys::Win32::Foundation::{HWND, LPARAM};
        use windows_sys::Win32::System::Threading::GetCurrentProcessId;
        use windows_sys::Win32::UI::WindowsAndMessaging::{EnumWindows, GW_OWNER, GetWindow, GetWindowThreadProcessId, IsWindowVisible};
        unsafe extern "system" fn each(window: HWND, found: LPARAM) -> windows_sys::core::BOOL {
            let mut process = 0u32;
            // SAFETY: a window EnumWindows handed over; `found` is the isize below, alive across EnumWindows.
            unsafe {
                GetWindowThreadProcessId(window, &mut process);
                if process == GetCurrentProcessId() && IsWindowVisible(window) != 0 && GetWindow(window, GW_OWNER).is_null() {
                    *(found as *mut isize) = window as isize;
                    return 0;
                }
            }
            1
        }
        let mut found = 0isize;
        // SAFETY: the callback writes only `found`, which outlives the call.
        unsafe { EnumWindows(Some(each), &mut found as *mut isize as LPARAM) };
        found
    }

    /// Both formats put on the clipboard, opened by `owner` (the app's main window, which the clipboard then names as its owner; with
    /// none, `EmptyClipboard` leaves the clipboard ownerless, which its documentation says `SetClipboardData` may refuse).
    pub fn put(owner: isize, dib: &[u8], png: &[u8]) -> std::io::Result<()> {
        // The clipboard may be held a moment by another app: tried a few times.
        let mut opened = false;
        for _ in 0..10 {
            // SAFETY: the app's own window, or none.
            if unsafe { OpenClipboard(owner as windows_sys::Win32::Foundation::HWND) } != 0 {
                opened = true;
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        if !opened {
            return Err(std::io::Error::last_os_error());
        }
        let result = (|| {
            // SAFETY: the clipboard is open on this thread; a block handed to SetClipboardData is the clipboard's from then on.
            unsafe {
                if EmptyClipboard() == 0 {
                    return Err(std::io::Error::last_os_error());
                }
                let name: Vec<u16> = "PNG".encode_utf16().chain(std::iter::once(0)).collect();
                let png_format = RegisterClipboardFormatW(name.as_ptr());
                for (format, bytes) in [(CF_DIBV5 as u32, dib), (png_format, png)] {
                    if format == 0 {
                        continue;
                    }
                    let block = global(bytes)?;
                    if SetClipboardData(format, block).is_null() {
                        let error = std::io::Error::last_os_error();
                        GlobalFree(block);
                        return Err(error);
                    }
                }
                Ok(())
            }
        })();
        // SAFETY: opened above.
        unsafe { CloseClipboard() };
        result
    }
}

/// The files ports of Windows.
pub fn file_ports() -> FilePorts {
    FilePorts {
        handles: std::sync::Arc::new(WindowsFileHandles),
        pdf: std::sync::Arc::new(WindowsPdf),
        desktop: std::sync::Arc::new(WindowsDesktop),
    }
}

/// C# `ToLocalTime().ToString("g", CultureInfo.CurrentCulture)`: the user's short date and short time (their own regional format,
/// `GetDateFormatEx`/`GetTimeFormatEx` of the user's default locale), in local time. None when Windows does not say.
pub fn short_date_time(at: SystemTime) -> Option<String> {
    use windows_sys::Win32::Globalization::{DATE_SHORTDATE, GetDateFormatEx, GetTimeFormatEx, TIME_NOSECONDS};
    use windows_sys::Win32::System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTime};
    let since = at.duration_since(SystemTime::UNIX_EPOCH).ok()?;
    let ticks = since.as_nanos() / 100 + 116_444_736_000_000_000;
    let file_time = FILETIME { dwLowDateTime: ticks as u32, dwHighDateTime: (ticks >> 32) as u32 };
    // SAFETY: plain structures written by the calls.
    let (mut utc, mut local): (SYSTEMTIME, SYSTEMTIME) = unsafe { (std::mem::zeroed(), std::mem::zeroed()) };
    if unsafe { FileTimeToSystemTime(&file_time, &mut utc) } == 0
        || unsafe { SystemTimeToTzSpecificLocalTime(std::ptr::null(), &utc, &mut local) } == 0
    {
        return None;
    }
    let mut date = [0u16; 128];
    let mut time = [0u16; 128];
    // SAFETY: a null locale is the user's default; the buffers and their lengths.
    let date_length = unsafe {
        GetDateFormatEx(std::ptr::null(), DATE_SHORTDATE, &local, std::ptr::null(), date.as_mut_ptr(), date.len() as i32, std::ptr::null())
    };
    let time_length =
        unsafe { GetTimeFormatEx(std::ptr::null(), TIME_NOSECONDS, &local, std::ptr::null(), time.as_mut_ptr(), time.len() as i32) };
    if date_length <= 0 || time_length <= 0 {
        return None;
    }
    let text = |units: &[u16]| std::ffi::OsString::from_wide(units).to_string_lossy().into_owned();
    Some(format!("{} {}", text(&date[..date_length as usize - 1]), text(&time[..time_length as usize - 1])))
}

/// The user's decimal separator (`LOCALE_SDECIMAL`), when Windows says.
pub fn decimal_separator() -> Option<String> {
    use windows_sys::Win32::Globalization::{GetLocaleInfoEx, LOCALE_SDECIMAL};
    let mut buffer = [0u16; 16];
    // SAFETY: a null name is LOCALE_NAME_USER_DEFAULT; the buffer and its length in UTF-16 units, NUL included.
    let written = unsafe { GetLocaleInfoEx(std::ptr::null(), LOCALE_SDECIMAL, buffer.as_mut_ptr(), buffer.len() as i32) };
    (written > 1).then(|| String::from_utf16_lossy(&buffer[..written as usize - 1]))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The handle of a file opened under a folder says the folder's final path is where it is.
    #[test]
    fn an_open_file_says_where_it_is() {
        let folder = std::env::temp_dir().join(format!("mighty-handles-{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        let path = folder.join("a.txt");
        std::fs::write(&path, "x").unwrap();
        let file = File::open(&path).unwrap();
        let handles = WindowsFileHandles;
        assert!(handles.is_disk(&file));
        let final_file = handles.final_path(&file).unwrap();
        let final_folder = handles.final_directory_path(&folder).unwrap();
        assert!(final_file.starts_with(&final_folder), "{final_file:?} under {final_folder:?}");
        assert!(!final_file.to_string_lossy().starts_with(r"\\?\"));
        drop(file);
        let _ = std::fs::remove_dir_all(&folder);
    }

    /// Programs, scripts and links are never opened; a document is the shell's.
    #[test]
    fn files_that_run_code_are_refused() {
        for name in ["setup.exe", "run.bat", "x.cmd", "a.js", "b.vbs", "c.lnk", "d.ps1", "e.msi", "f.scr", "g.hta"] {
            assert!(runs_code(Path::new(name)), "{name}");
        }
        for name in ["notes.txt", "photo.png", "page.html", "data.json", "README.md", "noextension"] {
            assert!(!runs_code(Path::new(name)), "{name}");
        }
    }

    /// The types that run code through a general app or a runtime are refused whatever this computer has installed, also when Windows
    /// would drop a trailing dot or blank from the name.
    #[test]
    fn types_that_run_through_a_runtime_or_launcher_are_refused() {
        for name in [
            "a.jar",
            "a.application",
            "a.xbap",
            "a.xll",
            "a.diagcab",
            "a.rdp",
            "a.themepack",
            "a.search-ms",
            "a.searchConnector-ms",
            "a.py",
            "a.PYW",
            "a.jar.",
            "a.jar ",
            "setup.exe. .",
        ] {
            assert!(runs_code(Path::new(name)), "{name}");
        }
    }

    /// The decision on its own: a type in the list, or a handler that is an interpreter or launcher (its path or its command line,
    /// quoted or not), runs code; a document's viewer does not.
    #[test]
    fn handlers_that_are_interpreters_or_launchers_run_code() {
        let handlers = |items: &[&str]| items.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        assert_eq!(handler_program(r#""C:\Program Files\Java\bin\javaw.exe" -jar "%1" %*"#), "javaw");
        assert_eq!(handler_program(r"C:\Program Files\Eclipse Adoptium\jdk-21\bin\javaw.exe"), "javaw");
        assert_eq!(handler_program(r#"C:\Windows\system32\msdt.exe /cab "%1""#), "msdt");
        assert_eq!(handler_program(r"C:\Windows\system32\rundll32.exe C:\Windows\system32\themecpl.dll,OpenThemeAction %1"), "rundll32");
        assert_eq!(handler_program(r"C:\Windows\System32\dfshim.dll"), "dfshim");
        assert_eq!(handler_program(r#"regedit.exe "%1""#), "regedit");
        assert_eq!(handler_program(r#""%1" %*"#), "%1");
        assert!(opens_as_code(".anything", &handlers(&[r"C:\Python312\py.exe", r#""C:\Python312\py.exe" "%1" %*"#])));
        assert!(opens_as_code(".mjs", &handlers(&[r#"C:\Program Files\nodejs\node.exe "%1""#])));
        assert!(opens_as_code(
            ".x",
            &handlers(&[
                r"C:\Windows\System32\dfshim.dll",
                r#""C:\Windows\System32\rundll32.exe" "C:\Windows\System32\dfshim.dll",ShOpenVerbApplication %1"#
            ])
        ));
        assert!(
            opens_as_code(".XLL", &handlers(&[r"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE"])),
            "listed whatever opens it"
        );
        assert!(!opens_as_code(".xlsx", &handlers(&[r"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE"])));
        assert!(!opens_as_code(".txt", &handlers(&[r"C:\Windows\system32\NOTEPAD.EXE", r"C:\Windows\system32\NOTEPAD.EXE %1"])));
        assert!(!opens_as_code(".png", &handlers(&[r#""C:\Program Files\BandiView\BandiView.exe" "%1""#])));
        assert!(!opens_as_code(".md", &[]), "a type nothing opens is the launcher's to refuse or not");
    }

    /// With this computer's own associations: where a type's handler is an interpreter or launcher, the handler alone refuses it (the
    /// list aside); the documents this computer opens are still opened.
    #[test]
    fn real_associations_refuse_launchers_and_keep_documents() {
        for (extension, program) in [
            (".rdp", "mstsc"),
            (".diagcab", "msdt"),
            (".xbap", "presentationhost"),
            (".application", "rundll32"),
            (".themepack", "rundll32"),
            (".jar", "javaw"),
            (".py", "py"),
            (".vbs", "wscript"),
        ] {
            let found = handlers(extension);
            if found.iter().any(|h| handler_program(h) == program) {
                assert!(opens_as_code(".unlisted", &found), "{extension} opens with {program}: {found:?}");
            } else {
                eprintln!("  ({extension} does not open with {program} here: {found:?})");
            }
        }
        assert!(!handlers(".txt").is_empty(), "Windows says what opens a .txt");
        for document in ["a.txt", "a.png", "a.jpg", "a.pdf", "a.html", "a.md", "a.json", "a.csv", "a.docx", "a.xlsx", "a.zip", "a.log"] {
            assert!(
                !runs_code(Path::new(document)),
                "{document} opens as a document: {:?}",
                handlers(&file_type(Path::new(document)).unwrap())
            );
        }
    }

    /// A Win32 error carried in an HRESULT reads as that error; any other HRESULT is said as one.
    #[test]
    fn shell_failures_say_what_failed() {
        match shell_error(0x8007_0002u32 as i32) {
            PlatformError::Io(error) => assert_eq!(error.raw_os_error(), Some(2)),
            other => panic!("{other:?}"),
        }
        let other = shell_error(0x8000_4005u32 as i32).to_string();
        assert!(other.contains("0x80004005"), "{other}");
    }

    /// A folder past MAX_PATH is reached through `\\?\`; a short path is left as it is.
    #[test]
    fn long_paths_reach_win32() {
        assert_eq!(long_path(Path::new(r"C:\short")), OsStr::new(r"C:\short"));
        let long = format!(r"C:\{}", "a".repeat(300));
        assert_eq!(long_path(Path::new(&long)), OsStr::new(&format!(r"\\?\{long}")));
        let share = format!(r"\\server\share\{}", "b".repeat(300));
        assert_eq!(long_path(Path::new(&share)), OsStr::new(&format!(r"\\?\UNC\server\share\{}", "b".repeat(300))));
        let relative = format!(r"C:\{}\..\x", "c".repeat(300));
        assert_eq!(long_path(Path::new(&relative)), OsStr::new(&relative), "a path Windows must normalize is left to it");
        let root = std::env::temp_dir().join(format!("mighty-long-{}", std::process::id()));
        let folder = (0..6).fold(root.clone(), |path, n| path.join(format!("{n}-{}", "d".repeat(50))));
        std::fs::create_dir_all(&folder).unwrap();
        assert!(folder.as_os_str().len() >= MAX_PATH);
        let final_folder = WindowsFileHandles.final_directory_path(&folder);
        let _ = std::fs::remove_dir_all(&root);
        assert!(final_folder.is_some_and(|f| f.ends_with(folder.file_name().unwrap())), "a folder past MAX_PATH has its final path");
    }

    /// The DIB is a 124-byte V5 header and the rows bottom first, BGRA.
    #[test]
    fn the_clipboard_picture_is_a_v5_dib() {
        let image = ClipboardImage { width: 2, height: 1, rgba: vec![255, 0, 0, 255, 0, 0, 255, 128], png: vec![] };
        let dib = dib_v5(&image);
        assert_eq!(dib.len(), 124 + 8);
        assert_eq!(&dib[..4], &124u32.to_le_bytes());
        assert_eq!(&dib[124..], &[0, 0, 255, 255, 255, 0, 0, 128]);
    }

    /// A one-page PDF (200 × 100 points) with its cross-reference table.
    fn pdf() -> Vec<u8> {
        let objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>",
        ];
        let mut out = b"%PDF-1.4\n".to_vec();
        let mut offsets = vec![];
        for (index, body) in objects.iter().enumerate() {
            offsets.push(out.len());
            out.extend_from_slice(format!("{} 0 obj\n{body}\nendobj\n", index + 1).as_bytes());
        }
        let table = out.len();
        out.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", objects.len() + 1).as_bytes());
        for offset in offsets {
            out.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
        }
        out.extend_from_slice(format!("trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n", objects.len() + 1).as_bytes());
        out
    }

    /// Windows.Data.Pdf reads the first page's size (in DIPs, 96 to the inch) and draws it at the size asked for, as PNG.
    #[test]
    fn a_pdf_first_page_is_drawn() {
        let page = WindowsPdf.first_page(&pdf(), &|width, height| Some(((width / 2.0) as u32, (height / 2.0) as u32))).unwrap().unwrap();
        assert!((page.width - 200.0 * 96.0 / 72.0).abs() < 1.0 && (page.height - 100.0 * 96.0 / 72.0).abs() < 1.0, "{page:?}");
        let png = page.png.unwrap();
        assert!(png.starts_with(&[0x89, b'P', b'N', b'G']), "a PNG");
        let width = u32::from_be_bytes([png[16], png[17], png[18], png[19]]);
        assert_eq!(width, (page.width / 2.0) as u32, "drawn at the size asked for");
        assert_eq!(WindowsPdf.first_page(&pdf(), &|_, _| None).unwrap().unwrap().png, None, "not drawn when no size is asked for");
        assert!(WindowsPdf.first_page(b"not a pdf", &|_, _| None).is_err(), "a document that does not load");
    }

    #[test]
    fn the_user_formats_read() {
        let text = short_date_time(SystemTime::UNIX_EPOCH + std::time::Duration::from_secs(1_794_000_000)).unwrap();
        assert!(text.chars().any(|c| c.is_ascii_digit()), "{text}");
        assert!(decimal_separator().is_some_and(|s| !s.is_empty()));
    }
}
