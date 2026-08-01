# DBVC Pull·Object History UI 설계 (Feature 6, 7)

## 1. Overview

14개 MVP 기능 중 Feature 6(Git Pull)과 Feature 7(Object History)은 코어가 구현·테스트되어 있으나
UI에 노출되지 않아 사용자가 도달할 수 없다. 이 문서는 두 기능을 View Changes 도구 창에 연결하는 설계를 다룬다.

| 기능 | 코어 상태 | 현재 |
| --- | --- | --- |
| Feature 6: Git Pull | `GitManager.PullChanges` 구현·테스트 완료(충돌 시 Abort 포함) | UI 없음 |
| Feature 7: Object History | `GitManager.GetHistory` 구현·테스트 완료 | UI 없음 |

이 작업으로 Feature 10(Object Explorer 오버레이)을 제외한 13개 기능이 사용 가능해진다.
Feature 10의 보류 사유는 [2026-08-01-dbvc-object-explorer-overlay.md](../plans/2026-08-01-dbvc-object-explorer-overlay.md)에 있다.

## 2. Scope

### In Scope
* View Changes 창 상단 액션 영역의 **Pull** 버튼과 그 안전장치
* 하단 영역을 `[Diff] [History]` 탭으로 나누고 선택된 객체의 커밋 이력 표시
* `IUserNotifier`에 확인 대화상자와 완료 알림 추가
* 버튼이 늘어나 좁은 도킹에서 잘리지 않도록 액션 영역 레이아웃 조정

### Out of Scope
* **이력에서 특정 리비전의 코드 보기.** Feature 7이 요구하는 것은 커밋 로그(SHA·메시지·날짜)이며,
  리비전 내용을 보려면 `GitManager`에 커밋 지정 조회 API를 새로 추가해야 한다. 필요해지면 그때 다룬다.
* **받은 스크립트를 데이터베이스에 적용.** DBVC는 스크립트를 만들 뿐 대상 서버에 실행하지 않는다
  (script-generation 설계 2절과 같은 방침).
* **충돌 해결 UI.** 충돌은 Pull을 중단하고 사용자가 Git 클라이언트에서 해결한다
  (ssms21-plugin-design 5절).
* **`ViewChangesViewModel`의 구조적 분할.** 최근 리뷰가 refresh→cleanup→records 파이프라인을
  Core로 빼낼 것을 권고했으나, 이 기능이 요구하는 변경이 아니므로 별도로 다룬다.
  다만 이력 로직은 처음부터 별도 ViewModel에 둔다(3.2 참고).

## 3. Component Design

### 3.1. Git Pull (Feature 6)

#### 3.1.1. `IUserNotifier` 확장

현재 `ShowError` 하나뿐이다. Pull에는 진행 확인과 완료 알림이 모두 필요하다.

```
IUserNotifier
    void ShowError(string title, string message)
    void ShowInfo(string title, string message)      // 신규
    bool Confirm(string title, string message)       // 신규. 사용자가 계속을 선택하면 true
```

`MessageBoxNotifier`는 각각 `MessageBoxImage.Information`과 `MessageBoxButton.OKCancel`로 구현한다.

`ShowInfo`는 script-generation 설계 4절이 요구하지만 구현되지 않은 "생성 결과 요약 알림"에도
쓸 수 있다. 그 적용은 이번 범위가 아니다.

#### 3.1.2. `PullCommand` (ViewChangesViewModel)

* **활성 조건:** `HasContext && IsMapped`. 초기화 여부는 따지지 않는다 — Git 저장소 작업이지
  데이터베이스 작업이 아니며, 액션 영역 자체가 `IsInitialized` 그리드 안이라 미초기화 상태에서는 보이지 않는다.
  컨텍스트와 `IsMapped`가 바뀔 때 재평가한다(기존 `RaiseActionCanExecuteChanged`에 합류시킨다).
* **동작 순서**
  0. `_configManager.TryGetMapping(server, database)`으로 저장소 경로를 얻는다. `null`이면 아무 일도 하지 않는다.
  1. `GetChangedFiles(mapping.GitPath)`로 작업 트리를 점검한다. 비어 있지 않으면 `Confirm`으로
     무엇이 사라질 수 있는지 알리고 진행 여부를 묻는다. 취소하면 아무 일도 하지 않는다(오류가 아니다).
  2. `PullChanges(server, database)`를 호출한다.
  3. `MergeConflictException` → 예외 메시지를 그대로 오류로 표시한다. `GitManager`가 이미
     한국어 안내 문구를 담고 있다.
  4. 그 외 예외(원격 미설정 시의 `InvalidOperationException` 등) → 메시지를 오류로 표시한다.
  5. `false` 반환(매핑 없음) → 오류로 표시한다. `CanExecute`가 `IsMapped`를 요구하므로 정상 경로에서는 도달하지 않는다.
  6. 성공 → `ShowInfo`로 완료를 알린다.

**미커밋 변경 확인이 필요한 이유.** 충돌이 발생하면 `GitManager`가 `Reset(ResetMode.Hard)`로
병합을 되돌린다. 이때 추적 중인 파일의 미커밋 변경도 함께 사라진다.
DBVC에서는 Refresh가 SMO로 모든 객체를 덮어쓰므로 이 상태가 오히려 일반적이다.
사라진 추출물은 Refresh로 복구되지만, 사전 고지 없이 사라지면 사용자는 원인을 알 수 없다.

#### 3.1.3. Pull 성공 후 자동 Refresh를 하지 않는다

Refresh는 SMO로 현재 데이터베이스를 다시 추출해 작업 트리를 덮어쓴다.
Pull 직후 Refresh를 실행하면 **방금 받은 원격 변경이 즉시 사라진다.**

Pull의 역할은 이력 동기화까지다. 받은 `.sql`을 자기 데이터베이스에 적용할지는 사용자가 판단하며,
DBVC는 그 실행에 관여하지 않는다. 완료 알림에 이 점을 명시한다.

### 3.2. Object History (Feature 7)

#### 3.2.1. `ObjectHistoryViewModel` (신규)

`ViewChangesViewModel`이 소유하고 `History` 속성으로 노출한다.

```
ObjectHistoryViewModel(IGitManager gitManager)
    ObservableCollection<HistoryEntryViewModel> Entries
    bool IsEmpty
    void Load(string? serverName, string? databaseName, string? relativePath)

HistoryEntryViewModel
    string ShortSha     // 앞 7자. 40자 미만이면 원본 그대로
    string Message      // 커밋 메시지의 첫 줄
    string Author
    string Date         // yyyy-MM-dd HH:mm
```

`Load`는 목록을 비우고 다시 채운다. 인자 중 하나라도 비어 있으면 비운 상태로 끝낸다.
`IsEmpty` 변경은 `INotifyPropertyChanged`로 알린다.

**별도 ViewModel로 두는 이유.** `ViewChangesViewModel`은 529줄에 명령 7개이고,
최근 리뷰가 "경계에 있으니 분할을 계획하라"고 지적했다. 이력 로직을 분리하면
이 기능이 기존 ViewModel에 더하는 것은 속성 하나와 갱신 호출 한 줄뿐이다.

**`CommitInfo`를 그대로 바인딩하지 않는 이유.** SHA 축약과 날짜 포맷은 화면 관심사다.
Core 모델에 UI 표현을 넣지 않는다.

#### 3.2.2. 갱신 시점

`SelectedChange` setter에서 `History.Load(ServerName, DatabaseName, value?.RelativePath)`를 호출한다.
선택이 바뀔 때마다 Git을 조회하지만, 사용자의 클릭에 반응하는 것이므로 지연 로드는 두지 않는다.

`SetContext`와 `Refresh`가 `Changes`를 비울 때 `SelectedChange`를 `null`로 설정한다.
지금은 건드리지 않아 이전 선택이 남는데, 목록에 없는 객체가 선택된 상태로 남으면
Diff와 이력이 실재하지 않는 대상을 가리키게 된다.

#### 3.2.3. 조회 실패

`GetHistory`는 예외를 삼키고 빈 목록을 반환한다. 따라서 "이력이 없는 신규 객체"와
"조회 실패"를 구분할 수 없다. 이 계약은 그대로 두고, 목록이 비면 "이력이 없습니다."를 표시한다.
신규 객체는 실제로 이력이 없으므로 대부분의 경우 이 문구가 사실이다.

### 3.3. 레이아웃 (ViewChangesControl.xaml)

#### 3.3.1. 하단을 `TabControl`로

`[Diff] [History]` 두 탭. Diff 탭은 기존 좌우 분할 그리드를 그대로 옮긴다.
**`OldTextEditor`와 `NewTextEditor`의 `x:Name`을 유지해야** 코드비하인드가 컴파일된다.

History 탭은 `Entries`를 바인딩한 `ListView`와, `IsEmpty`일 때만 보이는 안내 `TextBlock`을 겹쳐 둔다.

#### 3.3.2. 액션 영역을 `WrapPanel`로

현재 `StackPanel`에 버튼과 입력이 약 630px를 차지한다. Pull 버튼이 더해지면 700px에 근접해
도구 창을 좁게 도킹했을 때 잘린다. `WrapPanel`로 바꿔 폭이 부족하면 줄바꿈되게 한다.

## 4. Error Handling

* **미커밋 변경이 있는 상태의 Pull:** 확인을 받는다. 취소는 오류가 아니다.
* **병합 충돌:** `GitManager`가 병합을 되돌린 뒤 예외를 던진다. 메시지를 그대로 표시한다.
* **원격 미설정:** 오류로 표시한다.
* **이력 조회 실패:** 빈 목록으로 나타나며 "이력이 없습니다."가 표시된다. (3.2.3)

## 5. Testing Strategy

**단위 테스트 대상**

* `ObjectHistoryViewModel` — `Mock<IGitManager>`로 `GetHistory`를 스텁한다.
  SHA 7자 축약 / 메시지 첫 줄만 취함 / 날짜 포맷 / 빈 결과에서 `IsEmpty` / 인자가 비면 조회하지 않음 /
  다시 `Load`하면 이전 항목이 남지 않음
* `PullCommand` — `Mock<IGitManager>`와 확인 응답을 설정할 수 있는 알림 테스트 더블을 쓴다.
  미커밋 변경이 없으면 확인 없이 진행 / 있으면 확인을 받음 / 취소하면 `PullChanges`를 호출하지 않음 /
  `MergeConflictException`을 오류로 알림 / 성공 시 완료 알림 / **성공해도 SMO 추출을 호출하지 않음**(3.1.3)
* `SelectedChange` 변경이 `History.Load`를 부르는지, 목록을 비울 때 선택이 해제되는지

**수동 검증 대상 (SSMS 21)**

탭 전환 후 Diff 배경색이 다시 그려지는지, `WrapPanel` 줄바꿈이 좁은 도킹에서 의도대로 동작하는지,
확인·완료 대화상자가 SSMS 창 뒤로 숨지 않는지.
`TabControl`은 비활성 탭의 콘텐츠를 시각 트리에서 분리하므로 첫 항목을 특히 확인해야 한다.

## 6. 기존 테스트에 미치는 영향

`IUserNotifier`에 멤버를 추가하면 테스트 더블 `RecordingNotifier`가 컴파일되지 않는다.
`Confirm`의 반환값을 테스트가 설정할 수 있도록 확장한다. 기본값은 `true`(계속)로 두어
기존 테스트의 동작이 바뀌지 않게 한다.
