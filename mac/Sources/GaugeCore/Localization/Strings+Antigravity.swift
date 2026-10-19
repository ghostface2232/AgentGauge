// Antigravity rows, merged into `Strings.table`. Columns: [Korean, English, Japanese].
extension Strings {
    static let antigravity: [String: [String]] = [
        // Antigravity has no Gauge-readable credential or CLI login: its session lives in the
        // app, so a signed-out or not-installed card points there.
        "Auth_Missing_Antigravity": ["Antigravity 앱에서 로그인하세요.",
                                     "Sign in from the Antigravity app.",
                                     "Antigravityアプリでログインしてください。"],
    ]
}
