# 저장소 인코딩 UTF-8 전환 설계 — Git이 `.sql`을 텍스트로 보게 한다

## 1. 문제

저장소에 쌓이는 `.sql`이 UTF-16LE + BOM이다. `SmoManager.BuildScriptingOptions()`가
`ScriptingOptions.Encoding`을 설정하지 않아 SMO 기본값이 그대로 나간다.
`SmoManagerTests.ScriptAll_PreservesBytesExactly_WhenContentDiffers`가 기대 바이트
`FF FE`로 그 사실을 이미 못 박고 있다.

**Git은 UTF-16 파일을 바이너리로 취급한다.** 임시 저장소에서 재현했다.

```
$ git diff --stat    →  p.sql | Bin 60 -> 60 bytes
$ git diff           →  Binary files a/p.sql and b/p.sql differ
```

병합은 더 나쁘다. 서로 다른 줄을 고쳤는데도 3-way 병합이 성립하지 않는다.

```
$ git merge feat
warning: Cannot merge binary files: p.sql (HEAD vs. feat)
CONFLICT (content): Merge conflict in p.sql
```

### 1.1 혼자 쓰면 드러나지 않는다

DBVC의 Diff 뷰는 파일을 직접 읽어 그리므로 정상으로 보인다. 드러나는 자리는 전부 도구 밖이다.

- **GitLab MR에서 스키마 변경을 리뷰할 수 없다.** "Binary file, no diff"만 나온다.
  형상 관리를 도입한 이유의 절반이 여기서 사라진다
- `git blame`, `git log -p`가 무용하다
- 같은 파일을 둘이 만지면 통째로 ours/theirs 중 하나를 고르는 수밖에 없다
- 파일 크기가 두 배다

그래서 사용 인원이 늘어나는 시점이 이 결함이 처음 아픈 시점이다.

### 1.2 이미 알고 있었고 미뤄 둔 사안이다

`specs/2026-08-19-dbvc-ddl-trigger-v2-design.md` 2절이 "UTF-16 인코딩 문제는 이 스펙 밖이다 …
별도 스펙으로 뒤에 낸다", 5절이 "UTF-16 → UTF-8 전환 — 별도 스펙"이라 적었다. 이 문서가 그것이다.

미룬 판단 자체는 옳았다. 저장소에 쌓인 파일을 전부 다시 쓰는 작업이라 트리거 재설치와 한
릴리스에 겹치면 어느 쪽이 무엇을 깨뜨렸는지 가릴 수 없다.

### 1.3 함께 드러난 것 — 생성된 배포 스크립트도 BOM이 없다

`DeploymentViewModel.SaveScript()`가 `File.WriteAllText(path, export.Script)`를 부른다.
인자 두 개짜리 오버로드는 **BOM 없는 UTF-8**로 쓴다. 배포 3단계 루프는 그 파일을 SSMS 쿼리
창에서 사람이 직접 실행하는 것을 전제하는데, SSMS는 BOM 없는 `.sql`을 Windows ANSI
코드페이지(한국어 환경이면 949)로 읽는다. 한국어 주석과 `MS_Description` 확장 속성이 든
스크립트가 깨진 채 실행된다. 원인이 같고 고치는 방법도 같으므로 이 스펙에 포함한다.

## 2. 결정

**저장소 `.sql`을 UTF-8 + BOM으로 쓴다.** 읽는 코드는 건드리지 않는다.

### 2.1 읽는 자리는 이미 인코딩 중립이다 — 실측으로 확인했다

`.sql`을 읽는 자리는 여섯이다.

| 위치 | 방법 |
| --- | --- |
| `ScriptExporter.ReadWorkingTreeFile` | `File.ReadAllText` |
| `DiffService.ReadWorkingTreeFile` | `File.ReadAllText` |
| `DeploymentViewModel` (브랜치 파일) | `File.ReadAllText` |
| `SmoManager` (스테이징 텍스트) | `File.ReadAllText` |
| `GitManager.GetFileContentAtHead` | `Blob.GetContentText()` |
| `GitManager.GetFileContentBeforeLastCommit` | `Blob.GetContentText()` |

세 인코딩 × 두 경로를 실제로 돌려 한국어가 든 문자열이 왕복하는지 확인했다.

| | UTF-16LE + BOM | UTF-8 + BOM | UTF-8 BOM 없음 |
| --- | --- | --- | --- |
| `File.ReadAllText` | 일치 | 일치 | 일치 |
| `Blob.GetContentText()` | 일치 | 일치 | 일치 |

둘 다 BOM을 감지하고, BOM이 없으면 UTF-8로 읽는다. **그래서 이 전환은 읽는 코드를 한 줄도
바꾸지 않는다.** 다만 이것은 문서가 아니라 실측에 기댄 전제이므로, LibGit2Sharp 업그레이드가
동작을 바꾸면 Diff가 조용히 깨진다. 4절의 특성화 테스트가 그것을 막는다.

### 2.2 BOM을 붙이는 이유

DBVC의 읽기는 BOM이 있든 없든 동작하므로, 판단 기준은 **저장소 파일을 읽는 도구가 DBVC만이
아니라는 것**이다. 이 팀의 주된 사용법은 `.sql`을 SSMS로 열어 직접 실행하는 것이고(배포 3단계
루프), SSMS·`sqlcmd`는 BOM이 없으면 ANSI 코드페이지로 읽는다. 한국어를 담는 저장소에서
BOM 3바이트는 싼 보험이다.

BOM이 있어도 Git은 텍스트로 본다. NUL 바이트가 없기 때문이며, 1절의 재현과 반대 결과가 나온다.

이어붙이기는 문제가 되지 않는다. `ScriptExporter`가 모으는 것은 이미 디코딩된 `string`이고
디코더가 BOM을 떼어내므로, 병합된 스크립트 중간에 BOM이 끼지 않는다.

### 2.3 `.gitattributes`가 필요해진다 — 이 전환이 만드는 문제다

지금 `.sql`은 Git이 바이너리로 보므로 줄바꿈 변환이 **적용되지 않는다.** UTF-8로 바꾸는 순간
텍스트가 되어 `core.autocrlf`가 작동하기 시작한다. 기계마다 설정이 다르면 줄바꿈만 바뀐 가짜
diff가 쌓인다. 사용 인원이 늘수록 확실히 일어난다.

무엇을 쓰느냐가 중요하다. 두 후보를 실측했다.

| `.gitattributes` | 작업 트리 vs 블롭 바이트 | `git diff` |
| --- | --- | --- |
| `*.sql -text` | **같음** | 텍스트 |
| `*.sql text eol=crlf` | 다름 | 텍스트 |

`text eol=crlf`를 쓰면 안 된다. 블롭은 LF, 작업 트리는 CRLF가 되는데 **Diff 뷰의 Old는
블롭에서(`GetFileContentAtHead`), New는 작업 트리에서(`ReadWorkingTreeFile`) 온다.** 둘 사이에
줄바꿈 변환이 끼면 DiffPlex가 모든 줄을 변경으로 판정한다. 양쪽을 정규화하는 코드는 없다.

`-text`는 줄바꿈 변환만 끄고 텍스트 diff와 3-way 병합은 그대로 얻는다. 지금의 바이트 동일성을
유지하면서 Git이 내용을 읽게 만드는 유일한 조합이다.

## 3. 설계

### 3.1 쓰는 자리 둘

```csharp
// SmoManager.BuildScriptingOptions()
Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
```

```csharp
// DeploymentViewModel.SaveScript()
File.WriteAllText(path, export.Script, new UTF8Encoding(true));
```

`ScriptDrops` 세터가 값과 무관하게 `ScriptForCreateOrAlter`를 꺼버리는 부작용이 있었다.
`Encoding` 세터에도 같은 함정이 있는지는 문서로 알 수 없으므로, 실제 SQL Server에서 나온
산출물의 앞 3바이트를 통합 테스트로 확인한다(4절).

### 3.2 `RepositoryEncoding` — 저장소가 옛 인코딩인지 판정한다

Core에 새로 둔다. `ExtractionBaseline.Exists`와 같은 방식으로 규약(`[Schema]/[Type]/[Name].sql`)에
맞는 파일을 **처음 하나만** 찾아 앞 2바이트를 본다.

```
FF FE      → Legacy    (UTF-16LE)
그 외      → Current
파일 없음  → Unknown   (갓 연결한 저장소. 판정하지 않는다)
```

전부를 훑지 않는 이유는 `ExtractionBaseline`과 같다. 저장소가 한 인코딩으로 통일되어 있다는
전제가 깨지는 경우는 전환이 중간에 멈춘 때뿐이고, 그때는 다시 눌러 이어가면 된다.

읽기에 실패하면 `Unknown`으로 본다. 판정을 못 해 배너를 안 띄우는 쪽이, 멀쩡한 저장소에
전 파일 재작성을 권하는 쪽보다 안전하다.

### 3.3 배너와 전환 버튼

`IsTrackerOutdated` 배너와 같은 자리, 같은 어휘를 쓴다.

- 조건: `Mode`가 `Write`이고 `RepositoryEncoding.Detect(...) == Legacy`
- **배포·감사 모드에서는 띄우지 않는다.** 그쪽은 추출 자체가 금지되어 있고(`MappingPolicy`),
  전환된 커밋을 Pull하면 저절로 해결된다. 누를 수 없는 버튼을 보여 줄 이유가 없다
- 버튼이 하는 일: 확인 → `.gitattributes` 생성(없을 때만) → 전체 다시 추출
- 판정 시점은 `IsTrackerOutdated`와 같다(연결 시의 조사) **그리고 새로고침이 끝난 뒤**다.
  전환이 끝나면 파일이 UTF-8이 되어 `Detect`가 `Current`를 내고 배너가 스스로 사라진다 —
  커밋하기 전에 이미 사라진다. **그 사라짐이 전환이 실제로 일어났다는 유일한 화면 신호다.**
  다시 읽지 않으면 성공한 뒤에도 배너가 남아 사용자가 또 누른다

전체 추출이 끝나면 모든 `.sql`이 `수정`으로 뜬다. 커밋은 사용자가 한다 — 도구가 대신 만들지
않는다. 이 커밋이 저장소 전체를 다시 쓰는 유일한 커밋이므로, 메시지와 시점을 사람이 정해야 한다.

**확인 문구에 "팀에서 한 사람만 하고 나머지는 Pull하세요"를 넣는다.** 여러 사람이 각자 누르면
전 파일 재작성 커밋이 사람 수만큼 생겨 서로 충돌한다. 도구가 막을 수는 없고 말할 수는 있다.

### 3.4 `.gitattributes` 내용

```
# DBVC가 추출하는 .sql은 SMO가 CRLF로 쓴다. 줄바꿈 변환을 끄면 작업 트리와 블롭의 바이트가
# 같아진다 — Diff의 Old는 블롭에서, New는 작업 트리에서 오므로 변환이 끼면 모든 줄이
# 변경으로 보인다. 텍스트 diff와 3-way 병합은 -text와 무관하게 그대로 동작한다.
*.sql -text
```

이미 있으면 건드리지 않는다. 사용자가 손으로 넣은 규칙을 덮어쓰지 않기 위해서다.

## 4. 검증

| 확인할 것 | 어디서 | SQL Server |
| --- | --- | --- |
| 스크립팅 옵션이 UTF-8 + BOM을 요구한다 | `SmoManagerTests` | 불필요 |
| 추출된 바이트가 그대로 보존된다(기대값을 `EF BB BF`로) | `SmoManagerTests` | 불필요 |
| **실제 SMO 산출물이 `EF BB BF`로 시작한다** | `SmoManagerIntegrationTests` | **필요** |
| `Detect`가 Legacy / Current / Unknown을 가른다 | `RepositoryEncodingTests` | 불필요 |
| `.gitattributes` 내용이 `*.sql -text`를 담는다 | `RepositoryEncodingTests` | 불필요 |
| 이미 있는 `.gitattributes`를 덮어쓰지 않는다 | `RepositoryEncodingTests` | 불필요 |
| 배너가 `Write`에서만 뜬다 | `ViewChangesViewModelTests` | 불필요 |
| 전환 명령이 `.gitattributes`를 만든 뒤 전체 추출을 부른다 | `ViewChangesViewModelTests` | 불필요 |
| **읽는 자리가 세 인코딩 모두를 옳게 읽는다** (2.1의 전제) | 새 `FileEncodingTests` | 불필요 |

### 4.1 CI가 검증하지 못하는 것

배너가 실제로 뜨는지, 전환 버튼이 도는지, 전환된 저장소를 GitLab이 diff로 그리는지.
`docs/setup-checklist.md`에 0.5.15 절로 수동 절차를 적는다. 특히 **전환 뒤 GitLab MR에서 diff가
실제로 보이는지**가 이 작업의 목적이므로 반드시 눈으로 확인한다.

## 5. 범위 밖

- **과거 커밋의 UTF-16 블롭은 그대로 둔다.** 전환 이전 이력의 diff는 계속 바이너리로 보인다.
  `git filter-repo`로 이력을 다시 쓰면 고칠 수 있지만, 그러면 저장소를 가진 모든 사람이 클론을
  다시 받아야 한다. 목적은 앞으로의 리뷰가 되게 하는 것이지 과거를 되살리는 것이 아니다
- **`SmoManager.HasSameBytes`는 그대로 둔다.** 바이트 비교는 인코딩과 무관하게 옳다.
  전환 직후 모든 파일이 다르게 판정되는 것은 결함이 아니라 이 작업이 의도한 결과다
- **읽는 자리 여섯 곳은 그대로 둔다.** 2.1에서 실측으로 확인했다
- **저장소가 두 인코딩으로 섞인 상태를 부분 복구하지 않는다.** 전환이 중간에 멈추면 다시 누른다.
  전체 추출은 멱등이다
- `ScriptObjectsDetailed`의 실패 침묵 — 원인이 다르다. 별도 작업이다

## 6. 릴리스

`0.5.15`. 스키마 버전(v5)은 바뀌지 않는다 — 데이터베이스는 건드리지 않는 변경이다.

전환 순서를 문서에 절차로 적는다.

1. 한 사람이 0.5.15로 올리고 **전환하기**를 누른다
2. 전 파일이 `수정`으로 뜨면 커밋하고 Push한다
3. 나머지 인원은 0.5.15로 올린 뒤 **Pull만 한다.** 전환 버튼을 누르지 않는다
4. Pull하면 배너가 사라진다(파일이 이미 UTF-8이므로 `Detect`가 `Current`를 낸다)
5. 배포·감사 클론도 Pull만 하면 된다. 애초에 배너가 뜨지 않는다
