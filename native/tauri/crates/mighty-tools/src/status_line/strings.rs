//! The status line's copy (C# `StatusLineStrings`), read from the shared locale files on demand: the keys the Mac's
//! StatusLineView.swift and StatusLine.swift read.

use mighty_run::strings;

pub fn trust_prompt_template() -> String {
    strings::get("statusLine.untrustedPrompt")
}

pub fn trust_allow() -> String {
    strings::get("statusLine.trust")
}

pub fn trust_deny() -> String {
    strings::get("statusLine.notNow")
}

pub fn trust_note() -> String {
    strings::get("statusLine.reaskNote")
}

pub fn accessibility_label() -> String {
    strings::get("composer.statusLine.name")
}

pub fn error_start_template() -> String {
    strings::get("statusLine.error.start")
}

pub fn error_timeout() -> String {
    strings::get("statusLine.error.timeout")
}

pub fn error_exit_template() -> String {
    strings::get("statusLine.error.exitCode")
}

/// `StatusLineConfig.Source` names. The trust fingerprint a workspace's allowed command is saved under hashes this text, so it stays
/// as written in every language (as on the Mac); [`source_label`] is what the screen shows.
pub const SOURCE_WORKSPACE_LOCAL: &str = "프로젝트 로컬 설정";
pub const SOURCE_WORKSPACE: &str = "프로젝트 설정";
pub const SOURCE_USER: &str = "사용자 설정";

/// The localized name of a source; an unknown source shows as it is.
pub fn source_label(source: &str) -> String {
    match source {
        SOURCE_WORKSPACE_LOCAL => strings::get("statusLine.source.projectLocal"),
        SOURCE_WORKSPACE => strings::get("statusLine.source.project"),
        SOURCE_USER => strings::get("statusLine.source.user"),
        _ => source.to_string(),
    }
}
