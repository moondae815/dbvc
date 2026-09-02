# DBVC Global History Changed Files Diff Design

## 1. 목적 (Purpose)
- 현재 DBVC의 "이력(History)" 탭에서 전체 데이터베이스 이력을 조회할 때, 선택한 커밋에 어떤 파일들이 변경되었는지 목록으로 확인하고, 그 중 하나를 클릭하여 즉각적으로 Diff 뷰를 볼 수 있도록 개선합니다.
- 단, "단일 객체 이력 보기" (특정 파일의 이력만 필터링한 상태) 모드에서는 파일 목록을 띄우지 않고 기존처럼 2단 레이아웃을 유지하여 화면 공간을 절약합니다.

## 2. 데이터 흐름 및 아키텍처 (Architecture & Data Flow)
### 2.1 Git 계층 (IGitManager)
- `IGitManager`에 `GetChangedFilesAtCommit(serverName, databaseName, commitSha)` 메서드 추가.
- `LibGit2Sharp`를 사용해 대상 커밋(`commit.Tree`)과 부모 커밋(`commit.Parents.First().Tree`)의 트리를 `repo.Diff.Compare<TreeChanges>`로 비교.
- 변경된 각 파일의 상태(Added, Modified, Deleted)와 상대 경로(RelativePath) 반환.
- (예외 처리) 부모가 없는 최초 커밋인 경우 빈 트리(Empty Tree)와 비교.

### 2.2 뷰모델 계층 (ObjectHistoryViewModel)
- `ObservableCollection<HistoryChangedFileViewModel> ChangedFiles` 속성 추가.
- `SelectedChangedFile` 속성 추가.
- 전체 이력 모드(`IsSingleObjectMode == false`)에서 커밋(`SelectedEntry`)이 변경되면:
  1. `GetChangedFilesAtCommit` 호출하여 `ChangedFiles` 갱신.
  2. `SelectedChangedFile`을 `null`로 초기화.
  3. `UpdateDiffModel`을 호출하여 하단 Diff 뷰 정리.
- `SelectedChangedFile`이 변경되면:
  - 선택된 파일의 `RelativePath`를 이용해 `UpdateDiffModel` 호출 -> 하단 Diff 뷰에 해당 파일의 변경점 표시.

## 3. UI 구성 (ViewChangesControl.xaml)
- **전체 이력 모드 (`IsSingleObjectMode == false`)**:
  - `Grid`를 3단 수직 분할로 구성:
    - 상단: `HistoryListView` (기존 커밋 이력 목록)
    - 중간: `ChangedFilesListView` (신규 추가, 변경된 파일 목록)
    - 하단: `Diff View` (기존)
  - 중간 영역과 하단 영역 사이에 `GridSplitter` 추가.
  - `ChangedFilesListView`의 컬럼: `상태(State)`, `객체 유형(ObjectType)`, `객체명(ObjectName)`. (기존 스테이징 목록과 유사하게 파싱하여 바인딩)

- **단일 객체 모드 (`IsSingleObjectMode == true`)**:
  - 기존의 2단 수직 분할 구조(상단: 커밋 이력, 하단: Diff) 그대로 유지.
  - 파일 목록 뷰는 표시되지 않음.

## 4. 예외 및 엣지 케이스 처리 (Edge Cases)
- **부모 커밋이 없는 최초 커밋 선택 시**: 빈 트리와 비교하여 모든 파일을 `Added` 상태로 처리해야 함.
- **선택 해제 시 방어 코드**: 커밋이 선택 해제되거나, 파일 목록에서 파일이 선택 해제되면 `SelectedDiffModel`을 `null`로 만들어 하단 뷰를 깨끗하게 비움.
- **UI 테마 대응**: 새로 추가하는 `ChangedFilesListView`의 텍스트와 배경은 SSMS 셸 테마 리소스(또는 WPF 기본 리소스)를 상속받도록 하여 가시성 확보 (검은 바탕/흰 바탕 문제 방지).
