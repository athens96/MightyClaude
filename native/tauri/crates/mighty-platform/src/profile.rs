//! The behaviours that differ between the Windows and the Mac app on purpose (.omc/research/tauri/mac-gaps.md §2), as named
//! values the shared logic reads instead of asking which OS it runs on. Each value says what the 0.4.x app of that OS does today;
//! changing one changes what users of that OS see.

/// The OS a build runs on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Os {
    Windows,
    Macos,
}

impl Os {
    /// The name the page's `host` push and the logs use.
    pub fn name(self) -> &'static str {
        match self {
            Os::Windows => "windows",
            Os::Macos => "macos",
        }
    }
}

/// How absolute paths look: drive letters and UNC shares, or one root at `/`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PathStyle {
    Windows,
    Posix,
}

/// What a request for the plugin list gets while a read of it is already running.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PluginListWhileReading {
    /// The Mac answers `busy` at once.
    Busy,
    /// Windows waits for the running read and answers with its result.
    Join,
}

/// Which package manager updates the agent CLIs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CliUpdater {
    Homebrew,
    Winget,
}

/// Where the app's own settings live (beside the state file's fields).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingsStore {
    /// The Mac: UserDefaults `dev.mightycode.native` (`appUpdate.*`, `delegation.enabled`, `setup.neverShow`, ...).
    UserDefaults,
    /// Windows: `HKCU\Software\MightyCode` values for some, snapshot fields for others (`AppUpdateAutoCheck`, `AppUpdateLastCheckedAt`).
    RegistryAndSnapshot,
}

/// How the phone pairing identity is kept on disk.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PairingLayout {
    /// Windows: `mobile-identity.json`, the key, tokens and devices together.
    IdentityFile,
    /// The Mac: `mobile-remote.key`, `relay-keypair.json`, `relay-host-token.json`, `devices.json`.
    FourFiles,
}

/// Where a CLI's interactive sign-in (Antigravity) runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AgentSignIn {
    /// The Mac: a terminal pane in the window.
    TerminalPane,
    /// Windows: a console window of its own.
    ConsoleWindow,
}

/// What `--agent-io-mcp` listens on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum McpTransport {
    UnixSocket,
    NamedPipe,
}

/// The deliberate differences, one value per behaviour.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlatformProfile {
    pub os: Os,
    pub path_style: PathStyle,
    pub plugin_list_while_reading: PluginListWhileReading,
    pub cli_updater: CliUpdater,
    pub settings_store: SettingsStore,
    pub pairing_layout: PairingLayout,
    pub agent_sign_in: AgentSignIn,
    pub mcp_transport: McpTransport,
    /// The `platform` field of the phone protocol's `/m1/info` ("win32" on Windows, "darwin" on the Mac).
    pub m1_platform: &'static str,
    /// The remote workspace host percent-decodes request paths (the Mac does, Windows does not).
    pub remote_decodes_request_paths: bool,
    /// A Tailscale direct connection needs an inbound firewall rule first (Windows: `netsh` with elevation; the Mac has no step).
    pub direct_connection_firewall: bool,
    /// The app update check is off when the build carries no public key (Windows; the Mac checks and refuses at verification).
    pub update_needs_public_key: bool,
    /// A manifest address stamped into the build wins over one the user set (Windows).
    pub build_update_url_wins: bool,
    /// The staged package's SHA-256 is checked again just before the swap (Windows' `--update-helper`).
    pub update_rechecks_sha256: bool,
    /// The snapshot carries `autoUpdateCLIs` and `autoUpdatePlugins` (the Mac only).
    pub auto_update_keys: bool,
    /// A files pane is saved with the state (Windows; the Mac never saves one).
    pub saves_files_pane: bool,
}

impl PlatformProfile {
    /// What the Windows app (0.4.x C#) does.
    pub const fn windows() -> PlatformProfile {
        PlatformProfile {
            os: Os::Windows,
            path_style: PathStyle::Windows,
            plugin_list_while_reading: PluginListWhileReading::Join,
            cli_updater: CliUpdater::Winget,
            settings_store: SettingsStore::RegistryAndSnapshot,
            pairing_layout: PairingLayout::IdentityFile,
            agent_sign_in: AgentSignIn::ConsoleWindow,
            mcp_transport: McpTransport::NamedPipe,
            m1_platform: "win32",
            remote_decodes_request_paths: false,
            direct_connection_firewall: true,
            update_needs_public_key: true,
            build_update_url_wins: true,
            update_rechecks_sha256: true,
            auto_update_keys: false,
            saves_files_pane: true,
        }
    }

    /// What the Mac app (0.4.x Swift) does.
    pub const fn macos() -> PlatformProfile {
        PlatformProfile {
            os: Os::Macos,
            path_style: PathStyle::Posix,
            plugin_list_while_reading: PluginListWhileReading::Busy,
            cli_updater: CliUpdater::Homebrew,
            settings_store: SettingsStore::UserDefaults,
            pairing_layout: PairingLayout::FourFiles,
            agent_sign_in: AgentSignIn::TerminalPane,
            mcp_transport: McpTransport::UnixSocket,
            m1_platform: "darwin",
            remote_decodes_request_paths: true,
            direct_connection_firewall: false,
            update_needs_public_key: false,
            build_update_url_wins: false,
            update_rechecks_sha256: false,
            auto_update_keys: true,
            saves_files_pane: false,
        }
    }

    pub const fn for_os(os: Os) -> PlatformProfile {
        match os {
            Os::Windows => PlatformProfile::windows(),
            Os::Macos => PlatformProfile::macos(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_os_keeps_its_own_behaviour() {
        let windows = PlatformProfile::windows();
        let mac = PlatformProfile::macos();
        assert_eq!(windows.plugin_list_while_reading, PluginListWhileReading::Join);
        assert_eq!(mac.plugin_list_while_reading, PluginListWhileReading::Busy);
        assert_eq!(windows.cli_updater, CliUpdater::Winget);
        assert_eq!(mac.cli_updater, CliUpdater::Homebrew);
        assert_eq!((windows.m1_platform, mac.m1_platform), ("win32", "darwin"));
        assert!(windows.direct_connection_firewall && !mac.direct_connection_firewall);
        assert!(!windows.remote_decodes_request_paths && mac.remote_decodes_request_paths);
        assert!(mac.auto_update_keys && !windows.auto_update_keys);
        assert_eq!(PlatformProfile::for_os(Os::Macos), mac);
        assert_eq!(Os::Windows.name(), "windows");
    }
}
