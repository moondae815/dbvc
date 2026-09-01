# DBVC History Diff View Design

## 1. Overview
현재 DBVC의 이력 보기(History) 기능은 선택된 객체의 커밋 이력(날짜, 작성자, 커밋 메시지)만 목록 형태로 제공합니다. 
이 설계는 특정 커밋을 선택했을 때 해당 커밋에서 변경된 파일 내용을 즉시 확인할 수 있도록, 내장 Side-by-Side Diff 뷰와 SSMS 기본 Diff 탭 기능을 추가하는 것을 목표로 합니다.

## 2. Architecture & Data Flow

### 2.1. GitManager 계층 확장
`IGitManager` 인터페이스와 `GitManager` 구현체에 다음 메서드를 추가하여 특정 커밋의 변경 전/후 텍스트를 가져올 수 있도록 합니다.
- `string? GetFileContentAtCommit(string serverName, string databaseName, string relativeFilePath, string commitSha)`
- `string? GetFileContentAtCommitParent(string serverName, string databaseName, string relativeFilePath, string commitSha)`

*참고: GitManager 내부에서 `repo.Lookup<Commit>(sha)`를 이용해 해당 커밋과 첫 번째 부모 커밋의 Tree에서 파일 Blob 내용을 추출합니다. 최초 커밋(부모가 없는 경우)은 빈 문자열로 처리합니다.*

### 2.2. ViewModel 계층 (`ObjectHistoryViewModel`)
- `HistoryEntryViewModel SelectedEntry`: ListView에서 현재 선택된 커밋 바인딩
- `SideBySideDiffModel? SelectedDiffModel`: 선택된 커밋의 Diff 데이터 바인딩
- 이벤트 처리: `SelectedEntry`의 setter에서 `SelectedDiffModel` 갱신 로직(GitManager 호출)을 수행합니다. 
- 커맨드: `ICommand OpenDiffWindowCommand` 를 추가하여, SSMS 내장 `IVsDifferenceService`를 호출하도록 합니다. (실제 서비스 호출은 이벤트를 통해 코드 비하인드로 위임하거나 의존성을 주입받아 사용)

### 2.3. View 계층 (`ViewChangesControl.xaml`)
- **이력 보기 탭 UI 변경**:
  - 기존 `<Grid>`를 두 개의 `RowDefinition`으로 나눕니다 (예: 높이 `1*`과 `2*`, 사이에 `GridSplitter` 배치).
  - 상단 Row: 기존 이력 `<ListView>`. `SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"` 추가.
  - 하단 Row: `DeploymentDifferenceList`에서 사용하는 것과 동일한 `AvalonEdit` 기반의 좌우 분할 Diff 뷰 추가.
- **코드 비하인드 연동**:
  - `ObjectHistoryViewModel.PropertyChanged` 또는 `SelectionChanged` 이벤트를 구독하여, `SelectedDiffModel`이 변경될 때 `ApplyDiffPanes` 함수를 호출해 AvalonEdit 렌더러를 업데이트합니다.
- **더블클릭 동작 (SSMS 기본 Diff)**:
  - `<ListView>`의 `MouseDoubleClick` 이벤트 핸들러 또는 `InputBinding`을 통해, 선택된 변경 사항을 임시 파일 두 개로 쓰고 Visual Studio 셸의 `IVsDifferenceService`를 호출하여 새 탭을 엽니다.

## 3. Error Handling & Edge Cases
- **최초 커밋 (Initial Commit)**: 부모 커밋이 없을 경우, 이전 텍스트(Old Text)는 빈 문자열로 취급하여 전체가 추가된(Added) 것으로 표시합니다.
- **파일 삭제/이름 변경**: 현재는 단일 파일(객체) 경로 기준으로 조회하므로 파일이 삭제된 커밋이라면 새로운 내용은 빈 문자열로 취급합니다.
- **DiffService 및 IVsDifferenceService 누락**: `IVsDifferenceService`를 가져올 수 없는 비-Visual Studio 환경(예: 단위 테스트)에서는 더블클릭 이벤트가 안전하게 무시되거나 에러 메시지를 노출하도록 처리합니다.

## 4. Testing Strategy
- `GitManager`에 추가되는 커밋 특정 내용 조회 메서드에 대해 단위 테스트 작성. (Mock 저장소에서 특정 커밋의 Blob을 정상 반환하는지)
- `ObjectHistoryViewModel`의 `SelectedEntry` 할당 시 `SelectedDiffModel`이 올바르게 생성되는지 검증.
