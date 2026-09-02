# 설계 문서: 개체 탐색기 객체별 변경 이력 조회 (DBVC View History)

이 문서는 사용자가 SSMS 개체 탐색기(Object Explorer)에서 특정 객체를 우클릭하여, DBVC 저장소에 기록된 해당 객체의 변경 이력(커밋 로그)을 손쉽게 조회할 수 있도록 하는 기능의 설계 및 구현 방안을 정의합니다.

## 1. 개요
현재 DBVC에서는 "수정된(Uncommitted) 객체"에 한해서만 변경 사항 보기 도구 창에서 과거 이력을 조회할 수 있습니다. 이미 커밋되어 로컬 작업 트리에 변경 사항이 없는 일반 객체의 이력을 보려면 사용자가 직접 Git CLI나 다른 Git 도구를 사용해야 합니다.

이 기능을 통해 개체 탐색기에서 우클릭만으로 기존 도구 창을 활용해 특정 객체의 전체 커밋 이력을 빠르게 조회할 수 있는 진입점을 제공합니다.

## 2. 진입점 (Entry Point)
- **위치:** SSMS 개체 탐색기(Object Explorer) 우클릭 컨텍스트 메뉴
- **메뉴 항목:** "DBVC: 이력 보기" (View History)
- **객체 식별 로직:**
  - `DbvcPackage.vsct`에서 적절한 Context Menu Group을 찾아 버튼을 등록합니다. (SSMS의 경우 VSIP Logging을 통해 GUID/ID를 확인하거나, 알려진 개체 탐색기 노드 메뉴에 연결합니다.)

    > **실제 구현은 VSCT가 아니다.** SSMS 21의 개체 탐색기 노드 컨텍스트 메뉴에는 확장이 붙을
    > 공개 CommandPlacement 지점이 없어, `ShowHistoryCommand`가 `IObjectExplorerService`에서
    > WinForms `TreeView`를 리플렉션으로 찾아 `ContextMenuStrip.Opening`을 후킹하고 메뉴 항목을
    > 직접 넣는다. 개체 탐색기는 패키지 초기화보다 늦게 뜰 수 있어 2초 폴링 타이머로 재시도한다.
    > `DbvcPackage.vsct`의 `ShowHistoryCommandId`(0x0102)는 테스트 상수로만 남아 있다.
  - `ObjectExplorerConnectionSource`를 통해 선택된 노드의 `INodeContext.Context` (URN 문자열)를 읽어옵니다.
  - `SsmsUrn` 클래스에 객체 타입(ObjectType), 스키마(Schema), 이름(Name)을 추출하는 메서드(예: `TryParseObjectIdentity(urn)`)를 추가합니다. (예: `Server/Database/Table[@Name='Person' and @Schema='dbo']` -> Table, dbo, Person)
  - `ObjectPathConvention.GetRelativePath`를 이용해 Git 저장소 내의 상대 경로(`RelativePath`)를 도출합니다.

## 3. UI 및 상태 관리 (Tool Window State)
별도의 팝업이나 새 창을 띄우는 대신, 기존의 뷰와 ViewModel을 재활용하여 사용성을 높입니다.

- **단일 객체 모드(Single Object Mode) 도입:** 
  - `ViewChangesViewModel`에 `IsSingleObjectMode`(bool)와 같은 속성을 추가합니다.
  - 이 모드가 활성화되면:
    1. 뷰어 상단의 '변경 사항 목록(그리드)' 영역을 화면에서 숨김(Collapsed) 처리합니다.
    2. 하단의 `ObjectHistoryViewModel` 뷰 영역이 전체 공간을 채우도록 확장됩니다.
    3. UI 상단에 **"🔙 변경 사항 목록으로 돌아가기"** 버튼과 "조회 중인 객체: `dbo.Person`"이라는 안내 문구를 렌더링합니다.

## 4. 데이터 흐름 (Data Flow)
1. 사용자가 개체 탐색기 트리에서 특정 객체(예: 테이블)를 우클릭하고 **"DBVC: 이력 보기"**를 클릭합니다.
2. 커맨드 처리기(`ShowHistoryCommand`)가 실행되어:
   - `DbvcPackage.ShowToolWindow()`를 통해 변경 사항 보기 도구 창을 화면에 띄우거나 활성화(Focus)합니다.
   - 현재 도구 창의 뷰모델(즉, `ViewChangesViewModel`의 인스턴스)에 `ShowHistoryFor(databaseName, relativePath)` 메서드를 호출합니다.
3. 뷰모델은 `IsSingleObjectMode = true`로 상태를 전환하고, 내부적으로 `History.Load(serverName, databaseName, relativePath)`를 호출합니다.
4. 백그라운드에서 `IGitManager`가 해당 파일 경로에 대한 커밋 로그를 읽어온 뒤 화면에 렌더링합니다.

## 5. 예외 및 엣지 케이스 (Error Handling)
- **이력이 없는 경우:** 이제 막 데이터베이스에 생성하여 한 번도 DBVC로 추출·커밋된 적 없는 객체일 경우, 기존 로직 그대로 "해당 객체의 이력이 없습니다."라는 안내 텍스트가 표시됩니다.
- **지원하지 않는 노드:** 폴더 노드(예: "테이블", "저장 프로시저" 최상위 폴더)나 서버/DB 최상위 노드를 클릭한 경우:
  - URN에서 테이블 이름 등을 추출할 수 없으므로 `ShowHistoryFor` 동작을 수행할 수 없습니다.
  - 컨텍스트 메뉴 노출 조건을 제어하여 아예 버튼이 비활성화(Disabled) 되거나 안 보이게 하거나, 명령 실행 시 조용히 무시(또는 알림)하도록 처리합니다.

## 6. 테스트 (Testing)
- `SsmsUrn`의 URN 파싱 로직(`TryParseObjectIdentity`)에 대해 다양한 SMO URN 패턴(따옴표 이스케이프, 띄어쓰기 등)을 다루는 단위 테스트를 추가합니다.
- `ViewChangesViewModel`의 `ShowHistoryFor`가 모드 전환을 올바르게 수행하는지와 '돌아가기' 커맨드가 원래 상태로 정상 복귀시키는지 단위 테스트를 작성합니다.
