# 설계 문서: 이력 뷰 정합성·Git 의미론·스레드 정리 (History View Consolidation)

2026-08-31 이후 세 번에 걸쳐 들어온 이력 기능(개체별 이력 보기, 커밋 Diff, 전역 이력의 변경
파일 목록)을 검증한 결과 드러난 결함들을 한 번에 정리한다. 세 기능이 모두 이력 뷰 한 곳에서
만나므로 따로 고치면 서로의 가정을 다시 깨뜨린다.

관련 선행 설계:

- `2026-08-31-dbvc-view-history-design.md`
- `2026-09-01-history-diff-view-design.md`
- `2026-09-02-dbvc-global-history-diff-design.md`

## 1. 고치려는 것

### 1.1 단일 객체 모드가 두 개의 플래그로 갈라져 있다 (치명)

`IsSingleObjectMode`가 서로 다른 의미로 두 곳에 있다.

| 위치 | 의미 | 켜지는 시점 |
|---|---|---|
| `ViewChangesViewModel.cs:690` | 화면 레이아웃 전환 | 개체 탐색기 우클릭(`ShowHistoryFor`)일 때만 |
| `ObjectHistoryViewModel.cs:43` | 변경 파일 목록을 채울지 여부 | `RelativePath`가 있으면 언제나 |

`ViewChangesControl.xaml:138`의 레이아웃 분기는 앞쪽을, `ObjectHistoryViewModel.cs:69`의 파일
목록 채우기는 뒤쪽을 본다. 그런데 변경 목록에서 행을 하나 고르면(`ViewChangesViewModel.cs:673`)
뒤쪽만 켜진다. 그래서 **가장 흔한 진입 경로에서 레이아웃은 3단인데 가운데 파일 목록은 영구히
빈 채로 화면 1/3을 차지한다.** `ExitSingleObjectModeCommand`(`:705`)도 앞쪽 플래그만 되돌리고
`History.RelativePath`는 그대로 둬서, "돌아가기"를 눌러도 이력은 그 객체에 묶여 있다.

### 1.2 이름 변경 커밋이 잘못 보인다

`GitManager.cs:865`의 `Diff.Compare<TreeChanges>`는 LibGit2Sharp 기본값으로 rename 검출이 켜져
있다. DBVC에서 객체 이름 변경은 "옛 `.sql` 삭제 + 새 `.sql` 추가(내용 거의 동일)"라 이 검출에
정확히 걸린다. 결과는 상태가 "수정"으로 뭉뚱그려지고, `c.Path`(새 경로)가 부모 트리에 없으므로
Diff는 파일 전체가 추가된 것처럼 나온다.

### 1.3 병합 커밋임이 드러나지 않는다

`GetChangedFilesAtCommit`도 `GetFileContentAtCommitParent`도 첫 부모만 본다. DBVC는 Pull에서
병합 커밋을 만들므로 이력에 반드시 섞이는데, 사용자에게는 "이 커밋이 이 파일들을 바꿨다"로
읽힌다. 실제로는 상대 브랜치에서 들어온 전부다.

### 1.4 커밋을 고를 때마다 UI 스레드에서 저장소를 최대 3번 연다

`ObjectHistoryViewModel.SelectedEntry` setter(`:57~85`)가 동기적으로 `GetChangedFilesAtCommit`
1회와 `UpdateDiffModel` 안의 `GetFileContentAtCommitParent`·`GetFileContentAtCommit` 2회를
부르고, 각각 `new Repository()`를 연다. 목록에서 방향키로 훑으면 행마다 이 비용이 UI 스레드에서
난다. CLAUDE.md의 "무거운 작업은 `IBackgroundScheduler`로 UI 스레드 밖에서 돈다" 규약과 충돌하며,
`ObjectHistoryViewModel`은 스케줄러를 아예 받지 않는다.

`ViewChangesViewModel.cs:930`의 기존 주석은 History가 Git만 읽으므로 괜찮다고 적었는데, 그 판단은
**일회성 Load** 기준이었다. 선택마다 반복되는 지금은 근거가 성립하지 않는다. 최초 전체 추출
커밋을 고르면 수천 개 파일이 전부 `Added`로 나와 `ObservableCollection`에 하나씩 들어간다.

### 1.5 객체 유형 컬럼이 변경 목록과 다르다

`ViewChangesControl.xaml:268`이 원시값 `ObjectType`("StoredProcedure")을 바인딩한다. 바로 위
변경 목록(`:198`)은 `ObjectTypeText`("SP")를 쓴다. 같은 창의 두 목록이 같은 컬럼명으로 다른 값을
보여준다. `HistoryChangedFileViewModel`은 `ObjectTypeText`를 정의해 두고도 쓰지 않는다.

### 1.6 개체 탐색기 진입점의 검증이 얕다

`ViewChangesViewModel.ShowHistoryFor`(`:711`)는 DB 이름만 비교한다. 서버 A와 B에 같은 이름의 DB가
있으면 다른 서버 객체의 이력을 조용히 현재 저장소에서 조회한다. 또 미연결 상태에서 우클릭하면
`DatabaseName`이 null이라 `"선택한 객체는 현재 활성화된 DB()에 속하지 않습니다."`라는 빈 괄호
메시지가 뜬다.

### 1.7 문서 부채

README 19·110행은 여전히 "이력(날짜·작성자·메시지·SHA)"만 설명하고 우클릭 진입점·Diff 뷰·변경
파일 목록·더블클릭 비교 창이 어디에도 없다. 임시 파일(`ViewChangesControl.xaml.cs:261`)은 더블클릭
때마다 `%TEMP%`에 쌓이고 지워지지 않는다. 선행 설계 문서 셋에도 실제 구현과 어긋난 서술이 남아 있다.

## 2. 다루지 않는 것

- 병합 커밋의 모든 부모를 함께 보는 combined diff. 복잡도 대비 이득이 없다.
- 개체 탐색기에서 다른 DB를 우클릭했을 때의 자동 연결 전환. 진행 중이던 변경 목록과 커밋
  메시지가 조용히 날아갈 수 있으므로 사용자 손에 남긴다.
- Object Explorer 아이콘 오버레이(Feature 10). 여전히 보류다.

## 3. 이력 뷰 모드 통합

모드의 유일한 진실을 `ObjectHistoryViewModel.IsSingleObjectMode`(= `RelativePath` 유무) 하나로
만든다. `ViewChangesViewModel.IsSingleObjectMode`와 `ExitSingleObjectModeCommand`는 없앤다.

### 3.1 화면

`ViewChangesControl.xaml:292~340`의 전용 전체화면 Grid를 통째로 지운다. 개체 탐색기로 들어와도
변경 목록은 그대로 보이고 아래 "이력" 탭이 해당 객체로 필터링된 채 열린다. 맥락을 잃지 않으며,
변경 목록 선택 경로와 동작이 완전히 같아진다.

이력 탭 안에서 **변경 파일 목록 행과 그 GridSplitter를 `History.IsSingleObjectMode`에 따라
접는다** — `Visibility="Collapsed"`만으로는 `RowDefinition`이 자리를 지키므로 높이도 함께 0으로
내린다. 필터 상태면 2단(이력 + Diff), 전체 이력이면 3단(이력 + 파일 목록 + Diff)이다.

이력 탭 상단 배너에 `ScopeLabel`과 **"전체 이력으로"** 버튼을 둔다. 필터 중일 때만 보이며 동작은
`History.Load(ServerName, DatabaseName, null)`이다.

`ViewChangesControl.xaml:268`의 바인딩을 `ObjectTypeText`로 바꾼다.

### 3.2 코드비하인드

렌더러가 4개에서 2개로 준다. `_singleHistoryOldRenderer`/`_singleHistoryNewRenderer`(`:27~28`,
`:61~64`), `OnSingleHistoryOldScrollOffsetChanged`/`OnSingleHistoryNewScrollOffsetChanged`
(`:70~71`, `:124~127`, `:147~148`, `:340`, `:342`), 중복 `ApplyDiffPanes`/`SetPane`
호출(`:231`, `:237~238`)이 함께 사라진다.

### 3.3 ShowHistoryFor의 호출 순서

`SelectedChange`를 **먼저** 정리한 뒤 `History.Load`를 부른다. 반대로 하면 `SelectedChange`
setter(`:673`)가 방금 건 필터를 덮어쓴다.

- 변경 목록에 같은 `RelativePath` 행이 있으면 그 행을 선택한다. 두 상태가 일치해 사용자가 위
  목록에서도 그 객체를 볼 수 있다.
- 없으면(이미 커밋되어 변경 목록에 없는 객체가 이 기능의 주 대상이다) 뒷단 필드만 조용히 비우고
  `PropertyChanged`를 올린다. setter를 타면 `History.Load(..., null)`이 돌아 필터가 풀린다.

이력 탭을 자동 선택하기 위해 `ViewChangesViewModel`에 `SelectedTabIndex` 바인딩 속성을 더하고
`TabControl.SelectedIndex`에 묶는다.

## 4. Git 의미론

### 4.1 이름 변경 — 검출을 끈다

`GetCommitDetail`의 트리 비교에 `new CompareOptions { Similarity = SimilarityOptions.None }`을
넘긴다. 이름 변경은 "삭제 dbo.OldName" + "추가 dbo.NewName" 두 행이 되고 각 행의 Diff가 실제와
맞는다. Core 모델은 그대로다.

`Renamed` 상태를 새로 만들지 않는 이유: SQL에서 객체 이름 변경은 결국 DROP + CREATE로 배포되고
저장소에서도 파일 삭제 + 생성이다. 목록이 그 사실을 그대로 보여주는 편이 사용자의 멘탈 모델과
일치한다. 상태 하나를 더하면 Core 모델·뷰모델·UI가 모두 바뀌는데 얻는 것은 표시 한 줄뿐이다.

### 4.2 병합 커밋 — 첫 부모 기준을 유지하되 드러낸다

비교 기준은 첫 부모 그대로다(`git log --stat`과 같다). 대신 그 사실을 화면에 적는다.

- `CommitInfo`에 `ParentCount`를 더한다.
- `HistoryEntryViewModel.IsMerge`(= `ParentCount > 1`)로 노출한다.
- 이력 목록 행에 병합 표시를 넣고, 파일 목록 위에 "병합 커밋입니다 — 첫 부모 기준으로
  비교합니다" 안내를 띄운다.

## 5. 성능 · 스레드

### 5.1 Core: 조회를 하나로 합친다

```csharp
CommitDetail GetCommitDetail(string serverName, string databaseName,
                             string commitSha, string? relativeFilePath);
```

- `relativeFilePath == null` → `ChangedFiles`만 채운다. (커밋 선택)
- `relativeFilePath != null` → `OldText`/`NewText`만 채운다. (파일 선택, 또는 필터 모드)
- 어느 쪽이든 `Repository`는 한 번만 연다.

`CommitDetail`은 `HistoryChangedFile`과 같은 파일에 둔다.

```csharp
public sealed class CommitDetail
{
    public bool IsTruncated { get; set; }          // 상한을 넘어 잘렸다
    public int TotalChangedFileCount { get; set; } // 자르기 전 전체 개수
    public IReadOnlyList<HistoryChangedFile> ChangedFiles { get; set; }
    public string? OldText { get; set; }
    public string? NewText { get; set; }
}
```

병합 여부는 여기에 두지 않는다. `CommitInfo.ParentCount`(§4.2)가 `Load` 시점에 이미 실어 오므로
뷰모델은 선택된 `HistoryEntryViewModel`만 보면 안다. 같은 사실을 두 경로로 얻으면 나중에 갈라진다.

기존 `GetFileContentAtCommit` / `GetFileContentAtCommitParent` / `GetChangedFilesAtCommit`
(`GitManager.cs:803`, `:828`, `:855`) 셋은 이 API로 **대체해 제거한다.** 사용처는
`ObjectHistoryViewModel` 하나뿐이다. Core 테스트가 그만큼 다시 쓰이지만, 같은 사실을 두 경로로
얻는 구조를 남기면 나중에 갈라진다.

`OldText`/`NewText`의 `null` 의미는 지금과 같다 — 저장소를 못 찾았거나 그 트리에 파일이 없다.
호출부는 빈 문자열로 바꿔 전체 추가/삭제로 렌더링한다.

### 5.2 뷰모델: 백그라운드 + stale 가드

`ObjectHistoryViewModel`이 생성자로 `IBackgroundScheduler`를 받는다. 기본값은
`InlineBackgroundScheduler`라 단위 테스트는 지금처럼 동기로 돈다. `ViewChangesViewModel.cs:98`
에서 이미 들고 있는 `_scheduler`를 넘긴다 — `DeploymentViewModel`(`:95`)과 같은 배선이다.

방향키로 훑으면 요청이 겹치므로 **stale 가드**를 둔다. 요청마다 증가하는 토큰을 발급하고,
`onSucceeded`에서 토큰이 최신일 때만 `ChangedFiles`·`SelectedDiffModel`에 반영한다. 없으면 늦게
끝난 앞선 요청이 방금 고른 커밋의 결과를 덮어쓴다.

`work` 안에서는 `ObservableCollection`을 건드리지 않는다. Core가 만든 목록을 그대로 돌려주고
`onSucceeded`에서만 컬렉션에 옮긴다.

### 5.3 대용량 커밋

기준선이 없어 처음 도는 전체 추출은 수천 개 파일을 한 커밋에 담는다. `GetCommitDetail`이
**500개까지만** 채우고 `IsTruncated`와 `TotalChangedFileCount`를 세워 돌려준다. 화면은 목록 위에
"전체 N개 중 500개만 표시합니다"를 띄운다. 상한 값은 `GitManager`의 상수 한 곳에 둔다.

## 6. 개체 탐색기 진입점

`ShowHistoryCommand.Execute`가 URN에서 서버를 파싱하는 대신 **`_source.TryGetCurrent()`로 선택
노드의 `ServerName`/`DatabaseName`을 읽어** 활성 컨텍스트와 비교한다. `Connect`가 쓰는 것과 같은
출처라 표기가 어긋날 여지가 없다 — URN의 SMO 서버명과 연결 객체의
`ServerName`(`ObjectExplorerConnectionSource.cs:219`)은 형식이 다를 수 있어, URN을 파싱해 직접
비교하면 기능 자체가 막힌다.

분기는 셋이다. 정상일 때만 도구 창을 띄운다.

| 상황 | 처리 |
|---|---|
| DBVC가 아직 연결되지 않음 | "DBVC가 아직 연결되지 않았습니다. DBVC 창에서 **연결**을 눌러 이 데이터베이스를 대상으로 지정한 뒤 다시 시도하세요." |
| 서버 또는 DB 불일치 | "선택한 객체는 {노드서버}.{노드DB}에 있습니다. DBVC는 지금 {활성서버}.{활성DB}에 연결되어 있습니다." |
| 일치 | 도구 창 표시 → 이력 탭 선택 → `ShowHistoryFor` |

비교는 `StringComparison.OrdinalIgnoreCase`다. SQL Server 인스턴스 이름과 DB 이름 모두 대소문자를
구분하지 않는다.

## 7. 정리 대상 (검증에서 함께 나온 것)

- **임시 파일.** `ViewChangesControl.xaml.cs:261`이 더블클릭마다 `%TEMP%`에 `DBVC_*.sql` 둘을
  만들고 지우지 않는다. 도구 창이 닫힐 때 이번 세션이 만든 경로를 지운다. 비교 창이 아직 열려
  있을 수 있으므로 삭제 실패는 삼킨다.
- **더블클릭이 재구성 텍스트를 쓴다.** `:255~259`가 `SideBySideDiffModel`에서 Imaginary 줄을 걸러
  `Environment.NewLine`으로 다시 잇는다. 원래 줄 끝과 마지막 개행이 보존되지 않아 SSMS 내장 Diff가
  내장 뷰와 다르게 보일 수 있다. `CommitDetail.OldText`/`NewText`를 그대로 쓴다.
- **빈 Diff 영역.** Diff 행이 `Height="1*"` 고정이라 파일 미선택 상태에서도 1/3이 빈 채 남는다.
  `IsDiffVisible`이 거짓이면 행 높이를 0으로 내린다.

## 8. 테스트

**Core (`GitManagerTests`)**

- 이름을 바꾼 커밋이 삭제 1행 + 추가 1행으로 나온다 (rename 검출이 꺼져 있다).
- 병합 커밋의 `CommitInfo.ParentCount`가 2 이상이고, 파일 목록은 첫 부모 기준이다.
- `GetCommitDetail`이 경로 유무에 따라 목록만 / 본문만 채운다.
- 최초 커밋은 빈 트리와 비교해 전부 `Added`이고 `OldText`는 빈 문자열이다.
- 상한을 넘는 커밋에서 `IsTruncated`와 `TotalChangedFileCount`가 맞다.
- 없는 커밋·매핑 없는 DB는 빈 결과다.

**뷰모델 (`ObjectHistoryViewModelTests`)**

- 필터 모드에서는 `ChangedFiles`를 채우지 않고, 전체 이력 모드에서는 채운다.
- 늦게 도착한 앞선 요청의 결과가 반영되지 않는다 (stale 가드).
- 커밋을 바꾸면 `SelectedChangedFile`과 `SelectedDiffModel`이 초기화된다.
- 스케줄러를 거쳐 호출된다 (모의 스케줄러가 `Run`을 받는다).

**뷰모델 (`ViewChangesViewModelTests`)**

- `ShowHistoryFor`가 변경 목록에 있는 객체면 그 행을 선택한다.
- 변경 목록에 없는 객체여도 필터가 유지된다 (`SelectedChange` setter가 덮어쓰지 않는다).
- `ShowHistoryFor`가 이력 탭을 선택한다.
- "전체 이력으로"가 필터를 푼다.

**커맨드 (`ShowHistoryCommandTests`)**

- `ISsmsConnectionSource` 모의로 미연결 / 불일치 / 일치 세 갈래를 검증한다.

## 9. 문서

- `README.md` 19·110행을 고쳐 개체 탐색기 우클릭 진입점, 커밋 Diff 뷰, 변경 파일 목록,
  더블클릭 비교 창을 서술한다.
- `docs/setup-checklist.md`를 함께 맞춘다.
- `src/DBVC.Vsix/source.extension.vsixmanifest`를 0.5.10 → 0.5.11로 올린다.
- `2026-08-31-dbvc-view-history-design.md`에 **VSCT가 아니라 개체 탐색기 TreeView를 리플렉션으로
  후킹한 이유**와 2초 폴링 재시도 구조를 기록한다. 현재 설계서 §2와 계획 Task 4는 VSCT 등록을
  지시하지만 실제 구현은 `ShowHistoryCommand.cs`의 `ContextMenuStrip.Opening` 후킹이고, 그 경위가
  어디에도 없다.
- `2026-09-01-history-diff-view.md`의 존재하지 않는 API `ObjectPathConvention.GetRepositoryPath`
  를 정정한다. 저장소 루트가 곧 매핑 경로이므로 `NormalizePath(relativeFilePath)`면 된다.
- `2026-09-02-dbvc-global-history-diff-plan.md`의 Tech Stack을 정정한다 (xUnit/Moq/C# 10 →
  NUnit/Moq/C# 8.0).

## 10. 영향 범위

| 파일 | 성격 |
|---|---|
| `src/DBVC.Core/Abstractions.cs` | 메서드 3개 제거, `GetCommitDetail` 추가 |
| `src/DBVC.Core/GitManager.cs` | 위 구현, rename 검출 끄기, 상한 |
| `src/DBVC.Core/HistoryChangedFile.cs` | `CommitDetail` 추가 |
| `src/DBVC.Core/Models/CommitInfo.cs` | `ParentCount` 추가 |
| `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs` | 스케줄러 주입, stale 가드, `IsMerge` |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 플래그 제거, `SelectedTabIndex`, 호출 순서 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 전체화면 블록 제거, 행 접기, 바인딩 수정 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` | 렌더러 4→2, 임시 파일 정리, 원본 텍스트 사용 |
| `src/DBVC.Vsix/Commands/ShowHistoryCommand.cs` | `TryGetCurrent` 기반 검증과 안내 |
| `README.md`, `docs/setup-checklist.md`, 매니페스트, 선행 설계서 3종 | 문서 |

## 11. 검증 한계

CI는 WPF 렌더링, VS 패키지 로딩, SSMS 통합을 검증하지 않는다. 이 작업은 XAML 레이아웃과 개체
탐색기 컨텍스트 메뉴를 정면으로 건드리므로, **SSMS 21에서 직접 눌러 보기 전에는 완료로 보지
않는다.** 최소한 다음을 손으로 확인한다: 변경 목록 선택 시 파일 목록이 접히는지, 개체 탐색기
우클릭이 이력 탭을 여는지, 미연결·불일치 안내가 뜨는지, 이름을 바꾼 객체가 두 행으로 나오는지.
