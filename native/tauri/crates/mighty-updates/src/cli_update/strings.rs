//! The CLI update section's copy (C# `CliUpdateStrings`), read from the shared locale files so macOS and Windows show the same words.
//!
//! OS-bound differences: [`section_description`] reads the Windows variant ("이 PC에 설치된"); [`detail_winget_plan`] and
//! [`detail_winget_runtime_missing`] are Windows-only. C#'s fields are `static readonly` (read once, in the language of the first
//! use); these read the current language each time.

use mighty_run::strings::get;

macro_rules! copy {
    ($($field:literal $name:ident => $key:literal),* $(,)?) => {
        $(
            #[doc = concat!("C# `", $field, "`: `", $key, "`.")]
            pub fn $name() -> String {
                get($key)
            }
        )*

        /// Every field's value by its C# name, in the class's order (what the strings check reads instead of reflection).
        pub fn all() -> Vec<(&'static str, String)> {
            vec![$(($field, $name())),*]
        }
    };
}

copy! {
    "SectionTitle" section_title => "settings.cliUpdate.sectionTitle",
    "AutoUpdateToggle" auto_update_toggle => "settings.cliUpdate.autoUpdateToggle",
    "SectionDescription" section_description => "settings.cliUpdate.sectionDescriptionWindows",
    "ProgressInspecting" progress_inspecting => "settings.cliUpdate.progressInspecting",
    "ProgressProviderTemplate" progress_provider_template => "settings.cliUpdate.progressProviderTemplate",
    "LastRunTemplate" last_run_template => "settings.cliUpdate.lastRunTemplate",
    "UpdateButton" update_button => "settings.cliUpdate.updateButton",
    "UpdatingButton" updating_button => "settings.cliUpdate.updatingButton",
    "ResultRowTemplate" result_row_template => "settings.cliUpdate.resultRowTemplate",
    "VersionChangeTemplate" version_change_template => "settings.cliUpdate.versionChangeTemplate",
    "StatusUpdated" status_updated => "settings.cliUpdate.statusUpdated",
    "StatusCurrent" status_current => "settings.cliUpdate.statusCurrent",
    "StatusFailed" status_failed => "settings.cliUpdate.statusFailed",
    "StatusCancelled" status_cancelled => "settings.cliUpdate.statusCancelled",
    "StatusBusy" status_busy => "settings.cliUpdate.statusBusy",
    "StatusSkipped" status_skipped => "settings.cliUpdate.statusSkipped",
    "DetailClosing" detail_closing => "settings.cliUpdate.detailClosing",
    "DetailInspectCancelled" detail_inspect_cancelled => "settings.cliUpdate.detailInspectCancelled",
    "DetailInspectFailed" detail_inspect_failed => "settings.cliUpdate.detailInspectFailed",
    "DetailBusy" detail_busy => "settings.cliUpdate.detailBusy",
    "DetailCancelled" detail_cancelled => "settings.cliUpdate.detailCancelled",
    "DetailUnsupportedProvider" detail_unsupported_provider => "settings.cliUpdate.detailUnsupportedProvider",
    "DetailVersionUnknown" detail_version_unknown => "settings.cliUpdate.detailVersionUnknown",
    "DetailMissing" detail_missing => "settings.cliUpdate.detailMissing",
    "DetailUnknownMethod" detail_unknown_method => "settings.cliUpdate.detailUnknownMethod",
    "DetailNativeClaude" detail_native_claude => "settings.cliUpdate.detailNativeClaude",
    "DetailNpmPrerelease" detail_npm_prerelease => "settings.cliUpdate.detailNpmPrerelease",
    "DetailNpmRuntimeMissing" detail_npm_runtime_missing => "settings.cliUpdate.detailNpmRuntimeMissing",
    "DetailNpmPlan" detail_npm_plan => "settings.cliUpdate.detailNpmPlan",
    "DetailFailedExitTemplate" detail_failed_exit_template => "settings.cliUpdate.detailFailedExitTemplate",
    "DetailVersionRecheckFailed" detail_version_recheck_failed => "settings.cliUpdate.detailVersionRecheckFailed",
    "DetailUpdated" detail_updated => "settings.cliUpdate.detailUpdated",
    "DetailUnchanged" detail_unchanged => "settings.cliUpdate.detailUnchanged",
    "DetailWingetPlan" detail_winget_plan => "settings.cliUpdate.detailWingetPlan",
    "DetailWingetRuntimeMissing" detail_winget_runtime_missing => "settings.cliUpdate.detailWingetRuntimeMissing",
}

/// `detailFailedExitTemplate` with the exit code.
pub fn detail_failed_exit(code: i32) -> String {
    detail_failed_exit_template().replace("{code}", &code.to_string())
}

/// The macOS `label(_:)` switch: an unknown status reads 건너뜀.
pub fn status_label(status: &str) -> String {
    match status {
        "updated" => status_updated(),
        "current" => status_current(),
        "failed" => status_failed(),
        "cancelled" => status_cancelled(),
        "busy" => status_busy(),
        _ => status_skipped(),
    }
}
