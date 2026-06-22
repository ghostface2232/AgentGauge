namespace Gauge.Localization;

/// <summary>
/// The full translation table. Each entry maps a stable key to one string per
/// <see cref="AppLanguage"/>, indexed by the enum's integer value
/// ({ Korean, English, Japanese }). A null cell falls back to English, then to the key.
///
/// We keep translations in code (not .resw/.resx) deliberately: Gauge is an unpackaged
/// WinUI app where the MRT/PRI resource path is fragile, the string count is small, and
/// the language is fixed once at startup — so a plain dictionary read is all that's
/// needed and there is no satellite-assembly build step to get wrong.
/// </summary>
internal static class Strings
{
    public static readonly IReadOnlyDictionary<string, string?[]> Table = new Dictionary<string, string?[]>
    {
        // ── Shell / common UI ──────────────────────────────────────────────
        //                       { Korean,            English,             Japanese }
        ["Settings"]          = ["설정",             "Settings",          "設定"],
        ["Tooltip_Disconnect"] = ["연결 해제",        "Disconnect",        "接続解除"],
        ["Tooltip_Refresh"]   = ["새로고침",          "Refresh",           "更新"],
        ["Tooltip_Back"]      = ["뒤로",             "Back",              "戻る"],
        ["AddService"]        = ["서비스 추가",        "Add service",       "サービスを追加"],
        ["NoServicesToAdd"]   = ["추가할 서비스가 없습니다", "No services to add", "追加できるサービスがありません"],

        // ── Global settings (toggles above the service list) ───────────────
        ["Settings_Notifications"] = ["알림",          "Notifications",     "通知"],
        ["Settings_NotificationsDesc"] = ["한도 도달·초기화 시 알림", "Alert on limit reached and reset", "上限到達・リセット時に通知"],
        ["Settings_StartOnBoot"]   = ["시작프로그램 등록", "Start on boot",   "スタートアップに登録"],
        ["Settings_StartOnBootDesc"] = ["Windows 시작 시 자동 실행", "Launch automatically when Windows starts", "Windows起動時に自動実行"],
        ["Settings_ViewMode"]      = ["보기 방식",        "View mode",         "表示方式"],
        ["Settings_ViewModeDesc"]  = ["사용량 표시 방식 선택", "How usage is displayed", "使用量の表示方法を選択"],
        ["ViewMode_Bar"]          = ["바 모드",          "Bar",               "バー"],
        ["ViewMode_Gauge"]        = ["게이지 모드",       "Gauge",             "ゲージ"],
        ["Tray_StartOnBoot"]  = ["시작프로그램 등록",   "Start on boot",     "スタートアップに登録"],
        ["Tray_Exit"]         = ["종료",             "Exit",              "終了"],

        // ── Login button / auth card ───────────────────────────────────────
        ["Login"]             = ["로그인",            "Sign in",           "ログイン"],
        ["Login_Running"]     = ["로그인 중…",        "Signing in…",       "ログイン中…"],
        ["Login_Switch"]      = ["계정 전환",          "Switch account",    "アカウント切替"],

        // ── Update footer ──────────────────────────────────────────────────
        ["Update_Apply"]      = ["업데이트",          "Update",            "更新"],
        ["Update_Check"]      = ["업데이트 확인",       "Check for updates", "更新を確認"],
        ["Update_Checking"]   = ["업데이트 확인 중…",   "Checking for updates…", "更新を確認中…"],
        ["Update_Available"]  = ["업데이트 가능",       "Update available",  "更新があります"],
        ["Update_UpToDate"]   = ["최신 버전입니다",     "Up to date",        "最新です"],
        ["Update_CheckFailed"] = ["업데이트를 확인하지 못했습니다.", "Couldn't check for updates.", "更新を確認できませんでした。"],
        ["Update_Downloading"] = ["업데이트 다운로드 중…", "Downloading update…", "更新をダウンロード中…"],
        ["Update_Installing"] = ["업데이트를 설치하고 다시 시작합니다…", "Installing update and restarting…", "更新をインストールして再起動します…"],
        ["Update_InstallFailed"] = ["업데이트 설치를 시작하지 못했습니다.", "Couldn't start the update installation.", "更新のインストールを開始できませんでした。"],

        // ── Usage view ─────────────────────────────────────────────────────
        ["LastUpdated_Never"] = ["갱신 전",           "Not updated yet",   "未更新"],
        ["LastUpdated_At"]    = ["{0} 갱신",          "Updated {0}",       "{0} 更新"],            // {0} = HH:mm
        ["Loading"]           = ["사용량을 불러오는 중…", "Loading usage…",   "使用量を読み込み中…"],
        ["Empty_FetchFailed"] = ["사용량 정보를 불러올 수 없습니다.\n설정에서 로그인 상태를 확인하세요.",
                                 "Couldn't load usage.\nCheck your sign-in status in settings.",
                                 "使用量を読み込めませんでした。\n設定でログイン状態を確認してください。"],
        ["Empty_NoHistory"]   = ["사용 기록이 아직 없습니다.", "No usage yet.", "使用履歴がまだありません。"],
        ["NoData"]            = ["데이터 없음",         "No data",           "データなし"],
        ["Tray_NoData"]       = ["{0} 데이터 없음",     "{0} no data",       "{0} データなし"],       // {0} = tool
        ["Tray_Tooltip"]      = ["AgentGauge — {0}\n갱신: {1}", "AgentGauge — {0}\nUpdated: {1}", "AgentGauge — {0}\n更新: {1}"], // {0}=summary {1}=time

        // ── Usage-window labels (by UsageWindowType) ───────────────────────
        ["Label_FiveHour"]    = ["5시간",            "5-hour",            "5時間"],
        ["Label_Weekly"]      = ["주간",             "Weekly",            "週間"],
        ["Label_BillingCycle"] = ["사용량",          "Usage",             "使用量"],
        ["Label_ModelQuota"]  = ["모델",             "Model",             "モデル"],
        // GitHub Copilot's per-quota window labels (its monthly chat / completions / premium).
        ["Label_Copilot_Chat"] = ["채팅",            "Chat",              "チャット"],
        ["Label_Copilot_Completions"] = ["코드 완성", "Completions",      "コード補完"],
        ["Label_Copilot_Premium"] = ["프리미엄 요청", "Premium requests", "プレミアム要求"],

        // ── Reset-time phrases ─────────────────────────────────────────────
        ["Reset_Done"]        = ["초기화됨",          "Reset",             "リセット済み"],
        ["Reset_Soon"]        = ["곧 초기화",         "Resetting soon",    "まもなくリセット"],
        ["Reset_Unknown"]     = ["초기화 시각을 확인할 수 없습니다", "Reset time unavailable", "リセット時刻を確認できません"],
        ["Reset_InDays"]      = ["{0}일 후 ({1}) 초기화", "Resets in {0} days ({1})", "{0}日後（{1}）にリセット"], // {0}=n {1}=date
        ["Reset_InDay"]       = ["{0}일 후 ({1}) 초기화", "Resets in {0} day ({1})",  "{0}日後（{1}）にリセット"], // singular (English)
        ["Reset_InHoursMinutes"] = ["{0}시간 {1}분 후 초기화", "Resets in {0}h {1}m", "{0}時間{1}分後にリセット"],
        ["Reset_InHours"]     = ["{0}시간 후 초기화",  "Resets in {0}h",    "{0}時間後にリセット"],
        ["Reset_InMinutes"]   = ["{0}분 후 초기화",    "Resets in {0}m",    "{0}分後にリセット"],
        ["DateFormat_MonthDay"] = ["M월 d일",         "MMM d",             "M月d日"],

        // ── Notifications ──────────────────────────────────────────────────
        ["Notif_ThresholdTitle"] = ["{0} · {1} 한도 {2}% 도달", "{0} · {2}% of {1} limit used", "{0} · {1} 上限{2}%に到達"], // {0}=tool {1}=label {2}=percent
        ["Notif_ResetTitle"]  = ["{0} · {1} 한도 초기화", "{0} · {1} limit reset", "{0} · {1} 上限リセット"],
        ["Notif_ResetMessage"] = ["현재 {0:0}%로 한도 초기화됨", "Reset — {0:0}% now available", "利用枠が{0:0}%にリセットされました"],

        // ── Authentication state messages ──────────────────────────────────
        ["Auth_CliNotFound"]  = ["{0} CLI를 찾을 수 없습니다. CLI를 설치한 뒤 `{1}`를 실행하세요.",
                                 "{0} CLI not found. Install the CLI, then run `{1}`.",
                                 "{0} CLIが見つかりません。CLIをインストールして `{1}` を実行してください。"], // {0}=command {1}="command args"
        ["Auth_LoginInBrowser"] = ["브라우저 또는 CLI 창에서 로그인을 완료하세요.",
                                 "Complete the sign-in in your browser or the CLI window.",
                                 "ブラウザまたはCLIウィンドウでログインを完了してください。"],
        ["Auth_LoginCancelled"] = ["로그인이 취소되었습니다.", "Sign-in was cancelled.", "ログインがキャンセルされました。"],
        ["Auth_LoginProcessFailed"] = ["CLI 로그인 프로세스를 실행하지 못했습니다.", "Couldn't start the CLI sign-in process.", "CLIログインプロセスを実行できませんでした。"],
        ["Auth_LoginTimeout"] = ["로그인 제한 시간(10분)이 지났습니다. 다시 시도하세요.",
                                 "Sign-in timed out (10 minutes). Please try again.",
                                 "ログインの制限時間（10分）を過ぎました。もう一度お試しください。"],
        ["Auth_LoginBadExit"] = ["CLI 로그인이 완료되지 않았습니다. 종료 코드: {0}",
                                 "CLI sign-in didn't complete. Exit code: {0}",
                                 "CLIログインが完了しませんでした。終了コード: {0}"],
        ["Auth_CredentialNotFound"] = ["CLI가 정상 종료됐지만 로그인 정보를 찾지 못했습니다. CLI에서 로그인을 확인하세요.",
                                 "The CLI exited normally but no sign-in was found. Check the sign-in in the CLI.",
                                 "CLIは正常に終了しましたが、ログイン情報が見つかりませんでした。CLIでログインを確認してください。"],
        ["Auth_Expired"]      = ["로그인이 만료되었거나 거부되었습니다. 다시 로그인하세요.",
                                 "Your sign-in expired or was rejected. Please sign in again.",
                                 "ログイン有効期限が切れたか拒否されました。再度ログインしてください。"],
        ["Auth_SignedInWithPlan"] = ["로그인됨 · {0}", "Signed in · {0}", "ログイン済み · {0}"],
        ["Auth_SignedIn"]     = ["로그인됨",          "Signed in",         "ログイン済み"],
        ["Auth_InvalidCredential"] = ["로그인 정보가 올바르지 않습니다.", "The sign-in information is invalid.", "ログイン情報が正しくありません。"],
        ["Auth_Missing"]      = ["더 정확한 사용량 정보를 보려면 로그인해주세요.",
                                 "Sign in to see more accurate usage.",
                                 "より正確な使用量を表示するにはログインしてください。"],
        // Antigravity has no Gauge-readable credential; describe the source instead of asking
        // the user to sign in (the popover card shows the real usage once it flows).
        ["Auth_Antigravity"]  = ["Antigravity 앱에서 사용량을 읽어옵니다.",
                                 "Reads usage from the Antigravity app.",
                                 "Antigravityアプリから使用量を読み取ります。"],

        // ── Credential source messages ─────────────────────────────────────
        ["Cred_Missing"]      = ["로그인 정보가 없습니다.", "No sign-in information.", "ログイン情報がありません。"],
        ["Cred_ClaudeNoToken"] = ["Claude Code 자격증명에 OAuth 토큰이 없습니다.", "No OAuth token in the Claude Code credentials.", "Claude Codeの資格情報にOAuthトークンがありません。"],
        ["Cred_ClaudeExpired"] = ["Claude Code 로그인이 만료되었습니다. 다시 로그인하세요.", "Your Claude Code sign-in expired. Please sign in again.", "Claude Codeのログイン有効期限が切れました。再度ログインしてください。"],
        ["Cred_CodexNoToken"] = ["Codex 자격증명에 OAuth 토큰이 없습니다.", "No OAuth token in the Codex credentials.", "Codexの資格情報にOAuthトークンがありません。"],
        ["Cred_CodexExpired"] = ["Codex 로그인이 만료되었습니다. 다시 로그인하세요.", "Your Codex sign-in expired. Please sign in again.", "Codexのログイン有効期限が切れました。再度ログインしてください。"],
        ["Cred_ReadFailed"]   = ["자격증명 파일을 읽을 수 없습니다. CLI에서 다시 로그인하세요.", "Couldn't read the credential file. Sign in again from the CLI.", "資格情報ファイルを読み取れません。CLIで再度ログインしてください。"],
        ["Cred_CliInUse"]     = ["공식 CLI 로그인 정보를 사용 중입니다.", "Using the official CLI sign-in.", "公式CLIのログイン情報を使用中です。"],
        ["Cred_CursorReadFailed"] = ["Cursor 로그인 정보를 읽을 수 없습니다. Cursor 앱에서 다시 로그인하세요.", "Couldn't read the Cursor sign-in. Sign in again in the Cursor app.", "Cursorのログイン情報を読み取れません。Cursorアプリで再度ログインしてください。"],
        ["Cred_CursorMissing"] = ["Cursor 로그인 정보가 없습니다.", "No Cursor sign-in found.", "Cursorのログイン情報がありません。"],
        ["Cred_CursorParseFailed"] = ["Cursor 로그인 토큰을 해석할 수 없습니다. Cursor 앱에서 다시 로그인하세요.", "Couldn't parse the Cursor sign-in token. Sign in again in the Cursor app.", "Cursorのログイントークンを解析できません。Cursorアプリで再度ログインしてください。"],
        ["Cred_CursorExpired"] = ["Cursor 로그인이 만료되었습니다. Cursor 앱에서 다시 로그인하세요.", "Your Cursor sign-in expired. Sign in again in the Cursor app.", "Cursorのログイン有効期限が切れました。Cursorアプリで再度ログインしてください。"],
        ["Cred_CursorInUse"]  = ["Cursor 앱 로그인 정보를 사용 중입니다.", "Using the Cursor app sign-in.", "Cursorアプリのログイン情報を使用中です。"],
        ["Cred_CopilotInUse"] = ["GitHub 로그인 정보를 사용 중입니다.", "Using the GitHub sign-in.", "GitHubのログイン情報を使用中です。"],

        // ── Tool guidance (resolved lazily; see ToolCatalog) ───────────────
        ["Guidance_Cursor"]   = ["Cursor 앱에서 로그인하세요.", "Sign in from the Cursor app.", "Cursorアプリでログインしてください。"],
    };
}
