//! The inbound rule a Tailscale direct connection needs on Windows (C# `WindowsFirewallRule`, `WindowsFirewallRuleRunner`):
//! one rule named for the app, allowing this executable on the given TCP ports. The Mac has no such step
//! ([`crate::PlatformProfile::direct_connection_firewall`] is false) and answers `Unsupported`.

use crate::{BoxFuture, Result};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FirewallRule {
    Missing,
    /// The ports the rule allows now.
    Present {
        ports: Vec<u16>,
    },
    /// `netsh` answered something the reader does not understand.
    Unreadable,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FirewallChange {
    Applied,
    /// The user said no to the elevation prompt.
    Declined,
    /// The elevated command ran and failed (its words, never a path).
    Failed(String),
}

/// Contract: `read` needs no elevation; `apply` makes the rule allow exactly `ports` (each 1024–65535) for this executable and asks
/// for elevation once (Windows: UAC); an empty list removes the rule.
pub trait Firewall: Send + Sync {
    fn read(&self) -> BoxFuture<'_, Result<FirewallRule>>;
    fn apply(&self, ports: Vec<u16>) -> BoxFuture<'_, Result<FirewallChange>>;
}
