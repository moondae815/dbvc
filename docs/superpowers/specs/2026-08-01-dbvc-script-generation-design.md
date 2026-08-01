# DBVC Script Generation Design Spec (Deployment & Rollback)

## 1. Overview
이 문서는 14개 MVP 기능 중 아직 설계되지 않았던 두 기능의 세부 설계를 다룬다.

* **Feature 8: Deployment Script 생성 (단순 병합)**
* **Feature 9: Rollback Script 생성 (Git 이전 리비전 병합)**

두 기능 모두 "View Changes 창에서 선택(체크)한 객체들"을 입력으로 받아 **단일 `.sql` 텍스트 파일**을 만든다.
상위 설계(ssms21-plugin-design 4.3)의 서술을 구현 가능한 수준으로 구체화한다.

## 2. Scope
### In Scope
* 선택된 객체들의 DDL을 정해진 순서로 하나의 스크립트로 병합
* Deployment: **현재 타겟 DB 기준 최신 코드** (= 새로고침 시 작업 트리에 추출된 `.sql`)
* Rollback: **각 객체가 마지막으로 커밋되기 직전 상태의 코드** (Git History)
* 배치 구분자(`GO`) 삽입 및 객체별 헤더 주석
* 생성 결과를 사용자가 지정한 경로에 저장

### Out of Scope
* **의존성 정렬.** 상위 설계가 "단순 병합"을 명시한다. 객체 간 참조 순서는 사용자가 책임진다.
  (단, 타입 → 테이블 → 뷰/함수/프로시저 수준의 **안정적이고 예측 가능한 정렬**은 제공한다. 3.3 참고)
* 차등 배포(ALTER 생성). 추출된 스크립트는 SMO의 CREATE 스크립트 그대로다.
* 데이터 마이그레이션.
* 생성된 스크립트의 실행. DBVC는 파일을 만들 뿐 대상 서버에 적용하지 않는다.

## 3. Component Design

### 3.1. `ScriptGenerator` (DBVC.Core)
**목적:** 여러 객체의 DDL 조각을 하나의 스크립트 문서로 병합.

```
string BuildScript(IEnumerable<ScriptSection> sections, ScriptKind kind)
```

* `ScriptSection`: `{ QualifiedName, RelativePath, Sql }`
* `ScriptKind`: `Deployment` | `Rollback`
* 순수 함수. DB·Git·파일 시스템에 접근하지 않으므로 전량 단위 테스트 대상이다.

**출력 형식**

```sql
/* ============================================================
   DBVC Deployment Script
   Generated: <ISO-8601>
   Objects: 3
   ============================================================ */

/* ---- dbo.Users (dbo/Tables/Users.sql) ---- */
CREATE TABLE ...
GO

/* ---- dbo.usp_GetUsers (dbo/StoredProcedures/usp_GetUsers.sql) ---- */
CREATE PROCEDURE ...
GO
```

* 각 섹션 뒤에 단독 행 `GO`를 넣는다. 원본 조각이 이미 `GO`로 끝나면 중복 삽입하지 않는다.
* 내용이 비어 있는 섹션은 건너뛰되, 건너뛴 사실을 헤더에 기록한다.

### 3.2. 소스 해석
| 기능 | 좌측(원본) 소스 |
| --- | --- |
| Deployment | 작업 트리의 `.sql` 파일. 새로고침 시 `SmoManager`가 추출해 둔 **현재 DB 상태**다. |
| Rollback | `GitManager.GetFileContentBeforeLastCommit(...)` |

**Rollback의 "이전 리비전" 정의**
해당 파일을 건드린 **가장 최근 커밋의 부모** 시점 내용이다.
`repo.Commits.QueryBy(path)`의 첫 항목이 마지막 커밋이므로, 그 다음 항목(두 번째 로그 엔트리)의 내용을 취한다.
파일이 커밋 이력에 한 번만 등장하면(= 최초 생성 이후 수정 없음) 되돌릴 이전 상태가 없으므로 해당 객체는 건너뛴다.

### 3.3. 정렬
"단순 병합"이되 실행 결과가 매번 달라지지 않도록 **결정적(deterministic) 순서**를 사용한다.

1. 객체 타입 그룹 순서: `Types` → `TableTypes` → `Tables` → `Sequences` → `Synonyms` → `Views` → `Functions` → `StoredProcedures` → `Triggers`
2. 같은 그룹 내에서는 스키마 한정 이름 오름차순(대소문자 무시)

이는 의존성 해석이 아니라 **관례적 배치 순서**다. 순환 참조나 뷰 간 의존은 해결하지 않는다.

## 4. UI Integration (DBVC.Vsix)
* View Changes 창 상단 액션 영역에 버튼 2개 추가: **Deployment Script**, **Rollback Script**
* 활성 조건: 매핑됨 + 초기화됨 + 체크된 항목 1개 이상 (Commit 버튼과 동일하되 커밋 메시지는 불필요)
* 클릭 시 저장 위치를 묻고 파일로 기록한다.
  저장 대화상자는 `IFileSaveDialog`로 추상화해 ViewModel을 테스트 가능하게 유지한다.
* 생성 결과를 요약해 알린다. 예: `3개 객체를 내보냈습니다. 2개 객체는 이전 리비전이 없어 제외했습니다.`

## 5. Error Handling
* 이전 리비전이 없는 객체는 **오류가 아니라 제외 대상**이다. 제외 목록을 사용자에게 알린다.
* 모든 객체가 제외되면 파일을 만들지 않고 경고만 표시한다.
* 파일 쓰기 실패(권한/경로)는 `MessageBox`로 알린다. 부분 저장은 하지 않는다.
* 저장 대화상자를 사용자가 취소하면 아무 일도 일어나지 않는다(오류 아님).

## 6. Testing Strategy
* `ScriptGenerator`는 순수 함수이므로 병합 형식·`GO` 처리·정렬·빈 섹션 처리 전부 단위 테스트한다.
* `GitManager.GetFileContentBeforeLastCommit`은 실제 임시 저장소를 만들어 검증한다
  (커밋 2회 이상 / 1회만 / 파일 없음의 세 경우).
* ViewModel은 `IFileSaveDialog`와 코어 인터페이스를 Moq로 대체해 검증한다.
