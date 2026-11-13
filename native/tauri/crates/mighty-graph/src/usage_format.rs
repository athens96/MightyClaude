//! What a block says of its tokens and models (C# `ModelUsageFormat`, MightyCode.Core/ModelUsageFormat.cs) and the child blocks a
//! response record's tool calls opened (`GraphChildBlocks`, GraphChildBlocks.cs).

use indexmap::IndexMap;
use mighty_core::graph::{GraphResponseRecord, GraphTokenUsage, MightyGraphAgent};
use mighty_core::locale::Locale;
use mighty_core::model_label::{self, ModelOption};

use crate::node::{self, activity_ids, record_usage};

/// `ModelUsageFormat.BlockModels`: each model's tokens over the records, in the order they first came.
pub fn block_models(records: &[GraphResponseRecord]) -> Vec<(String, GraphTokenUsage)> {
    let mut sums: IndexMap<String, GraphTokenUsage> = IndexMap::new();
    for r in records {
        let key = r.model.clone().unwrap_or_default();
        let sum = sums.entry(key).or_default();
        *sum = node::add(*sum, record_usage(r));
    }
    sums.into_iter().collect()
}

/// `ModelUsageFormat.BlockCapsule`: "12K · Opus +1", the tokens alone, or the node's model label.
pub fn block_capsule(
    usage: Option<GraphTokenUsage>,
    records: &[GraphResponseRecord],
    node_model_label: Option<&str>,
    catalog: Option<&[ModelOption]>,
    versioned: bool,
    locale: &Locale,
) -> Option<String> {
    let label = if versioned { versioned_node_label(node_model_label, locale) } else { node_model_label.map(str::to_string) };
    if records.is_empty() {
        return label;
    }
    let Some(usage) = usage.filter(|u| !node::is_empty(u)) else { return label };
    let tokens = node::compact(usage.total());
    let models: Vec<_> = block_models(records).into_iter().filter(|(m, _)| !m.is_empty()).collect();
    if models.is_empty() {
        return Some(tokens);
    }
    let first = short_name(&models[0].0, catalog, versioned);
    Some(if models.len() == 1 { format!("{tokens} · {first}") } else { format!("{tokens} · {first} +{}", models.len() - 1) })
}

/// `ModelUsageFormat.BlockCapsuleHelp`: each model with its tokens' detail, one a line.
pub fn block_capsule_help(records: &[GraphResponseRecord], catalog: Option<&[ModelOption]>, versioned: bool, locale: &Locale) -> String {
    block_models(records)
        .into_iter()
        .filter(|(m, _)| !m.is_empty())
        .map(|(m, usage)| format!("{} · {}", short_name(&m, catalog, versioned), node::detail(&usage, locale)))
        .collect::<Vec<_>>()
        .join("\n")
}

/// `GraphChildBlock(Usage, Records)`.
#[derive(Debug, Clone, PartialEq)]
pub struct GraphChildBlock {
    pub usage: Option<GraphTokenUsage>,
    pub records: Vec<GraphResponseRecord>,
}

/// `ModelUsageFormat.ActivitySuffix`: what a tool row says of the response that called it, and of the sub-agent it opened.
pub fn activity_suffix(
    activity_id: &str,
    records: &[GraphResponseRecord],
    child: Option<&GraphChildBlock>,
    catalog: Option<&[ModelOption]>,
    versioned: bool,
    locale: &Locale,
) -> Option<String> {
    let child_capsule = || child.and_then(|c| block_capsule(c.usage, &c.records, None, catalog, versioned, locale));
    for record in records {
        if !activity_ids(record).any(|id| id == activity_id) {
            continue;
        }
        let caller = match caller_attribution(activity_id, std::slice::from_ref(record)) {
            Some(tokens) => {
                let tokens = node::compact(tokens);
                match record.model.as_deref().filter(|m| !m.is_empty()) {
                    Some(model) => format!("{} · {tokens}", short_name(model, catalog, versioned)),
                    None => tokens,
                }
            }
            None => locale.get("usage.modelUsage.sameResponse"),
        };
        if let Some(capsule) = child_capsule() {
            return Some(format!("{caller} · {} {capsule}", locale.get("usage.modelUsage.subagentPrefix")));
        }
        return Some(caller);
    }
    child_capsule().map(|capsule| format!("{} {capsule}", locale.get("usage.modelUsage.subagentPrefix")))
}

/// `ModelUsageFormat.CallerAttribution`: the response's tokens when the activity is its first call, none for a later one.
pub fn caller_attribution(activity_id: &str, records: &[GraphResponseRecord]) -> Option<i64> {
    for record in records {
        let Some(index) = activity_ids(record).position(|id| id == activity_id) else { continue };
        return (index == 0).then(|| record_usage(record).total());
    }
    None
}

/// `ModelUsageFormat.VersionedNodeLabel`: a saved node model label with its model part read through `ModelLabel`.
pub fn versioned_node_label(label: Option<&str>, locale: &Locale) -> Option<String> {
    let label = label?;
    for marker in
        [format!(" {}", locale.get("graph.nodeModel.configuredSuffix")), format!(" {}", locale.get("graph.nodeModel.requestedSuffix"))]
    {
        if let Some(model) = label.strip_suffix(marker.as_str()) {
            return Some(format!("{}{marker}", model_label::text(model, None, None)));
        }
    }
    Some(model_label::text(label, None, None))
}

/// `ModelUsageFormat.ShortName`: a model id's short display name, looked up against the catalogue.
pub fn short_name(model_id: &str, catalog: Option<&[ModelOption]>, versioned: bool) -> String {
    if versioned && let Some(label) = model_label::format(model_id) {
        return label;
    }
    let found = catalog.and_then(|c| c.iter().find(|o| o.value == model_id || o.resolved_model.as_deref() == Some(model_id)));
    match found {
        None if versioned => model_label::text(model_id, None, None),
        None => model_id.to_string(),
        Some(option) if versioned => model_label::option(option, None),
        Some(option) => option.display_name.clone(),
    }
}

/// `GraphChildBlocks.Map`: each tool call the records name whose block is in the run, by its activity id.
pub fn child_blocks(
    records: Option<&[GraphResponseRecord]>,
    agents: &[MightyGraphAgent],
    run_id: &str,
) -> IndexMap<String, GraphChildBlock> {
    let mut result = IndexMap::new();
    for record in records.unwrap_or(&[]) {
        for id in activity_ids(record) {
            let node = node::agent_node_id(run_id, id);
            let Some(agent) = agents.iter().find(|a| a.id() == node) else { continue };
            result.insert(
                id.to_string(),
                GraphChildBlock { usage: agent.usage, records: agent.response_records.iter().flatten().flatten().cloned().collect() },
            );
        }
    }
    result
}
