# DBVC (DB Version Control) SSMS Plugin Design Spec

## 1. Overview
DBVC는 SSMS 21 환경에서 데이터베이스 객체의 버전을 Git으로 관리하기 위한 경량화된 확장(Extension)입니다. VersionSQL과 유사하게 동작하지만, 개발자가 가장 많이 사용하는 "핵심 기능(14개 MVP)"에 집중하여 가볍고 빠르게 동작하도록 설계되었습니다.

## 2. MVP Features (14 Items)
1. SSMS Plugin (VSIX for SSMS 21 / 64-bit)
2. Database Object Export (using SMO)
3. Change Detection (Diff)
4. View Changes (Tool Window)
5. Git Commit (One-Click)
6. Git Pull
7. Object History
8. Deployment Script 생성 (단순 병합)
9. Rollback Script 생성 (Git 이전 리비전 병합)
10. Object Explorer Overlay (상태 아이콘: M/A/D/C)
11. SQL Editor Context Menu
12. Compare with Repository (Side-by-Side Diff)
13. Local Change Cache (DDL Trigger 기반 실시간 감지)
14. One-Click Commit

## 3. Architecture & Components
* **Target Environment**: SQL Server Management Studio (SSMS) 21 (Visual Studio 2022 Shell, 64-bit).
* **Architecture Style**: Native Integrated Architecture (All-in-One VSIX).
* **Core Modules**:
  * `DbvcPackage`: VSIX 진입점, 메뉴/툴바/창 초기화.
  * `SmoManager`: SQL Server Management Objects(SMO)를 이용해 데이터베이스 객체의 스크립트(CREATE/ALTER)를 추출.
  * `GitManager`: `LibGit2Sharp` 라이브러리를 사용하여 로컬 Git 저장소를 제어. 외부 Git 클라이언트 설치가 불필요함.
  * `StateTracker`: 변경 캐시 관리, 오버레이 상태 업데이트.
  * UI 계층: `UiController`라는 단일 클래스는 없다. `ViewChangesToolWindow`(창 등록), `ViewChangesControl`(WPF `UserControl`), `ViewChangesViewModel`과 `RelayCommand`(MVVM)로 나뉘어 있다. SQL 에디터 컨텍스트 메뉴는 `DBVC.Vsix/Commands`가 담당한다.

## 4. Data Flow & Integration
### 4.1. Change Detection (실시간 변경 감지)
1. 타겟 DB에 **DDL Trigger**를 생성하고, 모든 DDL 작업(CREATE, ALTER, DROP)을 `DBVC_ChangeLog` 테이블에 기록.
2. SSMS 플러그인의 `StateTracker`가 **사용자가 Refresh를 누를 때** `DBVC_ChangeLog`를 읽어와 로컬 캐시를 업데이트. 주기적 폴링은 구현되어 있지 않으며 계획에도 없다.
3. 갱신된 상태를 바탕으로 Object Explorer의 노드(테이블, 프로시저 등)에 상태 아이콘(M, A, D)을 오버레이 표시.

### 4.2. Repository Mapping
* **저장 위치**: `%APPDATA%\DBVC\mappings.json`
* 사용자별 로컬 환경에서 특정 SQL Server의 Database와 로컬 Git 저장소 디렉토리를 매핑.
* Git 폴더 내 스크립트 저장 구조: `[Schema]/[ObjectType]/[ObjectName].sql` (예: `dbo/StoredProcedures/usp_GetUsers.sql`)

### 4.3. Scripting (Deployment & Rollback)
* **Deployment Script**: 'View Changes' 창에서 선택한 객체들의 현재 타겟 DB 기준 최신 코드를 단일 `.sql` 텍스트 파일로 병합하여 제공.
* **Rollback Script**: 선택한 객체들이 마지막으로 커밋되기 직전 상태의 원본 코드를 Git History(`LibGit2Sharp`)에서 불러와 단일 `.sql` 파일로 병합.

## 5. Error Handling & Edge Cases
* **Merge Conflict**: `Git Pull` 시 충돌 발생 시, 안전하게 Pull을 Abort하고 사용자에게 경고 메시지를 표시하여 DB가 오염되는 것을 방지.
* **Trigger Failure**: DDL Trigger 수행 중 기록 권한 문제 등이 발생할 경우, 실무 트랜잭션이 실패하지 않도록 Trigger 내부에서 에러를 묵인(Try-Catch 처리).

## 6. Testing Strategy
* `GitManager`와 `SmoManager` 비즈니스 로직에 대한 NUnit/xUnit 단위 테스트 작성.
* SSMS 21 Experimental Instance를 통한 VSIX 수동 통합 테스트 진행.
