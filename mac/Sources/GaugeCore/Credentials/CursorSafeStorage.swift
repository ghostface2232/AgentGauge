import CommonCrypto
import Foundation

/// Opens the token envelope Cursor 3.27+ writes to `state.vscdb`. It is Chromium's macOS
/// `os_crypt` format, which Electron's `safeStorage` uses: `v10` + AES-128-CBC ciphertext,
/// keyed by PBKDF2-HMAC-SHA1 over the "Cursor Safe Storage" keychain password. The salt,
/// iteration count and space-filled IV are fixed by that format, not chosen here.
enum CursorSafeStorage {
    static let envelopePrefix = "cursor-safe-storage:v1:"
    static let keychainService = "Cursor Safe Storage"
    static let keychainAccount = "Cursor"

    private static let versionTag = Data("v10".utf8)
    private static let salt = Data("saltysalt".utf8)
    private static let iterations: UInt32 = 1003
    private static let iv = Data(repeating: 0x20, count: kCCBlockSizeAES128)

    static func isEnvelope(_ value: String) -> Bool { value.hasPrefix(envelopePrefix) }

    /// The decrypted token, or nil when the envelope is malformed or the password is wrong.
    static func decrypt(_ envelope: String, password: Data) -> String? {
        guard isEnvelope(envelope),
              let blob = Data(base64Encoded: String(envelope.dropFirst(envelopePrefix.count))),
              blob.starts(with: versionTag) else { return nil }
        let ciphertext = blob.dropFirst(versionTag.count)
        guard !ciphertext.isEmpty, ciphertext.count % kCCBlockSizeAES128 == 0,
              let key = deriveKey(password) else { return nil }

        var output = Data(count: ciphertext.count + kCCBlockSizeAES128)
        let capacity = output.count
        var produced = 0
        let status = output.withUnsafeMutableBytes { out in
            ciphertext.withUnsafeBytes { input in
                key.withUnsafeBytes { keyBytes in
                    iv.withUnsafeBytes { ivBytes in
                        CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                keyBytes.baseAddress, kCCKeySizeAES128, ivBytes.baseAddress,
                                input.baseAddress, ciphertext.count, out.baseAddress, capacity, &produced)
                    }
                }
            }
        }
        guard status == kCCSuccess else { return nil }
        return String(data: output.prefix(produced), encoding: .utf8)
    }

    static func deriveKey(_ password: Data) -> Data? {
        var key = Data(count: kCCKeySizeAES128)
        let status = key.withUnsafeMutableBytes { out in
            password.withUnsafeBytes { pw in
                salt.withUnsafeBytes { saltBytes in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2), pw.bindMemory(to: Int8.self).baseAddress, password.count,
                        saltBytes.bindMemory(to: UInt8.self).baseAddress, salt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), iterations,
                        out.bindMemory(to: UInt8.self).baseAddress, kCCKeySizeAES128)
                }
            }
        }
        return status == kCCSuccess ? key : nil
    }
}
