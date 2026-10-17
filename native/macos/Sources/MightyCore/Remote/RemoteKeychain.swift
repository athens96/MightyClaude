import Foundation
import Security

/// The connection keys of saved remote connections, one Keychain item per
/// connection, readable only on this Mac once it is unlocked. The key never goes
/// into workspace state or the connections file. A key is read when its
/// connection is first used (connect, refresh, a run), never at launch, so macOS
/// asks for Keychain access only then; the app's stable signing identity lets an
/// "Always Allow" answer last across updates.
enum RemoteKeychain {
    private static let service = "dev.mightyclaude.native.remote"

    static func load(_ id: String) -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: id,
                                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data,
              let key = String(data: data, encoding: .utf8), RemoteWorkspaceValidation.key(key) else { return nil }
        return key
    }

    static func save(_ key: String, id: String) -> Bool {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: id]
        let value = Data(key.utf8)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: value] as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var item = query
        item[kSecValueData as String] = value
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    static func delete(_ id: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: id]
        SecItemDelete(query as CFDictionary)
    }
}
