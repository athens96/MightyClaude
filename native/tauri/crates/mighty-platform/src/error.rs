use std::fmt;

/// Why a port could not do what it was asked. No message ever carries a secret, a key or an environment value.
#[derive(Debug)]
pub enum PlatformError {
    /// This OS has no implementation of the operation: not written yet (a stub that returns this until its stage), or never (the
    /// Mac has no firewall step).
    Unsupported {
        port: &'static str,
        operation: &'static str,
    },
    /// The OS said no: DPAPI or the Keychain refused a secret, the registry denied access, the user declined elevation.
    Refused(String),
    /// An argument the port cannot take (a relative working directory, a NUL in an environment value, a page address).
    Invalid(String),
    /// The operation did not end in time (a capture past its timeout, descendants still alive after a close).
    TimedOut(&'static str),
    /// Output larger than the caller allowed.
    TooLarge(usize),
    Io(std::io::Error),
}

pub type Result<T> = std::result::Result<T, PlatformError>;

impl PlatformError {
    pub fn unsupported(port: &'static str, operation: &'static str) -> PlatformError {
        PlatformError::Unsupported { port, operation }
    }

    pub fn is_unsupported(&self) -> bool {
        matches!(self, PlatformError::Unsupported { .. })
    }
}

impl fmt::Display for PlatformError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            PlatformError::Unsupported { port, operation } => write!(f, "unsupported on this platform: {port}.{operation}"),
            PlatformError::Refused(reason) => write!(f, "refused: {reason}"),
            PlatformError::Invalid(reason) => write!(f, "invalid: {reason}"),
            PlatformError::TimedOut(what) => write!(f, "timed out: {what}"),
            PlatformError::TooLarge(limit) => write!(f, "larger than {limit} bytes"),
            PlatformError::Io(error) => write!(f, "{error}"),
        }
    }
}

impl std::error::Error for PlatformError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            PlatformError::Io(error) => Some(error),
            _ => None,
        }
    }
}

impl From<std::io::Error> for PlatformError {
    fn from(error: std::io::Error) -> Self {
        PlatformError::Io(error)
    }
}
