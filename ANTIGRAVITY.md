# Antigravity 사용량 지원 가이드 (Windows)

## 1. 목적과 범위

이 문서는 Gauge에 Antigravity 사용량 카드를 추가하기 위한 구현 가이드다. 내용은 실제로 PC에 설치된 Antigravity를 직접 조사하고, **실행 중인 language server의 live 응답을 캡처해** 확인한 사실을 기준으로 한다.

- 조사 빌드: Antigravity IDE 버전 `2.1.4`(`--override_ide_version`), base VS Code 1.107.0, 내부 코드명 "jetski".
- 조사 방법: 설치 트리·번들 바이너리·런타임 로그 정적 분석 + 실행 중인 language server에 직접 Connect 호출(`RetrieveUserQuotaSummary`, `GetUserStatus` 등)을 보내 응답을 캡처. 5절의 schema는 실제 캡처한 응답이다.

Gauge에서 "Antigravity 지원 완성"의 범위는 다음과 같다.

1. 실행 중인 Antigravity IDE의 local language server에서 quota를 읽어 카드에 표시한다(attach 모드).
2. IDE가 꺼져 있으면 Gauge가 같은 `language_server.exe`를 직접 headless로 띄워(delegate 모드, 7.4절) 최신 quota를 받는다. 이마저 불가능하면(미설치·미로그인) 마지막 성공 snapshot과 갱신 시각을 유지한다.
3. Antigravity는 기본 등록 도구가 아니라 사용자가 settings에서 명시적으로 추가하는 opt-in 도구다.

인증(Google OAuth)과 backend 통신은 Antigravity 앱이 소유한다. Gauge는 그 credential을 읽거나 쓰지 않고, OAuth token endpoint를 직접 호출하지 않는다(9절).

## 2. Windows에서 Antigravity의 구조

Antigravity는 Windows에서 **단일 Electron IDE**다. VS Code/Codeium(Windsurf 계열) 포크이며, macOS 레퍼런스에 나오는 "데스크톱 앱 vs IDE" 같은 두 개의 별도 앱 구분은 Windows에 존재하지 않는다.

```
%LOCALAPPDATA%\Programs\Antigravity\
  Antigravity.exe                                  ← Electron IDE (VS Code 포크)
  bin\antigravity, bin\antigravity.cmd             ← VS Code `code` 런처 (파일 열기/확장 설치용)
  resources\bin\language_server.exe                ← 실행 중 quota를 노출하는 Go language server ★
  resources\app\extensions\antigravity\
    bin\language_server_windows_x64.exe            ← 확장 번들 LS 바이너리(별도, 메서드 이름이 다름)
    dist\extension.js                              ← LS를 spawn하고 Connect로 통신하는 확장

%APPDATA%\Antigravity\                             ← IDE user data (VS Code 레이아웃)
  logs\<timestamp>\ls-main.log, auth.log, cloudcode.log
  User\globalStorage\state.vscdb, storage.json

%USERPROFILE%\.antigravity\                        ← 확장/argv (CLI 런처용)
```

핵심:

- 실행 중 quota를 제공하는 프로세스는 IDE가 자식으로 띄우는 **`resources\bin\language_server.exe`** 다(실측: 프로세스 이름 `language_server.exe`, `--standalone --subclient_type hub`로 기동). 설치 트리에는 확장이 번들한 `language_server_windows_x64.exe`도 있으나, 현재 버전에서 실제 quota를 서빙한 프로세스는 `language_server.exe`였다. **두 바이너리는 노출하는 메서드 이름이 다르므로**(4.3절) 프로세스 매칭과 메서드 호출을 이름에 하드코딩하지 말 것.
- IDE가 자동으로 띄운 이 프로세스는 IDE가 실행 중일 때만 산다. 그러나 **`language_server.exe`는 standalone 실행이 가능한 자체 완결 바이너리**라, IDE가 꺼져 있어도 Gauge가 직접 띄워 동일한 quota를 받을 수 있다(실측 확인, 7.4절). 별도 헬퍼 없이 평범한 hidden 프로세스로 뜬다 — macOS 레퍼런스의 `agy`/pseudo-terminal 같은 건 필요 없다.
- `bin\antigravity(.cmd)`는 quota와 무관한 VS Code 런처(`ELECTRON_RUN_AS_NODE=1 Antigravity.exe …\cli.js`)다. quota를 주는 것은 `language_server.exe`뿐이다.

따라서 Windows에는 같은 엔진을 쓰는 두 가지 접근이 있다: 실행 중이면 **그 프로세스에 attach**하고, 꺼져 있으면 **Gauge가 엔진을 직접 띄운다(delegate)**. 두 경로 모두 동일한 Connect 호출과 파서를 공유한다. 둘 다 불가능할 때만 last-good + freshness로 떨어진다.

## 3. 기존 provider와 무엇이 다른가

Gauge의 기존 세 provider는 비교적 단순하다.

| 서비스 | 인증 정보 | 사용량 source | window |
| --- | --- | --- | --- |
| Claude Code | CLI 소유 credentials 파일 | Anthropic OAuth usage HTTPS endpoint | 5시간 + 주간 |
| Codex | CLI 소유 `auth.json` | ChatGPT usage HTTPS endpoint | 5시간 + 주간 |
| Cursor | Cursor read-only local state DB | Cursor usage-summary HTTPS endpoint | billing cycle 1개 |

세 provider 모두 token/세션을 읽으면 평범한 outbound HTTPS 한 번으로 사용량을 얻는다. Antigravity는 그렇지 않다.

1. **공개 usage endpoint가 없다.** 데이터는 실행 중인 language server가 localhost에 노출하는 내부 Connect API에서만 나온다.
2. **포트가 고정이 아니다.** language server는 매 실행마다 random 포트를 연다. 올바른 프로세스를 찾고, 그 PID가 LISTEN 중인 포트를 조회해야 한다.
3. **요청에 CSRF token이 필요하다.** token은 language server 프로세스의 command line에 들어 있다.
4. **local 서버가 self-signed HTTPS다.** 전역 TLS 검증을 끄지 않고 loopback 한정으로만 인증서를 허용해야 한다.
5. **한 도구에 quota window가 최대 4개다.** Gemini family와 Claude/GPT family가 각각 5시간·주간 limit를 가질 수 있다(5절). 이는 Gauge 공통 모델 변경을 요구한다(6절).
6. **사용률 없이 reset 정보만 있는 row가 있을 수 있다.** 이를 100% 소진으로 해석하면 안 된다.

## 4. 데이터 source: language server Connect API

### 4.1 프로세스와 포트

실행 중인 `language_server.exe`의 command line(실측, token 마스킹):

```
language_server.exe --standalone --override_ide_name antigravity --subclient_type hub
  --override_ide_version 2.1.4 --override_user_agent_name antigravity
  --https_server_port 0 --csrf_token <uuid> --app_data_dir antigravity
  --api_server_url https://generativelanguage.googleapis.com
  --cloud_code_endpoint https://daily-cloudcode-pa.googleapis.com --enable_sidecars
```

확인된 LISTEN 포트(예시 값, 매 실행 변동): `127.0.0.1:2654`(HTTPS, self-signed)와 `127.0.0.1:2655`(평문 HTTP). 둘 다 Connect API를 서빙했고, quota는 **HTTPS 포트**로 호출한다.

정리:

- `--https_server_port 0`은 "random 포트 할당"을 뜻한다. 고정 포트를 가정하지 말고 그 PID의 LISTEN 테이블에서 찾아야 한다(7.2). IPv4 `127.0.0.1`에 두 포트(HTTPS/HTTP)가 열린다.
- Gauge가 읽어야 하는 것은 `Antigravity.exe`가 아니라 **`language_server.exe` 프로세스의 command line**이다. 거기서 `--csrf_token`을 꺼낸다.
- 이 standalone(hub) 프로세스의 command line에는 `--extension_server_port`가 없다(구버전의 확장-spawn LS에는 있었다). 따라서 포트는 반드시 OS의 LISTEN 테이블에서 알아내야 한다.

### 4.2 Connect 호출 형식

generated protobuf client가 아니라 JSON-over-HTTP Connect 형식이다. 서비스 경로는 `/exa.language_server_pb.LanguageServerService/<Method>`.

공통 header:

```
Content-Type: application/json
Connect-Protocol-Version: 1
X-Codeium-Csrf-Token: <--csrf_token 값>
```

**CSRF는 강제된다**(실측: token 없이 호출하면 `401`). 정상 Connect 서버인지 판별하는 health probe로는 `GetUnleashData`를 쓸 수 있다(가벼운 호출, 200).

### 4.3 quota 호출

실행 중인 `language_server.exe`에 직접 호출해 응답을 받은 메서드(실측):

- **`RetrieveUserQuotaSummary`** — quota 본체(200). 요청 body는 `{"forceRefresh": true}`. `forceRefresh: true`면 language server가 backend에서 account quota를 다시 조회하므로 다른 PC에서 소진한 양도 반영될 수 있다. 응답 schema는 5절.
- `GetUserStatus` — plan label 등 보강용(200). `userStatus.planName`에서 `"Pro"` 같은 plan을 얻는다(9절). quota 성공 후 짧은 deadline의 best-effort 호출.
- 보조: `GetAvailableModels`, `GetCascadeModelConfigData`, `GetUnleashData`(health) 등도 200을 반환한다.

> **메서드 이름은 바이너리/버전마다 다르다 — 반드시 probe.** 실행 중인 `language_server.exe`(standalone hub)에서는 **`RetrieveUserQuotaSummary`가 200**, `RetrieveUserQuota`는 `404`였다. 반대로 확장 번들 바이너리 `language_server_windows_x64.exe`의 문자열에는 `RetrieveUserQuota`만 있고 `…Summary`는 없다. 즉 같은 설치 안에서도 바이너리마다 메서드 집합이 다르다. 이 API는 공개 안정 API가 아니므로, 구현은 **실제로 응답하는 메서드를 후보 목록으로 probe**(`RetrieveUserQuotaSummary` 우선, `RetrieveUserQuota` 대체)하고 200을 주는 것을 사용해야 한다. 이름을 하드코딩하지 않는다.

## 5. quota 응답과 정규화

### 5.1 응답 형태 (실측 캡처)

`RetrieveUserQuotaSummary` body `{"forceRefresh": true}`의 실제 응답(account 식별자만 마스킹, 구조·필드는 원본 그대로):

```json
{
  "response": {
    "groups": [
      {
        "displayName": "Gemini Models",
        "description": "Models within this group: Gemini Flash, Gemini Pro",
        "buckets": [
          {
            "bucketId": "gemini-weekly",
            "displayName": "Weekly Limit",
            "description": "You have used some of your weekly limit, it will fully refresh in 6 days, 23 hours.",
            "window": "weekly",
            "remainingFraction": 0.9989373,
            "resetTime": "2026-06-28T00:03:00Z"
          },
          {
            "bucketId": "gemini-5h",
            "displayName": "Five Hour Limit",
            "description": "You have used some of your 5-hour limit, it will fully refresh in 4 hours, 57 minutes.",
            "window": "5h",
            "remainingFraction": 0.9936241,
            "resetTime": "2026-06-21T05:03:00Z"
          }
        ]
      },
      {
        "displayName": "Claude and GPT models",
        "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
        "buckets": [
          { "bucketId": "3p-weekly", "displayName": "Weekly Limit", "window": "weekly", "remainingFraction": 1, "resetTime": "2026-06-28T00:05:32Z" },
          { "bucketId": "3p-5h",     "displayName": "Five Hour Limit", "window": "5h",   "remainingFraction": 1, "resetTime": "2026-06-21T05:05:32Z" }
        ]
      }
    ],
    "description": "Within each group, models share a weekly limit and a 5-hour limit. ..."
  }
}
```

확정된 사실:

- **정확히 두 family × (주간 + 5시간) = 4개 window.** family displayName은 `"Gemini Models"`와 `"Claude and GPT models"`.
- **`bucketId`가 stable하고 globally unique하다:** `gemini-5h`, `gemini-weekly`, `3p-5h`, `3p-weekly`. → 이 값을 그대로 provider-stable window ID로 쓰면 된다(접두사 `antigravity:` 정도만 부여).
- **`window` 필드가 `"5h"` / `"weekly"`로 duration을 직접 알려준다.** displayName 문자열을 파싱해 분류할 필요가 없다.
- `remainingFraction`은 **직접 float**다(이 빌드에서 oneof 형태는 관측되지 않음). 그래도 parser는 oneof(`{"case":"remainingFraction","value":..}`)도 허용해 두는 편이 안전하다.
- `resetTime`은 ISO-8601(Z). `description`은 prose이며 **100%(remainingFraction=1)인 bucket에는 없을 수 있다**(위 `3p-*` 참조) → optional로 다룬다.

### 5.2 정규화 규칙

- `UsedRatio = 1 - remainingFraction`, `[0, 1]`로 clamp.
- bucket의 `window` 필드로 `UsageWindowType`(`5h`→FiveHour, `weekly`→Weekly)을 매핑한다.
- stable window ID는 `bucketId`를 그대로 쓴다(`antigravity:gemini-5h` 등). 4개 window가 서로 독립적으로 reconcile/notify되도록 한다(6절).
- `remainingFraction`이 없는 bucket은 100% 소진으로 단정하지 말고 초기 구현에서 생략한다(사용률 미상 row를 0%/100%로 가정하지 않는다).
- `resetTime`은 ISO-8601 우선, 실제 확인된 경우에만 epoch seconds fallback.
- machine-facing 숫자·시간은 모두 `CultureInfo.InvariantCulture`로 파싱·포맷한다(한국어/일본어 culture에서 `double.Parse`/`DateTime.Parse`가 깨지는 것 방지).
- `displayName`/`description`은 server가 이미 사용자 언어/문구로 채워 주지만, Gauge는 자체 localization 라벨(5시간/주간)을 쓰는 편이 3개 언어 일관성에 맞다. server 문자열은 보조로만.

## 6. 먼저 필요한 공통 모델 변경

Antigravity는 같은 semantic type의 window(`FiveHour` 2개, `Weekly` 2개)를 동시에 가질 수 있다. 현재 Gauge는 window를 주로 `UsageWindowType`으로 식별하므로, 이를 먼저 보강하지 않으면 두 family가 서로 충돌한다. Antigravity를 노출하기 전에 다음을 끝낸다.

1. **stable window identity.** `UsageWindow`에 provider-stable `Id`를 추가하고, `ToolCardViewModel`의 row reconciliation, `UsageNotificationEvaluator`의 observation key, usage cache, per-window UI state에서 이를 사용한다. `UsageWindowType.FiveHour`/`Weekly`는 duration 의미로 그대로 둔다(family를 표현하려고 enum case를 늘리지 않는다).
2. **cache migration.** cache에는 stable ID와, 재시작 후 현재 언어로 label을 복원할 수 있는 semantic 정보가 필요하다(이미 번역된 문자열만 저장하면 언어 변경 시 옛 언어가 남는다). cache schema version을 올리고 구버전 record를 migration하거나 호환되지 않는 usage cache만 안전하게 폐기한다. 사용자 설정 파일은 건드리지 않는다.
3. **notification 독립성.** evaluator가 tool name + stable window ID를 key로 쓰게 해서, 두 family의 5시간 window가 baseline·threshold mask·reset detection을 서로 독립적으로 갖게 한다. 이 작업 전에는 Antigravity window를 notification 평가에 넣지 않는다.

## 7. provider 아키텍처

모든 Antigravity-specific 동작은 `AntigravityProvider` 내부에 격리한다. provider 경계 밖에는 `IUsageProvider`와 정규화된 공통 model만 노출하고, UI는 snapshot의 출처를 알지 못한다.

```
AntigravityProvider
  ├─ AntigravityProcessDiscovery     ← 실행 중 language server 프로세스 + CSRF token 찾기 (attach)
  ├─ AntigravityEngineHost           ← IDE가 없을 때 language_server.exe를 직접 띄움 (delegate, 7.4)
  ├─ WindowsListeningPortTable       ← PID가 LISTEN 중인 포트 조회
  ├─ AntigravityLoopbackClient       ← loopback 전용 HTTPS/Connect 클라이언트
  ├─ AntigravityQuotaParser          ← tolerant 파서
  └─ AntigravitySnapshotNormalizer   ← 최대 4개 semantic window로 정규화
```

### 7.1 프로세스/토큰 탐색

- Antigravity 설치 경로 아래의 language server 프로세스를 찾는다. 현재 버전은 `language_server.exe`지만 구버전은 `language_server_windows_x64.exe`였으므로, 이름은 `language_server*.exe` + 실행 파일 경로가 Antigravity 설치 폴더 아래인지 + `--csrf_token` 존재로 식별한다(이름 하드코딩 금지). WMI 또는 잘 격리된 Win32 호출로 full command line을 읽는다.
- Windows quoting 규칙으로 argument를 파싱한다(공백 split 금지). `--csrf_token` 값을 추출한다.
- 후보가 여러 개면(여러 IDE 창/워크스페이스) 모두 평가해 가장 풍부한 정상 응답을 고른다.
- 프로세스가 탐색 도중 종료되거나 access denied가 나는 상황을 정상 race로 처리한다.
- **token이 포함된 전체 command line을 로그/예외에 남기지 않는다.**

### 7.2 PID별 LISTEN 포트 탐색

- 매 refresh마다 `netstat`/PowerShell을 실행해 문자열을 파싱하지 말고 Windows IP Helper API의 `GetExtendedTcpTable`을 사용한다.
- 후보 PID가 소유한 LISTEN socket만 반환한다. language server는 HTTPS·HTTP 두 포트를 열므로 두 포트를 구분해 HTTPS(gRPC)를 quota 호출에 쓴다.
- Windows는 PID를 재사용하므로, token을 보내기 전에 그 포트가 의도한 PID의 소유인지 확인한다.

### 7.3 loopback transport

- Antigravity local 통신에는 **전용** `HttpClient`/handler를 쓴다. Gauge의 일반 internet client에서 TLS 검증을 완화하지 않는다.
- self-signed 인증서는 정확한 loopback 주소(127.0.0.1 + 해당 포트) 요청에만 한정 허용한다. look-alike hostname과 모든 non-loopback 목적지는 거부한다.
- request별 짧은 timeout과 source 전체 deadline을 별도로 두고 cancellation을 전파한다.
- Connect header를 명시적으로 설정하고, CSRF token·account payload·raw 응답을 로그로 남기지 않는다.

### 7.4 엔진 위임 실행 (delegate 모드, IDE가 꺼졌을 때)

IDE가 실행 중이 아니면 Gauge가 `resources\bin\language_server.exe`를 직접 띄워 quota를 받는다. **실측으로 IDE를 완전히 종료한 상태에서도 동작 확인됨** — 띄운 엔진이 디스크에 저장된 로그인으로 스스로 인증해 `forceRefresh`로 backend의 최신 quota(다른 PC 사용량 포함)를 돌려줬다.

검증된 실행 방식:

- Claude/Codex의 *delegated refresh*와 같은 원리다. Gauge는 credential을 만지지 않는다 — 엔진이 자기 로그인으로 알아서 인증한다.
- 평범한 **hidden 콘솔 프로세스**로 띄우면 된다(ConPTY/pseudo-terminal 불필요). 인자는 실행 중 IDE가 쓰던 것과 동일하게 주되, **CSRF token은 Gauge가 생성한 GUID를 `--csrf_token`으로 넘기고**(이 경로에선 프로세스에서 token을 읽을 필요가 없다) `--https_server_port 0`으로 random 포트를 받는다.
- 엔진은 **자식(sidecar) 프로세스를 함께 띄운다.** 준비까지 0.5~수 초 걸리므로, 포트가 열리고 quota가 실제 parse될 때까지 readiness를 polling한다(포트 bind만으로 ready 판단 금지).

수명주기 요구사항(미구현, 7.4가 핵심 작업):

- **전제 조건:** 사용자가 Antigravity에 최소 1회 로그인해 디스크에 credential이 있어야 한다. 미로그인이면 이 경로는 signed-out으로 떨어진다.
- 60초마다 새로 띄우지 않는다. 한 번 띄운 엔진을 **짧게 warm 상태로 재사용**하고, 반복 실패/wedged면 재시작한다(적정 warm 시간은 10절에서 측정).
- Gauge가 띄운 프로세스 **트리 전체**를 Windows **Job Object**로 소유해, 정상 종료·updater 종료·crash 복구 시 sidecar까지 빠짐없이 정리한다.
- **Gauge가 만든 프로세스만** 종료한다. 사용자가 켠 IDE나 그 자식 language server는 절대 건드리지 않는다(PID + 실행 파일 경로 + 생성 시각으로 소유권 확인, PID 재사용 대비).
- attach 모드(실행 중 IDE)가 가능하면 그쪽을 우선하고, delegate 모드는 IDE가 없을 때만 쓴다.

## 8. source pipeline과 실패 처리

Windows의 런타임 순서:

```
attach: 실행 중인 language server probe
  ↓ (프로세스 없음)
delegate: Gauge가 language_server.exe를 직접 띄워 probe (7.4)
  ↓ (미설치 / 미로그인 / 기동 실패)
provider failure 전파
  ↓
UsageCoordinator가 마지막 성공 snapshot 유지
```

원칙:

- 미설치·미로그인(signed out)은 앱 crash가 아니라 예상 가능한 사용자 상태다(빈 상태 또는 last-good 표시).
- malformed 응답, TLS 실패, timeout, 내부 프로토콜 변화는 provider failure이며 **마지막 성공값을 지우지 않는다.**
- 알려진 quota window가 하나도 없는 availability-only 응답으로 유용한 cache를 덮어쓰지 않는다.
- cached snapshot을 새 `CapturedAt`으로 재포장하면 거짓 notification transition이 생기므로 원래 capture time을 유지한다.
- delegate 모드 덕분에 IDE가 꺼져 있어도 다른 PC의 사용량까지 받아올 수 있다. delegate마저 불가능할 때만(엔진 미설치 또는 미로그인) last-good + 마지막 갱신 시각으로 떨어진다.

## 9. 등록과 인증 UX

Antigravity는 기본 등록 도구가 아니라 settings의 `+ Add service`에서 opt-in으로 추가한다. local language server의 존재를 OAuth credential처럼 모델링하지 않는다.

확인된 인증 흐름(`auth.log`): Google OAuth(`accounts.google.com/o/oauth2/v2/auth`, scope `cloud-platform`·`userinfo.email`·`userinfo.profile`, baked client_id, `localhost:8151/oauth-callback`), 상태 머신 `uninitialized → signedOut → validatingLogin → signedIn`, 토큰 갱신은 `antigravity.handleAuthRefresh`, backend는 `cloudcode-pa.googleapis.com`. **이 OAuth 경로는 Gauge가 복사하지 않는다** — Gauge는 다른 도구의 credential을 읽거나 갱신하지 않고, OAuth token endpoint를 호출하지 않는다.

settings card가 구분해야 하는 상태:

- 로그인됨 + quota 정상(IDE 실행 중이든, Gauge가 엔진을 직접 띄웠든 동일하게 보인다)
- 엔진은 떴으나 signed out → 사용자를 앱 로그인으로 안내
- 미설치 또는 한 번도 로그인 안 함 → no-data 안내(이 경우 delegate 모드도 불가)
- source는 있으나 local API가 아직 초기화 중(readiness polling 중)

로그인 여부는 OAuth 토큰을 직접 읽지 말고 **live language server가 quota를 주는지(signedIn) vs signedOut 상태를 반환하는지로** 판별한다. 로그인 action은 사용자를 Antigravity 앱으로 보내고, Gauge가 login 과정을 캡처·기록하지 않는다.

## 10. polling과 성능

측정 결과(실측):

- **language server는 quota를 내부 캐시한다**(바이너리 식별자 `retrieveUserQuotaSummaryCache`, `fetchRetrieveUserQuotaSummary`). 그래서 호출 비용이 두 갈래로 갈린다:
  - **`forceRefresh` 없이 호출 → 캐시 즉답(1~2ms).** 거의 공짜. 단 값은 엔진이 마지막으로 backend에서 받아온 시점 기준이라 cross-device로는 stale일 수 있다.
  - **`forceRefresh: true` → backend 왕복(~0.3~0.6s).** 다른 PC 사용량까지 최신화. delegate 엔진의 cold start 직후 첫 호출은 ~1.6s.
- **throttling 관측 안 됨:** `forceRefresh:true`를 2.5초 간격 5연속 호출해도 전부 200, latency 안정적. 즉 가끔의 강제 갱신은 안전하다(그래도 매 poll마다 강제하지는 않는다). 정확한 캐시 TTL은 Go duration 상수라 문자열로는 안 보이며, 실용적으로는 아래 전략으로 충분하다.

권장 polling 전략:

- 60초 periodic cycle은 **`forceRefresh` 없이** 호출한다(캐시 즉답, 사실상 무비용).
- **`forceRefresh: true`는 가끔만** — popover open 강제 refresh, 또는 수 분마다 1회 — 써서 cross-device 최신값을 끌어온다.
- delegate 모드에선 캐시가 *우리가 띄운 엔진 프로세스 안*에 산다. 엔진을 매 cycle 죽이면 캐시도 사라져 다음 기동의 첫 호출이 cold(~1.6s)가 되므로, **엔진을 warm 상태로 재사용**하는 게 cold start와 cross-device 신선도 모두에 유리하다(7.4). 유휴가 길어지면 정리한다.
- popover open 강제 refresh와 60초 timer가 중복 호출을 만들지 않도록 기존 debounce(10초)를 그대로 적용한다.

## 11. 보안 경계

Gauge가 credential을 소유하지 않더라도 이 구현은 secret을 다룬다.

- CSRF token은 즉시 loopback 요청을 만드는 데만 쓰고 저장하지 않는다.
- token과 전체 process command line을 log/예외에 포함하지 않는다.
- TLS 검증을 전역 해제하지 않는다. insecure 인증서 허용은 정확한 loopback 요청으로 제한한다.
- token을 보내기 전에 포트가 의도한 PID 소유인지 검증한다.
- internal API payload에는 email·plan identity가 있을 수 있으므로 민감 정보로 취급한다.
- Antigravity 소유 credential을 읽거나 쓰거나 갱신·삭제하지 않는다.

## 12. 구현 순서

1. **live 자료 확보** — 핵심은 확보됨(4·5절: command line/포트/CSRF/메서드/schema). 남은 것은 `forceRefresh` throttling과 cross-device 반영 지연 측정(13절). 5.1 응답을 파서 fixture로 사용한다.
2. **공통 window identity 보강** — stable ID 추가, UI reconciliation·cache·notification key 변경, migration과 regression test(6절).
3. **tolerant 파서** — preferred summary와 빈약한 model row, reset 형식, disabled bucket, unknown fraction, clamp 처리. 최대 4개 semantic window로 정규화.
4. **프로세스/포트 discovery (attach)** — 후보 분류, quote-safe flag 파싱, `GetExtendedTcpTable` 연결, race·access-denied 처리.
5. **loopback client** — Connect header, HTTPS 포트 probe, loopback-only 인증서 예외, deadline/cancellation.
6. **엔진 host (delegate, 7.4)** — hidden 프로세스 기동, readiness polling, Job Object 소유, warm 재사용, Gauge 소유 프로세스만 정리.
7. **provider 통합** — attach 우선 → delegate fallback, 후보 평가로 가장 풍부한 snapshot 선택, provider detail을 공통 model·UI 밖으로 격리.
8. **등록·localization·settings UX** — `ToolKind`/catalog/provider wiring, 한국어·영어·일본어 문자열, opt-in 유지.
9. **lifecycle 검증** — IDE 실행/미실행, delegate cross-device 반영, Gauge 종료·update 시 엔진 정리, concurrent refresh와 반복 popover open, stale PID·사용자 소유 프로세스 미종료.

## 13. 검증 상태

실행 중인 language server에 직접 호출해 확정한 것:

- ✅ 실행 프로세스/포트: `language_server.exe`(`--standalone --subclient_type hub`), `127.0.0.1`의 HTTPS+HTTP random 포트(예 2654/2655).
- ✅ CSRF 강제: token 없이 호출 시 401.
- ✅ HTTPS 포트는 self-signed(=`-SkipCertificateCheck` 필요) — loopback-only 예외로 처리.
- ✅ quota 메서드: 이 빌드는 `RetrieveUserQuotaSummary`(200), `RetrieveUserQuota`는 404 → probe 필요.
- ✅ 응답 schema 전체(5.1), bucketId가 stable·globally unique, `window` 필드로 5h/weekly 구분, 정확히 4개 window.
- ✅ plan label: `GetUserStatus.userStatus.planName`(예 "Pro").
- ✅ **delegate 모드(7.4): IDE 완전 종료 상태에서 Gauge가 `language_server.exe`를 직접 띄워 quota 수신 성공.** 디스크 저장 로그인으로 자체 인증, `forceRefresh`로 backend 최신값(다른 PC 사용량 포함) 반환, hidden 프로세스로 충분(ConPTY 불필요), 자식 sidecar 포함 프로세스 트리 정리 확인.

- ✅ throttling/캐시(10절): LS가 quota를 내부 캐시(`retrieveUserQuotaSummaryCache`). `forceRefresh` 없으면 1~2ms 즉답, `forceRefresh:true`면 backend 왕복 ~0.3~0.6s(cold ~1.6s). 2.5초 간격 5연속 강제 호출에도 throttle 없음.

아직 추가 관측이 필요한 것:

- 캐시 TTL의 정확한 값(`forceRefresh` 없는 값이 얼마나 stale해질 수 있는지) — Go 상수라 정적 추출 불가, 장시간 관측 필요.
- 다른 device의 사용량이 `forceRefresh`로 반영되는 지연(초 단위 추정, 정밀 측정 미완).
- logout/account switch가 이미 실행 중인 language server에 어떻게 반영되는지.
- 버전 업그레이드 시 프로세스 이름/메서드 이름이 또 바뀌는지(확장 번들 LS와 standalone LS의 차이가 그 사례).

위 항목은 기억이나 macOS fixture로 추정하지 않는다.

## 14. 최소 테스트 matrix

**파싱:** 직접/중첩/oneof remaining fraction, invariant culture ISO-8601·epoch reset, fraction 누락·disabled·음수·1 초과, unknown wrapper 필드와 미래 group, 4개 window의 순서와 stable ID, family grouping과 hidden model 제외, reset-only row를 소진으로 표시하지 않고 생략.

**프로세스/포트:** quote 포함 Windows 경로·argument, 유사 executable 이름의 false positive, token 없는 프로세스 거부, 여러 valid candidate, 검사 도중 종료, PID 재사용과 identity mismatch.

**transport:** 올바른 Connect header, loopback self-signed 허용, non-loopback·look-alike hostname 거부, 포트 ownership 검증, request timeout·전체 deadline·cancellation, endpoint fallback과 API-level error payload.

**delegate 엔진 수명주기(7.4):** hidden 기동과 readiness 성공, signed-out 상태, 포트 없음·bind됐으나 not-ready·wedged 엔진, concurrent refresh가 한 엔진을 안전 공유, idle teardown과 앱 종료/업데이트 시 정리, Gauge 소유 트리(sidecar 포함)만 종료, 사용자 소유 IDE·stale/재사용 PID 미종료.

**Gauge 통합:** `FiveHour` 2개·`Weekly` 2개가 독립 유지, cache round-trip에서 stable identity·localization 의미 보존, 두 family의 notification baseline·threshold 비충돌, Antigravity 실패가 다른 provider를 막지 않음, 전체 실패 후 last-good 유지, 등록 해제 시 notification history 제거.

## 15. 관련 파일

**확인된 Antigravity 설치 자료:**

- `…\Programs\Antigravity\resources\bin\language_server.exe` — 실행 중 quota를 서빙한 Go language server(`RetrieveUserQuotaSummary`)
- `…\Programs\Antigravity\resources\app\extensions\antigravity\bin\language_server_windows_x64.exe` — 확장 번들 LS(메서드 집합이 다름; `RetrieveUserQuota`)
- `…\Programs\Antigravity\resources\app\extensions\antigravity\dist\extension.js` — LS를 spawn하고 Connect로 통신하는 확장
- `%APPDATA%\Antigravity\logs\<timestamp>\ls-main.log` · `auth.log` — LS 기동 인자/포트, OAuth 상태 머신

**macOS 레퍼런스(구조·파싱 원칙 참고용, process plumbing은 POSIX 전용이라 그대로 이식 불가):**

- `Ref/CodexBar-main/docs/antigravity.md`
- `Ref/CodexBar-main/Sources/CodexBarCore/Providers/Antigravity/AntigravityStatusProbe.swift`
- `Ref/CodexBar-main/Sources/CodexBarCore/Providers/Antigravity/AntigravityQuotaSummaryParser.swift`
- `Ref/CodexBar-main/Tests/CodexBarTests/AntigravityQuotaSummaryTests.swift`

**Gauge 통합 지점:**

- `Providers/IUsageProvider.cs`
- `Models/UsageSnapshot.cs`, `Models/UsageWindow.cs`, `Models/UsageWindowType.cs`
- `ViewModels/ToolCardViewModel.cs`
- `Services/UsageNotificationEvaluator.cs`, `Services/UsageCacheStore.cs`, `Services/UsageCoordinator.cs`
- `Models/ToolCatalog.cs`, `Services/ToolRegistryStore.cs`
- `App.xaml.cs`

## 16. 결정 요약

- Windows quota source는 `language_server.exe` 하나이며 두 방식으로 닿는다: 실행 중 IDE에 **attach**하거나, IDE가 꺼졌으면 Gauge가 엔진을 **직접 띄운다(delegate, 7.4 — 실측 확인)**. 둘 다 불가능할 때만(미설치·미로그인) last-good + freshness로 떨어진다. 단, 사용자가 최소 1회 로그인한 적은 있어야 한다.
- token은 language server 프로세스 command line의 `--csrf_token`에서 읽고(없으면 401), 포트는 그 PID의 LISTEN 테이블에서 찾으며(self-signed HTTPS), quota는 HTTPS 포트로 Connect 호출한다.
- quota 메서드는 실행 중 빌드 기준 `RetrieveUserQuotaSummary`(200)다. 단 바이너리/버전마다 메서드 이름이 다르므로 후보 probe로 결정하고, schema는 live 응답으로 확정한다.
- quota는 정확히 4개 window(`gemini-5h/weekly`, `3p-5h/weekly`)이며 `bucketId`를 stable ID로, `window` 필드를 5h/weekly 매핑으로 쓴다. plan label은 `GetUserStatus.userStatus.planName`.
- 인증·backend 통신은 Antigravity 앱이 소유한다. Gauge는 OAuth credential을 읽거나 갱신하지 않는다.
- 4개 window를 노출하기 전에 공통 stable window identity를 먼저 추가한다.
- 프로세스/TCP 테이블/loopback TLS/엔진 수명주기는 Windows native 메커니즘(WMI/Win32, `GetExtendedTcpTable`, 전용 HttpClient, Job Object)으로 구현한다. delegate 엔진은 Gauge 소유 프로세스 트리만 정리한다.
