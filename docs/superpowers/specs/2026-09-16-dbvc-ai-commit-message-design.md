# AI 커밋 메시지 생성 설계 — 빈칸 앞에서 멈추지 않는다

> **성격: 기능 추가.** `DBVC.Core`에 AI 설정 저장소와 OpenAI 호환 클라이언트가 생기고,
> `IGitManager`에 unified diff 하나가 붙는다. `DBVC.Vsix`에는 버튼 하나와 옵션 페이지
> 하나가 는다. **커밋 경로 자체는 바뀌지 않는다.**

## 1. 문제

지금 커밋 메시지는 `ViewChangesControl.xaml`의 TextBox에 손으로 적는다. 그리고 그 칸이
비어 있으면 `CanCommit`이 false라 Commit 버튼이 눌리지 않는다.

DB 변경 하나를 커밋하려면 사용자는 방금 자기가 프로시저에 무엇을 했는지 문장으로 옮겨야
한다. 객체 열 개를 한 번에 커밋하는 날에는 그 열 개를 아우르는 한 줄을 지어내야 한다.
실제로 일어나는 일은 정해져 있다 — `수정`, `sp 변경`, `1` 같은 커밋이 이력에 쌓인다.

**이력이 쓸모없어지면 이 도구의 존재 이유가 절반 사라진다.** DBVC는 "누가 언제 무엇을
바꿨는가"에 답하려고 만든 물건이고, 그 답의 절반은 커밋 메시지가 낸다. `수정`이 200개
쌓인 이력은 `git log`를 여는 사람에게 아무것도 말해 주지 않는다.

### 1.1 규칙을 문서로 정해도 지켜지지 않는다

"커밋 메시지는 이렇게 적으세요"를 문서에 적는 방법은 이미 여러 번 시도되는 것이고, 매번
같은 자리에서 진다. 사람이 빈칸을 마주한 시점에 드는 비용이 문서를 읽은 기억보다 크기
때문이다. **규칙을 지키는 쪽이 지키지 않는 쪽보다 쉬워야 지켜진다.**

버튼 하나로 형식에 맞는 초안이 채워지면, 사용자가 하는 일은 "짓기"에서 "고치기"로 바뀐다.
그 둘의 비용 차이가 이 기능의 전부다.

## 2. 결정

### 2.1 AI는 초안만 만든다. 커밋은 사람이 누른다

`AI 생성` 버튼은 메시지를 만들어 TextBox에 채우고 끝난다. 커밋하지 않는다.

Commit을 누를 때 빈칸이면 자동으로 생성해 그대로 커밋하는 안을 검토했고, 버렸다. 두 가지
이유다. 첫째, **검토되지 않은 문장이 이력에 영구히 남는다** — 커밋 메시지는 고치려면
rebase가 필요하고, 이 팀의 대상은 그것을 하지 않는다. 둘째, 커밋 버튼의 반응 시간에
네트워크 지연이 섞여 들어간다. 지금 커밋은 느릴 때 15초가 걸리는데 거기에 AI 호출 몇 초가
더해지면, 실패했을 때 사용자는 무엇이 실패했는지 구분하지 못한다.

**이 결정의 효과: AI 호출이 실패해도 최대 피해가 "메시지를 직접 적는다"다.** 커밋 경로에
새 실패 지점이 하나도 생기지 않는다.

### 2.2 프로바이더는 OpenAI 호환 엔드포인트 하나로 추상화한다

사내 LLM 서버(vLLM·Ollama·사내 게이트웨이)와 외부 상용 API를 둘 다 허용한다. 개발자마다
쓰는 것이 다를 수 있고, 조직이 나중에 어느 쪽으로 정하든 도구는 그대로 쓰인다.

그러므로 **설계는 더 엄격한 쪽(외부 전송)을 기준으로 한다.** 사내 서버만 상정하고 만들면
누군가 URL만 바꿔 외부로 운영 DDL을 보내는 일이 안전장치 없이 일어난다.

### 2.3 AI가 보는 것은 변경된 줄뿐이다

전송하는 것은 두 가지다.

1. 변경 객체 목록 — `dbo.usp_GetOrder (PROCEDURE, 수정)`
2. 그 객체들의 unified diff — 바뀐 줄과 앞뒤 맥락

전체 DDL은 보내지 않는다. 테이블 3000개 규모에서 전체 DDL은 수천 줄이 되고, 그만큼이
외부로 나간다. **변경된 줄만으로도 "어떤 컬럼을 더했다" 수준의 요약은 나온다** — 목적에
필요한 최소치가 그것이다.

`MaxDiffLines`(기본 400줄)로 상한을 건다. 넘으면 객체별로 균등하게 잘라 `-- (이하 생략)`을
남기되, **객체 목록은 전부 유지한다.** 잘린 diff로도 "무엇이 바뀌었는지"는 말할 수 있어야
하기 때문이다. 이 상한의 목적은 토큰 비용이 아니라 전송량 자체의 한계다.

### 2.4 어디로 나가는지 모른 채 나가지 않는다

새 호스트로 처음 보내기 직전에 한 번 확인받는다.

> 선택한 변경의 SQL diff가 `{호스트}`로 전송됩니다. 계속하시겠습니까?

동의한 호스트를 `consentedHost`에 남기고 다시 묻지 않는다. **URL이 바뀌면 다시 묻는다** —
동의는 "AI를 쓰는 것"이 아니라 "이 목적지로 보내는 것"에 대한 것이기 때문이다.

### 2.5 생성되는 메시지는 한국어 본문 + 영문 접두어, 스코프 없음

`feat: 주문 조회 프로시저에 취소일자 필터를 더한다`

이 저장소의 커밋 규약(CLAUDE.md)과 같은 문체다. 스코프는 쓰지 않는다 — 스키마명을 스코프로
넣을 수 있지만, 여러 스키마가 섞인 커밋에서 무엇을 고를지 규칙이 애매해지고 그 애매함을
AI에게 맡기면 매번 다른 답이 나온다.

접두어 집합은 `feat|fix|refactor|perf|chore`로 고정한다. `docs`·`test`는 스키마 저장소에
해당하는 변경이 없다.

### 2.6 API 키는 DPAPI로 암호화해 별도 파일에 둔다

CLAUDE.md의 "인증 정보는 디스크에 쓰지 않는다"는 **SSMS 개체 탐색기가 제공하는 SQL
자격증명**에 대한 규칙이다. 그 규칙이 성립하는 이유는 값의 출처가 따로 있어 언제든 다시
얻을 수 있다는 것이었다. API 키에는 그런 출처가 없다. 저장하지 않으면 SSMS를 켤 때마다
붙여 넣어야 한다.

그래서 `%APPDATA%\DBVC\ai-settings.json`에 DPAPI(CurrentUser)로 암호화해 둔다. 같은 PC의
다른 계정은 복호화하지 못한다.

**`mappings.json`과 별도 파일인 것이 중요하다.** 매핑은 (서버, DB)마다 다르고 팀원끼리
내용을 주고받는 일이 실제로 있다. 같은 파일에 키가 들어 있으면 그때 딸려 나간다.

## 3. 구성요소

나누는 기준은 **테스트가 볼 수 있는가**다. 네트워크와 Git과 셸을 각각 이음매 뒤로 밀어
내면, 남는 것은 문자열을 다루는 순수 로직이고 그 부분이 가장 자주 틀린다.

### 3.1 `DBVC.Core`

| 이름 | 하는 일 |
| --- | --- |
| `Models/AiSettings` | `BaseUrl`·`Model`·`ApiKey`·`TimeoutSeconds`·`MaxDiffLines`·`ConsentedHost`. 순수 데이터 |
| `IAiSettingsStore` / `AiSettingsStore` | `ai-settings.json` 읽기·쓰기. 키는 `ISecretProtector`를 거친다 |
| `ISecretProtector` / `DpapiSecretProtector` | 문자열 보호·복원. 이 이음매가 있어야 테스트가 진짜 DPAPI 없이 파일 형식을 검증한다 |
| `IChatCompletionClient` / `OpenAiCompatibleClient` | `POST {BaseUrl}/chat/completions`. `HttpClient` + `System.Text.Json`뿐이라 새 패키지가 없다 |
| `CommitMessageComposer` | 프롬프트 조립과 응답 정제. 정적·순수 |
| `IAiCommitMessageGenerator` / `CommitMessageGenerator` | 위의 것들을 엮어 `Generate(server, database, relativePaths, ct)` → 메시지 한 줄 |

**DPAPI 하나만 타깃을 가린다.** `ProtectedData`는 net48에서는 프레임워크의 `System.Security`에
있지만 netstandard2.0에는 없다. Core는 두 타깃을 함께 내므로 조건부로 다뤄야 한다 —
net48은 프레임워크 참조, netstandard2.0은 `System.Security.Cryptography.ProtectedData`
패키지를 **그 타깃에만** 건다. net48 쪽 참조가 바뀌지 않으므로 `IncludeCoreDependenciesInVsix`
목록은 재계산 대상이 아니다(CLAUDE.md의 그 규칙이 걸리는 경우인지 먼저 확인할 것).

`IAiCommitMessageGenerator`는 `Abstractions.cs`에 둔다 — UI를 네트워크 없이 테스트 가능하게
하는 이음매이므로 그 파일의 기존 인터페이스들과 성격이 같다.

`DbvcServices`가 조립한다. 하나의 `AiSettingsStore`를 공유하는 것이 중요하다 — 따로 만들면
옵션 화면에서 저장한 값이 생성기에 보이지 않는다(기존 `ConfigManager`·자격증명 저장소와
같은 이유다).

### 3.2 diff는 `GitManager`가 낸다

`IGitManager.GetUnifiedDiff(serverName, databaseName, relativePaths, maxLines)`를 더한다.
LibGit2Sharp의 `Diff.Compare<Patch>()`로 작업 트리와 HEAD를 비교한다.

새 클래스를 만들지 않는 이유가 둘이다. "Git 저장소를 읽는 일은 `GitManager`가 한다"가 이미
이 저장소의 규칙이고, 화면이 보여 주는 diff(`DiffService`)와 AI가 보는 diff가 **같은
출처**여야 "왜 엉뚱한 요약이 나왔나"를 사용자가 추적할 수 있다.

### 3.3 `DBVC.Vsix`

- `UI/AiOptionsControl.xaml` — URL·모델·API 키(`PasswordBox`)·타임아웃·diff 최대 줄 수,
  그리고 `연결 테스트` 버튼
- `AiOptionPage : UIElementDialogPage` — 위 컨트롤을 담는다
- `DbvcPackage`에 `[ProvideOptionPage(typeof(AiOptionPage), "DBVC", "AI 커밋 메시지", ...)]`
- `ViewChangesViewModel.GenerateCommitMessageCommand`
- `ViewChangesControl.xaml`의 커밋 메시지 TextBox 오른쪽, Commit 앞에 `AI 생성` 버튼

**`DialogPage`가 아니라 `UIElementDialogPage`인 이유.** 기본 `DialogPage`는 PropertyGrid를
그려 API 키가 화면에 평문으로 노출된다. 마스킹하려면 어차피 커스텀 `UITypeEditor`를 써야
하는데, `UIElementDialogPage`면 이 저장소가 이미 쓰는 WPF 패턴 그대로 `PasswordBox`를
붙일 수 있다.

**함정 — `SaveSettingsToStorage`를 반드시 override한다.** 기본 구현은 속성을 VS 설정
저장소(레지스트리)에 **평문으로** 남긴다. `LoadSettingsFromStorage`/`SaveSettingsToStorage`
둘 다 `AiSettingsStore`로만 위임하고 기본 구현을 호출하지 않는다. 이 한 줄을 빠뜨리면
DPAPI 암호화가 통째로 무의미해진다. 그 자리에 이 사유를 주석으로 남긴다.

**`연결 테스트` 버튼이 있는 이유.** URL·모델·키 세 개가 모두 맞아야 동작하는데, 틀렸다는
것을 사용자가 처음 알게 되는 자리가 "커밋하려던 순간"이면 안 된다.

## 4. 흐름

```
AI 생성 클릭
  → 설정 확인 (비었으면 안내하고 끝)
  → 기존 메시지가 있으면 덮어쓰기 확인
  → 새 호스트면 전송 동의 확인 (2.4)
  → IBackgroundScheduler.Run
       GitManager.GetUnifiedDiff → CommitMessageComposer.BuildPrompt
       → OpenAiCompatibleClient → CommitMessageComposer.Clean
  → CommitMessage에 채움
```

HTTP는 수 초가 걸린다. UI 스레드에서 하면 그동안 SSMS 전체가 멈춘다 — 기존
`IBackgroundScheduler`를 그대로 쓴다.

진행 표시는 기존 `IsBusy` + `ProgressText`("AI가 커밋 메시지를 만드는 중...")를 재사용한다.
그동안 다른 버튼이 잠기지만, 별도 잠금 상태를 새로 만드는 것보다 이 저장소의 기존 규칙과
일관된 편이 낫다.

### 4.1 오류

**설정이 비었을 때 버튼을 잠그지 않는다.** 잠긴 버튼은 이유를 말하지 못한다. 누르면
"도구 > 옵션 > DBVC > AI 커밋 메시지에서 프로바이더 URL과 모델을 설정하세요"라고 안내한다.

**기존 메시지가 있으면 `IUserNotifier.Confirm`으로 묻는다.** 손으로 적던 문장이 클릭 한
번에 사라지면 안 된다.

실패는 전부 한국어 사유로 바꾼다 — 연결 실패 / 타임아웃 / 401·403(키가 거부됨) /
404(URL 또는 모델 이름) / 429(호출 한도) / 그 외. 서버가 돌려준 영문 본문은 인용으로만
덧붙인다(CLAUDE.md의 UI 문구 규칙).

**실패해도 `CommitMessage`는 건드리지 않는다.** 빈칸으로 만들면 사용자가 적던 것까지 잃는다.

### 4.2 응답 정제

AI가 코드펜스를 씌우거나, 여러 줄을 뱉거나, 접두어를 빠뜨리는 일은 정상적으로 일어난다.
`CommitMessageComposer.Clean`이 처리한다.

- 코드펜스(```)와 감싼 따옴표·백틱을 벗긴다
- 첫 줄만 취한다
- 접두어가 2.5의 집합에 없으면 `chore:`를 붙인다
- 72자를 넘으면 잘라낸다

system 프롬프트에 형식을 적고 예시를 두어 개 넣는다. 설명만으로는 형식 준수율이 잘 오르지
않는다. 그럼에도 정제 단계를 두는 이유는, **형식 위반이 드물어도 0이 되지는 않기 때문**이다.

## 5. 테스트

`DBVC.Core.Tests`(net48 + net10.0). 실패하는 테스트부터 쓴다.

| 대상 | 무엇을 증언하는가 |
| --- | --- |
| `CommitMessageComposer` | 코드펜스·따옴표·여러 줄 정제, 접두어 없는 응답에 `chore:` 부여, 72자 초과 처리, `MaxDiffLines` 초과 시 잘라내되 객체 목록은 남는지 |
| `AiSettingsStore` | 가짜 `ISecretProtector`로 저장·복원 왕복. 그리고 **저장된 파일 본문에 키 원문이 없다** |
| `OpenAiCompatibleClient` | 가짜 `HttpMessageHandler`로 URL 조합(끝 슬래시 유무), `Authorization: Bearer`, 요청 JSON 형태, 상태 코드별 한국어 예외 |
| `GitManager.GetUnifiedDiff` | 임시 저장소로 검증(기존 `GitManagerTests` 방식) |

`DBVC.Vsix.Tests` — 가짜 생성기로 ViewModel을 검증한다: 성공 시 채움, 실패 시 기존 메시지
보존, 설정 미비 안내, **동의를 거절하면 호출 자체가 일어나지 않음**.

`AiSettingsStore` 테스트가 이 기능에서 가장 값지다. 키가 평문으로 디스크에 닿지 않는다는
것은 눈으로 봐서는 확인되지 않고, 한번 깨지면 조용히 깨진다.

### 5.1 CI가 검증하지 못하는 것

옵션 페이지 등록은 VS 패키지 로딩에 속한다. **SSMS 21에서 도구 > 옵션을 직접 열어 보기
전에는 "동작한다"고 말하지 않는다.** 실제 프로바이더 호출도 마찬가지다.

## 6. 범위 밖

지금 하지 않는다. 필요해지면 얹을 수 있고, 지금 넣으면 검증할 조합만 는다.

- 후보 여러 개를 보여 주고 고르게 하기
- 스트리밍 응답
- 자동 재시도
- 프록시 인증 UI (시스템 프록시 설정을 따른다)
- 커밋 본문(여러 줄) 생성 — 제목 한 줄만 만든다

## 7. 함께 고치는 문서

사용자 눈에 보이는 동작이 바뀌므로 `README.md`와 `docs/setup-checklist.md`에 옵션 설정
절차를 더하고, `src/DBVC.Vsix/source.extension.vsixmanifest`의 버전을 올린다.
