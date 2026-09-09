# `master` 기준선 하네스 — 설계

> **성격: 새 도구(`tools/DBVC.Baseline`). 제품(`DBVC.Vsix`)에는 들어가지 않는다.**
> 운영 DB를 **읽기만** 해서 `master` 브랜치의 기준선을 만든다. 팀 배포 백로그 10번을 닫는다.

## 1. 왜 필요한가

`master`에는 "운영 DB에 있어야 할 것"이 담겨야 하는데, **그것을 담을 경로가 도구 안에 없다.**

감사(`audit`) 클론은 `InstallTracker`·`Extract`·`Commit`·`Push`가 전부 금지다(`MappingPolicy`).
설계상 옳다 — 운영 DB에 DDL 트리거를 설치하는 사고를 막는 것이 그 모드의 존재 이유다. 그러나
그 결과로 운영에서 뽑아 `master`에 담을 방법이 하나도 없다. `master`가 비면 운영의 모든 객체가
`MissingInBranch`("DB에만 있음")로 떠서 화면이 잡음뿐이 된다. **DBA가 첫날 여는 화면이 그것이다.**

### 검토한 길과 배제 사유

| | 방법 | 판정 |
| --- | --- | --- |
| A | 운영 백업을 복원한 임시 DB에서 추출 | **배제.** 백업이 몇 테라이고 복원할 비운영 인스턴스가 없다 |
| B | 첫 정규 배포 시점의 `develop`을 `master`로 | **배제.** `master := develop`이라 `git diff master..develop`이 비고, 지금까지 쌓인 개발↔운영 드리프트가 **영구히** 포착되지 않는다. 나중에 알고 싶어지면 결국 A를 해야 한다 |
| C | 감사 모드에 일회성 "기준선 만들기"를 연다 | **보류.** 옳은 장기 답이지만 `Extract`·`Commit`·`Push`를 조건부로 여는 일이라 가드레일이 복잡해진다. 이번 일정에 넣을 크기가 아니다 |
| E | 사람이 SSMS 마법사로 떠서 커밋 | **배제.** 비교가 `HasSameBytes`라 바이트 단위다. 마법사 기본값은 `ScriptForCreateOrAlter`도 UTF-8 BOM도 `DriAll`도 다르다 — 모든 파일이 어긋나 B와 같은 결과에 일만 더한다 |
| **D** | `DBVC.Core`를 부르는 하네스 | **채택** |

## 2. 왜 이것이 안전한가

**운영 DB에 아무것도 쓰지 않는다.** 근거는 셋이다.

1. **`SmoManager`는 `DBVC_ChangeLog`도 `StateTracker`도 참조하지 않는다.** 트리거는 *증분* 감지용이지
   추출의 전제가 아니다. 하네스는 `InstallTracker`를 부르지 않으므로 설치되는 것이 없다.
2. **추출과 차이 검사는 같은 루프다.** 둘 다 `RunScriptingLoop`을 돌고 마지막 콜백만 다르다 —
   추출은 `PublishIfChanged(stagingPath, outputPath)`, 비교는 `HasSameBytes(...)`. 즉 하네스가 하는
   읽기는 **앞으로 감사 클론이 매주 하게 될 바로 그 읽기**다. 새로운 종류의 부하가 아니다.
3. **필요한 권한은 접속 + 대상 객체의 `VIEW DEFINITION`뿐이다.**

몇 테라라는 크기는 데이터이고, 스크립팅이 읽는 것은 카탈로그 메타데이터다. 소요는 **객체 수**에
비례하지 용량에 비례하지 않는다.

## 3. 무엇을 만드는가

새 프로젝트 **`tools/DBVC.Baseline`** — `src/`가 아니라 `tools/`에 둔다. 제품이 아니라는 것이
경로에서 보여야 하고, 솔루션과 `.vsix`에 딸려 들어가지 않는다.

**타깃은 `net48`이다. 선택이 아니라 요구다.** `develop` 쪽 추출은 SSMS 안 net48에서
MDS 5.1.5 + SMO 171.30.0으로 돈다. 이 도구의 존재 이유가 **바이트 동일성**인데, 같은 코드를 다른
런타임에서 돌려 놓고 "아마 같을 것"에 기댈 수는 없다.

**MDS·SMO를 직접 `PackageReference` 하지 않는다.** `DBVC.Core` 프로젝트 참조로만 받는다
(CLAUDE.md의 규칙 — 직접 참조하면 다른 SMO가 올라와 `TypeLoadException`이 나고 그것이 삼켜진다).

## 4. 구성 요소

| 파일 | 책임 | DB 필요 |
| --- | --- | --- |
| `BaselineOptions` | 인자 파싱·검증. 순수 함수 | 아니오 |
| `PreflightCheck` | 대상 폴더가 기준선을 받아도 되는 상태인지 판정. 파일시스템을 직접 보지 않고 관측값(6.3)을 받는 순수 함수 | 아니오 |
| `BaselineRunner` | 순서를 지휘한다. `IConfigManager`·`ISmoManager`만 안다 | 아니오 |
| `BaselineReport` | 결과 모델과 렌더링 | 아니오 |
| `Program` | 인자, 암호 프롬프트, 임시 폴더 수명, 출력, 종료 코드 | 실행 시에만 |

**`ISmoManager`와 `IConfigManager`가 이미 인터페이스라는 것이 이 설계의 이음매다.** 그래서
`BaselineRunner`는 SQL Server 없이 목으로 전부 테스트된다. DB가 필요한 것은 `Program`의 마지막
한 줄뿐이다.

## 5. 실행 흐름

```
dbvc-baseline --server PRODSRV --database SalesDB --repo D:\dbvc\prod --sql-user dbvc_reader
```

1. **인자 검증** → 실패면 종료 코드 3
2. **사전 점검**(6.3) → 실패면 종료 코드 3
3. **암호 프롬프트**(6.1) — 가려진 입력
4. **임시 설정 생성**(6.2) — `%TEMP%` 아래 새 폴더의 `mappings.json`
5. `AddMapping(new MappingConfig { ..., Mode = Write })`
6. `smo.ScriptObjectsDetailed(server, db, objectNames: null, progress, ct)` → `ScriptResult`
7. `AddMapping(new MappingConfig { ..., Mode = Audit })` — 같은 대상을 덮어쓴다
8. `smo.CompareWithRepository(server, db, progress, ct)` → `ComparisonResult`
9. **결과 조립·출력**, `finally`에서 임시 폴더 삭제

7번이 6번보다 뒤여야 한다. `MappingPolicy`는 `Compare`를 `mode != Write`에서만 허용하므로,
순서가 뒤집히면 `OperationNotAllowedException`이 난다.

## 6. 안전 장치

### 6.1 암호는 인자로 받지 않는다

셸 이력과 프로세스 목록에 남기 때문이다. 가려진 프롬프트로 묻고, `SessionCredentialStore`
(메모리 전용, 디스크에 닿는 경로 없음)에 넣어 `SmoManager`에 주입한다.
**`--password` 플래그를 주면 거부한다** — 편의를 위해 열어 두면 반드시 쓰인다.

`--sql-user`를 생략하면 `SqlConnectionFactory`가 Windows 통합 인증으로 떨어진다(자격증명 저장소가
비었을 때의 기본 동작). 이번 팀은 SQL 읽기 계정을 쓰지만 경로를 막지는 않는다.

### 6.2 임시 매핑은 `%APPDATA%`를 건드리지 않는다

`ConfigManager(string configFilePath)`가 public이므로 `%TEMP%` 아래 새 폴더를 가리킨다.
`finally`에서 폴더째 지운다.

**이것이 D안의 원래 위험을 없앤다.** 진짜 `mappings.json`에 운영 DB가 `Mode = Write`로 남으면,
누군가 DBVC를 열고 초기화를 누르는 순간 운영에 트리거가 설치된다. 임시 파일을 쓰면 그런 상태가
**한 번도 존재하지 않는다** — 설치된 DBVC는 이 하네스가 돌았다는 사실을 알 수 없다.

### 6.3 대상 폴더가 비어 있지 않으면 거부한다

막으려는 사고는 하나다: **실수로 `dbvc\dev`를 가리켜 개발 클론을 운영 사진으로 덮어쓰는 것.**

`Program`이 세 값을 관측해 `PreflightCheck`에 넘긴다. 판정 함수는 파일시스템을 보지 않으므로
DB도 디스크도 없이 테스트된다.

| 관측값 | 거부 조건 |
| --- | --- |
| 폴더가 존재하는가 | 없으면 거부 |
| `.git`이 있는가 | 없으면 거부 — 클론이 아닌 아무 폴더를 가리킨 것이다 |
| `.sql` 파일이 하나라도 있는가 | 있으면 거부 |

**작업 트리가 깨끗한지는 보지 않는다.** `GitManager` 의존이 생기는 데 비해 얻는 것이 없다 —
막으려는 사고(개발 클론 덮어쓰기)는 `.sql` 존재 검사가 이미 잡고, `master`에 갓 체크아웃한
클론은 정의상 `.sql`이 없다.

### 6.4 커밋·푸시는 하지 않는다

사람이 `git status`로 확인하고 커밋한다. 첫 실행에서 권한 누락으로 객체가 빠졌는데 그대로
커밋되면, 나중에 그 객체가 "브랜치에만 있음"으로 떠서 배포 스크립트에 `CREATE`가 들어가고
실행하면 그 배치가 통째로 끊긴다(`SmoManager`의 주석이 이 함정을 기록해 두고 있다).

## 7. 종료 코드와 실패 처리

| 코드 | 뜻 |
| --- | --- |
| 0 | 추출·검증 모두 깨끗 — 커밋해도 된다 |
| 1 | 추출에 실패한 객체가 있다 (대개 `VIEW DEFINITION` 누락, 암호화된 모듈) |
| 2 | 검증에서 차이가 나왔다 — **이 기준선은 믿으면 안 된다** |
| 3 | 사전 점검·인자 오류 |

**실패해도 성공한 것은 그대로 남긴다.** 사람이 보고 판단할 재료가 있어야 한다. 다만 실패 객체
목록을 반드시 출력한다 — 조용히 빠지는 것이 이 도구의 유일한 치명적 실패 방식이다.

코드 2는 무언가 비결정적이거나 실행 중에 운영이 바뀌었다는 뜻이다. 어느 쪽이든 그 기준선은
버리고 다시 돌린다.

## 8. 테스트

TDD. 테스트 이름은 영어 `Method_Result_WhenCondition`.

**`tests/DBVC.Baseline.Tests`는 `net48` 단독이다.** 하네스가 net48이라 net10.0에서 참조할 수 없다.
저장소의 `net48;net10.0` 관례에서 벗어나는 유일한 자리이므로 이유를 프로젝트 파일 주석에 남긴다.
Windows에서만 돈다.

DB 없이 도는 것:

- **`BaselineOptions`** — 필수 인자 누락, 모르는 플래그, 그리고 **`--password`를 주면 거부**
- **`PreflightCheck`** — 관측값 세 개(폴더 존재 / `.git` 존재 / `.sql` 존재)의 조합마다 판정.
  파일시스템을 보지 않으므로 임시 폴더도 필요 없다
- **`BaselineRunner`**(목 `IConfigManager` + `ISmoManager`)
  - 추출 전에 `Mode = Write` 매핑을 쓰는가
  - `ScriptObjectsDetailed`를 **`objectNames: null`** 로 부르는가 — 목록을 넘기면 조용히 부분
    기준선이 만들어지므로 못 박는다
  - 검증 전에 `Mode = Audit`으로 덮어쓰는가 (5장의 순서)
  - `HasFailures`면 판정이 "실패", 차이가 있으면 "믿을 수 없음", 둘 다 깨끗해야 "정상"
  - 추출이 예외로 죽어도 임시 설정이 정리되는가
- **`BaselineReport` 렌더링에 암호가 나타나지 않는가** — 보안 성질을 테스트로 고정한다

DB가 필요한 것 하나 — 기존 `SmoManagerIntegrationTests`의 방식을 따른다. `localhost`에 임시 DB를
만들고 **하네스 경로로 뽑은 바이트와 일반 추출 경로로 뽑은 바이트가 같은지** 비교한다. 접속되지
않으면 실패가 아니라 Skip이다. 이것이 "같은 스크립팅 경로"라는 주장의 유일한 자동 검증이다.

## 9. CI가 검증하지 못하는 것

**실제로 운영 DB에 붙여 돌려 보기 전에는 "동작한다"고 말하지 않는다.**

- 운영 DB 접속 자체와 그 계정의 `VIEW DEFINITION` 실제 범위
- 객체 수에 따른 소요 시간
- 운영에만 있는 객체 타입을 DBVC가 열거하는지

**첫 실행이 곧 인수 시험이다.** 종료 코드 1이 나오는 것을 실패로 보지 않는다 — 그것이 권한 구멍을
드러내는 방법이고, 지금은 그것을 알 다른 수단이 없다.

## 10. 함께 고칠 문서

이 결정이 **회의 결정 항목 하나를 닫는다.**

| 문서 | 무엇 |
| --- | --- |
| `docs/setup-checklist.md` | `master` 기준선 절차(런북)를 적는다 — **10번을 닫는 것은 도구가 아니라 이 절차다.** 그리고 3단계에 `develop`·`master` 브랜치를 만드는 항목이 없다(예시가 `main`). 함께 넣는다 |
| `docs/team-rollout-backlog.md` | 10번을 닫고 결정 근거를 남긴다 — A 배제 사유(백업 몇 테라, 비운영 인스턴스 없음), D 채택, C는 P3로 남긴다 |
| `docs/why-db-version-control.html` | 결정 항목 "`master` 기준선을 어떻게 만들지"가 **정해졌다.** 오늘 정해야 하는 것이 7개 → 6개. 도입 절의 "함정 2 — 아직 정해지지 않았다"도 바뀐다. **같은 URL로 재게시한다** |
| `CLAUDE.md` | 아키텍처 절이 "두 계층이다"로 시작하는데 프로젝트가 셋이 된다. 한 줄 |

`README.md`는 하네스가 사용자 기능이 아니므로 손대지 않는다. 저장소 구조를 나열하는 자리가
있으면 그때 판단한다.

## 11. 남는 위험

- **하네스가 상시 존재한다.** 가드레일을 우회하는 경로가 저장소에 남는다는 뜻이다. `tools/`에
  두고 이름과 주석으로 용도를 못 박지만, 그것이 막아 주는 것은 오해이지 오용이 아니다.
  오용을 실제로 막는 것은 6.3의 사전 점검이다.
- **C를 대체하지 않는다.** 환경을 새로 온보딩할 때마다 이 하네스를 다시 꺼내야 한다. 그것이
  불편해지는 시점이 C를 착수할 시점이다. 백로그에 P3로 남긴다.
- **첫 실행 전까지는 전부 추정이다.** 특히 소요 시간과 권한 범위.
