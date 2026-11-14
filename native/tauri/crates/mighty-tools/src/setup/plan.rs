//! Which items an install runs and in what order (C# `SetupPlan`, SetupPlan.cs:12-47): the picked ones plus every prerequisite
//! that is not there yet, prerequisites first, the catalog's order breaking ties.

use std::collections::HashSet;

use crate::setup::catalog::{SetupCatalog, SetupPlatform};

/// C# `SetupPlan.Order`.
pub fn order(
    selected: &HashSet<String>,
    catalog: &SetupCatalog,
    platform: SetupPlatform,
    is_satisfied: &dyn Fn(&str) -> bool,
) -> Vec<String> {
    let mut included: HashSet<String> = HashSet::new();
    fn include(
        id: &str,
        catalog: &SetupCatalog,
        platform: SetupPlatform,
        is_satisfied: &dyn Fn(&str) -> bool,
        included: &mut HashSet<String>,
    ) {
        let Some(entry) = catalog.item(id).and_then(|item| item.entry(platform)) else { return };
        if !included.insert(id.to_string()) {
            return;
        }
        for prerequisite in &entry.prerequisites {
            if !is_satisfied(prerequisite) {
                include(prerequisite, catalog, platform, is_satisfied, included);
            }
        }
    }
    for item in &catalog.items {
        if selected.contains(&item.id) {
            include(&item.id, catalog, platform, is_satisfied, &mut included);
        }
    }
    topological(&included, catalog, platform)
        .unwrap_or_else(|| catalog.items.iter().map(|item| item.id.clone()).filter(|id| included.contains(id)).collect())
}

/// C# `SetupPlan.AddedPrerequisites`: the prerequisites [`order`] adds that were not picked themselves.
pub fn added_prerequisites(
    selected: &HashSet<String>,
    catalog: &SetupCatalog,
    platform: SetupPlatform,
    is_satisfied: &dyn Fn(&str) -> bool,
) -> HashSet<String> {
    order(selected, catalog, platform, is_satisfied).into_iter().filter(|id| !selected.contains(id)).collect()
}

/// C# `SetupPlan.HasNoCycle`.
pub fn has_no_cycle(catalog: &SetupCatalog, platform: SetupPlatform) -> bool {
    let all: HashSet<String> = catalog.items.iter().map(|item| item.id.clone()).collect();
    topological(&all, catalog, platform).is_some()
}

/// Kahn's order over `ids`, earliest catalog item first among the ready ones; None on a cycle.
fn topological(ids: &HashSet<String>, catalog: &SetupCatalog, platform: SetupPlatform) -> Option<Vec<String>> {
    let ordered: Vec<&str> = catalog.items.iter().map(|item| item.id.as_str()).filter(|id| ids.contains(*id)).collect();
    let mut waiting: Vec<(&str, HashSet<&str>)> = ordered
        .iter()
        .map(|id| {
            let prerequisites =
                catalog.item(id).and_then(|item| item.entry(platform)).map(|entry| entry.prerequisites.as_slice()).unwrap_or(&[]);
            (*id, prerequisites.iter().map(String::as_str).filter(|p| ids.contains(*p)).collect())
        })
        .collect();
    let mut result = Vec::new();
    while !waiting.is_empty() {
        let next = ordered.iter().find(|id| waiting.iter().any(|(w, left)| w == *id && left.is_empty()))?;
        result.push(next.to_string());
        waiting.retain(|(w, _)| w != next);
        for (_, left) in waiting.iter_mut() {
            left.remove(next);
        }
    }
    Some(result)
}
