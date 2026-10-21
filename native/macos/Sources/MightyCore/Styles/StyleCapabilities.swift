import Foundation

/// The app-side features a manifest may name, each with its closed states.
/// Frozen with the tag (§1.8). Empty since v7: the one feature there was left
/// with the bundled style that used it. The `capabilities` field and the two
/// rule kinds that read a feature stay in the schema, so any name a manifest
/// declares is now refused as unknown.
public enum StyleCapabilityID {
    public static let vocabulary: [String: [String]] = [:]
    public static var all: Set<String> { Set(vocabulary.keys) }
}
