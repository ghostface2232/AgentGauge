// Feature rows, merged into `Strings.table`. Columns: [Korean, English, Japanese]. Shared keys
// follow the Windows meaning (Localization/Strings.cs) in Mac phrasing: title-case English
// control labels and the system's own アップデート for software updates. Every key lives in
// exactly one feature table; the merge keeps the first, so a duplicate would be dead.
extension Strings {
    static let features: [String: [String]] = [
        // Card header
        "Status_Refreshing": ["새로고침 중…", "Refreshing…", "更新中…"],
        "Tooltip_RefreshIssue": ["마지막 갱신 실패 · 이전 값을 표시 중", "Last refresh failed · showing previous data",
                                 "最終更新に失敗・以前のデータを表示中"],

        // Display settings
        "Settings_Display": ["표시", "Display", "表示"],
        "Settings_ViewMode": ["사용량 보기", "View Mode", "表示方式"],
        "ViewMode_Bar": ["막대", "Bars", "バー"],
        "ViewMode_Gauge": ["게이지", "Gauges", "ゲージ"],
        "Settings_Sparkline": ["소진 추이 그래프", "Trend Sparkline", "消費推移グラフ"],
        "Settings_SparklineHelp": ["최근 6시간 동안 남은 사용량의 변화를 막대 옆에 그립니다.",
                                   "Draws the last six hours of remaining quota beside each bar.",
                                   "直近6時間の残量の推移を各バーの横に描きます。"],
        "Settings_PaceModel": ["주간 페이스 기준", "Weekly Pace Basis", "週間ペース基準"],
        "Settings_PaceModelHelp": ["여러 날에 걸친 한도의 페이스를 어떤 요일 기준으로 계산할지 정합니다.",
                                   "Which days a multi-day limit is expected to be spent on, for the pace caption.",
                                   "複数日にわたる上限のペースをどの曜日基準で計算するかを決めます。"],
        "Settings_ApiCost": ["API 환산 비용 표시", "Show API-Equivalent Cost", "API換算コストを表示"],
        "Settings_ApiCostHelp": ["Claude Code와 Codex의 로컬 세션 로그를 읽어 이번 달 사용량을 공개 API 요금으로 환산합니다. 구독 요금과는 무관한 추정치입니다.",
                                 "Reads Claude Code and Codex's local session logs to price this month's tokens at public API rates — an estimate unrelated to what your subscription costs.",
                                 "Claude CodeとCodexのローカルセッションログを読み、今月のトークンを公開API料金で換算します。サブスクリプション料金とは無関係の目安です。"],

        // Antigravity has no AgentGauge-readable credential; describe the source instead.
        "Auth_Antigravity": ["Antigravity 앱에서 사용량을 읽어옵니다.", "Reads usage from the Antigravity app.",
                             "Antigravityアプリから使用量を読み取ります。"],

        // Updates
        "Settings_About": ["정보", "About", "情報"],
        "Update_Apply": ["업데이트", "Update", "アップデート"],
        "Update_Check": ["업데이트 확인", "Check for Updates", "アップデートを確認"],
        "Update_Checking": ["업데이트 확인 중…", "Checking for updates…", "アップデートを確認中…"],
        "Update_Available": ["업데이트 가능", "Update available", "アップデートがあります"],
        "Update_AvailableVersion": ["{0} 업데이트 가능", "Version {0} is available", "バージョン{0}があります"],
        "Update_UpToDate": ["최신 버전입니다", "Up to date", "最新です"],
        "Update_CheckFailed": ["업데이트를 확인하지 못했습니다.", "Couldn't check for updates.", "アップデートを確認できませんでした。"],
        "Update_Downloading": ["업데이트 다운로드 중…", "Downloading update…", "アップデートをダウンロード中…"],
        "Update_Installing": ["업데이트를 설치하고 다시 시작합니다…", "Installing update and restarting…", "アップデートをインストールして再起動します…"],
        "Update_InstallFailed": ["업데이트 설치를 시작하지 못했습니다.", "Couldn't start the update installation.", "アップデートのインストールを開始できませんでした。"],
        "Update_NotWritable": ["AgentGauge를 응용 프로그램 폴더로 옮긴 뒤 다시 시도하세요.",
                               "Move AgentGauge to the Applications folder, then try again.",
                               "AgentGaugeをアプリケーションフォルダに移動してから、もう一度お試しください。"],
        "Update_Done": ["AgentGauge가 {0}(으)로 업데이트되었습니다.", "AgentGauge was updated to {0}.", "AgentGaugeを{0}にアップデートしました。"],
        // Public service status chip
        "ServiceStatus_Minor": ["서비스 장애", "Service degradation", "サービスの一部障害"],
        "ServiceStatus_Major": ["서비스 장애", "Service outage", "サービス障害"],
        "ServiceStatus_Maintenance": ["서비스 점검", "Service maintenance", "サービスメンテナンス"],
        "ServiceStatus_Unknown": ["서비스 상태 확인 불가", "Service status unavailable", "サービス状態を確認できません"],
        "ServiceStatus_Stale": ["서비스 상태 미확인", "Service status unconfirmed", "サービス状態は未確認"],
        "ServiceStatus_Detail": ["서비스 전체 상태 · 내 계정의 오류 원인과 다를 수 있습니다.\n{0}\n마지막 확인: {1}",
                                 "Service-wide status; may be unrelated to your account's error.\n{0}\nLast checked: {1}",
                                 "サービス全体の状態です。アカウントのエラー原因とは異なる場合があります。\n{0}\n最終確認: {1}"],
    ]
}
