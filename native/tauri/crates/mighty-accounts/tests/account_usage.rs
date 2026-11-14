//! C# AccountUsageVerification (MightyCode.Core.Tests/AccountUsageVerification.cs), AntigravityAccountUsageVerification and the usage
//! parts of AcpAgentAccountVerification and StringsVerification (Verification.cs:1402-1431). Every check injects the transport, the
//! clock and a fixture credentials file in a temporary folder: nothing here starts a real CLI, touches the network or reads the real
//! profile. The English half of LocalizedStatusVerification is tests/usage_language.rs (a language is the process's own).

mod common;

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use common::{env, temp, text};
use mighty_accounts::usage::{
    self, AccountResetEntitlement, AccountUsageFailure, AccountUsageFailureKind, AccountUsageHttpRequest, AccountUsageHttpResponse,
    AccountUsageService, AccountUsageShapeLog, AccountUsageSmokeOutcome, AccountUsageSnapshot, AccountUsageStatus, AccountUsageStdio,
    AccountUsageWindow, AntigravityAccountProbe, ClaudeAccountProbe, ClaudeQuotaCredential, ClaudeResetEntitlements, Clock,
    CodexAccountProbe, Guarded, HttpFailure, HttpFn, HttpsGet, ResetProgram, ResetReadDeadlineBox, ResetState, RuntimeProbe, UsageError,
    UsageProbe, claude_reset_smoke, copy, credential_file,
};
use mighty_core::json::Wire;
use mighty_core::model::AppSnapshot;
use mighty_platform::BoxFuture;
use mighty_run::strings;
use serde_json::{Value, json};

/// A made-up sign-in value. It is not a real token and never leaves the fixture.
const FIXTURE_TOKEN: &str = "sk-ant-oat01-FIXTURE-NOT-A-REAL-TOKEN-0123456789";
const USAGE_BODY: &str = r#"{"five_hour":{"utilization":42.5,"resets_at":"2026-09-20T12:00:00Z"},"seven_day":{"utilization":91,"resets_at":"2026-09-24T00:00:00Z"}}"#;
const PROFILE_BODY: &str = r#"{"account":{"email":"fixture@example.test"},"organization":{"rate_limit_tier":"max_20x"}}"#;

fn instant() -> DateTime<Utc> {
    DateTime::parse_from_rfc3339("2026-09-20T10:00:00Z").unwrap().with_timezone(&Utc)
}

fn o(at: DateTime<Utc>) -> String {
    usage::timestamp(at)
}

fn clock_at(at: DateTime<Utc>) -> Clock {
    Arc::new(move || at)
}

fn credential() -> ClaudeQuotaCredential {
    ClaudeQuotaCredential { token: FIXTURE_TOKEN.into(), plan: Some("max".into()) }
}

/// `WriteCredentials`: a fixture credentials file in a temporary `.claude` folder.
fn write_credentials(home: &std::path::Path, token: &str, expires_at: Option<i64>) -> std::path::PathBuf {
    let folder = home.join(".claude");
    std::fs::create_dir_all(&folder).unwrap();
    let path = folder.join(credential_file::FILE_NAME);
    let expires = expires_at.unwrap_or((instant() + TimeDelta::hours(4)).timestamp_millis());
    let body = json!({"claudeAiOauth": {"accessToken": token, "expiresAt": expires, "scopes": ["user:profile", "user:inference"], "subscriptionType": "max"}});
    std::fs::write(&path, body.to_string()).unwrap();
    path
}

/// The usage endpoints answered from fixtures: the base read, the profile, and each entitlement query as given.
fn answers(
    cedar: (u16, &'static str),
    juniper: (u16, &'static str),
) -> impl Fn(&AccountUsageHttpRequest) -> AccountUsageHttpResponse + Send + Sync {
    move |request: &AccountUsageHttpRequest| match request.url.query() {
        Some("cedar_ember=1&skip_spend=1") => AccountUsageHttpResponse::new(cedar.0, cedar.1),
        Some("at_wall=1&skip_spend=1") => AccountUsageHttpResponse::new(juniper.0, juniper.1),
        _ => AccountUsageHttpResponse::new(200, if request.url.path().ends_with("profile") { PROFILE_BODY } else { USAGE_BODY }),
    }
}

fn read_claude<'a>(
    environment: &'a std::collections::BTreeMap<String, String>,
    load: impl FnOnce() -> Option<ClaudeQuotaCredential> + Send + 'a,
    http: &'a dyn HttpsGet,
    at: DateTime<Utc>,
    deadline: &'a ResetReadDeadlineBox,
    log: Option<&'a AccountUsageShapeLog>,
) -> BoxFuture<'a, Result<AccountUsageSnapshot, UsageError>> {
    Box::pin(async move { ClaudeAccountProbe::read(environment, load, http, &move || at, Some(deadline), log).await })
}

/// An app snapshot from its JSON, as the state reads it.
fn snapshot(value: Value) -> AppSnapshot {
    AppSnapshot::read(&value).expect("a snapshot")
}

fn probe_of(f: impl Fn(String) -> Result<AccountUsageSnapshot, UsageError> + Send + Sync + 'static) -> UsageProbe {
    Arc::new(move |provider| {
        let value = f(provider);
        Box::pin(async move { value })
    })
}

fn unavailable(detail: String) -> UsageError {
    UsageError::Failure(AccountUsageFailure::new(AccountUsageFailureKind::Unavailable, detail))
}

/// StringsVerification.cs:269 `AccountUsageMacOS`: the copy the class must show, in Korean.
const ACCOUNT_USAGE_MACOS: [(&str, &str); 61] = [
    ("Title", "계정 사용 한도"),
    ("ChipsTooltip", "계정 사용 한도 · 클릭해 상세 보기"),
    ("RefreshTooltip", "계정 한도 다시 확인"),
    ("RefreshAccessibilityLabel", "계정 한도 새로고침"),
    ("ChipBeforeFirstRun", "실행 후 표시"),
    ("ChipChecking", "확인 중"),
    ("ChipEmpty", "—"),
    ("ToggleLabel", "Claude 한도를 직접 조회"),
    ("ToggleDescription", "끄면 앱이 Anthropic에 직접 조회하지 않습니다. Claude 실행 때 CLI가 보고하는 한도만 표시합니다."),
    ("SharedLimitsNote", "계정 한도는 같은 계정을 사용하는 앱·세션에서 공유됩니다. 자동 조회는 직접 조회를 켜기 전에는 일어나지 않습니다."),
    ("UsedPercentTemplate", "{percent}% 사용"),
    ("ResetTemplate", "초기화 {date}"),
    ("CheckedAtTemplate", "{time} 확인"),
    ("LastKnownPrefix", "마지막 확인값 · "),
    ("WindowFiveHourSuffix", " (5시간)"),
    ("WindowSevenDaySuffix", " (7일)"),
    ("ClaudeBeforeFirstRunNote", "Claude를 한 번 실행하면 CLI가 보고한 세션·주간 한도가 여기에 표시됩니다."),
    ("CardChecking", "계정 사용 한도를 확인하고 있습니다…"),
    ("CardNotCheckedYet", "아직 확인하지 않았습니다."),
    ("WindowSession", "세션"),
    ("WindowWeekly", "주간"),
    ("WindowDaily", "일간"),
    ("WindowMonthly", "월간"),
    ("WindowSpendLimit", "지출 한도"),
    ("DetailNotCheckedYet", "계정 사용량을 아직 확인하지 않았습니다."),
    ("DetailShutdown", "계정 조회를 종료했습니다."),
    ("DetailCancelled", "계정 조회를 취소했습니다."),
    ("DetailRefreshFailed", "계정 사용량을 갱신하지 못했습니다. 잠시 후 다시 확인하세요."),
    ("DetailAuthentication", "CLI 로그인을 다시 확인하세요. 계정 한도 조회 권한이 없거나 로그인이 만료되었습니다."),
    ("DetailRateLimited", "조회가 제한되었습니다. 잠시 후 자동으로 다시 확인합니다."),
    ("DetailLastKnownSuffix", " 마지막으로 확인한 값입니다."),
    (
        "DetailAntigravityUnavailable",
        "Antigravity CLI는 이 연결 방식에서 계정 한도를 제공하지 않습니다. 터미널의 agy에서 /usage로 확인하세요.",
    ),
    ("GroupWindowTemplate", "{group} · {window}"),
    ("AntigravityGroupGemini", "Gemini"),
    ("AntigravityGroupClaudeGpt", "Claude·GPT"),
    ("DetailAntigravity", "Antigravity 계정 한도"),
    ("DetailAntigravityNotInstalled", "Antigravity CLI(agy)를 설치하고 로그인하세요."),
    ("DetailAntigravityNoWindows", "이 계정은 사용 한도 기간을 제공하지 않습니다."),
    ("DetailUnsupportedProvider", "지원하지 않는 계정입니다."),
    ("DetailCodexNotInstalled", "Codex CLI를 설치하고 로그인하세요."),
    ("DetailCodexNeedsChatGPT", "ChatGPT로 Codex CLI에 로그인하면 계정 한도를 확인할 수 있습니다."),
    ("DetailCustomAuthentication", "사용자 지정 인증의 계정 한도는 CLI에서 확인하세요."),
    ("DetailCodexNoWindows", "이 계정에서 사용량 한도 창을 제공하지 않습니다."),
    ("DetailCodex", "Codex 계정 한도"),
    ("DetailClaudeNoWindows", "이 Claude 계정에서 구독 한도를 제공하지 않습니다."),
    ("DetailClaude", "Claude 계정 한도"),
    ("DetailSessionReportedStale", "세션에서 마지막으로 받은 계정 한도입니다."),
    ("DetailSessionReported", "실행 중인 세션에서 받은 계정 한도입니다."),
    ("ResetTitle", "리셋권"),
    ("ResetLink", "claude.ai에서 리셋"),
    ("ResetProgramCedarEmber", "지급된 리셋권"),
    ("ResetProgramJuniperTide", "한도 도달 리셋"),
    ("ResetAvailableCedarEmber", "남은 리셋권 {count}회 · {expiry}까지"),
    ("ResetAvailableJuniperTide", "지금 리셋 가능"),
    ("ResetHeld", "리셋권은 한도에 도달했을 때 쓸 수 있습니다."),
    ("ResetCooldownCedarEmber", "{time}부터 다시 쓸 수 있습니다."),
    ("ResetCooldownJuniperTide", "{time}부터 가능 · 주 {count}회"),
    ("ResetExhausted", "이번 기간의 리셋권을 모두 썼습니다."),
    ("ResetNone", "현재 사용 가능한 리셋권이 없습니다."),
    ("ResetIneligible", "이 계정에는 현재 리셋권이 제공되지 않습니다."),
    ("ResetUnknown", "이 앱의 연결 방식에서는 리셋권 정보를 제공하지 않습니다. claude.ai 설정 > 사용량에서 확인하세요."),
];

/// StringsVerification.cs:89 `Validate` (empty, duplicate, placeholder, mirrored, equal, missing).
fn validate(actual: &[(String, String)], truth: &[(&str, &str)]) -> Option<String> {
    let mut seen: Vec<&str> = vec![];
    for (name, value) in actual {
        if value.is_empty() {
            return Some(format!("{name} is empty"));
        }
        if seen.contains(&value.as_str()) {
            return Some(format!("{name} duplicates another"));
        }
        seen.push(value);
        let bad_brace = value.split('{').skip(1).any(|part| {
            let inner = part.split('}').next().unwrap_or("");
            !part.contains('}')
                || inner.is_empty()
                || !inner.starts_with(|c: char| c.is_ascii_alphabetic())
                || !inner.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
        });
        if bad_brace || value.matches('}').count() != value.matches('{').count() {
            return Some(format!("{name} has a placeholder that is not {{name}}"));
        }
        match truth.iter().find(|(field, _)| field == name) {
            None => return Some(format!("{name} mirrors nothing in macOS")),
            Some((_, expected)) if expected != value => return Some(format!("{name} differs from macOS: {value}")),
            _ => {}
        }
    }
    truth.iter().find(|(field, _)| !actual.iter().any(|(name, _)| name == field)).map(|(field, _)| format!("missing {field}"))
}

/// StringsVerification.cs:339 `AccountUsageStringsMatchMacOS`.
#[test]
fn account_usage_strings_match_macos() {
    common::korean();
    let actual: Vec<(String, String)> = copy::all().into_iter().map(|(field, value)| (field.to_string(), value)).collect();
    assert_eq!(validate(&actual, &ACCOUNT_USAGE_MACOS), None);
    let broken = |field: &str, value: &str| -> Vec<(String, String)> {
        actual.iter().map(|(name, v)| (name.clone(), if name == field { value.to_string() } else { v.clone() })).collect()
    };
    assert!(validate(&broken("Title", ""), &ACCOUNT_USAGE_MACOS).is_some(), "an empty value must fail");
    assert!(
        validate(&broken("UsedPercentTemplate", "{ percent }% 사용"), &ACCOUNT_USAGE_MACOS).is_some(),
        "a malformed placeholder must fail"
    );
    assert!(
        validate(&broken("ChipChecking", &copy::chip_before_first_run()), &ACCOUNT_USAGE_MACOS).is_some(),
        "a duplicate value must fail"
    );
    assert!(validate(&broken("WindowWeekly", "Weekly"), &ACCOUNT_USAGE_MACOS).is_some(), "a value that differs from macOS must fail");
    let missing: Vec<(String, String)> = actual.iter().filter(|(name, _)| name != "DetailClaude").cloned().collect();
    assert!(validate(&missing, &ACCOUNT_USAGE_MACOS).is_some(), "a missing literal must fail");
}

/// AccountUsageVerification.cs:55 `Secret`: the fixture sign-in reaches only the `Authorization` header of a request to
/// api.anthropic.com — never a snapshot, its text form, a log line or an error.
#[tokio::test]
async fn secret() {
    common::korean();
    let home = temp("usage-secret");
    let path = write_credentials(&home, FIXTURE_TOKEN, None);
    let seen: Arc<Mutex<(Vec<String>, String)>> = Arc::new(Mutex::new((vec![], String::new())));
    let record = seen.clone();
    let http = HttpFn(move |request: &AccountUsageHttpRequest| {
        let mut seen = record.lock().unwrap();
        seen.0.push(request.header("Authorization").unwrap_or("").to_string());
        seen.1.push_str(&format!("GET {} timeout={:?} {request:?}\n", request.url, request.timeout));
        Ok(AccountUsageHttpResponse::new(200, if request.url.path().ends_with("usage") { USAGE_BODY } else { PROFILE_BODY }))
    });
    let loaded = credential_file::read(&text(&home), &env(&[]), instant());
    assert_eq!(loaded.as_ref().map(|c| c.token.as_str()), Some(FIXTURE_TOKEN), "the fixture credential must be read from the file");
    assert!(!format!("{loaded:?}").contains(FIXTURE_TOKEN), "a credential's Debug never shows its token");
    let deadline = ResetReadDeadlineBox::new();
    let snapshot = read_claude(&env(&[]), || loaded.clone(), &http, instant(), &deadline, None).await.expect("read");
    {
        let seen = seen.lock().unwrap();
        assert!(
            seen.0.len() == 4 && seen.0.iter().all(|h| *h == format!("Bearer {FIXTURE_TOKEN}")),
            "the bearer header carries the sign-in for the request only"
        );
        assert!(!seen.1.contains(FIXTURE_TOKEN), "a log line must not contain the sign-in");
    }
    assert!(!format!("{snapshot:?}").contains(FIXTURE_TOKEN), "the snapshot text form must not contain the sign-in");
    assert!(snapshot.windows.len() == 2 && snapshot.plan.as_deref() == Some("max_20x"), "the fixture quota is still mapped");
    for (status, body) in [(401u16, "{}"), (429, "{}"), (500, FIXTURE_TOKEN), (302, "")] {
        let failing = HttpFn(move |_: &AccountUsageHttpRequest| {
            Ok(AccountUsageHttpResponse {
                status,
                body: body.into(),
                retry_after: (status == 429).then(|| "120".into()),
                redirect_location: (status == 302).then(|| "https://evil.test/api/oauth/usage".into()),
            })
        });
        let failed = read_claude(&env(&[]), || loaded.clone(), &failing, instant(), &deadline, None).await;
        let Err(error) = failed else { panic!("status {status} must fail") };
        let UsageError::Failure(failure) = &error else { panic!("status {status}: {error:?}") };
        assert!(
            !failure.to_string().contains(FIXTURE_TOKEN) && !format!("{error:?}").contains(FIXTURE_TOKEN),
            "an error must not contain the sign-in"
        );
    }
    let service = AccountUsageService::new(
        probe_of(|_| {
            Err(UsageError::Failure(AccountUsageFailure::new(AccountUsageFailureKind::Authentication, copy::detail_authentication())))
        }),
        clock_at(instant()),
    );
    let failed = service.read("claude", false).await;
    assert!(!format!("{failed:?}").contains(FIXTURE_TOKEN), "the failure snapshot must not contain the sign-in");
    let outcome = AccountUsageSmokeOutcome {
        chips: 3,
        cards: 3,
        window_rows: 2,
        direct_lookup_default_off: true,
        claude_chip: copy::chip_before_first_run(),
        gemini_note: copy::detail_antigravity_unavailable(),
        restored: true,
    };
    assert!(
        !outcome.json().contains(FIXTURE_TOKEN) && outcome.json().starts_with(r#"{"chips":3,"cards":3,"windowRows":2"#),
        "the smoke result carries counts and copy only"
    );
    assert!(std::fs::read_to_string(&path).unwrap().contains(FIXTURE_TOKEN), "the credentials file is left exactly as it was");
    let _ = std::fs::remove_dir_all(&home);
}

/// AccountUsageVerification.cs:120 `RefusesAnotherHostOrARedirect`.
#[tokio::test]
async fn refuses_another_host_or_a_redirect() {
    common::korean();
    for address in [
        "https://evil.test/api/oauth/usage",
        "http://api.anthropic.com/api/oauth/usage",
        "https://api.anthropic.com.evil.test/api/oauth/usage",
    ] {
        assert!(usage::guard(&url::Url::parse(address).unwrap()).is_err(), "{address} must be refused");
    }
    for address in [
        "https://api.anthropic.com:8443/api/oauth/usage",
        "https://user@api.anthropic.com/api/oauth/usage",
        "https://api.anthropic.com/api/oauth/usage#x",
    ] {
        assert!(
            usage::guard(&url::Url::parse(address).unwrap()).is_err(),
            "{address}: another port, user info or a fragment is refused too"
        );
    }
    let usage_url = usage::endpoint("usage", None).unwrap();
    assert!(usage::guard(&usage_url).is_ok() && usage_url.as_str() == "https://api.anthropic.com/api/oauth/usage", "the only endpoint");
    assert_eq!(ClaudeAccountProbe::TIMEOUT, Duration::from_secs(10), "the timeout is 10 seconds");
    for query in ["at_wall=1", "cedar_ember=1", "at_wall=1&skip_spend=0", "cedar_ember=1&extra=true"] {
        let address = format!("https://api.anthropic.com/api/oauth/usage?{query}");
        assert!(usage::guard(&url::Url::parse(&address).unwrap()).is_err(), "usage?{query} must be refused before send");
    }
    for query in ["at_wall=1&skip_spend=1", "cedar_ember=1&skip_spend=1"] {
        assert!(
            usage::guard(&url::Url::parse(&format!("https://api.anthropic.com/api/oauth/usage?{query}")).unwrap()).is_ok(),
            "{query} is allowed"
        );
    }
    let deadline = ResetReadDeadlineBox::new();
    for (status, location) in [(302u16, Some("https://evil.test/api/oauth/usage")), (301, None), (200, Some("https://evil.test/"))] {
        let attempts = Arc::new(AtomicUsize::new(0));
        let counted = attempts.clone();
        let http = HttpFn(move |request: &AccountUsageHttpRequest| {
            counted.fetch_add(1, Ordering::SeqCst);
            usage::guard(&request.url).map_err(HttpFailure::Refused)?;
            Ok(AccountUsageHttpResponse {
                status,
                body: USAGE_BODY.into(),
                retry_after: None,
                redirect_location: location.map(str::to_string),
            })
        });
        let read = read_claude(&env(&[]), || Some(credential()), &http, instant(), &deadline, None).await;
        assert!(
            matches!(&read, Err(UsageError::Failure(f)) if f.kind == AccountUsageFailureKind::Network)
                && attempts.load(Ordering::SeqCst) == 1,
            "the redirect is refused after one attempt, never followed: {read:?}"
        );
    }
    for key in ClaudeAccountProbe::BLOCKED_ENVIRONMENT {
        let loaded = Arc::new(AtomicUsize::new(0));
        let flag = loaded.clone();
        let http = HttpFn(|_: &AccountUsageHttpRequest| Ok(AccountUsageHttpResponse::new(200, USAGE_BODY)));
        let read = read_claude(
            &env(&[(key, "1")]),
            move || {
                flag.fetch_add(1, Ordering::SeqCst);
                Some(credential())
            },
            &http,
            instant(),
            &deadline,
            None,
        )
        .await;
        assert!(
            matches!(&read, Err(UsageError::Failure(f)) if f.kind == AccountUsageFailureKind::Unavailable)
                && loaded.load(Ordering::SeqCst) == 0,
            "{key} is refused before the credentials file is read"
        );
    }
    assert!(
        !ClaudeAccountProbe::custom_authentication(&env(&[("ANTHROPIC_API_KEY", "false"), ("CLAUDE_CODE_USE_BEDROCK", "0")])),
        "0 and false are off"
    );
}

/// AccountUsageVerification.cs:187 `DashboardUsageBarsAreTheLeadingWindows`.
#[test]
fn dashboard_usage_bars_are_the_leading_windows() {
    common::korean();
    let claude = vec![
        AccountUsageWindow::new("spend_limit", 50.0),
        AccountUsageWindow::new("seven_day", 95.0),
        AccountUsageWindow::new("daily", 3.0),
        AccountUsageWindow::new("five_hour", 12.5),
    ];
    let rows = usage::leading_bars_of(&[
        ("claude".into(), claude.clone()),
        ("codex".into(), vec![AccountUsageWindow::new("spend_limit", 80.0)]),
        ("gemini".into(), vec![]),
    ]);
    assert!(rows.len() == 1 && rows[0].provider == "claude", "only a provider with a session or weekly window gets a usage row");
    let bars = &rows[0].bars;
    assert!(
        bars.len() == 2 && bars[0].label == usage::window_label("five_hour") && bars[1].label == usage::window_label("seven_day"),
        "the bars are the session window then the weekly one"
    );
    assert!(
        bars[0].percent == format!("{}%", usage::percent(12.5)) && bars[1].percent == "95%",
        "the percents are worded as the chips word them"
    );
    assert!((bars[0].fraction - 0.125).abs() < 1e-9 && !bars[0].warning && bars[1].warning, "the fill follows the percent and 90% warns");
    let chip_windows: Vec<String> = usage::leading(&claude).iter().map(|w| usage::window_label(&w.kind)).collect();
    assert_eq!(
        chip_windows,
        bars.iter().map(|b| b.label.clone()).collect::<Vec<_>>(),
        "the dashboard bars are exactly the chips' leading windows"
    );
}

fn codex_fixture() -> AccountUsageSnapshot {
    CodexAccountProbe::map(
        &json!({"account": {"type": "chatgpt", "email": "fixture@example.test"}}),
        &json!({"rateLimitsByLimitId": {"codex": {"planType": "plus", "primary": {"usedPercent": 8, "windowDurationMins": 300}, "secondary": {"usedPercent": 61, "windowDurationMins": 10080}}}}),
    )
    .unwrap()
}

/// AccountUsageVerification.cs:203 `DirectLookupIsOffByDefault`: out of the box nothing is looked up directly — Claude shows what
/// the CLI reported during a run, Codex is asked through its own CLI and Antigravity says why it has none.
#[tokio::test]
async fn direct_lookup_is_off_by_default() {
    common::korean();
    assert!(
        !AppSnapshot::default().claude_direct_usage_lookup_enabled && AppSnapshot::default().version == 1,
        "the saved switch is off by default"
    );
    let legacy = snapshot(json!({"version": 1, "theme": "dark"}));
    assert!(!legacy.claude_direct_usage_lookup_enabled && legacy.version == 1, "a saved file without the field keeps the default");
    let reported = json!({
        "provider": "claude", "source": "claude.mods+stream-json", "tokenScope": "response", "updatedAt": o(instant()),
        "rateLimits": [{"kind": "five_hour", "percentUsed": 12.5, "resetsAt": o(instant() + TimeDelta::hours(3))}, {"kind": "seven_day", "percentUsed": 44}],
        "rateLimitsUpdatedAt": o(instant() - TimeDelta::minutes(1)),
    });
    let state = json!({
        "workspaces": [{"id": "w1", "path": "/fixture"}],
        "sessions": [
            {"id": "a", "workspaceId": "w1", "kind": "claude", "provider": "claude", "sessionUsage": reported},
            {"id": "b", "workspaceId": "w1", "kind": "claude", "provider": "codex"},
            {"id": "c", "workspaceId": "w1", "kind": "claude", "provider": "gemini"},
            {"id": "d", "workspaceId": "w1", "kind": "shell", "provider": "claude"},
        ],
    });
    let asked = Arc::new(Mutex::new(Vec::<String>::new()));
    let record = asked.clone();
    let service = AccountUsageService::new(
        probe_of(move |provider| {
            record.lock().unwrap().push(provider.clone());
            if provider == "gemini" { Err(unavailable(copy::detail_antigravity_unavailable())) } else { Ok(codex_fixture()) }
        }),
        clock_at(instant()),
    );
    let status = AccountUsageStatus::new(service.clone(), clock_at(instant()));
    status.update(&snapshot(state.clone()));
    assert_eq!(status.providers(), ["claude", "codex", "gemini"], "a chip per provider with a local AI pane, in the macOS order");
    assert_eq!(status.targets(), ["codex", "gemini"], "Claude is never asked directly while the switch is off");
    status.refresh(false).await;
    assert_eq!(*asked.lock().unwrap(), ["codex", "gemini"], "the direct Claude lookup did not run");
    let chips = status.chips();
    assert_eq!(chips.len(), 3, "three chips");
    assert_eq!(chips[0].text, "세션 13% 주간 44%", "the Claude chip shows the limits the CLI reported");
    assert_eq!(chips[1].text, "세션 8% 주간 61%", "the Codex chip comes from its own CLI");
    assert_eq!(chips[2].text, copy::chip_empty(), "Antigravity has no window to show");
    let cards = status.cards();
    assert_eq!(cards[2].detail.as_deref(), Some(copy::detail_antigravity_unavailable().as_str()), "Antigravity says why");
    assert!(cards[0].checked_at.as_deref().is_some_and(|c| !c.is_empty()), "the popover shows the time of the last check");
    assert!(cards[1].windows.len() == 2 && cards[1].windows[0].used == "8% 사용", "the popover shows the used percentage");
    let orphan = AccountUsageStatus::new(service.clone(), clock_at(instant()));
    orphan.update(&snapshot(json!({"sessions": [{"id": "a", "workspaceId": "w1", "kind": "claude", "provider": "claude"}]})));
    assert!(orphan.providers().is_empty(), "a pane without a registered workspace claims no account");
    let mut on = state;
    on["claudeDirectUsageLookupEnabled"] = json!(true);
    status.update(&snapshot(on));
    assert_eq!(status.targets(), ["claude", "codex", "gemini"], "Claude is asked only after the user switches the lookup on");
    status.set_direct_claude_lookup(false);
    assert!(status.snapshot("claude").is_some(), "a session's report stays when the switch goes off");
}

/// AccountUsageVerification.cs:273 `ClaudeReadsQuotaAndProfileFromTheCredentialsFile`.
#[tokio::test]
async fn claude_reads_quota_and_profile_from_the_credentials_file() {
    common::korean();
    let home = temp("usage-claude");
    write_credentials(&home, FIXTURE_TOKEN, None);
    let urls = Arc::new(Mutex::new(Vec::<String>::new()));
    let record = urls.clone();
    let http = HttpFn(move |request: &AccountUsageHttpRequest| {
        record.lock().unwrap().push(request.path_and_query());
        let budget = if request.url.query().is_some() { ClaudeAccountProbe::RESET_TIMEOUT } else { ClaudeAccountProbe::TIMEOUT };
        assert_eq!(request.timeout, budget, "the usage and profile reads take 10 seconds, the entitlement reads the CLI's 5");
        Ok(AccountUsageHttpResponse::new(200, if request.url.path().ends_with("usage") { USAGE_BODY } else { PROFILE_BODY }))
    });
    let home_text = text(&home);
    let deadline = ResetReadDeadlineBox::new();
    let snapshot = read_claude(&env(&[]), || credential_file::read(&home_text, &env(&[]), instant()), &http, instant(), &deadline, None)
        .await
        .expect("read");
    assert_eq!(
        *urls.lock().unwrap(),
        [
            "/api/oauth/usage",
            "/api/oauth/profile",
            "/api/oauth/usage?cedar_ember=1&skip_spend=1",
            "/api/oauth/usage?at_wall=1&skip_spend=1"
        ],
        "only the quota, profile and the two entitlement queries are asked"
    );
    assert!(snapshot.resets.iter().map(|r| r.state.as_str()).eq(["ineligible", "ineligible"]), "a body without reset fields is ineligible");
    assert!(snapshot.status == "available" && snapshot.windows.len() == 2, "the quota windows are mapped");
    assert!(snapshot.windows[0].kind == "session" && (snapshot.windows[0].used_percent - 42.5).abs() < 0.001, "the five hour window");
    assert!(snapshot.windows[1].kind == "weekly" && snapshot.windows[1].window_minutes == Some(10080), "the seven day window");
    assert!(
        snapshot.account_label.as_deref() == Some("fixture@example.test") && snapshot.plan.as_deref() == Some("max_20x"),
        "label and plan from the profile"
    );
    assert_eq!(snapshot.detail, copy::detail_claude(), "the Claude detail sentence");

    let expired =
        json!({"claudeAiOauth": {"accessToken": FIXTURE_TOKEN, "expiresAt": (instant() - TimeDelta::hours(1)).timestamp_millis()}})
            .to_string();
    assert!(credential_file::parse(&expired, instant()).is_none(), "an expired sign-in is refused");
    assert!(
        credential_file::parse(r#"{"claudeAiOauth":{"accessToken":"x","scopes":["user:inference"]}}"#, instant()).is_none(),
        "no user:profile"
    );
    assert!(credential_file::parse(&"x".repeat(credential_file::MAXIMUM_BYTES + 1), instant()).is_none(), "an oversized file is refused");
    assert!(credential_file::parse("not json", instant()).is_none(), "a malformed file is refused");
    assert!(credential_file::read(&text(&home.join("missing")), &env(&[]), instant()).is_none(), "a missing file is refused");
    let malformed = ClaudeAccountProbe::map(
        &json!({"five_hour": {"utilization": "nope"}, "seven_day": {"utilization": 140}, "seven_day_sonnet": {"utilization": 3, "resets_at": "뭐"}}),
        &Value::Null,
        None,
    );
    assert!(
        malformed.windows.len() == 1 && malformed.windows[0].kind == "seven_day_Sonnet",
        "an unusable utilization is dropped, never 0%"
    );
    assert!(malformed.windows[0].resets_at.is_none(), "an unparseable reset date is dropped");
    let none = ClaudeAccountProbe::map(&json!({}), &Value::Null, None);
    assert!(none.status == "unavailable" && none.detail == copy::detail_claude_no_windows(), "an account without windows says so");
    let _ = std::fs::remove_dir_all(&home);
}

/// AccountUsageVerification.cs:325 `FailureKeepsTheLastKnownValueAndBacksOff`.
#[tokio::test]
async fn failure_keeps_the_last_known_value_and_backs_off() {
    common::korean();
    let now = Arc::new(Mutex::new(instant()));
    let mode = Arc::new(Mutex::new("ok"));
    let calls = Arc::new(AtomicUsize::new(0));
    let (m, c) = (mode.clone(), calls.clone());
    let probe = probe_of(move |provider| {
        c.fetch_add(1, Ordering::SeqCst);
        match *m.lock().unwrap() {
            "ok" => Ok(AccountUsageSnapshot {
                windows: vec![AccountUsageWindow::new("session", 30.0)],
                status: "available".into(),
                detail: copy::detail_claude(),
                ..AccountUsageSnapshot::new(&provider)
            }),
            "limited" => Err(UsageError::Failure(AccountUsageFailure {
                kind: AccountUsageFailureKind::RateLimited,
                detail: copy::detail_rate_limited(),
                retry_after_seconds: usage::retry_interval(Some("600"), instant()),
            })),
            _ => Err(UsageError::Failure(AccountUsageFailure::new(AccountUsageFailureKind::Authentication, copy::detail_authentication()))),
        }
    });
    let clock_now = now.clone();
    let service = AccountUsageService::new(probe, Arc::new(move || *clock_now.lock().unwrap()));
    let advance = |seconds: i64| *now.lock().unwrap() += TimeDelta::seconds(seconds);
    let good = service.read("claude", true).await;
    assert!(good.status == "available" && good.fetched_at.is_some(), "the first read succeeds");
    advance(30);
    *mode.lock().unwrap() = "limited";
    let stale = service.read("claude", true).await;
    assert!(stale.status == "stale" && stale.windows.len() == 1, "the last known windows survive a failure");
    assert_eq!(
        stale.detail,
        format!("{}{}", copy::detail_rate_limited(), copy::detail_last_known_suffix()),
        "marked as the last known one"
    );
    let before = calls.load(Ordering::SeqCst);
    advance(60);
    *mode.lock().unwrap() = "ok";
    let held = service.read("claude", true).await;
    assert!(calls.load(Ordering::SeqCst) == before && held.status == "stale", "a forced refresh cannot bypass the Retry-After cooldown");
    advance(600);
    let recovered = service.read("claude", true).await;
    assert!(calls.load(Ordering::SeqCst) == before + 1 && recovered.status == "available", "the read resumes after the back-off");
    advance(600);
    *mode.lock().unwrap() = "auth";
    let cleared = service.read("claude", true).await;
    assert!(
        cleared.status == "unavailable" && cleared.windows.is_empty() && cleared.detail == copy::detail_authentication(),
        "an authentication failure clears the old account"
    );
    assert_eq!(usage::retry_interval(Some("5"), instant()), usage::MINIMUM_BACKOFF_SECONDS, "Retry-After is clamped up to the minimum");
    assert_eq!(usage::retry_interval(Some("999999"), instant()), usage::MAXIMUM_BACKOFF_SECONDS, "Retry-After is clamped down to a day");
    let http_date = (instant() + TimeDelta::seconds(300)).format("%a, %d %b %Y %H:%M:%S GMT").to_string();
    let interval = usage::retry_interval(Some(&http_date), instant());
    assert!(interval > 290.0 && interval <= 300.0, "an HTTP date Retry-After is understood: {http_date} → {interval}");
    assert_eq!(usage::retry_interval(None, instant()), usage::DEFAULT_BACKOFF_SECONDS, "none waits the default");
}

/// A line-delimited JSON-RPC channel that answers from a fixture script, then waits forever.
struct FakeStdio {
    sent: Arc<Mutex<Vec<String>>>,
    replies: Vec<String>,
    closed: Arc<AtomicUsize>,
}

impl AccountUsageStdio for FakeStdio {
    fn write_line<'a>(&'a mut self, line: &'a str) -> BoxFuture<'a, Result<(), String>> {
        self.sent.lock().unwrap().push(line.to_string());
        Box::pin(async { Ok(()) })
    }
    fn read_line(&mut self) -> BoxFuture<'_, Result<Option<String>, String>> {
        Box::pin(async move {
            if self.replies.is_empty() {
                std::future::pending::<()>().await;
            }
            Ok(Some(self.replies.remove(0)))
        })
    }
    fn close(&mut self) -> BoxFuture<'_, ()> {
        self.closed.fetch_add(1, Ordering::SeqCst);
        Box::pin(async {})
    }
}

/// AccountUsageVerification.cs:374 `CodexIsAskedThroughItsOwnAppServer`.
#[tokio::test]
async fn codex_is_asked_through_its_own_app_server() {
    let sent = Arc::new(Mutex::new(vec![]));
    let closed = Arc::new(AtomicUsize::new(0));
    let replies = [
        r#"{"id":1,"result":{"userAgent":"fixture"}}"#,
        r#"{"id":2,"result":{"account":{"type":"chatgpt","email":"fixture@example.test","planType":"plus"}}}"#,
        r#"{"id":3,"result":{"rateLimitsByLimitId":{"codex":{"planType":"pro","primary":{"usedPercent":8,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"usedPercent":61,"windowDurationMins":4320}}}}}"#,
    ];
    let channel = FakeStdio { sent: sent.clone(), replies: replies.iter().map(|r| r.to_string()).collect(), closed: closed.clone() };
    let snapshot = CodexAccountProbe::read(Box::new(channel), Duration::from_secs(5)).await.expect("read");
    let sent = sent.lock().unwrap().clone();
    assert_eq!(sent.len(), 4, "initialize, initialized, account/read and rateLimits/read only");
    assert!(
        sent.iter().all(|line| !line.contains("thread") && !line.contains("tool")),
        "no thread is started or resumed and no tool is called"
    );
    assert!(sent[2].contains("\"refreshToken\":false"), "the CLI's own sign-in is never refreshed by the app");
    assert!(
        snapshot.provider == "codex"
            && snapshot.account_label.as_deref() == Some("fixture@example.test")
            && snapshot.plan.as_deref() == Some("pro"),
        "label and plan"
    );
    assert!(
        snapshot.windows[0].kind == "session" && snapshot.windows[0].resets_at.is_some(),
        "the 300 minute window is the session window"
    );
    assert_eq!(snapshot.windows[1].kind, "4320m", "a nonstandard period is kept, never relabelled as a week");
    assert_eq!(closed.load(Ordering::SeqCst), 1, "the channel is closed");
    for lines in [vec![r#"{"id":1,"result":{}}"#, r#"{"id":2,"result":{"account":{"type":"apikey"}}}"#], vec![r#"{"id":1,"result":{}}"#]] {
        let channel = FakeStdio {
            sent: Arc::new(Mutex::new(vec![])),
            replies: lines.iter().map(|r| r.to_string()).collect(),
            closed: closed.clone(),
        };
        let read = CodexAccountProbe::read(Box::new(channel), Duration::from_millis(200)).await;
        assert!(matches!(read, Err(UsageError::Failure(_))), "an unusable app-server answer fails instead of hanging: {read:?}");
    }
}

/// AccountUsageVerification.cs:404 `ClosingTheAppCancelsPendingReads`.
#[tokio::test]
async fn closing_the_app_cancels_pending_reads() {
    common::korean();
    let entered = Arc::new(tokio::sync::Notify::new());
    let calls = Arc::new(AtomicUsize::new(0));
    let (e, c) = (entered.clone(), calls.clone());
    let probe: UsageProbe = Arc::new(move |provider: String| -> BoxFuture<'static, Result<AccountUsageSnapshot, UsageError>> {
        c.fetch_add(1, Ordering::SeqCst);
        let e = e.clone();
        Box::pin(async move {
            e.notify_one();
            std::future::pending::<()>().await;
            Ok(AccountUsageSnapshot {
                windows: vec![AccountUsageWindow::new("session", 1.0)],
                status: "available".into(),
                ..AccountUsageSnapshot::new(&provider)
            })
        })
    });
    let service = AccountUsageService::new(probe, clock_at(instant()));
    let status = AccountUsageStatus::new(service.clone(), clock_at(instant()));
    status.update(&snapshot(json!({"workspaces": [{"id": "w", "path": "/fixture"}], "sessions": [{"id": "a", "workspaceId": "w", "kind": "claude", "provider": "codex"}]})));
    let first = tokio::spawn(service.read("codex", false));
    let second = tokio::spawn(service.read("codex", false));
    tokio::time::timeout(Duration::from_secs(5), entered.notified()).await.expect("the read started");
    status.close().await;
    let cancelled = tokio::time::timeout(Duration::from_secs(5), first).await.unwrap().unwrap();
    let joined = second.await.unwrap();
    assert!(calls.load(Ordering::SeqCst) == 1 && joined == cancelled, "one read at a time per provider");
    assert!(cancelled.status == "cancelled" && cancelled.detail == copy::detail_cancelled(), "a pending read ends as cancelled");
    let after_close = service.read("codex", false).await;
    assert!(after_close.status == "cancelled" && service.cached("codex").is_none(), "nothing is read or kept after the app closes");
    assert!(status.snapshot("codex").is_none(), "the rows are dropped");
}

// ── 리셋권 ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn quoted(value: Option<String>) -> String {
    value.map(|v| format!("\"{v}\"")).unwrap_or_else(|| "null".into())
}

fn grant(left: i32, ends: DateTime<Utc>, usable_now: bool, requires_limit: bool) -> String {
    format!(
        r#"{{"id":"grant-fixture","label":"fixture","resets_total":5,"resets_left":{left},"starts_at":{},"ends_at":{},"clears":[],"paused":false,"usable_now":{usable_now},"use_requires_limit":{requires_limit},"percent_used":{{}}}}"#,
        quoted(Some(o(instant() - TimeDelta::days(1)))),
        quoted(Some(o(ends)))
    )
}

fn cedar(
    grant: Option<String>,
    at_limit: bool,
    cooldown: Option<DateTime<Utc>>,
    exhausted: &str,
    eligible: bool,
    reason: Option<&str>,
) -> String {
    format!(
        r#"{{"cedar_ember":{{"eligible":{eligible},"ineligible_reason":{},"at_limit":{at_limit},"exhausted":{exhausted},"grants":[{}],"next_grant_id":{},"weekly_resets_at":null,"cooldown_until":{}}}}}"#,
        quoted(reason.map(str::to_string)),
        grant.clone().unwrap_or_default(),
        quoted(grant.map(|_| "grant-fixture".to_string())),
        quoted(cooldown.map(o))
    )
}

fn juniper(available: bool, next: Option<DateTime<Utc>>, per_week: i32) -> String {
    format!(
        r#"{{"juniper_tide":{{"in_experiment":true,"ineligible_reason":null,"available":{available},"next_available_at":{},"weekly_resets_at":{},"resets_per_week":{per_week},"tenure_bucket":"established","billing_path":"subscription","billing_period":"monthly","extra_usage_state":"off"}}}}"#,
        quoted(next.map(o)),
        quoted(Some(o(instant() + TimeDelta::days(5))))
    )
}

/// `ResetScenario`: what each entitlement query answers and what the rows must become (field names read from the CLI 2.1.280 binary;
/// only the values are made up).
struct Scenario {
    name: &'static str,
    cedar: (u16, String),
    juniper: (u16, String),
    cedar_state: &'static str,
    juniper_state: &'static str,
}

fn scenarios() -> Vec<Scenario> {
    let (future, past) = (instant() + TimeDelta::days(1), instant() - TimeDelta::days(1));
    vec![
        Scenario {
            name: "available",
            cedar: (200, cedar(Some(grant(3, future, true, false)), true, None, "[]", true, None)),
            juniper: (200, juniper(true, None, 2)),
            cedar_state: "available",
            juniper_state: "available",
        },
        Scenario {
            name: "held",
            cedar: (200, cedar(Some(grant(2, future, false, true)), false, None, "[]", true, None)),
            juniper: (200, juniper(false, Some(future), 2)),
            cedar_state: "held",
            juniper_state: "cooldown",
        },
        Scenario {
            name: "cooldown",
            cedar: (200, cedar(Some(grant(1, future, false, false)), true, Some(future), "[]", true, None)),
            juniper: (200, juniper(false, Some(past), 0)),
            cedar_state: "cooldown",
            juniper_state: "none",
        },
        Scenario {
            name: "exhausted",
            cedar: (200, cedar(Some(grant(0, future, false, false)), true, Some(past), r#"["spent"]"#, true, None)),
            juniper: (200, juniper(false, Some(past), 3)),
            cedar_state: "exhausted",
            juniper_state: "exhausted",
        },
        Scenario {
            name: "none",
            cedar: (200, cedar(None, false, None, "[]", true, None)),
            juniper: (200, juniper(false, None, 0)),
            cedar_state: "none",
            juniper_state: "none",
        },
        Scenario {
            name: "ineligible",
            cedar: (200, cedar(None, false, None, "[]", false, Some("not_in_experiment"))),
            juniper: (404, String::new()),
            cedar_state: "ineligible",
            juniper_state: "ineligible",
        },
        Scenario {
            name: "unknown",
            cedar: (503, String::new()),
            juniper: (200, r#"{"juniper_tide":42}"#.into()),
            cedar_state: "unknown",
            juniper_state: "unknown",
        },
    ]
}

fn scenario_http(scenario: &Scenario, posts: Arc<AtomicUsize>) -> impl HttpsGet + use<> {
    let (cedar, juniper) = (scenario.cedar.clone(), scenario.juniper.clone());
    HttpFn(move |request: &AccountUsageHttpRequest| {
        usage::guard(&request.url).map_err(HttpFailure::Refused)?;
        if request.method != "GET" {
            posts.fetch_add(1, Ordering::SeqCst);
        }
        assert_eq!(
            request.header("Authorization"),
            Some(format!("Bearer {FIXTURE_TOKEN}").as_str()),
            "the sign-in only rides the Authorization header"
        );
        Ok(match request.url.query() {
            Some("cedar_ember=1&skip_spend=1") => AccountUsageHttpResponse::new(cedar.0, &cedar.1),
            Some("at_wall=1&skip_spend=1") => AccountUsageHttpResponse::new(juniper.0, &juniper.1),
            _ => AccountUsageHttpResponse::new(200, if request.url.path().ends_with("profile") { PROFILE_BODY } else { USAGE_BODY }),
        })
    })
}

async fn one_state(name: &str) {
    common::korean();
    let all = scenarios();
    let scenario = all.iter().find(|s| s.name == name).unwrap();
    let posts = Arc::new(AtomicUsize::new(0));
    let http = scenario_http(scenario, posts.clone());
    let deadline = ResetReadDeadlineBox::new();
    let snapshot =
        read_claude(&env(&[]), || Some(credential()), &http, instant() + TimeDelta::seconds(10), &deadline, None).await.expect("read");
    assert_eq!(posts.load(Ordering::SeqCst), 0, "usageReset {name}: no POST exists in this app");
    let state = |program: &str| snapshot.resets.iter().find(|r| r.program == program).map(|r| r.state.clone()).unwrap();
    assert_eq!(state(ResetProgram::CEDAR_EMBER), scenario.cedar_state, "cedar_ember state in {name}");
    assert_eq!(state(ResetProgram::JUNIPER_TIDE), scenario.juniper_state, "juniper_tide state in {name}");
    for row in ClaudeResetEntitlements::rows(Some(&snapshot), true) {
        assert!(!row.text.is_empty() && !row.text.starts_with("usage.reset."), "state {name} renders through a shared key that resolved");
    }
}

/// AccountUsageVerification.cs:462 `UsageResetUnknown`.
#[tokio::test]
async fn usage_reset_unknown() {
    one_state("unknown").await;
}

/// AccountUsageVerification.cs:463 `UsageResetIneligible`.
#[tokio::test]
async fn usage_reset_ineligible() {
    one_state("ineligible").await;
}

/// AccountUsageVerification.cs:464 `UsageResetNone`.
#[tokio::test]
async fn usage_reset_none() {
    one_state("none").await;
}

/// AccountUsageVerification.cs:465 `UsageResetExhausted`.
#[tokio::test]
async fn usage_reset_exhausted() {
    one_state("exhausted").await;
}

/// AccountUsageVerification.cs:466 `UsageResetCooldown`.
#[tokio::test]
async fn usage_reset_cooldown() {
    one_state("cooldown").await;
}

/// AccountUsageVerification.cs:467 `UsageResetHeld`.
#[tokio::test]
async fn usage_reset_held() {
    one_state("held").await;
}

/// AccountUsageVerification.cs:468 `UsageResetAvailable`.
#[tokio::test]
async fn usage_reset_available() {
    one_state("available").await;
}

/// AccountUsageVerification.cs:569 `UsageResetRendersSevenStates`.
#[tokio::test]
async fn usage_reset_renders_seven_states() {
    common::korean();
    let mut seen = std::collections::HashSet::new();
    let posts = Arc::new(AtomicUsize::new(0));
    let mut clock = instant();
    let deadline = ResetReadDeadlineBox::new();
    for scenario in scenarios() {
        let http = scenario_http(&scenario, posts.clone());
        clock += TimeDelta::seconds(61);
        let snapshot = read_claude(&env(&[]), || Some(credential()), &http, clock, &deadline, None).await.expect("read");
        assert!(
            snapshot.status == "available" && snapshot.windows.len() == 2,
            "the base usage windows are untouched by the entitlement read"
        );
        assert!(snapshot.resets.iter().map(|r| r.program.as_str()).eq(ResetProgram::ALL), "one row per programme, in order");
        let granted = snapshot.resets.iter().find(|r| r.program == ResetProgram::CEDAR_EMBER).unwrap().clone();
        let at_wall = snapshot.resets.iter().find(|r| r.program == ResetProgram::JUNIPER_TIDE).unwrap().clone();
        assert_eq!(granted.state, scenario.cedar_state, "cedar_ember in {}", scenario.name);
        assert_eq!(at_wall.state, scenario.juniper_state, "juniper_tide in {}", scenario.name);
        seen.insert(granted.state.clone());
        seen.insert(at_wall.state.clone());
        for row in ClaudeResetEntitlements::rows(Some(&snapshot), true) {
            assert!(
                !row.text.is_empty() && !row.text.starts_with("usage.reset.") && !row.label.is_empty(),
                "every state renders from a resolved key"
            );
        }
        assert!(
            ClaudeResetEntitlements::LINK_KEY == "usage.reset.link" && !ClaudeResetEntitlements::link_label().is_empty(),
            "the link exists in every state"
        );
        assert!(ClaudeResetEntitlements::LINK_TARGET.starts_with("https://claude.ai/"), "the link goes to claude.ai Settings > Usage");
        match scenario.name {
            "available" => {
                assert!(
                    granted.copy_key() == "usage.reset.available.cedarEmber"
                        && granted.remaining_count == Some(3)
                        && granted.expires_at.is_some(),
                    "granted line"
                );
                assert!(ClaudeResetEntitlements::line(&granted).contains('3'), "the remaining count is shown");
                assert!(
                    at_wall.copy_key() == "usage.reset.available.juniperTide" && at_wall.resets_per_week == Some(2),
                    "the at-wall available line"
                );
            }
            "held" => {
                assert_eq!(granted.copy_key(), "usage.reset.held", "the held sentence");
                assert!(
                    at_wall.copy_key() == "usage.reset.cooldown.juniperTide"
                        && at_wall.next_available_at.is_some()
                        && at_wall.resets_per_week == Some(2),
                    "at-wall cooldown"
                );
                assert!(ClaudeResetEntitlements::line(&at_wall).contains('2'), "the weekly count is shown");
            }
            "cooldown" => assert!(granted.copy_key() == "usage.reset.cooldown.cedarEmber" && at_wall.copy_key() == "usage.reset.none"),
            "exhausted" => assert!(granted.copy_key() == "usage.reset.exhausted" && at_wall.copy_key() == "usage.reset.exhausted"),
            "none" => assert!(granted.copy_key() == "usage.reset.none" && at_wall.copy_key() == "usage.reset.none"),
            "ineligible" => assert!(granted.copy_key() == "usage.reset.ineligible" && at_wall.copy_key() == "usage.reset.ineligible"),
            _ => assert!(
                granted.copy_key() == "usage.reset.unknown" && at_wall.copy_key() == "usage.reset.unknown",
                "unknown blames this app's connection"
            ),
        }
        let visible = format!("{snapshot:?}");
        assert!(
            !visible.contains(FIXTURE_TOKEN) && !visible.contains("grant-fixture"),
            "no sign-in and no grant id reaches the presentation data"
        );
    }
    assert!(ResetState::ALL.iter().all(|s| seen.contains(*s)) && seen.len() == 7, "all seven states are covered: {seen:?}");
    assert_eq!(posts.load(Ordering::SeqCst), 0, "no POST exists in this app");

    // The clock, not the wall time, decides when a grant window has closed.
    let closing = clock + TimeDelta::seconds(200);
    let expiring_cedar = cedar(Some(grant(4, closing, true, false)), true, None, "[]", true, None);
    let expiring_juniper = juniper(true, None, 1);
    let expiring = HttpFn(move |request: &AccountUsageHttpRequest| {
        Ok(match request.url.query() {
            Some("cedar_ember=1&skip_spend=1") => AccountUsageHttpResponse::new(200, &expiring_cedar),
            Some("at_wall=1&skip_spend=1") => AccountUsageHttpResponse::new(200, &expiring_juniper),
            _ => AccountUsageHttpResponse::new(200, if request.url.path().ends_with("profile") { PROFILE_BODY } else { USAGE_BODY }),
        })
    });
    let before = read_claude(&env(&[]), || Some(credential()), &expiring, clock, &deadline, None).await.unwrap();
    assert_eq!(before.resets[0].state, "available", "the grant is usable while its window is open");
    let after = read_claude(&env(&[]), || Some(credential()), &expiring, clock + TimeDelta::seconds(300), &deadline, None).await.unwrap();
    assert_eq!(after.resets[0].state, "none", "the injected clock, not the wall clock, closes the grant window");
    assert!(ClaudeResetEntitlements::rows(Some(&before), false).is_empty(), "the 리셋권 rows are hidden while the direct lookup is off");
    assert!(ClaudeResetEntitlements::rows(None, false).is_empty(), "nothing is teased before the switch is on");
    let shown = ClaudeResetEntitlements::rows(None, true);
    assert!(
        shown.len() == 2 && shown.iter().all(|r| r.state == "unknown"),
        "a relaunch with nothing read yet is unknown, never a stale number"
    );
}

/// AccountUsageVerification.cs:547 `UsageResetSmokeRendersAvailableAndUnknown`.
#[tokio::test]
async fn usage_reset_smoke_renders_available_and_unknown() {
    common::korean();
    let result = claude_reset_smoke::run().await;
    assert_eq!(result.cedar_ember_state, ResetState::AVAILABLE, "smoke: cedar_ember renders available from the fixture");
    assert_eq!(result.juniper_tide_state, ResetState::UNKNOWN, "smoke: juniper_tide renders unknown when the handler answers 503");
    assert_eq!(result.fake_transport_post_count, 0, "smoke: no POST exists in this app");
    assert!(
        result.requests.iter().map(String::as_str).eq(claude_reset_smoke::EXPECTED_REQUESTS),
        "smoke: the GET allow-list with skip_spend=1"
    );
    assert!(result.lines.len() == 2 && result.lines.iter().all(|l| !l.is_empty() && !l.starts_with("usage.reset.")), "both rows resolve");
    assert!(result.passed, "smoke: the result the smoke writes records passed");
    let recorded = format!("{result:?}");
    assert!(
        !recorded.contains(claude_reset_smoke::FIXTURE_TOKEN) && !recorded.contains("grant-fixture"),
        "no token or grant id reaches the result"
    );
}

/// AccountUsageVerification.cs:520 `UsageResetSmokeIsWiredIntoBothPlatforms`, its Core half: no POST exists anywhere on the account
/// usage surface (this crate's, C#'s and the Mac's sources). That the app's smoke runs it is the smoke stage's (W6-C phase 2).
#[test]
fn usage_reset_smoke_is_wired_into_both_platforms() {
    let manifest = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let native = manifest.join("..").join("..").join("..");
    let mut files: Vec<std::path::PathBuf> = vec![manifest.join("src").join("usage.rs")];
    files.extend(std::fs::read_dir(manifest.join("src").join("usage")).unwrap().flatten().map(|e| e.path()));
    let listed = |folder: std::path::PathBuf, prefixes: &[&str]| -> Vec<std::path::PathBuf> {
        std::fs::read_dir(folder)
            .map(|entries| {
                entries
                    .flatten()
                    .map(|e| e.path())
                    .filter(|p| p.file_name().and_then(|n| n.to_str()).is_some_and(|n| prefixes.iter().any(|prefix| n.starts_with(prefix))))
                    .collect()
            })
            .unwrap_or_default()
    };
    files.extend(listed(native.join("windows").join("MightyCode.Core"), &["AccountUsage"]));
    files.extend(listed(native.join("macos").join("Sources").join("MightyCore"), &["AccountUsage", "AccountReset"]));
    assert!(files.len() > 6, "the usage sources were found: {files:?}");
    for file in files {
        assert!(
            !std::fs::read_to_string(&file).unwrap().contains("\"POST\""),
            "no POST exists on the account usage surface: {}",
            file.display()
        );
    }
}

/// AccountUsageVerification.cs:687 `UsageResetFirstResponseLog`: the first successful reset response's key paths and value types
/// are logged once per programme, never a value.
#[tokio::test]
async fn usage_reset_first_response_log() {
    let home = temp("usage-shape-log");
    write_credentials(&home, FIXTURE_TOKEN, None);
    let lines = Arc::new(Mutex::new(Vec::<String>::new()));
    let sink = lines.clone();
    let log = AccountUsageShapeLog::new(move |line| sink.lock().unwrap().push(line.to_string()));
    let deadline = ResetReadDeadlineBox::new();
    const CEDAR: &str = r#"{"cedar_ember":{"grants":[{"resets_left":2,"ends_at":"2026-09-21T00:00:00Z","usable_now":true,"use_requires_limit":false,"paused":false}],"in_experiment":true}}"#;
    const JUNIPER: &str =
        r#"{"juniper_tide":{"in_experiment":true,"available":true,"resets_per_week":3,"weekly_resets_at":"2026-09-22T00:00:00Z"}}"#;
    let http = HttpFn(|request: &AccountUsageHttpRequest| Ok(answers((200, CEDAR), (200, JUNIPER))(request)));
    let loaded = credential_file::read(&text(&home), &env(&[]), instant());
    read_claude(&env(&[]), || loaded.clone(), &http, instant(), &deadline, Some(&log)).await.unwrap();
    let first = lines.lock().unwrap().clone();
    assert!(!first.is_empty(), "shape lines are emitted on first read");
    for line in &first {
        let content = line.strip_prefix("account-usage: ").unwrap_or_else(|| panic!("every line has the prefix: {line}"));
        let separator = content.rfind(": ").unwrap_or_else(|| panic!("every line is 'path: type': {line}"));
        assert!(
            ["object", "array", "string", "number", "boolean", "null"].contains(&&content[separator + 2..]),
            "a JSON kind word: {line}"
        );
    }
    let all = first.join("\n");
    assert!(!all.contains(FIXTURE_TOKEN) && !all.contains("fixture@"), "no sign-in or e-mail in a shape line");
    read_claude(&env(&[]), || loaded.clone(), &http, instant(), &deadline, Some(&log)).await.unwrap();
    assert_eq!(lines.lock().unwrap().len(), first.len(), "shape lines are logged only once per programme");
    let _ = std::fs::remove_dir_all(&home);
}

/// AccountUsageVerification.cs:740 `UsageResetRateLimitDeadline`: a 429 on a reset GET sets a deadline; no reset GET is sent before
/// it; the base usage is unaffected.
#[tokio::test]
async fn usage_reset_rate_limit_deadline() {
    let deadline = ResetReadDeadlineBox::new();
    let reset_calls = Arc::new(AtomicUsize::new(0));
    let make = |status: u16, retry_after: Option<&'static str>| {
        let calls = reset_calls.clone();
        HttpFn(move |request: &AccountUsageHttpRequest| {
            Ok(match request.url.query() {
                Some(_) => {
                    calls.fetch_add(1, Ordering::SeqCst);
                    AccountUsageHttpResponse {
                        status,
                        body: String::new(),
                        retry_after: retry_after.map(str::to_string),
                        redirect_location: None,
                    }
                }
                None => AccountUsageHttpResponse::new(200, if request.url.path().ends_with("profile") { PROFILE_BODY } else { USAGE_BODY }),
            })
        })
    };
    let first = read_claude(&env(&[]), || Some(credential()), &make(429, Some("120")), instant(), &deadline, None).await.unwrap();
    assert_eq!(reset_calls.swap(0, Ordering::SeqCst), 2, "the first call makes two reset GETs (one per programme)");
    assert!(!first.windows.is_empty() && first.resets.iter().all(|r| r.state == ResetState::UNKNOWN), "base usage kept, resets unknown");
    assert!(deadline.get().is_some_and(|d| d > instant()), "a deadline is set after 429");
    let second =
        read_claude(&env(&[]), || Some(credential()), &make(200, None), instant() + TimeDelta::seconds(60), &deadline, None).await.unwrap();
    assert_eq!(reset_calls.swap(0, Ordering::SeqCst), 0, "no reset GETs before the deadline");
    assert!(second.resets.iter().all(|r| r.state == ResetState::UNKNOWN), "reset rows stay unknown during the deadline");
    read_claude(&env(&[]), || Some(credential()), &make(200, None), instant() + TimeDelta::seconds(130), &deadline, None).await.unwrap();
    assert_eq!(reset_calls.load(Ordering::SeqCst), 2, "reset GETs resume after the deadline");
}

/// AccountUsageVerification.cs:793 `UsageResetGuardCheck`: an unsanctioned usage query is refused before the transport sees it.
#[tokio::test]
async fn usage_reset_guard_check() {
    for query in [
        "cedar_ember=1",
        "at_wall=1",
        "cedar_ember=1&skip_spend=0",
        "at_wall=1&skip_spend=0",
        "cedar_ember=1&extra=true&skip_spend=1",
        "at_wall=1&extra=1&skip_spend=1",
        "skip_spend=1&cedar_ember=1",
        "skip_spend=1&at_wall=1",
    ] {
        assert!(
            usage::guard(&url::Url::parse(&format!("https://api.anthropic.com/api/oauth/usage?{query}")).unwrap()).is_err(),
            "usage?{query}"
        );
    }
    for address in [
        "https://api.anthropic.com/api/oauth/usage",
        "https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1",
        "https://api.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=1",
        "https://api.anthropic.com/api/oauth/profile",
    ] {
        assert!(usage::guard(&url::Url::parse(address).unwrap()).is_ok(), "{address} passes the guard");
    }
    let attempts = Arc::new(AtomicUsize::new(0));
    let counted = attempts.clone();
    let guarded = Guarded(HttpFn(move |_: &AccountUsageHttpRequest| {
        counted.fetch_add(1, Ordering::SeqCst);
        Ok(AccountUsageHttpResponse::new(200, USAGE_BODY))
    }));
    for address in [
        "https://api.anthropic.com/api/oauth/usage?cedar_ember=1",
        "https://api.anthropic.com/api/oauth/usage?at_wall=1",
        "https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=0",
        "https://api.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=0",
        "https://api.anthropic.com/api/oauth/usage?cedar_ember=1&extra=true&skip_spend=1",
        "https://api.anthropic.com/api/oauth/usage?skip_spend=1&at_wall=1",
        "http://api.anthropic.com/api/oauth/usage",
        "https://evil.test/api/oauth/usage",
    ] {
        let request = AccountUsageHttpRequest::new(url::Url::parse(address).unwrap(), vec![], ClaudeAccountProbe::TIMEOUT);
        assert!(matches!(guarded.get(request).await, Err(HttpFailure::Refused(_))), "{address} is refused");
    }
    assert_eq!(attempts.load(Ordering::SeqCst), 0, "the transport count stays 0 for refused queries");
    let sanctioned = AccountUsageHttpRequest::new(usage::endpoint("usage", None).unwrap(), vec![], ClaudeAccountProbe::TIMEOUT);
    assert!(guarded.get(sanctioned).await.is_ok() && attempts.load(Ordering::SeqCst) == 1, "a sanctioned request reaches the transport");
}

/// AccountUsageVerification.cs:853 `UsageResetShapeLogPathsMatchMacOS`.
#[test]
fn usage_reset_shape_log_paths_match_macos() {
    let lines = Arc::new(Mutex::new(Vec::<String>::new()));
    let sink = lines.clone();
    let log = AccountUsageShapeLog::new(move |line| sink.lock().unwrap().push(line.to_string()));
    let body: Value = serde_json::from_str(
        r#"{"cedar_ember":{"grants":[{"resets_left":2,"ends_at":"2026-09-21T00:00:00Z","usable_now":true}],"in_experiment":true}}"#,
    )
    .unwrap();
    log.log_shape_once(ResetProgram::CEDAR_EMBER, &body);
    let emitted = lines.lock().unwrap().clone();
    assert!(!emitted.is_empty(), "shape lines must be emitted");
    assert!(emitted.iter().all(|l| !l.contains("cedar_ember.cedar_ember")), "no path holds the programme name twice in a row");
    assert!(emitted.iter().any(|l| l.contains("cedar_ember: object")), "the first path token is the body's top-level key");
    assert_eq!(emitted[0], "account-usage: cedar_ember: object", "{emitted:?}");
}

// ── Antigravity ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// agy 1.5.5's real `/usage` reply.
const AGY_USAGE: &str = r#"{"conversation_id":"","status":"SUCCESS","response":"group\tbucket","usage":{"input_tokens":0},"command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[{"name":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.983944296836853,"reset_time":"2026-11-14T03:59:25Z"},{"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.9997177720069885,"reset_time":"2026-11-10T17:58:15Z"}]},{"name":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS","buckets":[{"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-17T13:01:31Z"},{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-10T18:01:31Z"}]}]}}}"#;

/// AntigravityAccountUsageVerification.cs:22 `TheUsageReplyGivesFourGroupedWindows` (its English label is tests/usage_language.rs').
#[test]
fn the_usage_reply_gives_four_grouped_windows() {
    common::korean();
    let value = AntigravityAccountProbe::map(AGY_USAGE, "").unwrap();
    assert!(value.provider == "gemini" && value.status == "available", "an available reading");
    let kinds: Vec<&str> = value.windows.iter().map(|w| w.kind.as_str()).collect();
    assert_eq!(kinds, ["weekly@gemini", "session@gemini", "weekly@claude_gpt", "session@claude_gpt"], "four grouped windows");
    let weekly = &value.windows[0];
    assert!(
        (weekly.used_percent - 1.6055703163147).abs() < 0.0001 && weekly.window_minutes == Some(10080),
        "the weekly window's use and length"
    );
    assert_eq!(
        usage::date(weekly.resets_at.as_deref()),
        Some(DateTime::parse_from_rfc3339("2026-11-14T03:59:25Z").unwrap().with_timezone(&Utc)),
        "its reset"
    );
    assert!(value.windows[1].window_minutes == Some(300) && value.windows[2].used_percent == 0.0, "the 5-hour window, and an unused group");
    assert_eq!(usage::window_label("session@gemini"), "Gemini · 세션");
    assert_eq!(usage::window_label("weekly@claude_gpt"), "Claude·GPT · 주간");
    assert_eq!(usage::window_label("weekly@Other Models"), "Other Models · 주간", "another group keeps agy's name");
    let chip: Vec<String> = usage::leading(&value.windows).into_iter().map(|w| w.kind).collect();
    assert_eq!(chip, ["session@gemini", "weekly@gemini"], "the chip keeps the most-used group per window: session first, then weekly");
}

/// AntigravityAccountUsageVerification.cs:44 `LeadingKeepsUngroupedOrder`.
#[test]
fn leading_keeps_ungrouped_order() {
    common::korean();
    let windows =
        [AccountUsageWindow::new("Sonnet", 90.0), AccountUsageWindow::new("weekly", 10.0), AccountUsageWindow::new("session", 5.0)];
    assert!(usage::leading(&windows).iter().map(|w| w.kind.as_str()).eq(["session", "weekly"]), "the session window, then the weekly one");
    let grouped = [
        AccountUsageWindow::new("session@a", 10.0),
        AccountUsageWindow::new("session@b", 70.0),
        AccountUsageWindow::new("weekly@a", 40.0),
        AccountUsageWindow::new("weekly@b", 5.0),
    ];
    assert!(usage::leading(&grouped).iter().map(|w| w.kind.as_str()).eq(["session@b", "weekly@a"]), "the most-used group of each window");
}

/// AntigravityAccountUsageVerification.cs:53 `InvalidBucketsAreSkippedAndUnknownGroupsKeepTheirName`.
#[test]
fn invalid_buckets_are_skipped_and_unknown_groups_keep_their_name() {
    common::korean();
    const BODY: &str = r#"{"status":"SUCCESS","command":{"data":{"groups":[{"name":"New Models","buckets":[{"id":"n-5h","window":"5h","remaining_fraction":0.25},{"id":"n-w","window":"weekly","remaining_fraction":"x"},{"id":"n-w2","window":"weekly","remaining_fraction":7},{"id":"n-5h-dup","window":"5h","remaining_fraction":0.5}]}]}}}"#;
    let value = AntigravityAccountProbe::map(&format!("noise\n{BODY}\n"), "").unwrap();
    assert!(
        value.windows.len() == 1 && value.windows[0].kind == "session@New Models" && value.windows[0].used_percent == 75.0,
        "one valid bucket, under agy's own group name"
    );
    let empty = AntigravityAccountProbe::map(r#"{"status":"SUCCESS","command":{"data":{"groups":[]}}}"#, "").unwrap();
    assert!(
        empty.status == "unavailable" && empty.windows.is_empty() && empty.detail == copy::detail_antigravity_no_windows(),
        "no group, no window"
    );
}

/// AntigravityAccountUsageVerification.cs:63 `SignedOutReadsAsAuthenticationAndGarbageAsInvalid`.
#[test]
fn signed_out_reads_as_authentication_and_garbage_as_invalid() {
    common::korean();
    let failure = |stdout: &str, stderr: &str| AntigravityAccountProbe::map(stdout, stderr).err().map(|f| f.kind);
    assert_eq!(
        failure("", "Error: Please sign in to continue.\n"),
        Some(AccountUsageFailureKind::Authentication),
        "signed out on standard error"
    );
    assert_eq!(
        failure(r#"{"status":"ERROR","response":"Authentication required. Run agy to sign in."}"#, ""),
        Some(AccountUsageFailureKind::Authentication),
        "signed out in its reply"
    );
    assert_eq!(failure("<<garbage>> {not json", ""), Some(AccountUsageFailureKind::InvalidResponse), "anything else is an invalid reply");
}

fn no_find() -> usage::Finder {
    Arc::new(|_| Box::pin(async { None }))
}

/// AcpAgentAccountVerification.cs:356 `OpencodeAndHermesHaveNoAccountUsageAndNoCliIsAsked`.
#[tokio::test]
async fn opencode_and_hermes_have_no_account_usage_and_no_cli_is_asked() {
    common::korean();
    let home = temp("usage-acp");
    let looked = Arc::new(Mutex::new(Vec::<String>::new()));
    let record = looked.clone();
    let find: usage::Finder = Arc::new(move |provider| {
        record.lock().unwrap().push(provider);
        Box::pin(async { None })
    });
    let home_text = text(&home);
    let probe = usage::runtime_probe(RuntimeProbe {
        find,
        environment: Arc::new(std::collections::BTreeMap::new),
        home: Arc::new(move || home_text.clone()),
        working_directory: home.clone(),
        processes: Arc::new(mighty_platform::fake::FakeProcessHost::default()),
        http: Arc::new(HttpFn(|_: &AccountUsageHttpRequest| panic!("no request"))),
    });
    for (provider, key) in [
        ("opencode", "accountUsage.detailOpencodeUnavailable"),
        ("hermes", "accountUsage.detailHermesUnavailable"),
        ("pi", "accountUsage.detailPiUnavailable"),
    ] {
        let read = probe(provider.into()).await;
        assert!(
            matches!(&read, Err(UsageError::Failure(f)) if f.kind == AccountUsageFailureKind::Unavailable && f.detail == strings::get(key)),
            "{provider}: unavailable, {read:?}"
        );
    }
    assert!(looked.lock().unwrap().is_empty(), "no CLI is looked for");
    let service = AccountUsageService::new(probe, usage::system_clock());
    let status = AccountUsageStatus::new(service, usage::system_clock());
    status.update(&snapshot(json!({
        "workspaces": [{"id": "w", "path": text(&home)}],
        "sessions": [
            {"id": "a", "workspaceId": "w", "kind": "claude", "provider": "hermes"},
            {"id": "b", "workspaceId": "w", "kind": "claude", "provider": "opencode"},
            {"id": "c", "workspaceId": "w", "kind": "claude", "provider": "opencode"},
        ],
    })));
    assert!(
        status.providers() == ["opencode", "hermes"] && status.targets() == ["opencode", "hermes"],
        "a chip each, in the provider order"
    );
    status.refresh(true).await;
    let cards = status.cards();
    assert!(
        cards.iter().map(|c| (c.provider.as_str(), c.title.as_str(), c.detail.clone())).eq([
            ("opencode", mighty_run::providers::name("opencode"), Some(strings::get("accountUsage.detailOpencodeUnavailable"))),
            ("hermes", mighty_run::providers::name("hermes"), Some(strings::get("accountUsage.detailHermesUnavailable"))),
        ]) && cards.iter().all(|c| c.windows.is_empty()),
        "each card says why it has none: {cards:?}"
    );
    assert!(
        status.snapshot("opencode").is_some_and(|s| s.status == "unavailable")
            && status.snapshot("hermes").is_some_and(|s| s.status == "unavailable"),
        "unavailable, not an error"
    );
    assert!(status.chips().iter().all(|chip| chip.text == copy::chip_empty() && !chip.warning), "the chips stay empty");
    assert!(status.leading_bars().is_empty(), "the dashboard draws no bars for them");
    let unknown = usage::runtime_probe(RuntimeProbe {
        find: no_find(),
        environment: Arc::new(std::collections::BTreeMap::new),
        home: Arc::new(String::new),
        working_directory: home.clone(),
        processes: Arc::new(mighty_platform::fake::FakeProcessHost::default()),
        http: Arc::new(HttpFn(|_: &AccountUsageHttpRequest| panic!("no request"))),
    });
    assert!(
        matches!(unknown("other".into()).await, Err(UsageError::Failure(f)) if f.detail == copy::detail_unsupported_provider()),
        "an unknown provider"
    );
    let _ = std::fs::remove_dir_all(&home);
}

/// AntigravityAccountUsageVerification.cs:76 `TheAppsProbeRunsAgyUsage`: the probe the app gives the service runs agy with exactly the
/// `/usage` arguments in the temporary folder (the stand-in agy over the real Windows process layer), or says why not.
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn the_apps_probe_runs_agy_usage() {
    common::korean();
    let state = temp("agy-usage");
    let folder = state.file_name().unwrap().to_string_lossy().into_owned();
    let marker = std::env::temp_dir().join(format!("mighty-agy-usage-{folder}.marker"));
    std::fs::write(&marker, "").unwrap();
    let agy = mighty_run::model::CliCommand::new(text(&common::fake_cli()), vec!["--agy-usage-cli".into(), text(&state)], None);
    let processes: Arc<dyn mighty_platform::ProcessHost> = Arc::new(mighty_platform_windows::WindowsProcessHost::new());
    let environment: std::collections::BTreeMap<String, String> = std::env::vars().collect();
    let parts = |find: usage::Finder| RuntimeProbe {
        find,
        environment: Arc::new({
            let environment = environment.clone();
            move || environment.clone()
        }),
        home: Arc::new({
            let state = text(&state);
            move || state.clone()
        }),
        working_directory: state.clone(),
        processes: processes.clone(),
        http: Arc::new(HttpFn(|_: &AccountUsageHttpRequest| panic!("no request"))),
    };
    let finder: usage::Finder = {
        let agy = agy.clone();
        Arc::new(move |provider| {
            let found = (provider == "gemini").then(|| agy.clone());
            Box::pin(async move { found })
        })
    };
    let probe = usage::runtime_probe(parts(finder));
    let snapshot = probe("gemini".into()).await.expect("agy answered");
    assert!(snapshot.status == "available" && snapshot.windows.len() == 4, "agy's four windows: {} {}", snapshot.status, snapshot.detail);
    let argv: Vec<String> = serde_json::from_str(&std::fs::read_to_string(state.join("agy-argv.json")).unwrap()).unwrap();
    assert!(
        argv == AntigravityAccountProbe::ARGUMENTS && AntigravityAccountProbe::ARGUMENTS == ["-p", "/usage", "--output-format", "json"],
        "agy -p /usage"
    );
    let ran = common::json(&std::fs::read_to_string(state.join("agy-run.json")).unwrap());
    assert!(ran["NO_COLOR"] == "1" && ran["CI"] == "1", "quiet");
    assert_eq!(ran["inTemporaryFolder"], "yes", "run in the temporary folder, so no project's configuration is read");
    std::fs::write(state.join("agy-signed-out"), "").unwrap();
    let signed_out = probe("gemini".into()).await;
    assert!(
        matches!(&signed_out, Err(UsageError::Failure(f)) if f.kind == AccountUsageFailureKind::Authentication),
        "a sign-in to check: {signed_out:?}"
    );
    let none = usage::runtime_probe(parts(no_find()));
    let read = none("gemini".into()).await;
    assert!(matches!(&read, Err(UsageError::Failure(f)) if f.detail == copy::detail_antigravity_not_installed()), "no agy: {read:?}");

    let service = AccountUsageService::new(probe, usage::system_clock());
    let status = AccountUsageStatus::new(service, usage::system_clock());
    status.update(&snapshot_of_gemini(&state));
    std::fs::remove_file(state.join("agy-signed-out")).unwrap();
    status.refresh(true).await;
    let chips = status.chips();
    assert!(
        chips.len() == 1 && chips[0].provider == "gemini" && chips[0].text == "Gemini · 세션 0% Gemini · 주간 2%" && !chips[0].warning,
        "the chip: {chips:?}"
    );
    let cards = status.cards();
    assert!(
        cards.len() == 1
            && cards[0].windows.len() == 4
            && cards[0].windows[0].label == "Gemini · 주간 (7일)"
            && cards[0].windows[1].label == "Gemini · 세션 (5시간)"
            && cards[0].detail.is_none(),
        "the card: {cards:?}"
    );
    let bars = status.leading_bars();
    assert!(
        bars.len() == 1 && bars[0].bars.iter().map(|b| b.label.as_str()).eq(["Gemini · 세션", "Gemini · 주간"]),
        "the dashboard's bars"
    );
    let _ = std::fs::remove_file(&marker);
    common::remove_when_free(&state).await;
}

#[cfg(windows)]
fn snapshot_of_gemini(state: &std::path::Path) -> AppSnapshot {
    snapshot(
        json!({"workspaces": [{"id": "w", "path": text(state)}], "sessions": [{"id": "a", "workspaceId": "w", "kind": "claude", "provider": "gemini"}]}),
    )
}

/// `AccountUsageRuntime.CodexStdio` over the real Windows process layer: `codex app-server --listen stdio://` (the stand-in) answers the
/// account read, and the probe gives its windows.
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn the_codex_app_server_is_asked_over_the_process_host() {
    let state = temp("codex-app-server");
    let processes: Arc<dyn mighty_platform::ProcessHost> = Arc::new(mighty_platform_windows::WindowsProcessHost::new());
    let codex = mighty_run::model::CliCommand::new(text(&common::fake_cli()), vec!["--codex-app-server".into(), text(&state)], None);
    let channel = usage::HostCodexStdio::start(processes.as_ref(), &codex, &state).await.expect("started");
    let snapshot = CodexAccountProbe::read(Box::new(channel), Duration::from_secs(20)).await.expect("read");
    assert!(snapshot.windows.len() == 2 && snapshot.plan.as_deref() == Some("pro") && snapshot.windows[1].kind == "weekly", "{snapshot:?}");
    let argv: Vec<String> = serde_json::from_str(&std::fs::read_to_string(state.join("codex-argv.json")).unwrap()).unwrap();
    assert_eq!(argv, ["app-server", "--listen", "stdio://"], "its own app-server over stdio");
    let lines = std::fs::read_to_string(state.join("codex-stdin.jsonl")).unwrap();
    assert_eq!(lines.lines().count(), 4, "four lines written: {lines}");
    common::remove_when_free(&state).await;
}

/// The cards and chips of a reading through the service: an entitlement row reaches the Claude card only while the switch is on.
#[tokio::test]
async fn the_reset_rows_follow_the_switch() {
    common::korean();
    let service = AccountUsageService::new(
        probe_of(|provider| {
            Ok(AccountUsageSnapshot {
                windows: vec![AccountUsageWindow::new("five_hour", 95.0)],
                status: "available".into(),
                resets: vec![AccountResetEntitlement::new(ResetProgram::CEDAR_EMBER, ResetState::HELD)],
                ..AccountUsageSnapshot::new(&provider)
            })
        }),
        clock_at(instant()),
    );
    let status = AccountUsageStatus::new(service, clock_at(instant()));
    let state = json!({"claudeDirectUsageLookupEnabled": true, "workspaces": [{"id": "w"}], "sessions": [{"id": "a", "workspaceId": "w", "kind": "claude", "provider": "claude"}]});
    status.update(&snapshot(state));
    status.refresh(true).await;
    let rows = status.reset_rows();
    assert!(rows.len() == 2 && rows[0].state == "held" && rows[1].state == "unknown", "{rows:?}");
    assert!(status.chips()[0].warning, "95% warns");
    status.set_direct_claude_lookup(false);
    assert!(status.reset_rows().is_empty() && status.snapshot("claude").is_none(), "switching off drops what only the direct lookup read");
    assert_eq!(status.chips()[0].text, copy::chip_before_first_run(), "and the chip waits for a run");
}
