// History rows, merged into `Strings.table`. Columns: [Korean, English, Japanese].
extension Strings {
    static let history: [String: [String]] = [
        // The weekly pace model picker's options. Uniform is the original even spread; work
        // days flattens weekends; automatic shapes the curve from the recorded history once
        // two weeks exist.
        "PaceModel_Uniform": ["매일 균등 (7일)", "Every day (7 days)", "毎日均等（7日）"],
        "PaceModel_WorkDays": ["근무일 (월–금)", "Workdays (Mon–Fri)", "平日（月〜金）"],
        "PaceModel_Automatic": ["자동 (사용 추이)", "Automatic (from history)", "自動（使用傾向）"],

        // Sparkline hover ({0}=HH:mm, {1}=whole remaining percent). Windows formats {1:0};
        // here the caller passes the already-rounded integer.
        "Burndown_Hover": ["{0} · {1}% 남음", "{0} · {1}% left", "{0} · 残り{1}%"],

        // ETA-to-exhaustion captions, from the measured recent burn rate (UsageEtaClassifier).
        "Eta_InDays": ["{0}일 후 소진 예상", "Runs out in ~{0} days", "{0}日後に使い切る見込み"],
        "Eta_InDay": ["{0}일 후 소진 예상", "Runs out in ~{0} day", "{0}日後に使い切る見込み"],
        "Eta_InHoursMinutes": ["{0}시간 {1}분 후 소진 예상", "Runs out in ~{0}h {1}m", "{0}時間{1}分後に使い切る見込み"],
        "Eta_InHours": ["{0}시간 후 소진 예상", "Runs out in ~{0}h", "{0}時間後に使い切る見込み"],
        "Eta_InMinutes": ["{0}분 후 소진 예상", "Runs out in ~{0}m", "{0}分後に使い切る見込み"],
    ]
}
