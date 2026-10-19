// Columns: [Korean, English, Japanese] — the order of `AppLanguage`. Shared keys keep the
// Windows app's wording (Localization/Strings.cs) so both platforms read the same.
enum Strings {
    /// Every feature's rows merged into one lookup. Features keep their rows in their own
    /// `Strings+<Feature>.swift` table so they read side by side with the code that uses them.
    static let table: [String: [String]] = {
        var merged = core
        for part in [features, antigravity, apiCost, history] {
            merged.merge(part) { current, _ in current }
        }
        return merged
    }()

    static let core: [String: [String]] = [
        // Usage windows
        "Label_FiveHour": ["5시간", "5-hour", "5時間"],
        "Label_Weekly": ["주간", "Weekly", "週間"],
        "Label_BillingCycle": ["사용량", "Usage", "使用量"],
        "Label_ModelQuota": ["모델", "Model", "モデル"],
        // GitHub Copilot's per-quota labels (its monthly chat / completions / premium).
        "Label_Copilot_Chat": ["채팅", "Chat", "チャット"],
        "Label_Copilot_Completions": ["코드 자동 완성", "Completions", "コード補完"],
        "Label_Copilot_Premium": ["프리미엄 요청", "Premium requests", "プレミアム要求"],

        // Reset phrasing ({0}=n, {1}=date)
        "Reset_Done": ["초기화됨", "Reset", "リセット済み"],
        "Reset_Soon": ["곧 초기화", "Resetting soon", "まもなくリセット"],
        "Reset_Unknown": ["초기화 시각을 확인할 수 없습니다", "Reset time unavailable", "リセット時刻を確認できません"],
        "Reset_InDays": ["{0}일 후 초기화 ({1})", "Resets in {0} days ({1})", "{0}日後（{1}）にリセット"],
        "Reset_InDay": ["{0}일 후 초기화 ({1})", "Resets in {0} day ({1})", "{0}日後（{1}）にリセット"],
        "Reset_InHoursMinutes": ["{0}시간 {1}분 후 초기화", "Resets in {0}h {1}m", "{0}時間{1}分後にリセット"],
        "Reset_InHours": ["{0}시간 후 초기화", "Resets in {0}h", "{0}時間後にリセット"],
        "Reset_InMinutes": ["{0}분 후 초기화", "Resets in {0}m", "{0}分後にリセット"],
        "DateFormat_MonthDay": ["M월 d일", "MMM d", "M月d日"],

        // Pace caption
        "Pace_OnPace": ["0% · 적정", "0% · On pace", "0% · 適正ペース"],
        "Pace_Deficit": ["−{0}% · 빠르게 소진 중", "−{0}% · Deficit", "−{0}% · 不足"],
        "Pace_Reserve": ["+{0}% · 여유 있음", "+{0}% · Reserve", "+{0}% · 余裕"],
        "Pace_Tooltip": ["남은 시간에 비해 사용 속도가 얼마나 빠르거나 느린지 보여줍니다.",
                         "Shows how fast or slow this window is being used relative to the time left.",
                         "残り時間に対して使用ペースがどれだけ速いか遅いかを示します。"],

        // Notifications ({0}=tool, {1}=label or scope, …)
        "Notif_ThresholdTitle": ["{0} · {1} 한도 {2}% 사용", "{0} · {2}% of {1} limit used", "{0} · {1} 上限{2}%に到達"],
        "Notif_ThresholdScopedTitle": ["{0} · {1} {2} 한도 {3}% 사용", "{0} · {3}% of {1} {2} limit used", "{0} · {1} {2} 上限{3}%に到達"],
        "Notif_ResetTitle": ["{0} · {1} 한도 초기화", "{0} · {1} limit reset", "{0} · {1} 上限リセット"],
        "Notif_ResetScopedTitle": ["{0} · {1} {2} 한도 초기화", "{0} · {1} {2} limit reset", "{0} · {1} {2} 上限リセット"],
        "Notif_ResetMessage": ["한도가 초기화되어 현재 {0}% 남았습니다.", "Reset — {0}% now available", "利用枠が{0}%にリセットされました"],

        // Popover
        "LastUpdated_Never": ["아직 갱신되지 않음", "Not updated yet", "未更新"],
        "LastUpdated_At": ["마지막 갱신 {0}", "Updated {0}", "{0} 更新"],
        "Loading": ["사용량을 불러오는 중…", "Loading usage…", "使用量を読み込み中…"],
        "NoData": ["데이터 없음", "No data", "データなし"],
        "Card_RefreshFailed": ["갱신 실패", "Update failed", "更新失敗"],
        "ResetCredits": ["초기화 {0}회", "{0} resets", "リセット {0}回"],
        "Tooltip_ResetCredits": ["사용할 수 있는 사용량 초기화 {0}회", "{0} rate-limit resets available", "利用可能な使用量リセット {0}回"],
        "Empty_NoTools": ["표시 중인 도구가 없습니다.", "No tools are shown.", "表示中のツールがありません。"],
        "Action_Refresh": ["새로 고침", "Refresh", "更新"],
        "Action_Settings": ["설정…", "Settings…", "設定…"],
        "Action_Quit": ["AgentGauge 종료", "Quit AgentGauge", "AgentGaugeを終了"],
        "Action_SignIn": ["로그인…", "Sign In…", "ログイン…"],
        "Action_TryAgain": ["다시 시도", "Try Again", "再試行"],
        "Ax_Used": ["{0}, {1}퍼센트 사용, {2}", "{0}, {1} percent used, {2}", "{0}、{1}パーセント使用、{2}"],
        "Ax_Remaining": ["{0}, {1}퍼센트 남음, {2}", "{0}, {1} percent remaining, {2}", "{0}、残り{1}パーセント、{2}"],

        // Authentication states
        "Auth_SignedIn": ["로그인됨", "Signed in", "ログイン済み"],
        "Auth_SignedInWithPlan": ["로그인됨 · {0}", "Signed in · {0}", "ログイン済み · {0}"],
        "Auth_Missing": ["사용량을 확인하려면 로그인해 주세요.", "Sign in to see usage.", "使用量を表示するにはログインしてください。"],
        "Auth_Expired": ["로그인이 만료되었거나 거부되었습니다. 다시 로그인하세요.",
                         "Your sign-in expired or was rejected. Please sign in again.",
                         "ログイン有効期限が切れたか拒否されました。再度ログインしてください。"],
        "Auth_KeychainDenied": ["키체인 접근이 허용되지 않았습니다. 다시 시도하면 macOS가 다시 묻습니다.",
                                "Keychain access wasn't allowed. Try again and macOS will ask once more.",
                                "キーチェーンへのアクセスが許可されませんでした。再試行するとmacOSが再度確認します。"],
        "Auth_ReadFailed": ["로그인 정보를 읽을 수 없습니다. CLI에서 다시 로그인하세요.",
                            "Couldn't read the sign-in. Sign in again from the CLI.",
                            "ログイン情報を読み取れません。CLIで再度ログインしてください。"],
        "Auth_Checking": ["확인 중…", "Checking…", "確認中…"],
        "Auth_Missing_Cursor": ["Cursor 앱에서 로그인하세요.", "Sign in from the Cursor app.", "Cursorアプリでログインしてください。"],
        "Auth_Expired_Cursor": ["Cursor 로그인이 만료되었습니다. Cursor 앱에서 다시 로그인하세요.",
                                "Your Cursor sign-in expired. Sign in again in the Cursor app.",
                                "Cursorのログイン有効期限が切れました。Cursorアプリで再度ログインしてください。"],
        "Auth_ReadFailed_Cursor": ["Cursor 로그인 정보를 읽을 수 없습니다. Cursor 앱에서 다시 로그인하세요.",
                                   "Couldn't read the Cursor sign-in. Sign in again in the Cursor app.",
                                   "Cursorのログイン情報を読み取れません。Cursorアプリで再度ログインしてください。"],
        "Auth_Missing_Copilot": ["GitHub CLI(gh)로 로그인하면 사용량을 확인할 수 있습니다.",
                                 "Sign in with the GitHub CLI (gh) to see usage.",
                                 "GitHub CLI（gh）でログインすると使用量を表示できます。"],
        "Action_OpenApp": ["{0} 열기", "Open {0}", "{0}を開く"],

        // Settings
        "Settings_General": ["일반", "General", "一般"],
        "Settings_LaunchAtLogin": ["로그인 시 열기", "Open at Login", "ログイン時に開く"],
        "Settings_LaunchAtLoginApproval": ["시스템 설정 › 일반 › 로그인 항목에서 허용해야 합니다.",
                                           "Allow it in System Settings › General › Login Items.",
                                           "システム設定 › 一般 › ログイン項目で許可してください。"],
        "Settings_ShowPercent": ["메뉴 막대에 사용률 표시", "Show Percentage in Menu Bar", "メニューバーに使用率を表示"],
        "Settings_DisplayBasis": ["표시 기준", "Show As", "表示基準"],
        "Basis_Used": ["사용량", "Used", "使用量"],
        "Basis_Remaining": ["남은 양", "Remaining", "残量"],
        "Settings_Notifications": ["알림", "Notifications", "通知"],
        "Settings_NotifyThresholds": ["한도 도달 알림", "Threshold Alerts", "上限到達の通知"],
        "Settings_NotifyResets": ["초기화 알림", "Reset Alerts", "リセットの通知"],
        "Settings_NotificationsDenied": ["시스템 설정에서 AgentGauge 알림이 꺼져 있습니다.",
                                         "Notifications for AgentGauge are turned off in System Settings.",
                                         "システム設定でAgentGaugeの通知がオフになっています。"],
        "Action_OpenSystemSettings": ["시스템 설정 열기", "Open System Settings", "システム設定を開く"],
        "Settings_Accounts": ["계정", "Accounts", "アカウント"],
        "Settings_ShowTool": ["표시", "Show", "表示"],
        "Settings_ShowToolHelp": ["끄면 이 도구를 숨기고 새로 고침을 멈춥니다.",
                                  "Turning this off hides the tool and pauses its refresh.",
                                  "オフにするとこのツールを非表示にし、更新を停止します。"],
        "Settings_Version": ["버전", "Version", "バージョン"],
        "Settings_SaveFailed": ["설정을 저장하지 못했습니다.", "Couldn't save the setting.", "設定を保存できませんでした。"],
    ]
}
