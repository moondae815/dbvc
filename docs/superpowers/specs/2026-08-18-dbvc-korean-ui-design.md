# DBVC UI 문구 한국어 통일 설계

## 1. Overview

DBVC의 화면 문구는 두 언어가 섞여 있다. 규칙이 없지는 않다 — **명사형 라벨(버튼·컬럼·탭·메뉴)은 영어, 문장(툴팁·알림·오류·확인)은 한국어**가 사실상의 관행이었다. 그러나 그 관행에서 벗어난 곳이 넷이다.

| 어긋난 곳 | 위치 |
| --- | --- |
| `저장소 연결...` — 유일한 한국어 버튼 | `ViewChangesControl.xaml:46` |
| `This database is not initialized for DBVC.` — 영어 문장 | `ViewChangesControl.xaml:141` |
| `Active Database is not mapped to a Git repository.` — 영어 문장 | `ViewChangesViewModel.cs:22` |
| `DBVC Deployment Script 저장` — 한 제목 안에서 혼용 | `ViewChangesViewModel.cs:739` |

특히 셋째는 노란 경고 배너로 강조되어 뜨는데, **바로 옆에 붙는 버튼이 한국어**(`저장소 연결...`)라 한 배너 안에서 두 언어가 마주 본다. 매핑되지 않은 데이터베이스를 열면 항상 보이는 화면이므로, 도입 초기 사용자가 가장 먼저 마주치는 인상이 이것이다.

이 문서는 화면 문구를 한국어로 통일하는 설계를 다룬다.

## 2. 결정된 전제

사용자가 정한 것이며, 이 설계의 나머지가 여기서 따라 나온다.

* **Git 동사(`Commit`·`Pull`·`Push`)는 영어로 둔다.** 한국 개발자가 그대로 쓰는 어휘이고, Git 클라이언트·문서와 용어가 어긋나면 오히려 대응이 끊긴다. 기존 문서도 이미 "**Commit** 을 누르면", "**Pull** 은" 형태로 쓰고 있어 화면과 문서가 계속 맞는다.
* **상태값은 표시 계층에서만 바꾼다.** Core가 내놓는 `Added`·`Modified`·`Deleted`는 데이터로 남긴다(4.4).
* **VSIX 확장 이름·설명과 파일 저장 대화상자 필터는 한국어로 바꾼다.**
* **생성된 `.sql` 헤더는 영어로 둔다.** 배포 담당자에게 전달되거나 다른 도구가 읽을 수 있고, 바꾸면 기존 생성물과 diff가 틀어진다.

## 3. Scope

### In Scope

* `ViewChangesControl.xaml`의 버튼·컬럼·탭 라벨과 두 문장
* `ViewChangesViewModel`의 경고 문구와 스크립트 대화상자 제목
* `ChangeItemViewModel`에 표시 전용 상태 텍스트 추가
* `DbvcPackage.vsct`의 컨텍스트 메뉴 문구
* `source.extension.vsixmanifest`의 이름·설명
* `IFileSaveDialog`의 필터
* 위 라벨을 참조하는 README·도입 체크리스트
* **"View Changes" 명칭 정리** (4.8)

### Out of Scope

* **Git 동사 버튼.** 2절 참조.
* **생성된 `.sql` 파일의 헤더.** 2절 참조.
* **`Language="en-US"`(vsixmanifest).** 설치 대상 판정에 관여할 수 있고, 바꿔야 할 **측정된** 이유가 없다. 텍스트만 바꾼다.
* **코드 식별자.** `ViewChangesToolWindow`·`ViewChangesViewModel`·`ViewChangesControl`·`ViewChangesCommand` 같은 클래스·파일 이름은 사용자에게 보이지 않는다. 건드리면 diff만 커지고 얻는 것이 없다.
* **`DBVC_ChangeLog` 테이블과 그 컬럼 이름.** 데이터베이스 스키마이지 UI가 아니다.
* **기본 저장 파일명.** ASCII로 유지한다(4.7).

## 4. Component Design

### 4.1. 버튼 — `ViewChangesControl.xaml`

| 현재 | 변경 | Width |
| --- | --- | --- |
| `Connect` | 연결 | 80 → 70 |
| `Refresh` | 새로고침 | 70 → 80 |
| `Commit` | *그대로* | 70 |
| `Pull` | *그대로* | 70 |
| `Push` | *그대로* | 70 |
| `Deployment Script` | 배포 스크립트 | 130 → 100 |
| `Rollback Script` | 롤백 스크립트 | 120 → 100 |
| `Setup DBVC` | DBVC 초기화 | 150 (유지) |
| `저장소 연결...` | *그대로* | 110 (유지) |

`Setup DBVC` 가 **초기화**가 되는 이유는 바로 위 안내문이 이미 "초기화되지 않았습니다"라고 말하기 때문이다(4.3). 버튼과 안내문이 같은 말을 써야 무엇을 누르는지가 자명해진다.

**폭은 글자 수에 맞춰 줄인다.** `WrapPanel` 총 폭이 줄어 좁게 도킹했을 때 줄바꿈이 오히려 **덜** 일어난다 — 이 작업이 레이아웃을 나쁘게 만들지 않는다는 뜻이다.

툴팁은 이미 전부 한국어이므로 **바뀌지 않는다.** 다만 `Connect`·`Refresh`·`Setup DBVC` 세 버튼에는 툴팁이 없는데, 이 설계는 그것을 채우지 않는다 — 문구 통일과 별개의 일이다.

### 4.2. 컬럼과 탭

| 현재 | 변경 |
| --- | --- |
| `Stage` | 스테이징 |
| `State` | 상태 |
| `Object` | 객체 |
| `Diff` (탭) | 비교 |
| `History` (탭) | 이력 |
| `Date` | 날짜 |
| `Author` | 작성자 |
| `Message` | 메시지 |
| `SHA` | *그대로* |

**`SHA`만 남기는 이유.** 번역어가 없는 식별자 형식 이름이고, 사용자가 그 값을 Git 클라이언트에 그대로 붙여넣는 대상이다. "해시"로 옮기면 화면의 값과 사용자가 할 행동 사이의 연결이 흐려진다.

`History` 탭의 빈 목록 문구는 이미 "이력이 없습니다."이므로, 탭 이름을 **이력**으로 하면 둘이 맞는다.

### 4.3. 문장 둘

| 현재 | 변경 |
| --- | --- |
| `This database is not initialized for DBVC.` | 이 데이터베이스는 아직 DBVC로 초기화되지 않았습니다. |
| `Active Database is not mapped to a Git repository.` | 현재 데이터베이스에 연결된 Git 저장소가 없습니다. |

**둘째에서 "매핑"을 쓰지 않는다.** 바로 옆에 붙는 버튼이 `저장소 연결...`이므로, 배너가 같은 말("연결")을 써야 사용자가 무엇을 눌러야 하는지 문장 하나로 안다. "매핑"은 `ConfigManager`의 내부 용어이지 사용자가 알아야 할 개념이 아니다.

### 4.4. 상태값 — 표시 전용 변환

`ChangeItemViewModel`에 표시 전용 속성을 더하고, XAML의 `State` 컬럼 바인딩을 그리로 옮긴다.

```csharp
/// <summary>화면에 뿌리는 상태. Core의 <see cref="State"/>는 데이터로 남는다.</summary>
public string StateText => State switch
{
    "Added" => "추가",
    "Modified" => "수정",
    "Deleted" => "삭제",
    _ => State ?? string.Empty
};
```

**Core의 값을 바꾸지 않는 이유.** 이 문자열들은 표시용이 아니라 **데이터**다. `WorkingTreeCleaner`가 `DeletedState` 상수로 비교해 삭제된 객체의 파일을 지울지 판정하고, `StateTracker`가 DDL 이벤트 타입(`CREATE_*`/`DROP_*`)에서 만들어 내며, Core 테스트 여럿이 문자열로 검증한다. 표시만 바꾸면 Core 계약과 그 테스트가 그대로 남는다.

**`_ => State`(원문 통과)가 중요하다.** Core가 새 상태값을 내놓게 되면 조용히 빈칸이 되는 대신 원문이 화면에 뜬다. 번역표에 없는 값이 생겼다는 사실 자체가 보여야 한다.

### 4.5. 컨텍스트 메뉴 — `DbvcPackage.vsct`

`DBVC: Compare with Repository` → **`DBVC: 저장소 버전과 비교`** (`ButtonText`·`MenuText` 둘 다).

보기 메뉴의 `DBVC`는 제품 이름이므로 그대로 둔다. 두 항목의 `ToolTipText`는 이미 한국어다.

`CompareWithRepositoryCommand.cs`의 XML 주석이 이 메뉴를 `"Compare with Repository"`로 부르고 있으므로 함께 고친다 — 화면에 뜨지는 않지만 다음 사람이 코드에서 메뉴를 찾을 때 쓰는 이름이다.

### 4.6. 확장 관리자 — `source.extension.vsixmanifest`

| 항목 | 변경 |
| --- | --- |
| `DisplayName` | `DBVC — SSMS 데이터베이스 형상 관리` |
| `Description` | `SQL Server Management Studio 21용 데이터베이스 형상 관리(Database Version Control) 확장입니다.` |
| `Tags` | *그대로* |
| `Language` | *그대로 (`en-US`)* |

**`Tags`를 두는 이유.** 검색어이지 표시 문구가 아니다. `SQL`·`SSMS`·`Git`은 한국어 환경에서도 그대로 검색된다.

### 4.7. 파일 저장 대화상자

**필터** (`IFileSaveDialog.cs`)

```
SQL 스크립트 (*.sql)|*.sql|모든 파일 (*.*)|*.*
```

**제목과 기본 파일명** (`ViewChangesViewModel.GenerateScript`)

지금은 값 하나가 양쪽에 쓰인다.

```csharp
var kindLabel = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";
var title = $"DBVC {kindLabel} Script";                      // 알림·대화상자 제목
... $"DBVC_{kindLabel}_{DatabaseName}.sql"                    // 기본 파일명
```

이것을 **둘로 가른다.**

```csharp
var kindText = kind == ScriptKind.Rollback ? "롤백" : "배포";   // 표시
var kindSlug = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";  // 파일명
var title = $"DBVC {kindText} 스크립트";
... _saveDialog.PromptForSavePath($"{title} 저장", $"DBVC_{kindSlug}_{DatabaseName}.sql");
```

**기본 파일명을 ASCII로 유지하는 이유.** 이 파일은 폐쇄망 반입 절차를 거치거나 다른 도구가 처리할 수 있다. 한글 파일명이 인코딩 문제를 살 이유가 없고, 얻는 것도 없다.

`title`은 대화상자 제목뿐 아니라 **"내보낼 내용이 없습니다" 알림의 제목**으로도 쓰이므로, 한 번 바꾸면 두 곳이 함께 한국어가 된다.

### 4.8. "View Changes" 명칭 정리

문서는 이 창을 "View Changes 도구 창"이라 부르는데, **UI는 그 이름을 한 번도 말하지 않는다.** 실제 창 제목은 `DBVC`이고(`ViewChangesToolWindow.cs:11`), 보기 메뉴 항목도 `DBVC`다(`.vsct`).

**문서를 UI에 맞춘다. 그 반대가 아니다.** 창 제목 `DBVC`는 그것을 여는 메뉴 항목 `DBVC`와 이미 일치하므로 UI 쪽이 옳고, 문서만 실재하지 않는 이름을 쓰고 있다. 창 제목을 더 서술적으로 바꾸면 메뉴와 어긋나므로 건드리지 않는다.

세 곳이다.

| 위치 | 현재 | 변경 |
| --- | --- | --- |
| `README.md:9` | `**WPF 기반 차이점 뷰어 (View Changes Tool Window):**` | `**WPF 기반 차이점 뷰어 (DBVC 창):**` |
| `README.md:23` | `View Changes 도구 창에서` | `DBVC 창에서` |
| `docs/setup-checklist.md:459` | `View Changes 창에서` | `DBVC 창에서` |

코드의 `ViewChangesToolWindow`·`ViewChangesViewModel`·`ViewChangesControl`은 **바꾸지 않는다**(3절).

### 4.9. 문서

바뀐 라벨을 참조하는 곳을 함께 고친다. 고치지 않으면 문서가 없는 버튼을 누르라고 시킨다.

| 라벨 | README | 체크리스트 |
| --- | --- | --- |
| `Connect` → 연결 | 3 | 15 |
| `Refresh` → 새로고침 | 3 | 8 |
| `Setup DBVC` → DBVC 초기화 | 1 | 3 |
| `Deployment Script` → 배포 스크립트 | 0 | 2 |
| `Rollback Script` → 롤백 스크립트 | 0 | 1 |
| `Diff` → 비교 | 3 | 5 |
| `History` → 이력 | 2 | 2 |

`Commit`·`Pull`·`Push` 참조는 **손대지 않는다.**

숫자는 `grep -c` 기준의 근사치다. 실제 편집은 문맥을 보고 판단한다 — 예컨대 `Diff`는 탭 이름을 가리킬 때만 바꾸고, "diff를 본다"처럼 일반 명사로 쓰인 곳은 그대로 둔다.

## 5. Error Handling

이 작업은 새 실패 경로를 만들지 않는다. 문자열 교체와 표시 전용 속성 하나가 전부다.

유일하게 새로 판단이 필요한 지점은 4.4의 알 수 없는 상태값이며, 그것은 예외가 아니라 **원문 통과**로 처리한다.

## 6. Testing Strategy

**단위 테스트**

`ChangeItemViewModelTests`

* `Added`·`Modified`·`Deleted` → 각각 `추가`·`수정`·`삭제`
* 번역표에 없는 값(예: `Renamed`) → **그 값이 그대로** 나오는지. 이것이 "조용히 빈칸이 되지 않는다"는 계약이다
* `null` → 빈 문자열

**기존 테스트가 닿는 곳은 정확히 두 군데다.** 세어 확인했다.

1. `ViewChangesViewModelTests.cs:1401` — `_notifier.InfoCalls[0].Title` 이 `"DBVC Deployment Script"` 인지 검증한다. 4.7에서 제목이 `DBVC 배포 스크립트` 로 바뀌므로 **함께 갱신해야 한다.** 이 테스트가 실패하면 그것이 정상이다.
2. `ViewChangesViewModelTests.cs:180` — 영어 경고문을 언급하는 **주석 한 줄**. 문구를 4.3에 맞춘다.

**함께 바뀌지 않는 것.** `ScriptGeneratorTests.cs:30,43` 과 `ViewChangesViewModelTests.cs:1334,1362` 는 **생성된 스크립트 본문**의 헤더(`DBVC Deployment Script`)를 검증한다. 그 헤더는 영어로 남기기로 했으므로(2절) 이 넷은 그대로 통과해야 한다. 알림 제목과 스크립트 헤더가 지금은 같은 문자열이라 헷갈리기 쉬운데, 이 설계가 그 둘을 갈라놓는다 — **하나는 바뀌고 하나는 안 바뀐다.**

그 밖의 테스트는 Core의 `State` 값과 한국어 알림 문구를 검증하므로 영향받지 않는다.

**검증되지 않는 것 (명시)**

* **버튼 폭과 `WrapPanel` 줄바꿈.** WPF 렌더링은 CI가 검증하지 않는다. 폭이 줄어드는 방향이므로 악화될 가능성은 낮지만, SSMS 21에서 좁게 도킹해 눈으로 확인해야 한다.
* **`.vsct` 메뉴 문구의 실제 표시.** `.vsct` 컴파일과 메뉴 등록은 SSMS 실행 환경에서만 확인된다.
* **확장 관리자의 이름·설명 표시.** `.vsix` 설치 후에만 보인다.

## 7. 기존 코드에 미치는 영향

* `ChangeItemViewModel`에 속성 하나가 는다. 기존 `State`는 그대로 남는다.
* `ViewChangesViewModel.GenerateScript`의 지역 변수 하나가 둘로 갈린다. 알림 제목이 바뀌므로 그것을 검증하는 테스트 하나가 함께 갱신된다(6절).
* `ViewChangesControl.xaml`의 `State` 컬럼 바인딩이 `StateText`로 바뀐다.
* **Core는 전혀 바뀌지 않는다.** 이 작업은 `DBVC.Vsix`와 문서에만 닿는다.
* `.vsct`가 바뀌므로 `.vsix` 재빌드가 필요하다. 버전을 올릴지는 이 설계의 범위 밖이다.
