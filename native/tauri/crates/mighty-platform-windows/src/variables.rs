//! An environment as Windows reads it (C# `Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)`, `WindowsCommand.Variables`).

use std::collections::BTreeMap;
use std::fmt;

use crate::win::fold;

/// Environment variables whose names are one name in any case (`Path` is `PATH`). A name keeps the spelling it was first set
/// with, as .NET's dictionary indexer keeps its key. `Debug` shows the names only: values can be credentials.
#[derive(Clone, Default, PartialEq, Eq)]
pub struct Variables(BTreeMap<String, (String, String)>);

impl Variables {
    pub fn new() -> Variables {
        Variables::default()
    }

    /// This process's environment as .NET reads it (`Environment.GetEnvironmentVariables()`): the hidden `=C:` entries, whose name
    /// begins with `=`, are left out.
    pub fn process() -> Variables {
        std::env::vars_os()
            .map(|(name, value)| (name.to_string_lossy().into_owned(), value.to_string_lossy().into_owned()))
            .filter(|(name, _)| !name.is_empty() && !name.starts_with('='))
            .collect()
    }

    pub fn get(&self, name: &str) -> Option<&str> {
        self.0.get(&fold(name)).map(|(_, value)| value.as_str())
    }

    pub fn contains(&self, name: &str) -> bool {
        self.0.contains_key(&fold(name))
    }

    /// Sets `name` (C# `dictionary[name] = value`): a name already there in another case keeps its spelling.
    pub fn set(&mut self, name: impl Into<String>, value: impl Into<String>) {
        let name = name.into();
        let value = value.into();
        match self.0.entry(fold(&name)) {
            std::collections::btree_map::Entry::Occupied(mut entry) => entry.get_mut().1 = value,
            std::collections::btree_map::Entry::Vacant(entry) => {
                entry.insert((name, value));
            }
        }
    }

    pub fn remove(&mut self, name: &str) -> Option<String> {
        self.0.remove(&fold(name)).map(|(_, value)| value)
    }

    pub fn len(&self) -> usize {
        self.0.len()
    }

    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    /// Each variable once, with the spelling it keeps.
    pub fn iter(&self) -> impl Iterator<Item = (&str, &str)> {
        self.0.values().map(|(name, value)| (name.as_str(), value.as_str()))
    }

    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.0.values().map(|(name, _)| name.as_str())
    }

    /// One entry per name ignoring case, for the ports' maps (`CliEnvironment::values`, `SpawnRequest::env`).
    pub fn into_map(self) -> BTreeMap<String, String> {
        self.0.into_values().collect()
    }
}

/// Later entries win, names compared ignoring case (C# `WindowsCommand.Variables`).
impl<K: Into<String>, V: Into<String>> FromIterator<(K, V)> for Variables {
    fn from_iter<I: IntoIterator<Item = (K, V)>>(entries: I) -> Variables {
        let mut variables = Variables::new();
        for (name, value) in entries {
            variables.set(name, value);
        }
        variables
    }
}

impl<'a> From<&'a BTreeMap<String, String>> for Variables {
    fn from(map: &'a BTreeMap<String, String>) -> Variables {
        map.iter().map(|(name, value)| (name.clone(), value.clone())).collect()
    }
}

impl fmt::Debug for Variables {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_set().entries(self.names()).finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_one_name_in_any_case_and_keep_their_first_spelling() {
        let mut variables: Variables = [("Path", "p"), ("PathExt", ".EXE")].into_iter().collect();
        assert_eq!(variables.get("PATH"), Some("p"));
        assert_eq!(variables.get("PATHEXT"), Some(".EXE"));
        variables.set("PATH", "q");
        assert_eq!(variables.iter().collect::<Vec<_>>(), [("Path", "q"), ("PathExt", ".EXE")]);
        assert!(!format!("{variables:?}").contains('q'), "values never show");
        assert!(!Variables::process().names().any(|name| name.starts_with('=')));
    }
}
