# 개발 클론 브랜치 정책 정정 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 배포 문서 셋과 설득 자료에서 "개발 클론은 `develop`에 둔다" 규칙을 철회하고, 그 자리에 실제 위험(공용 DB의 통짜 스냅샷 때문에 남의 변경이 딸려 오는 것)과 도구가 알리는 범위를 적는다.

**Architecture:** 코드 변경이 없는 문서 작업이다. 파일 넷을 고치고 그중 하나(설득 자료)는 게시된 아티팩트와 같은 URL로 다시 게시한다. 각 작업은 `grep`으로 검증 가능한 상태 변화를 남기고, 마지막 작업이 네 문서의 상호 일관성을 확인한다.

**Tech Stack:** Markdown, HTML(설득 자료), Artifact 도구(재게시), git

**Spec:** `docs/superpowers/specs/2026-09-09-dbvc-branch-policy-correction-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.**
- 문체는 기존 문서를 따른다 — 한국어 평서문, "왜"를 남긴다, 단정할 수 없는 것은 단정하지 않는다.
- 커밋 메시지는 한국어 명령형 현재시제 + 스코프: `docs(공지): …`
- 커밋 메시지 끝에 다음 두 줄을 붙인다:
  ```
  Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
  ```
- **`README.md`는 고치지 않는다.** `:213`이 이미 `dev` 클론을 "없음 — 자유롭게 전환"으로 적고 있다. 고쳐야 할 것이 생겼다면 이 계획의 방향이 틀린 것이므로 멈추고 사람에게 묻는다.
- **코드는 건드리지 않는다.** 고정 브랜치 칸은 그대로 선택으로 둔다. `RepositoryStateEvaluator`도 그대로 둔다.
- 줄바꿈은 기존 문서와 같이 100자 안팎에서 접는다.

---

### Task 1: 배포 공지의 브랜치 규칙을 교체한다

전원이 읽는 문서이고 회의보다 먼저 나가므로 가장 먼저 고친다.

**Files:**
- Modify: `docs/rollout-announcement.md` (머리말 `:3-5`, 요약 4번 `:27`, 2절 `:100-141`, 릴리스 노트 템플릿 `:169-172`)

**Interfaces:**
- Produces: 2절의 새 제목 `## 2. 공용 개발 DB에서 커밋할 때` — Task 2와 Task 3이 이 절을 링크로 가리킨다.

- [ ] **Step 1: 현재 상태를 확인한다**

Run: `grep -n "develop.*둔다\|develop.*고정한다\|갈아탄 채" docs/rollout-announcement.md`
Expected: **4줄** — `:27`(요약), `:100`(2절 제목), `:131`(왜 위험한가), `:170`(릴리스 템플릿).
실측값이다. 다르면 파일이 이미 손대진 것이므로 멈추고 알린다.

- [ ] **Step 2: 머리말을 고친다**

`:3-5`에서 이 문장을 찾는다:

```
그리고 개발 클론이 **어느 브랜치에 있어야 하는지** — 도구가 대신 정해 주지 않는 두 가지를
```

이렇게 바꾼다:

```
그리고 공용 개발 DB에서 커밋할 때 **무엇을 조심해야 하는지** — 도구가 대신 정해 주지 않는
두 가지를
```

- [ ] **Step 3: "지금 할 일" 4번을 고친다**

`:27`의 이 줄을:

```
4. 개발 클론은 **`develop`에 둔다.** 브랜치를 갈아탄 채 DBVC를 쓰지 않는다
```

이렇게 바꾼다:

```
4. 개발 클론의 **브랜치는 자유다.** 커밋할 때 경고가 뜨면 **멈추고 확인한다**
```

- [ ] **Step 4: 2절을 통째로 교체한다**

`## 2. 개발 클론은 `develop`에 고정한다`(`:100`)부터 `### 왜 어긋나면 위험한가` 절의 끝(`:141`, `---` 직전)까지를 아래로 교체한다.

````markdown
## 2. 공용 개발 DB에서 커밋할 때

### 규칙

**개발 클론의 브랜치는 자유다.** `feature/*`든 `hotfix/*`든 원하는 브랜치를 만들어 거기서
커밋하고, 테스트에 올릴 때 `develop`에 병합한다. 저장소를 연결할 때 **고정 브랜치 칸은
비운다** — 채우면 그 브랜치를 벗어난 동안 DBVC 창이 조회조차 되지 않아 이 흐름 자체가 막힌다.

**커밋할 때 "이 객체는 …에서도 변경했습니다"가 뜨면 멈추고 확인한다.** 같은 객체를 남도
만졌다는 뜻이고, 지금 커밋에 그 사람의 작업이 함께 담긴다는 뜻이다. 무엇을 하기로 할지는
팀이 정한다 —
[`setup-checklist.md`의 "조직이 정해야 할 운영 규칙"](setup-checklist.md#조직이-정해야-할-운영-규칙)에 있다.

지금 어느 브랜치인지는 도구 창 오른쪽 위, 버전 왼쪽의 `브랜치: …` 에서 본다.

### 고정 브랜치 칸은 비운다

배포·감사 클론은 **고정 브랜치가 필수**라 도구가 강제한다 — 어긋나면 화면을 덮고 동작을
막는다. 개발 클론은 그 칸이 **선택**이고, 우리는 **비운다.**

채우면 개발 클론도 똑같이 막히는데, 그 대가가 위 규칙과 양립하지 않는다 — `feature/x`에 있는
동안 DBVC 창을 **조회조차 할 수 없다.** 그러면 그 브랜치에 DB 변경을 담을 수 없고, "브랜치에서
커밋하고 `develop`에 병합한다"가 성립하지 않는다.

**칸을 비워도 도구가 손을 놓는 것은 아니다.** detached HEAD와 끝나지 않은 병합은 고정 여부와
무관하게 그대로 막는다 — 그 상태에서 커밋하면 어느 브랜치에도 남지 않거나 중간 상태가 저장소에
들어간다.

**도구에 브랜치 전환 기능은 넣지 않는다** — 넣으면 미커밋 변경이 있는 채로 갈아타는 사고를
도구가 거들게 된다. 전환은 외부 Git 클라이언트의 몫이다.

### 왜 남의 변경이 딸려 오는가

**공용 개발 DB가 하나뿐이기 때문이다.**

DBVC가 저장소에 쓰는 `.sql`은 그 객체의 **통짜 스냅샷**이다. 일부만 떼어 낼 수 없다. 그런데
DB는 하나뿐이므로, 프로시저 `P`를 남이 먼저 고쳤고 내가 이어서 고쳤다면 DB의 `P`에는 둘이 함께
들어 있다. 내가 추출해 커밋하면 **그 사람의 작업도 같이 담긴다.**

목록 자체는 걱정하지 않아도 된다 — 변경 로그에서 **내가 만진 객체만** 걸러 오므로 남이 만진
다른 객체가 내 목록에 오르지는 않는다. 문제는 **둘 다 만진 같은 객체** 하나이고, 도구는 커밋
직전에 그 객체와 그 사람을 짚어 알린다.

**딸려 온 것이 어디로 가느냐가 갈린다.** `feature/*`는 `develop`에 병합되므로 `develop`에서
온 것이 `develop`으로 돌아간다 — 자정된다. `hotfix/*`는 `master`로 가므로 아직 승격되지 않은
변경이 운영에 일찍 도착할 수 있다. 도구는 **이 경우를 아직 알리지 않는다.** 운영 배포
스크립트를 DBA가 읽는 것이 현재의 관문이다.

브랜치를 오래 들고 있는 것이 위험한 이유도 이것이다. 오래 들고 있을수록 같은 객체를 남이 만질
확률과 섞이는 양이 함께 커진다. **코드는 브랜치를 몇 주 들고 있어도 되지만 DB 변경은 그렇지
않다 — DB는 이미 공유되어 있다.** `feature/*`에서 DB를 고쳤으면 빨리 `develop`에 병합한다.
````

- [ ] **Step 5: 릴리스 노트 템플릿의 `### 브랜치`를 고친다**

`:169-172`의 이 블록을:

```
### 브랜치
개발 클론은 `develop`에 둡니다. 브랜치를 갈아탄 채 DBVC를 쓰면 공용 DB와
파일의 기준이 어긋나 비교 결과가 조용히 거짓이 됩니다.
자세한 내용은 `docs/rollout-announcement.md`.
```

이렇게 바꾼다:

```
### 브랜치
개발 클론의 브랜치는 자유입니다. 커밋할 때 "이 객체는 …에서도 변경했습니다"가
뜨면 멈추고 확인하세요 — 그 사람의 작업이 함께 담깁니다.
자세한 내용은 `docs/rollout-announcement.md` 2절.
```

- [ ] **Step 6: 옛 규칙이 남지 않았는지 확인한다**

Run: `grep -n "develop.*둔다\|develop.*고정한다\|갈아탄 채\|내가 지운 것처럼" docs/rollout-announcement.md`
Expected: 아무것도 나오지 않는다 (exit code 1).

Run: `grep -c "브랜치 전환 기능은 넣지 않는다" docs/rollout-announcement.md`
Expected: `1` — 유지하기로 한 문장이 살아 있다.

- [ ] **Step 7: 커밋**

```bash
git add docs/rollout-announcement.md
git commit -F - <<'EOF'
docs(공지): 개발 클론의 브랜치 고정 규칙을 철회한다

"develop에 둔다"가 브랜치를 자유롭게 만들어 커밋하고 develop에 병합하는 흐름을
막는다. 그 자리에 진짜 위험을 적는다 - 추출한 .sql이 통짜 스냅샷이라 같은 객체를
남도 만졌으면 딸려 오고, feature는 develop으로 돌아가 자정되지만 hotfix는 master로
새어 나간다. 후자는 도구가 아직 알리지 않는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 2: 설치 체크리스트의 규칙 요약과 hotfix 절을 고친다

**Files:**
- Modify: `docs/setup-checklist.md` (`:956` 규칙 요약, `:960` 보강, `:967-974` hotfix 절)

**Interfaces:**
- Consumes: Task 1이 만든 `rollout-announcement.md` 2절 제목.
- Produces: hotfix가 결정 항목이 아니라 정해진 규칙이라는 서술 — Task 4가 설득 자료에서 결정 항목을 지울 때 이것을 근거로 삼는다.

- [ ] **Step 1: 현재 상태를 확인한다**

Run: `grep -n "develop.*고정한다\|hotfix" docs/setup-checklist.md`
Expected: `:956`의 고정 규칙과 `:967` 이하 hotfix 절이 보인다.

- [ ] **Step 2: 규칙 요약 문장을 고친다**

`:956`의 이 부분을:

```
프로젝트 릴리스 하나뿐이고, 갱신은 5영업일 기한부 필수이며, **개발 클론은 `develop`에 고정한다.**
```

이렇게 바꾼다:

```
프로젝트 릴리스 하나뿐이고, 갱신은 5영업일 기한부 필수이며, **개발 클론의 브랜치는 자유이되
커밋 시 경고가 뜨면 멈추고 확인한다.**
```

- [ ] **Step 3: "DB 변경은 짧게 산다"에 한 문장을 보탠다**

`:960` 문단의 마지막 문장 `**DB는 이미 공유되어 있다.**` 뒤에 이어서 적는다:

```
브랜치가 자유인 이상 이것이 실질 안전장치다.
```

- [ ] **Step 4: hotfix 절을 교체한다**

`**`hotfix/*`의 DB 변경을 어떻게 할지 정한다.**` 로 시작하는 문단부터 번호 목록 세 줄까지(`:967-974`)를 아래로 교체한다.

````markdown
**`hotfix/*`도 `feature/*`와 같은 정책이다.** 브랜치를 자유롭게 만들어 커밋하고 병합한다.
따로 절차를 두지 않는 이유는 대안이 더 나쁘기 때문이다 — 공용 개발 DB가 하나뿐이라 hotfix용
DB 변경을 만들 다른 장소가 없고, 운영 백업을 복원한 별도 DB는 하필 **긴급 경로에** 리드타임을
붙인다.

**대신 잔여 위험을 알고 있어야 한다.** `hotfix/*`는 `master`로 직행하므로, 딸려 온 미승격
변경이 `feature/*`처럼 자정되지 않고 운영에 일찍 도착할 수 있다. DBVC는 **이 경우를 아직
알리지 않는다** — 남의 *미커밋* 작업은 커밋 전에 알리지만, 이미 커밋된 *미승격* 변경은 알리지
못한다. 현재의 관문은 하나다: **배포 스크립트를 DBA가 읽는 것.** 그 구멍을 닫는 항목은
[`team-rollout-backlog.md`](team-rollout-backlog.md)의 "11. 미승격 변경 경고(경고 A)"다.
````

- [ ] **Step 5: 확인한다**

Run: `grep -n "develop.*고정한다\|운영 백업을 복원한 별도 DB\*\*에서 만든다" docs/setup-checklist.md`
Expected: 아무것도 나오지 않는다.

Run: `grep -c '와 같은 정책이다' docs/setup-checklist.md`
Expected: `1`

> 백틱이 든 문자열은 큰따옴표로 감싸지 않는다 — bash가 명령 치환을 해 버린다. 백틱을 아예 피해
> 백틱 없는 부분 문자열로 확인한다.

- [ ] **Step 6: 커밋**

```bash
git add docs/setup-checklist.md
git commit -F - <<'EOF'
docs(체크리스트): hotfix를 feature와 같은 정책으로 정하고 잔여 위험을 적는다

세 선택지를 놓고 고르던 것을 닫는다. 공용 개발 DB가 하나뿐이라 hotfix용 DB 변경을
만들 다른 장소가 없고, 백업 복원은 하필 긴급 경로에 리드타임을 붙인다. 대신 미승격
변경이 운영에 일찍 도착할 수 있다는 것과, 도구가 그것을 아직 알리지 않는다는 것을
남긴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 3: 백로그에 경고 A를 올리고 브랜치 항목의 이력을 고친다

**Files:**
- Modify: `docs/team-rollout-backlog.md` (우선순위 표 `:29`와 새 P2 행, `### 3. 브랜치 전환` 절 `:121-126`, 새 절 `### 11.`)

**Interfaces:**
- Produces: 백로그 항목 제목 `### 11. 미승격 변경 경고(경고 A)` — Task 2와 Task 4가 이 이름으로 가리킨다. 철자를 바꾸지 않는다.

- [ ] **Step 1: 현재 상태를 확인한다**

Run: `grep -n "3번 브랜치 전환\|### 3. 브랜치 전환\|### 9. 클라이언트 버전 보고" docs/team-rollout-backlog.md`
Expected: 세 줄이 나온다.

- [ ] **Step 2: 우선순위 표의 P0 3번 행을 고친다**

`:29`의 이 행을:

```
| ~~P0~~ | ~~3번 브랜치 전환을 정책으로 닫기~~ | 같은 문서 2절. "개발 클론은 `develop` 고정"을 1번과 한 공지에 넣었다 |
```

이렇게 바꾼다:

```
| ~~P0~~ | ~~3번 브랜치 전환을 정책으로 닫기~~ | 같은 문서 2절. 처음엔 "`develop` 고정"으로 닫았으나 그 규칙이 실제 브랜치 흐름을 막아 2026-09-09에 철회했다 — 지금은 "브랜치 자유 + 커밋 경고" |
```

- [ ] **Step 3: 우선순위 표에 경고 A 행을 더한다**

`| **P2** | 9번 클라이언트 버전 보고 | …` 행 **바로 다음 줄**에 삽입한다:

```
| **P2** | 11번 미승격 변경 경고(경고 A) | `hotfix`를 `feature`와 같은 정책으로 둔 이상, 미승격 변경이 운영에 일찍 도착하는 것을 막는 유일한 수단이다. 지금 관문은 DBA가 배포 스크립트를 읽는 것 하나뿐이다 |
```

- [ ] **Step 4: "3. 브랜치 전환" 절을 교체한다**

`### 3. 브랜치 전환 — 정책으로 닫음`(`:121`)부터 그 절의 끝(`### 4. ChangeLog 보존 정책` 직전)까지를 아래로 교체한다.

````markdown
### 3. 브랜치 전환 — 정책으로 닫음

없다. **정책으로 닫았다** — 도구에 브랜치 전환을 넣으면 미커밋 변경이 있는 채로 갈아타는
사고를 도구가 거들게 된다. 전환은 외부 Git 클라이언트의 몫이다.

처음에는 "개발 클론은 `develop` 고정"으로 닫았는데, 그 규칙이 **브랜치를 자유롭게 만들어
커밋하고 `develop`에 병합하는 흐름 자체를 막는다**는 것이 2026-09-09에 드러나 철회했다.
사유와 대체 규칙은
[설계 문서](superpowers/specs/2026-09-09-dbvc-branch-policy-correction-design.md)에 있다.
````

- [ ] **Step 5: 새 절 "11. 미승격 변경 경고(경고 A)"를 더한다**

`### 9. 클라이언트 버전 보고` 절이 끝나는 자리(다음 `## ` 제목 직전)에 삽입한다.

````markdown
### 11. 미승격 변경 경고(경고 A)

[Git 워크플로 설계](superpowers/specs/2026-08-24-dbvc-git-workflow-design.md) 3.10이 정의한
경고 둘 중 하나만 구현되어 있다. **경고 B**(남의 *미커밋* 작업이 딸려 온다)는
`CoAuthorDetector`로 커밋 직전에 뜬다. **경고 A**(이미 커밋된 *미승격* 변경이 딸려 온다 —
`P@develop != P@master`)는 없다.

1차에서 뺀 것은 의도였다 — `develop`과 `master`에 내용이 쌓이기 전에는 판정할 대상이 없다
(같은 문서 7.1). **그 조건은 도입 직후 해소된다.**

`hotfix/*`를 `feature/*`와 같은 정책으로 두기로 한 이상(2026-09-09), 미승격 변경이 운영에
일찍 도착하는 것을 막을 수단은 이것뿐이다. 지금 관문은 DBA가 배포 스크립트를 읽는 것 하나다.

**착수 전에 다시 볼 것.** 설계대로 `P@develop != P@master`로만 판정하면 `feature/*`에서도
매번 뜬다 — 거기서는 딸려 온 변경이 `develop`으로 돌아가 자정되므로 무해한데도 그렇다. 매번
뜨는 경고는 무시된다. 병합 목적지를 판정에 넣을지 정해야 한다.

재료도 아직 없다: `IGitManager.GetFileContentAtHead`가 브랜치 인자를 받지 않는다 — 설계 3.10이
"브랜치 인자를 받도록 넓힌다"고 했던 그것이다.
````

- [ ] **Step 6: 확인한다**

Run: `grep -c "11. 미승격 변경 경고(경고 A)" docs/team-rollout-backlog.md`
Expected: `2` — 우선순위 표의 행 하나와 절 제목 하나.

Run: `grep -c '을 1번과 한 공지에 넣었다' docs/team-rollout-backlog.md`
Expected: `0`

- [ ] **Step 7: 커밋**

```bash
git add docs/team-rollout-backlog.md
git commit -F - <<'EOF'
docs(백로그): 미승격 변경 경고를 P2로 올리고 브랜치 항목의 이력을 남긴다

경고 A가 설계에만 있고 계획에도 백로그에도 없었다. hotfix를 feature와 같은
정책으로 둔 이상 그 구멍을 닫는 유일한 수단이므로 항목으로 세운다. 설계대로
만들면 feature에서도 매번 떠서 무시된다는 주의사항을 함께 적는다.

3번 항목에는 develop 고정으로 닫았다가 철회한 이력을 남긴다 - 남기지 않으면
다음 사람이 같은 규칙을 다시 만든다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 4: 설득 자료를 고치고 같은 URL로 다시 게시한다

게시된 아티팩트와 저장소 파일이 갈라지면 안 되므로 수정과 재게시가 한 작업이다.

**Files:**
- Modify: `docs/why-db-version-control.html` (구조 절 문단, 한계 목록 2곳, 확정 규칙 1곳, 결정 항목 2개 삭제)

**Interfaces:**
- Consumes: Task 3이 만든 백로그 절 제목은 여기서 참조하지 않는다(설득 자료는 저장소 내부 문서를 본문에서 링크하지 않고 꼬리말에서만 가리킨다).

**아티팩트 URL:** `https://claude.ai/code/artifact/c0a8e030-cea9-4d79-93cc-0e27bbdcd1cf`

- [ ] **Step 0: 고치기 전 `<li>` 수를 세어 적어 둔다**

Run: `grep -c "<li>" docs/why-db-version-control.html`

**2026-09-09 기준 실측값은 `37`이다.** 다르면 파일이 이미 손대진 것이므로 멈추고 알린다.
이 수를 적어 둔다. Step 6에서 **이 수보다 1 적어야** 한다 — 한계 항목 하나가 늘고(Step 4)
결정 항목 둘이 줄기(Step 5) 때문이다.

- [ ] **Step 1: 확정 규칙을 교체한다**

`<b>DBVC를 누를 때는 <code>develop</code>에 있는다</b>` 로 시작하는 `<li>` 전체를 찾는다:

```html
            <li>
              <b>DBVC를 누를 때는 <code>develop</code>에 있는다</b>
              <code>feature/*</code> 이름은 자유롭게 만들고 코드 작업도 거기서 한다.
              다만 [새로고침]·[커밋]은 <code>develop</code>으로 돌아와서 누른다.
              브랜치 전환·병합은 외부 Git 클라이언트의 몫이다.
            </li>
```

이렇게 바꾼다:

```html
            <li>
              <b>브랜치는 자유다 — 경고가 뜨면 멈춘다</b>
              <code>feature/*</code>든 <code>hotfix/*</code>든 원하는 브랜치에서 커밋하고
              테스트에 올릴 때 <code>develop</code>에 병합한다. 다만 커밋할 때
              "이 객체는 …에서도 변경했습니다"가 뜨면 멈추고 확인한다.
              브랜치 전환·병합은 외부 Git 클라이언트의 몫이다.
            </li>
```

- [ ] **Step 2: 구조 절의 문단을 다시 쓴다**

`개발 클론이 <code>develop</code>에 있어야 하는 이유도 같다.` 로 시작하는 `<p>` 전체를 찾아 아래로 교체한다.

```html
        <p>
          개발 클론의 브랜치가 자유로울 수 있는 이유도 여기 있다. DBVC가 저장소에 쓰는
          <code>.sql</code>은 그 객체의 <em>통짜 스냅샷</em>이라 일부만 떼어 낼 수 없는데,
          공용 개발 DB는 하나뿐이다. 그래서 프로시저를 남이 먼저 고치고 내가 이어서 고쳤다면
          내 커밋에 <strong>그 사람의 작업도 같이 담긴다.</strong> 도구는 커밋 직전에 그 객체와
          그 사람을 짚어 알린다. 그리고 <strong>딸려 온 것이 어디로 가느냐가 갈린다</strong> —
          <code>feature/*</code>는 <code>develop</code>에 병합되므로 <code>develop</code>에서
          온 것이 <code>develop</code>으로 돌아가 자정되지만, <code>hotfix/*</code>는
          <code>master</code>로 간다.
        </p>
```

- [ ] **Step 3: 한계 목록의 브랜치 항목을 교체한다**

`<strong>개발 클론의 브랜치는 기본값이 검사하지 않는다 — 켤 수는 있다.</strong>` 로 시작하는 `<li>` 전체를 아래로 교체한다.

```html
        <li>
          <strong>같은 객체를 남도 만졌으면 그 변경이 내 커밋에 딸려 온다.</strong>
          저장소에 쓰는 <code>.sql</code>이 객체의 통짜 스냅샷이고 공용 DB는 하나뿐이라,
          일부만 떼어 낼 방법이 없다. 도구는 커밋 직전에 <em>누가 언제 그 객체를 만졌는지</em>
          짚어 알리지만 <strong>막지는 않는다</strong> — 대부분은 이어서 작업한 정상적인
          경우이고, 막으면 사람들이 도구를 쓰지 않게 되기 때문이다. 알림을 받았을 때 무엇을
          하기로 할지는 아래 결정 항목에 있다.
        </li>
```

- [ ] **Step 4: 한계 목록에 hotfix 항목을 더한다**

Step 3에서 만든 `<li>` **바로 다음**, 오버레이 항목 `<li>` **앞**에 삽입한다.

```html
        <li>
          <strong><code>hotfix</code>로 나가는 DB 변경은 자정되지 않는다.</strong>
          <code>feature/*</code>에 딸려 들어온 변경은 <code>develop</code>으로 돌아가므로
          결과가 남지 않지만, <code>hotfix/*</code>는 <code>master</code>로 직행한다.
          아직 승격되지 않은 변경이 운영에 일찍 도착할 수 있고, <strong>도구는 이 경우를 아직
          알리지 않는다.</strong> 지금의 관문은 하나다 — 배포 스크립트를 DBA가 읽는 것.
        </li>
```

- [ ] **Step 4b: "DB 변경은 짧게 산다" 확정 규칙에 한 문장을 보탠다**

확정 규칙 목록에서 `<b>DB 변경은 짧게 산다</b>` 를 품은 `<li>` 를 찾아, 마지막 문장
`코드와 달리 DB는 이미 공유되어 있다.` 뒤에 이어 적는다:

```html
              브랜치가 자유인 이상 이것이 실질 안전장치다.
```

- [ ] **Step 5: 결정 항목 두 개를 지운다**

`<b>개발 클론의 브랜치를 도구가 강제하게 할 것인가` 로 시작하는 `<li>` 전체와, `<b><code>hotfix/*</code>의 DB 변경을 어떻게 할지` 로 시작하는 `<li>` 전체를 각각 통째로 삭제한다. 각 `<li>`는 안에 `<span class="record">` 블록을 품고 있으므로 대응하는 `</li>`까지 지운다.

- [ ] **Step 6: 남은 참조가 없는지 확인한다**

Run: `grep -n "develop</code>으로 돌아와서\|기본값이 검사하지 않는다\|hotfix/\*</code>의 DB 변경을 어떻게" docs/why-db-version-control.html`
Expected: 아무것도 나오지 않는다.

Run: `grep -c "<li>" docs/why-db-version-control.html`
Expected: **Step 0에서 적어 둔 수보다 정확히 1 적다.**

> 맞지 않으면 `<li>` 를 잘못 지웠거나 남겼다는 뜻이다. 멈추고 `git diff`를 읽는다.

- [ ] **Step 7: 게시본을 읽는다**

Artifact 도구를 `action: "read"`, `url: https://claude.ai/code/artifact/c0a8e030-cea9-4d79-93cc-0e27bbdcd1cf` 로 부른다. 결과가 저장 파일 경로를 주면 **그 파일을 전부 읽는다** — 읽지 않으면 게시가 거부된다.

- [ ] **Step 8: 같은 URL로 다시 게시한다**

Artifact 도구를 아래로 부른다:
- `file_path`: `D:\git-root\dbvc\docs\why-db-version-control.html`
- `url`: `https://claude.ai/code/artifact/c0a8e030-cea9-4d79-93cc-0e27bbdcd1cf`
- `label`: `브랜치 정책 정정`
- `favicon`은 넘기지 않는다 (기존 아이콘 유지).

Expected: `Published … at https://claude.ai/code/artifact/c0a8e030-…` — 새 URL이 생기면 잘못된 것이다.

- [ ] **Step 9: 커밋**

```bash
git add docs/why-db-version-control.html
git commit -F - <<'EOF'
docs(설득자료): 브랜치 규칙을 철회하고 hotfix 잔여 위험을 한계에 적는다

확정 규칙이 "develop으로 돌아와서 누른다"고 말하고 있었는데 그것이 팀의 브랜치
흐름을 막는다. 브랜치 자유 + 커밋 경고로 바꾼다. 결정 항목 둘(브랜치 강제,
hotfix 절차)은 답이 정해졌으므로 지우고, hotfix가 자정되지 않는다는 사실과
도구가 그것을 아직 알리지 않는다는 사실을 한계 목록으로 옮긴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 5: 네 문서의 일관성을 확인한다

앞의 넷이 모두 끝나야 돌 수 있는 검사다. 코드가 없으므로 이것이 이 계획의 테스트다.

**Files:**
- Modify: 검사에서 발견된 것만. 없으면 수정 없음.

- [ ] **Step 1: 옛 규칙이 어느 표현으로도 남지 않았는지 확인한다**

```bash
grep -rn "develop.*둔다\|develop.*고정한다\|갈아탄 채\|develop으로 돌아와" \
  docs/rollout-announcement.md docs/setup-checklist.md \
  docs/why-db-version-control.html docs/team-rollout-backlog.md
```

Expected: 아무것도 나오지 않는다 (exit code 1).

> 백로그의 P0 3번 행에는 `"develop` 고정"이 **이력으로** 남아 있다. 그 행이 걸리면 정상이다 — 문장이 "철회했다"를 포함하는지 눈으로 확인한다.

- [ ] **Step 2: 새 규칙이 네 문서에 모두 있는지 확인한다**

```bash
grep -l "브랜치는 자유" docs/rollout-announcement.md docs/setup-checklist.md docs/why-db-version-control.html
```

Expected: 세 파일이 모두 나온다. (백로그는 규칙을 서술하는 문서가 아니라 제외.)

- [ ] **Step 3: 삭제한 결정 항목을 가리키는 문장이 없는지 확인한다**

```bash
grep -n "아래 결정 항목에 있다" docs/why-db-version-control.html
```

Expected: **1건**만 나온다 — Step 3에서 새로 쓴 한계 항목이 "같은 객체를 둘이 만질 때 어떻게 조율하는가" 항목을 가리키는 것이다. 그 결정 항목이 실제로 남아 있는지 함께 확인한다:

```bash
grep -c "같은 객체를 둘이 만질 때 어떻게 조율하는가" docs/why-db-version-control.html
```

Expected: `1`

- [ ] **Step 4: 백로그 상호 참조를 확인한다**

```bash
grep -n "11. 미승격 변경 경고" docs/setup-checklist.md docs/team-rollout-backlog.md
```

Expected: `setup-checklist.md` 1건(Task 2가 건 링크), `team-rollout-backlog.md` 2건(표 행 + 절 제목). 제목 철자가 셋 다 같은지 본다.

- [ ] **Step 5: README가 바뀌지 않았는지 확인한다**

```bash
git log --oneline --since="1 day ago" -- README.md
```

Expected: 아무것도 나오지 않는다. 나오면 이 계획의 방향이 틀렸다는 신호이므로 멈추고 사람에게 묻는다.

`README.md:213`이 여전히 `dev` 클론을 "없음 — 자유롭게 전환"으로 적고 있는지도 눈으로 본다:

```bash
grep -n "자유롭게 전환" README.md
```

Expected: 1건.

- [ ] **Step 6: 게시본과 저장소 파일이 같은지 확인한다**

Artifact 도구 `action: "read"` 로 아티팩트를 읽고, 저장된 파일과 `docs/why-db-version-control.html`을 비교한다:

```bash
diff <(tail -n +2 "<저장된 경로>") docs/why-db-version-control.html
```

Expected: 마지막 두 줄(`</body></html>` 래퍼)만 차이로 나온다. 본문 차이가 있으면 재게시가 반영되지 않은 것이다.

- [ ] **Step 7: 발견된 것이 있으면 고치고 커밋한다**

없으면 이 단계를 건너뛴다. 있으면:

```bash
git add -A
git commit -F - <<'EOF'
docs: 브랜치 정책 정정 후 남은 상호 참조를 맞춘다

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```
