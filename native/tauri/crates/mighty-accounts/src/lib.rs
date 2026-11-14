//! Mighty Code's CLI accounts, login recovery and account usage (W6-C), with no Tauri dependency and no OS code: the accounts and
//! their coordinator (`accounts`), sign-in in a terminal (`terminal`), login recovery (`login`), account usage (`usage`), the
//! Bedrock reset (`bedrock`) and their forms (`view`). What mighty-run already reads of the accounts is reused from it.

pub mod accounts;
pub mod bedrock;
pub mod login;
pub mod terminal;
pub mod usage;
pub mod view;
