//! The environment the agent CLIs start with (C# `WindowsShellEnvironment`, Swift `CLIEnvironmentResolver`): what the user's
//! terminal would have, so a CLI finds the same tools and credentials there and here.

use std::collections::BTreeMap;
use std::fmt;
use std::path::Path;

use crate::BoxFuture;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnvironmentSource {
    /// Captured from the user's shell profile (Windows: PowerShell's ConsoleHost profiles; the Mac: the login shell, `-ilc`).
    Profile,
    /// The capture failed, timed out or was malformed: this process's own environment.
    ProcessFallback,
    /// Given by the caller (tests).
    Provided,
}

/// One resolved environment. Values can be credentials: `Debug` shows the source and the count only, and nothing logs them.
#[derive(Clone, PartialEq, Eq)]
pub struct CliEnvironment {
    /// Windows implementations keep one entry per name ignoring case.
    pub values: BTreeMap<String, String>,
    pub source: EnvironmentSource,
}

impl fmt::Debug for CliEnvironment {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "CliEnvironment({:?}, {} names)", self.source, self.values.len())
    }
}

/// Resolves the CLI environment.
///
/// Contract: a resolve never fails — a prompt, a broken profile or a malformed capture falls back to the process environment after
/// a bounded wait (4 s); a result is reused for 10 s per workspace unless `force`; a forced resolve that fails discards the previous
/// capture rather than let it outlive a changed profile; neither output stream of the capture is logged. The capture runs in the
/// user's home (never an untrusted project folder) and puts no value in its arguments.
pub trait ShellEnvironment: Send + Sync {
    fn resolve(&self, workspace: Option<&Path>, force: bool) -> BoxFuture<'_, CliEnvironment>;
    /// The last resolved environment without waiting (synchronous callers such as a terminal starting), else the process's.
    fn current(&self) -> CliEnvironment;
    /// Forgets the cached capture for `workspace` (None: the home folder's).
    fn invalidate(&self, workspace: Option<&Path>);
}
