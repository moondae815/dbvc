# Object Explorer 상태 오버레이 (Feature 10) — 착수 보류

> **상태: 보류 유지. 단 사유가 바뀌었다.**
> 2026-09-09 실기 실험으로 **기술적으로는 가능하다**는 것이 확인되었다. 더 이상 "방법이 없어서"
> 미루는 것이 아니라, **비용과 취약성에 비해 얻는 것이 적어서** 미룬다. 이 문서는 그 실험의
> 기록이자, 나중에 착수할 사람이 같은 조사를 되풀이하지 않게 하는 근거다.

**목표(원안):** SSMS Object Explorer 트리의 노드(테이블, 프로시저 등)에
변경 상태 아이콘(M / A / D / C)을 오버레이로 표시한다. (ssms21-plugin-design 4.1-3)

## 1. 공개 확장점은 없다 (2026-08-01 판단 유지)

- 노드 타입과 아이콘 핸들러 정의(`sqlexplorerhier.xml`)는 `ObjectExplorer.dll`의 **임베디드
  리소스**다. 디스크 파일이 아니므로 제3자가 노드 타입을 더하거나 `IIconHandler`를 끼워 넣을
  자리가 없다.
- `IObjectExplorerService`의 공개 멤버는 `NewConnection`, `ConnectToServer`, `TryConnectToServer`,
  `DisconnectServer`, `DisconnectSelectedServer`, `GetSelectedNodes`, `FindNode`, `SynchronizeTree`
  뿐이다. 아이콘에 관한 것은 하나도 없다.
- VS 문서에 SSMS 개체 탐색기 아이콘에 대한 항목은 없다. 검색하면 VS **개체 브라우저**(Object
  Manager)의 `IVsSimpleObjectList2.GetDisplayData`가 나오는데, 그것은 다른 트리다.
- `Microsoft.SqlServer.Management.UI.VSIntegration` 계열 어셈블리는 여전히 NuGet에 없다.
  다만 이 저장소는 애초에 컴파일 타임 참조를 쓰지 않으므로(리플렉션) 이것은 더 이상 제약이 아니다.

## 2. 그런데 트리가 WinForms다 — 이것이 2026-08-01에 놓친 사실

```
ObjectExplorerControl : LazyTreeView : ThemedTreeView : System.Windows.Forms.TreeView
ExplorerHierarchyNode : HierarchyTreeNode : LazyNode : System.Windows.Forms.TreeNode
```

아이콘과 배지는 SSMS 고유 배관이 아니라 **WinForms 공개 API**로 그려진다.

- 아이콘: `LazyTreeView.AddIconToImageMap()`이 `node.ImageIndex`/`SelectedImageIndex`를
  `TreeView.ImageList`의 인덱스로 설정한다.
- 배지: `TreeView.StateImageList`에 `policy_nopolicy`/`policy_failed`/`policy_passed` 3장이
  올라가 있고, `AdjustStateHealthStatus()`가 `node.StateImageIndex`로 지목한다.
  **SSMS의 정책 상태(PBM) 오버레이가 바로 이 메커니즘이다.**

트리 참조를 얻는 경로는 이 저장소에 이미 있다 — `ShowHistoryCommand.TryHookTreeView()`가
`IObjectExplorerService`의 `Tree` 속성을 리플렉션으로 읽는다. 개체 탐색기 컨텍스트 메뉴가
SSMS 21에서 동작하므로 검증된 경로다. **오버레이는 새 위험을 만들지 않는다.**

## 3. 실기 실험 (2026-09-09, SSMS 21 / Northwind / `dbo.Table_2`)

폐기용 프로브 명령을 만들어 실제 SSMS 21 안에서 눌렀다. 결과:

| 확인 항목 | 결과 |
| --- | --- |
| `TreeView` 확보 | 성공. `ObjectExplorerControl`, `ImageList` 7개·`StateImageList` 3개, 모두 16x16 |
| 선택 노드 타입 | `ExplorerHierarchyNode`, `TreeNode` 파생 확인 |
| 경로 A — `StateImageList` 추가 + `StateImageIndex` 지정 | **화면에 나타남.** 아이콘 왼쪽 별도 칸에 그려지고 노드가 그만큼 오른쪽으로 밀린다 |
| 경로 B — 아이콘에 배지를 합성해 `ImageList` 추가 + `ImageIndex` 교체 | **화면에 나타남.** 아이콘 위에 겹쳐 그려진다 |
| URN → TreeNode 매핑 | 성공. `FindNode(urn)` → `GetService(typeof(INavigableItem))` → `INavigableItem.Tag`(WeakReference) → 선택 노드와 **동일 객체** |
| 새로 고침(F5) 후 생존 | **사라진다** |

`DrawMode`가 `OwnerDrawText`인 것도 확인했다 — 텍스트만 SSMS가 직접 그리고 이미지는 WinForms
기본 경로를 타므로, 우리가 넣은 이미지가 그대로 렌더된다.

URN → TreeNode가 성립한다는 것이 중요하다. 실제 기능은 "선택된 노드"가 아니라 "변경된 객체
N개"를 이름으로 찾아 칠해야 하는데, 그 경로가 열려 있다는 뜻이다.

## 4. 착수한다면 반드시 감당해야 하는 것

1. **갱신마다 다시 칠해야 한다.** `LoadNodeValuesFromItem` → `AddIconToImageMap`이 `ImageIndex`를
   원본으로 되돌리고, 이어지는 `AdjustStateHealthStatus`가 `StateImageIndex`를 -1/1로 되돌린다.
   실기에서도 F5 후 표시가 사라졌다. `TreeView.AfterExpand` 같은 공개 이벤트에 재적용을 걸어야 한다.
2. **경로 B를 쓴다.** 경로 A는 노드당 상태 이미지가 하나뿐이라 SSMS 정책 상태 배지와 배타적이고,
   노드를 옆으로 밀어 트리 정렬이 흐트러진다. 경로 B는 둘 다 없다.
3. **`ImageList` 누수를 막아야 한다.** 상태가 바뀔 때마다 합성 이미지를 `Images`에 계속 더하면
   목록이 무한히 자란다. `(원본 인덱스, 상태) → 합성 인덱스` 캐시가 필요하다.
   프로브는 매번 더하기만 했다 — 그대로 옮기면 안 된다.
4. **펼치지 않은 노드는 칠할 수 없다.** `TreeNode`가 아직 만들어지지 않았다.
   "펼쳐진 노드만 표시한다"가 사양이 되며, 이는 오버레이를 상태의 **완전한** 표시로 볼 수 없다는 뜻이다.
5. **UI 스레드 전용.** 트리를 건드리는 모든 지점이 그렇다.
6. **문서화되지 않은 규약 하나에 기댄다.** `INavigableItem.Tag`가 트리 노드를 가리키는
   `WeakReference`를 담는다는 것 — `ExplorerHierarchyNode` 생성자가 넣는다 — 은 SSMS 내부 구현이다.
   `Tag`는 공개 속성이지만 SSMS가 자기 용도로 쓰는 자리이므로 **읽기만 하고 쓰지 않는다.**

## 5. 그럼에도 지금 착수하지 않는 이유

가능하다는 것과 할 만하다는 것은 다르다.

- 4번(펼친 노드만) 때문에 오버레이는 상태를 **부분적으로만** 보여 준다. 사용자가 "아이콘이 없으니
  안 바뀐 것"이라고 읽으면 오히려 틀린 정보를 주게 된다. View Changes 창은 이 결함이 없다.
- 1번(재적용)과 3번(캐시)은 계속 살아 있어야 하는 상태 기계다. SSMS의 트리 갱신 시점을 우리가
  전부 알 수는 없으므로, 표시가 어긋나는 경우를 계속 쫓게 된다.
- 얻는 것은 편의뿐이다. 변경 상태는 View Changes 도구 창에서 전부, 그리고 정확하게 확인할 수 있다.
  다른 13개 기능 중 어느 것도 Feature 10에 의존하지 않는다.

`docs/team-rollout-backlog.md`의 P3 판단("그대로 둔다")은 유지한다. 다만 그 문서의 사유
"SSMS에 확장점이 없다"는 **부정확하다** — 확장점은 없지만 구현 경로는 있다. 사유를
"부분적으로만 정확한 표시가 되고, 유지 비용이 얻는 것보다 크다"로 읽는 것이 맞다.

## 6. 착수할 때 이어서 할 일

1~3, 5는 이미 구현·테스트되어 있다(`SsmsUrn`, `ObjectPathConvention`, `StateTracker.GetObjectState`).
남은 것은 4번뿐이고, 그 방법은 위 3~4절에 있다.

실험에 쓴 폐기용 프로브는 `spike/oe-overlay-probe` 브랜치에 있었고 실험 종료와 함께 버렸다.
다시 필요하면 이 문서만으로 재작성할 수 있다.
