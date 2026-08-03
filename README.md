# DBVC (Database Version Control)

DBVC는 SQL Server Management Studio (SSMS) 21을 위한 데이터베이스 형상 관리(Version Control) 확장(VSIX) 플러그인입니다. 데이터베이스 스키마 변경 사항을 Git을 통해 추적하고, 변경된 내용을 확인 및 커밋할 수 있는 통합된 작업 환경을 제공합니다.

## 주요 기능
- **변경 사항 자동 감지 (DDL Trigger):** DDL 트리거가 스키마 변경을 발생 즉시 `DBVC_ChangeLog` 테이블에 기록합니다. 기록된 변경은 **Refresh**를 누르거나 **Connect·Setup DBVC·Commit**이 끝난 뒤 자동으로 화면에 반영됩니다(주기적 폴링은 하지 않습니다).
- **객체 스크립팅 (SMO):** SQL Server Management Objects(SMO)를 사용해 변경된 객체(테이블, 저장 프로시저 등)를 `.sql` 파일 형태로 내보냅니다.
- **Git 통합 (LibGit2Sharp):** 내보낸 `.sql` 파일들을 Git 저장소에 스테이징(Staging) 및 커밋(Commit)할 수 있는 완벽한 형상 관리 기능을 제공합니다.
- **WPF 기반 차이점 뷰어 (View Changes Tool Window):**
  - 변경된 항목들의 목록을 손쉽게 확인하고 선택할 수 있습니다.
  - `AvalonEdit` 및 `DiffPlex`를 활용하여 변경 전(Old)과 변경 후(New)의 SQL 코드를 T-SQL 문법 하이라이팅이 적용된 좌우 분할(Side-by-Side) 뷰로 비교할 수 있습니다. 추가·삭제·수정된 줄은 배경색으로 구분되며, 좌우 줄이 정렬되고 스크롤이 함께 움직입니다.
- **배포/롤백 스크립트 생성:** 선택한 객체들의 DDL을 단일 `.sql` 파일로 병합합니다. 배포 스크립트는 현재 DB 기준 최신 코드를, 롤백 스크립트는 각 객체가 마지막으로 커밋되기 직전의 코드를 담습니다.
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

### 사전 요구 사항
- .NET 8.0 SDK 이상 (또는 .NET 10.0)
- Visual Studio 2022 (VSIX 개발 SDK 포함)
- SSMS 21 (확장 설치 테스트용)

### 플러그인 사용법
1. **설치 및 실행:** 솔루션 빌드 후 생성된 `.vsix` 확장 파일을 실행하여 SSMS 21에 설치합니다. SSMS 상단 메뉴에서 **View(보기) > Other Windows(다른 창) > DBVC View Changes**를 클릭하여 형상 관리 패널을 엽니다.
2. **대상 데이터베이스 지정:** 패널 상단의 **Server / Database** 입력란에 대상을 입력하고 **"Connect"** 를 누릅니다.
3. **Git 저장소 연결:** 해당 데이터베이스가 Git 저장소에 매핑되어 있지 않으면 경고 배너가 나타나고 커밋이 비활성화됩니다. 배너의 **"저장소 연결..."** 버튼을 눌러 스크립트를 보관할 폴더를 지정하세요. 이미 `git init`된 폴더여야 하며, 아니면 오류가 표시되고 매핑되지 않습니다. 매핑은 `%APPDATA%\DBVC\mappings.json`에 저장됩니다.
4. **데이터베이스 초기화 (Setup):** 대상 데이터베이스에 아직 DBVC 설정이 안 되어 있다면 패널 중앙에 **"Setup DBVC"** 버튼이 나타납니다. 이 버튼을 클릭하면 플러그인이 추적용 테이블(`DBVC_ChangeLog`)과 DDL 감지 트리거를 설치합니다. 권한 부족 등으로 설치에 실패하면 오류 메시지가 표시되고 화면은 초기화 전 상태로 유지됩니다.
5. **변경 사항 추적 및 비교:** 스키마(테이블, 저장 프로시저 등)를 변경한 뒤 **"Refresh"** 를 누르면 현재 DB 객체가 `.sql` 파일로 추출되고 변경된 객체 목록이 나타납니다. 항목을 클릭하면 하단 뷰어에서 Git 저장소의 이전 버전(Old)과 현재 DB 버전(New)의 T-SQL 차이를 구문 강조와 함께 확인할 수 있습니다.
6. **Git 커밋:** 커밋할 항목을 체크하고 커밋 메시지를 작성한 뒤 **"Commit"** 버튼을 누르면, 체크된 객체의 `.sql` 파일만 Git 저장소에 커밋됩니다. 커밋된 변경은 `DBVC_ChangeLog`에서 처리 완료로 표시되어 다음 새로고침 시 목록에서 사라집니다.
   데이터베이스에서 삭제(DROP)된 객체는 새로고침 시 해당 `.sql` 파일이 저장소에서 함께 제거되므로, 커밋하면 삭제가 그대로 형상 관리에 반영됩니다.
7. **원격 변경 가져오기:** **"Pull"** 버튼을 누르면 원격 저장소의 변경을 로컬 저장소로 가져옵니다. 커밋하지 않은 변경이 있으면 먼저 확인을 받습니다 — 충돌이 발생하면 병합을 되돌리면서 그 변경도 함께 사라지기 때문입니다(Refresh로 다시 추출할 수 있습니다). Pull은 파일만 가져올 뿐 데이터베이스에 적용하지 않으므로, 받은 스크립트를 확인한 뒤 필요하면 직접 실행하세요.
   저장소를 `git clone`이 아니라 `git init`으로 직접 만들었다면 현재 브랜치에 추적 중인 원격 브랜치가 없을 수 있습니다. 이때는 Pull이 안내 메시지와 함께 멈추므로, Git 클라이언트에서 `git push -u origin <브랜치>`를 한 번 실행해 추적을 설정하세요.
8. **객체 이력 확인:** 목록에서 객체를 선택하고 하단의 **History** 탭을 열면 그 객체의 `.sql` 파일을 변경한 커밋들이 최신순으로 표시됩니다.

### 빌드 및 테스트
명령줄에서 다음 명령을 실행하여 전체 솔루션을 빌드하고 테스트할 수 있습니다.
```bash
# 솔루션 빌드
dotnet build DBVC.slnx

# 단위 테스트 실행
dotnet test tests/DBVC.Core.Tests
dotnet test tests/DBVC.Vsix.Tests
```

> **참고:** `.vsct` 컴파일과 `.vsix` 패키징에는 Windows의 Visual Studio SDK가 필요합니다.
> 다른 OS에서는 C# 컴파일과 단위 테스트만 수행됩니다.

`.vsix` 생성은 Windows에서 다음과 같이 수행합니다.
```powershell
msbuild src/DBVC.Vsix/DBVC.Vsix.csproj -restore -p:Configuration=Release
```

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
