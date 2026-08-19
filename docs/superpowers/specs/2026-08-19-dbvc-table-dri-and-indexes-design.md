# 테이블 제약·인덱스·확장 속성 스크립팅 설계

## 1. 문제

테이블 `.sql`에 **컬럼 정의만** 담긴다. 기본값(DEFAULT), 기본 키, 외래 키, UNIQUE, CHECK,
모든 인덱스, 확장 속성이 빠진다.

SSMS 테이블 디자이너로 컬럼과 기본값을 함께 추가했을 때 실제로 나온 결과다:

```
-- DBVC_ChangeLog: ALTER TABLE dbo.Table_2 ADD CONSTRAINT DF_Table_2_RegDate DEFAULT getdate() FOR RegDate
-- 저장소에 쓰인 것:
CREATE TABLE [dbo].[Table_2](
    [ID] [varchar](50) COLLATE Korean_Wansung_CI_AS NULL,
    [RegDate] [datetime2](7) NULL
) ON [PRIMARY]
```

원인은 `SmoManager.ScriptObjects`가 만드는 `ScriptingOptions`가 4개 값만 지정하고 나머지를
SMO 기본값에 맡기는 데 있다. `Microsoft.SqlServer.SqlManagementObjects 171.30.0`에서 실제로
객체를 만들어 읽은 기본값은 관련된 것이 전부 `false`다:

```
DriAll  DriDefaults  DriPrimaryKey  DriForeignKeys  DriUniqueKeys  DriChecks   -> False
Indexes  ClusteredIndexes  NonClusteredIndexes  XmlIndexes  FullTextIndexes    -> False
ExtendedProperties  Permissions  Statistics                                    -> False
```

`docs/superpowers/specs/`와 코드 주석 어디에도 이 옵션을 두고 내린 결정 기록이 없다.
의도한 범위 축소가 아니라 누락으로 판단한다.

### 왜 고쳐야 하는가

1. 저장소가 테이블의 실제 모습을 담지 못한다 — 형상 관리의 목적 자체가 흔들린다.
2. **배포·롤백 스크립트가 이 `.sql`을 그대로 병합한다**(`ScriptExporter` → `ScriptGenerator`).
   그 스크립트로 배포하면 기본 키도 인덱스도 기본값도 없는 테이블이 생긴다.
3. 스크립트에 담기지 않는 것만 바꾸면 DDL 로그는 변경을 기록하는데 파일은 그대로다 —
   목록에는 "수정"으로 뜨는데 비교창이 비어 보인다.

DML 트리거는 이 문제에 해당하지 않는다. `EnumerateTargets`가 `table.Triggers`를 따로 열거해
`[Schema]/Triggers/*.sql`에 독립 객체로 저장하고 있다.

## 2. 결정

`ScriptingOptions`에 다음을 켠다.

| 옵션 | 값 | 이유 |
|---|---|---|
| `DriAll` | true | 기본값·PK·FK·UNIQUE·CHECK를 한 번에 켠다. 개별 `Dri*`를 나열하면 SMO가 옵션을 더할 때 빠지는 것이 생긴다 |
| `Indexes` | true | 제약이 만들지 않는 일반 인덱스 |
| `ClusteredIndexes` / `NonClusteredIndexes` | true | `Indexes` 하나에 기대지 않고 명시한다 |
| `XmlIndexes` / `FullTextIndexes` | true | 같은 이유 |
| `ExtendedProperties` | true | MS_Description 등 컬럼 설명 |

**켜지 않는 것과 그 이유**

- `Permissions` — 서버·환경마다 로그인과 역할이 달라 저장소가 환경 종속이 된다. 배포 스크립트에
  들어가면 대상 환경에 없는 주체를 참조해 실패한다.
- `Statistics` — 데이터 분포의 부산물이지 스키마가 아니다. 같은 스키마에서도 매번 달라져
  잡음 diff를 만든다.
- `ScriptData` — DBVC는 스키마 형상 관리다.

## 3. 설계

### 3.1 옵션 구성을 이름 있는 자리로 뺀다

지금은 `ScriptObjects` 한복판의 객체 초기화자 안에 있어 테스트가 닿지 않는다.
`internal static ScriptingOptions BuildScriptingOptions()`로 빼고 `ScriptObjects`는 그것을 쓴다.
무엇을 켜고 껐는지가 한 곳에 모이고, 값이 계약으로 검증된다.

### 3.2 성능

제약·인덱스·확장 속성을 읽으려면 SMO가 테이블마다 추가 조회를 한다. 새로고침은 DDL 로그가
가리키는 객체만 스크립팅하므로(보통 한두 개) 체감되지 않는다. 비용이 드러나는 곳은 **전체
다시 추출**이다.

`ConfigureBulkEnumeration`의 `SetDefaultInitFields`는 건드리지 않는다. 그 주석에 실측으로
남은 결론이 있다 — 필드를 더 넣으면 열거가 오히려 느려진다(871 ms → 13359 ms). 이번 옵션은
열거가 아니라 **스크립팅 단계**의 조회를 늘리는 것이라 그 튜닝과 층이 다르다.

### 3.3 기존 저장소에 미치는 영향

옵션을 켠 뒤 **처음 추출되는 테이블의 `.sql`은 전부 바뀐다.** 기존 저장소를 쓰던 사용자는
테이블 전체가 "수정"으로 뜨는 큰 커밋을 한 번 겪는다. 이것은 결함이 아니라 그동안 빠져 있던
정보가 들어오는 것이다. `README.md`와 `docs/setup-checklist.md`에 적어 사용자가 놀라지 않게 한다.

새로고침은 DDL 로그가 아는 객체만 추출하므로, 최근에 바꾸지 않은 테이블은 **전체 다시 추출**을
누르기 전까지 옛 형태로 남는다. 이 점도 함께 적는다.

## 4. 검증

- `BuildScriptingOptions`가 켜야 할 것을 켜고 켜지 말아야 할 것을 껐는지 단위 테스트로 고정한다.
  값의 목록 자체가 계약이므로, 나중에 누가 조용히 끄면 테스트가 죽어야 한다.
- 실제 스크립트 내용은 `SmoManagerIntegrationTests`(로컬 SQL Server 필요, 없으면 Skip)에서
  기본값 제약을 가진 테이블을 만들고 스크립트에 `DEFAULT`가 들어가는지 본다.
- SSMS 21에서 직접 확인: 기본값·인덱스를 가진 테이블을 만들고 새로고침 → 비교창에 들어오는지.
  CI가 검증하지 못하는 구간이다.
