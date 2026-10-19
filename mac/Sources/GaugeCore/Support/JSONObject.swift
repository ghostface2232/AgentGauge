import Foundation

/// Tolerant read-only view over a decoded JSON object. These usage endpoints are
/// undocumented, so every accessor returns nil for an absent key, a JSON null, or a value of
/// an unexpected type instead of throwing — a shape change drops one field, never the
/// whole response.
struct JSONObject {
    let storage: [String: Any]

    init?(_ value: Any?) {
        guard let dictionary = value as? [String: Any] else { return nil }
        storage = dictionary
    }

    static func parse(_ data: Data) throws -> JSONObject {
        let value = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        guard let object = JSONObject(value) else { throw UsageFetchError.invalidResponse }
        return object
    }

    /// Whether the key is present at all, even as an explicit null.
    func contains(_ key: String) -> Bool { storage[key] != nil }

    func object(_ key: String) -> JSONObject? { JSONObject(storage[key]) }

    func objects(_ key: String) -> [JSONObject]? {
        guard let array = storage[key] as? [Any] else { return nil }
        return array.compactMap { JSONObject($0) }
    }

    func string(_ key: String) -> String? { storage[key] as? String }

    func double(_ key: String) -> Double? {
        guard let raw = storage[key] else { return nil }
        if let number = raw as? NSNumber {
            // JSONSerialization decodes true/false as NSNumber too; a flag is not a quantity.
            guard CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
            let value = number.doubleValue
            return value.isFinite ? value : nil
        }
        if let text = raw as? String {
            // Double(String) is locale-independent, so a Korean/Japanese locale cannot
            // change how a machine-facing number parses.
            guard let value = Double(text.trimmingCharacters(in: .whitespaces)), value.isFinite else { return nil }
            return value
        }
        return nil
    }

    func int64(_ key: String) -> Int64? {
        guard let value = double(key), value == value.rounded(), abs(value) < 9.0e18 else { return nil }
        return Int64(value)
    }

    func bool(_ key: String) -> Bool? {
        guard let number = storage[key] as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
        return number.boolValue
    }

    func date(_ key: String) -> Date? {
        guard let text = string(key) else { return nil }
        return Timestamp.parse(text)
    }
}
