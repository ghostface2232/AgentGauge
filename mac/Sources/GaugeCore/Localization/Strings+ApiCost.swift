// ApiCost rows, merged into `Strings.table`. Columns: [Korean, English, Japanese].
// API-equivalent cost: the month's tokens from the CLIs' local session logs priced at
// public API list rates. An estimate beside the plan label, never a bill; the "+" form
// marks a floor because some model had no known rate.
extension Strings {
    static let apiCost: [String: [String]] = [
        "ApiCost_Value": ["≈ ${0}", "≈ ${0}", "≈ ${0}"],
        "ApiCost_ValueFloor": ["≈ ${0}+", "≈ ${0}+", "≈ ${0}+"],
        "Tooltip_ApiCost": [
            "이번 달 API 환산 추정 · 로컬 세션 로그의 토큰을 공개 API 요금으로 계산한 값이며 구독 요금과 무관합니다.\n입력 {0} · 캐시 읽기 {1} · 캐시 쓰기 {2} · 출력 {3}",
            "This month at API list prices — an estimate from the tokens in local session logs, unrelated to what the subscription costs.\nInput {0} · cache read {1} · cache write {2} · output {3}",
            "今月のAPI換算の目安 · ローカルのセッションログのトークンを公開API料金で計算した値で、サブスクリプション料金とは無関係です。\n入力 {0} · キャッシュ読取 {1} · キャッシュ書込 {2} · 出力 {3}",
        ],
        "ApiCost_Unpriced": [
            "\n가격 미확인 모델({0})의 토큰 {1}개는 제외됨",
            "\n{1} tokens from unpriced models ({0}) not included",
            "\n価格未確認のモデル（{0}）の {1} トークンは未計上",
        ],
    ]
}
