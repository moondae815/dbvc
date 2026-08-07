# DBVC (Database Version Control)

DBVC는 SQL Server Management Studio (SSMS) 21을 위한 데이터베이스 형상 관리(Version Control) 확장(VSIX) 플러그인입니다. 데이터베이스 스키마 변경 사항을 Git을 통해 추적하고, 변경된 내용을 확인 및 커밋할 수 있는 통합된 작업 환경을 제공합니다.

## 주요 기능
- **변경 사항 자동 감지 (DDL Trigger):** DDL 트리거가 스키마 변경을 발생 즉시 `DBVC_ChangeLog` 테이블에 기록합니다. 기록된 변경은 **Refresh**를 누르거나 **Connect·Setup DBVC·Commit**이 끝난 뒤 자동으로 화면에 반영됩니다(주기적 폴링은 하지 않습니다).
- **객체 스크립팅 (SMO):** SQL Server Management Objects(SMO)를 사용해 변경된 객체(테이블, 저장 프로시저 등)를 `.sql` 파일 형태로 내보냅니다.
- **Git 통합 (LibGit2Sharp):** 내보낸 `.sql` 파일들을 Git 저장소에 스테이징(Staging) 및 커밋(Commit)할 수 있는 완벽한 형상 관리 기능을 제공합니다.
- **WPF 기반 차이점 뷰어 (View Changes Tool Window):**
  - 변경된 항목들의 목록을 손쉽게 확인하고 선택할 수 있습니다.
  - `AvalonEdit` 및 `DiffPlex`를 활용하여 변경 전(Old)과 변경 후(New)의 SQL 코드를 T-SQL 문법 하이라이팅이 적용된 좌우 분할(Side-by-Side) 뷰로 비교할 수 있습니다. 추가·삭제·수정된 줄은 배경색으로 구분되며, 좌우 줄이 정렬되고 스크롤이 함께 움직입니다.
- **배포/롤백 스크립트 생성:** 선택한 객체들의 DDL을 단일 `.sql` 파일로 병합합니다. 배포 스크립트는 현재 DB 기준 최신 코드를, 롤백 스크립트는 각 객체가 마지막으로 커밋되기 직전의 코드를 담습니다. 생성된 파일의 헤더에는 제외된 객체가 기록됩니다.
- **SQL 에디터 컨텍스트 메뉴:** 에디터에서 객체 이름을 선택하고 우클릭하면 저장소 버전과 바로 비교할 수 있습니다.
- **Git Pull:** 원격 저장소의 변경을 로컬 저장소로 가져옵니다. 충돌이 발생하면 병합을 중단하고 되돌립니다. 받은 스크립트를 데이터베이스에 적용할지는 사용자가 판단합니다.
- **객체 이력:** 선택한 객체의 커밋 이력(날짜·작성자·메시지·SHA)을 하단 History 탭에서 확인할 수 있습니다.

### 기능 커버리지
14개 MVP 기능 중 13개가 구현되어 있습니다. **Object Explorer 상태 아이콘 오버레이(Feature 10)는 미구현**입니다.
SSMS Object Explorer의 아이콘 오버레이에는 공개 확장점이 없고 필요한 어셈블리가 NuGet에 배포되지 않아,
검증 가능한 형태로 구현할 수 없다고 판단했습니다.
사유와 선행 조건은 [docs/superpowers/plans/2026-08-01-dbvc-object-explorer-overlay.md](docs/superpowers/plans/2026-08-01-dbvc-object-explorer-overlay.md)에 정리되어 있습니다.
변경 상태는 View Changes 도구 창에서 모두 확인할 수 있습니다.

## 아키텍처 및 기술 스택
DBVC는 성능과 유지보수성을 위해 비즈니스 로직(Core)과 UI(Vsix) 계층이 분리되어 있습니다.
- **타겟 환경:** SSMS 21 (Visual Studio 2022 Shell, 64-bit)
- **DBVC.Core:** 
  - `.NET Standard 2.0` 대상
  - 형상 관리 핵심 로직 (`StateTracker`, `GitManager`, `SmoManager`)
- **DBVC.Vsix:**
  - `.NET Framework 4.8` 대상 (SSMS 21 호환)
  - MVVM 패턴 기반의 WPF 사용자 인터페이스
  - UI 라이브러리: `AvalonEdit` (T-SQL 구문 강조), `DiffPlex` (Diff 렌더링 엔진)
- **테스트:** NUnit 4 및 Moq를 사용한 단위 테스트 (TDD 방법론 적용)

## 모듈 구조
```text
dbvc/
├── src/
│   ├── DBVC.Core/         # Git 연동, 상태 관리, SMO 데이터 추출 로직
│   └── DBVC.Vsix/         # VS/SSMS ToolWindow, ViewModels, XAML 레이아웃
└── tests/
    ├── DBVC.Core.Tests/   # 핵심 로직 단위 테스트
    └── DBVC.Vsix.Tests/   # UI/ViewModel 단위 테스트
```

## 시작하기

**처음 도입한다면 [도입 체크리스트](docs/setup-checklist.md)를 따라가세요.** Git 저장소를 만들기 전 상태에서 `.vsix` 빌드, SSH 준비, 저장소 생성, SSMS 설치, 데이터베이스 초기화, 동작 검증까지 체크하며 진행할 수 있습니다. 온라인 GitHub와 폐쇄망 GitLab 두 환경을 모두 다루며, 단계 순서와 그 이유도 함께 적혀 있습니다.

아래는 설치를 마친 뒤의 동작 방식입니다.

### 동작 방식

- **변경 추적:** 스키마를 변경한 뒤 **Refresh** 를 누르면 현재 DB 객체가 `.sql` 파일로 추출되고 변경된 객체 목록이 나타납니다. 항목을 클릭하면 하단 **Diff** 탭에서 저장소의 이전 버전(Old)과 현재 DB 버전(New)을 구문 강조와 함께 비교할 수 있습니다.
- **커밋:** 항목을 체크하고 메시지를 작성한 뒤 **Commit** 을 누르면 체크된 객체의 `.sql` 파일만 커밋됩니다. 커밋된 변경은 `DBVC_ChangeLog`에서 처리 완료로 표시되어 다음 새로고침 시 목록에서 사라집니다. 데이터베이스에서 삭제(DROP)된 객체는 새로고침 시 `.sql` 파일이 저장소에서 함께 제거되므로, 커밋하면 삭제가 그대로 반영됩니다.
- **원격 변경 가져오기:** **Pull** 은 원격의 변경을 로컬 저장소로 가져옵니다. **파일만 가져올 뿐 데이터베이스에 적용하지 않으므로**, 받은 스크립트를 확인한 뒤 필요하면 직접 실행하세요. 커밋하지 않은 변경이 있으면 먼저 확인을 받습니다 — 병합 중 충돌이 나면 되돌리면서 그 변경도 함께 사라질 수 있기 때문입니다(Refresh로 다시 추출할 수 있습니다).
- **객체 이력:** 목록에서 객체를 선택하고 하단 **History** 탭을 열면 그 객체의 `.sql` 파일을 변경한 커밋들이 최신순으로 표시됩니다.

**Git 인증은 SSH만 지원합니다.** DBVC는 Git 자격 증명을 묻지도 저장하지도 않고, libgit2가 시스템 `ssh`에 그대로 위임합니다. HTTPS 원격을 매핑하면 Pull이 실패하면서 SSH로 바꾸는 방법을 안내합니다. 준비 절차는 [체크리스트 2단계](docs/setup-checklist.md#2단계--ssh-준비-개발-노트북)에 있습니다.

**데이터베이스 연결 정보는 SSMS 개체 탐색기에서만 옵니다.** DBVC 창에는 입력란이 없습니다 — 개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 선택하고 **Connect** 를 누르면 서버·데이터베이스·인증 방식·계정·암호를 그 연결에서 그대로 가져와 접속합니다. Windows 통합 인증과 SQL Server 인증을 모두 지원하며, 어느 쪽인지는 개체 탐색기의 연결이 정합니다.

**인증 정보는 디스크에 저장되지 않습니다.** SSMS가 살아 있는 동안 프로세스 메모리에만 있고, SSMS를 닫으면 사라집니다. 다시 열었을 때는 개체 탐색기에 접속한 뒤 **Connect** 를 한 번 누르면 됩니다. DBVC 창을 열어 둔 채 개체 탐색기에서 다른 데이터베이스를 선택하면 대상이 저절로 따라가지는 않고, 선택이 다르다는 안내가 뜹니다 — **Connect** 를 눌러야 전환됩니다. Microsoft Entra ID로 접속한 연결은 토큰 기반이라 재사용할 수 없으며, 이 경우 사유를 표시하고 접속을 시도하지 않습니다.

### 빌드 및 테스트

소스를 빌드하려면 .NET 8.0 SDK 이상(또는 .NET 10.0)이 필요합니다.

```bash
# 솔루션 빌드
dotnet build DBVC.slnx

# 단위 테스트 실행
dotnet test tests/DBVC.Core.Tests
dotnet test tests/DBVC.Vsix.Tests
```

> **참고:** `.vsct` 컴파일과 `.vsix` 패키징에는 Windows와 Visual Studio 2022의
> **Visual Studio 확장 개발** 워크로드가 필요합니다.
> 다른 OS에서는 C# 컴파일과 단위 테스트만 수행됩니다.

`.vsix` 생성 명령과 확인 방법은 [체크리스트 1단계](docs/setup-checklist.md#1단계--vsix-만들기-개발-노트북)에 있습니다.

> **알려진 이슈:** GitHub Actions의 `windows-latest`에서는 msbuild가 성공해도 `.vsix`가
> 생성되지 않습니다. `Microsoft.VSSDK.BuildTools`가 복원·임포트되지 않는 것으로 보이며
> 원인은 아직 규명되지 않았습니다. 이 때문에 VSIX 패키징은 CI에서 제외되어 있습니다.
> 자세한 내용은 `.github/workflows/ci.yml`의 주석을 참고하세요.

### CI가 검증하는 범위
`main` push와 PR마다 GitHub Actions가 다음을 검증합니다.

| 잡 | 내용 |
| --- | --- |
| Windows | 전체 빌드 + **net48** 및 net10.0 단위 테스트 |
| Linux | 전체 빌드 + net10.0 단위 테스트 |

`.NET Framework 4.8` 타깃은 Windows에서만 실행할 수 있습니다.
`Microsoft.Data.SqlClient`가 net462 구현체를 `runtimes/win` 아래에만 배포하기 때문이며,
Mono를 설치해도 해결되지 않습니다.

**CI로 검증되지 않는 것:** WPF 렌더링, VS 패키지 로딩, `.vsct` 메뉴 등록, SSMS 통합, 실제 DB 연결.
이들은 SSMS 21 실행 환경에서 수동으로 확인해야 합니다.

## 라이선스
MIT License
