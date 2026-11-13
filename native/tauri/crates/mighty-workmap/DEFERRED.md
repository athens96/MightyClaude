# mighty-workmap: what is left for a later stage

Every C# check of the work map and the work dashboard this crate does not run whole yet, with what it needs.

- WorkMapVerification.cs:348 `ThePhoneReadsAndStartsTheWorkMapThroughTheRouter` (whole): the phone's `/m1/workspaces/{id}/workmap` and
  `…/workmap/summarize` routes (`MobileDesktopRouter`) — W8. The model they serve is `WorkMapService::model` (the phone's copy leaves the
  commits' `files` out, `filesCount` only).
- WorkMapVerification.cs:342 (in `TheWorkMapPaneIsNeverSavedAndNeverCounted`): the usage chips leave the pane out
  (`AccountUsageStatus.LocalProviders`) — W6. The rest of the check is tests/work_map.rs.
- WorkDashboardVerification.cs:67-68 (in `SettingsSelectionSurvivesPersistence`): the settings categories and sections
  (`SettingsNavigation`, `SettingsSections.MacOrder`) — W6. Its normalize part is tests/work_dashboard.rs.
- WorkDashboardVerification.cs:130 `BackgroundWorkOpenSurvivesABriefReplaceBlock` (whole): a reader holding the state file while
  `DesktopService` saves twenty switches; it needs the app's save queue (`DesktopService.UpdateAsync` → `Saver`) over a held file.
  mighty-core's store keeps C#'s 40 × 50 ms retry of a refused swap (store.rs `replace`); the check itself comes with the host's save
  tests.
- `FileOriginalStore`: the work map reads a run's manifest (`originals::FileOriginals::paths`, C# `FileOriginalStore.Paths`); the store
  that writes them (`Capture`, `Retain`, `Prune`) is the files pane's (W4-B). `RequestsComeFromTheOpenPanesRunsAndWhatTheyEdited` writes
  the manifest as that store does.
- Git runs through `Git` (C# `DelegationGit.Run`) with an empty hooks folder of the app's own; the delegation's git (W7) will share it.
