# DBVC (Database Version Control)

DBVC는 SQL Server Management Studio (SSMS) 21을 위한 데이터베이스 형상 관리(Version Control) 확장(VSIX) 플러그인입니다. 데이터베이스 스키마 변경 사항을 Git을 통해 추적하고, 변경된 내용을 확인 및 커밋할 수 있는 통합된 작업 환경을 제공합니다.

## 주요 기능
- **변경 사항 자동 감지 (DDL Trigger):** 데이터베이스에서 발생하는 스키마 변경 사항을 실시간으로 감지하고 추적합니다. (`DBVC_ChangeLog` 테이블 활용)
- **객체 스크립팅 (SMO):** SQL Server Management Objects(SMO)를 사용해 변경된 객체(테이블, 저장 프로시저 등)를 `.sql` 파일 형태로 내보냅니다.
- **Git 통합 (LibGit2Sharp):** 내보낸 `.sql` 파일들을 Git 저장소에 스테이징(Staging) 및 커밋(Commit)할 수 있는 완벽한 형상 관리 기능을 제공합니다.
- **WPF 기반 차이점 뷰어 (View Changes Tool Window):**
  - 변경된 항목들의 목록을 손쉽게 확인하고 선택할 수 있습니다.
  - `AvalonEdit` 및 `DiffPlex`를 활용하여 변경 전(Old)과 변경 후(New)의 SQL 코드를 T-SQL 문법 하이라이팅이 적용된 형태의 좌우 분할(Side-by-Side) 뷰로 직관적으로 비교할 수 있습니다.

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
2. **데이터베이스 초기화 (Setup):** Object Explorer에서 버전 관리를 적용할 데이터베이스에 연결합니다. 해당 데이터베이스에 아직 DBVC 설정이 안 되어 있다면 패널 중앙에 **"Setup DBVC"** 버튼이 나타납니다. 이 버튼을 클릭하면 플러그인이 데이터베이스에 추적용 테이블(`DBVC_ChangeLog`)과 DDL 감지 트리거를 자동으로 설치합니다.
3. **변경 사항 추적 및 비교:** 스키마(테이블, 저장 프로시저 등)를 변경하면 DBVC 패널에 변경된 객체 목록이 나타납니다. 항목을 클릭하면 하단 뷰어를 통해 변경 전(Old)과 후(New)의 T-SQL 코드 차이를 구문 강조와 함께 확인할 수 있습니다.
4. **Git 커밋:** 하단 입력창에 커밋 메시지를 작성하고 **"Commit"** 버튼을 누르면, 변경된 객체들의 DDL이 `.sql` 스크립트 파일로 자동 추출된 후 Git 저장소에 커밋됩니다.

### 빌드 및 테스트
명령줄에서 다음 명령을 실행하여 전체 솔루션을 빌드하고 테스트할 수 있습니다.
```bash
# 솔루션 빌드
dotnet build src/DBVC.sln

# 단위 테스트 실행
dotnet test tests/DBVC.Core.Tests
dotnet test tests/DBVC.Vsix.Tests
```

## 라이선스
MIT License
