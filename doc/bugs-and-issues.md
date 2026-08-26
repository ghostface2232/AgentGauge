# AgentGauge 버그 및 이슈 목록

> 기준 버전: v0.3.1 · 작성일: 2026-08-13
> 데이터/서비스 계층과 UI/ViewModel 계층을 나눠 전수 감사한 결과. 각 건은 실제 코드를 읽어 확인한 것만 수록. 심각도 순 정렬.
> 참고: 명시적으로 점검했으나 문제없음이 확인된 영역 — 문화권 파싱(InvariantCulture 일관), Claude 429/쿨다운 상태기계, 캐시 재수화 LabelKey 왕복, 알림 평가기 baseline/reset/mask, live-fetch 게이트, SQLite 손상 복구, Job Object 티어다운, 토글 가드↔디바운스 상호작용, WM_NCCALCSIZE 서브클래스 수명, 설정 reflect-back 루프 방지, Strings.cs 테이블(92키 3열 완비·플레이스홀더 일치).

---

> **수정 현황 (2026-08-13)**: H1 → `9dbd1af`+`7570a93`, M1 → `f137554`+`38b11d8`, M2 → `362f4cb` 에서 수정 완료.
> **수정 현황 (2026-08-26)**: M8 → `24daa0c`+`f6fde7d` 에서 수정 완료 (후자는 부수로 발견된 `AntigravityEngineHost.Dispose`의 무한 게이트 대기 종료 행도 함께 수정 — UI 컨텍스트에서 시작된 갱신은 Dispose가 UI 스레드를 점유하는 동안 게이트를 풀 수 없음).

## HIGH

### H1. 혼합 DPI 다중 모니터에서 첫 열기 배치가 잘못된 스케일 사용 — ✅ 수정됨
`Views/PopoverWindow.xaml.cs:403-411` (`CaptureTargetMonitor`), `Show()`(:194-196), `PositionAndResize`(:413-435)
`_workArea`는 커서 아래 모니터(`DisplayArea.GetFromPoint`)에서 얻지만 `_scale`은 **숨겨진 창이 현재 놓여 있는 모니터**의 DPI(`GetDpiForWindow`)에서 얻는다. 100% 모니터에 마지막으로 표시됐던 창을 150% 모니터의 트레이에서 열면, 첫 열기에서 크기·마진이 잘못 계산되어 팝오버가 축소/확대·오정렬된다. `MoveAndResize`로 창이 새 모니터에 도착한 뒤 발생하는 DPI 변경 리레이아웃도 낡은 `_scale`을 재사용하므로 **다음 열기까지** 잘못된 상태가 유지된다. `BodyScroll.MaxHeight`와 트레이 아이콘 픽셀 크기도 같은 스케일을 상속. 스펙("100/125/150% DPI 대응") 직접 위반.
**수정 방향**: 대상 모니터 자체에서 스케일 도출(`GetDpiForMonitor`), 그리고/또는 `WM_DPICHANGED`에서 `_scale` 재캡처 후 `PositionAndResize` 재실행.

---

## MEDIUM

### M1. `ToolRegistry._enabled` 크로스 스레드 데이터 레이스 — ✅ 수정됨
`Services/ToolRegistry.cs:24` · 소비: `App.xaml.cs:134` → `Services/UsageService.cs:46`
코디네이터 갱신 루프(스레드풀)가 매 사이클 `IsEnabled` → `List<T>.Contains`를 호출하는 동안, UI 스레드는 같은 `List<ToolKind>`를 `Add`/`Remove`/`ReorderEnabled`로 변경한다. `List<T>`는 동시 읽기-중-쓰기 안전을 보장하지 않음 — 1분 틱과 도구 제거가 겹치면 찢어진 상태를 읽을 수 있고, `MergeIntoCache`가 enabled 목록에 없는 도구의 캐시를 삭제하므로 **여전히 켜져 있는 도구의 카드가 일시 삭제되고 그 삭제가 usage-cache.json에 영속화**될 수 있다.
**수정 방향**: `_enabled`를 락으로 보호하거나, 불변 `IReadOnlyList`를 원자적으로 교체(`Volatile.Write`).

### M2. Cursor 스키마 드리프트 시 0% "성공"이 마지막 정상 스냅샷을 덮어씀 — ✅ 수정됨
`Providers/CursorProvider.cs:150` (`return 0;`)
`usage-summary` 응답 형태가 바뀌어(필드 개명, 인식 필드 없는 플랜) 퍼센트 우선순위 체인이 전부 실패하면 `0`으로 폴백해 **0% 사용의 성공 스냅샷**을 반환한다. 코디네이터는 이를 라이브 성공으로 취급 — 마지막 정상값 교체·영속화, `usage-history.db`에 가짜 0 하락 기록(ETA 분류기가 사이클 리셋으로 오인), 평가기의 reset-fallback 오탐 가능. 다른 프로바이더(Copilot/Antigravity)는 사용 불가한 버킷을 **건너뛰지** 0으로 가정하지 않으며, "실패가 last-good을 잘못된 데이터로 대체해선 안 된다"는 스펙과 모순.
**수정 방향**: 인식 필드가 하나도 없으면 `ParsePlanPercentUsed`가 `null` 반환 → 윈도 생략 또는 throw로 last-good 유지.

### M3. Copilot 월간 초기화 시각을 로컬 시간으로 파싱할 가능성
`Providers/GitHubCopilotProvider.cs:120` + `Providers/Internal/JsonElementExtensions.cs:60-63`
`quota_reset_date_utc`가 날짜만 있는 문자열(예: `"2026-09-01"`)로 오면 `DateTimeOffset.TryParse(…, RoundtripKind)`가 **로컬 오프셋**을 적용 — UTC+9에서는 실제 UTC 초기화보다 9시간 이른 시각으로 표시되고, 평가기의 reset-advance 비교도 어긋난다. 날짜 전용 파서 `GetDateOnlyOrNull`(:64)이 정의만 되고 **저장소 어디서도 미사용**인 점이 이 지점의 원래 의도였음을 시사.
**주의**: 현재 테스트 픽스처는 full timestamp(`"2026-07-01T00:00:00Z"`)를 사용 — AGENTS.md의 "라이브 응답을 먼저 확인" 원칙대로 실제 응답이 날짜 전용인지 확인 후 수정할 것.
**수정 방향**: 실측 확인 후 `AssumeUniversal` 적용 또는 `GetDateOnlyOrNull` → UTC 자정 사용.

### M4. 드래그 재정렬 중 컬렉션 변경 시 크래시/순서 손상
`Views/PopoverWindow.xaml.cs` `ReorderSurface`: `BeginDrag`(:1058-1085)에서 `_home`/`_centers`를 1회 스냅샷하지만 `ShiftOthers`(:1150-1179)·`OnPointerMoved`(:997-1013)·`ComputeTarget`(:1114-1141)은 **라이브** `_count()`로 인덱싱
팝오버 열기가 강제 갱신을 트리거하므로, 드래그 중 결과가 도착해 `UsageViewModel.Apply`가 카드를 추가/제거하면 `panel.Children.Count > _home.Length` → 포인터 이동 핸들러에서 `IndexOutOfRangeException`(미처리 → 앱 크래시), 또는 `Commit`이 낡은 인덱스로 `Cards.Move` 호출 → `ArgumentOutOfRangeException`/순서 손상. 설정 그리드의 `RebuildCards`도 동일 노출.
**수정 방향**: 드래그 중 `CollectionChanged` 구독으로 활성 드래그 취소/정착, `ShiftOthers`/`Commit`에서 `_home.Length`와 라이브 카운트 양쪽으로 인덱스 검증.

### M5. 접근성: 아이콘 전용 버튼·스위치·드롭다운의 UIA 이름 부재
`Views/PopoverWindow.xaml` — 새로고침(:491-498), 설정 톱니(:499-506), 뒤로(:533-536), 연결 해제 X(:113-117), 서비스 추가(:713-719), 업데이트(:725-731) 버튼이 `FontIcon`만 포함해 UIA Name이 비어 있음(WinUI는 툴팁을 자동 승격하지 않음). `ToggleSwitch` 3개(의도적으로 On/Off 내용 비움)와 `ComboBox` 2개도 이름 없음 — 스크린 리더가 용도 없이 "토글 스위치/콤보 상자"만 낭독. 코드베이스가 패턴을 이미 알고 있으나(`NotificationOptionsButton`:577) 나머지에 미적용.
**수정 방향**: 각 아이콘 버튼에 `AutomationProperties.Name="{loc:Localize …}"`, 스위치/콤보에 `Name` 또는 행 레이블 `LabeledBy`.

### M6. 감소된 모션 설정이 알림 디스클로저 외 모든 애니메이션에서 무시됨
`AnimateNotificationOptions`만 `_uiSettings.AnimationsEnabled` 확인(`PopoverWindow.xaml.cs:527`). 표시 슬라이드인(:437-471), 사용량↔설정 전환(:638-688, `EnableDependentAnimation` X 이동 포함), 스크롤바 페이드(:848-880), 재정렬 시프트(:1211-1247)는 무조건 실행. 명시적 `Storyboard`는 OS 설정으로 자동 억제되지 않음.
**수정 방향**: 모든 스토리보드를 `UISettings.AnimationsEnabled`로 게이트하고 끝 상태로 점프(디스클로저가 패턴 예시).

### M7. `AddServiceButton` 위치가 스펙과 불일치
`Views/PopoverWindow.xaml:713-719` — AGENTS.md §Settings panel은 "+" 버튼이 **스크롤 본문 안**, 인증 카드 다음이라 명시하지만 실제로는 Row 2 푸터 바(더구나 `DataContext=Update`인 영역) 안에 있음. 코드 비하인드 핸들러와 파스타임 툴팁 덕에 동작만 할 뿐.
**수정 방향**: 버튼을 `AuthRepeater` 뒤 스크롤 안으로 이동하거나 AGENTS.md를 실태에 맞게 수정 — 둘 중 하나로 정합화.

### M8. `UsageCoordinator.Dispose`가 진행 중 갱신과 레이스 — ✅ 수정됨
`Services/UsageCoordinator.cs:483-502` + `RefreshCoreAsync` finally(:281)
`Dispose`가 `_cts` 취소 직후 `_cts`·`_refreshGate`를 `_loopTask`나 진행 중 갱신을 기다리지 않고 폐기. (a) 게이트를 쥔 갱신이 폐기된 세마포어에 `Release()` → `ObjectDisposedException`이 `OperationCanceledException` 전용 처리를 탈출해 `async void` 핸들러(`OnPopoverOpened`/`RefreshAfterWake`)로 유출, (b) 종료와 경합한 `RefreshAsync`가 폐기된 `_cts.Token` 참조. 종료/언어 재시작 시에만 발생하나 "깨끗한 티어다운" 의도를 무산시킴.
**수정 방향**: `_disposed` 선설정 후 `RefreshAsync` 조기 반환, 루프 태스크를 짧게 대기 후 폐기 — 또는 세마포어는 폐기하지 않고 `Release`를 플래그로 가드.

### M9. 카드 내 행 순서가 낡을 수 있음 — 그룹 헤더/구분선이 그룹 중간에 걸림
`ViewModels/ToolCardViewModel.cs:102-134` (`Update`)
기존 행은 제자리 갱신, 신규 행은 표시 인덱스에 삽입하지만 **위치가 바뀐 기존 행의 Move 단계가 없음** (`UsageViewModel.RefreshCards`:229-247와 `SettingsViewModel.RebuildCards`는 재정렬함). 프로바이더가 그룹/버킷을 다른 순서로 내보내면(Antigravity 동적 버킷, 기존 행 사이에 나타나는 Codex `additional_rate_limits`) `Windows` 컬렉션은 옛 순서를 유지한 채 `AssignGroupHeaders`(:178-203)는 새 순서 기준 위치로 헤더/구분선을 할당 — 구분선이 목록 중간 행 위에 렌더링되고 5시간/주간 쌍이 분리 표시될 수 있음.
**수정 방향**: add/update 루프 후 `RefreshCards`와 같은 Move 패턴으로 `Windows`를 목표 순서에 맞춰 제자리 재정렬.

---

## LOW

### L1. 알림 토글 적용 경로가 읽기 실패 시 fail-open
`App.xaml.cs:303-305` + `NotificationSettingsStore.cs:31-35` — `store.Load()`가 읽기 실패 시 **전체 켜짐 기본값** 반환. 양쪽 다 끈 사용자가 일시적 읽기 실패 중 한 종류를 토글하면 `desired = 기본값.With(변경)`이 **다른 종류까지 재활성화**하고 `TrySave` 성공으로 영속화 — AGENTS.md가 패널 열기 경로에 금지한 바로 그 "un-mute" 시나리오. **수정**: `TryLoad` 사용, 실패 시 현재 표시 중인 환경설정을 기준으로.

### L2. 일시적 자격 증명 파일 읽기 오류가 `Invalid`로 보고 → 허위 "로그아웃" + 불필요한 CLI 스폰
`Services/CliCredentialSource.cs` `ReadJson` — CLI가 토큰을 회전 기록하는 순간의 공유 위반/반쯤 쓰인 JSON(읽기 전용 정책이 감내하려던 바로 그 순간)이 진짜 손상과 구분 없이 `Invalid`. 프로바이더는 무의미한 위임 갱신 CLI를 스폰하고, 재읽기도 충돌하면 `AuthenticationRequiredException` → 인증 카드가 다음 라이브 페치까지 "로그아웃"으로 표시. **수정**: `IOException`(일시)과 파싱 오류(손상) 구분, 1회 재시도 또는 별도 상태.

### L3. Antigravity 루프백 클라이언트의 자체 타임아웃이 재시도 로직을 탈출
`Providers/Internal/AntigravityLoopbackClient.cs:79-93` + `AntigravityEngineHost.cs:84-101` — `_http.Timeout`(5초) 만료는 `TaskCanceledException`인데 catch 필터는 `HttpRequestException/IOException`만. 위임 모드에서 포트는 열었지만 5초 넘게 걸리는 콜드 스타트 엔진(15초 `ReadyTimeout` 폴 루프가 존재하는 바로 그 사유)이 첫 느린 프로브에서 시도 전체를 중단; 어태치 모드에선 느린 서버 하나가 다음 포트 순회를 막음. 증상은 재시도 설계가 커버해야 할 상황에서 Antigravity 카드가 낡은 채 유지. **수정**: 호출자 토큰이 취소되지 않은 `TaskCanceledException`을 "이 시도 실패, 계속"으로 처리.

### L4. 히스토리 스토어 첫 `GetRecent`가 UI 스레드에서 SQLite 읽기
`Services/UsageHistoryStore.cs:123-139` — 세션 첫 라이브 `Record` 전에 팝오버가 열리면(콜드 스타트의 정상 순서) `HydrateTailIfNeeded`의 SELECT가 UI 스레드에서 실행되고, `Record`가 WAL 쓰기 트랜잭션 동안 `_gate`를 쥐므로 UI 스레드가 그 뒤에서 블록 가능. "UI 스레드 읽기는 DB에 닿지 않는다"는 스펙 문구 위반. **수정**: 시작 시 백그라운드 선행 하이드레이션.

### L5. 위임 엔진 준비 루프가 벽시계 시간 사용
`Providers/Internal/AntigravityEngineHost.cs:88` — `DateTime.UtcNow` 데드라인은 프로젝트의 단조 시간 규칙 위반. NTP 스텝/수동 변경 시 15초 창이 임의로 늘어나 죽은 엔진을 수 분간 폴링하며 호스트 게이트를 점유(종료 시 `Dispose`가 대기). **수정**: `Stopwatch`/`TimeProvider.GetTimestamp`. (부수: `AntigravityDelegateReader` 요약 주석의 "warm reuse"가 실제 per-read 티어다운과 모순 — 주석 수정.)

### L6. Copilot의 "비용 제로" 플로어가 팝오버 열 때마다 `gh auth token` 프로세스 스폰
`Services/UsageCoordinator.cs:323-327` + `Services/GitHubCliTokenReader.cs` — 플로어 0의 근거는 "읽기가 HTTP GET 1회인 프로바이더"인데 Copilot 읽기는 gh 프로세스 실행(키링/DPAPI 접근 가능) + GET. Antigravity 플로어가 존재하는 것과 같은 비용 계급(더 작을 뿐). **수정**: gh 토큰을 몇 분간 메모리 캐시하거나 소규모 플로어 부여.

### L7. "0분 후 초기화" 표시 및 시간 표기 비일관
`Localization/ResetTimeFormatter.cs:37-40` — `ForRow`는 `ForNotification`(:68)의 `Math.Max(1, minutes)` 가드가 없어 1-59초 남았을 때 "0분 후 초기화" 렌더링. 또 `hours>0 && minutes==0`이면 `ForNotification`은 `Reset_InHours`로 전환하는데 `ForRow`는 "2h 0m" 표기. **수정**: `ForNotification`의 분기 미러링.

### L8. `AuthenticationCardViewModel`의 `StateChanged` 구독 누수
`ViewModels/AuthenticationCardViewModel.cs:30` — 앱 수명 싱글턴인 프로바이더에 람다 구독, 해제 경로 없음. 카드가 `RebuildCards`로 재생성될 때마다 제거된 카드가 프로바이더에 뿌리내린 채 상태 변경을 계속 처리, add/remove 반복 시 죽은 VM 누적. **수정**: 핸들러 보관 + `Detach()`/`IDisposable`, `RebuildCards` 제거 분기에서 호출.

### L9. 팝오버 열기 디바운스 무장 조건과 문서 불일치
`Services/UsageCoordinator.cs:225-251` — `_lastRefreshStartedTimestamp`는 `providersToRefresh.Count > 0`일 때만 설정. 활성 도구가 플로어 안의 Antigravity뿐이면 열기 사이클이 타임스탬프를 안 잡아, 직후 새로고침 클릭이 **실제로 페치**됨 — AGENTS.md가 기술한 모델("열기 후 10초 내 클릭은 플로어 걸린 프로바이더를 못 가져올 수 있다")과 반대. 실동작이 사용자에게 유리하므로 코드 또는 문서 중 한쪽으로 정합화.

---

## 참고 (결함 아님 / 관찰)

- **재시작 후 리셋 토스트 재생**: 재수화된 낡은 baseline(어젯밤 95%) + 앱 꺼진 사이 리셋 → 첫 라이브 읽기에서 "초기화" 토스트 발화. threshold 재생은 baseline 규칙이, 낡은 crossing은 gap-consumption 규칙이 막지만 리셋 토스트 자체는 오프타임 갭 후 재생됨. 의도된 UX일 수 있어 관찰로만 기록.
- `UsageCacheStore.Load`가 디스크의 `UsedRatio`를 클램프하지 않음(변조/손상 시 >1이 UI/평가기로 유입) — 방어적 하드닝 수준.
- COVERAGE.md는 정확하며, 위 최대 미테스트 위험 영역(M1, M8)은 실제로 커버리지 목록 밖.
