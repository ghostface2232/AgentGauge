# CodexBar 레퍼런스 조사 — AgentGauge가 가져올 것

> 조사일: 2026-09-10 · 대상: [steipete/CodexBar](https://github.com/steipete/CodexBar) `main` (v0.58.1 개발 중, 최신 릴리스 v0.58.0 / 2026-09-09)
> 조사 근거: 저장소 클론 후 `README.md`, `CHANGELOG.md` 전체(2,564행 / v0.25 → v0.58.1), `docs/` 147개 문서 중 핵심 문서(`architecture`, `refresh-loop`, `ui`, `status`, `sessions`, `provider`, `widgets`, `cli`, `configuration`), 최근 커밋 60개, `Sources/` 모듈 구조.
> Swift 구현 세부까지 정독하지는 않았다. 아래의 "구현" 서술은 공식 문서와 체인지로그가 명시한 범위까지만이다.
> 비교 대상 AgentGauge 현황은 `AGENTS.md`(v0.3.4 기준)와 `doc/improvements.md`를 근거로 한다.

---

## 0. 한 줄 요약

CodexBar는 **프로바이더 폭(69개)** 으로 간 프로젝트이고, AgentGauge는 **5개 도구의 깊이**로 가기로 이미 결정했다(로드맵 결정 사항). 따라서 가져올 것은 프로바이더가 아니라 **① 갱신 정책 · ② 상태(장애) 폴링 · ③ 표시 제어 · ④ 히스토리 시각화 · ⑤ CLI/JSON 출력 · ⑥ 아키텍처 규율(디스크립터 + 드리프트 테스트 + 문서 frontmatter)** 이다.

---

## 1. 두 프로젝트 대조

| | AgentGauge | CodexBar |
| --- | --- | --- |
| 플랫폼 | Windows 10 2004+ / WinUI 3, .NET 10, 언패키지드 자체 포함 | macOS 14+ / SwiftUI+AppKit, Swift 6 |
| 표면 | 트레이 아이콘 + 팝오버 1개 | 메뉴바 상태 아이템(프로바이더당 1개 또는 병합) + 설정 창 + 위젯 + CLI + HTTP 서버 |
| 프로바이더 | 5 (Claude Code, Codex, Cursor, Antigravity, Copilot) | 69 |
| 데이터 취득 | 각 CLI가 저장한 토큰을 **읽기 전용**으로 재사용해 공식 usage API 호출 | 동일 철학 + 브라우저 쿠키 / OAuth 디바이스 플로우 / API 키 / 로컬 SQLite / PTY까지 확장 |
| 갱신 | 1분 스케줄러 + 프로바이더별 고정 케이던스(3/5/10/15분) | Manual·1·2·5·15·30분 + **Adaptive** + **Adaptive(agent-aware)** |
| 히스토리 | SQLite 90일 원시 샘플, UI 노출은 ETA 캡션 한 줄 | 메뉴 내 차트, 위젯 차트, 번다운, 일별 원장, 비용 스캔 |
| 배포 | Inno Setup per-user 설치기(무서명) | 서명된 .app + Sparkle 자동 업데이트 + Homebrew/AUR + CLI 타르볼 |
| 로컬라이제이션 | ko/en/ja (코드 내 테이블) | 21개 언어 공유 카탈로그(앱+웹사이트), RTL |

---

## 2. 아키텍처·구성에서 배울 점

### 2.1 모듈 경계 (`docs/architecture.md`)

```
CodexBarCore    fetch + parse (프로바이더 디스크립터, 프로브, 파싱)
CodexBar        상태 + UI (UsageStore, SettingsStore, StatusItemController, 아이콘 렌더)
CodexBarCLI     번들 CLI — Core를 그대로 재사용
CodexBarWidget  WidgetKit — 공유 스냅샷을 읽음
CodexBar*Probe  별도 프로세스 헬퍼 (Claude PTY 워치독 등)
```

핵심은 **"UI가 없어도 값을 얻을 수 있는 층"이 물리적으로 분리**돼 있다는 점이다. CLI·위젯·HTTP 서버가 전부 같은 fetch 파이프라인을 호출하므로 출력 표면이 늘어도 로직이 갈라지지 않는다.

AgentGauge는 이미 `Providers/` + `Services/` + `ViewModels/` + `Views/`로 나뉘어 있으나 **한 어셈블리**다. CLI나 JSON 출력을 언젠가 붙일 생각이라면, 지금 `Gauge.Core`(Models+Providers+Services) / `Gauge`(WinUI) 두 프로젝트로 쪼개 두는 편이 나중보다 싸다. `Gauge.Tests`가 이미 있으므로 참조 그래프 변경 비용은 크지 않다.

### 2.2 디스크립터 기반 프로바이더 등록 (`docs/provider.md`)

CodexBar가 명시한 목표는 "프로바이더 추가 = 폴더 하나 + 디스크립터 하나 + 구현 하나".

- `ProviderDescriptor`가 **단일 진실 원천**: id, 표시 라벨/URL, 브랜딩(아이콘·주색·컨페티 팔레트), capability 플래그(`supportsCredits`, `supportsTokenCost`, `supportsStatusPolling`, `supportsLogin`), 그리고 fetch 전략 목록.
- `ProviderFetchStrategy`가 취득 경로 하나(CLI / 쿠키 / OAuth / 로컬 프로브)를 구현. 한 프로바이더가 여러 전략을 갖고 fallback 순서를 가진다.
- 런타임 설정은 `ProviderSettingsSectionKey` + 타입 지정 섹션 페이로드로 프로바이더가 **자기 설정 스키마를 소유**한다. 컨테이너가 딱 한 곳에서만 type-erased 캐스트를 한다.
- 자격 증명 동작도 `ProviderCredentialAdapter`가 소유 — config→환경 변수 투영, 토큰 해석, 진단 분류, 검증, "자격 증명 없음" 메시지까지.
- 부트스트랩은 두 개의 평평한 매니페스트(`ProviderManifest`, `ProviderImplementationManifest`)로 닫아 두되, 동적 등록용 `register(_:)`는 남겨 둔다.

**AgentGauge 적용점**: 현재 `IUsageProvider` + `UsageProviderBase` + `ToolCatalog`로 이미 유사 구조지만, 브랜딩·표시 문자열·인증 카드 문구·CLI 로그인 명령이 여러 곳에 흩어져 있다. `ToolCatalog` 항목을 "디스크립터"로 승격시켜 **아이콘·색·기본 등록 여부·로그인 명령·상태 페이지 URL·capability 플래그**까지 한 레코드에 모으면, 카드·설정·트레이·알림이 같은 곳을 읽게 된다.

### 2.3 아키텍처 드리프트 트립와이어 — 가져올 가치 높음

`ProviderArchitectureGatekeeperTests` — 셰이핑된 Swift 소스를 렉시컬 스캔해서 **프로바이더 식별자 리터럴이 있어서는 안 될 위치에 나타나면 실패**시키는 테스트다. 문서가 스스로 한계를 솔직하게 명시한다: 표현식 파싱이 필요한 위치, 문자열 결합, 리플렉션, 중첩 인자는 범위 밖이며 "적대적 완전성 주장이 아니라 정직한 실수를 잡는 트립와이어"라고 못박는다.

AgentGauge의 `AGENTS.md`는 대단히 상세한 규범 문서인데, 규범을 **강제하는 테스트**는 일부(COVERAGE seams)에 그친다. 같은 발상으로 값싼 테스트 두어 개를 넣을 수 있다:

- `Views/**.xaml`에 하드코딩 색상 리터럴(`#FF…`)이 없는지 — AGENTS.md의 "No hardcoded colors" 규칙.
- `Localization/Strings.cs` 테이블의 모든 행이 3열을 채웠는지, XAML이 참조하는 `Localize` 키가 전부 존재하는지.
- 기계용 파싱 지점(`double.Parse` / `DateTime.Parse`)에 `CultureInfo.InvariantCulture`가 빠진 호출이 없는지 — AGENTS.md가 "important"로 표시한 침묵 버그 원천이다.

### 2.4 문서 규율

모든 `docs/*.md`가 frontmatter를 갖는다:

```yaml
---
summary: "Refresh cadence, background updates, and error handling."
read_when:
  - Changing refresh cadence, background tasks, or refresh triggers
  - Investigating refresh timing or stale data behavior
---
```

`make docs-list`가 요약을 나열한다. 에이전트가 "지금 이 작업에 무슨 문서를 읽어야 하나"를 스스로 고르게 하는 장치다. AgentGauge는 반대로 **AGENTS.md 하나가 55KB**로 비대하다. 섹션별로 `doc/`에 분할하고 각 파일에 `summary`/`read_when`을 얹은 뒤 AGENTS.md는 인덱스와 불변식만 남기는 리팩터가 유효하다.

### 2.5 설정 파일

- 경로: 신규 설치는 `~/.config/codexbar/config.json`(XDG), 기존 설치는 레거시 경로 유지 — **마이그레이션 없이 양쪽 지원**.
- 제한적 파일 권한으로 저장(비밀 값 포함).
- **CLI로 같은 설정을 조작**: `codexbar config providers` / `config enable --provider grok` / `config set-api-key --provider elevenlabs --stdin`.
- iCloud 동기화(옵트인, 기본 꺼짐)에서 **절대 동기화하지 않는 것**을 설계로 못박음: hooks(로컬 바이너리를 실행하므로), 머신 로컬 경로, 메뉴바 레이아웃/지오메트리, 사용 히스토리, 비용 원장. 레코드가 스키마 버전을 담고, 구 버전 앱은 신 페이로드를 덮어쓰지 않고 동기화를 **일시정지**한다.

AgentGauge의 `AppSettingsFile`은 이미 read-modify-write + `[JsonExtensionData]`로 미지의 키를 보존한다 — 같은 문제의식이다. 여기서 가져올 것은 **"동기화하면 안 되는 것 목록을 문서에 먼저 쓴다"** 는 규율과, 구 버전이 신 스키마를 만나면 쓰기를 멈추는 방어다.

---

## 3. 기능 상세 검토

각 항목은 **CodexBar / AgentGauge 현황 / 판단** 순이다.

### 3.1 적응형 갱신 (Adaptive Refresh) — 최우선

**CodexBar** (`docs/refresh-loop.md`): `AdaptiveRefreshPolicy`는 **순수 함수**다. 입력 = (현재 시각, 마지막 메뉴 열림 시각, 마지막 로컬 코딩 활동 시각, 저전력 모드, 발열 상태) → 출력 = (다음 지연, 안정적인 `Reason` 열거값). 정책 자체는 시계도 `ProcessInfo`도 읽지 않고, 호출부가 틱 직전에 불순 신호를 모아 넘긴다.

| 조건 (첫 매치 승) | 지연 | Reason |
| --- | ---: | --- |
| 저전력 모드 또는 발열 serious/critical | 30분 | `constrained` |
| 메뉴를 5분 이내에 열었음 | 2분 | `recentInteraction` |
| 5분 초과 ~ 1시간 이내 | 5분 | `warm` |
| 로컬 코딩 활동이 5분 이내 (메뉴 규칙보다 빠를 때만) | 5분 | `codingActivity` |
| 1~4시간 전 | 15분 | `idle` |
| 기록 없음 또는 4시간 이상 | 30분 | `longIdle` |

- 모든 결과가 **구조적으로 2~30분 범위**임을 보장. 쿼터·지연·에러·계정·시간대 신호는 **의도적으로 배제**했다(설명 가능성 유지).
- 선택된 지연과 이유를 로컬 로그에 남긴다(`reason=warm delay=300s`). 프로바이더 신원·계정·경로·응답은 절대 로그하지 않는다.
- 별도 모듈 `AdaptiveRefreshCore` + `AdaptiveReplayKit` + `AdaptiveReplayCLI` — **기록된 트레이스를 리플레이해 정책 변경의 효과를 측정**하는 하네스가 따로 있다.
- agent-aware 변형은 로컬 프로세스 목록 스캔이 필요하므로 **명시적 동의(undecided / allowed / declined)** 를 받고, 거절하면 평범한 Adaptive로 되돌린다. 스캔 예산(프로세스 64개, 롤아웃 메타 128건, 150ms 디렉터리 예산)까지 문서에 박아 놓았다.

**AgentGauge**: 1분 스케줄러 + 프로바이더별 고정 케이던스, 팝오버 열기 시 강제 갱신(10초 디바운스), 재개/잠금해제 시 5초 후 1회 갱신. 이미 `TimeProvider` 주입으로 단조 시간 기반이며 단위 테스트 가능하다. v0.4 백로그에 "agent-aware adaptive refresh"가 있다.

**판단**: 정책 테이블을 **순수 함수 + Reason 열거값**으로 만드는 형태가 그대로 이식 가능하다. AgentGauge에 맞는 입력은 (현재 시각, 마지막 팝오버 열림, 배터리 절약 모드, 세션 잠금 상태). 로컬 프로세스 스캔(agent-aware)은 **하지 않는 편**을 권한다 — Windows에서 프로세스 명령줄을 읽는 것은 WMI 경유라 비싸고, 프라이버시 서사가 지금의 "읽기 전용 자격 증명 파일" 한 줄에서 크게 무거워진다. 저전력·유휴만으로도 Antigravity 엔진 기동과 Copilot 호출을 줄이는 실익이 충분하다.

또한 **프로바이더별 케이던스는 유지**해야 한다 — AGENTS.md가 근거를 명시한 서버 측 제약(Claude의 ~5분 캐시)과 비용 제약(Antigravity 프로세스 기동)이 CodexBar에는 없는 조건이다. 적응형은 그 위에 곱해지는 배율로 얹는 게 맞다.

### 3.2 장애 상태(Status) 폴링 — 저비용 고효용

**CodexBar** (`docs/status.md`): OpenAI·Claude·Cursor·Factory·Copilot은 Statuspage.io의 `api/v2/status.json`, Gemini·Antigravity는 Google Workspace 인시던트 피드(`https://www.google.com/appsstatus/dashboard/incidents.json`)에서 해당 제품 ID를 골라 **가장 심각한 활성 인시던트**를 표시한다. 설정 → Advanced의 토글 하나로 켜고 끄며, 메뉴에 인시던트 요약과 신선도를, 아이콘에 인디케이터 오버레이를 얹는다. `statusPageURL`이 있으면 폴링하고, `statusLinkURL`만 있으면 폴링 없이 링크만 연다.

**AgentGauge**: 없음.

**판단**: **가장 비용 대비 효용이 큰 이식 후보.** 사용자가 "갱신 실패" 배지를 볼 때 원인이 내 토큰인지 서비스 장애인지 구분할 방법이 지금 없다. Statuspage JSON은 인증 없는 단순 GET이고 스키마가 안정적이며, AgentGauge의 5개 도구 중 최소 Claude(`status.anthropic.com`)·Cursor·Copilot(`githubstatus.com`)이 Statuspage를 쓴다. 구현은 `ToolCatalog`(→디스크립터)에 `StatusPageUrl`을 추가하고, 15~30분 케이던스의 별도 폴러를 두고, 카드 헤더에 인시던트 칩(이미 `ResetCredits` 칩 패턴이 있다)과 트레이 툴팁 한 줄을 붙이는 정도다. 프라이버시 문서 변경도 "무인증 공개 상태 페이지 GET" 한 줄이면 끝난다.

### 3.3 Pace / ETA 표현

**CodexBar** (`docs/ui.md`의 "Pace tracking"):

- 용어가 확정돼 있다. **On pace** / **X% in deficit**(균등 소비보다 앞서감) / **X% in reserve**(여유). 메뉴바 토큰에서는 `+11%` / `-8%` / `0%`의 부호 형태.
- 적자일 때 우측 라벨이 **"Runs out in …"** 카운트다운으로 바뀌고, 리셋까지 버티면 **"Lasts until reset"**.
- **윈도의 3% 미만이 경과했을 때는 pace를 숨긴다**(주간 메뉴바 토큰만 1% 예외). 초반 노이즈를 구조적으로 차단하는 값싼 규칙이다.
- **Work days 설정**(Automatic / 4 / 5 / 7일): 주간 pace의 기준 모델을 사용자가 고른다. 주말에 안 쓰는 사람에게 균등 소비 모델이 항상 "적자"라고 거짓말하는 문제를 정면으로 다룬 설정이다. Automatic은 충분한 데이터가 쌓이면 히스토리 기반 예측을 쓴다.
- 각 pace 토큰은 **자기 윈도만 읽는다**(주간 pace가 세션 델타를 빌려오지 않음). 단 `Runs out`은 항상 주간(또는 자동) 레인에서 추정한다.
- 데이터가 없으면 다른 윈도로 대체하지 않고 **en dash**를 렌더한다.

**AgentGauge**: `UsagePaceClassifier`(경고 캡션)와 `UsageEtaClassifier`(최소 3개 샘플 / 30분 스팬 / 양의 기울기 / 리셋 이전 도달 시에만 "N시간 후 소진 예상")가 이미 있고, 사이클 리셋 감지 시 이전 샘플을 버린다. 품질은 이미 상당하다.

**판단**: 이식할 것은 알고리즘이 아니라 **① 3% 게이트(초반 침묵 규칙의 명문화), ② "적자/여유"의 부호 있는 단일 수치 표현, ③ Work days 설정, ④ 데이터 없음은 대체 금지·en dash 규칙**이다. 특히 ③은 주간 윈도를 쓰는 Claude/Codex 사용자에게 직접적인 개선이고, AgentGauge의 90일 히스토리가 이미 근거 데이터를 갖고 있다.

### 3.4 표시 항목 가시성 제어 — `improvements.md` #14와 직결

**CodexBar**: 프로바이더 설정의 **Visible usage items**로 어떤 쿼터·사용량·크레딧 행이 메뉴·프리뷰·Overview에 나타날지 선택한다. 명시적으로 못박은 성질이 좋다.

- 기본은 전부 표시.
- 숨긴 행이 일시적으로 보고를 멈춰도 **개별 복구 가능**하게 남는다. `Restore Defaults`로 전부 복구.
- **표시 선택은 fetch·알림·쿼터 계산을 바꾸지 않는다.**
- 가시성 변경이 계정 캐시 identity와 누적 지출을 보존한다(자격 증명·엔드포인트 변경만 이전 소유권을 무효화한다).

**AgentGauge**: 도구를 조용히 시키는 유일한 방법이 등록·알림 이력까지 버리는 "연결 해제"다.

**판단**: `improvements.md` #14(도구별 숨기기·폴링 일시정지)를 설계할 때 **위의 네 가지 불변식을 그대로 채택**할 것. 특히 "가시성은 표시 설정이지 알림 설정이 아니다"와 "숨겨도 캐시 키·알림 베이스라인을 잃지 않는다"는 `UsageNotificationEvaluator`의 베이스라인 설계와 정확히 맞물린다 — 잘못 구현하면 숨겼다 켤 때 오래된 크로싱이 재생된다.

행 단위(윈도 단위) 가시성까지 갈지는 별개 판단이다. Copilot의 3개 청구주기 윈도나 Antigravity의 4개 패밀리 버킷을 다 볼 필요가 없는 사용자가 있으므로, 도구 단위 다음의 자연스러운 확장이다.

### 3.5 히스토리 시각화 — `improvements.md` #13 재확인

**CodexBar**: 메뉴 안 사용률 히스토리 차트(DST 안전한 데이터 포인트 식별, 0.19.0), 일별 비용·토큰 차트의 **호버 시 날짜·비용·토큰 표시**(0.58.0), 스크롤 가능한 뷰포트에 담아 긴 히스토리도 네이티브 메뉴를 밀지 않게 함, Token/Cost 전환 시 스크롤 위치 보존, 위젯의 **Burn Down** 차트(세션/주간, 결합형까지).

**AgentGauge**: 90일 샘플이 DB에 있으나 UI 노출은 ETA 캡션 한 줄. `improvements.md` #13이 이미 "배관은 다 돼 있다 — 같은 호출의 룩백만 늘리면 24시간 스파크라인"이라고 정확히 진단했다.

**판단**: 결론 변경 없음. 다만 CodexBar에서 추가로 가져올 디테일이 둘 있다.

- **번다운(잔여량 하강선) 형태**가 사용률 상승선보다 "리셋까지 버티나"를 직관적으로 보여준다. 이상적 균등 소비선을 함께 그리면 3.3의 적자/여유가 그림으로 설명된다.
- **차트 위 호버 인스펙션**. WinUI에서는 `ToolTipService`나 포인터 이동 시 캡션 갱신으로 저렴하게 구현된다.

### 3.6 트레이/메뉴바 표시 커스터마이즈 — 선별 채택

**CodexBar** (`docs/ui.md`의 "Layout tokens"): 메뉴바 텍스트를 **토큰 레이아웃 에디터**로 조립한다. 토큰 그룹은 Identity(아이콘·이름·계정) · Usage(세션% / 주간% / 스코프 주간% / 자동% / 사용량 바 / 각 pace) · Time(Resets in, Reset at, Runs out) · Money(Balance, Cost today, Cost 30d) · Structure(구분점, 공백, 줄바꿈)다. 프리셋 + 드래그 재배열 + 두 줄 구성 + **프로바이더별 오버라이드** + **조건부 토큰**(if/then/else 규칙으로 사용량·리셋·pace·잔액 비교)까지 간다. 저장 포맷은 V3 키와 **구버전이 읽을 수 있는 V2 투영**을 함께 저장해 다운그레이드 안전성을 확보한다.

성능 디테일 하나가 유용하다. **평범한 단일행 텍스트 레이아웃은 캐시된 템플릿 이미지로 렌더**해 상태 아이템 재드로우 때 AppKit이 재사용하게 하고, 스테일 데이터·고대비 모드·컬러 이모지·다행 텍스트는 속성 문자열 렌더로 빠진다. 로딩 애니메이션에는 **연속 지속 시간 상한**을 둬서 프로바이더가 멈춰도 메뉴바가 영원히 재드로우되지 않게 한다.

**AgentGauge**: 트레이 아이콘은 사용 수준별 색상 변형 교체뿐이다. `improvements.md` #6이 "16px에서 초록↔노랑 구분이 안 되고 색각 이상 사용자에게 무용"이라고 지적하며 퍼센트 텍스트 아이콘을 Phase 2 최우선으로 제안한다.

**판단**: **토큰 에디터는 과설계로 판단 — 도입하지 말 것.** AgentGauge는 트레이 아이콘이 하나뿐이고 프로바이더가 5개다. 대신 가져올 것은 —

- **어떤 도구·어떤 윈도를 트레이에 표시할지**의 단순 선택(자동=최고 사용률 / 특정 도구 고정).
- **퍼센트 텍스트 아이콘** — `improvements.md` #6 그대로.
- **렌더 캐시**: 동일 (텍스트, 테마, DPI) 조합의 아이콘 비트맵 재사용. 매 갱신마다 트레이 아이콘을 다시 그리는 현재 구조에 그대로 적용된다.
- **애니메이션 지속 상한**: 카드 헤더 `ProgressRing`이 프로바이더 행이 걸렸을 때 무한히 도는 것을 막는 안전장치.

### 3.7 컴팩트 계정 행 / Overview

**CodexBar**: 계정이 4개 이상이면 **컴팩트 스택 행**으로 전환해 계정당 "가장 제약이 큰 쿼터 최대 2개 + 각자의 리셋 시각"만 보여준다. 규칙이 정교하다 — 퍼센트와 리셋은 **같은 계정·같은 윈도로 스코프**되며, 다른 쿼터의 더 이른 리셋이 제약 쿼터의 리셋을 대체하지 않는다. 리셋 데이터가 없으면 시간을 **지어내지 않고** 라벨과 퍼센트만 남긴다. 행을 클릭하면 전체 카드로 확장된다. 병합 모드에서는 최대 6개 프로바이더 행의 **Overview 탭**을 제공하고, 행 클릭으로 해당 프로바이더 상세로 점프한다.

**AgentGauge**: 팝오버 높이 상한 + 내부 스크롤. `improvements.md`가 v0.4 백로그에 "컴팩트 뷰 모드"를 이미 담고 있다.

**판단**: 컴팩트 모드를 만들 때의 규칙으로 **"가장 제약이 큰 윈도 1~2개만, 리셋은 그 윈도의 것만, 없으면 지어내지 말 것"** 을 그대로 채택. AgentGauge는 이미 "denominator를 날조하지 않는다"는 같은 계열의 규칙(절대 카운트 캡션)을 갖고 있어 일관된다.

### 3.8 알림

**CodexBar**: 옵트인 쿼터 경고 알림과 **프로바이더 단위 임계값**, 알림 문구에 (개인정보 표시가 켜져 있을 때) 트리거한 계정을 포함한다. 그리고 **리셋 컨페티** — 주간 한도가 활성 사용 후 리셋되면 전체 화면 축하, 세션 리셋도 별도 옵션, 프로바이더 브랜드 팔레트 사용. 체인지로그에 이와 관련된 버그 픽스가 반복 등장한다. "스테일 사용량 반등 후 중복 컨페티", "동일 이메일 워크스페이스 간 리셋 감지 격리", "확인되기 전 리셋 공표 금지" — **리셋 감지가 실전에서 가장 어려운 부분**이라는 증거다.

**AgentGauge**: 임계(70/90)와 리셋 토스트, 종류별 토글 2개(마스터 없음), 베이스라인 확립, 마스크 재장전, 캐시 재방출 무시, 폴링 갭 처리까지 이미 정교하다. 문서에 근거가 명시돼 있고 순수 함수로 단위 테스트된다.

**판단**: **알림 코어는 AgentGauge가 이미 CodexBar와 대등하거나 낫다.** 가져올 것은 셋뿐이다.

- `improvements.md` #9(토스트에 리셋 시각 포함)는 유효하며 CodexBar의 "계정 포함" 발상과 같은 계열이다.
- 프로바이더별 임계값 커스터마이즈는 "도구×종류 매트릭스 금지" 결정과 충돌하지 않는다(임계값은 알림 종류가 아니라 수치다). 다만 우선순위는 낮다.
- 컨페티는 취향 문제다. 도입한다면 CodexBar가 겪은 함정 — **리셋이 확인되기 전에 축하하지 말 것** — 을 먼저 방어할 것. AgentGauge의 리셋 감지는 이미 "리셋 시각 전진 + 사용량 하락"의 이중 조건이라 조건은 갖춰져 있다.

### 3.9 비용/지출 (Usage & Spend) — 신중

**CodexBar**: 로컬 세션 로그(JSONL/SQLite)를 스캔해 토큰과 **추정 비용**을 계산한다. 규모가 크다 — models.dev 라이브 가격 메타데이터, 요청일 기준 **역사적 가격** 적용, 티어 경계 보존, 25,000 세션 / 256MiB 상한의 WAL SQLite 저장소, 증분 스캔과 중단된 스캔 재개, 일별 원장, 시간대 선택, 토큰 믹스, 시간별 활동 히트맵, 커스텀 가격 오버레이.

주목할 것은 **정직성 장치**다. 추정치는 estimate로 **라벨**되고, 부분 합계는 **coverage(적용 범위)** 를 함께 표시하며, 가격을 모르는 것은 실제 청구서인 척하지 않는다. `provenance` 필드가 값의 출처를 명시한다.

**AgentGauge**: ccusage를 의도적으로 제거했다. 근거는 명확하다 — ccusage는 로컬 로그의 토큰만 세므로 **실제 쿼터와 리셋 일정에 접근할 수 없고**, 그 활동 기반 5시간 블록·달력 월요일 주간·역사적 최대 정규화가 `/usage`가 보여주는 값과 어긋나 퍼센트·리셋 불일치를 만들었다.

**판단**: **그 결정은 유효하며 뒤집을 이유가 없다.** 다만 CodexBar가 실제로 하는 일은 "로컬 로그로 쿼터를 추정"이 아니라 "**쿼터는 API에서, 비용은 로컬 로그에서**"라는 완전히 분리된 두 지표다. AgentGauge가 언젠가 비용을 다룬다면 그 분리와 라벨링 규율(estimate / coverage / provenance)이 전제 조건이다. 현재 v0.4 범위에서는 **하지 않는 쪽**을 권한다 — 5개 도구 중 로컬 로그를 파싱할 수 있는 것은 Claude/Codex뿐이고, 유지보수 비용(모델 가격 추적)이 앱의 나머지 전체와 맞먹는다.

### 3.10 CLI · HTTP 서버 · 대시보드 JSON — 전략적

**CodexBar** (`docs/cli.md`): 앱과 같은 fetch 파이프라인을 쓰는 번들 CLI다.

- `codexbar usage --format text|json|toon` — JSON은 프로바이더별 특수 키가 아니라 **제네릭 `usage.details` 배열**이다(섹션마다 `title`, `rows{label,value,secondaryValue}`, 선택적 `bars`/`line`). 레거시 프로바이더 전용 키는 호환 별칭으로 남기지 않고 잘라냈다.
- `codexbar cards` — 터미널에 **반응형 카드 그리드**를 그린다. `$COLUMNS` 존중, `--brief`로 표 모드, truecolor 터미널 자동 감지.
- `codexbar serve` — 포그라운드 HTTP 서버(`/usage`, `/cost`, `/health`, 토큰 게이트된 `/dashboard/v1/snapshot`, 내장 웹 UI). 보안 설계가 모범적이다. 기본 루프백 바인드, 비루프백 바인드는 **베어러 토큰 + `--allow-plain-http` 명시적 승인** 둘 다 요구, 토큰은 쿼리 스트링으로 절대 받지 않음(`ps` 노출 때문에 플래그보다 환경 변수를 권장), `Cache-Control: no-store`, CORS·TLS·데몬 모드 없음.
- 캐시 전략: 응답 만료 후 **last-good을 즉시 반환하고 백그라운드로 재구축**, 일시적 실패는 최대 10주기(최소 5분) last-good 유지 — 폴링 클라이언트가 데이터↔에러로 깜빡이지 않게 한다.
- `codexbar diagnose` — 이슈 리포트용 **레드액션된 진단 내보내기**.

그리고 이것이 만든 생태계가 있다. Waybar / COSMIC / GNOME / Cinnamon / KDE Plasma(3종) / Noctalia / SketchyBar·tmux·Zellij / **Elgato Stream Deck** 통합이 전부 서드파티가 CLI 위에 얹은 것이다. README의 "Related" 섹션 절반이 이 목록이다.

**AgentGauge**: 없음.

**판단**: **v0.5급 전략 항목.** `gauge.exe --json` 하나만 있어도 PowerShell 프롬프트, Windows Terminal 상태줄, Rainmeter, Stream Deck, 타사 위젯이 붙을 수 있다. 2.1의 Core 분리가 선행 조건이며, 그 자체로도 옳은 리팩터다. 서버(`serve`)까지는 과하고, **① 제네릭 스키마의 `--json` 단발 출력**과 **② 레드액션된 `--diagnose` 내보내기**가 실질적 시작점이다. ②는 무서명 앱의 이슈 리포트 품질을 즉시 올린다(AgentGauge는 이미 `DiagnosticsLog`에 스크럽 규칙을 갖고 있어 절반은 돼 있다).

주의할 점 하나. CodexBar의 `usage.details` 제네릭 스키마 전환은 레거시 키를 **깨는** 선택이었다. 처음부터 제네릭으로 시작하는 편이 낫다.

### 3.11 프라이버시 UX

CodexBar가 프라이버시를 **기능으로** 다루는 방식이 배울 만하다.

- README에 "우리가 디스크를 스캔하나요?"를 **자문자답**으로 넣고, 크롤링하지 않고 알려진 소수 위치만 읽는다고 명시한 뒤 감사 노트가 있는 이슈로 링크한다.
- **Hide personal information** 설정 — 켜면 이메일 로컬 파트를 가리고, CLI 환경 변수와 JSON에서 계정 필드를 생략하며, `serve`는 이 설정을 **요청마다 읽어** 재시작 없이 반영한다.
- 위험한 능력에는 **사전 동의 프롬프트**를 둔다. agent-aware 스캔은 프로세스 목록을 읽기 전에 묻고, 거절하면 조용히 평범한 Adaptive로 되돌아간다. 동의 값이 손상되면 **`undecided`로 복구**한다(절대 허용으로 복구하지 않는다).
- **Disable Keychain access** 설정과, 그 설정이 **못 하는 일**까지 문서화한다("CodexBar가 실행하는 프로바이더 소유 CLI는 제약할 수 없다").
- macOS 권한 요청 각각에 대해 "왜 필요한가 / 거절하면 어떤 대안이 있는가"를 README에 표로 제시한다.

**AgentGauge 적용점**: `improvements.md` #3이 PRIVACY.md 부정확성을 이미 P1로 잡았고 반영됐다. 여기서 더 가져올 것은 —

- **"안 하는 일" 목록의 명문화**: 파일시스템 크롤링 없음, 프로세스 스캔 없음, 텔레메트리 없음, 토큰 쓰기 없음. 무서명 앱에 가장 값싼 신뢰 신호다.
- **개인정보 숨기기 토글**: 카드 헤더의 계정·플랜과 향후 JSON 출력에 적용. 화면 공유와 스크린샷을 자주 하는 개발자 사용자층에 정확히 맞다.
- **거절 가능한 능력의 기본값은 항상 "묻지 않음"** 이고, 손상된 동의 값은 거절로 복구한다는 규칙.

### 3.12 접근성

CodexBar 0.25에서 상태 아이콘·메뉴 행·프로바이더 스위처 버튼·사용량 차트에 VoiceOver 라벨을 추가했고, 컴팩트 행의 VoiceOver 문구에 리셋을 포함시킨다.

AgentGauge는 "색상만으로 상태 전달 금지"와 "항상 퍼센트 숫자 표시"를 규칙으로 갖고 있으나, **내레이터(Narrator) 라벨은 문서에 언급이 없다.** `UsageGauge` 커스텀 컨트롤과 진행 바 행에 `AutomationProperties.Name`을 붙이는 작업이 남아 있을 가능성이 크다 — 확인 후 처리할 값싼 항목이다.

### 3.13 업데이트와 배포

- **Sparkle + appcast**: 릴리스마다 `docs: update appcast for X` 커밋이 쌍으로 붙는다. 0.57.0에서 Sparkle 2.9.6의 아카이브 처리·패키지 서명 보호를 채택한 것을 **체인지로그 하이라이트로 공표**했다. AgentGauge의 `UpdateService`는 GitHub 릴리스를 직접 읽고 설치기를 조용히 실행한다 — 무서명이므로 **다운로드한 설치기의 무결성 검증(해시 비교)** 이 있는지 확인할 가치가 있다. Sparkle의 EdDSA 서명에 해당하는 최소 장치다.
- **Homebrew / AUR**: Windows 대응은 winget(`improvements.md` #5)과 scoop이다. 결론 변경 없음, 우선순위 유지.
- **릴리스 노트 형식**: `### Highlights` 3~5줄(굵은 리드 + 이슈 번호) 다음에 `### Added` / `### Fixed`. 하이라이트가 전부 **사용자 언어**로 쓰여 있고("Menus that fit your workflow"), 기여자를 이름으로 감사한다. AgentGauge 릴리스 노트에 그대로 적용 가능한 형식이다.
- **이슈 라벨 가이드**(`docs/ISSUE_LABELING.md`)를 문서로 둔다.

---

## 4. 우선순위 제안

`improvements.md`의 기존 P1~P3와 v0.4 백로그를 전제로, **이번 조사에서 새로 추가되는 항목만** 순위를 매긴다.

| # | 항목 | 근거 절 | 규모 | 우선 |
| --- | --- | --- | --- | --- |
| A | **프로바이더 상태(장애) 폴링 + 카드 칩/트레이 툴팁** | 3.2 | M | P1 |
| B | **적응형 갱신 = 순수 정책 함수 + Reason** (프로세스 스캔 없이, 기존 프로바이더 케이던스 위에 배율로) | 3.1 | M | P1 |
| C | **pace 3% 게이트 + 적자/여유 부호 표현 + 데이터 없음은 en dash** | 3.3 | S | P1 |
| D | **Work days(4/5/7·자동) 주간 pace 모델 선택** | 3.3 | M | P2 |
| E | **도구 숨기기/일시정지 — CodexBar의 4대 불변식 채택** (`improvements.md` #14 설계 보강) | 3.4 | M | P2 |
| F | **번다운 차트 + 차트 호버 인스펙션** (스파크라인 작업에 얹어서) | 3.5 | M | P2 |
| G | **트레이 아이콘 렌더 캐시 + 스피너 지속 상한** | 3.6 | S | P2 |
| H | **개인정보 숨기기 토글 + PRIVACY/README의 "안 하는 일" 목록** | 3.11 | S | P2 |
| I | **`Gauge.Core` 분리 → `--json` 단발 출력 + 레드액션 `--diagnose`** | 2.1 / 3.10 | L | P2 |
| J | **AGENTS.md 분할 + `summary`/`read_when` frontmatter** | 2.4 | M | P3 |
| K | **규범 강제 테스트 2~3개**(하드코딩 색상, 로컬라이즈 키 누락, InvariantCulture 누락) | 2.3 | S | P3 |
| L | **업데이트 다운로드 무결성 검증(해시)** | 3.13 | S | P2 |
| M | **Narrator 자동화 라벨 감사** | 3.12 | S | P2 |
| N | **릴리스 노트 Highlights 형식 채택** | 3.13 | S | P3 |

**다섯 개만 한다면**: A(상태 폴링) · C(pace 표현 정리) · B(적응형 갱신) · H(프라이버시 서사) · L(업데이트 무결성).

### 반영 현황 (2026-09-18, v0.4.3 + 후속 브랜치 기준)

| 상태 | 항목 |
| --- | --- |
| ✅ 반영 | A 상태 폴링 (`ProviderStatusService`, v0.4.0) · B 적응형 갱신 (`AdaptiveRefreshPolicy`, 저전력·잠금 인식, 프로세스 스캔 없음) · C pace 3% 게이트 + 부호 표현 + en dash · D Work days (`WeeklyPaceModel` — 매일 균등 / 근무일 월–금 / 자동, 설정 드롭다운; 자동은 `UsageWeekdayProfile`로 14일 이상 히스토리가 쌓이면 요일별 소비 형태를 쓰고 그 전엔 균등) · E 도구 숨기기 (4대 불변식 채택) · F 번다운 스파크라인 + 호버 · M Narrator 이름 (`UsageAccessibilityTests`) · K 규범 테스트 3종 (XAML 색상 `PopoverXamlContractTests`, 로컬라이즈 키 `LocalizationKeyUsageTests`, InvariantCulture `CultureContractTests`) · L 업데이트 무결성 (GitHub 자산 `digest` 대조, `UpdateService`) |
| 🟡 부분 | G — 아이콘 소스 캐시와 새로고침 링 30초 상한은 반영. 텍스트 아이콘 렌더 캐시는 `improvements.md` #6과 함께 |
| ❌ 미반영 | H 개인정보 숨기기 토글 + "안 하는 일" 목록 · I `Gauge.Core` 분리 + `--json`/`--diagnose` · J AGENTS.md 분할 · N 릴리스 노트 Highlights 형식 |

"다섯 개만 한다면" 중 남은 것은 **H** 하나다.

**번복 (2026-09-18) — 3.9 비용/지출**: "하지 않는 쪽"으로 판단했던 로컬 비용 스캔을 **좁은 범위로** 도입했다. CodexBar 소스(`Sources/CodexBarCore/Vendored/CostUsage/`)를 직접 확인한 뒤, 판단 근거였던 두 부담을 이렇게 줄였다. 유지보수 부담은 models.dev 라이브 조회·역사적 가격·커스텀 오버레이 없이 **번들 가격표 하나**로(오래되면 "가격 미확인"으로 드러날 뿐 틀린 숫자가 되지 않음), 규모 부담은 Claude·Codex 두 도구와 **이번 달 합계 하나**로(일별 원장 차트·히트맵·30일 창 없음). 3.9절이 전제 조건으로 꼽은 분리와 라벨링 규율은 그대로 지켰다 — 쿼터는 API, 비용은 로컬 로그. 추정은 "≈"로, 가격 미확인 모델이 있으면 "+" 하한으로 표시하고 툴팁에 명시한다. CodexBar와 달리 첫 실행 자동 활성화는 하지 않는다(기본 꺼짐, 켜는 것이 곧 로그 읽기 동의). 설계 상세는 AGENTS.md "API-equivalent cost".

---

## 5. 가져오지 말 것

| 항목 | 이유 |
| --- | --- |
| 프로바이더 수 확장 | 로드맵에서 이미 "깊이 > 폭"으로 결정했다. CodexBar의 69개는 브라우저 쿠키 수확·API 키 보관·수동 cURL 캡처를 전제하며, AgentGauge의 "CLI가 관리하는 파일만 읽기 전용" 서사를 깬다. |
| 브라우저 쿠키 임포트 | 위와 동일. Windows에서는 DPAPI 복호화가 필요해 "비밀번호를 저장하지 않는다"는 약속이 설명하기 어려워진다. |
| agent-aware 로컬 프로세스 스캔 | 실익 대비 프라이버시 서사 비용이 크다. CodexBar조차 명시적 동의 게이트, 스캔 예산, 세션 식별자 폐기까지 붙여야 했다. |
| 메뉴바 토큰 레이아웃 에디터 / 조건부 토큰 | 트레이 아이콘 하나에 도구 5개인 앱에는 과설계다. V2/V3 이중 저장 같은 하위호환 부채가 따라온다. |
| ~~로컬 비용 스캔(Usage & Spend)~~ (2026-09-18 좁은 범위로 번복, §4 말미 참조) | 3.9 참조. 모델 가격 추적 유지보수가 앱 나머지 전체와 맞먹는다. |
| 클라우드 설정 동기화 | 계정·서버 의존을 도입한다. 필요하면 설정 파일 내보내기/가져오기로 충분하다. |
| WidgetKit에 대응하는 위젯 | 이미 MSIX 충돌로 거절된 결정이다. 대신 3.10의 `--json`이 서드파티 위젯 경로를 연다. |

---

## 6. 부록 — 참조한 CodexBar 파일

| 파일 | 무엇이 있나 |
| --- | --- |
| `README.md` | 기능 목록, 69개 프로바이더 표, 프라이버시·권한 서사, 생태계 링크 |
| `CHANGELOG.md` | v0.25 → v0.58.1. `### Highlights` 블록만 훑으면 제품 방향이 보인다 |
| `docs/architecture.md` | 모듈 경계, 데이터 흐름, CLI 로그인 라이프사이클 |
| `docs/refresh-loop.md` | 적응형 갱신 정책 테이블, 동의 모델, 스캔 예산 |
| `docs/ui.md` | 레이아웃 토큰, 아이콘 렌더 캐시, 컴팩트 계정 행, Pace tracking |
| `docs/status.md` | 상태 폴링 소스와 인디케이터 매핑 |
| `docs/provider.md` | 디스크립터 아키텍처, 게이트키퍼 테스트 위협 모델 |
| `docs/cli.md` | CLI 명령, JSON 스키마, `serve` 보안 모델 |
| `docs/configuration.md` | 설정 파일 경로·스키마, 동기화 제외 목록 |
| `docs/widgets.md` | 위젯 스냅샷 파이프라인, 갱신 타이밍 |
| `Sources/AdaptiveReplayKit/` | 갱신 정책 리플레이 하네스 — 정책을 데이터로 검증하는 접근 |

재현 방법: `git clone --depth 400 https://github.com/steipete/CodexBar.git`. Windows에서는 `Tests/CodexBarTests/Fixtures/PiFamily/` 경로 길이 초과로 체크아웃이 부분 실패하므로, `git restore --source=HEAD -- docs`로 필요한 트리만 복구하면 된다.
