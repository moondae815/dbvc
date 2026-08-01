# DBVC P1 결함 수정 설계 (삭제 객체 정리 · 매핑 등록 UI · Diff 하이라이팅)

## 1. Overview

설계 문서와 구현을 대조한 결과, **설계에 명시되어 있으나 실제로는 동작하지 않는** 결함 3건을 확인했다.
이 문서는 그 3건의 수정 설계를 다룬다. 세 항목은 서로 독립적이며 구현 순서에 제약이 없다.

| # | 설계 근거 | 현재 동작 |
| --- | --- | --- |
| 1 | ssms21-plugin-design 4.1 (DROP 추적, 상태 D) | DROP된 객체의 `.sql` 파일을 아무도 지우지 않아 삭제가 커밋되지 않는다 |
| 2 | ssms21-plugin-design 4.2 (Repository Mapping) | 매핑을 등록할 UI가 없어 `mappings.json`을 손으로 만들어야 한다 |
| 3 | view-changes-design 2.3, 3.2 (DiffPlex 렌더링) | 양쪽 원문을 평문으로 표시할 뿐 차이를 강조하지 않는다 |

### 1.1. 결함 1의 실패 경로

`SmoManager`는 현재 존재하는 객체만 열거해 파일을 쓰고, 사라진 객체의 파일은 그대로 둔다
(`SmoManager.EnumerateTargets`). 그 결과 `DROP TABLE dbo.Users` 이후:

1. `DBVC_ChangeLog`에 `DROP_TABLE`이 기록되고 목록에 `Deleted`로 표시된다.
2. 작업 트리의 `dbo/Tables/Users.sql`은 그대로 남아 Git이 변경을 감지하지 못한다.
3. `CommitChanges`가 스테이징할 것을 찾지 못해 `false`를 반환하고, UI는 "커밋할 변경사항이 없습니다"로 끝난다.
4. `MarkProcessed`가 호출되지 않아 항목이 목록에 영구히 남는다.

다른 객체와 함께 커밋하면 더 나쁘다. 다른 파일이 스테이징되어 커밋은 성공하고,
`MarkProcessed`가 삭제 항목까지 처리 완료로 표시해 **삭제가 커밋되지 않은 채 목록에서 사라진다.**

`DiffServiceTests`의 전제 "객체가 DROP되면 작업 트리에 파일이 없다"는 이 결함 탓에 실제로는 성립하지 않는다.

## 2. Scope

### In Scope
* DDL 로그의 DROP 이벤트에 근거한 작업 트리 파일 정리
* View Changes 창에서 DB↔Git 저장소 매핑을 등록하는 경로
* DiffPlex 모델을 사용한 줄 정렬·줄 배경색·좌우 스크롤 동기화
* 위 변경으로 실제와 어긋나게 되는 문서의 갱신

### Out of Scope
* **Feature 6(Git Pull), Feature 7(Object History)의 UI 연결.** 코어는 구현·테스트되어 있으나 UI에 노출되지 않았다. 별도 작업으로 다룬다.
* **설계·코드의 세부 문구 불일치 정정.** `ScriptGenerator` 헤더의 스킵 기록 누락, 스크립트 생성 성공 시 요약 알림 부재, `UiController` 명칭 드리프트 등. 별도 작업으로 다룬다.
* **트리거 설치 이전에 삭제된 객체.** DDL 로그에 근거가 없으므로 자동 정리 대상이 아니다. 사용자가 파일을 직접 지우면 Git 상태로 잡혀 정상 처리된다.
* **삭제의 취소·복구.** Git 클라이언트의 몫이다.
* **SSMS 다크 테마 연동.** Diff 배경색은 고정값을 쓰되 교체 가능한 형태로 노출한다.
* **단어 단위 diff 강조.** 줄 단위까지만 구현한다.

## 3. Component Design

### 3.1. `WorkingTreeCleaner` (DBVC.Core)

**목적:** DDL 로그가 DROP을 기록한 객체의 `.sql` 파일을 작업 트리에서 제거한다.

기존 매니저 중 어느 것도 이 책임을 갖지 않는다. `StateTracker`는 상태 캐시,
`SmoManager`는 추출, `GitManager`는 Git 전용이다. 따라서 새 컴포넌트를 둔다.

```
IWorkingTreeCleaner
    CleanupResult RemoveDeletedObjectFiles(string repoPath, IEnumerable<ChangeRecord> records)

CleanupResult
    List<string> RemovedPaths
    List<string> FailedPaths
    bool HasFailures
```

인터페이스는 `Abstractions.cs`에, 구현은 `src/DBVC.Core/WorkingTreeCleaner.cs`에 둔다.

**삭제 대상 판정**은 아래를 모두 만족하는 레코드에 한한다. 하나라도 어긋나면 건너뛴다.

1. `State`가 `"Deleted"` (대소문자 무시)
2. `LastLogId > 0` — DDL 로그에 근거가 있는 항목만. Git 상태에서만 유래한 항목(`LastLogId == 0`)은 이미 파일이 없다는 뜻이므로 지울 것이 없다
3. `ObjectPathConvention.TryParseRelativePath`가 `RelativePath`를 `[Schema]/[타입]/[이름].sql` 규약으로 해석함
4. `repoPath`와 결합한 절대 경로가 저장소 루트의 하위임 — 경로 탈출 방어
5. 해당 파일이 실제로 존재함

`repoPath`가 비었거나 존재하지 않는 디렉터리면 빈 결과를 반환한다.

**오류 처리:** 파일 하나의 삭제 실패(잠김·권한 등)가 나머지를 막아서는 안 된다.
`SmoManager.ScriptAll`과 동일하게 개별 실패를 격리해 `FailedPaths`에 모은다.

**빈 디렉터리는 남긴다.** Git이 빈 디렉터리를 추적하지 않으므로 정리할 이유가 없고,
지우면 다음 추출 때 다시 만들어야 한다.

#### 3.1.1. 호출 지점

`ViewChangesViewModel.Refresh`에서 `RefreshState` 직후, 목록을 채우기 전에 호출한다.

```
SMO 추출 → RefreshState → GetPendingChanges → [정리] → Changes 채우기
```

이 순서인 이유: `RefreshState`가 Git 상태를 읽는 시점에는 파일이 아직 남아 있으므로
Git은 변경을 보고하지 않고, DDL 로그의 `Deleted`가 그대로 채택된다.
정리는 그 판정을 바꾸지 않으며, 이후 `Commands.Stage`가 삭제를 스테이징할 수 있게 만든다.

`FailedPaths`가 비어 있지 않으면 기존 `warnings` 목록에 합류시켜 경고 배너로 알린다.

ViewModel 생성자에 `IWorkingTreeCleaner?` 매개변수를 추가하고 기본값으로 `WorkingTreeCleaner`를 쓴다.
생성자 매개변수가 늘어나지만, 테스트가 각 의존성을 Moq로 개별 대체하는 현행 방식과 맞다.

### 3.2. 매핑 등록 UI (DBVC.Vsix)

#### 3.2.1. `IFolderBrowseDialog`

```
IFolderBrowseDialog
    string? PromptForFolder(string description, string? initialPath)   // 취소하면 null
```

`IFileSaveDialog`와 같은 위치·같은 계약이다. net48 WPF에는 폴더 선택 대화상자가 없으므로
구현체 `FolderBrowserDialogAdapter`는 `System.Windows.Forms.FolderBrowserDialog`를 쓴다.
`DBVC.Vsix.csproj`에 `System.Windows.Forms` 프레임워크 어셈블리 참조를 추가한다. 패키지 추가는 없다.

#### 3.2.2. `IGitManager.IsRepository`

```
bool IsRepository(string path)
```

`GitManager`에 이미 있는 `private static IsValidRepository`를 공개 인스턴스 메서드로 노출한다.
매핑 전 검증에만 쓴다.

#### 3.2.3. `ConnectRepositoryCommand` (ViewChangesViewModel)

* **활성 조건:** `HasContext && !IsMapped`. 컨텍스트와 `IsMapped`가 바뀔 때 재평가한다.
* **동작 순서**
  1. 폴더 선택을 요청한다. 취소하면 아무 일도 하지 않는다(오류가 아니다).
  2. 선택한 폴더가 Git 저장소인지 `IsRepository`로 확인한다. 아니면 오류를 알리고 **매핑을 저장하지 않는다.**
     유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패하므로 여기서 막는다.
  3. `AddMapping(server, database, path)`으로 등록한다. `ConfigManager`가 `mappings.json`에 즉시 영속화한다.
  4. `SetContext(server, database)`를 다시 실행해 매핑·초기화 상태를 재판정하고 목록을 새로고침한다.

#### 3.2.4. XAML 재구성

현재 경고 배너는 `IsInitialized == true`인 Grid 안에 있어 초기화 전에는 보이지 않고,
Setup 오버레이가 같은 메시지를 중복 표시한다(`ViewChangesControl.xaml` 49–53행, 93–95행).
매핑 등록은 초기화 여부와 무관하게 가능해야 하므로 배너를 위로 올린다.

최상위 `Grid`의 행 구성을 다음으로 바꾼다.

| 행 | 내용 | 표시 조건 |
| --- | --- | --- |
| 0 | Server / Database / Connect | 항상 |
| 1 | 경고 배너 | `HasWarning` |
| 2 | 콘텐츠(액션·목록·Diff) | `IsInitialized` |
| 2 | Setup 오버레이 | `IsInitialized`의 역 |

* 배너는 `DockPanel`로 바꿔 오른쪽에 **"저장소 연결..."** 버튼을, 나머지 영역에 `WarningMessage`를 둔다.
  버튼의 표시 조건은 `IsMapped`의 역이다. 추출 실패 같은 다른 경고가 뜰 때는 배너만 나오고 버튼은 숨는다.
* 콘텐츠 Grid의 내부 행에서 배너 행을 제거한다.
* Setup 오버레이 안의 `WarningMessage` `TextBlock`을 제거한다. 배너가 그 역할을 대신한다.

### 3.3. Diff 하이라이팅 (DBVC.Vsix)

#### 3.3.1. `DiffTextBuilder` — 순수 변환

DiffPlex `SideBySideDiffModel`의 한쪽 `Lines`를 에디터에 넣을 텍스트와 줄별 종류로 변환한다.

```
enum DiffLineKind { Unchanged, Inserted, Deleted, Modified, Padding }

class DiffPane
    string Text                            // 패딩 줄을 빈 줄로 포함한 전체 텍스트
    IReadOnlyList<DiffLineKind> LineKinds  // 1-based 줄 번호에 대응 (인덱스 0 = 1번 줄)

static DiffPane DiffTextBuilder.Build(IEnumerable<DiffPiece>? lines)
```

* DiffPlex `ChangeType` 매핑: `Unchanged`→`Unchanged`, `Inserted`→`Inserted`, `Deleted`→`Deleted`, `Modified`→`Modified`, `Imaginary`→`Padding`
* `DiffPiece.Text`가 `null`인 줄(= `Imaginary`)은 빈 문자열이 된다
* 줄은 `\n`으로 잇고 마지막 줄 뒤에 개행을 넣지 않는다
* 입력이 `null`이거나 비면 `Text`는 빈 문자열, `LineKinds`는 빈 목록이다

WPF·파일 시스템에 의존하지 않는 순수 함수이므로 전량 단위 테스트 대상이다.

**줄 번호에 대한 결정:** 패딩 줄이 삽입되므로 에디터가 표시하는 줄 번호는 원본 파일의 줄 번호와 어긋난다.
좌우 대응 관계를 눈으로 짚을 수 있는 편이 원본 줄 번호 보존보다 낫다고 판단해 이 방식을 택했다.

#### 3.3.2. `DiffLineBackgroundRenderer : IBackgroundRenderer`

AvalonEdit의 `KnownLayer.Background`에 줄 배경을 그린다.

* `SetLineKinds(IReadOnlyList<DiffLineKind>)`로 현재 문서의 줄 종류를 받는다.
  배열을 교체한 뒤 배경 레이어를 무효화해 즉시 다시 그리게 한다
* `Draw`는 `textView.VisualLines`(화면에 보이는 줄)만 순회한다. 문서 전체를 순회하지 않는다
* 줄 번호가 `LineKinds` 범위를 벗어나면 그 줄은 칠하지 않는다. 텍스트와 종류 배열이 어긋난 순간에도 예외를 던지지 않는다
* `Unchanged`는 칠하지 않는다

| 종류 | 색 |
| --- | --- |
| Inserted | `#E6FFED` |
| Deleted | `#FFEEF0` |
| Modified | `#FFF5B1` |
| Padding | `#F0F0F0` |

색은 `Brush` 프로퍼티로 노출해 나중에 VS 테마 리소스로 교체할 수 있게 둔다.

#### 3.3.3. 연결과 스크롤 동기화

`ViewChangesControl.OnSelectionChanged`가 지금까지 UI에서 쓰이지 않던
`DiffService.GetDiffModel`을 호출하도록 바꾼다.

```
model = DiffService.GetDiffModel(server, database, relativePath)
oldPane = DiffTextBuilder.Build(model.OldText.Lines)   → OldTextEditor.Text, 렌더러에 종류 전달
newPane = DiffTextBuilder.Build(model.NewText.Lines)   → NewTextEditor.Text, 렌더러에 종류 전달
```

렌더러 두 개는 생성자에서 각 에디터의 `TextArea.TextView.BackgroundRenderers`에 한 번 등록한다.

**호출 순서는 `Text` 설정 → `SetLineKinds`다.** 반대로 하면 새 텍스트를 이전 줄 종류로 한 번 그린 뒤
다시 그리게 된다. 선택이 해제되어 양쪽을 비울 때는 텍스트와 함께 줄 종류도 빈 목록으로 교체한다.

**스크롤 동기화:** 두 에디터의 `TextArea.TextView.ScrollOffsetChanged`를 상호 연결하고,
재진입 방지 플래그로 무한 루프를 막는다. 수직·수평 오프셋을 모두 맞춘다.
`Unloaded`에서 구독을 해제한다. 기존 `SelectionChanged` 해제와 같은 자리다.

## 4. Error Handling

* **파일 삭제 실패:** 개별 실패를 격리하고 경고 배너로 알린다. 나머지 항목은 계속 처리한다.
* **선택한 폴더가 Git 저장소가 아님:** `MessageBox`로 알리고 매핑을 저장하지 않는다.
* **폴더 선택 취소:** 아무 일도 일어나지 않는다. 오류가 아니다.
* **Diff 생성 실패:** 기존과 같다. 신규 객체는 좌측이, 삭제된 객체는 우측이 비어 자연스럽게 표현된다.

## 5. Testing Strategy

**단위 테스트 대상**

* `WorkingTreeCleaner` — 실제 임시 폴더를 만들어 검증한다.
  `Deleted` + `LastLogId > 0`이면 삭제 / `LastLogId == 0`이면 미삭제 /
  `State`가 `Deleted`가 아니면 미삭제 / 규약에 맞지 않는 경로는 미삭제 /
  저장소 밖을 가리키는 경로는 미삭제 / 파일이 없으면 무동작 / 삭제 실패는 `FailedPaths`로 격리
* `DiffTextBuilder` — 패딩 줄이 빈 줄이 되는지, `ChangeType` 매핑, 줄 종류 배열과 텍스트 줄 수의 일치,
  빈 입력, 한쪽만 내용이 있는 경우
* `ViewChangesViewModel` — `IWorkingTreeCleaner`를 Moq로 대체해 `Refresh`가 정리를 호출하는지,
  실패가 경고에 반영되는지. `IFolderBrowseDialog`와 `IGitManager.IsRepository`를 Moq로 대체해
  취소 / 저장소 아님 / 정상 등록 세 경로

**수동 검증 대상 (SSMS 21 실행 환경)**

`DiffLineBackgroundRenderer`의 렌더링, 스크롤 동기화, XAML 레이아웃 변경.
WPF 런타임이 필요해 CI에서 확인할 수 없다. README가 이미 "CI로 검증되지 않는 것"으로 분류한 범주다.

## 6. 문서 갱신

이번 변경으로 실제와 어긋나게 되는 문서를 함께 고친다.

* `README.md` — Diff 뷰어 설명을 실제 동작에 맞추고, 사용법에 매핑 등록 단계를 추가한다.
  삭제된 객체가 어떻게 처리되는지 덧붙인다.
* `2026-07-31-dbvc-view-changes-design.md` — 경고 배너 위치와 매핑 등록 흐름을 반영한다.
* `2026-07-31-dbvc-core-engine-design.md` 3.1 — 삭제된 객체의 작업 트리 정리 규칙을 추가한다.

범위 밖(P2·P3)으로 분류한 항목은 이 작업에서 문서를 건드리지 않는다.
