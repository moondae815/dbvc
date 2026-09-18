# 배포 문서 넷의 경계 다시 긋기 — 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `docs/` 아래 네 문서가 각각 한 독자·한 성격만 갖도록 내용을 옮겨, 같은 규칙이 두 곳에
복제되거나 같은 검증 항목의 상태가 두 곳으로 갈리는 일이 구조적으로 불가능하게 만든다.

**Architecture:** 문서 수는 넷 그대로, 파일 이름도 그대로 둔다. 내용만 옮긴다 — 규칙은
`rollout-announcement.md`로, 검증 항목은 `ssms-manual-verification.md`로 모으고,
`setup-checklist.md`는 순수 설치 절차로 줄인다. 검증 항목은 **1부(아직 안 밟은 것) → 2부(밟았고
릴리스마다 다시 보는 것)** 로만 흐르게 해 같은 항목이 두 부에 동시에 존재할 수 없게 한다.

**Tech Stack:** Markdown. 코드 변경 없음. 빌드·테스트 없음 — 검증은 `grep` 기반 구조 검사다.

**Spec:** [`docs/superpowers/specs/2026-09-19-dbvc-docs-restructure-design.md`](../specs/2026-09-19-dbvc-docs-restructure-design.md)

## Global Constraints

- **한국어.** 모든 문서 문장은 한국어다. 기존 문체를 따른다 — 평서문, "왜"를 남기고, 함정을 적는다.
- **문장을 새로 쓰지 않는다.** 이 작업은 **이동**이다. 옮기면서 다시 쓰는 것은 설계가 명시한
  자리(3.2.1의 순서 재배열, 3.4의 압축, 아래 Task 2 Step 6의 사실 정정)뿐이다. 나머지는 원문을
  그대로 옮긴다 — 옮기며 고치면 무엇이 이동이고 무엇이 변경인지 diff에서 갈리지 않는다.
- **커밋 메시지:** 한국어 명령형 현재시제 + 스코프. 예: `docs: 검증 항목을 한 문서로 모은다`
- **커밋 메시지 끝에** `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Bash 도구에서 여러 줄 커밋 메시지는 heredoc으로 쓴다.** PowerShell here-string(`@'...'@`)을
  Bash에 쓰면 메시지 앞뒤에 `@`가 들어간다 — 이 세션에서 실제로 겪었다.
  ```bash
  git commit -F - <<'EOF'
  제목
  
  본문
  EOF
  ```
- **줄 번호 기준은 커밋 `7c95036`이다.** Task 1·2는 `setup-checklist.md`를 **읽기만** 하므로 그
  줄 번호가 끝까지 유효하다. Task 3이 처음으로 그 파일을 고친다.
- **파일 이름을 바꾸지 않는다.** `specs/`와 `plans/`가 지금 이름을 가리킨다.
- **`specs/`·`plans/`·`README.md`·`user-guide.html`·`why-db-version-control.html`은 고치지 않는다.**
  (`README.md`는 Task 5에서 **읽어서 확인만** 한다.)

---

## 파일 구조

| 파일 | 지금 | 뒤 | 책임 |
| --- | --- | --- | --- |
| `docs/ssms-manual-verification.md` | 342줄 | 약 650줄 | 확인 항목의 유일한 자리 |
| `docs/rollout-announcement.md` | 228줄 | 약 340줄 | 규칙의 유일한 자리 |
| `docs/setup-checklist.md` | 1230줄 | 약 730줄 | 절차의 유일한 자리 |
| `docs/team-rollout-backlog.md` | 352줄 | 약 210줄 | 남은 일과 우선순위 |
| `CLAUDE.md` | — | +1줄 | 다음 사람이 어디에 더할지 |

**순서가 중요하다.** 내용을 옮긴 뒤에 원본을 지운다. Task 1·2가 `setup`에서 가져가고, Task 3이
비로소 `setup`을 지운다. 뒤집으면 중간 상태에서 내용을 잃는다.

---

### Task 1: 검증 항목을 한 문서로 모은다

**Files:**
- Modify: `docs/ssms-manual-verification.md` (전면 재구성)
- Read only: `docs/setup-checklist.md` (7단계 457–939, 8단계 병합 절, AI 절)

**Interfaces:**
- Produces: `ssms-manual-verification.md`가 **1부 / 2부 / 3부** 세 절을 갖는다. Task 3이 `setup`에서
  지울 절들의 내용이 전부 여기 들어와 있어야 한다 — Task 3은 이 문서에 그 내용이 있다고 전제하고
  지운다.
- Produces: 구조 불변식 — **2부에는 `- [ ]`(미확인 체크박스)가 하나도 없다.** 2부는 전부
  `- [x] … — YYYY-MM-DD` 다. Task 5의 검증이 이것을 기계적으로 확인한다.

- [ ] **Step 1: 옮겨 올 원본을 모두 읽는다**

세 자리를 읽는다. 읽지 않고 옮기면 절 안의 상호 참조(예: "위 '일상 사용에 필요한 권한'의 조회
쿼리")가 끊긴 채 옮겨진다.

```bash
sed -n '453,939p' docs/setup-checklist.md    # 7단계 전체
sed -n '989,1005p' docs/setup-checklist.md   # 8단계 "배포·감사 클론 병합 (0.8.0)"
sed -n '1052,1088p' docs/setup-checklist.md  # AI 커밋 메시지 설정
```

- [ ] **Step 2: 머리말과 "적는 법"을 고친다**

`ssms-manual-verification.md` 1–20행. 제목은 `# SSMS 수동 확인`으로 바꾼다 — "남은 항목"이 더는
문서 전체가 아니라 1부다.

머리말에 **두 문장을 넣는다.**

```markdown
**여기 없는 것은 어디에.** 설치 절차는 [`setup-checklist.md`](setup-checklist.md), 팀이 지켜야 할
규칙은 [`rollout-announcement.md`](rollout-announcement.md), 남은 일과 우선순위는
[`team-rollout-backlog.md`](team-rollout-backlog.md)에 있다.

**항목은 1부에서 2부로만 흐른다.** 1부는 아직 한 번도 확인하지 않은 것, 2부는 확인해 본 적이
있어 릴리스마다 다시 보는 것이다. 1부에서 밟았으면 날짜를 적고 **2부의 해당 묶음으로 옮긴다** —
양쪽에 두지 않는다. 같은 항목이 두 군데에 있으면 어느 쪽이 사실인지 알 수 없게 되고, 그것이
이 문서와 `setup-checklist.md` 사이에서 실제로 일어났던 일이다.
```

- [ ] **Step 3: 1부를 만든다 — 아직 안 밟은 것만**

지금 문서의 `## 권하는 순서`부터 `## H` 까지를 `## 1부. 지금 남은 것` 아래로 옮긴다.
**밟은 묶음(A · E · D-1 · B-1 · B-2 · G-1~G-5)은 1부에서 들어낸다** — Step 4에서 2부로 간다.

1부에 남는 여덟 묶음과, 각 묶음 머리에 적을 **옮겨 갈 자리**:

| 묶음 | 옮겨 갈 자리 | 이 단계에서 할 일 |
| --- | --- | --- |
| B-3. 변경 로그 정리 실패 안내 (0.5.14) | → 2-4 | **`setup` 609–645를 여기로 가져온다.** DENY/REVOKE 쿼리와 `sysadmin` 함정이 거기 있다. 지금 B-3은 요약 5줄뿐이고 원문을 가리킨다 |
| B-4. 추출물의 한국어 | → 2-2 | 그대로 |
| C-1. 이력 탭 분할선과 병합 안내 (0.5.12·0.5.13) | → 2-7 | **`setup` 646–747의 A~E를 펼쳐 넣는다.** 지금은 "항목이 많아 옮기지 않는다"며 가리키기만 한다. 병합 커밋을 만드는 `git switch`/`merge --no-ff` 절차도 함께 온다 |
| C-2. 개체 탐색기 진입과 목록 | → 2-7 | 그대로 |
| D-2. 차이 검사와 배포 스크립트 | → 2-9 | 그대로 |
| D-3. 연결 | → 2-1 | 그대로 |
| F. 병합 (0.8.0) — F-0~F-4 | → 2-9 | **`setup` 989–1005의 11항목을 흡수한다.** F와 중복이고 F가 더 자세하다. F에 없는 항목만 보탠다 |
| H. 용도 기본값과 `master` 커밋 확인 (0.9.5) | → 2-1 (앞 세 항목) · 2-3 (뒤 두 항목) | 그대로 |

묶음 머리에 이렇게 적는다:

```markdown
### B-4. 추출물의 한국어

> 밟으면 날짜를 적고 **2-2. 추출과 변경 목록**으로 옮긴다.
```

`## 권하는 순서`는 이 여덟 기준으로 다시 쓴다. 지금 본문이 이미 이 순서를 말하고 있으므로
(B-3·B-4 → C → D-2·D-3 → H → F), **G가 다 찼다**는 문장만 걷어내고 나머지는 그대로 둔다.

- [ ] **Step 4: 2부를 만든다 — 11묶음**

`## 2부. 회귀 스위트 (매 릴리스)` 를 만들고 아래 11묶음을 만든다. **모든 항목은 `- [x]`이고 끝에
마지막 확인 날짜가 붙는다.** 날짜를 모르는 항목(`setup`에서 오는 것 중 확인 기록이 없는 것)은
`— 확인 기록 없음`으로 적는다 — 빈 `- [ ]`로 두면 Step 위의 불변식이 깨지고 1부와 구분되지 않는다.

| 묶음 | 어디서 오는가 (커밋 `7c95036` 기준) |
| --- | --- |
| **2-1. 연결과 인증** | `setup` 855–864 (인증) · `setup` 750–755 (0.4.0 중 **저장소 받기** 6항목) |
| **2-2. 추출과 변경 목록** | `setup` 843–854 (0.3.0 추출 형식과 속도) · `setup` 776–797 (0.3.1 이름 변경과 테이블 디자이너) · `setup` 460–461 (테이블 디자이너 `추가`) · `setup` 480 (기본값 제약과 인덱스) |
| **2-3. 작업자 필터와 커밋 전 확인** | `setup` 815–835 (작업자 필터 8항목) · `setup` 836–842 (커밋 전 확인 4항목) |
| **2-4. 변경 로그와 추적기** | `setup` 563–575 (0.5.17 작성자) · `setup` 544–562 (0.5.18 되돌리기) · `setup` 503–530 (0.5.21 무시와 보존) · `setup` 531–543 (0.5.20 `.gitattributes`) · `setup` 759–775 (0.3.1 스키마 v4 업그레이드) · 1부 B-1 · 1부 B-2 |
| **2-5. 스크립트 생성** | `setup` 865–875 |
| **2-6. Pull·Push 안내 문구** | `setup` 876–884 (Pull 성공) · `setup` 885–913 (Pull 실패) · `setup` 914–932 (Push 실패) · `setup` 756–757 (0.4.0 중 **원격 확인** 2항목) |
| **2-7. 이력과 비교 창** | `setup` 933–939 (컨텍스트 메뉴) · `setup` 462–466 (비교 세로·가로 스크롤, 탭 오가며 배경색) |
| **2-8. 저장소 차단과 브랜치** | `setup` 798–814 (0.3.0 브랜치 표시와 저장소 차단 9항목) · 1부 A (0.6.0 브랜치 조작 5항목) |
| **2-9. 배포·감사 클론과 병합** | 1부 D-1 (화면 잠금 6항목) |
| **2-10. 테마와 도구 줄** | 1부 G-1 ~ G-5 · `setup` 483–502 (도구 줄 정리 0.9.0) 중 **G에 없는 항목만** · `setup` 473–479 (좁게 도킹, 진행 표시, 어두운 테마) |
| **2-11. AI 커밋 메시지** | 1부 E (3항목) · `setup` 1078–1088 (AI 절의 검증성 항목 5개 — 목적지 확인 대화상자, 재출현 안 함, 스킴 변경 시 재출현, 덮어쓰기 취소, 실패해도 창이 산다) |

**`setup` 7단계 "기본 흐름"의 두 줄은 어디로도 가지 않는다.**

| 줄 | 처리 |
| --- | --- |
| 468 — 개체 탐색기 우클릭 → **DBVC: 이력 보기** | **뺀다.** 1부 C-2가 같은 것을 더 자세히 본다(다른 서버·DB일 때, 연결 안 됐을 때, 이름 변경 커밋, 방향키 훑기). 3부에 사유와 함께 적는다 |
| 469 — "이력 탭의 분할선과 안내는 아래 **0.5.12** 절에서 따로 확인한다" | **지운다.** 가리키던 절이 이 문서 1부 C-1으로 옮겨 온다. 남기면 `setup`에 없는 절을 가리키게 된다 |

**묶음 이름이 설계와 다른 곳이 하나 있다.** 설계 3.3은 2-4를 "커밋·되돌리기·무시"라 불렀다.
`setup` 759–775(0.3.1 스키마 v4 업그레이드)를 여기 넣기로 하면서 **"변경 로그와 추적기"** 로
바꾼다 — 그 절의 주제는 "추적기 업그레이드가 기존 행을 보존하면서 새 열을 채우는가"이고,
0.5.21의 추적기 업데이트 항목과 같은 것을 본다. 항목을 쪼개 `sp_rename` 건만 2-2로 보내지
않는다. 절 하나가 한 흐름을 이루고 있어 쪼개면 논리가 끊긴다.

**2-10에서 주의할 것.** `setup` 483–502와 1부 G는 같은 화면을 본다. **1부 G를 원본으로 삼는다** —
더 자세하고 날짜가 있다. `setup` 쪽에서는 G에 없는 항목만 보탠다. 반대로 하면 2026-09-18·19에
확인한 날짜를 잃는다.

- [ ] **Step 5: 3부에 줄을 보탠다**

지금의 `## 뺀 항목과 이유` 표를 `## 3부. 뺀 항목과 이유`로 바꾸고 **한 줄을 보탠다.**

```markdown
| `setup-checklist.md` 7단계 "0.5.15 — 저장소 인코딩 전환" 절 전체 | 위 0.5.15 줄과 같은 이유다. 팀 배포 전이라 옛 UTF-16 저장소를 가진 사람이 없고, 개발 저장소는 2026-09-07에 전환을 마쳤다. `setup`에서도 지운다(Task 3) — 남는 정보는 `setup`의 "알려진 제약"에 있는 "전환 이전 커밋의 diff는 바이너리로 남는다" 한 줄이다 |
```

Step 4에서 옮기다 "이미 다른 묶음이 같은 것을 본다"로 판정한 항목이 나오면 같은 표에 사유와 함께
적는다.

- [ ] **Step 6: "다 채운 뒤" 절에서 `setup-checklist.md`를 뺀다**

지금 문장이 결과를 옮길 곳으로 `setup-checklist.md` 체크박스를 든다. 검증 항목이 더는 거기 없다.

```markdown
알려 주면 결과를 `team-rollout-backlog.md` "남은 검증"과 해당 계획서에 옮기고, 실패 항목은
따로 추적한다.
```

- [ ] **Step 7: 구조 불변식을 확인한다**

```bash
cd C:/git-root/dbvc
awk '/^## 2부/,/^## 3부/' docs/ssms-manual-verification.md | grep -c '^- \[ \]'
```
Expected: `0` — 2부에 미확인 체크박스가 하나도 없다.

```bash
grep -c '^## 1부\|^## 2부\|^## 3부' docs/ssms-manual-verification.md
```
Expected: `3`

```bash
grep -n '원문에서 체크한다\|setup-checklist.md" \|항목이 많아' docs/ssms-manual-verification.md
```
Expected: 출력 없음 — `setup`을 가리키며 미루던 문장이 남아 있지 않다.

- [ ] **Step 8: 커밋**

```bash
cd C:/git-root/dbvc
git add docs/ssms-manual-verification.md
git commit -F - <<'EOF'
docs: 검증 항목을 한 문서로 모으고 1부에서 2부로만 흐르게 한다

setup-checklist 7단계와 이 문서가 같은 항목을 나눠 갖고 있었고, 그래서
같은 항목의 확인 상태가 두 군데로 갈렸다. C-1이 "원문에서 체크한다"며
가리키던 setup 쪽 체크박스는 아무도 채우지 않았다.

항목을 전부 이 문서로 가져와 1부(아직 안 밟은 것)와 2부(밟았고 릴리스마다
다시 보는 것)로 나눈다. 1부에서 밟으면 날짜를 적고 2부로 옮기므로 같은
항목이 두 부에 동시에 있는 상태가 생기지 않는다. 2부에 미확인 체크박스가
없다는 것이 그 불변식이다.

원본은 아직 setup-checklist에 남아 있다. 다음 커밋에서 지운다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 2: 운영 규칙을 한 문서로 모은다

**Files:**
- Modify: `docs/rollout-announcement.md` (2절 재구성 + 3절 신설)
- Read only: `docs/setup-checklist.md` 1092–1166 (조직이 정해야 할 운영 규칙)

**Interfaces:**
- Consumes: 없음 (Task 1과 독립이다 — 다른 파일을 만진다)
- Produces: `rollout-announcement.md`가 규칙의 유일한 자리가 된다. Task 3이 `setup` 1092–1166을
  지울 때 그 내용이 여기 있다고 전제한다.

- [ ] **Step 1: 옮겨 올 원본을 읽는다**

```bash
sed -n '1092,1166p' docs/setup-checklist.md
```

- [ ] **Step 2: 머리말에 두 독자와 "여기 없는 것은 어디에"를 적는다**

```markdown
**두 독자를 위한 문서다.** 1·2절은 DBVC를 쓰는 사람 모두가 읽는다. 3절은 팀 리드와 DBA가
읽고 **답을 적는다.**

**여기 없는 것은 어디에.** 설치 절차는 [`setup-checklist.md`](setup-checklist.md), SSMS에서
눌러 보는 확인 항목은 [`ssms-manual-verification.md`](ssms-manual-verification.md), 남은 일과
우선순위는 [`team-rollout-backlog.md`](team-rollout-backlog.md)에 있다.
```

- [ ] **Step 3: 릴리스 담당자 절차의 빌드 명령을 링크로 바꾼다**

지금 두 항목이 `setup` 1단계와 같은 것을 말한다.

```markdown
- [ ] 개발 노트북에서 Release로 빌드하고 **산출물이 실제로 생겼는지 확인한다.** 빌드 성공이
      `.vsix` 생성을 뜻하지 않는다. 명령과 실패했을 때의 갈래는
      [`setup-checklist.md` 1단계](setup-checklist.md)에 있다.
```

(지금의 "개발 노트북에서 Release로 빌드한다" 항목과 "산출물이 실제로 생겼는지 확인한다" 항목,
그 안의 `dir src\DBVC.Vsix\bin\Release\net48\*.vsix` 를 위 한 항목으로 대체한다.)

- [ ] **Step 4: 2절을 아홉 흐름으로 다시 배열한다**

`## 2. 공용 개발 DB에서 커밋할 때` 아래를 아래 순서로 만든다. 1·2·3·8·9는 지금 2절에 있고,
4·5·6·7은 `setup` 1092–1166에서 온다.

1. **브랜치는 자유다. 고정 브랜치 칸은 비운다** — 지금 2절 "규칙" + "고정 브랜치 칸은 비운다"
2. **왜 남의 변경이 딸려 오는가** — 지금 2절 같은 이름 절
3. **`develop` 병합은 무해하고 `master` 병합은 위험하다** — 지금 2절, `feature/*`·`hotfix/*` 구분 없음
4. **DB 변경은 짧게 산다** — `setup`에서
5. **같은 객체에 대한 동시 작업은 조율한다** — `setup`에서
6. **`hotfix/*`도 같은 정책이고, 잔여 위험은 이것이다** — `setup`에서 (대안이 더 나쁜 이유 포함)
7. **`master`에서 커밋하면 도구가 묻는다 (0.9.5)** — `setup`에서
8. **병합은 배포·감사 클론에서 한다** — 주의 3항목. 지금 양쪽에 있으므로 **하나만 남긴다**
9. **브랜치 이름과 커밋 메시지는 지라 티켓 키로 시작한다** — 지금 2절

3과 6이 같은 위험을 두 번 말하게 되므로, **6은 "그래서 `hotfix/*`를 어떻게 다루기로 했는가"와
"현재의 관문"만** 남기고 위험 설명은 3에 둔다.

- [ ] **Step 5: 3절 "조직이 정해야 할 것"을 만든다**

`setup` 1092–1166에서 오되 **아직 답이 없는 것만** 싣는다.

| 항목 | 처리 |
| --- | --- |
| 드리프트 검사(`[차이 검사]`) 주기와 책임자 | 미정 — 답을 적는 칸. `setup`의 예시 문장("테스트는 배포 직후 및 매일 아침, 운영은 매주 월요일 DBA가 확인한다")을 그대로 가져온다 |
| `develop`을 리셋·force-push하는가 | 미정 — 한다면 주기와 절차를 적는 칸 |
| 같은 객체를 둘이 만질 때 어떻게 조율하는가 | 미정 — 2절 5번이 문제를 설명하고 여기가 답을 담는다 |
| 공용 계정의 권한 범위 | 미정 |
| `VIEW DEFINITION`을 어디까지 부여하는가 | 미정. `setup` 5단계의 "설치 스크립트가 부여하지 않는다" 사유를 여기로 함께 가져온다 |
| 한 사람이 한 PC를 쓰는가 | **확인됨(2026-09-10).** 결론과 "깨지면 어떻게 되는가" 두 줄로 줄인다 |

`setup` 1092–1166의 첫 단락(*"`.vsix` 배포·갱신과 브랜치 규칙은 이미 정해져 있다 — rollout…"*)은
**옮기지 않는다.** 이 문서를 가리키는 문장이라 이 문서 안에서는 뜻이 없다.

- [ ] **Step 6: 사실 하나를 바로잡는다**

지금 2절 끝이 이렇게 말한다.

> 도구는 **이 경우를 아직 알리지 않는다.** 운영 배포 스크립트를 DBA가 읽는 것이 현재의 관문이다.

**0.8.0에서 닫혔다.** 백로그 11번(경고 A)이 운영 병합 미리보기의 "확인 필요"로 구현되었다
(`specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md` 3.4). 관문은 이제 둘이다.

```markdown
**도구가 운영 병합 미리보기에서 알린다(0.8.0~).** 아직 운영에 나가지 않은 다른 브랜치와 줄이
겹치면 "확인 필요"가 뜬다 — 막지는 않는 경고다. 그것이 걸러 내지 못하는 것까지 합쳐, 마지막
관문은 여전히 **배포 스크립트를 DBA가 읽는 것**이다.
```

같은 문장이 `setup` 1092–1166에도 있다. 그쪽은 Task 3에서 통째로 지워지므로 따로 고치지 않는다.

- [ ] **Step 7: 릴리스 노트 템플릿은 건드리지 않는다**

복사해 GitLab에 붙이는 것이라 자족해야 한다. 규칙 요약이 본문과 겹치는 것은 **의도된 예외**다.
템플릿 위에 한 줄을 적어 다음 사람이 "중복이네" 하고 지우지 않게 한다.

```markdown
> **아래 템플릿의 규칙 요약이 2절과 겹치는 것은 일부러 그렇다.** 릴리스 노트는 복사되어
> GitLab에 홀로 놓이므로 링크로 대체하면 폐쇄망에서 원문에 닿지 못한다.
```

- [ ] **Step 8: 확인한다**

```bash
cd C:/git-root/dbvc
grep -n '아직 알리지 않는다' docs/rollout-announcement.md
```
Expected: 출력 없음 — Step 6의 정정이 반영됐다.

```bash
grep -n '^## 3\.' docs/rollout-announcement.md
```
Expected: 한 줄 — 3절이 생겼다.

```bash
grep -c '짧게 산다\|같은 객체\|hotfix' docs/rollout-announcement.md
```
Expected: `3` 이상 — `setup`에서 온 규칙 넷(4·5·6·7)이 실제로 들어왔다.

```bash
grep -n 'dir src.DBVC.Vsix' docs/rollout-announcement.md
```
Expected: 출력 없음 — 빌드 명령이 링크로 바뀌었다.

- [ ] **Step 9: 커밋**

```bash
cd C:/git-root/dbvc
git add docs/rollout-announcement.md
git commit -F - <<'EOF'
docs: 운영 규칙을 배포 공지 한 곳으로 모은다

같은 규칙이 이 문서 2절과 setup-checklist "조직이 정해야 할 운영 규칙"에
거의 같은 문장으로 나란히 있었다. 2026-09-09에 develop 고정 규칙을 철회할
때 문서별 변경표를 따로 만들어야 했던 이유가 이 복제다.

setup 쪽 규칙을 2절로 흡수하고, 아직 답이 없는 것만 3절 "조직이 정해야 할
것"으로 세운다. 릴리스 담당자 절차의 빌드 명령은 setup 1단계 링크로 바꾼다.

미승격 변경 경고가 "아직 없다"던 문장도 바로잡는다 — 0.8.0의 운영 병합
미리보기로 닫혔다. 복제가 갈라지면 어떻게 되는지의 실례다.

원본은 아직 setup-checklist에 남아 있다. 다음 커밋에서 지운다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 3: 설치 체크리스트를 절차만 남기고 줄인다

**Files:**
- Modify: `docs/setup-checklist.md`

**Interfaces:**
- Consumes: Task 1이 검증 항목을, Task 2가 운영 규칙을 각각 자기 문서로 옮겨 두었다.
  **이 Task는 지우는 쪽이다.** 먼저 옮겨졌는지 확인하고 지운다.
- Produces: `setup-checklist.md`에 `### 0.` 로 시작하는 버전 라벨 절이 하나도 없다.

- [ ] **Step 1: 옮겨졌는지 먼저 확인한다**

지우기 전에 확인한다. 하나라도 비면 멈추고 Task 1·2로 돌아간다.

```bash
cd C:/git-root/dbvc
grep -c 'DENY UPDATE ON' docs/ssms-manual-verification.md          # 0.5.14가 옮겨졌나
grep -c '안쪽 분할선' docs/ssms-manual-verification.md              # 0.5.12 A절이 옮겨졌나
grep -c 'Tmp_' docs/ssms-manual-verification.md                    # 0.3.1 이름 변경이 옮겨졌나
grep -c '짧게 산다' docs/rollout-announcement.md                    # 운영 규칙이 옮겨졌나
grep -c '드리프트 검사' docs/rollout-announcement.md                # 3절이 생겼나
```
Expected: 다섯 줄 모두 `1` 이상.

- [ ] **Step 2: 7단계를 "설치 확인" 다섯 항목으로 줄인다**

453–939행을 아래로 **통째로 대체한다.**

```markdown
## 7단계 — 설치 확인 (각 기계에서)

설치가 끝났는지만 본다. **기능이 의도대로 도는지 훑는 회귀 목록은
[`ssms-manual-verification.md`](ssms-manual-verification.md) 2부에 있다** — 릴리스마다 밟는
것이라 설치자의 일이 아니다.

- [ ] 저장 프로시저를 하나 `ALTER` 한 뒤 **새로고침** → 목록에 `수정`으로 뜨는지
- [ ] 항목 선택 → 하단 **비교** 탭에 좌(이전)/우(현재) 코드가 보이는지
- [ ] 항목 선택 → **이력** 탭에 그 객체의 커밋(날짜·작성자·메시지·SHA)이 뜨는지
- [ ] 도구 창 **오른쪽 위 버전**이 방금 설치한 `.vsix`의 버전과 같은지
      (`알 수 없음`이거나 `1.0.0`이면 빌드 배선이 끊긴 것이다. 숫자가 이전 버전 그대로면
      설치 관리자가 같은 버전이라며 건너뛴 것이므로, 버전을 올려 다시 빌드하거나 먼저 제거한다)
- [ ] 객체를 `DROP` 한 뒤 **새로고침** → `삭제`로 뜨고, 체크해서 Commit하면 저장소에서도
      파일이 사라지는지
```

- [ ] **Step 3: 8단계에서 병합 체크박스를 지운다**

989–1005행의 `#### 배포·감사 클론 병합 (0.8.0)` 절(머리글 포함 11항목)을 지우고 한 줄로 바꾼다.

```markdown
> **병합 기능(0.8.0)의 확인 항목은** [`ssms-manual-verification.md`](ssms-manual-verification.md)
> 1부 F절에 있다. 원격에 `master`와 운영 클론이 더 있어야 해서 설치와는 준비가 다르다.
```

- [ ] **Step 4: AI 절에서 검증성 항목 다섯을 지운다**

1078–1088행의 다섯 항목(목적지 확인 대화상자 / 재출현 안 함 / 스킴 변경 시 재출현 / 덮어쓰기
취소 / 실패해도 창이 산다)을 지운다. **`ai-settings.json`에 키 원문이 없는지 확인하는 항목
(1073–1077)은 남긴다** — 설정을 마친 그 자리에서 보는 것이다.

절 끝에 한 줄을 남긴다.

```markdown
> AI 기능이 의도대로 도는지(목적지 확인 대화상자, 덮어쓰기 취소, 실패했을 때 창이 살아 있는지)는
> [`ssms-manual-verification.md`](ssms-manual-verification.md) 2-11에 있다.
```

- [ ] **Step 5: "조직이 정해야 할 운영 규칙" 절을 지운다**

1092–1166행을 **통째로 지운다.** Task 2에서 `rollout-announcement.md`로 옮겼다.

- [ ] **Step 6: 0단계에 체크박스 하나를 더한다**

0단계(34행 이후)의 마지막 항목으로 넣는다.

```markdown
- [ ] **[`rollout-announcement.md`](rollout-announcement.md)의 운영 규칙을 팀이 읽었고, 답이
      적혔는지 확인한다.** "채워 넣을 칸" 셋(GitLab 배포 프로젝트·릴리스 담당자·공지 채널)과
      3절 "조직이 정해야 할 것"(차이 검사 주기, `develop` 리셋 정책 등)이 비어 있으면 설치는
      되지만 운영이 사람마다 갈린다. **이것은 설치자가 정하는 것이 아니라 팀이 정하는 것이다** —
      리드 타임이 있으므로 0단계에 둔다.
```

- [ ] **Step 7: 머리말에 "여기 없는 것은 어디에"를 넣는다**

지금 머리말의 *"이 문서는 설치하는 사람의 것이다"* 단락 바로 뒤에 넣는다. 그 선언이 이제
사실이 된다.

```markdown
**여기 없는 것은 어디에.** 팀이 지켜야 할 규칙과 조직이 정해야 할 것은
[`rollout-announcement.md`](rollout-announcement.md), 기능이 의도대로 도는지 훑는 확인 항목은
[`ssms-manual-verification.md`](ssms-manual-verification.md), 남은 일과 우선순위는
[`team-rollout-backlog.md`](team-rollout-backlog.md)에 있다.
```

- [ ] **Step 8: 끊긴 앵커를 고친다**

`setup-checklist.md#조직이-정해야-할-운영-규칙` 을 가리키는 곳이 있다. 그 절이 사라졌다.

```bash
cd C:/git-root/dbvc
grep -rn 'setup-checklist.md#' docs/*.md CLAUDE.md README.md
```

나온 자리를 `rollout-announcement.md`의 해당 절로 바꾼다. (지금 알려진 자리는
`docs/rollout-announcement.md` 2절 안의 한 줄이고, Task 2의 재배열에서 이미 사라졌을 수 있다 —
그래도 다시 확인한다.)

- [ ] **Step 9: 확인한다**

```bash
cd C:/git-root/dbvc
grep -n '^### 0\.' docs/setup-checklist.md
```
Expected: 출력 없음 — 버전 라벨 절이 남아 있지 않다.

```bash
grep -n '^## 7단계\|^## 조직이' docs/setup-checklist.md
```
Expected: `## 7단계 — 설치 확인 (각 기계에서)` 한 줄만. "조직이…" 는 없다.

```bash
wc -l docs/setup-checklist.md
```
Expected: 700~760 사이.

```bash
grep -rn 'setup-checklist.md#' docs/*.md CLAUDE.md README.md
```
Expected: 출력 없음.

- [ ] **Step 10: 커밋**

```bash
cd C:/git-root/dbvc
git add docs/setup-checklist.md docs/rollout-announcement.md
git commit -F - <<'EOF'
docs: 설치 체크리스트를 절차만 남기고 줄인다

머리에 "설치하는 사람의 것"이라 써 놓고 네 독자를 섞고 있었다 — 설치자,
릴리스마다 검증하는 사람, 규칙을 정하는 조직, 문제를 겪는 사람. 1230줄
중 설치 절차는 절반이 안 됐고, 그래서 0.3.0부터 0.9.0까지 모든 릴리스의
검증 항목이 여기 쌓였다.

앞선 두 커밋이 옮겨 간 자리를 지운다. 7단계는 설치가 됐는지만 보는 다섯
항목으로 줄이고, 0단계에 "팀이 규칙을 정했는지" 체크박스를 더한다 —
리드 타임이 있어 나중에 알면 늦다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 4: 백로그를 남은 일만 남기고 정리한다

**Files:**
- Modify: `docs/team-rollout-backlog.md`

**Interfaces:**
- Consumes: Task 1이 만든 검증 문서 (여기서 "남은 검증"이 그쪽을 가리킨다)
- Produces: 없음 (마지막 문서다)

- [ ] **Step 1: 절 순서를 바꾼다**

지금은 릴리스 연대기가 먼저 나오고 전제가 뒤에 있다. 우선순위의 근거가 전제이므로 전제가 앞이다.

| 순서 | 절 | 내용 |
| --- | --- | --- |
| 1 | 머리말 | 이 문서가 무엇인지 + "여기 없는 것은 어디에". **연대기를 걷어낸다** |
| 2 | 팀 환경 | 지금 71–79행. **맨 앞으로 올린다** |
| 3 | 지금 할 일의 순서 | 지금 41–58행. **취소선 친 줄을 지운다** |
| 4 | 남은 항목 | 8·9·12·13번 본문 + 1번의 미결 |
| 5 | 끝난 일 | 표 하나 (Step 3) |
| 6 | 남은 검증 | 한 줄 (Step 4) |

- [ ] **Step 2: 머리말을 다시 쓴다**

```markdown
# 팀 배포 백로그

DBVC를 팀에 공유하기 위해 **남은 일**과 그 순서다. 끝난 일은 아래 "끝난 일" 표에 한 줄씩
있고, 자세한 사유는 각 설계 문서에 있다.

**여기 없는 것은 어디에.** 설치 절차는 [`setup-checklist.md`](setup-checklist.md), 팀이 지켜야
할 규칙은 [`rollout-announcement.md`](rollout-announcement.md), SSMS에서 눌러 보는 확인 항목은
[`ssms-manual-verification.md`](ssms-manual-verification.md)에 있다.
```

지금 머리말(3–40행)의 릴리스 연대기는 Step 3의 표로 간다. **다만 한 문단은 4절로 살린다** —
AI 서버 주소가 빌드에 박혀 있다는 것(*"서버를 옮기면 새 릴리스를 내거나 23명이 각자 옵션을
고쳐야 한다"*)은 끝난 일이 아니라 **아직 살아 있는 제약**이다. 1번 항목 옆에 둔다.

- [ ] **Step 3: "끝난 일" 표를 만든다**

```markdown
## 끝난 일

사유와 설계는 링크에 있다. **한 줄로 줄일 수 없는 것이 나오면 그 항목은 아직 안 끝난 것이다** —
그럴 때는 위 "남은 항목"으로 올린다.

| # | 항목 | 닫힌 자리 | 사유 |
| --- | --- | --- | --- |
| 1 | `.vsix` 배포·갱신 채널 | 2026-09-07 | [`rollout-announcement.md`](rollout-announcement.md) 1절 — GitLab 릴리스 + 5영업일 기한부. **"채워 넣을 칸" 셋은 아직 비어 있다**(남은 항목 참조) |
| 2 | 작업 트리 되돌리기 | 0.5.18 (0.5.19에서 집계 보정) | [설계](superpowers/specs/2026-09-07-dbvc-discard-changes-design.md) |
| 3 | 브랜치 전환 | 정책 | 도구에 넣지 않는다. `develop` 고정으로 닫았다가 2026-09-09에 철회 — [설계](superpowers/specs/2026-09-09-dbvc-branch-policy-correction-design.md) |
| 4 | ChangeLog 보존 정책 | 0.5.21 | `dbo.DBVC_PurgeChangeLog`, 30일 — [설계](superpowers/specs/2026-09-07-dbvc-changelog-retention-and-ignore-design.md) |
| 5 | 미채택자 행 누적 | 0.5.21 | 4번의 보존 정책이 `IsProcessed`를 가리지 않고 지운다 — 같은 설계 |
| 10 | `master` 기준선 절차 | 2026-09-09 | `tools/DBVC.Baseline` — [설계](superpowers/specs/2026-09-09-dbvc-master-baseline-harness-design.md). 절차는 [`setup-checklist.md`](setup-checklist.md) 8단계 |
| 11 | 미승격 변경 경고(경고 A) | 0.8.0 | 운영 병합 미리보기의 줄 단위 겹침 판정 — [설계](superpowers/specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md) 3.4 |
| — | `.sql` diff가 텍스트로 보임 | 2026-09-07 확인 | 전환 이후 커밋 둘 사이로 봐야 한다 |
| — | `.gitattributes` 동작 3항목 | 2026-09-09 확인 | `.gitattributes`가 없는 배포 클론으로 봐야 결론이 난다 |
| — | 기존 저장소의 `.gitattributes` 조사 | 2026-09-09 확인 | 개발 PC의 저장소 셋. **팀의 실제 저장소는 각자 확인해야 한다** |
| — | "무시"와 30일 보존 | 2026-09-09 확인 | 업그레이드 도중 옛 트리거가 DBVC 자신의 DDL을 기록하던 결함을 이 검증이 잡았다 |
```

- [ ] **Step 4: "남은 검증"을 한 줄로 바꾼다**

지금 297–352행을 아래로 대체한다. 완료 기록은 Step 3의 표로 갔다.

```markdown
## 남은 검증

SSMS 21에서 눌러 봐야 하는 것은 [`ssms-manual-verification.md`](ssms-manual-verification.md)에
있다. 1부가 아직 안 밟은 것, 2부가 릴리스마다 훑는 것이다.
```

- [ ] **Step 5: 남은 항목 본문을 추린다**

4절에는 **8·9·12·13번만** 남긴다. 2·3·4·5·6·7·10·11번 본문을 지운다.

**7번(Object Explorer 오버레이)은 예외로 4절에 남긴다.** P3이지만 "끝난 일"이 아니라 "안 하기로
한 일"이고, 보류 사유(펼친 노드만 칠해져 상태를 반쪽만 보여 준다)가 표 한 칸에 들어가지 않는다.
6번(병합 충돌)도 같은 이유로 두 줄로 남긴다.

3절의 우선순위 표에서 취소선(`~~P0~~` 등) 친 줄을 지운다. 남는 것은 P2 둘, P3 넷이다.

- [ ] **Step 6: 확인한다**

```bash
cd C:/git-root/dbvc
grep -c '~~' docs/team-rollout-backlog.md
```
Expected: `0` — 취소선이 남아 있지 않다.

```bash
grep -n '^## ' docs/team-rollout-backlog.md
```
Expected: 팀 환경 / 지금 할 일의 순서 / 배포 전에 막을 것(또는 남은 항목) / 끝난 일 / 남은 검증

```bash
wc -l docs/team-rollout-backlog.md
```
Expected: 190~240 사이.

- [ ] **Step 7: 커밋**

```bash
cd C:/git-root/dbvc
git add docs/team-rollout-backlog.md
git commit -F - <<'EOF'
docs: 백로그를 남은 일만 남기고 끝난 일은 표로 줄인다

남은 일을 보러 온 사람이 릴리스 연대기와 끝난 항목 여섯을 먼저 읽어야
했다. 우선순위의 근거인 팀 환경 전제는 정작 뒤에 있었다.

전제를 맨 앞으로 올리고, 닫힌 항목은 설계 링크가 달린 표 한 줄로 줄인다.
한 줄로 줄일 수 없으면 아직 안 끝난 것이므로 남은 항목으로 올린다는 규칙을
표 위에 적는다. 남은 검증은 검증 문서를 가리키는 한 줄이 된다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 5: 다음 사람이 어디에 더할지 적고, 전체를 검증한다

**Files:**
- Modify: `CLAUDE.md`
- Read only: `README.md`, `docs/*.md`

**Interfaces:**
- Consumes: Task 1~4의 결과 전부

- [ ] **Step 1: `CLAUDE.md`에 한 줄 보탠다**

"작업 방식" 절의 이 항목을 찾는다.

```markdown
- 사용자 눈에 보이는 동작이 바뀌면 `README.md`와 `docs/setup-checklist.md`를 함께 고치고,
  `src/DBVC.Vsix/source.extension.vsixmanifest`의 버전을 올린다.
```

아래로 바꾼다.

```markdown
- 사용자 눈에 보이는 동작이 바뀌면 `README.md`와 `docs/setup-checklist.md`를 함께 고치고,
  `src/DBVC.Vsix/source.extension.vsixmanifest`의 버전을 올린다.
  **SSMS에서 눌러 봐야 하는 확인 항목은 `docs/ssms-manual-verification.md` 1부에 더한다** —
  `setup-checklist.md`에 적지 않는다. 거기는 설치 절차만 담고, 검증 항목을 섞으면
  0.3.0~0.9.0이 그랬듯 다시 쌓인다. 팀이 지켜야 할 **규칙**이 생기면
  `docs/rollout-announcement.md`다.
```

- [ ] **Step 2: 규칙이 한 곳에만 있는지 확인한다**

```bash
cd C:/git-root/dbvc
grep -rn '딸려\|짧게 산다\|MR로만 병합\|고정 브랜치 칸' docs/*.md
```
Expected: **`docs/rollout-announcement.md` 에서만** 나온다.
(`docs/*.md`는 `docs/superpowers/**`와 `.html`을 포함하지 않으므로 설계서와 설득 자료는 걸리지
않는다. 걸리면 그것은 이번 이동이 놓친 자리다.)

- [ ] **Step 3: 검증 항목이 한 곳에만 있는지 확인한다**

```bash
grep -n '^### 0\.' docs/setup-checklist.md
```
Expected: 출력 없음.

```bash
awk '/^## 2부/,/^## 3부/' docs/ssms-manual-verification.md | grep -c '^- \[ \]'
```
Expected: `0`.

- [ ] **Step 4: 링크와 앵커가 살아 있는지 확인한다**

```bash
grep -rno '\[[^]]*\]([^)]*\.md[^)]*)' docs/*.md | sed 's/.*(\(.*\))/\1/' | sort -u
```
나온 경로가 실제로 존재하는지 하나씩 본다. `#` 앵커가 붙은 것은 대상 문서에 그 제목이 있는지
확인한다.

```bash
grep -rn 'setup-checklist.md#' docs/*.md CLAUDE.md README.md
```
Expected: 출력 없음.

- [ ] **Step 5: 네 머리말이 서로 맞는지 확인한다**

```bash
grep -A4 -n '여기 없는 것은 어디에' docs/setup-checklist.md docs/rollout-announcement.md docs/ssms-manual-verification.md docs/team-rollout-backlog.md
```
Expected: 네 문서 모두에서 나오고, 각자 **자기를 뺀 나머지 셋**을 가리킨다.

- [ ] **Step 6: `README.md`와 어긋나지 않는지 본다**

`README.md`는 고치지 않는다. 다만 이번에 옮긴 규칙 문장과 충돌하는 곳이 생겼는지 읽어 본다.

```bash
grep -n 'develop\|master\|hotfix\|고정 브랜치' README.md
```

충돌이 있으면 **고치지 말고 보고한다** — `README.md` 변경은 이 계획의 범위 밖이고, 충돌이
있다는 것은 설계의 방향을 다시 봐야 한다는 신호다.

- [ ] **Step 7: 전체 분량을 확인한다**

```bash
wc -l docs/setup-checklist.md docs/rollout-announcement.md docs/ssms-manual-verification.md docs/team-rollout-backlog.md
```
Expected: 대략 730 / 340 / 650 / 210, 합계 1900~1950. **합계가 지금(2152)보다 줄어드는 것이
정상이다** — 복제가 사라졌기 때문이다. 늘었다면 옮기며 다시 쓴 자리가 있다는 뜻이므로 어디인지
찾는다.

- [ ] **Step 8: 커밋**

```bash
cd C:/git-root/dbvc
git add CLAUDE.md
git commit -F - <<'EOF'
docs: 새 확인 항목을 어느 문서에 더할지 CLAUDE.md에 적는다

문서 넷의 경계를 다시 그었어도, 다음 기능의 확인 항목이 어디로 갈지
적어 두지 않으면 setup-checklist에 다시 쌓인다. 0.3.0부터 0.9.0까지
그렇게 쌓였다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## 되돌리는 법

문서만 바꾸므로 되돌리기는 git 하나다. 각 Task가 커밋 하나이므로 Task 단위로 되돌릴 수 있다.

```bash
git revert <커밋 SHA>
```

**Task 3만 따로 되돌리지 않는다.** Task 1·2가 옮긴 것을 Task 3이 지웠으므로, Task 3만 되돌리면
내용이 두 곳에 생겨 정확히 이번에 고치려던 상태가 된다. 되돌린다면 3 → 2 → 1 역순으로 함께
되돌린다.
