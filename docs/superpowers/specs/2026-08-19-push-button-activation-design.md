# DBVC Push 버튼 활성화 조건 개선 설계

## 1. 개요 (Overview)
현재 DBVC의 "Push" 버튼은 Git 저장소가 매핑되어 있기만 하면(커밋할 내용이 없어도) 항상 활성화되어 있습니다.
이를 개선하여, **로컬 저장소에 원격 저장소로 Push 할 커밋이 존재할 때만** Push 버튼이 활성화되도록 합니다.

## 2. 요구사항 (Requirements)
* Git 저장소가 데이터베이스와 매핑되어 있어야 합니다.
* 원격(Remote) 저장소가 설정되어 있어야 합니다.
* 현재 작업 중인 로컬 브랜치가 원격 브랜치를 추적(Tracking) 중이어야 합니다.
* 로컬 브랜치에 원격 브랜치보다 앞선 커밋(`AheadBy > 0`)이 1개 이상 존재해야 합니다.
* 위의 조건을 모두 만족하는 경우에만 Push 버튼이 활성화(Enabled) 됩니다.

## 3. 아키텍처 및 구현 설계 (Architecture & Implementation)

### 3.1. `IGitManager` 확장
* `DBVC.Core.IGitManager` 인터페이스에 상태 조회를 위한 새 메서드를 추가합니다.
  ```csharp
  bool HasCommitsToPush(string serverName, string databaseName);
  ```
* `GitManager.cs`에서 `LibGit2Sharp`을 사용해 로직을 구현합니다.
  * 저장소 경로를 확인하여 `Repository` 인스턴스를 생성합니다.
  * `repo.Network.Remotes.Any()`와 `repo.Head.IsTracking`을 검사하여 추적 중인 원격 브랜치 유무를 확인합니다.
  * `repo.Head.TrackingDetails.AheadBy > 0` 여부를 반환합니다.
  * 내부에서 발생하는 예외는 모두 무시하고 `false`를 반환합니다 (상태 확인 시 예외로 인해 UI가 터지는 것을 방지).

### 3.2. `ViewChangesViewModel` 바인딩 변경
* `CanPush()` 메서드의 반환 조건을 변경합니다.
  ```csharp
  private bool CanPush() => HasContext && IsMapped && _gitManager.HasCommitsToPush(ServerName!, DatabaseName!);
  ```
* DBVC의 기존 구조상, 커밋 성공(`Commit`), 새로고침(`Refresh`), 저장소 연결(`ConnectRepository`) 등 상태 변화가 일어나는 시점에 `RaiseActionCanExecuteChanged()`가 호출되어 전체 커맨드의 `CanExecute`를 갱신합니다. 이 메커니즘을 그대로 활용하므로, 상태 구독을 위한 추가 이벤트 연결은 필요하지 않습니다.

## 4. 테스트 계획 (Testing)
* `GitManager`에 추가된 `HasCommitsToPush` 메서드가 원격이 없는 경우, 추적 브랜치가 없는 경우, 커밋이 앞서 있는 경우 등 다양한 상태에서 기대하는 `bool` 값을 반환하는지 테스트 코드로 검증합니다.
* SSMS (또는 VSIX 실험적 인스턴스) 상에서, Commit 수행 직후 Push 버튼이 활성화되고, Push 성공 직후 Push 버튼이 비활성화되는지 UI 흐름을 확인합니다.
