# DBVC Pull 결과 구분 설계

## 1. 개요 (Overview)

현재 DBVC의 "Pull" 버튼은 원격에 새 변경이 하나도 없어도 `원격 저장소의 변경을 가져왔습니다.` 안내를
띄웁니다. 사용자는 받은 것이 없는데 받았다는 말을 듣고, 이어서 "받은 스크립트"를 찾아 헤매게 됩니다.

이를 개선하여, **원격의 커밋을 실제로 반영했을 때와 이미 최신이었을 때를 구분해** 안내합니다.
아울러 실제로 받아온 경우에는 **받은 스크립트가 놓인 저장소 폴더 경로**를 안내에 함께 싣습니다.

### 1.1. 현재 동작의 원인

두 지점이 겹칩니다.

* `GitManager.PullChanges`(`src/DBVC.Core/GitManager.cs:195-254`)가 결과를 `bool`로 뭉갭니다.
  `LibGit2Sharp`의 `MergeResult.Status`는 `UpToDate` / `FastForward` / `NonFastForward` /
  `Conflicts` 네 값인데, `Conflicts`만 걸러 예외로 바꾸고(245행) 나머지는 전부 `true`로 떨어집니다
  (253행). 이 메서드가 `false`를 내는 경우는 "매핑된 저장소 없음" 하나뿐입니다(198행).
* `ViewChangesViewModel.Pull()`(`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:522-587`)은
  `true`를 받으면 무조건 같은 안내를 띄웁니다. 받은 커밋 수를 물어보는 코드가 없습니다.

이는 이미 Push 쪽에서 해결된 문제입니다. `PushChanges`는 `PushResult { NoMapping, NothingToPush,
Pushed }`를 돌려주고 ViewModel이 `switch`로 문구를 가릅니다. Pull만 그 대칭 처리가 빠져 있습니다.

`2026-08-03-dbvc-pull-hardening-and-doc-alignment-design.md`는 Pull의 오류 경로(인증·충돌·원격
진단)만 다루면서 `MergeStatus.Conflicts` 분기는 "그대로 둔다"고 적었습니다. `UpToDate`를 성공과
구분하는 문제는 그때 검토된 적이 없습니다 — 의도된 설계가 아니라 빈칸입니다.

## 2. 요구사항 (Requirements)

* 원격에 새 커밋이 없어 병합할 것이 없었으면, "가져왔다"가 아니라 **이미 최신**임을 알립니다.
* 원격의 커밋을 실제로 반영했으면(fast-forward든 병합 커밋이든) 기존과 같이 알리되, **받은
  스크립트가 놓인 저장소 폴더 경로**를 함께 싣습니다.
* 매핑된 저장소가 없는 경우의 오류 안내는 지금과 같습니다.
* 예외로 끝나는 경로(인증 실패, 원격 진단, 병합 충돌, 작업 트리 충돌)의 동작과 문구는 바뀌지
  않습니다.
* 이미 최신이었을 때는 이력 탭을 다시 읽지 않습니다.

## 3. 아키텍처 및 구현 설계 (Architecture & Implementation)

### 3.1. `PullResult` 도입

`src/DBVC.Core/Models/PullResult.cs`를 새로 만듭니다. `PushResult.cs` 옆에, 같은 모양으로 둡니다 —
두 동작의 결과를 한 눈에 대응시켜 읽게 하려는 것입니다.

```csharp
public enum PullResult
{
    /// <summary>이 (서버, 데이터베이스)에 매핑된 저장소가 없다.</summary>
    NoMapping,

    /// <summary>원격에 새 커밋이 없었다. 정상 상태이며 오류가 아니다.</summary>
    AlreadyUpToDate,

    /// <summary>원격의 커밋을 로컬에 반영했다.</summary>
    Pulled
}
```

### 3.2. `GitManager.PullChanges` 반환 타입 변경

`bool` → `PullResult`. 바뀌는 곳은 두 줄뿐입니다.

* 198행 `if (repoPath == null) return false;` → `return PullResult.NoMapping;`
* 253행 `return true;` →
  ```csharp
  // UpToDate는 "받을 것이 없었다"이지 실패가 아니다. FastForward와 구분하지 않으면
  // 화면이 받은 것이 없는데 받았다고 말한다.
  return result.Status == MergeStatus.UpToDate
      ? PullResult.AlreadyUpToDate
      : PullResult.Pulled;
  ```

`MergeStatus.NonFastForward`(병합 커밋이 만들어진 경우)는 `Pulled`입니다. `MergeStatus.Conflicts`
분기(245-251행)와 `try`/`catch` 다섯 개는 손대지 않습니다 — 그 순서에 정확성이 걸려 있다는 주석이
이미 붙어 있습니다.

`src/DBVC.Core/Abstractions.cs:61`의 `IGitManager.PullChanges` 시그니처도 같이 바꿉니다.

### 3.3. `ViewChangesViewModel.Pull` 분기

`PushChanges`를 다루는 `Push()`의 `switch`와 같은 모양으로 바꿉니다.

```csharp
PullResult result;
try
{
    result = _gitManager.PullChanges(ServerName!, DatabaseName!);
}
catch (...) { /* 기존 catch 네 개 그대로 */ }

switch (result)
{
    case PullResult.NoMapping:
        _notifier.ShowError("DBVC Pull 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
        return;

    case PullResult.AlreadyUpToDate:
        _notifier.ShowInfo("DBVC Pull", "원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.");
        return;

    case PullResult.Pulled:
        _notifier.ShowInfo(
            "DBVC Pull",
            "원격 저장소의 변경을 가져왔습니다." + Environment.NewLine +
            "받은 스크립트는 아래 폴더에 있습니다:" + Environment.NewLine + Environment.NewLine +
            mapping.GitPath + Environment.NewLine + Environment.NewLine +
            "확인한 뒤 필요하면 데이터베이스에 적용하세요.");
        break;
}

History.Load(ServerName, DatabaseName, SelectedChange?.RelativePath);
SelectionChanged?.Invoke(this, EventArgs.Empty);
```

두 가지가 이 모양의 근거입니다.

* **경로는 이미 손에 있다.** `Pull()`은 서두(527행)에서 `TryGetMapping`으로 `mapping`을 얻어
  미커밋 변경을 검사합니다. 안내에 경로를 싣기 위해 새 API를 더할 필요가 없습니다.
* **`History.Load`는 `Pulled`일 때만 부른다.** 그 호출에 달린 기존 주석은 "Pull의 목적(새 커밋
  반영)을 이루려면 방금 받은 커밋 로그와 Diff를 화면에 즉시 보여줘야 한다"를 근거로 삼습니다.
  받은 것이 없으면 그 근거가 사라지고, 화면만 다시 그려져 "뭔가 됐나?"라는 같은 오해를 남깁니다.

`Pull` 직후 `Refresh`를 부르지 않는 기존 규칙(578행 주석)은 그대로입니다.

## 4. 사용자에게 보이는 변화

| 상황 | 지금 | 변경 후 |
|---|---|---|
| 원격에 새 커밋 있음 | `원격 저장소의 변경을 가져왔습니다.` + 적용 안내 | 같은 문구 + **저장소 폴더 경로** |
| 원격에 새 커밋 없음 | 위와 **동일한** 문구 | `원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.` |
| 매핑 없음 | `매핑된 Git 저장소를 찾을 수 없습니다.` | 동일 |
| 충돌·인증·원격 오류 | 각 예외의 한국어 안내 | 동일 |

## 5. 테스트 계획 (Testing)

**`tests/DBVC.Core.Tests/GitManagerTests.cs`**

* 새 테스트 `PullChanges_ReturnsAlreadyUpToDate_WhenTheRemoteHasNoNewCommits` — 원격을 만들고
  아무 커밋도 더하지 않은 채 Pull해 `PullResult.AlreadyUpToDate`를 확인합니다. 이 테스트가 이번
  변경에서 유일하게 새로운 동작을 지키므로 **먼저 실패시킨 뒤** 구현합니다.
* 기존 두 테스트는 단언만 바꿉니다. 502행 `PullChanges_FastForwards_WhenRemoteHasNewCommits`의
  `Is.True` → `Is.EqualTo(PullResult.Pulled)`, 530행
  `PullChanges_ReturnsFalse_WhenDatabaseIsNotMapped`의 `Is.False` →
  `Is.EqualTo(PullResult.NoMapping)`(이름도 `ReturnsNoMapping`으로 고칩니다).
* 예외를 단언하는 나머지 Pull 테스트 여덟 개는 반환값을 보지 않으므로 그대로 통과해야 합니다.

**`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`**

* Moq 설정을 옮깁니다. `Returns(true)` 6곳(851·865·978·993·1019·1037행) →
  `Returns(PullResult.Pulled)`, `Returns(false)` 1곳(879행) → `Returns(PullResult.NoMapping)`.
  `.Throws(...)`로 끝나는 네 곳(907·924·940·961행)은 반환값을 쓰지 않으므로 그대로입니다.
* 이름이 반환값을 말하는 테스트를 고칩니다. 876행
  `PullCommand_ReportsAMissingMapping_WhenPullChangesReturnsFalse` →
  `..._WhenPullChangesReturnsNoMapping`.
* 991행 `PullCommand_NotifiesOnSuccess`의 단언에 **저장소 경로가 안내에 실렸는지**를 더합니다.
* 새 테스트 두 개:
  * `PullCommand_ReportsAlreadyUpToDate_WhenNothingWasPulled` — 안내 문구가 "가져왔습니다"가
    아님을 확인합니다.
  * `PullCommand_DoesNotReloadHistory_WhenNothingWasPulled` — `GetHistory`가 불리지 않음을
    확인합니다.
* 1003행 `PullCommand_ReloadsHistoryAndRendersDiff_AfterASuccessfulPull`은 `Pulled` 경로에서
  계속 통과해야 합니다 — 새 테스트와 짝을 이뤄 "받았을 때만 다시 읽는다"를 양쪽에서 지킵니다.

**수동 확인 (CI가 검증하지 않는 영역)**

SSMS 21에서 Pull을 두 번 눌러, 첫 번째는 경로가 실린 안내가, 두 번째는 "이미 최신입니다"가 뜨는지
봅니다.

## 6. 문서 (Documentation)

* `README.md:60` — Pull 설명에 두 문구를 구분해 적고, 받은 파일이 놓이는 위치를 밝힙니다.
* `docs/setup-checklist.md:297` — **지금 이 항목이 틀리게 됩니다.** 현재는
  "`원격 저장소의 변경을 가져왔습니다.` 알림이 뜨면 SSH 경로가 끝까지 동작하는 것이다"라고
  적혀 있는데, 갓 설정한 저장소는 대개 up-to-date라 변경 후에는 다른 문구가 뜹니다. **두 문구 중
  어느 쪽이든 뜨면 SSH 경로가 동작한 것**으로 고칩니다.
* `src/DBVC.Vsix/source.extension.vsixmanifest` — 버전 `0.2.0` → `0.2.1`.

## 7. 범위 밖 (Out of Scope)

미커밋 변경이 있으면 **받을 것이 없어도** 사전 확인 창이 먼저 뜨는 문제
(`ViewChangesViewModel.cs:531-546`)는 그대로 둡니다. 없앨 수 있는지 판단하려면 fetch를 먼저 하고
병합만 미루는 구조가 필요해서 이번 변경보다 큽니다.

받은 스크립트를 DBVC 안에서 열어 보거나 폴더를 여는 기능(툴바 버튼, `Process.Start`)도 이번에는
넣지 않습니다. 경로를 안내에 싣는 것으로 원래의 혼란은 해소되고, 그 이상은 `IUserNotifier` 확장을
부릅니다.
