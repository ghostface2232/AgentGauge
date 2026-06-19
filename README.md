<img width="128" alt="gauge_icon" src="https://github.com/user-attachments/assets/ab882100-f767-4ed1-91d2-91c7c04492d5" />

# Gauge

Gauge는 Claude Code, Codex, Cursor의 실제 사용량 한도를 손쉽게 확인하는 Windows 시스템 트레이 앱입니다.


## 스크린샷
<img width="572" height="862" alt="스크린샷 2026-06-19 091057" src="https://github.com/user-attachments/assets/9f3140c2-f139-4b76-837f-9346306076c1" />



## 주요 기능

- Claude Code, Codex, Cursor의 실제 사용량 표시
- 설정 화면의 **+ 서비스 추가**로 원하는 서비스를 등록하고, 카드에서 제거 가능(기본: Claude Code · Codex)
- 70% 초과 시 노랑색 / 90% 초과 시 빨간색으로 진행 막대 및 트레이 아이콘 컬러 변경
- 60초 주기로 사용량 갱신 / 시스템 트레이에서 앱 열 때 즉시 갱신
- 도구가 많아지면 팝오버 높이를 제한하고 내부 스크롤로 표시
- Windows 시작 시 자동 실행 설정
- 라이트/다크 모드 지원


## 요구 사항

- Windows 10 2004(빌드 19041) 이상 또는 Windows 11
- x64 PC
- 사용할 도구 설치: Claude Code CLI, Codex CLI, Cursor 앱 중 필요한 것
- 해당 도구에 로그인된 상태(Cursor는 Cursor 앱에서 로그인)


## 실행

1. `GaugeSetup-win-x64.exe`로 설치합니다(관리자 권한 불필요). 완료 화면에서 바로 실행할 수 있습니다.
2. 작업 표시줄 알림 영역의 Gauge 아이콘을 왼쪽 클릭합니다.

앱은 일반 메인 창을 띄우지 않습니다. 트레이 아이콘을 오른쪽 클릭하면 자동 시작을 전환하거나 앱을 종료할 수 있습니다.

서명되지 않은 로컬 빌드에서는 Windows SmartScreen의 알 수 없는 게시자 경고가 나타날 수 있습니다.


## 로그인과 데이터

Gauge는 자격증명을 직접 발급하거나 갱신하지 않습니다. 각 공식 CLI가 관리하는 파일을 읽기 전용으로 사용합니다.

| 도구 | 자격증명 위치 | 로그인 방법 |
| --- | --- | --- |
| Claude Code | `%USERPROFILE%\.claude\.credentials.json` | `claude /login` |
| Codex | `%CODEX_HOME%\auth.json` 또는 `%USERPROFILE%\.codex\auth.json` | `codex login` |
| Cursor | `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (읽기 전용) | Cursor 앱에서 로그인 |

Cursor는 별도 CLI 로그인이 없으며, Cursor 앱에 로그인하면 Gauge가 로컬 세션 토큰을 읽어 사용량을 표시합니다(파일은 읽기 전용으로만 접근).

로그인이 필요하면 팝오버의 설정 화면에서 해당 CLI 로그인 프로세스를 열 수 있습니다. Gauge는 CLI 자격증명 파일을 쓰거나 삭제하지 않으며, 토큰이나 CLI 로그인 출력을 기록하지 않습니다.


## 업데이트

Gauge는 시작 시 GitHub의 최신 Release를 조용히 확인하고, 새 버전이 있으면 설정 화면의 **업데이트** 카드에 표시합니다. 카드의 **업데이트 확인** 버튼으로 수동 확인도 가능합니다.

새 버전이 있을 때 **지금 업데이트**를 누르면 설치 프로그램을 내려받아 자동(무인) 모드로 실행합니다. 실행 중인 Gauge가 종료되고, 같은 위치에 새 버전이 설치된 뒤 자동으로 다시 시작됩니다. 관리자 권한은 필요하지 않습니다.


## 현재 제한 사항

- CLI가 갱신하는 토큰에 의존하므로 오랫동안 CLI를 사용하지 않아 토큰이 만료되면 CLI에서 다시 로그인해야 할 수 있습니다.
- 앱 자체 OAuth/PKCE 및 토큰 갱신은 구현되어 있지 않습니다.
- 공식 코드 서명이 포함되어 있지 않습니다.
- 배포 자동화는 현재 x64만 대상으로 합니다.
