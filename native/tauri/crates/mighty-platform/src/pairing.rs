//! The phone pairing identity (C# `MobileIdentity`, Swift `MobileRemoteService` + `MobileDeviceRegistry`): the host's id and token,
//! the pairing key, the relay secret key and the paired phones. The shapes are the m1 protocol's; only the files differ by OS
//! ([`crate::PairingLayout`]).

use std::fmt;

use crate::Result;

/// Where and what a phone asked to be pushed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PushRegistration {
    pub handle: String,
    pub kinds: Vec<String>,
    pub updated_at: String,
}

#[derive(Clone, PartialEq, Eq)]
pub struct PairedDevice {
    /// A 22-character client id, or `legacy` (every app without a device token).
    pub id: String,
    pub name: String,
    /// SHA-256 of the device token, lower-case hex; None for the legacy row.
    pub token_hash: Option<String>,
    pub first_seen: String,
    pub last_seen: String,
    pub push: Option<PushRegistration>,
}

impl fmt::Debug for PairedDevice {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("PairedDevice").field("id", &self.id).field("name", &self.name).finish_non_exhaustive()
    }
}

#[derive(Clone, PartialEq, Eq)]
pub struct PairingIdentity {
    pub host_id: String,
    /// 64 lower-case hex characters.
    pub host_token: String,
    pub pairing_key: String,
    /// The relay's X25519 secret key.
    pub secret_key: [u8; 32],
    pub devices: Vec<PairedDevice>,
}

/// Secrets never reach a log.
impl fmt::Debug for PairingIdentity {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("PairingIdentity").field("host_id", &self.host_id).field("devices", &self.devices).finish_non_exhaustive()
    }
}

/// Contract: `load` is `Ok(None)` when nothing was paired yet and `Err` when the files exist but are damaged, foreign or too large
/// (the caller decides whether to start over). `save` commits the identity and the device list together, so a failed key rotation
/// cannot leave a new key with an old device list (Windows: one file written atomically; the Mac: its four files, each atomically).
/// The folder is this user's only.
pub trait PairingStore: Send + Sync {
    fn load(&self) -> Result<Option<PairingIdentity>>;
    fn save(&self, identity: &PairingIdentity) -> Result<()>;
}
