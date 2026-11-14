//! Mighty Code's CLI accounts, login recovery and account usage (W6-C), with no Tauri dependency and no OS code: the accounts and
//! their coordinator (`accounts`), sign-in in a terminal (`terminal`), login recovery (`login`), account usage (`usage`), the
//! Bedrock reset (`bedrock`) and their forms (`view`). What mighty-run already reads of the accounts is reused from it.
//!
//! | C# | here |
//! |---|---|
//! | `CliAccountStatus`, `CliAccountMethod`, `CliAccountSupport`, `CliAccountsCoordinator`, `CliAccountSmoke`, `CliAccountStrings`, `PiAccounts` (and `PiAgent`'s credentials) | [`accounts`] (the status record and opencode's and Hermes's readers are mighty-run's `acp::accounts`) |
//! | `CliAccountTerminal`, `CliTerminalSignIn`, `CliTerminalLogin`, `CliLoginWait` | [`terminal`] |
//! | `CliAuthFailure.Providers` / `SignInCanFix`, `CliAutoLoginGate`, `CliLoginOutputParser`, `CliLoginRetryBook`, `CliBackgroundLogin` | [`login`] (the failure signals are mighty-run's `failure`) |
//! | `AccountUsage*`, `ClaudeCredentialFile`, the three probes, `AccountUsageService`, `AccountUsageStatus`, `ClaudeResetEntitlements`, `ClaudeResetSmoke`, `AccountUsageRuntime` | [`usage`] |
//! | `BedrockSettings` (its `RuntimeFailureGuidance` is mighty-run's `failure::bedrock_guidance`) | [`bedrock`] |
//!
//! The OS reaches this crate only through ports: children through `mighty_platform::ProcessHost` (and [`accounts::HostRunner`]),
//! the hidden sign-in through `mighty_platform::PtyHost`, and two ports W6-A's `mighty_platform` does not have yet, defined here as
//! the least this crate needs: the terminal window that outlives the app ([`terminal::TerminalLauncher`]) and the HTTPS GET
//! ([`usage::HttpsGet`]). Anything that changes the machine or the user's CLI configuration (a sign-in, a sign-out, the Bedrock
//! reset) is a call the host makes, so the host gates it (`--allow-state-write`).

pub mod accounts;
pub mod bedrock;
pub mod login;
pub mod terminal;
pub mod usage;
pub mod view;

/// A copy class of C#'s (`CliAccountStrings`, `AccountUsageStrings`): one function per field, each reading its locale key in the
/// language the host chose, and `all()`, every field by its C# name (what the strings check reads through reflection).
macro_rules! copy {
    ($(#[$meta:meta])* $vis:vis mod $module:ident { $($(#[$doc:meta])* $name:ident ($field:literal) = $key:literal;)* }) => {
        $(#[$meta])*
        $vis mod $module {
            $(
                $(#[$doc])*
                pub fn $name() -> String {
                    mighty_run::strings::get($key)
                }
            )*
            /// Every field by its C# name, with its text now.
            pub fn all() -> Vec<(&'static str, String)> {
                vec![$(($field, $name())),*]
            }
        }
    };
}
pub(crate) use copy;

/// Polls every future at once and gives their outputs in order (C#'s `Task.WhenAll` over the reads it started together).
pub(crate) async fn join_all<'a, T>(futures: Vec<mighty_platform::BoxFuture<'a, T>>) -> Vec<T> {
    let mut slots: Vec<Option<mighty_platform::BoxFuture<'a, T>>> = futures.into_iter().map(Some).collect();
    let mut outputs: Vec<Option<T>> = slots.iter().map(|_| None).collect();
    std::future::poll_fn(|context| {
        let mut pending = false;
        for (slot, output) in slots.iter_mut().zip(outputs.iter_mut()) {
            if let Some(future) = slot {
                match future.as_mut().poll(context) {
                    std::task::Poll::Ready(value) => {
                        *output = Some(value);
                        *slot = None;
                    }
                    std::task::Poll::Pending => pending = true,
                }
            }
        }
        if pending { std::task::Poll::Pending } else { std::task::Poll::Ready(()) }
    })
    .await;
    outputs.into_iter().flatten().collect()
}

pub(crate) fn lock<T>(mutex: &std::sync::Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// A name of the CLI environment: exact first, then ignoring case where names are Windows' (C#'s `OrdinalIgnoreCase` dictionaries).
pub(crate) fn env_get<'a>(environment: &'a std::collections::BTreeMap<String, String>, name: &str) -> Option<&'a str> {
    environment.get(name).map(String::as_str).or_else(|| {
        mighty_run::os::windows()
            .then(|| environment.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.as_str()))
            .flatten()
    })
}
