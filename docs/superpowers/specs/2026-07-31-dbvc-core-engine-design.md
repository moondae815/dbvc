# DBVC Core Engine Design Spec

## 1. Overview
이 문서는 DBVC (DB Version Control) SSMS 플러그인의 "Phase 2: 코어 엔진 완성" 단계를 위한 세부 설계 문서입니다. 앞서 구축된 기초 스캐폴딩(`DBVC.Core` 프로젝트) 내에 있는 핵심 매니저 클래스들(`SmoManager`, `GitManager`, `StateTracker`)의 스텁(Stub) 코드를 실제 비즈니스 로직으로 구현합니다.

## 2. Scope & Features
본 설계 문서는 초기 14개 MVP 기능 중 아래의 기능 구현을 다룹니다:
* Feature 2: Database Object Export (SMO)
* Feature 3: Change Detection (Diff)
* Feature 5: Git Commit (One-Click)
* Feature 6: Git Pull
* Feature 7: Object History
* Feature 13: Local Change Cache (DDL Trigger 기반 감지)
* Feature 14: Support Objects (Table, View, Stored Procedure, Function, Trigger, UDT, UDTT, Sequence, Synonym)

## 3. Component Design

### 3.1. SmoManager (객체 스크립팅)
**목적:** DB 객체의 DDL을 로컬 파일로 추출.
* **패키지 의존성:** `Microsoft.SqlServer.SqlManagementObjects`
* **구현 세부사항:**
  * `Server` 인스턴스를 통해 대상 `Database` 객체에 접근.
  * `Scripter` 객체를 사용하여 `Create` 스크립트를 추출.
  * **스크립트 옵션:** `IncludeIfNotExists = false`, `ScriptDrops = false`. 대상 파일에 덮어쓰기 형태로 스크립트를 저장.
  * **폴더 구조 컨벤션:** 로컬 저장소 경로 내 `[Schema]/[ObjectType]/[ObjectName].sql` 형태로 저장. (예: `dbo/Tables/Users.sql`)
  * **오류 처리:** 특정 객체 스크립팅 실패 시, 해당 객체만 실패로 처리하고 전체 스크립팅 프로세스가 중단되지 않도록 `try-catch` 구현.

### 3.2. GitManager (Git 제어)
**목적:** 로컬 Git 저장소의 커밋 및 원격 동기화.
* **패키지 의존성:** `LibGit2Sharp`
* **구현 세부사항:**
  * **Repository 접근:** `_configManager`를 통해 조회된 로컬 폴더 경로로 `Repository` 객체 인스턴스화.
  * **Commit:** `Commands.Stage(repo, "*")`를 통해 변경사항을 스테이징하고, `repo.Commit(message, author, committer)`를 호출하여 커밋 생성.
  * **Pull:** `Commands.Pull(repo, signature, pullOptions)`를 호출. 충돌(Conflict)이 감지되면 Pull을 안전하게 중단하고 예외를 발생시켜 사용자에게 알림.
  * **History:** 특정 파일 경로를 넘겨받아 `repo.Commits.QueryBy(path)`로 해당 파일의 커밋 로그(SHA, Message, Date)를 반환.

### 3.3. StateTracker (변경 상태 캐시)
**목적:** `DBVC_ChangeLog` 테이블과 Git 상태를 비교하여 메모리 내 변경 상태 캐시 관리.
* **패키지 의존성:** `Microsoft.Data.SqlClient` (SSMS 내장 의존성과 충돌을 피하기 위해 `System.Data.SqlClient`를 사용할 수도 있으나 가급적 최신 드라이버 권장)
* **구현 세부사항:**
  * **DB 조회:** 연결 문자열을 사용해 `DBVC_ChangeLog` 테이블에서 아직 커밋되지 않은(또는 마지막 동기화 이후의) DDL 이벤트 로그를 `SELECT`.
  * **상태 비교:** 로컬 Git 저장소의 상태(`repo.RetrieveStatus()`)와 DB 로그를 종합하여 각 객체의 최종 상태(Modified, Added, Deleted, Clean)를 결정.
  * **스레드 안전성:** 내부 상태 캐시는 비동기적(UI Thread 외부)으로 갱신될 수 있으므로 `ConcurrentDictionary` 등의 Thread-safe 구조를 유지.

## 4. Testing Strategy
* **Unit Tests:** `LibGit2Sharp` 및 `SMO` 로직은 외부 의존성이 크므로, 실제 임시(Temp) 폴더와 Local Git Repo를 생성하여 Integration-style의 단위 테스트를 작성(`tests/DBVC.Core.Tests`).
* **Test Database:** SMO 추출 테스트를 위해 `LocalDB` 또는 테스트 전용 SQL 인스턴스를 활용한 TDD 고려 (테스트 환경에 맞춰 구성).
