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
