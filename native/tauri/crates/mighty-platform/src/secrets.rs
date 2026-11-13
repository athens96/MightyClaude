//! Secrets kept for this user on this computer: the connection keys of saved remote connections (C# `IRemoteKeyVault` /
//! `DpapiRemoteKeyVault`, Swift `RemoteKeychain`). A secret never goes into the workspace state or the connections file.
//!
//! | | Windows | Mac |
//! |---|---|---|
//! | where | `<data>\remote-keys\<sha256(id) hex>.bin`, DPAPI for the current user | Keychain, service `dev.mightycode.native.remote` (legacy `dev.mightyclaude.native.remote`, copied on first read), account = id |
//! | who can read | this user on this PC (folder ACL: this user only) | this Mac once unlocked (`AfterFirstUnlockThisDeviceOnly`) |

use crate::Result;

/// Contract:
/// - `load` answers None when there is no secret **or** it cannot be read back (a damaged or foreign file, a refusal that may pass):
///   such a file stays where it is, and the next `save` replaces it. Whether the text has the protocol's shape is the caller's check.
/// - `save` replaces the secret atomically; `Err` when it could not be kept beyond this launch.
/// - `delete` of a missing secret succeeds.
/// - A secret is read when first needed (a connection's first use), never at launch, so the Keychain asks only then.
/// - Calls may block (a Keychain prompt): call them off the UI thread.
/// - No error, log line or `Debug` output carries a secret or the id's file path.
pub trait SecretVault: Send + Sync {
    fn load(&self, id: &str) -> Option<String>;
    fn save(&self, id: &str, secret: &str) -> Result<()>;
    fn delete(&self, id: &str) -> Result<()>;
}
