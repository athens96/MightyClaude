//! C# WorkMapVerification.cs (Verification.cs:1194-1207, "work map: …"): native/contracts/fixtures/work-map.json — the rules of
//! docs/work-map.md that MightyCore's WorkMap.swift holds too — and beside them a real git repository, the summary run with a scripted
//! Claude, the service, the requests and the pane that is never saved. The phone's routes (:348) are W8's.

mod common;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use common::*;
use indexmap::IndexMap;
use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AppSnapshot, LogEntry, RunSession, Workspace};
use mighty_workmap::summary::{self, SummaryError};
use mighty_workmap::{WorkMapCommit, WorkMapRequest, pane};
use serde_json::{Value, json};

/// WorkMapVerification.cs:23 `TheFixtureLimitsAreTheCodesLimits`.
#[test]
fn work_map_the_fixtures_limits_and_prompt_are_the_codes() {
    let f = fixture();
    let n = |key: &str| f[key].as_u64().unwrap_or_else(|| panic!("{key}")) as usize;
    assert!(
        n("maximumVersions") == mighty_workmap::MAXIMUM_VERSIONS
            && n("maximumCommitsPerVersion") == mighty_workmap::MAXIMUM_COMMITS_PER_VERSION
            && n("maximumFilesPerCommit") == mighty_workmap::MAXIMUM_FILES_PER_COMMIT
            && n("maximumRequests") == mighty_workmap::MAXIMUM_REQUESTS
            && n("maximumCommitsPerRequest") == mighty_workmap::MAXIMUM_COMMITS_PER_REQUEST
            && n("requestTextLength") == mighty_workmap::REQUEST_TEXT_LENGTH,
        "the version, commit, file and request limits"
    );
    assert!(
        n("maximumTopics") == summary::MAXIMUM_TOPICS
            && n("titleLength") == summary::TITLE_LENGTH
            && n("summaryLength") == summary::SUMMARY_LENGTH
            && n("notesLength") == summary::NOTES_LENGTH
            && n("summaryTimeoutSeconds") as u64 == summary::TIMEOUT.as_secs(),
        "the summary limits"
    );
    assert!(
        strings(&f["lanes"]) == mighty_workmap::LANES && strings(&f["releaseSteps"]) == mighty_workmap::RELEASE_STEPS,
        "the lanes and the release steps"
    );
    assert_eq!(f["prompt"].as_str(), Some(summary::PROMPT_TEMPLATE), "the prompt template is the fixture's");
}

/// WorkMapVerification.cs:36 `LanesAreReadFromTheScope`.
#[test]
fn work_map_lanes_are_read_from_a_commit_titles_scope() {
    for case in fixture()["laneCases"].as_array().unwrap() {
        let subject = case["subject"].as_str().unwrap();
        let (lanes, scope, text) = mighty_workmap::parse_subject(subject);
        assert_eq!(lanes, strings(&case["lanes"]), "lanes of {subject:?}");
        assert_eq!(scope, case["scope"].as_str().map(str::to_string), "scope of {subject:?}");
        assert_eq!(Some(text.as_str()), case["text"].as_str(), "text of {subject:?}");
    }
}

/// WorkMapVerification.cs:49 `OnlyReleaseTagsCountNewestFirst`.
#[test]
fn work_map_only_vx_y_z_tags_are_versions_newest_first() {
    for case in fixture()["tagCases"].as_array().unwrap() {
        let tags = strings(&case["tags"]);
        assert_eq!(
            mighty_workmap::tags(tags.iter().map(String::as_str)),
            strings(&case["kept"]),
            "tags kept and ordered by version number"
        );
    }
}

/// WorkMapVerification.cs:56 `TheVersionBeingWorkedOnComesFromTheVersionFile`.
#[test]
fn work_map_the_unreleased_versions_name_comes_from_version_above_the_newest_tag() {
    for case in fixture()["versionNameCases"].as_array().unwrap() {
        let (file, tag) = (text(case, "file"), text(case, "newestTag"));
        assert_eq!(mighty_workmap::version_name(file.as_deref(), tag.as_deref()), text(case, "version"), "VERSION {file:?} after {tag:?}");
    }
}

/// WorkMapVerification.cs:63 `ReleaseStateNamesTheNextStep`.
#[test]
fn work_map_a_release_state_names_the_next_step() {
    for case in fixture()["releaseStatusCases"].as_array().unwrap() {
        let state = &case["state"];
        let raw = match state {
            Value::Null => None,
            Value::String(s) => Some(s.clone()),
            other => Some(other.to_string()),
        };
        let (status, step) = mighty_workmap::release_status(raw.as_deref());
        assert!(Some(status.as_str()) == case["status"].as_str() && step == text(case, "step"), "release state {state}: {status} {step:?}");
    }
}

/// WorkMapVerification.cs:74 `DotsGrowWithTheSquareRootOfTheCount`.
#[test]
fn work_map_dots_grow_with_the_square_root_of_the_commit_count() {
    let dots = &fixture()["dotSize"];
    assert!(dots["minimum"] == json!(6) && dots["range"] == json!(14), "the dot's minimum and range");
    for case in dots["cases"].as_array().unwrap() {
        let (count, largest, size) = (case["count"].as_i64().unwrap(), case["largest"].as_i64().unwrap(), case["size"].as_i64().unwrap());
        assert_eq!(mighty_workmap::dot_size(count as i32, largest as i32) as i64, size, "dot of {count} of {largest}");
    }
}

/// WorkMapVerification.cs:85 `AnswersAreReadByTheFixtureRules`.
#[test]
fn work_map_a_topic_answer_is_read_by_the_fixtures_rules() {
    let other = ko().get("workMap.topic.other");
    for case in fixture()["summaryCases"].as_array().unwrap() {
        let name = case["name"].as_str().unwrap();
        let commits: Vec<(String, Vec<String>)> =
            case["commits"].as_array().unwrap().iter().map(|c| (c["sha"].as_str().unwrap().to_string(), strings(&c["lanes"]))).collect();
        let ids: HashSet<String> = strings(&case["requests"]).into_iter().collect();
        let read = summary::parse_answer(case["answer"].as_str().unwrap(), &commits, &ids, &other);
        if case.get("error") == Some(&Value::Bool(true)) {
            assert_eq!(read.err(), Some(SummaryError::Output), "{name}: unreadable answers fail");
            continue;
        }
        let topics = read.unwrap_or_else(|e| panic!("{name}: {e:?}"));
        let expected = case["topics"].as_array().unwrap();
        assert_eq!(topics.len(), expected.len(), "{name}: topics");
        for (i, (topic, want)) in topics.iter().zip(expected).enumerate() {
            let title = if want["title"] == json!("@other") { other.clone() } else { want["title"].as_str().unwrap().to_string() };
            assert!(
                topic.title == title && Some(topic.summary.as_str()) == want["summary"].as_str(),
                "{name}: topic {i} title and summary"
            );
            assert!(
                topic.lanes == strings(&want["lanes"])
                    && topic.commits == strings(&want["commits"])
                    && topic.requests == strings(&want["requests"]),
                "{name}: topic {i} lanes, commits and requests"
            );
        }
    }
}

fn work_commit(sha: &str, lanes: &[&str], scope: Option<&str>, subject: &str) -> WorkMapCommit {
    WorkMapCommit {
        sha: sha.into(),
        date: "2026-01-01T00:00:00+00:00".into(),
        lanes: lanes.iter().map(|l| l.to_string()).collect(),
        scope: scope.map(str::to_string),
        subject: subject.into(),
        files: vec![],
        files_count: 0,
    }
}

/// WorkMapVerification.cs:109 `ThePromptFillsEveryPlaceholderOnce`.
#[test]
fn work_map_the_prompt_fills_each_placeholder_once() {
    let commits =
        [work_commit("aaaaaaa1", &["mac"], Some("mac"), "uses {version} literally"), work_commit("bbbbbbb2", &["etc"], None, "plain")];
    let requests =
        [WorkMapRequest { id: "p1/r1".into(), pane_id: "p1".into(), text: "make it".into(), date: None, files: vec![], commits: vec![] }];
    let prompt = summary::prompt("Korean", "0.4.49", &commits, &requests, Some("NOTES"));
    assert!(
        prompt.contains("Answer in Korean.")
            && prompt.contains("Version: 0.4.49\n")
            && prompt.contains("aaaaaaa1 [mac] mac: uses {version} literally\nbbbbbbb2 [etc] plain")
            && prompt.contains("p1/r1: make it")
            && prompt.ends_with("Release notes:\nNOTES"),
        "the prompt's parts"
    );
    let empty = summary::prompt("English", "next", &[], &[], None);
    assert!(empty.contains("Requests the user sent (id: text):\n(none)\nRelease notes:\n(none)"), "no requests and no notes read (none)");
    let long = summary::prompt("English", "1", &[], &[], Some(&"x".repeat(9000))).chars().count();
    let short = summary::prompt("English", "1", &[], &[], Some("")).chars().count();
    assert_eq!(long - short, summary::NOTES_LENGTH - "(none)".len(), "notes are cut to 8,000");
}

/// WorkMapVerification.cs:293 `RequestsComeFromTheOpenPanesRunsAndWhatTheyEdited` (the run's original written as the files pane's store
/// writes its manifest).
#[test]
fn work_map_requests_come_from_the_open_panes_runs_and_what_they_edited() {
    let parent = temp("requests");
    let folder = parent.join("work");
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(folder.join("a.txt"), "now").unwrap();
    let originals = parent.join("originals");
    let real = mighty_core::workspace_files::real_path(&folder).unwrap().to_string_lossy().into_owned();
    let run_folder = mighty_workmap::originals::run_folder(&originals, "pane", "run-1");
    std::fs::create_dir_all(&run_folder).unwrap();
    std::fs::write(
        run_folder.join("run.json"),
        json!({"version": 1, "sessionId": "pane", "runId": "run-1", "sequence": 1, "files": [{"path": "a.txt", "blob": "b", "bytes": 6, "created": false}], "root": real, "refused": []}).to_string(),
    )
    .unwrap();
    let store = mighty_workmap::originals::FileOriginals::new(originals);
    let entry = LogEntry::new("e1".into(), "user", "x".into(), "2026-11-02T20:58:00+09:00".into(), None);
    let run = |id: &str, input: &str| MightyGraphRun { id: Some(id.into()), input: Some(input.into()), ..MightyGraphRun::default() };
    let pane = |id: &str, workspace: &str, runs: Vec<MightyGraphRun>| RunSession {
        id: Some(id.into()),
        workspace_id: Some(workspace.into()),
        graph_runs: Some(runs),
        ..RunSession::default()
    };
    let sessions = [
        pane(
            "pane",
            "ws1",
            vec![
                MightyGraphRun { source_run_id: Some("run-1".into()), root_entries: Some(vec![entry]), ..run("entry-1", &"q".repeat(300)) },
                MightyGraphRun { notice_run: Some(true), ..run("notice", "a notice") },
                run("blank", "   "),
                run("plain", "no edits"),
            ],
        ),
        pane("other", "ws2", vec![run("x", "another workspace")]),
    ];
    let files = |pane: &str, run: &str, root: &str| store.paths(pane, run, root);
    let inputs = mighty_workmap::requests(sessions.iter(), "ws1", &folder.to_string_lossy(), &files);
    assert_eq!(
        inputs.iter().map(|i| i.run_id.as_str()).collect::<Vec<_>>(),
        ["entry-1", "plain"],
        "a notice run, an empty input and another workspace's pane are left out"
    );
    assert!(
        inputs[0].pane_id == "pane"
            && inputs[0].text.chars().count() == 200
            && inputs[0].date.as_deref() == Some("2026-11-02T20:58:00+09:00")
            && inputs[0].files == ["a.txt"],
        "the text is cut to 200, the date is the first root record's and the files are the run's (read under its source run)"
    );
    assert!(inputs[1].date.is_none() && inputs[1].files.is_empty(), "a run with no record has no date and no files");
    assert!(
        mighty_workmap::requests(sessions.iter(), "ws1", &parent.join("elsewhere").to_string_lossy(), &files)[0].files.is_empty(),
        "originals kept under another folder name no files"
    );
    remove(&parent);
}

/// WorkMapVerification.cs:325 `TheWorkMapPaneIsNeverSavedAndNeverCounted` (its usage-chip part is W6's `AccountUsageStatus`).
#[test]
fn work_map_the_pane_is_never_saved_and_never_counted() {
    let pane_id = pane::pane_id("ws1");
    assert!(
        pane::KIND == "workmap" && pane_id == "workmap:ws1" && mighty_core::wire::identifier(&pane::pane_id(&mighty_core::wire::new_id())),
        "one pane per workspace, a valid identifier"
    );
    let folder = temp("pane");
    let workspace = Workspace {
        id: Some("ws1".into()),
        name: Some("w".into()),
        path: Some(folder.to_string_lossy().into_owned()),
        ..Workspace::default()
    };
    let agent =
        RunSession { id: Some("agent1".into()), workspace_id: Some("ws1".into()), title: Some("Claude".into()), ..RunSession::default() };
    let map = RunSession {
        id: Some(pane_id.clone()),
        workspace_id: Some("ws1".into()),
        title: Some(ko().get("workMap.title")),
        kind: Some(pane::KIND.into()),
        draft: Some("never kept".into()),
        status: Some("running".into()),
        ..RunSession::default()
    };
    let stray = RunSession {
        id: Some("workmap-other".into()),
        workspace_id: Some("ws1".into()),
        kind: Some(pane::KIND.into()),
        ..RunSession::default()
    };
    let snapshot = AppSnapshot {
        workspaces: Some(vec![workspace]),
        sessions: Some(vec![Arc::new(agent), Arc::new(map), Arc::new(stray)]),
        active_workspace_id: Some("ws1".into()),
        active_session_id: Some(pane_id.clone()),
        ..AppSnapshot::default()
    };
    let live = mighty_core::normalize::normalize(&snapshot, false).expect("normalized");
    assert!(
        live.sessions().iter().map(|s| s.id()).collect::<Vec<_>>() == ["agent1", pane_id.as_str()]
            && live.active_session_id.as_deref() == Some(pane_id.as_str()),
        "the running app keeps exactly one work map per workspace"
    );
    let kept = live.session(&pane_id).unwrap();
    assert!(kept.kind() == "workmap" && kept.draft.as_deref() == Some("") && kept.status() == "idle", "it keeps nothing but its title");
    assert_eq!(
        mighty_core::normalize::normalize(&snapshot, true)
            .expect("restored")
            .sessions()
            .iter()
            .map(|s| s.id().to_string())
            .collect::<Vec<_>>(),
        ["agent1"],
        "a restart never brings it back"
    );
    let mut selected = live.clone();
    selected.pane_layout_active_session_ids = Some(IndexMap::from([("ws1".to_string(), Some(pane_id.clone()))]));
    let stored = mighty_core::normalize::stored(&selected);
    use mighty_core::Wire;
    assert!(
        !stored.to_json().contains(&pane_id)
            && stored.active_session_id.is_none()
            && stored.pane_layout_active_session_ids.as_ref().is_some_and(|m| m.is_empty()),
        "the written state names it nowhere"
    );
    assert!(!mighty_core::shell::is_counted("workmap"), "the status counts leave it out");
    remove(&folder);
}

// ── a real repository, the summary run and the service (Windows: git and the scripted Claude start through WindowsProcessHost) ──

#[cfg(windows)]
mod real {
    use super::*;
    use mighty_workmap::{WorkMapService, WorkMapStart, WorkMapStoredSummary, WorkMapTopic};

    fn topic(title: &str, lanes: &[&str], commits: &[String]) -> WorkMapTopic {
        WorkMapTopic {
            title: title.into(),
            summary: String::new(),
            lanes: lanes.iter().map(|l| l.to_string()).collect(),
            commits: commits.to_vec(),
            requests: vec![],
        }
    }

    fn input(run: &str, date: Option<&str>, files: &[&str]) -> mighty_workmap::WorkMapRequestInput {
        mighty_workmap::WorkMapRequestInput {
            pane_id: "p1".into(),
            run_id: run.into(),
            text: format!("text {run}"),
            date: date.map(str::to_string),
            files: files.iter().map(|f| f.to_string()).collect(),
        }
    }

    fn request_ids(version: &mighty_workmap::WorkMapVersion) -> Vec<String> {
        version.requests.iter().map(|r| r.id.clone()).collect()
    }

    /// WorkMapVerification.cs:158 `AGitRepositoryIsReadByTagsMergesAndReleaseState`.
    #[tokio::test(flavor = "multi_thread")]
    async fn work_map_a_real_git_repository_is_read_by_tags_merges_version_and_release_state() {
        if mighty_workmap::git::executable().is_none() {
            return;
        }
        let parent = temp("repo");
        let git = git(&parent);
        let folder = make_repository(&git, &parent).await;
        let (empty, none) = (IndexMap::new(), HashMap::new());
        let model = mighty_workmap::build(&git, &folder, &[], &empty, None, &none).await;
        assert!(
            model.available && model.reason.is_none() && model.head.as_ref().is_some_and(|h| h.len() == 8),
            "a repository is available, with the HEAD's first 8"
        );
        let keys: Vec<&str> = model.versions.iter().map(|v| v.key.as_str()).collect();
        assert_eq!(keys, ["next", "v0.1.1", "v0.1.0"], "newest first: the unreleased version, then the tags (the other tag is no version)");
        let (next, middle, first) = (&model.versions[0], &model.versions[1], &model.versions[2]);
        assert!(
            next.version.as_deref() == Some("0.1.2")
                && next.tag.is_none()
                && next.status == "releasing"
                && next.release_step.as_deref() == Some("ci")
                && next.total == 1
                && next.date.as_deref() == Some("2026-02-01"),
            "the unreleased version: {:?} {} {:?} {:?}",
            next.version,
            next.status,
            next.release_step,
            next.date
        );
        assert!(
            next.commits.len() == 1
                && next.commits[0].lanes == ["mac", "windows"]
                && next.commits[0].files == ["a.txt"]
                && next.counts["mac"] == 1
                && next.counts["windows"] == 1,
            "its commit's lanes and files"
        );
        assert!(
            next.range_end.len() == 40 && next.range_end.starts_with(model.head.as_deref().unwrap()),
            "the unreleased range ends at HEAD"
        );
        assert!(
            middle.status == "released"
                && middle.version.as_deref() == Some("0.1.1")
                && middle.tag.as_deref() == Some("v0.1.1")
                && middle.total == 3
                && middle.commits.len() == 3
                && !middle.truncated,
            "v0.1.1 holds the two commits and the side commit, not the merge"
        );
        assert!(!model.versions.iter().flat_map(|v| &v.commits).any(|c| c.subject.starts_with("Merge")), "merges are left out");
        assert!(
            middle.commits.iter().find(|c| c.subject == "three").and_then(|c| c.scope.as_deref()) == Some("phone")
                && middle.counts.get("ci") == Some(&1)
                && middle.counts.get("phone") == Some(&1)
                && middle.counts.get("windows") == Some(&1)
                && !middle.counts.contains_key("mac"),
            "its commits are read by scope, a conventional title too"
        );
        assert!(
            first.total == 1 && first.commits[0].lanes == ["mac"] && first.date.as_ref().is_some_and(|d| d.len() == 10),
            "the oldest tag starts from the beginning"
        );
        assert!(
            middle.commits.iter().all(|c| c.sha.len() == 8) && middle.range_end.len() == 40,
            "commits carry 8 characters, a range its end's 40"
        );
        assert!(model.versions.iter().all(|v| v.topics.is_none() && !v.topics_stale && v.summary_error.is_none()), "no summary yet");

        // A stored summary is stale once the range ends elsewhere.
        let mut stored = IndexMap::new();
        stored.insert(
            "v0.1.1".to_string(),
            WorkMapStoredSummary {
                range_end: middle.range_end.clone(),
                topics: vec![topic("T", &["phone"], &[middle.commits[0].sha.clone()])],
                created: "now".into(),
            },
        );
        stored.insert(
            "v0.1.0".to_string(),
            WorkMapStoredSummary {
                range_end: "0000".into(),
                topics: vec![topic("Old", &["mac"], &[first.commits[0].sha.clone()])],
                created: "then".into(),
            },
        );
        let summed =
            mighty_workmap::build(&git, &folder, &[], &stored, Some("next"), &HashMap::from([("v0.1.0".to_string(), "oops".to_string())]))
                .await;
        assert!(
            summed.summarizing.as_deref() == Some("next")
                && summed.versions[1].topics.as_ref().is_some_and(|t| t.len() == 1)
                && !summed.versions[1].topics_stale
                && summed.versions[2].topics_stale
                && summed.versions[2].topics.as_ref().is_some_and(|t| t.len() == 1)
                && summed.versions[2].summary_error.as_deref() == Some("oops"),
            "a summary shows, and is stale when its range ended elsewhere"
        );

        // Requests go to the version of the oldest commit that followed them.
        let asked = mighty_workmap::build(
            &git,
            &folder,
            &[
                input("r1", Some("2000-01-01T00:00:00+00:00"), &["a.txt"]),
                input("r2", Some("2026-01-01T00:00:01+00:00"), &["a.txt"]),
                input("r3", Some("2999-01-01T00:00:00+00:00"), &[]),
                input("r4", Some("2000-01-01T00:00:00+00:00"), &[]),
                input("r5", None, &["b.txt"]),
                input("r6", Some("2026-01-01T12:00:00+00:00"), &["nothing.txt"]),
            ],
            &empty,
            None,
            &none,
        )
        .await;
        assert!(
            request_ids(&asked.versions[2]) == ["p1/r1"] && asked.versions[2].requests[0].commits.len() == 2,
            "a request whose file the oldest commit touched sits in that commit's version"
        );
        assert!(
            request_ids(&asked.versions[1]) == ["p1/r5"]
                && asked.versions[1].requests[0].date.is_none()
                && asked.versions[1].requests[0].commits.len() == 1,
            "no date: every commit that touched its files follows"
        );
        let mut newest = request_ids(&asked.versions[0]);
        newest.sort();
        assert_eq!(
            newest,
            ["p1/r2", "p1/r3"],
            "only commits after the request count; one with no commit sits in the unreleased version when newer than the newest tag"
        );
        assert!(
            asked.versions.iter().flat_map(|v| &v.requests).all(|r| r.id != "p1/r4" && r.id != "p1/r6"),
            "an old request with no commit is dropped"
        );

        // Not a repository.
        let plain = parent.join("plain");
        std::fs::create_dir_all(&plain).unwrap();
        let gone = mighty_workmap::build(&git, &plain, &[], &empty, None, &none).await;
        assert!(
            !gone.available && gone.reason.as_deref() == Some("notGit") && gone.versions.is_empty(),
            "a folder that is no repository says notGit"
        );
        remove(&parent);
    }

    /// WorkMapVerification.cs:201 `ARepositoryWithoutTagsIsOneUnreleasedVersion`.
    #[tokio::test(flavor = "multi_thread")]
    async fn work_map_a_repository_without_tags_is_one_unreleased_version() {
        if mighty_workmap::git::executable().is_none() {
            return;
        }
        let parent = temp("untagged");
        let git = git(&parent);
        let folder = parent.join("repo");
        std::fs::create_dir_all(&folder).unwrap();
        run_git(&git, &folder, &["init", "-q"]).await;
        let (empty, none) = (IndexMap::new(), HashMap::new());
        let fresh = mighty_workmap::build(&git, &folder, &[], &empty, None, &none).await;
        assert!(fresh.available && fresh.head.is_none() && fresh.versions.is_empty(), "no commit yet: nothing to show, nothing failed");
        commit(&git, &folder, "a.txt", "(docs) first", "2026-01-01T00:00:00+00:00").await;
        let model = mighty_workmap::build(&git, &folder, &[], &empty, None, &none).await;
        assert!(
            model.versions.len() == 1
                && model.versions[0].key == "next"
                && model.versions[0].version.is_none()
                && model.versions[0].status == "unreleased"
                && model.versions[0].commits[0].lanes == ["etc"],
            "with no tag the whole history is the unreleased version"
        );
        remove(&parent);
    }

    /// WorkMapVerification.cs:220 `ClaudeIsAskedOnceInAFolderOfItsOwn`.
    #[tokio::test(flavor = "multi_thread")]
    async fn work_map_claude_is_asked_once_in_a_folder_of_its_own_with_a_time_limit() {
        let processes = processes();
        let environment = std::collections::BTreeMap::new();
        let ok = summary::run(processes.as_ref(), &fake_claude("ok"), &environment, "hello prompt", std::time::Duration::from_secs(60))
            .await
            .expect("an answer");
        let parts: Vec<&str> = ok.split('|').collect();
        assert_eq!(
            parts[0], "got:hello prompt",
            "the prompt arrives as one stream-json user message and the result event's text comes back: {ok}"
        );
        assert!(
            parts[1].contains("mighty-work-map-") && !std::path::Path::new(parts[1]).exists(),
            "it runs in a temporary folder that is gone after: {}",
            parts[1]
        );
        assert_eq!(parts[2], "tools-off", "it is launched with the model probe's headless arguments");
        let hold = summary::run(processes.as_ref(), &fake_claude("hold"), &environment, "x", std::time::Duration::from_secs(1)).await;
        assert_eq!(hold.err(), Some(SummaryError::Timeout), "a run past its time says timeout");
        let silent = summary::run(processes.as_ref(), &fake_claude("silent"), &environment, "x", std::time::Duration::from_secs(30)).await;
        assert_eq!(silent.err(), Some(SummaryError::Output), "no result event reads as unreadable");
        let missing = mighty_run::model::CliCommand::new(temp("missing").join("no-such-claude.exe").to_string_lossy(), vec![], Some("1"));
        let cli = summary::run(processes.as_ref(), &missing, &environment, "x", std::time::Duration::from_secs(30)).await;
        assert_eq!(cli.err(), Some(SummaryError::Cli), "a program that cannot start says cli");
    }

    /// WorkMapVerification.cs:235 `TheServiceSummarizesOneVersionAtATimeAndSavesIt`.
    #[tokio::test(flavor = "multi_thread")]
    async fn work_map_the_service_summarizes_one_version_at_a_time_and_saves_it() {
        if mighty_workmap::git::executable().is_none() {
            return;
        }
        let parent = temp("service");
        let git = git(&parent);
        let folder = make_repository(&git, &parent).await;
        let workspace = Workspace {
            id: Some("ws1".into()),
            name: Some("w".into()),
            path: Some(folder.to_string_lossy().into_owned()),
            ..Workspace::default()
        };
        let snapshot = AppSnapshot { workspaces: Some(vec![workspace.clone()]), ..AppSnapshot::default() };
        let data = parent.join("data");
        let gate = Arc::new(tokio::sync::Notify::new());
        let opened = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let runs = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let last_prompt = Arc::new(std::sync::Mutex::new(String::new()));
        let found = Arc::new(std::sync::Mutex::new(Some(mighty_run::model::CliCommand::new("claude", vec![], Some("1")))));
        let finding = found.clone();
        let finder: mighty_workmap::service::Finder = Arc::new(move || {
            let command = finding.lock().unwrap().clone();
            Box::pin(async move { command })
        });
        let (waiting, count, prompts, open) = (gate.clone(), runs.clone(), last_prompt.clone(), opened.clone());
        let runner: summary::Runner = Arc::new(move |_, prompt, _| {
            let (waiting, count, prompts, open) = (waiting.clone(), count.clone(), prompts.clone(), open.clone());
            Box::pin(async move {
                count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                *prompts.lock().unwrap() = prompt.clone();
                if !open.load(std::sync::atomic::Ordering::SeqCst) {
                    waiting.notified().await;
                }
                let shas: Vec<String> =
                    prompt.lines().filter_map(|l| l.split_once(" [").map(|(sha, _)| sha.to_string())).filter(|s| s.len() == 8).collect();
                Ok(format!("```json\n{}\n```", json!({"topics": [{"title": "Everything", "summary": "All of it.", "commits": shas}]})))
            })
        });
        let state = snapshot.clone();
        let make = |runner: summary::Runner| {
            let state = state.clone();
            WorkMapService::new(
                data.join("work-map"),
                git.clone(),
                Arc::new(move || state.clone()),
                parent.join("originals"),
                finder.clone(),
                runner,
                ko(),
            )
        };
        let service = make(runner.clone());
        let (changed_tx, mut changed_rx) = tokio::sync::mpsc::unbounded_channel::<String>();
        service.on_changed(Arc::new(move |id: &str| {
            let _ = changed_tx.send(id.to_string());
        }));

        assert!(
            service.start(&workspace, "v9.9.9").await == WorkMapStart::UnknownKey && service.model(&workspace).await.summarizing.is_none(),
            "an unknown key is refused and leaves nothing busy"
        );
        assert_eq!(service.start(&workspace, "v0.1.1").await, WorkMapStart::Started, "a known version starts");
        assert!(
            service.start(&workspace, "next").await == WorkMapStart::Busy
                && service.model(&workspace).await.summarizing.as_deref() == Some("v0.1.1"),
            "a second one waits: one at a time per workspace"
        );
        opened.store(true, std::sync::atomic::Ordering::SeqCst);
        gate.notify_waiters();
        gate.notify_one();
        assert!(tokio::time::timeout(std::time::Duration::from_secs(30), changed_rx.recv()).await.is_ok(), "the end is announced");
        let model = service.model(&workspace).await;
        let done = model.versions.iter().find(|v| v.key == "v0.1.1").unwrap();
        assert!(
            model.summarizing.is_none()
                && done.topics.as_ref().is_some_and(|t| t.len() == 1 && t[0].commits.len() == 3)
                && !done.topics_stale
                && done.summary_error.is_none()
                && runs.load(std::sync::atomic::Ordering::SeqCst) == 1,
            "the topics are read, kept and fresh"
        );
        let prompt = last_prompt.lock().unwrap().clone();
        assert!(
            prompt.contains("Version: 0.1.1") && prompt.contains("[phone] phone: three"),
            "the prompt carries the version and its commits: {prompt}"
        );
        let saved: Vec<_> =
            std::fs::read_dir(data.join("work-map")).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
        assert!(
            saved.len() == 1
                && saved[0].len() == 64 + 5
                && saved[0].ends_with(".json")
                && saved[0][..64].bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()),
            "saved as <sha256 of the root>.json, with no temporary file left: {saved:?}"
        );
        let later = make(runner);
        assert!(
            later
                .model(&workspace)
                .await
                .versions
                .iter()
                .find(|v| v.key == "v0.1.1")
                .unwrap()
                .topics
                .as_ref()
                .is_some_and(|t| t.len() == 1),
            "a new run of the app reads the saved summary"
        );

        // The CLI is not there.
        *found.lock().unwrap() = None;
        assert_eq!(service.start(&workspace, "v0.1.0").await, WorkMapStart::Started, "a summary without the CLI starts");
        assert!(tokio::time::timeout(std::time::Duration::from_secs(30), changed_rx.recv()).await.is_ok(), "and ends");
        let error = service.model(&workspace).await.versions.iter().find(|v| v.key == "v0.1.0").unwrap().summary_error.clone();
        assert_eq!(error, Some(ko().get("workMap.error.cli")), "and says the CLI was not found");
        let remote = Workspace {
            id: Some("ws2".into()),
            name: Some("r".into()),
            path: Some(folder.to_string_lossy().into_owned()),
            remote: Some(mighty_core::model::RemoteWorkspaceReference {
                connection_id: Some("c".into()),
                workspace_id: Some("w".into()),
                host_name: Some("host".into()),
                version: 2,
            }),
            ..Workspace::default()
        };
        let denied = service.model(&remote).await;
        assert!(
            !denied.available
                && denied.reason.as_deref() == Some("remote")
                && service.start(&remote, "next").await == WorkMapStart::Unavailable,
            "a remote workspace has no work map"
        );
        remove(&parent);
    }
}
