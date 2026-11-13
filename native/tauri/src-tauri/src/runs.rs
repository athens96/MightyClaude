//! The run engine in the app: one [`RunService`] (the run half of C# `DesktopService`) for every pane, with mighty-graph's tracker
//! plugged into its runner (`mighty_graph::TrackerFactory`: each request's graph run published every second and at its end, with the
//! notice runs its background work opened), the picture cache (`AgentImageCache`) and the CLIs' catalogue (`ProviderCatalog`), whose
//! readings the composer's pills and menus show.
//!
//! The service owns the state from here on: every change the host makes goes through it (`View::update` → `RunService::update`), and
//! each [`ServiceEvent`] it hands back is applied to the page's copy by [`Host::service_event`] (host.rs), which pushes `event` and
//! `permission` with C#'s rules (WebBridge.cs:75-82) and queues the saves.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use mighty_composer::images::{AgentImageCache, AgentImageError, CacheError};
use mighty_composer::runtime as composer_runtime;
use mighty_core::model::{AgentImageRef, AppSnapshot, RegisteredModelEntry, RunSession, Workspace};
use mighty_graph::TrackerFactory;
use mighty_platform::{ProcessHost, ShellEnvironment};
use mighty_run::model::{CliCommand, ModelCatalog, ProviderRuntime};
use mighty_run::parser::{ImageError, ImageStore};
use mighty_run::providers::{self, Finder, ProviderCatalog};
use mighty_run::service::{Configure, RunService, RunServiceOptions};

use crate::host::Host;

/// `AgentImageSupport.MaximumCacheBytes`.
const IMAGE_CACHE_BYTES: u64 = 200 * 1_048_576;

/// The smoke's stand-in Claude (mighty-run's `mighty-fake-cli --replay-cli`): asks one Bash permission and one plan, then replays a
/// recording. Every other provider is missing.
#[derive(Debug, Clone)]
pub struct FakeClaude {
    pub binary: PathBuf,
    pub recording: String,
    /// Where the fake writes what it was sent (`<record>.args`, `.input`, `.answers`).
    pub record: PathBuf,
}

pub struct Runs {
    pub service: Arc<RunService>,
    pub catalog: Arc<ProviderCatalog>,
    pub images: Arc<AgentImageCache>,
    /// Each provider's runtime as last read (C# `service.Runtime` / `PaneRuntime`): none until the first read finished.
    runtime: Mutex<HashMap<String, ProviderRuntime>>,
}

/// mighty-composer's picture cache as the run engine's image store (`AgentImageStore.Prepare`).
struct CacheStore(Arc<AgentImageCache>);

fn image_error(error: CacheError) -> ImageError {
    match error {
        CacheError::Image(AgentImageError::Empty) => ImageError::Empty,
        CacheError::Image(AgentImageError::TooLarge) => ImageError::TooLarge,
        CacheError::Image(AgentImageError::UnsupportedType) => ImageError::UnsupportedType,
        CacheError::Image(AgentImageError::Undecodable) | CacheError::Io(_) => ImageError::Undecodable,
        CacheError::Image(AgentImageError::ExternalSvg) => ImageError::ExternalSvg,
        CacheError::Image(AgentImageError::TooManyPixels) => ImageError::TooManyPixels,
        CacheError::Image(AgentImageError::InvalidEncoding) => ImageError::InvalidEncoding,
    }
}

impl ImageStore for CacheStore {
    fn prepare(&self, base64: &str, media_type: &str, source: &str, read_path: Option<&str>) -> Result<AgentImageRef, ImageError> {
        Ok(self.0.prepare_base64(base64, media_type).map_err(image_error)?.reference(source, read_path))
    }
}

/// The mighty-bridge plugin the Claude runs load (C#: `mods/mighty-bridge` copied next to the app as `claude-mods`): beside the exe
/// when it was put there, else the repository's own (a development build).
pub fn plugin_directory() -> String {
    let beside = std::env::current_exe().ok().and_then(|exe| exe.parent().map(|dir| dir.join("claude-mods")));
    match beside.filter(|dir| dir.join(".claude-plugin").is_dir()) {
        Some(dir) => dir.to_string_lossy().into_owned(),
        None => Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("..")
            .join("mods")
            .join("mighty-bridge")
            .to_string_lossy()
            .into_owned(),
    }
}

impl Runs {
    /// The service over the loaded state, its events pumped into the host, and the CLIs read once in the background.
    pub fn start(
        host: &Arc<Host>,
        processes: Arc<dyn ProcessHost>,
        environment: Arc<dyn ShellEnvironment>,
        fake: Option<FakeClaude>,
        image_dir: PathBuf,
    ) -> Arc<Runs> {
        mighty_run::strings::set_language(host.locale.language());
        // `{value:N0}` (the attachments' sizes) in the user's regional format, as C#'s current culture writes it.
        if let Some((separator, sizes)) = crate::platform::number_grouping() {
            mighty_run::strings::set_number_grouping(&separator, &sizes);
        }
        let finder: Option<Finder> = fake.map(|fake| {
            let command = CliCommand::new(
                fake.binary.to_string_lossy().into_owned(),
                vec!["--replay-cli".into(), fake.recording.clone(), fake.record.to_string_lossy().into_owned()],
                Some("2.1.322"),
            );
            let finder: Finder = Arc::new(move |provider: String| {
                let found = (provider == "claude").then(|| command.clone());
                Box::pin(async move { found })
            });
            finder
        });
        let catalog = Arc::new(ProviderCatalog::new(processes, environment, finder));
        let images = Arc::new(AgentImageCache::new(image_dir, IMAGE_CACHE_BYTES));
        let store: Arc<dyn ImageStore> = Arc::new(CacheStore(images.clone()));
        // A Claude run's Edit, Write and MultiEdit results keep the files' originals (files.rs, C# FileOriginalStore).
        let originals = crate::files::originals_hook(&host.app);
        let configure: Configure = Box::new(move |options| {
            options.graph = Some(Arc::new(TrackerFactory::new()));
            options.images = Some(store);
            options.file_originals = originals;
        });
        let snapshot = host.view().snapshot.clone();
        let (service, mut events) = RunService::new(
            snapshot,
            catalog.clone(),
            RunServiceOptions {
                plugin_directory: plugin_directory(),
                permission_card: true,
                remote: None,
                configure: Some(configure),
                admitting: None,
            },
        );
        let runs = Arc::new(Runs { service: Arc::new(service), catalog, images, runtime: Mutex::new(HashMap::new()) });
        host.attach(runs.clone());
        let pump = host.clone();
        tauri::async_runtime::spawn(async move {
            while let Some(event) = events.recv().await {
                pump.service_event(event);
            }
        });
        let reader = host.clone();
        let reading = runs.clone();
        tauri::async_runtime::spawn(async move {
            let info = reading.catalog.get_runtime(false, None).await;
            {
                let mut runtime = crate::host::lock(&reading.runtime);
                for provider in info.providers {
                    runtime.insert(provider.id.clone(), provider);
                }
            }
            reader.log.line(&format!(
                "runtime read: {}",
                crate::host::lock(&reading.runtime)
                    .values()
                    .map(|p| format!("{} {}", p.id, if p.available { "available" } else { "missing" }))
                    .collect::<Vec<_>>()
                    .join(", ")
            ));
            reader.panes_changed();
        });
        runs
    }

    /// `PaneRuntime(provider)` as the run engine read it.
    pub fn runtime(&self, provider: &str) -> Option<ProviderRuntime> {
        crate::host::lock(&self.runtime).get(provider).cloned()
    }

    /// One provider read again for a folder (`RefreshProviderAsync`), kept as its runtime.
    pub async fn refresh(&self, provider: &str, folder: Option<&str>) -> ProviderRuntime {
        let value = self.catalog.refresh_provider(provider, folder, false).await;
        crate::host::lock(&self.runtime).insert(provider.to_string(), value.clone());
        value
    }

    /// The runtime as the composer reads it (mighty-composer's mirror of C# `ProviderRuntime`).
    pub fn composer_runtime(&self, provider: &str) -> Option<composer_runtime::ProviderRuntime> {
        self.runtime(provider).map(|r| composer_runtime(&r))
    }

    /// `PaneRuntime(provider)?.ModelCatalog ?? ProviderCatalog.Fallback(provider)`.
    pub fn catalog_of(&self, provider: &str) -> ModelCatalog {
        self.runtime(provider).map(|r| r.model_catalog).unwrap_or_else(|| providers::fallback(provider))
    }

    /// `ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, RegisteredModelsFor(...))`.
    pub fn efforts(&self, pane: &RunSession, workspace: Option<&Workspace>, snapshot: &AppSnapshot) -> Vec<String> {
        let registered = registered_models_for(pane.provider(), workspace, snapshot);
        providers::efforts(pane.provider(), pane.model(), &self.catalog_of(pane.provider()), Some(&registered))
    }
}

/// mighty-run's `ProviderRuntime` as mighty-composer reads it (the one place the two meet).
pub fn composer_runtime(value: &ProviderRuntime) -> composer_runtime::ProviderRuntime {
    let caps = &value.capabilities;
    composer_runtime::ProviderRuntime {
        name: value.name.clone(),
        available: value.available,
        detail: value.detail.clone(),
        catalog: composer_runtime::ModelCatalog {
            source: value.model_catalog.source.clone(),
            models: value
                .model_catalog
                .models
                .iter()
                .map(|m| composer_runtime::CatalogModel {
                    value: m.value.clone(),
                    display_name: m.display_name.clone(),
                    description: m.description.clone(),
                    resolved_model: m.resolved_model.clone(),
                })
                .collect(),
            // `ModelListNotices.Notice` is the model list's (W5's catalogue settings); none until then.
            notice: None,
        },
        capabilities: composer_runtime::ProviderCapabilities {
            effort: caps.effort,
            permission_modes: caps.permission_modes.clone(),
            max_turns: caps.max_turns,
            max_budget_usd: caps.max_budget_usd,
            resume: caps.resume,
            fast_mode: caps.fast_mode,
            web_search: caps.web_search,
            network_access: caps.network_access,
            attachments: caps.attachments,
        },
    }
}

/// C# `RegisteredModelsFor` (MainWindow.cs:781): the workspace's registered models for the provider, then the app's it does not name.
pub fn registered_models_for(provider: &str, workspace: Option<&Workspace>, snapshot: &AppSnapshot) -> Vec<RegisteredModelEntry> {
    let entries = |config: Option<&mighty_core::model::ModelDefaultsConfig>| -> Vec<RegisteredModelEntry> {
        let side = config.and_then(|c| if provider == "codex" { c.codex.as_ref() } else { c.claude.as_ref() });
        side.and_then(|s| s.registered_models.as_ref()).map(|list| list.iter().flatten().cloned().collect()).unwrap_or_default()
    };
    let from_workspace = entries(workspace.and_then(|w| w.model_defaults.as_ref()));
    let from_app = entries(snapshot.model_defaults.as_ref());
    if from_app.is_empty() {
        return from_workspace;
    }
    if from_workspace.is_empty() {
        return from_app;
    }
    let names: Vec<Option<String>> = from_workspace.iter().map(|e| e.name.clone()).collect();
    from_workspace.iter().cloned().chain(from_app.into_iter().filter(|e| !names.contains(&e.name))).collect()
}
