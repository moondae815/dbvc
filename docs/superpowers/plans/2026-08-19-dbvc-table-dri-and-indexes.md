# 테이블 제약·인덱스·확장 속성 스크립팅 구현 계획

설계: `docs/superpowers/specs/2026-08-19-dbvc-table-dri-and-indexes-design.md`

## 1. 옵션 구성을 테스트 가능한 자리로 뺀다

- [x] `SmoManagerTests`에 `BuildScriptingOptions_EnablesConstraintsIndexesAndExtendedProperties` 추가 —
      `DriAll`, `Indexes`, `ClusteredIndexes`, `NonClusteredIndexes`, `XmlIndexes`,
      `FullTextIndexes`, `ExtendedProperties`가 true인지. (실패 확인)
- [x] `BuildScriptingOptions_LeavesEnvironmentSpecificArtifactsOut` 추가 —
      `Permissions`, `Statistics`, `ScriptData`, `ScriptDrops`, `IncludeIfNotExists`가 false인지.
      끄는 쪽도 계약이다. (실패 확인)
- [x] `SmoManager`에 `internal static ScriptingOptions BuildScriptingOptions()`를 만들고
      기존 4개 값 + 새 옵션을 담는다. 무엇을 왜 켰는지/껐는지 XML 주석에 남긴다.
- [x] `ScriptObjects`가 그 메서드를 쓰도록 바꾼다. `ToFileOnly`/`FileName`은 그대로.
- [x] 두 테스트 통과 확인.

## 2. 실제 스크립트 내용 검증 (로컬 SQL Server 필요, 없으면 Skip)

- [x] `SmoManagerIntegrationTests`에 기본값 제약과 비클러스터드 인덱스를 가진 임시 테이블을
      만들고 추출 → `.sql`에 `DEFAULT`와 `CREATE NONCLUSTERED INDEX`가 들어가는지 확인하는
      테스트 추가. 기존 픽스처의 Skip 규약을 그대로 따른다.

## 3. 문서·버전

- [x] `README.md` — 저장소에 무엇이 담기는지 설명하는 자리에 제약·인덱스·확장 속성이 포함됨을
      적고, 기존 저장소는 **전체 다시 추출** 한 번이 필요하다는 것을 함께 적는다.
- [x] `docs/setup-checklist.md` 동작 검증에 "기본값·인덱스를 가진 테이블을 만들고 새로고침 →
      비교창에 제약과 인덱스가 보이는지" 항목 추가.
- [x] `src/DBVC.Vsix/source.extension.vsixmanifest` 버전 올림.

## 4. 마무리

- [x] `dotnet build DBVC.slnx`, `dotnet test tests/DBVC.Core.Tests`, `dotnet test tests/DBVC.Vsix.Tests`
- [x] SSMS 21에서 직접 확인해야 하는 항목을 사용자에게 알린다 — CI가 못 보는 구간이다.
