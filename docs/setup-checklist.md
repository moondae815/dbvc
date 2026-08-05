# DBVC 도입 체크리스트

제로 상태에서 DBVC를 실제로 쓰기까지의 순서다. 위에서부터 차례로 진행하고 완료한 항목에 체크한다.

**대상 환경 두 가지**

| | 개발 노트북 | 운영 PC |
| --- | --- | --- |
| 망 | 온라인 | 폐쇄망 |
| 원격 | github.com | 사내 GitLab 16.3 |
| 계정 | GitHub 계정 | LDAP(Windows AD) |

**단계 순서가 중요한 이유.** SSH가 되기 전에는 저장소를 못 받고, 저장소에 추적 브랜치가 없으면
Pull이 거부되고, 폴더가 Git 저장소가 아니면 DBVC가 매핑을 거부한다. 순서를 지키면 이 세 가지를
각각 따로 해결할 필요가 없다.

**소요 시간 감각.** 1~4단계(노트북)는 처음 한 번에 1~2시간. 5단계(폐쇄망)는 방화벽 승인 대기가
변수라 며칠 걸릴 수 있다 — **0단계의 방화벽 요청을 가장 먼저 넣어두는 것을 권한다.**

---

## 0단계 — 시작 전에 (지금 바로)

- [ ] **폐쇄망 방화벽 개방 요청을 넣는다.** 운영 PC → 사내 GitLab 호스트, **TCP 22번(SSH) 아웃바운드**.
      이것이 이 문서 전체에서 리드타임이 가장 긴 항목이고, 승인이 안 나면 5단계 전체가 막힌다.
      요청 사유: "Git over SSH로 DB 스키마 형상 관리 도구를 사용".
- [ ] 사내 GitLab에서 **새 프로젝트를 만들 권한**이 있는지 확인한다. 없으면 관리자에게 요청한다.
- [ ] 개발 노트북에 **Visual Studio 2022**가 설치되어 있고 **Visual Studio 확장 개발** 워크로드가
      포함되어 있는지 확인한다. `.vsix`를 만들려면 이 워크로드가 필요하다.
- [ ] 두 기계에 **SSMS 21**이 설치되어 있는지 확인한다.
- [ ] 두 기계에서 **로컬 관리자 권한**이 있는지 확인한다. `.vsix` 설치가 전체 사용자 설치라
      UAC 승인이 필요하다 (4단계). 없으면 그 단계에서 막힌다.
- [ ] 각 기계에서 **어떤 인증으로 SQL Server에 붙을지** 정한다. DBVC는 **Windows 통합 인증과
      SQL Server 인증을 모두** 지원하며, (서버, 데이터베이스)마다 따로 기억한다.
      개발 노트북은 Windows 인증, 폐쇄망 운영 PC는 SQL 인증처럼 섞어 써도 된다.
  - SQL 인증을 쓸 서버는 **혼합 모드**여야 한다:
    `SELECT SERVERPROPERTY('IsIntegratedSecurityOnly');` 이 `0`이면 SQL 인증 가능(`1`이면 Windows 전용).

- [ ] 위에서 정한 계정으로 대상 데이터베이스에 다음이 가능한지 확인한다.
  - 테이블 생성 (`DBVC_ChangeLog` 생성용)
  - DDL 트리거 생성 (`CREATE TRIGGER ... ON DATABASE`)
  - 스키마 객체 조회 (스크립트 추출용)

> **확인 방법:** SSMS에서 대상 DB에 **DBVC에서 쓸 바로 그 계정으로** 접속해
> `SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE');` 이 `1`이면 통과.
> Windows 계정으로 확인해 놓고 DBVC에서는 SQL 로그인을 쓰면 권한이 다를 수 있다.

---

## 1단계 — `.vsix` 만들기 (개발 노트북)

CI는 `.vsix`를 만들지 않는다(`.github/workflows/ci.yml` 주석 참고). 직접 빌드해야 한다.

**빌드 도구 요건.** `.NET Framework MSBuild`가 필요하다. `dotnet build`로는 VSIX가 만들어지지 않는다
— VSSDK 패키징 타깃이 .NET Framework MSBuild에서만 동작한다. Build Tools for Visual Studio 2022에
아래 두 워크로드가 모두 있어야 한다. **둘 중 하나만 있으면 실패한다.**

| 워크로드 | 없을 때 증상 |
| --- | --- |
| Visual Studio 확장 빌드 도구 | `.vsix`가 생성되지 않음 |
| .NET 데스크톱 빌드 도구 | `MSB4236: 'Microsoft.NET.Sdk' SDK를 찾을 수 없습니다` |

```
vs_BuildTools.exe --add Microsoft.VisualStudio.Workload.VisualStudioExtensionBuildTools ^
                  --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools ^
                  --includeRecommended --passive --norestart
```

- [ ] 소스를 받는다.
  ```
  git clone https://github.com/moondae815/dbvc.git
  cd dbvc
  ```
- [ ] **개발자 명령 프롬프트(Developer Command Prompt for VS 2022)** 를 열고 빌드한다.
      일반 명령 프롬프트에서는 `msbuild`를 찾지 못한다.
  ```
  msbuild src\DBVC.Vsix\DBVC.Vsix.csproj -restore -p:Configuration=Release
  ```
- [ ] 산출물이 실제로 생겼는지 확인한다. **경로에 `net48`이 들어간다.**
  ```
  dir src\DBVC.Vsix\bin\Release\net48\*.vsix
  ```
  크기가 8MB 안팎이면 정상이다.

> **`.vsix`가 없으면 여기서 멈춘다.** 뒷단계가 전부 이것에 의존한다.
> msbuild가 성공했는데 파일이 없으면 위 표의 "확장 빌드 도구" 워크로드를 확인한다.

- [ ] 만들어진 `.vsix` 파일을 **따로 보관한다.** 5단계에서 폐쇄망 PC로 옮겨야 한다.

---

## 2단계 — SSH 준비 (개발 노트북)

DBVC는 자격 증명을 묻지도 저장하지도 않는다. libgit2가 시스템 `ssh`에 그대로 넘기므로,
평소 쓰는 Git과 똑같은 SSH 설정을 그대로 물려받는다.

- [ ] **OpenSSH 클라이언트가 있는지 확인한다.**
  ```
  ssh -V
  ```
  실패하면: 설정 > 앱 > 선택적 기능 > 기능 추가 > **OpenSSH 클라이언트** 설치.

- [ ] **키를 만든다.** 이미 `~\.ssh\id_ed25519`가 있으면 건너뛴다.
  ```
  ssh-keygen -t ed25519 -C "본인메일@example.com"
  ```
  passphrase를 걸면 `ssh-agent`에 등록해 두는 편이 편하다:
  ```
  Get-Service ssh-agent | Set-Service -StartupType Automatic
  Start-Service ssh-agent
  ssh-add $env:USERPROFILE\.ssh\id_ed25519
  ```

- [ ] **공개키를 GitHub에 등록한다.** `~\.ssh\id_ed25519.pub` 내용을 통째로 복사해
      GitHub > Settings > SSH and GPG keys > New SSH key.
  ```
  type %USERPROFILE%\.ssh\id_ed25519.pub
  ```

- [ ] **접속을 확인한다.** 이 단계가 `known_hosts` 등록을 겸한다.
  ```
  ssh -T git@github.com
  ```
  처음이면 `Are you sure you want to continue connecting (yes/no)?`가 뜬다 — **`yes`를 입력한다.**
  `Hi <사용자명>! You've successfully authenticated...`가 나오면 성공이다.

> **이 확인을 건너뛰지 않는다.** DBVC 도구 창 안에서는 호스트 신뢰 여부를 묻는 프롬프트에
> 답할 방법이 없어서, `known_hosts`에 없는 호스트로는 Pull이 그냥 실패한다.

---

## 3단계 — 스키마 저장소 만들기 (개발 노트북)

**원격을 먼저 만들고 clone하는 순서로 진행한다.** `git init`으로 시작하면 추적 브랜치가 없어
Pull이 거부되고(그 상태를 DBVC가 한국어로 안내는 하지만), 별도로 `git push -u`를 해줘야 한다.
clone은 그 문제를 애초에 만들지 않는다.

- [ ] GitHub에서 **새 저장소를 만든다.** 이름 예: `db-schema-<데이터베이스명>`.
      **"Add a README file"을 체크한다** — 빈 저장소는 clone해도 브랜치가 없다.
      사내 스키마이므로 **Private**로 만든다.

- [ ] **SSH URL로 clone한다.** HTTPS URL이 아니라 SSH URL이어야 한다.
  ```
  cd C:\dbvc-repos
  git clone git@github.com:<계정>/db-schema-<데이터베이스명>.git
  ```
  > SSH URL은 `git@github.com:...` 형태다. `https://github.com/...`을 쓰면 DBVC가 Pull에서
  > 거부하면서 SSH로 바꾸는 방법을 안내한다.

- [ ] **추적 브랜치가 설정됐는지 확인한다.** clone했다면 자동으로 되어 있다.
  ```
  git -C db-schema-<데이터베이스명> status -sb
  ```
  첫 줄이 `## main...origin/main` 처럼 `...` 뒤에 원격 브랜치가 보이면 통과.
  `## main` 만 보이면 추적이 없는 것이다:
  ```
  git -C db-schema-<데이터베이스명> push -u origin main
  ```

- [ ] clone된 폴더의 **전체 경로를 적어둔다.** 4단계에서 DBVC에 입력한다.

---

## 4단계 — SSMS에 설치하고 첫 연결 (개발 노트북)

- [ ] **SSMS 21을 완전히 종료한다.**
- [ ] 1단계에서 만든 `.vsix`를 더블클릭해 설치한다. **UAC 창이 뜨면 "예"를 누른다.**
      DBVC는 전체 사용자 설치(매니페스트의 `AllUsers="true"`)라 관리자 권한이 필요하다.
      설치 위치는 `...\SSMS 21\Release\Common7\IDE\Extensions\` 아래다.
  > 개발 노트북에 **Visual Studio도 설치되어 있다면** 설치 대상이 SSMS 21인지 확인한다.
  > DBVC는 `Microsoft.VisualStudio.Ssms`만 대상으로 하므로 VS에는 설치되지 않는 것이 정상이다.
- [ ] SSMS 21을 실행하고 **View(보기) 메뉴 > DBVC**를 연다. 메뉴 아래쪽에 있다.
      메뉴에 항목이 없으면 설치가 안 된 것이다 — SSMS를 껐다 켜고 다시 확인한다.
  > "다른 창(Other Windows)" 안이 **아니다.** SSMS에서는 그 하위 메뉴 자체가 숨겨져 있어
  > 거기에 넣으면 보이지 않는다 (Visual Studio와 다른 점이다).

- [ ] 패널 상단 **Server / Database** 입력란에 대상을 입력한다.
      Server는 SSMS 접속 시 쓰는 것과 같은 값(예: `localhost`, `SQLSRV01\INST1`).

- [ ] 그 옆 **인증 방식**을 고른다.
  - **Windows 인증** — 추가 입력이 없다.
  - **SQL Server 인증** — **User / Password** 칸이 나타난다. 0단계에서 정한 계정을 입력한다.
      암호는 DPAPI로 암호화되어 `%APPDATA%\DBVC\credentials.json`에 저장되며,
      **저장한 Windows 계정에서만** 복호화된다. 다음부터는 암호 칸을 비워 두면 저장된 값을 쓴다.

- [ ] **Connect** 를 누른다. 접속에 실패하면 배너에 한국어 사유가 뜬다
      (로그인 실패, 서버 도달 불가 등). 성공하면 아래 매핑 경고로 넘어간다.

- [ ] 경고 배너 `Active Database is not mapped to a Git repository.` 가 뜨는지 확인한다.
      **뜨는 것이 정상이다** — 아직 매핑하지 않았다.

- [ ] 배너의 **"저장소 연결..."** 버튼을 누르고 3단계에서 clone한 폴더를 선택한다.
      배너가 사라지면 성공이다.
  > Git 저장소가 아닌 폴더를 고르면 오류가 나고 매핑되지 않는다. `.git` 폴더가 있는
  > 최상위 폴더를 골라야 한다.

- [ ] 매핑이 저장됐는지 확인한다.
  ```
  type %APPDATA%\DBVC\mappings.json
  ```
  SQL 인증을 골랐다면 인증 정보도 확인한다. `ProtectedPassword` 가 알아볼 수 없는
  Base64 문자열이어야 한다 — 평문이 보이면 결함이다.
  ```
  type %APPDATA%\DBVC\credentials.json
  ```

---

## 5단계 — 데이터베이스 초기화 (개발 노트북)

- [ ] 패널 중앙에 **"Setup DBVC"** 버튼이 보이면 누른다.
      `DBVC_ChangeLog` 테이블과 DDL 트리거가 설치된다. 이 스크립트는 멱등이라 다시 실행해도 안전하다.
      권한이 부족하면 오류가 뜨고 화면은 초기화 전 상태로 남는다 — 0단계의 권한 확인으로 돌아간다.

- [ ] 설치를 확인한다. SSMS 쿼리 창에서:
  ```sql
  SELECT COUNT(*) FROM sys.objects WHERE name = 'DBVC_ChangeLog';       -- 1
  SELECT COUNT(*) FROM sys.triggers WHERE parent_class_desc = 'DATABASE'; -- 1 이상
  ```

- [ ] **Refresh** 를 누른다. 현재 DB의 객체가 `.sql` 파일로 추출되고 변경 목록이 채워진다.
      첫 실행이라 모든 객체가 `Added`로 나온다.

- [ ] 목록 항목을 하나 클릭해 하단 **Diff** 탭에 코드가 보이는지 확인한다.

- [ ] **첫 커밋을 만든다.** 항목을 전부 체크하고 커밋 메시지를 쓴 뒤 **Commit** 을 누른다.
      예: `chore: 초기 스키마 스냅샷`

- [ ] 원격에 올린다. DBVC에는 Push 기능이 없으므로 Git 클라이언트에서 한다.
  ```
  git -C C:\dbvc-repos\db-schema-<데이터베이스명> push
  ```

- [ ] **Pull을 눌러본다.** `원격 저장소의 변경을 가져왔습니다.` 알림이 뜨면 SSH 경로가 끝까지 동작하는 것이다.
      **이 확인이 이 문서에서 가장 중요하다** — 여기까지 되면 개발 노트북은 완료다.

---

## 6단계 — 폐쇄망 PC 전개

0단계의 방화벽 승인이 난 뒤에 진행한다.

- [ ] **방화벽이 실제로 열렸는지 확인한다.** 운영 PC에서:
  ```
  ssh -T git@<gitlab-호스트>
  ```
  `Connection timed out`이면 아직 안 열린 것이다. `Permission denied (publickey)` 는
  **포트가 열렸다는 뜻이므로 성공**이다(키를 아직 안 올렸을 뿐).

- [ ] `.vsix` 파일을 사내 반입 절차에 따라 운영 PC로 옮긴다.

- [ ] **2단계를 운영 PC에서 반복한다.** 키는 기계마다 따로 만드는 것을 권한다.
  - [ ] `ssh -V` 로 OpenSSH 클라이언트 확인
  - [ ] `ssh-keygen -t ed25519` 로 키 생성
  - [ ] 공개키를 **GitLab** 에 등록: 우측 상단 아바타 > Preferences > SSH Keys
  - [ ] `ssh -T git@<gitlab-호스트>` 로 접속 확인 및 `known_hosts` 등록 (`yes` 입력)

- [ ] **GitLab에 프로젝트를 만든다.** README 포함(빈 저장소가 되지 않도록), Private.

- [ ] **SSH URL로 clone한다.**
  ```
  git clone git@<gitlab-호스트>:<그룹>/db-schema-<데이터베이스명>.git
  ```
  > GitLab이 비표준 SSH 포트를 쓴다면 URL이 `ssh://git@<호스트>:2222/<그룹>/<프로젝트>.git`
  > 형태가 된다. GitLab 프로젝트 페이지의 Clone 버튼이 알려주는 값을 그대로 쓴다.

- [ ] `git -C <폴더> status -sb` 로 추적 브랜치 확인.

- [ ] **4단계를 운영 PC에서 반복한다** (VSIX 설치 → Connect → 저장소 연결).

- [ ] **5단계를 운영 PC에서 반복한다** (Setup DBVC → Refresh → Commit → push → Pull).

---

## 7단계 — 동작 검증 (각 기계에서)

여기부터는 기능이 의도대로 도는지 확인하는 항목이다. CI가 검증할 수 없는 것들이다.

### 기본 흐름

- [ ] 저장 프로시저를 하나 `ALTER` 한 뒤 **Refresh** → 목록에 `Modified` 로 뜨는지
- [ ] 항목 선택 → **Diff** 탭에서 좌(이전)/우(현재)가 나뉘고 **변경된 줄에 배경색**이 칠해지는지
- [ ] Diff 좌우 **세로 스크롤이 함께** 움직이는지
- [ ] Diff **가로 스크롤**: 한쪽을 오른쪽 끝까지 밀었을 때 반대쪽이 자기 한계로 끌어당기지 않는지
      (넓은 쪽이 좁은 쪽 한계까지 끌려오면 결함이다)
- [ ] 탭을 **History → Diff** 로 오갔을 때 배경색이 다시 그려지는지
- [ ] 항목 선택 → **History** 탭에 그 객체의 커밋(날짜·작성자·메시지·SHA)이 최신순으로 뜨는지
- [ ] 도구 창을 **좁게 도킹**했을 때 상단 버튼들이 잘리지 않고 줄바꿈되는지
- [ ] 객체를 `DROP` 한 뒤 **Refresh** → `Deleted` 로 뜨고, 체크해서 Commit하면 저장소에서도 파일이 사라지는지

### 인증 (SQL 인증을 쓰는 기계에서)

- [ ] **암호를 저장하고 SSMS를 재시작** → Connect 시 암호 칸을 비운 채 눌러도 접속되는지
- [ ] **틀린 암호로 Connect** → 배너에 `로그인하지 못했습니다` 와 혼합 모드 안내가 뜨는지.
      영문 SqlException 원문이 그대로 보이면 결함이다
- [ ] **Windows 인증으로 되돌린 뒤 Connect** → 접속되고, `credentials.json` 의 해당 항목에서
      `ProtectedPassword` 가 `null` 이 되는지
- [ ] `credentials.json` 을 텍스트 편집기로 열어 **암호가 평문으로 보이지 않는지**
- [ ] 인증 입력란이 늘었으므로 **도구 창을 좁게 도킹**했을 때 상단 첫 줄이 잘리지 않고 줄바꿈되는지

### 스크립트 생성

- [ ] 항목 몇 개 체크 → **Deployment Script** → 저장 → **"N개 객체를 내보냈습니다."** 알림이 뜨는지
      (제외가 없어도 알림이 떠야 한다)
- [ ] 저장소에 `.sql` 파일이 없는 객체를 포함해 Deployment Script를 만들면
      **"추출된 파일이 없어 제외했습니다"** 문구가 나오는지
- [ ] 커밋 이력이 하나뿐인 객체로 **Rollback Script** 를 만들면
      **"이전 리비전이 없어 제외했습니다"** 문구가 나오는지
- [ ] 생성된 `.sql` 을 텍스트 편집기로 열어 헤더에 `Excluded: N (...)` 줄이 있는지
- [ ] 스크립트를 만든 뒤 상단 **경고 배너가 오염되지 않았는지** (매핑 경고만 떠야 한다)

### Pull 실패 경로 (안내 문구 확인)

각 상황을 일부러 만들어 **한국어 안내가 뜨는지** 본다. libgit2 영문 원문이 보이면 결함이다.

- [ ] **HTTPS 원격.** 임시로 `git remote set-url origin https://...` 로 바꾸고 Pull →
      `HTTPS 원격은 DBVC가 인증할 수 없습니다. SSH 원격으로 바꾸세요.` 와 변환 예시가 뜨는지.
      확인 후 SSH URL로 되돌린다.
- [ ] **추적 브랜치 없음.** `git checkout -b tmp` 로 새 브랜치를 만들고 Pull →
      `추적 중인 원격 브랜치가 없어 Pull할 수 없습니다` 와 `git push -u origin tmp` 안내가 뜨는지.
      확인 후 `git checkout main` 으로 되돌린다.
- [ ] **미커밋 변경이 있는 상태.** Refresh만 하고 커밋하지 않은 채 Pull →
      확인 대화상자에 **"거부됩니다. 이 경우 저장소는 그대로입니다"** 와
      **"사라질 수 있습니다"** 가 **둘 다** 보이는지. 창이 SSMS 뒤로 숨지 않는지.
- [ ] **겹치는 미커밋 변경.** 원격이 바꾼 파일을 로컬에서 커밋하지 않고 수정한 뒤 Pull → 확인 →
      안내가 뜨고 **로컬 수정 내용이 그대로 남아 있는지**. (사라지면 심각한 결함이다)
- [ ] **SSH 포트 문구.** 안내 목록의 포트 항목이 `원격 호스트의 SSH 포트(기본 22)가 열려 있는지`
      로 표시되는지. 비표준 포트를 쓰는 GitLab에서 특히 확인한다.
- [ ] **OpenSSH가 없는 상태** (선택). 선택적 기능에서 OpenSSH 클라이언트를 끄고 Pull →
      `OpenSSH 클라이언트를 설치한 뒤 다시 시도하세요` 안내가 뜨는지. 확인 후 다시 켠다.

### 컨텍스트 메뉴

- [ ] SQL 에디터에서 객체 이름을 선택하고 우클릭 → **DBVC: Compare with Repository** 가 보이고
      동작하는지

---

## 알려진 제약

작업 전에 알고 있으면 좋은 것들이다. 결함이 아니라 현재 설계의 경계다.

- **인증은 SSH만.** HTTPS 원격은 인증할 수 없다. 폐쇄망 방화벽이 끝내 안 열리면 HTTPS + 액세스 토큰
  방식을 새로 설계해야 하며, 사유와 조건은
  [specs/2026-08-03-dbvc-ssh-first-git-auth-design.md](superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md) 3절에 있다.
- **SQL 인증 암호는 저장한 Windows 계정에 묶인다.** DPAPI(CurrentUser)로 보호하므로
  `credentials.json`을 다른 계정이나 다른 기계로 복사해도 복호화되지 않는다. 그 경우 Connect에서
  다시 입력하면 된다. 공용 계정으로 로그온해 쓰는 환경이면 이 점을 미리 확인한다.
- **DDL 변경 이력의 `LoginName`은 실제 접속 계정을 기록한다.** SQL 인증으로 모두가 같은 로그인을
  공유하면 `DBVC_ChangeLog.LoginName`으로 사람을 구분할 수 없다. 현재 화면에는 이 값을 쓰지 않지만
  (Git 커밋 작성자는 `git config`에서 온다), 사람별 추적이 필요하면 로그인을 나눈다.
- **Push 기능이 없다.** 커밋까지가 DBVC의 역할이고, 원격에 올리는 것은 Git 클라이언트로 한다.
- **Pull은 파일만 가져온다.** 받은 `.sql` 을 데이터베이스에 적용할지는 사용자가 판단한다.
  DBVC는 스크립트를 실행하지 않는다.
- **변경 감지는 Refresh 시점.** DDL 트리거가 발생 즉시 `DBVC_ChangeLog` 에 기록하지만,
  화면 반영은 Refresh·Connect·Setup·Commit 직후에만 일어난다. 주기적 폴링은 하지 않는다.
- **Object Explorer 상태 아이콘 오버레이는 미구현.** SSMS에 공개 확장점이 없어 보류했다
  (Feature 10, [plans/2026-08-01-dbvc-object-explorer-overlay.md](superpowers/plans/2026-08-01-dbvc-object-explorer-overlay.md)).
  변경 상태는 View Changes 창에서 확인한다.

---

## 막혔을 때

| 증상 | 확인할 것 |
| --- | --- |
| 메뉴에 DBVC가 없다 | **보기 메뉴 본체**를 봤는지 ("다른 창" 안이 아니다). SSMS를 완전히 종료한 뒤 `.vsix` 재설치. 확장 관리자에서 설치 여부 확인 |
| `.vsix` 설치가 "관리 권한이 있어야 합니다"로 끝난다 | 관리자 권한으로 설치해야 한다. UAC 승인 창을 놓쳤는지 확인 |
| SSMS가 아니라 Visual Studio에 설치됐다 | 두 제품이 다 있을 때 생길 수 있다. VS에서 제거하고, SSMS의 `VSIXInstaller.exe`에 `/instanceIds:<SSMS 인스턴스ID>` 를 주어 설치한다 (`vswhere.exe -all -products *` 로 ID 확인) |
| "저장소 연결..."이 오류를 낸다 | 고른 폴더에 `.git` 이 있는지. clone된 최상위 폴더인지 |
| Setup DBVC가 실패한다 | 0단계의 권한 확인. `CREATE TABLE`·`CREATE TRIGGER` 권한 |
| Connect가 "로그인하지 못했습니다"를 낸다 | 사용자명·암호, 그리고 서버가 혼합 모드인지 (`SERVERPROPERTY('IsIntegratedSecurityOnly')` 가 `0`) |
| Connect가 "저장된 암호를 사용할 수 없습니다"를 낸다 | 암호를 저장한 Windows 계정과 지금 로그온한 계정이 다르다. 암호를 다시 입력한다 |
| Refresh해도 목록이 비어 있다 | DDL 트리거 설치 확인. 트리거 설치 **이후에** 변경한 객체만 잡힌다 |
| Pull이 영문 메시지를 낸다 | 안내가 붙지 않은 경우다. 원격 URL이 SSH도 HTTPS도 아닌 형태인지 확인 |
| Pull이 `known_hosts` 를 말한다 | Git 클라이언트에서 `ssh -T git@<호스트>` 를 한 번 실행해 `yes` 입력 |
| 커밋했는데 원격에 없다 | DBVC에 Push가 없다. `git push` 를 직접 실행 |
