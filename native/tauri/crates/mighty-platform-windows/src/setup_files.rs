//! The setup screen's install folder on Windows (C# `SetupScriptFiles`, SetupPlan.cs:215-333): a folder created with this user as
//! its owner and an access list of this user alone (full control, nothing inherited), refused when it is a junction, a link or
//! another reparse point or belongs to someone else, its list put back on every run; markers that are real folders only; the
//! leftovers of earlier runs removed without following a link; whether the app holds an administrator's token; whether the
//! script's PowerShell still runs. W6-B.

use std::ffi::c_void;
use std::os::windows::fs::MetadataExt;
use std::path::Path;
use std::ptr::{null, null_mut};

use mighty_platform::{PlatformError, Result, SetupFiles};
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HLOCAL, LocalFree};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, GetNamedSecurityInfoW, SDDL_REVISION_1, SE_FILE_OBJECT,
    SetNamedSecurityInfoW,
};
use windows_sys::Win32::Security::{
    ACL, CheckTokenMembership, CreateWellKnownSid, DACL_SECURITY_INFORMATION, EqualSid, GetSecurityDescriptorDacl, GetTokenInformation,
    OWNER_SECURITY_INFORMATION, PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID, SECURITY_ATTRIBUTES, TOKEN_QUERY,
    TOKEN_USER, TokenUser, WinBuiltinAdministratorsSid,
};
use windows_sys::Win32::Storage::FileSystem::CreateDirectoryW;
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, GetExitCodeProcess, OpenProcess, OpenProcessToken, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    QueryFullProcessImageNameW,
};
use windows_sys::core::BOOL;

use crate::win::{equal_ignore_case, wide};

const FILE_ATTRIBUTE_DIRECTORY: u32 = 0x10;
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
/// `GetExitCodeProcess`' answer for a process still running.
const STILL_ACTIVE: u32 = 259;

/// The setup folder's port on Windows.
#[derive(Debug, Default, Clone, Copy)]
pub struct WindowsSetupFiles;

/// The current user's SID, as its bytes and its SDDL text.
struct User {
    sid: Vec<u8>,
    text: String,
}

impl User {
    fn current() -> Result<User> {
        let mut token: HANDLE = null_mut();
        // SAFETY: the token is closed below; the buffer is u64-aligned for TOKEN_USER and sized by the first call; the SID's text
        // is freed with LocalFree.
        unsafe {
            if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
                return Err(refused("no user identity"));
            }
            let mut length = 0u32;
            GetTokenInformation(token, TokenUser, null_mut(), 0, &mut length);
            let mut buffer = vec![0u64; (length as usize).div_ceil(8)];
            let read = GetTokenInformation(token, TokenUser, buffer.as_mut_ptr() as *mut c_void, length, &mut length);
            CloseHandle(token);
            if read == 0 {
                return Err(refused("no user identity"));
            }
            let user = &*(buffer.as_ptr() as *const TOKEN_USER);
            let size = windows_sys::Win32::Security::GetLengthSid(user.User.Sid) as usize;
            let sid = std::slice::from_raw_parts(user.User.Sid as *const u8, size).to_vec();
            let mut text: *mut u16 = null_mut();
            if ConvertSidToStringSidW(user.User.Sid, &mut text) == 0 {
                return Err(refused("the user's SID could not be read"));
            }
            let length = (0..).take_while(|&at| *text.add(at) != 0).count();
            let value = String::from_utf16_lossy(std::slice::from_raw_parts(text, length));
            LocalFree(text as HLOCAL);
            Ok(User { sid, text: value })
        }
    }

    fn psid(&self) -> PSID {
        self.sid.as_ptr() as PSID
    }
}

/// A security descriptor made from SDDL, freed on drop.
struct Descriptor(PSECURITY_DESCRIPTOR);

impl Descriptor {
    /// C# `UserOnly`: this user the owner, a protected list with one entry, full control for this user inherited by folders and
    /// files (`FA` is `FileSystemRights.FullControl`, 0x1F01FF).
    fn user_only(user: &User) -> Result<Descriptor> {
        let sddl = wide(format!("O:{0}D:P(A;OICI;FA;;;{0})", user.text));
        let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
        // SAFETY: a NUL-terminated SDDL string; the descriptor is allocated with LocalAlloc and freed on drop.
        let ok =
            unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), SDDL_REVISION_1, &mut descriptor, null_mut()) };
        if ok == 0 {
            return Err(refused("the setup folder's access list"));
        }
        Ok(Descriptor(descriptor))
    }

    fn dacl(&self) -> Result<*mut ACL> {
        let (mut present, mut defaulted): (BOOL, BOOL) = (0, 0);
        let mut dacl: *mut ACL = null_mut();
        // SAFETY: the descriptor is valid while `self` lives; the list points into it.
        let ok = unsafe { GetSecurityDescriptorDacl(self.0, &mut present, &mut dacl, &mut defaulted) };
        if ok == 0 || present == 0 {
            return Err(refused("the setup folder's access list"));
        }
        Ok(dacl)
    }
}

impl Drop for Descriptor {
    fn drop(&mut self) {
        // SAFETY: allocated by ConvertStringSecurityDescriptorToSecurityDescriptorW.
        unsafe { LocalFree(self.0 as HLOCAL) };
    }
}

fn refused(what: &str) -> PlatformError {
    PlatformError::Refused(format!("{what}: {}", std::io::Error::last_os_error()))
}

fn win32(code: u32, what: &str) -> Result<()> {
    if code == 0 { Ok(()) } else { Err(PlatformError::Refused(format!("{what}: {}", std::io::Error::from_raw_os_error(code as i32)))) }
}

fn attributes(path: &Path) -> Option<u32> {
    std::fs::symlink_metadata(path).ok().map(|meta| meta.file_attributes())
}

fn owned_by(path: &Path, user: &User) -> Result<bool> {
    let name = wide(path);
    let mut owner: PSID = null_mut();
    let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
    // SAFETY: `owner` points into `descriptor`, freed after the comparison.
    unsafe {
        win32(
            GetNamedSecurityInfoW(
                name.as_ptr(),
                SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION,
                &mut owner,
                null_mut(),
                null_mut(),
                null_mut(),
                &mut descriptor,
            ),
            "the setup folder's owner",
        )?;
        let same = EqualSid(owner, user.psid()) != 0;
        LocalFree(descriptor as HLOCAL);
        Ok(same)
    }
}

impl SetupFiles for WindowsSetupFiles {
    /// C# `PrepareDirectory`: the parent made, the folder created with the user-only descriptor when nothing is there, then a real
    /// folder this user owns, its owner and protected list written again.
    fn prepare_folder(&self, path: &Path) -> Result<()> {
        if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)?;
        }
        let user = User::current()?;
        let descriptor = Descriptor::user_only(&user)?;
        if attributes(path).is_none() {
            let attributes = SECURITY_ATTRIBUTES {
                nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor.0,
                bInheritHandle: 0,
            };
            let name = wide(path);
            // SAFETY: a NUL-terminated path and a descriptor that lives across the call. A folder made meanwhile is checked below.
            unsafe { CreateDirectoryW(name.as_ptr(), &attributes) };
        }
        match attributes(path) {
            Some(found) if found & FILE_ATTRIBUTE_DIRECTORY != 0 => {
                if found & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
                    return Err(PlatformError::Refused(format!("setup folder is not a plain folder: {}", path.display())));
                }
            }
            _ => return Err(PlatformError::Refused(format!("setup folder is not a folder: {}", path.display()))),
        }
        if !owned_by(path, &user)? {
            return Err(PlatformError::Refused(format!("setup folder belongs to another user: {}", path.display())));
        }
        // Every run puts the protected user-only list back before anything is written, whatever was changed in between.
        let dacl = descriptor.dacl()?;
        let name = wide(path);
        // SAFETY: the SID and the list live across the call.
        let set = unsafe {
            SetNamedSecurityInfoW(
                name.as_ptr(),
                SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                user.psid(),
                null_mut(),
                dacl,
                null(),
            )
        };
        win32(set, "the setup folder's access list")
    }

    /// C# `MarkerExists`: a folder, and not a junction, link or other reparse point.
    fn is_plain_folder(&self, path: &Path) -> bool {
        attributes(path).is_some_and(|found| found & FILE_ATTRIBUTE_DIRECTORY != 0 && found & FILE_ATTRIBUTE_REPARSE_POINT == 0)
    }

    /// C# `RemoveLeftovers`: a real folder removed with what it holds (`remove_dir_all` follows no link inside it), a junction or
    /// directory link removed with RemoveDirectory (only the link goes), anything else deleted; failures skipped.
    fn remove_leftovers(&self, folder: &Path) {
        let Ok(entries) = std::fs::read_dir(folder) else { return };
        for entry in entries.flatten() {
            let path = entry.path();
            let Some(found) = attributes(&path) else { continue };
            let _ = if found & FILE_ATTRIBUTE_DIRECTORY != 0 && found & FILE_ATTRIBUTE_REPARSE_POINT == 0 {
                std::fs::remove_dir_all(&path)
            } else if found & FILE_ATTRIBUTE_DIRECTORY != 0 {
                std::fs::remove_dir(&path)
            } else {
                std::fs::remove_file(&path)
            };
        }
    }

    /// C# `WindowsPrincipal.IsInRole(Administrator)`: the Administrators group is enabled in this process's token (a filtered UAC
    /// token holds it for deny only, which does not count).
    fn is_elevated(&self) -> bool {
        let mut sid = [0u64; 9];
        let mut size = size_of_val(&sid) as u32;
        let mut member: BOOL = 0;
        // SAFETY: a buffer large enough for any SID (68 bytes); CheckTokenMembership with a null token reads the caller's.
        unsafe {
            if CreateWellKnownSid(WinBuiltinAdministratorsSid, null_mut(), sid.as_mut_ptr() as PSID, &mut size) == 0 {
                return false;
            }
            CheckTokenMembership(null_mut(), sid.as_mut_ptr() as PSID, &mut member) != 0 && member != 0
        }
    }

    /// C# `IsPowerShellRunning`: the process is alive and its image is `powershell` (any case), as `Process.ProcessName` reads it.
    fn is_power_shell_running(&self, id: u32) -> bool {
        // SAFETY: the handle is closed before returning; the buffer holds `size` units.
        unsafe {
            let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, id);
            if process.is_null() {
                return false;
            }
            let mut code = 0u32;
            let alive = GetExitCodeProcess(process, &mut code) != 0 && code == STILL_ACTIVE;
            let mut buffer = vec![0u16; 32_768];
            let mut size = buffer.len() as u32;
            let named = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, buffer.as_mut_ptr(), &mut size) != 0;
            CloseHandle(process);
            if !alive || !named {
                return false;
            }
            let image = String::from_utf16_lossy(&buffer[..size as usize]);
            let file = image.rsplit(['\\', '/']).next().unwrap_or("");
            let stem = file.rsplit_once('.').map_or(file, |(stem, _)| stem);
            equal_ignore_case(stem, "powershell")
        }
    }

    fn system_folder(&self) -> Option<String> {
        crate::win::system_directory().map(|folder| folder.to_string_lossy().into_owned()).filter(|folder| !folder.is_empty())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The folder's owner and access list as Windows reports them back, in SDDL.
    fn security_of(path: &Path) -> String {
        use windows_sys::Win32::Security::Authorization::ConvertSecurityDescriptorToStringSecurityDescriptorW;
        let name = wide(path);
        let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
        // SAFETY: the descriptor and the text are LocalAlloc'd by Windows and freed here.
        unsafe {
            let read = GetNamedSecurityInfoW(
                name.as_ptr(),
                SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                null_mut(),
                null_mut(),
                null_mut(),
                null_mut(),
                &mut descriptor,
            );
            assert_eq!(read, 0, "the folder's security reads");
            let mut text: *mut u16 = null_mut();
            let converted = ConvertSecurityDescriptorToStringSecurityDescriptorW(
                descriptor,
                SDDL_REVISION_1,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                &mut text,
                null_mut(),
            );
            assert_ne!(converted, 0, "the folder's security converts");
            let length = (0..).take_while(|&at| *text.add(at) != 0).count();
            let sddl = String::from_utf16_lossy(std::slice::from_raw_parts(text, length));
            LocalFree(text as HLOCAL);
            LocalFree(descriptor as HLOCAL);
            sddl
        }
    }

    /// An owner-and-list SDDL text as Windows writes it back.
    fn canonical(sddl: &str) -> String {
        use windows_sys::Win32::Security::Authorization::ConvertSecurityDescriptorToStringSecurityDescriptorW;
        let text = wide(sddl);
        let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
        // SAFETY: a NUL-terminated SDDL string; the descriptor and the text are LocalAlloc'd by Windows and freed here.
        unsafe {
            assert_ne!(
                ConvertStringSecurityDescriptorToSecurityDescriptorW(text.as_ptr(), SDDL_REVISION_1, &mut descriptor, null_mut()),
                0
            );
            let mut out: *mut u16 = null_mut();
            let converted = ConvertSecurityDescriptorToStringSecurityDescriptorW(
                descriptor,
                SDDL_REVISION_1,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                &mut out,
                null_mut(),
            );
            assert_ne!(converted, 0);
            let length = (0..).take_while(|&at| *out.add(at) != 0).count();
            let canonical = String::from_utf16_lossy(std::slice::from_raw_parts(out, length));
            LocalFree(out as HLOCAL);
            LocalFree(descriptor as HLOCAL);
            canonical
        }
    }

    /// C# `PrepareDirectory`'s folder read back from Windows: this user its owner, a protected list (nothing inherited) of one entry,
    /// full control for this user inherited by folders and files — made new, and put back after the list was widened.
    #[test]
    fn the_setup_folder_is_this_users_alone_as_windows_reads_it_back() {
        let root = std::env::temp_dir().join(format!("mighty-platform-windows-setup-folder-acl-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let folder = root.join("setup");
        WindowsSetupFiles.prepare_folder(&folder).expect("the folder is prepared");
        let user = User::current().unwrap();
        // Protected (P; Windows adds AI, the list written with auto-inheritance), one entry: full control for this user. The expected
        // text goes through Windows once too, so a SID it writes as an alias (LA for the built-in Administrator CI runs as) compares.
        let expected =
            [canonical(&format!("O:{0}D:P(A;OICI;FA;;;{0})", user.text)), canonical(&format!("O:{0}D:PAI(A;OICI;FA;;;{0})", user.text))];
        let user_only = |sddl: String| expected.contains(&sddl);
        assert!(user_only(security_of(&folder)), "{}", security_of(&folder));
        // Widened in between (everyone may read): the next run puts the user-only list back.
        let everyone = wide(format!("D:(A;OICI;FA;;;{0})(A;OICI;FR;;;WD)", user.text));
        let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
        // SAFETY: a NUL-terminated SDDL string; the descriptor is freed by Descriptor's drop.
        let made = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(everyone.as_ptr(), SDDL_REVISION_1, &mut descriptor, null_mut())
        };
        assert_ne!(made, 0);
        let wide_list = Descriptor(descriptor);
        let name = wide(&folder);
        // SAFETY: the list lives in `wide_list` across the call.
        let set = unsafe {
            SetNamedSecurityInfoW(
                name.as_ptr(),
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION,
                null_mut(),
                null_mut(),
                wide_list.dacl().unwrap(),
                null(),
            )
        };
        assert_eq!(set, 0);
        assert!(!user_only(security_of(&folder)), "the list was widened");
        WindowsSetupFiles.prepare_folder(&folder).expect("prepared again");
        assert!(user_only(security_of(&folder)), "the user-only list is back: {}", security_of(&folder));
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn this_process_is_no_powershell_and_a_dead_id_runs_nothing() {
        assert!(!WindowsSetupFiles.is_power_shell_running(std::process::id()));
        assert!(!WindowsSetupFiles.is_power_shell_running(0));
    }
}
