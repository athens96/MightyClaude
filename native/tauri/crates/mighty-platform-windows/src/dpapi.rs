//! C# `DpapiRemoteKeyVault`, byte for byte: one file per id, `<data>\remote-keys\<sha256(id) lower hex>.bin`, holding
//! `CryptProtectData` of the secret's UTF-8 bytes for the current user (no `CRYPTPROTECT_LOCAL_MACHINE`, `CRYPTPROTECT_UI_FORBIDDEN`)
//! with the description `MightyCode remote key v2` and the entropy `MightyCode remote key v2` NUL `<id>` (UTF-8), so a blob another
//! program protected for this user, or one copied from another id's file, does not decrypt. Files the C# app wrote load here and
//! the other way round.
//!
//! The folder is a plain folder this user owns, its access list this user alone (protected, inherited by files); it is set before
//! every write, as C# does. A file that is empty, over 16 KiB, or does not decrypt to UTF-8 reads as no secret and stays in place.

use std::ffi::c_void;
use std::fs;
use std::io::Read;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::ptr::{null, null_mut};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use mighty_platform::{PlatformError, Result, SecretVault};
use sha2::{Digest, Sha256};
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HLOCAL, LocalFree};
use windows_sys::Win32::Security::Authorization::{
    EXPLICIT_ACCESS_W, GetNamedSecurityInfoW, NO_MULTIPLE_TRUSTEE, SE_FILE_OBJECT, SET_ACCESS, SetEntriesInAclW, SetNamedSecurityInfoW,
    TRUSTEE_IS_SID, TRUSTEE_IS_USER, TRUSTEE_W,
};
use windows_sys::Win32::Security::Cryptography::{CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData};
use windows_sys::Win32::Security::{
    ACL, CopySid, DACL_SECURITY_INFORMATION, EqualSid, GetLengthSid, GetTokenInformation, OWNER_SECURITY_INFORMATION,
    PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID, SUB_CONTAINERS_AND_OBJECTS_INHERIT, TOKEN_QUERY, TOKEN_USER,
    TokenUser,
};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

pub const FOLDER: &str = "remote-keys";
/// Larger than any blob DPAPI makes for a 128-character key; a bigger file is not ours.
const MAXIMUM_BLOB: usize = 16_384;
const PURPOSE: &str = "MightyCode remote key v2";
/// .NET's `FileSystemRights.FullControl`.
const FULL_CONTROL: u32 = 0x001F_01FF;
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
const DELETE_ATTEMPTS: u32 = 10;

/// Secrets under `<data>\remote-keys`.
pub struct DpapiVault {
    directory: PathBuf,
}

/// The type name only: the vault holds no secret, and its folder is not worth a log line.
impl std::fmt::Debug for DpapiVault {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("DpapiVault")
    }
}

/// sha256(id) in lower-case hex, `.bin`: the name says nothing about the id.
pub fn file_name(id: &str) -> String {
    let digest = Sha256::digest(id.as_bytes());
    let mut name: String = digest.iter().map(|byte| format!("{byte:02x}")).collect();
    name.push_str(".bin");
    name
}

/// The DPAPI entropy for one id: the purpose label, a NUL, the id.
pub fn entropy(id: &str) -> Vec<u8> {
    format!("{PURPOSE}\0{id}").into_bytes()
}

impl DpapiVault {
    /// `directory` is the data folder; the keys go in its `remote-keys`.
    pub fn new(directory: PathBuf) -> DpapiVault {
        DpapiVault { directory }
    }

    fn folder(&self) -> PathBuf {
        self.directory.join(FOLDER)
    }

    fn file(&self, id: &str) -> PathBuf {
        self.folder().join(file_name(id))
    }

    /// The folder exists, is a plain folder this user owns, and only this user may open it (C# `PrepareFolder`).
    fn prepare_folder(&self) -> Result<PathBuf> {
        fs::create_dir_all(&self.directory)?;
        let folder = self.folder();
        let user = Sid::current_user()?;
        match fs::create_dir(&folder) {
            // A new folder gets this user as its owner and the user-only list before anything is written in it.
            Ok(()) => user_only(&folder, &user)?,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(error) => return Err(error.into()),
        }
        let metadata = fs::symlink_metadata(&folder)?;
        if !metadata.is_dir() || metadata.file_type().is_symlink() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(PlatformError::Refused("the key folder is not a plain folder".into()));
        }
        if !owned_by(&folder, &user)? {
            return Err(PlatformError::Refused("the key folder belongs to another user".into()));
        }
        user_only(&folder, &user)?;
        Ok(folder)
    }
}

impl SecretVault for DpapiVault {
    fn load(&self, id: &str) -> Option<String> {
        let blob = read_bounded(&self.file(id))?;
        let mut plain = transform(&blob, &entropy(id), Direction::Decrypt).ok()?;
        let text = std::str::from_utf8(&plain).ok().map(str::to_string);
        wipe(&mut plain);
        text
    }

    fn save(&self, id: &str, secret: &str) -> Result<()> {
        if secret.is_empty() {
            return Err(PlatformError::Invalid("an empty secret".into()));
        }
        let folder = self.prepare_folder()?;
        let blob = transform(secret.as_bytes(), &entropy(id), Direction::Encrypt)?;
        atomic_write(&folder.join(file_name(id)), &blob)
    }

    /// A file held open by another program (an antivirus scan, a backup) is tried again for about a second.
    fn delete(&self, id: &str) -> Result<()> {
        let path = self.file(id);
        let mut attempt = 1;
        loop {
            match fs::remove_file(&path) {
                Ok(()) => return Ok(()),
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
                Err(_) if attempt < DELETE_ATTEMPTS => {
                    attempt += 1;
                    std::thread::sleep(Duration::from_millis(100));
                }
                Err(error) => return Err(error.into()),
            }
        }
    }
}

fn read_bounded(path: &Path) -> Option<Vec<u8>> {
    let file = fs::File::open(path).ok()?;
    let mut blob = Vec::new();
    file.take(MAXIMUM_BLOB as u64 + 1).read_to_end(&mut blob).ok()?;
    (!blob.is_empty() && blob.len() <= MAXIMUM_BLOB).then_some(blob)
}

/// Writes `bytes` beside `path` and renames it over, so a reader sees the old file or the new one, never half of one.
fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    let temporary = path.with_extension(format!("bin.{}-{nanos}.tmp", std::process::id()));
    let written = (|| {
        use std::io::Write;
        let mut file = fs::File::create(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, path)
    })();
    if written.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    Ok(written?)
}

fn wipe(bytes: &mut [u8]) {
    for byte in bytes.iter_mut() {
        // Volatile so the compiler cannot drop the wipe of a buffer about to be freed.
        unsafe { std::ptr::write_volatile(byte, 0) };
    }
}

fn wide(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
}

#[derive(Clone, Copy)]
enum Direction {
    Encrypt,
    Decrypt,
}

/// `CryptProtectData` / `CryptUnprotectData` for the current user, never with UI. DPAPI's output is wiped before it is freed.
fn transform(value: &[u8], entropy: &[u8], direction: Direction) -> Result<Vec<u8>> {
    if value.is_empty() {
        return Err(PlatformError::Invalid("DPAPI input is empty".into()));
    }
    let input = CRYPT_INTEGER_BLOB { cbData: value.len() as u32, pbData: value.as_ptr() as *mut u8 };
    let extra = CRYPT_INTEGER_BLOB { cbData: entropy.len() as u32, pbData: entropy.as_ptr() as *mut u8 };
    let mut output = CRYPT_INTEGER_BLOB { cbData: 0, pbData: null_mut() };
    let description: Vec<u16> = PURPOSE.encode_utf16().chain(std::iter::once(0)).collect();
    // SAFETY: every pointer is valid for the call; DPAPI only reads `input` and `extra` and allocates `output` with LocalAlloc.
    let ok = unsafe {
        match direction {
            Direction::Encrypt => {
                CryptProtectData(&input, description.as_ptr(), &extra, null(), null(), CRYPTPROTECT_UI_FORBIDDEN, &mut output)
            }
            Direction::Decrypt => CryptUnprotectData(&input, null_mut(), &extra, null(), null(), CRYPTPROTECT_UI_FORBIDDEN, &mut output),
        }
    };
    if ok == 0 {
        return Err(PlatformError::Refused(format!("DPAPI: {}", std::io::Error::last_os_error())));
    }
    let size = output.cbData as usize;
    // SAFETY: DPAPI returned `size` bytes at `pbData`, which this function owns until LocalFree.
    let bytes = unsafe {
        let data = std::slice::from_raw_parts_mut(output.pbData, size);
        let copy = (1..=MAXIMUM_BLOB).contains(&size).then(|| data.to_vec());
        wipe(data);
        LocalFree(output.pbData as HLOCAL);
        copy
    };
    bytes.ok_or(PlatformError::Refused("DPAPI output size".into()))
}

/// A copy of a SID.
struct Sid(Vec<u8>);

impl Sid {
    fn current_user() -> Result<Sid> {
        let mut token: HANDLE = null_mut();
        // SAFETY: the token handle is closed below; the buffer is u64-aligned for TOKEN_USER and sized by the first call.
        unsafe {
            if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
                return Err(PlatformError::Refused(format!("no user identity: {}", std::io::Error::last_os_error())));
            }
            let mut length = 0u32;
            GetTokenInformation(token, TokenUser, null_mut(), 0, &mut length);
            let mut buffer = vec![0u64; (length as usize).div_ceil(8)];
            let read = GetTokenInformation(token, TokenUser, buffer.as_mut_ptr() as *mut c_void, length, &mut length);
            CloseHandle(token);
            if read == 0 {
                return Err(PlatformError::Refused(format!("no user identity: {}", std::io::Error::last_os_error())));
            }
            let user = &*(buffer.as_ptr() as *const TOKEN_USER);
            let size = GetLengthSid(user.User.Sid);
            let mut sid = vec![0u8; size as usize];
            if CopySid(size, sid.as_mut_ptr() as PSID, user.User.Sid) == 0 {
                return Err(PlatformError::Refused("the user's SID could not be copied".into()));
            }
            Ok(Sid(sid))
        }
    }

    fn as_psid(&self) -> PSID {
        self.0.as_ptr() as PSID
    }
}

fn win32(code: u32, what: &str) -> Result<()> {
    if code == 0 { Ok(()) } else { Err(PlatformError::Refused(format!("{what}: {}", std::io::Error::from_raw_os_error(code as i32)))) }
}

/// Owner this user, the access list this user alone with full control, protected from inheritance (C# `UserOnly`).
fn user_only(folder: &Path, user: &Sid) -> Result<()> {
    let access = EXPLICIT_ACCESS_W {
        grfAccessPermissions: FULL_CONTROL,
        grfAccessMode: SET_ACCESS,
        grfInheritance: SUB_CONTAINERS_AND_OBJECTS_INHERIT,
        Trustee: TRUSTEE_W {
            pMultipleTrustee: null_mut(),
            MultipleTrusteeOperation: NO_MULTIPLE_TRUSTEE,
            TrusteeForm: TRUSTEE_IS_SID,
            TrusteeType: TRUSTEE_IS_USER,
            ptstrName: user.as_psid() as *mut u16,
        },
    };
    let mut acl: *mut ACL = null_mut();
    // SAFETY: `access` names a SID that outlives both calls; the ACL SetEntriesInAclW allocates is freed with LocalFree.
    unsafe {
        win32(SetEntriesInAclW(1, &access, null(), &mut acl), "the key folder's access list")?;
        let path = wide(folder);
        let set = SetNamedSecurityInfoW(
            path.as_ptr(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            user.as_psid(),
            null_mut(),
            acl,
            null(),
        );
        LocalFree(acl as HLOCAL);
        win32(set, "the key folder's access list")
    }
}

fn owned_by(folder: &Path, user: &Sid) -> Result<bool> {
    let path = wide(folder);
    let mut owner: PSID = null_mut();
    let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
    // SAFETY: `owner` points into `descriptor`, which is freed after the comparison.
    unsafe {
        win32(
            GetNamedSecurityInfoW(
                path.as_ptr(),
                SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION,
                &mut owner,
                null_mut(),
                null_mut(),
                null_mut(),
                &mut descriptor,
            ),
            "the key folder's owner",
        )?;
        let same = EqualSid(owner, user.as_psid()) != 0;
        LocalFree(descriptor as HLOCAL);
        Ok(same)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temporary(name: &str) -> PathBuf {
        let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        std::env::temp_dir().join(format!("mighty-dpapi-{name}-{}-{nanos}", std::process::id()))
    }

    #[test]
    fn names_and_entropy_are_the_csharp_ones() {
        // sha256("connection-1") as .NET's SHA256 computes it (C# Convert.ToHexStringLower(SHA256.HashData(UTF8(id)))).
        assert_eq!(file_name("connection-1"), "3f8cb879cc51cc8c920e5c2333858a5407b2d6c8f2a1b7761c5eb6620f815d7b.bin");
        assert_eq!(entropy("c1"), b"MightyCode remote key v2\0c1");
    }

    #[test]
    fn a_secret_comes_back_only_for_its_own_id() {
        let directory = temporary("roundtrip");
        let vault = DpapiVault::new(directory.clone());
        assert_eq!(vault.load("a"), None);
        vault.save("a", "key-for-a").unwrap();
        assert_eq!(vault.load("a").as_deref(), Some("key-for-a"));
        assert!(owned_by(&directory.join(FOLDER), &Sid::current_user().unwrap()).unwrap());

        // A blob copied to another id's file does not decrypt there (the entropy names the id).
        fs::copy(directory.join(FOLDER).join(file_name("a")), directory.join(FOLDER).join(file_name("b"))).unwrap();
        assert_eq!(vault.load("b"), None);
        // Garbage, an empty file and one too large read as no secret and stay in place.
        fs::write(directory.join(FOLDER).join(file_name("c")), b"not dpapi").unwrap();
        assert_eq!(vault.load("c"), None);
        assert!(directory.join(FOLDER).join(file_name("c")).exists());
        fs::write(directory.join(FOLDER).join(file_name("d")), vec![1u8; MAXIMUM_BLOB + 1]).unwrap();
        assert_eq!(vault.load("d"), None);

        vault.save("a", "replaced").unwrap();
        assert_eq!(vault.load("a").as_deref(), Some("replaced"));
        vault.delete("a").unwrap();
        vault.delete("a").unwrap();
        assert_eq!(vault.load("a"), None);
        assert!(vault.save("e", "").is_err());
        let _ = fs::remove_dir_all(directory);
    }
}
