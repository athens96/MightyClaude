//! The ids the page names controls by (C# `WebNativeReader.Id`: one integer per control object, from one counter, the same for that
//! object on every read).
//!
//! Builders make new elements on every read, so an id is kept per *identity* instead: the surface, the control's key path (its
//! section, then its position among its siblings or the key the builder gave it), its kind and its words (a button's name and label,
//! a field's name). A control drawn again in the same place with the same words keeps its id; one whose words changed (Sign in → Sign
//! out) gets a new one, as C#'s rebuilt control did, so an input the page sent for what it showed before reaches nothing. An identity
//! never seen gets the next integer of the host's one counter; ids are never given twice. After each read the identities it did not
//! reach are forgotten ([`Ids::prune`]), so the table holds one read's controls, not every control ever drawn.

use std::collections::{HashMap, HashSet};

/// The host's counter and the ids it gave, by surface and identity.
#[derive(Debug, Default)]
pub struct Ids {
    next: i64,
    known: HashMap<String, HashMap<String, i64>>,
    /// The identities the read under way reached, by surface.
    seen: HashMap<String, HashSet<String>>,
}

impl Ids {
    /// The id of `identity` in `surface`: the one it had, else the next.
    pub fn id(&mut self, surface: &str, identity: &str) -> i64 {
        self.seen.entry(surface.to_string()).or_default().insert(identity.to_string());
        let known = self.known.entry(surface.to_string()).or_default();
        if let Some(id) = known.get(identity) {
            return *id;
        }
        self.next += 1;
        known.insert(identity.to_string(), self.next);
        self.next
    }

    /// A read of `surface` ended: the identities it did not reach are forgotten.
    pub fn prune(&mut self, surface: &str) {
        let seen = self.seen.remove(surface).unwrap_or_default();
        if let Some(known) = self.known.get_mut(surface) {
            known.retain(|identity, _| seen.contains(identity));
        }
    }

    /// How many identities `surface` holds (tests).
    pub fn len(&self, surface: &str) -> usize {
        self.known.get(surface).map_or(0, HashMap::len)
    }

    /// A surface closed: its identities go (a surface of the same name opened later gets new ids, as C#'s new controls did).
    pub fn forget(&mut self, surface: &str) {
        self.known.remove(surface);
        self.seen.remove(surface);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_identity_keeps_its_id_and_ids_are_never_given_twice() {
        let mut ids = Ids::default();
        let a = ids.id("settings", "/0|button|Save");
        let b = ids.id("settings", "/1|button|Save");
        assert_eq!((a, b), (1, 2));
        assert_eq!(ids.id("settings", "/0|button|Save"), 1, "the same control read again");
        assert_eq!(ids.id("dialog-1", "/0|button|Save"), 3, "another surface's control is another control");
        ids.forget("settings");
        assert_eq!(ids.id("settings", "/0|button|Save"), 4, "a surface opened again gets new ids");
    }

    /// Review L2: a read keeps only what it reached; the table never grows past one read's controls.
    #[test]
    fn a_read_forgets_what_it_did_not_reach() {
        let mut ids = Ids::default();
        for row in 0..100 {
            ids.id("settings", &format!("/{row}|button|Row {row}"));
            ids.prune("settings");
        }
        assert_eq!(ids.len("settings"), 1, "each read reached one row");
        ids.id("settings", "/a|button|A");
        ids.id("settings", "/b|button|B");
        ids.prune("settings");
        let a = ids.id("settings", "/a|button|A");
        ids.prune("settings");
        assert_eq!(ids.len("settings"), 1);
        assert_eq!(ids.id("settings", "/a|button|A"), a, "a control read again keeps its id");
    }
}
