//! The "install missing tools" flow of the toolkit box (C# `ToolkitPlanItem`, `ToolkitStepResult`, `ToolkitRunItem`,
//! `ToolkitPresentation`, `ToolkitRunner`, ToolkitRunner.cs) and the box's executor (C# `CliToolkitExecutor`,
//! MainWindow.Settings.cs:556-571). The plan probes every entry again and keeps only missing ones; the verdict after a run comes
//! from probing again, never from exit codes, and the steps are its explanation.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use mighty_platform::{BoxFuture, ProcessHost, ShellEnvironment};
use mighty_run::strings;

use crate::setup::windows::set_variable;
use crate::toolkit::file::{ToolkitApproval, ToolkitEntrySource, ToolkitFileEntry, ToolkitInstallSpec};
use crate::toolkit::probe::{self, ProbeResult, ToolkitProbeContext};
use crate::toolkit::store::ToolkitStore;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlanAction {
    Run,
    Skip,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitPlanItem {
    pub entry: ToolkitFileEntry,
    pub action: PlanAction,
    pub commands: Vec<Vec<String>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StepOutcome {
    Ok,
    Failed,
    Skipped,
}

impl StepOutcome {
    pub fn name(self) -> &'static str {
        match self {
            StepOutcome::Ok => "Ok",
            StepOutcome::Failed => "Failed",
            StepOutcome::Skipped => "Skipped",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitStepResult {
    pub argv: Vec<String>,
    pub outcome: StepOutcome,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Verdict {
    Installed,
    Failed,
    Skipped,
}

impl Verdict {
    pub fn name(self) -> &'static str {
        match self {
            Verdict::Installed => "Installed",
            Verdict::Failed => "Failed",
            Verdict::Skipped => "Skipped",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitRunItem {
    pub entry_id: String,
    pub verdict: Verdict,
    pub steps: Vec<ToolkitStepResult>,
}

/// What the toolkit box says about a plan, a run and a file that went wrong (C# `ToolkitPresentation`).
pub mod presentation {
    use super::*;

    /// One tool of the confirmation: its name over every command it will run, or over the one line that says it is left out.
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct PlanRow {
        pub name: String,
        pub lines: Vec<String>,
        pub skipped: bool,
    }

    /// The confirmation's rows, one per tool of the plan in its order: nothing runs that is not listed.
    pub fn plan_rows(plan: &[ToolkitPlanItem]) -> Vec<PlanRow> {
        plan.iter()
            .map(|item| match item.action {
                PlanAction::Skip => PlanRow {
                    name: item.entry.display_name.clone(),
                    lines: vec![format!("→ {}", strings::get("settings.toolkit.verdictSkipped"))],
                    skipped: true,
                },
                PlanAction::Run => PlanRow {
                    name: item.entry.display_name.clone(),
                    lines: item.commands.iter().map(|argv| argv.join(" ")).collect(),
                    skipped: false,
                },
            })
            .collect()
    }

    /// A result row's name: the tool's own, or its id once the tool is no longer listed.
    pub fn result_name(item: &ToolkitRunItem, entries: &[ToolkitFileEntry]) -> String {
        entries
            .iter()
            .find(|entry| entry.id == item.entry_id)
            .map(|entry| entry.display_name.clone())
            .unwrap_or_else(|| item.entry_id.clone())
    }

    /// The line over the tools when toolkit.json could not be read or changed: that it failed, then why.
    pub fn error_banner(reason: &str) -> String {
        format!("{} {reason}", strings::get("settings.toolkit.errorBanner"))
    }
}

/// C# `ToolkitCommandOutput`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommandOutput {
    pub exit_code: i32,
    pub output: String,
}

impl CommandOutput {
    pub fn success() -> CommandOutput {
        CommandOutput { exit_code: 0, output: String::new() }
    }

    pub fn failure(output: impl Into<String>, exit_code: i32) -> CommandOutput {
        CommandOutput { exit_code, output: output.into() }
    }
}

/// Runs one step's argv (C# `IToolkitRunnerExecutor`).
pub trait ToolkitExecutor: Send + Sync {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput>;
}

/// C# `ToolkitRunner`.
pub struct ToolkitRunner<'a> {
    store: &'a ToolkitStore,
    probe_context: ToolkitProbeContext,
}

impl<'a> ToolkitRunner<'a> {
    pub fn new(store: &'a ToolkitStore, probe_context: ToolkitProbeContext) -> ToolkitRunner<'a> {
        ToolkitRunner { store, probe_context }
    }

    /// The entries that are missing now: the bundled entry and approved user entries run, unapproved user entries are skipped.
    pub fn plan(&self) -> Vec<ToolkitPlanItem> {
        let (entries, _) = self.store.list();
        let mut items = Vec::new();
        for entry in entries {
            let approval = self.store.get_approval(&entry);
            if probe::probe(&entry, approval.as_ref(), &self.probe_context) == ProbeResult::Installed {
                continue;
            }
            if entry.source == ToolkitEntrySource::User && approval.is_none() {
                items.push(ToolkitPlanItem { entry, action: PlanAction::Skip, commands: vec![] });
            } else {
                let commands = install_commands(&entry, approval.as_ref(), &self.probe_context);
                if commands.is_empty() {
                    continue;
                }
                items.push(ToolkitPlanItem { entry, action: PlanAction::Run, commands });
            }
        }
        items
    }

    /// Executes a plan: items in order, one failure does not stop the rest; a fetch step is tried once more when its output reads as
    /// a network error; the verdict comes from probing again.
    pub async fn run(&self, items: &[ToolkitPlanItem], executor: &dyn ToolkitExecutor) -> Vec<ToolkitRunItem> {
        let mut results = Vec::new();
        for item in items {
            if item.action == PlanAction::Skip {
                results.push(ToolkitRunItem { entry_id: item.entry.id.clone(), verdict: Verdict::Skipped, steps: vec![] });
                continue;
            }
            let steps = execute_entry(item, executor).await;
            let approval = self.store.get_approval(&item.entry);
            let verdict = match probe::probe(&item.entry, approval.as_ref(), &self.probe_context) {
                ProbeResult::Installed => Verdict::Installed,
                ProbeResult::Missing => Verdict::Failed,
            };
            results.push(ToolkitRunItem { entry_id: item.entry.id.clone(), verdict, steps });
        }
        results
    }
}

async fn execute_entry(item: &ToolkitPlanItem, executor: &dyn ToolkitExecutor) -> Vec<ToolkitStepResult> {
    let mut steps = Vec::new();
    let mut failed = false;
    for command in &item.commands {
        if failed {
            steps.push(ToolkitStepResult { argv: command.clone(), outcome: StepOutcome::Skipped });
            continue;
        }
        let mut result = executor.run(command).await;
        if result.exit_code != 0 && is_fetch_step(command) && is_network_error(&result.output) {
            result = executor.run(command).await;
        }
        if result.exit_code != 0 {
            failed = true;
            steps.push(ToolkitStepResult { argv: command.clone(), outcome: StepOutcome::Failed });
        } else {
            steps.push(ToolkitStepResult { argv: command.clone(), outcome: StepOutcome::Ok });
        }
    }
    steps
}

/// The one place that turns an entry into argv arrays.
pub fn install_commands(entry: &ToolkitFileEntry, _approval: Option<&ToolkitApproval>, ctx: &ToolkitProbeContext) -> Vec<Vec<String>> {
    let argv = |parts: &[&str]| parts.iter().map(|part| part.to_string()).collect::<Vec<_>>();
    match &entry.install {
        // plugin: marketplace add, then install with --json.
        ToolkitInstallSpec::Plugin { source, plugin_id } => vec![
            argv(&["claude", "plugin", "marketplace", "add", "--scope", "user", source]),
            argv(&["claude", "plugin", "install", plugin_id, "--scope", "user", "--json"]),
        ],
        // mcp: one command.
        ToolkitInstallSpec::Mcp { name, executable, args } => {
            let mut command = argv(&["claude", "mcp", "add", "--scope", "user", name, "--", executable]);
            command.extend(args.iter().cloned());
            vec![command]
        }
        // skill: git clone into %USERPROFILE%\.claude\skills\<name>.
        ToolkitInstallSpec::Skill { url } => skill_commands(url, &ctx.home_directory),
        ToolkitInstallSpec::Package { manager, name, .. } if manager == "npm" => vec![argv(&["npm", "install", "-g", name])],
        // winget: the exact install command from the constraint table.
        ToolkitInstallSpec::Package { manager, name, .. } if manager == "winget" => vec![argv(&[
            "winget",
            "install",
            "--exact",
            "--id",
            name,
            "--source",
            "winget",
            "--scope",
            "user",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--disable-interactivity",
        ])],
        // brew and repoScript are macOS-only; never build commands for them on Windows.
        _ => vec![],
    }
}

fn skill_commands(url: &str, home: &Path) -> Vec<Vec<String>> {
    let Some(component) = probe::skill_folder_name(url) else { return vec![] };
    let destination = home.join(".claude").join("skills").join(component);
    vec![vec!["git".into(), "clone".into(), url.into(), destination.to_string_lossy().into_owned()]]
}

/// Fetch commands eligible for the network-error retry (derived from the argv, no stored flag).
pub fn is_fetch_step(argv: &[String]) -> bool {
    let at = |index: usize| argv.get(index).map(String::as_str);
    match at(0) {
        Some("git") => matches!(at(1), Some("clone" | "ls-remote")),
        Some("npm") | Some("winget") => at(1) == Some("install"),
        Some("claude") => argv.len() > 2 && at(1) == Some("plugin") && matches!(at(2), Some("marketplace" | "install")),
        _ => false,
    }
}

/// The output reads as a known network failure.
pub fn is_network_error(output: &str) -> bool {
    let lower = output.to_lowercase();
    [
        "could not resolve host",
        "connection refused",
        "network is unreachable",
        "could not connect",
        "timed out",
        "ssl handshake",
        "could not download",
        "network error",
        "failed to connect",
        "no route to host",
    ]
    .iter()
    .any(|pattern| lower.contains(pattern))
}

/// Finds a step's program along the PATH the CLI environment keeps now (C# `WindowsCommand.Find`: PATH × PATHEXT); None leaves the
/// bare name, which cannot start.
pub type Resolver = Arc<dyn Fn(&str, &BTreeMap<String, String>) -> Option<String> + Send + Sync>;

/// One toolkit step at most this long (C#: `TimeSpan.FromMinutes(5)`).
pub const STEP_TIMEOUT: Duration = Duration::from_secs(5 * 60);
/// Each output kept to this many bytes (C# `CliRunner.OutputCapBytes`).
pub const OUTPUT_CAP: usize = 1024 * 1024;

/// C# `CliToolkitExecutor`: the program found along the PATH (a `.cmd` shim such as npm's runs through cmd.exe: the platform's
/// `cli_request`), started through `ProcessHost` in this process's folder, and one that cannot start is that step failing with why
/// (exit code -1), never the whole install.
pub struct ProcessToolkitExecutor {
    pub processes: Arc<dyn ProcessHost>,
    pub environment: Arc<dyn ShellEnvironment>,
    pub resolve: Resolver,
    /// C# `CliRunner`'s working folder when none is given: this process's.
    pub working_directory: PathBuf,
}

impl ToolkitExecutor for ProcessToolkitExecutor {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput> {
        Box::pin(async move {
            let Some((binary, args)) = argv.split_first() else { return CommandOutput::success() };
            let mut environment = self.environment.current().values;
            let program = (self.resolve)(binary, &environment).unwrap_or_else(|| binary.clone());
            set_variable(&mut environment, crate::setup::screen::NO_CURRENT_DIRECTORY_SEARCH, "1");
            let request = match self.processes.cli_request(Path::new(&program), args, &self.working_directory, &environment) {
                Ok(request) => request,
                Err(error) => return CommandOutput::failure(error.to_string(), -1),
            };
            match crate::process::run(self.processes.as_ref(), request, None, STEP_TIMEOUT, OUTPUT_CAP).await {
                Err(error) => CommandOutput::failure(error.to_string(), -1),
                Ok(ran) if ran.exit_code == 0 => CommandOutput::success(),
                Ok(ran) => CommandOutput::failure(ran.output + &ran.error_output, ran.exit_code),
            }
        })
    }
}
