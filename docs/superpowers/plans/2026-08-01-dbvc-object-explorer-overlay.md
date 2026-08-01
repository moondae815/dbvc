# Object Explorer 상태 오버레이 (Feature 10) — 착수 보류

> **상태: BLOCKED.** 이 문서는 구현 계획이 아니라 **왜 아직 구현하지 않았는지**에 대한 기록이다.
> 아래 선행 조건이 해소되기 전에는 코드를 작성하지 않는다.

**목표(원안):** SSMS Object Explorer 트리의 노드(테이블, 프로시저 등)에
변경 상태 아이콘(M / A / D / C)을 오버레이로 표시한다. (ssms21-plugin-design 4.1-3)

## 1. 무엇이 필요한가

| 필요한 것 | 상태 |
| --- | --- |
| Object Explorer 트리에 접근하는 API (`IObjectExplorerService`, `INodeInformation`) | `Microsoft.SqlServer.Management.UI.VSIntegration` 어셈블리에 있음 |
| 위 어셈블리의 배포 경로 | **NuGet에 없음.** SSMS 설치 디렉터리에만 존재 |
| 노드 아이콘을 교체·합성하는 공개 확장점 | **없음.** 문서화된 API가 존재하지 않음 |

확인 결과(2026-08-01, nuget.org):
`Microsoft.SqlServer.Management.UI.VSIntegration`, `Microsoft.SqlServer.Management.SqlStudio`,
`Microsoft.SqlServer.SqlStudio`, `Microsoft.SqlServer.Management.SDK.SqlStudio` — 모두 검색 결과 0건.

## 2. 왜 지금 구현하지 않는가

1. **빌드 재현성이 깨진다.** 로컬 SSMS 설치 경로를 하드코딩한 `<Reference>`가 필요해지고,
   SSMS가 없는 환경(CI 포함)에서 솔루션이 빌드되지 않는다.
2. **공개 확장점이 없다.** 아이콘 오버레이는 상용 도구들이 내부 구현에 의존해 구현하는 영역이다.
   문서화되지 않은 내부에 기대는 코드는 SSMS 패치마다 조용히 깨진다.
3. **검증할 수 없다.** 현재 개발 환경에서 SSMS를 실행할 수 없어, 작성해도 동작 여부를 확인할 방법이 없다.
   동작을 확인하지 못한 코드를 "구현 완료"로 보고하는 것은 이 저장소가 이미 한 번 겪은 문제다.
   (`GetStatus`가 무조건 `"Clean"`을 반환하던 스텁 등)

## 3. 선행 조건

아래가 모두 충족되면 착수한다.

- [ ] Windows + SSMS 21이 설치된 개발/검증 환경 확보
- [ ] `Microsoft.SqlServer.Management.UI.VSIntegration`을 참조하는 재현 가능한 방법 결정
      (SSMS 설치 경로 탐지 후 조건부 참조 / 사내 NuGet 피드에 재배포 등)
- [ ] Object Explorer 노드 아이콘 오버레이의 실제 확장점을 SSMS 21에서 실험으로 확인
      (프로토타입 1개 노드에 아이콘을 얹는 것까지 성공)

## 4. 선행 조건이 해소되면 할 일 (개요)

1. `IObjectExplorerService`로 선택/확장된 노드의 URN을 얻는다.
2. URN을 `ObjectPathConvention`의 `[Schema]/[ObjectType]/[Name].sql` 경로로 변환한다.
   (변환 규칙은 이미 구현·테스트되어 있다)
3. `StateTracker.GetObjectState(server, db, qualifiedName)`로 상태를 조회한다.
   (이미 구현·테스트되어 있다)
4. 상태에 대응하는 오버레이 이미지를 노드에 합성한다. ← **이 단계만이 미지의 영역이다**
5. `RefreshState` 이후 영향받은 노드만 다시 그린다.

즉 1~3, 5에 필요한 코어 로직은 이미 존재한다. 남은 것은 4번, SSMS 내부 확장점 하나다.

## 5. 대안

오버레이 없이도 변경 상태는 View Changes 도구 창에서 전부 확인할 수 있다.
Feature 10은 편의 기능이며, 다른 13개 기능의 동작에 필요하지 않다.
