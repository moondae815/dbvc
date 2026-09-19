# DBVC 도입 체크리스트

제로 상태에서 DBVC를 실제로 쓰기까지의 순서다. 위에서부터 차례로 진행하고 완료한 항목에 체크한다.

**이 문서는 설치하는 사람의 것이다.** 설치가 끝난 뒤 쓰는 사람에게 나눠 줄 것은
[`user-guide.html`](user-guide.html)이다 — 개발자와 DBA가 매일 무엇을 누르는지만 담은 한 장이고,
파일 하나로 돌아다니도록 만들어 두었다.

**여기 없는 것은 어디에.** 팀이 지켜야 할 규칙과 조직이 정해야 할 것은
[`rollout-announcement.md`](rollout-announcement.md), 기능이 의도대로 도는지 훑는 확인 항목은
[`ssms-manual-verification.md`](ssms-manual-verification.md), 남은 일과 우선순위는
[`team-rollout-backlog.md`](team-rollout-backlog.md)에 있다.

**대상 환경 두 가지** — 두 기계 모두 **Windows 11** 기준이다.

| | 개발 노트북 | 운영 PC |
| --- | --- | --- |
| OS | Windows 11 | Windows 11 |
| 망 | 온라인 | 폐쇄망 |
| 원격 | github.com | 사내 GitLab 16.3 |
| 계정 | GitHub 계정 | LDAP(Windows AD) |

**명령을 실행하는 곳.** 이 문서의 명령은 Windows 11의 기본 터미널인 **Windows Terminal의
PowerShell** 에서 실행하는 것을 기준으로 적었다. 일부 명령은 **관리자 권한** 창이
필요한데, 해당 항목에 표시해 두었다.

> Windows 10에서 쓰던 `type %APPDATA%\...` 같은 명령 프롬프트 문법은 PowerShell에서 동작하지 않는다
> (`%VAR%`가 그대로 문자열로 남는다). 아래 명령은 모두 PowerShell 문법으로 바꿔 두었다.

**단계 순서가 중요한 이유.** SSH가 되기 전에는 저장소를 못 받고, 저장소에 추적 브랜치가 없으면
Pull이 거부되고, 폴더가 Git 저장소가 아니면 DBVC가 매핑을 거부한다. 순서를 지키면 이 세 가지를
각각 따로 해결할 필요가 없다.

**소요 시간 감각.** 1~5단계(노트북)는 처음 한 번에 1~2시간. 6단계(폐쇄망)는 방화벽 승인 대기가
변수라 며칠 걸릴 수 있다 — **0단계의 방화벽 요청을 가장 먼저 넣어두는 것을 권한다.**

---

## 0단계 — 시작 전에 (지금 바로)

- [ ] **폐쇄망 방화벽 개방 요청을 넣는다.** 운영 PC → 사내 GitLab 호스트, **TCP 22번(SSH) 아웃바운드**.
      이것이 이 문서 전체에서 리드타임이 가장 긴 항목이고, 승인이 안 나면 6단계 전체가 막힌다.
      요청 사유: "Git over SSH로 DB 스키마 형상 관리 도구를 사용".
- [x] 사내 GitLab에서 **새 프로젝트를 만들 권한**이 있는지 확인한다. 없으면 관리자에게 요청한다.
- [x] 개발 노트북에 **Visual Studio 2022**가 설치되어 있고 **Visual Studio 확장 개발** 워크로드가
      포함되어 있는지 확인한다. `.vsix`를 만들려면 이 워크로드가 필요하다.
- [x] 두 기계에 **SSMS 21**이 설치되어 있는지 확인한다.
- [x] 두 기계가 **Windows 11**인지 확인한다. Windows 10에서도 동작하지만 이 문서의 설정 앱 경로는
      Windows 11 기준이다.
  ```powershell
  Get-ComputerInfo -Property OsName,OsVersion | Format-List
  ```
  `OsName`에 `Windows 11`이 나오면 된다 (`OsVersion`은 Windows 11도 `10.0.x`로 시작한다 — 정상이다).
- [x] 두 기계에서 **로컬 관리자 권한**이 있는지 확인한다. `.vsix` 설치가 전체 사용자 설치라
      UAC 승인이 필요하다 (4단계). 없으면 그 단계에서 막힌다.
      Windows 11 Home / Pro 어느 쪽이든 상관없다.
  ```powershell
  whoami /groups | Select-String 'S-1-5-32-544'
  ```
  `BUILTIN\Administrators` 줄이 보이면 통과다. 관리자 권한 없이 연 창에서는 그 줄에
  `Group used for deny only`(권한 거부용) 가 함께 붙는데, **정상이다** — UAC가 승인 전까지
  권한을 낮춰 둔 것뿐이고 4단계에서 "예"를 누르면 올라간다. 줄 자체가 안 나오면 관리자가 아니다.
- [x] 각 기계에서 **어떤 인증으로 SQL Server에 붙을지** 정한다. DBVC는 **Windows 통합 인증과
      SQL Server 인증을 모두** 지원하며, (서버, 데이터베이스)마다 따로 기억한다.
      개발 노트북은 Windows 인증, 폐쇄망 운영 PC는 SQL 인증처럼 섞어 써도 된다.
  - SQL 인증을 쓸 서버는 **혼합 모드**여야 한다:
    `SELECT SERVERPROPERTY('IsIntegratedSecurityOnly');` 이 `0`이면 SQL 인증 가능(`1`이면 Windows 전용).

- [x] 위에서 정한 계정으로 대상 데이터베이스에 다음이 가능한지 확인한다.
  - 테이블 생성 (`DBVC_ChangeLog` 생성용)
  - DDL 트리거 생성 (`CREATE TRIGGER ... ON DATABASE`)
  - 스키마 객체 조회 (스크립트 추출용)
  - `dbo` 가장 (트리거가 `WITH EXECUTE AS 'dbo'`로 실행되므로 `db_owner`여야 한다)

> **확인 방법:** SSMS에서 대상 DB에 **DBVC에서 쓸 바로 그 계정으로** 접속해
> `SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE');` 이 `1`이면 통과.
> Windows 계정으로 확인해 놓고 DBVC에서는 SQL 로그인을 쓰면 권한이 다를 수 있다.

- [x] **[`rollout-announcement.md`](rollout-announcement.md)를 팀이 읽었는지 확인한다.**
      "채워 넣을 칸" 셋과 3절 "조직이 정해야 할 것"은 2026-09-19에 채워 두었다 — 그래서 이제
      확인할 것은 빈칸이 아니라 **팀이 그 답에 실제로 동의했는가**다. 특히 3절의 `db_owner`
      유지는 잔여 위험을 알고 고른 쪽이라, 그 위험을 아는 사람이 팀에 있어야 한다.
      **이것은 설치자가 정하는 것이 아니다** — 리드 타임이 있으므로 0단계에 둔다.

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

PowerShell에서는 줄바꿈 기호가 `^`가 아니라 백틱(`` ` ``)이다. 아래를 그대로 쓴다.

```powershell
.\vs_BuildTools.exe --add Microsoft.VisualStudio.Workload.VisualStudioExtensionBuildTools `
                    --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools `
                    --includeRecommended --passive --norestart
```

> Windows 11에는 `winget`이 기본 포함되어 있으므로, Build Tools 자체가 아직 없다면 내려받기부터
> 한 번에 할 수 있다.
> ```powershell
> winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VisualStudioExtensionBuildTools --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --includeRecommended --passive --norestart"
> ```
> 폐쇄망 PC에서는 `winget`이 원격 저장소에 닿지 못하므로 이 방법을 쓸 수 없다. 다만 폐쇄망 PC는
> `.vsix`를 받아 설치만 하므로 빌드 도구 자체가 필요 없다 (6단계).

- [x] 소스를 받는다.
  ```powershell
  git clone https://github.com/moondae815/dbvc.git
  cd dbvc
  ```
- [x] 빌드한다. 일반 PowerShell 창에서 그대로 된다 — 개발자용 셸은 필요하지 않다.
  ```powershell
  dotnet build src\DBVC.Vsix\DBVC.Vsix.csproj -c Release
  ```
- [x] 산출물이 실제로 생겼는지 확인한다. **경로에 `net48`이 들어간다.**
  ```powershell
  Get-ChildItem src\DBVC.Vsix\bin\Release\net48\*.vsix |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
  ```
  크기가 8MB 안팎이면 정상이다.

> **`.vsix`가 없으면 여기서 멈춘다.** 뒷단계가 전부 이것에 의존한다.
> 빌드가 성공했는데 파일이 없으면 위 표의 "확장 빌드 도구" 워크로드를 확인한다 —
> `Microsoft.VSSDK.BuildTools`가 임포트되지 않으면 어떤 빌드 도구로도 `.vsix`는 나오지 않는다.
> 워크로드가 멀쩡한데도 안 나오면 **개발자용 셸**에서 msbuild로 한 번 더 시도한다. 시작 메뉴에서
> `Developer PowerShell for VS 2022`(또는 `Developer Command Prompt for VS 2022`)를 연다 —
> Windows 11의 Windows Terminal을 쓴다면 탭 새로 만들기 옆 **∨** 를 눌러 같은 이름의 프로필을
> 고르면 된다.
>
> ```powershell
> msbuild src\DBVC.Vsix\DBVC.Vsix.csproj -restore -p:Configuration=Release
> ```

- [x] 만들어진 `.vsix` 파일을 **따로 보관한다.** 6단계에서 폐쇄망 PC로 옮겨야 한다.

---

## 2단계 — SSH 준비 (개발 노트북)

DBVC는 자격 증명을 묻지도 저장하지도 않는다. libgit2가 시스템 `ssh`에 그대로 넘기므로,
평소 쓰는 Git과 똑같은 SSH 설정을 그대로 물려받는다.

- [x] **OpenSSH 클라이언트가 있는지 확인한다.** Windows 11에는 기본으로 들어 있어
      대개 그냥 통과한다.
  ```powershell
  ssh -V
  ```
  `OpenSSH_for_Windows_...` 가 나오면 통과다. 실패하면 (사내 이미지에서 빼 놓은 경우가 있다)
  **관리자 권한 PowerShell** 에서 설치한다.
  ```powershell
  Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
  ```
  설정 앱으로 하려면 **설정 > 시스템 > 선택적 기능 > 기능 보기 > OpenSSH 클라이언트** 다.
  Windows 10의 "설정 > 앱 > 선택적 기능"에서 **시스템 아래로 옮겨졌다.** 바로 열려면:
  ```powershell
  start ms-settings:optionalfeatures
  ```

- [x] **키를 만든다.** 이미 `~\.ssh\id_ed25519`가 있으면 건너뛴다.
  ```powershell
  ssh-keygen -t ed25519 -C "본인메일@example.com"
  ```
  passphrase를 걸면 `ssh-agent`에 등록해 두는 편이 편하다. Windows 11에서 `ssh-agent` 서비스는
  **기본이 "사용 안 함"** 이라 서비스부터 켜야 한다.
  ```powershell
  # 앞의 두 줄은 관리자 권한 PowerShell에서
  Get-Service ssh-agent | Set-Service -StartupType Automatic
  Start-Service ssh-agent
  # 이 줄은 평소 쓰는 일반 창에서 (사용자 계정별로 등록된다)
  ssh-add $env:USERPROFILE\.ssh\id_ed25519
  ```

- [x] **공개키를 GitHub에 등록한다.** `~\.ssh\id_ed25519.pub` 내용을 통째로 복사해
      GitHub > Settings > SSH and GPG keys > New SSH key.
  ```powershell
  # 화면으로 확인
  Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub
  # 클립보드로 바로 복사
  Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | Set-Clipboard
  ```
  > `.pub` 이 붙은 **공개키** 파일이다. 확장자 없는 `id_ed25519`(개인키)는 절대 올리지 않는다.

- [x] **접속을 확인한다.** 이 단계가 `known_hosts` 등록을 겸한다.
  ```powershell
  ssh -T git@github.com
  ```
  처음이면 `Are you sure you want to continue connecting (yes/no)?`가 뜬다 — **`yes`를 입력한다.**
  `Hi <사용자명>! You've successfully authenticated...`가 나오면 성공이다.

> **이 확인을 건너뛰지 않는다.** DBVC 도구 창 안에서는 호스트 신뢰 여부를 묻는 프롬프트에
> 답할 방법이 없어서, `known_hosts`에 없는 호스트로는 Pull이 그냥 실패한다.

---

## 3단계 — 스키마 저장소 만들기 (개발 노트북)

**원격을 먼저 만들고 터미널에서 clone하는 순서로 진행한다.** `git init`으로 시작하면 추적 브랜치가
없어 Pull과 Push 모두 거부되고(그 상태를 DBVC가 한국어로 안내는 하지만), 별도로 `git push -u`를
해줘야 한다. clone은 그 문제를 애초에 만들지 않는다.

> **DBVC 창의 "저장소 연결..."에도 받기 기능이 있지만 이 문서는 터미널을 쓴다.** 이유가 둘이다.
> 아래 `develop`·`master` 브랜치를 만들려면 **로컬 클론이 먼저 있어야 해서** 도구로 받으면
> 4단계까지 갔다가 이 단계로 되돌아와야 한다. 그리고 SSMS 안의 `ssh.exe`는 호스트 키 확인을
> 물을 수 없어, 2단계의 `ssh -T`를 건너뛴 경우 **아무 말 없이 멈춘다** — 터미널이면 `yes`를
> 입력하라고 물어보고 지나간다.

- [x] GitHub에서 **새 저장소를 만든다.** 이름 예: `db-schema-<데이터베이스명>`.
      **"Add a README file"을 체크한다** — 빈 저장소는 clone해도 브랜치가 없다.
      사내 스키마이므로 **Private**로 만든다.

- [x] **터미널에서 clone한다.**
  ```powershell
  git clone git@github.com:<계정>/db-schema-<데이터베이스명>.git
  ```
  > SSH URL은 `git@github.com:...` 형태다. `https://github.com/...`을 쓰면 clone은 되어도
  > 이후 DBVC의 Pull·Push가 SSH만 지원하므로 다시 SSH 원격으로 바꿔야 한다.

  > **받을 위치.** OneDrive가 동기화하는 폴더(바탕 화면·문서)는 피한다. Windows 11에서는
  > 이 폴더들이 기본으로 OneDrive 백업 대상이라 `.git` 내부 파일이 동기화와 충돌할 수 있다.

- [x] **추적 브랜치가 설정됐는지 확인한다.** clone 직후에는 보통 되어 있지만 확인해 둔다.
  ```powershell
  git -C db-schema-<데이터베이스명> status -sb
  ```
  첫 줄이 `## main...origin/main` 처럼 `...` 뒤에 원격 브랜치가 보이면 통과.
  `## main` 만 보이면 추적이 없는 것이다:
  ```powershell
  git -C db-schema-<데이터베이스명> push -u origin main
  ```

- [x] **clone된 폴더의 전체 경로를 적어둔다.** 4단계에서 **저장소 연결...** 을 누를 때 그대로
      입력한다.

- [x] **`develop`과 `master` 브랜치를 만든다.** 8단계의 배포·감사 클론이 고정 브랜치로 쓸
      브랜치들이라 지금 만들어 둔다.
  ```powershell
  git -C db-schema-<데이터베이스명> checkout -b develop
  git -C db-schema-<데이터베이스명> push -u origin develop
  git -C db-schema-<데이터베이스명> checkout -b master
  git -C db-schema-<데이터베이스명> push -u origin master
  git -C db-schema-<데이터베이스명> checkout main
  ```

---

## 4단계 — SSMS에 설치하고 첫 연결 (개발 노트북)

- [x] **SSMS 21을 완전히 종료한다.**
- [x] 1단계에서 만든 `.vsix`를 더블클릭해 설치한다. **UAC 창이 뜨면 "예"를 누른다.**
      DBVC는 전체 사용자 설치(매니페스트의 `AllUsers="true"`)라 관리자 권한이 필요하다.
      설치 위치는 `...\SSMS 21\Release\Common7\IDE\Extensions\` 아래다.
  > 다른 기계에서 복사해 온 파일이면 Windows가 차단 표시를 붙여 설치가 막힐 수 있다
  > (파일 속성 아래쪽의 "차단 해제"). 미리 풀어 두려면:
  > ```powershell
  > Unblock-File .\DBVC.Vsix.vsix
  > ```
  > Windows 11 파일 탐색기는 우클릭 메뉴가 접혀 있다 — "속성"이나 원하는 항목이 안 보이면
  > **추가 옵션 표시**(`Shift+F10`)를 누른다.

  > 개발 노트북에 **Visual Studio도 설치되어 있다면** 설치 대상이 SSMS 21인지 확인한다.
  > DBVC는 `Microsoft.VisualStudio.Ssms`만 대상으로 하므로 VS에는 설치되지 않는 것이 정상이다.
- [x] SSMS 21을 실행하고 **View(보기) 메뉴 > DBVC**를 연다. 메뉴 아래쪽에 있다.
      메뉴에 항목이 없으면 설치가 안 된 것이다 — SSMS를 껐다 켜고 다시 확인한다.
  > "다른 창(Other Windows)" 안이 **아니다.** SSMS에서는 그 하위 메뉴 자체가 숨겨져 있어
  > 거기에 넣으면 보이지 않는다 (Visual Studio와 다른 점이다).

- [x] **개체 탐색기**에서 0단계에서 정한 계정으로 대상 데이터베이스(또는 그 하위 개체)에 먼저
      접속해 둔다. Server/Database, 인증 방식, 계정은 모두 그 연결에서 그대로 온다 — DBVC 창에는
      입력란이 없다.

      인증 정보는 개체 탐색기의 연결에서 그대로 오며 디스크에 저장되지 않는다.
      SSMS를 다시 열면 개체 탐색기에 접속한 뒤 연결을 한 번 더 누른다.

- [x] 개체 탐색기에서 대상 데이터베이스를 선택한 뒤 **연결** 을 누른다. 접속에 실패하면 배너에
      한국어 사유가 뜬다 (로그인 실패, 서버 도달 불가 등). 성공하면 아래 매핑 경고로 넘어간다.

- [x] 경고 배너 `현재 데이터베이스에 연결된 Git 저장소가 없습니다.` 가 뜨는지 확인한다.
      **뜨는 것이 정상이다** — 아직 매핑하지 않았다.

- [x] 배너의 **"저장소 연결..."** 버튼을 누르고 **"이미 받아둔 폴더를 연결합니다"** 를 고른 뒤,
      3단계에서 적어 둔 폴더를 선택한다. 배너가 사라지면 성공이다.
  > Git 저장소가 아닌 폴더를 고르면 오류가 나고 매핑되지 않는다. `.git` 폴더가 있는
  > 최상위 폴더를 골라야 한다.

- [x] 매핑이 저장됐는지 확인한다.
  ```powershell
  Get-Content $env:APPDATA\DBVC\mappings.json
  ```

- [x] `%APPDATA%\DBVC` 에 `credentials.json` 이 **없는지** 확인한다. 이전 버전이 남긴 파일이
      있었다면 확장이 처음 로드될 때 지워진다.
  ```powershell
  # 아무것도 출력되지 않으면 통과
  Get-ChildItem $env:APPDATA\DBVC -Filter credentials.json
  ```

---

## 5단계 — 데이터베이스 초기화 (개발 노트북)

- [ ] **초기화하는 계정이 `db_owner`인지 확인한다.** 트리거를 `dbo` 권한으로 실행하도록 만들기 때문에
      `dbo`를 가장할 수 있어야 한다. 권한이 부족하면 초기화가 실패하고 사유가 그대로 표시된다.

> **초기화하는 사람과 일상적으로 쓰는 사람의 권한은 다르다.** 아래 "일상 사용에 필요한 권한"을
> 함께 읽는다. 팀에 배포하기 전에 확인해야 하는 것은 그쪽이다.

- [ ] 패널 중앙에 **"DBVC 초기화"** 버튼이 보이면 누른다.
      `DBVC_ChangeLog` 테이블과 DDL 트리거가 설치된다. 이 스크립트는 멱등이라 다시 실행해도 안전하다.
      권한이 부족하면 오류가 뜨고 화면은 초기화 전 상태로 남는다 — 위 `db_owner` 확인과
      0단계의 권한 확인으로 돌아간다.

- [ ] 설치를 확인한다. SSMS 쿼리 창에서:
  ```sql
  SELECT COUNT(*) FROM sys.objects WHERE name = 'DBVC_ChangeLog';       -- 1
  SELECT COUNT(*) FROM sys.triggers WHERE parent_class_desc = 'DATABASE'; -- 1 이상
  ```

- [ ] **새로고침** 을 누른다. 현재 DB의 객체가 `.sql` 파일로 추출되고 변경 목록이 채워진다.
      첫 실행이라 모든 객체가 `추가`로 나온다.

- [ ] 목록 항목을 하나 클릭해 하단 **비교** 탭에 코드가 보이는지 확인한다.

- [ ] **첫 커밋을 만든다.** 항목을 전부 체크하고 커밋 메시지를 쓴 뒤 **Commit** 을 누른다.
      예: `chore: 초기 스키마 스냅샷`

- [ ] 원격에 올린다. DBVC의 **Push** 버튼을 누른다.

- [ ] **Pull을 눌러본다.** `원격 저장소의 변경을 가져왔습니다.` 또는 `원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.` 중 **어느 쪽이든** 알림이 뜨면 SSH 경로가 끝까지 동작하는 것이다. 갓 설정한 저장소는 받아올 커밋이 없으므로 대개 후자가 뜬다.
      **이 확인이 이 문서에서 가장 중요하다** — 여기까지 되면 개발 노트북은 완료다.

> **콘솔 창이 잠깐 떴다 사라지는 것은 정상이다.** DBVC에 동봉된 libgit2에는 SSH 라이브러리가
> 들어 있지 않아, 원격과 통신할 때 시스템 `ssh.exe`를 자식 프로세스로 실행한다. SSMS는 콘솔이
> 없는 GUI 프로세스라 Windows가 그 순간 콘솔을 새로 할당했다가 닫는다. Pull과 Push 모두에서
> 매번 보이며, 오히려 SSH 경로가 실제로 돌았다는 표시다.

### 일상 사용에 필요한 권한

초기화는 한 번, `db_owner`가 한다. 그 뒤로 **매일 DBVC를 쓰는 사람에게 필요한 권한은 그보다 훨씬
좁다.** 여러 사람에게 배포하기 전에 이것부터 확인한다.

| 하는 일 | 필요한 권한 | 없으면 |
| --- | --- | --- |
| 변경 목록 조회 | `dbo.DBVC_ChangeLog`에 `SELECT` | `변경 로그를 읽지 못했습니다` 가 화면에 뜬다 |
| 커밋 뒤 로그 닫기 | `dbo.DBVC_ChangeLog`에 `UPDATE` | 커밋은 성공하는데 그 항목이 새로고침마다 되살아난다 |
| 객체 스크립팅 | 대상 객체에 `VIEW DEFINITION` | 그 객체만 조용히 빠진다 |
| 스키마 변경 자체 | 평소 쓰던 DDL 권한 그대로 | — |

앞의 둘은 **설치 스크립트가 `public`에 부여하므로 별도 조치가 필요 없다**(0.5.14 / 스키마 v5부터,
v6에도 그대로 유지된다). 그 이전 버전으로 초기화한 데이터베이스는 **변경 추적기 업데이트**를
한 번 눌러야 부여된다 — 스키마 v6부터는 같은 버튼이 `DBVC_ChangeLog`를 30일 지나면 지우는
`dbo.DBVC_PurgeChangeLog` 프로시저도 함께 설치한다.

> **업데이트 당일, 공용 DB가 잠깐 뻑뻑해질 수 있다.** 정리에는 별도 실행 트리거나 스케줄이 없다 —
> 새로고침마다 불리므로, 업데이트 뒤 처음 새로고침을 누른 클라이언트가 그 자리에서 수백만 행을
> 지울 수도 있다. 개발자 20명·DBA 3명이 같은 DB를 쓰는 환경에서는 여러 사람이 업데이트 당일에
> 몰려 각자 첫 정리를 동시에 돌릴 수 있다. 배치(5,000행)로 나누므로 한 번에 수백만 행을 잠그는
> 일은 없다. 다만 5,000행은 SQL Server가 행 잠금을 테이블 잠금으로 승격시키는 기준선이라
> 배치마다 `DBVC_ChangeLog`에 테이블 잠금이 걸릴 수 있고, 그동안은 그 표에 쓰는 쪽(DDL)뿐
> 아니라 읽는 쪽(다른 사람의 새로고침)도 함께 기다린다. 몇 분 동안 공용 DB가 평소보다
> 느리게 느껴질 수 있다.

- [ ] **`db_owner`가 아닌 계정으로 한 번 시험한다.** 팀의 대다수가 그 상태일 수 있고, 증상이
      조용하기 때문에 실제로 눌러 보기 전에는 드러나지 않는다.
  ```sql
  -- 부여되어 있는지 본다. SELECT와 UPDATE 두 줄이 나와야 한다.
  SELECT dp.permission_name
  FROM sys.database_permissions dp
  WHERE dp.major_id = OBJECT_ID(N'dbo.DBVC_ChangeLog')
    AND USER_NAME(dp.grantee_principal_id) = N'public';
  ```

- [ ] **`VIEW DEFINITION`은 조직이 정한다.** 설치 스크립트가 부여하지 않는다 — 범위가
      `DBVC_ChangeLog` 한 테이블이 아니라 데이터베이스 전체라 성격이 다르고, 무엇을 형상 관리
      대상으로 볼지는 팀의 결정이기 때문이다. 개발 DB에서는 보통 이미 열려 있다.

---

## 6단계 — 폐쇄망 PC 전개

0단계의 방화벽 승인이 난 뒤에 진행한다.

- [ ] **방화벽이 실제로 열렸는지 확인한다.** 운영 PC에서:
  ```powershell
  # 포트만 먼저 본다 (Windows 11 기본 포함 cmdlet, 키 없이도 결과가 나온다)
  Test-NetConnection -ComputerName <gitlab-호스트> -Port 22
  ssh -T git@<gitlab-호스트>
  ```
  `TcpTestSucceeded : True` 면 열린 것이다. `ssh` 쪽에서 `Connection timed out`이면 아직 안 열린 것이고,
  `Permission denied (publickey)` 는 **포트가 열렸다는 뜻이므로 성공**이다(키를 아직 안 올렸을 뿐).
  > `Test-NetConnection`은 응답이 없으면 20초 남짓 기다린 뒤 실패로 끝난다 — 멈춘 것이 아니다.

- [ ] `.vsix` 파일을 사내 반입 절차에 따라 운영 PC로 옮긴다. 옮긴 뒤 차단 표시를 푼다.
  ```powershell
  Unblock-File .\DBVC.Vsix.vsix
  ```

- [ ] **2단계를 운영 PC에서 반복한다.** 키는 기계마다 따로 만드는 것을 권한다.
  - [ ] `ssh -V` 로 OpenSSH 클라이언트 확인 (Windows 11 기본 포함. 사내 이미지에서 빠져 있으면
        폐쇄망에서는 `Add-WindowsCapability`가 Windows Update에 닿지 못할 수 있다 — 이때는
        Git for Windows가 함께 설치하는 `ssh.exe`를 쓰거나 사내 배포 서버(WSUS/SCCM)에 요청한다)
  - [ ] `ssh-keygen -t ed25519` 로 키 생성
  - [ ] 공개키를 **GitLab** 에 등록: 우측 상단 아바타 > Preferences > SSH Keys
  - [ ] `ssh -T git@<gitlab-호스트>` 로 접속 확인 및 `known_hosts` 등록 (`yes` 입력)

- [ ] **GitLab에 프로젝트를 만든다.** README 포함(빈 저장소가 되지 않도록), Private.

- [ ] **터미널에서 받는다.** 3단계와 같다.
  ```powershell
  git clone git@<gitlab-호스트>:<그룹>/db-schema-<데이터베이스명>.git
  ```
  > GitLab이 비표준 SSH 포트를 쓴다면 URL이 `ssh://git@<호스트>:2222/<그룹>/<프로젝트>.git`
  > 형태가 된다. GitLab 프로젝트 페이지의 Clone 버튼이 알려주는 값을 그대로 쓴다.

  > **받을 위치.** OneDrive가 동기화하는 폴더(바탕 화면·문서)는 피한다. Windows 11에서는
  > 이 폴더들이 기본으로 OneDrive 백업 대상이라 `.git` 내부 파일이 동기화와 충돌할 수 있다.

- [ ] `git -C <폴더> status -sb` 로 추적 브랜치 확인.

- [ ] **3단계처럼 `develop`과 `master` 브랜치를 만들어 push한다.** GitLab은 별도 원격이라
      GitHub에 만든 브랜치가 따라오지 않는다. 8단계의 배포·감사 클론이 이 브랜치들을 고정
      브랜치로 쓴다.

- [ ] **4단계를 운영 PC에서 반복한다** (VSIX 설치 → 연결 → 저장소 연결).

- [ ] **5단계를 운영 PC에서 반복한다** (DBVC 초기화 → 새로고침 → Commit → Push → Pull).

---

## 7단계 — 설치 확인 (각 기계에서)

설치가 끝났는지만 본다. **기능이 의도대로 도는지 훑는 회귀 목록은
[`ssms-manual-verification.md`](ssms-manual-verification.md) 2부에 있다** — 릴리스마다 밟는
것이라 설치자의 일이 아니다.

- [ ] 저장 프로시저를 하나 `ALTER` 한 뒤 **새로고침** → 목록에 `수정`으로 뜨는지
- [ ] 항목 선택 → 하단 **비교** 탭에 좌(이전)/우(현재) 코드가 보이는지
- [ ] 항목 선택 → **이력** 탭에 그 객체의 커밋(날짜·작성자·메시지·SHA)이 뜨는지
- [ ] 도구 창 **오른쪽 위 버전**이 방금 설치한 `.vsix`의 버전과 같은지
      (`알 수 없음` 이거나 `1.0.0` 이면 빌드 배선이 끊긴 것이다. 숫자가 이전 버전 그대로면
      설치 관리자가 같은 버전이라며 건너뛴 것이므로, 버전을 올려 다시 빌드하거나 먼저 제거한다)
- [ ] 객체를 `DROP` 한 뒤 **새로고침** → `삭제`로 뜨고, 체크해서 Commit하면 저장소에서도
      파일이 사라지는지

---

## 8단계 — 배포·감사 클론 만들기 (테스트/운영 PC)

1~7단계는 **개발 DB(`Write`)** 클론 하나를 기준으로 적었다. 테스트·운영 DB의 차이를 검사하려면
**별도 폴더에 별도 클론**을 하나 더 만들어야 한다 — 개발 클론과 폴더를 같이 쓰면 검사할 때마다
브랜치를 갈아타야 하고, 그 클론에 미커밋 변경이 남아 있으면 전환 자체가 막힌다.

- [ ] 클론을 놓을 폴더를 개발 클론과 **다르게** 정한다. 예: 개발 `...\dbvc\dev`, 테스트
      `...\dbvc\test`, 운영 `...\dbvc\prod`. 같은 PC에 셋이 있어도 폴더가 나뉘어 있으면
      간섭하지 않는다.
- [ ] SSMS 개체 탐색기에서 **그 DB**(테스트 DB 또는 운영 DB)에 접속한 뒤 DBVC 창에서
      **연결**을 누른다.
- [ ] 배너의 **저장소 연결...** 을 누른다. 이미 받아둔 폴더를 연결하든 그 자리에서 새로
      받든, 대화상자에 **용도** 선택(개발 / 배포 / 감사)과 **고정 브랜치** 입력이 함께 있다.
- [ ] **용도로 "배포" 또는 "감사"를 고른다.** 테스트 DB는 배포, 운영 DB는 감사다.
- [ ] **고정 브랜치를 확인한다.** 용도를 고르면 칸이 함께 채워진다(0.9.5) — 배포는 `develop`,
      감사는 `master`, 개발은 빈 칸이다. 조직이 다른 이름을 쓰면 그 자리에서 고쳐 적는다.
      **배포·감사는 이 칸을 비우면 연결이 거부된다** — 고정 없는 배포 클론은 "어느 브랜치와
      비교하는지" 자체가 없어지는 사고를 그대로 허용하기 때문이다. 개발 용도는 이 칸을
      비운 채로 둔다(브랜치를 자유롭게 바꾸는 클론이므로).
- [ ] 연결이 끝나면 화면에 **초기화 오버레이가 뜨지 않고**, 대신 **`[차이 검사]`**·
      **`[배포 스크립트 저장...]`** 두 버튼만 있는 패널이 뜨는지 확인한다. 이 대상에서는
      새로고침·Commit·Push·초기화가 모두 잠겨 있다 — 배포·감사 클론은 대상 DB를 바꾸지 않고
      **비교만** 하기 때문이다.
- [ ] `[차이 검사]`를 눌러 본다. 원격을 먼저 Pull한 뒤 대상 DB 전체를 고정 브랜치와 비교하며,
      **저장소에는 아무것도 쓰지 않는다.** 원격이 없거나 현재 브랜치에 추적 중인 원격 브랜치가
      없으면 Pull은 건너뛰고 비교만 한다 — 원격이 있는데 실패하면 사유를 띄우고 멈춘다.
- [ ] 차이가 있으면 목록에서 항목을 골라 좌(브랜치)/우(데이터베이스) diff가 뜨는지 확인한다.
- [ ] `[배포 스크립트 저장...]`으로 파일을 만들고, 머리말에 제외된 객체와 사유(수동 변경 필요 /
      브랜치에 없음 / 스크립트로 만들 내용 없음 / 스크립팅에 실패해 판정하지 못함)가 나뉘어
      있는지 확인한다.
- [ ] 만들어진 스크립트를 **SSMS 쿼리 창에서 직접 실행한다.** DBVC는 스크립트를 실행하지
      않는다 — 실행은 항상 사람의 몫이다.
- [ ] 실행한 뒤 `[차이 검사]`를 다시 눌러 방금 반영한 항목이 목록에서 사라졌는지 확인한다.
      남아 있으면 스크립트 실행이 일부만 성공했거나, 자동화되지 않아 손으로 처리해야 하는
      항목이라는 뜻이다.

> **감사(`Audit`) 대상의 차이는 전부 "확인 필요"로만 뜬다.** 운영 DB에는 DDL 트리거를 설치할 수
> 없어서, DBVC는 "아직 배포 안 됨"과 "누군가 운영을 직접 고침"을 구분할 방법이 없다. 두 경우
> 모두 같은 문구로 보고하며, 어느 쪽인지는 DBA가 직접 판단한다.

> **배포·감사 저장소에 커밋되지 않은 변경이 있으면 화면이 통째로 차단된다.** 비교 기준은
> "고정 브랜치의 내용"인데 실제로 읽는 파일은 작업 트리이기 때문이다 — 미커밋 편집이 있으면
> 그 편집이 브랜치인 척하게 되어 결과가 조용히 거짓이 된다. 이 클론은 애초에 커밋을 하지
> 않으므로, 이런 상태가 생겼다면 십중팔구 외부 Git 클라이언트(또는 텍스트 편집기)로 파일을
> 직접 건드린 것이다. **외부 Git 클라이언트에서 `git status`로 무엇이 바뀌었는지 확인하고
> `git checkout -- .`(또는 `git reset --hard`)로 되돌린 뒤 DBVC에서 다시 연결한다.** DBVC 창
> 안에는 되돌리는 기능이 없다 — 배포·감사 클론은 원래 아무것도 쓰지 않아야 하는 저장소이므로,
> 되돌리기는 상태를 복구하는 것이지 DBVC의 정상 기능이 아니다.

> **병합 기능(0.8.0)의 확인 항목은** [`ssms-manual-verification.md`](ssms-manual-verification.md)
> 1부 F절에 있다. 원격에 `master`와 운영 클론이 더 있어야 해서 설치와는 준비가 다르다.

### `master` 기준선을 만든다

**운영 DB에는 아무것도 설치되지 않는다.** 이 절차가 운영에 하는 일은 읽기 하나뿐이고, 그것은
앞으로 감사 클론이 매주 하게 될 바로 그 읽기다. 트리거도 테이블도 만들지 않는다.

감사 클론은 추출·커밋이 잠겨 있어 스스로 `master`를 채우지 못한다. 그래서 별도 도구
(`tools/DBVC.Baseline`)로 한 번 만든다. `master` 브랜치는 이미 만들어 두었다 — 개발 노트북의
GitHub 저장소는 3단계에서, 폐쇄망 GitLab 저장소는 6단계에서.

- [ ] **그 `master`를 체크아웃한 빈 클론**을 하나 만든다. 운영 DB를 읽으므로 **운영이 보는
      원격**(폐쇄망이면 GitLab)의 `master`여야 한다. 개발·테스트·운영 클론과 다른 폴더여야
      한다 — 도구는 `.sql`이 이미 있는 폴더를 거부한다.
- [ ] **읽기 계정을 확인한다.** 그 계정에 대상 객체의 `VIEW DEFINITION`이 있어야 한다.
      없는 객체는 조용히 빠지고, 나중에 "브랜치에만 있음"으로 떠서 배포 스크립트에 `CREATE`가
      들어간다.
- [ ] **도구를 실행한다.** 암호는 인자로 받지 않고 실행 중에 묻는다.

      ```powershell
      dotnet run --project tools/DBVC.Baseline -f net48 -- `
        --server <운영서버> --database <DB> --repo <그 클론 경로> --sql-user <읽기 계정>
      ```

- [ ] **종료 코드로 판단한다.**

      | 코드 | 뜻 | 할 일 |
      | --- | --- | --- |
      | 0 | 추출·검증 모두 깨끗 | `git status`로 확인하고 커밋·push |
      | 1 | 스크립팅에 실패한 객체가 있다 — 또는 접속·자격 증명 오류로 추출이 아예 시작되지 못했다 | 화면에 출력된 사유를 먼저 읽는다. 객체별 실패라면 그 객체에 `VIEW DEFINITION`을 부여하고, 아래로 클론을 비운 뒤 다시 실행 |
      | 2 | 검증에서 차이가 나왔다 | **커밋하지 않는다.** 실행 중 운영이 바뀌었을 수 있으니 아래로 클론을 비운 뒤 다시 실행 |
      | 3 | 인자·사전 점검 오류 | 출력된 사유대로 고친다 |

      > **1·2번 코드로 다시 실행하기 전에 클론을 비운다.** 추출이 실패해도 성공한 객체의
      > `.sql`은 클론에 그대로 남는다(설계가 그렇게 정했다) — 지우지 않고 다시 실행하면
      > 도구가 `이미 .sql 파일이 있습니다`로 거부하고 엉뚱하게 3번 코드가 뜬다. 이 클론은
      > 기준선 전용이고 아직 커밋 전이므로 작업 트리를 통째로 되돌리면 된다.
      > ```powershell
      > git -C <그 클론 경로> clean -fd
      > git -C <그 클론 경로> reset --hard
      > ```

- [ ] **클론을 정리한다.** 그대로 감사 클론으로 써도 되고, 폐기하고 8단계에서 새로 만들어도 된다.

도구는 커밋하지 않는다. 운영 사진이 사람 눈을 거치지 않고 `master`에 올라가는 것은 자동화할
자리가 아니기 때문이다.

---

## AI 커밋 메시지 설정 (선택)

커밋 메시지 칸 옆의 **AI 생성** 버튼을 쓰려면 이 절을 따른다. **이 절 전체가 선택이다** —
설정하지 않아도 DBVC의 나머지는 전부 그대로 동작한다.

- [ ] SSMS에서 **도구 > 옵션 > DBVC > AI 커밋 메시지**를 연다.
- [ ] **주소와 모델에 사내 서버 기본값이 이미 들어 있다**(`http://172.20.100.40`,
      `pelly:latest`). 그대로 쓸 것이면 다음 두 항목을 건너뛴다.
- [ ] 다른 프로바이더를 쓸 때만: **프로바이더 주소**에 기준(base) 주소를 넣는다 —
      `/chat/completions`까지 붙은 전체 주소가 아니다. 전체 주소를 넣으면 DBVC가 그 뒤에
      `/chat/completions`를 한 번 더 이어 붙여 경로가 겹치고 404가 난다. 도구가 주소를 대신
      고쳐 주지 않으므로 직접 맞게 적는다. 기준이 어디까지인지는 서버가 정한다 — OpenAI를
      비롯한 대부분은 `/v1`까지이고(`https://api.openai.com/v1`), 앞단 프록시가 바로 받는
      서버는 호스트 자체가 기준이다.
- [ ] 다른 프로바이더를 쓸 때만: **모델**을 그 서버가 올려 둔 이름으로 바꾼다.
- [ ] **API 키**(필요하면)·**응답 대기(초)**·**보낼 diff 최대 줄 수**를 확인한다.
      인증이 필요 없는 사내 서버라면 API 키는 비워 둔다.
- [ ] **연결 테스트**를 눌러 주소·모델·키가 맞는지 확인한다. 여기서 통과해 두면 커밋하려는
      순간에야 주소가 틀린 것을 알게 되는 일이 없다.
- [ ] 확인을 눌러 저장한다.
- [ ] `%APPDATA%\DBVC\ai-settings.json`이 생겼고, API 키 **원문이 그 안에 없는지** 확인한다.
  ```powershell
  Get-Content $env:APPDATA\DBVC\ai-settings.json
  ```
  `protectedApiKey` 값이 입력한 키와 다른, 암호화된 문자열로 보이면 통과다. API 키를 비워
  두었다면 이 값은 `null`이다.

> AI 기능이 의도대로 도는지(목적지 확인 대화상자, 덮어쓰기 취소, 실패했을 때 창이 살아 있는지)는
> [`ssms-manual-verification.md`](ssms-manual-verification.md) 2-11에 있다.

---

## 알려진 제약

작업 전에 알고 있으면 좋은 것들이다. 결함이 아니라 현재 설계의 경계다.

- **인증은 SSH만.** HTTPS 원격은 인증할 수 없다. 폐쇄망 방화벽이 끝내 안 열리면 HTTPS + 액세스 토큰
  방식을 새로 설계해야 하며, 사유와 조건은
  [specs/2026-08-03-dbvc-ssh-first-git-auth-design.md](superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md) 3절에 있다.
- **인증 정보는 SSMS 프로세스와 함께 산다.** 디스크에 남지 않으므로 다른 기계로 옮길 것도 없고,
  SSMS를 닫으면 사라진다. 다시 열었을 때는 개체 탐색기에 접속한 뒤 연결을 한 번 누른다.
- **DDL 변경 이력의 `LoginName`은 실제 접속 계정을 기록한다.** SQL 인증으로 모두가 같은 로그인을
  공유하면 `DBVC_ChangeLog.LoginName`으로 사람을 구분할 수 없다. 현재 화면에는 이 값을 쓰지 않지만
  (Git 커밋 작성자는 `git config`에서 온다), 사람별 추적이 필요하면 로그인을 나눈다.
- **Push는 커밋만 올린다.** 작업 트리와 커밋 이력은 변하지 않으므로 실패해도 잃을 것이 없다 —
  성공하면 원격 추적 ref(`refs/remotes/...`)만 갱신된다. 원격이 앞서 있으면 거부되며, Pull로
  받아 병합한 뒤 다시 누른다. force push는 제공하지 않는다.
- **Pull은 파일만 가져온다.** 받은 `.sql` 을 데이터베이스에 적용할지는 사용자가 판단한다.
  DBVC는 스크립트를 실행하지 않는다.
- **변경 감지는 새로고침 시점.** DDL 트리거가 발생 즉시 `DBVC_ChangeLog` 에 기록하지만,
  화면 반영은 새로고침·연결·DBVC 초기화·Commit 직후에만 일어난다. 주기적 폴링은 하지 않는다.
- **DBVC를 걷어낼 때는 트리거를 먼저 지운다.** `DBVC_ChangeLog` 만 지우고 트리거를 남기면
  그 데이터베이스의 **이후 모든 DDL이 실패하고 롤백된다** — 트리거가 없는 테이블에 INSERT하려다
  오류 208을 내고, 그 오류가 배치를 중단시키기 때문이다. `DROP TABLE` 자체는 트리거가 자기 이름과
  `DBVC_ChangeLog` 를 예외로 두고 있어 성공하므로, 증상은 *다음* 문장에서야 드러난다.
  순서는 이렇다:

  ```sql
  DROP TRIGGER [trg_DBVC_DDL_Tracker] ON DATABASE;
  DROP TABLE [dbo].[DBVC_ChangeLog];
  ```

- **전환 이전 커밋의 diff는 바이너리로 남는다.** 0.5.15가 추출물 인코딩을 UTF-8로 바꿨지만
  과거 커밋의 블롭은 그대로다. 이력을 다시 쓰면(`git filter-repo`) 고칠 수 있으나 저장소를
  가진 모든 사람이 클론을 다시 받아야 하므로 하지 않는다. 전환 이후의 변경은 정상적으로 보인다.
- **Object Explorer 상태 아이콘 오버레이는 미구현.** 만들 수는 있지만 펼친 노드만 칠할 수 있어
  상태를 반쪽만 보여 주므로 보류했다
  (Feature 10, [plans/2026-08-01-dbvc-object-explorer-overlay.md](superpowers/plans/2026-08-01-dbvc-object-explorer-overlay.md)).
  변경 상태는 DBVC 창에서 확인한다.

---

## 막혔을 때

| 증상 | 확인할 것 |
| --- | --- |
| 메뉴에 DBVC가 없다 | **보기 메뉴 본체**를 봤는지 ("다른 창" 안이 아니다). SSMS를 완전히 종료한 뒤 `.vsix` 재설치. 확장 관리자에서 설치 여부 확인 |
| `.vsix` 설치가 "관리 권한이 있어야 합니다"로 끝난다 | 관리자 권한으로 설치해야 한다. UAC 승인 창을 놓쳤는지 확인 |
| SSMS가 아니라 Visual Studio에 설치됐다 | 두 제품이 다 있을 때 생길 수 있다. VS에서 제거하고, SSMS의 `VSIXInstaller.exe`에 `/instanceIds:<SSMS 인스턴스ID>` 를 주어 설치한다 (`vswhere.exe -all -products *` 로 ID 확인) |
| "저장소 연결..."이 오류를 낸다 | 고른 폴더에 `.git` 이 있는지. clone된 최상위 폴더인지 |
| DBVC 초기화가 실패한다 | 0단계의 권한 확인. `CREATE TABLE`·`CREATE TRIGGER` 권한. 트리거가 `dbo`로 실행되므로 계정이 `db_owner`인지도 확인한다 (5단계 첫 항목) |
| 연결이 "로그인하지 못했습니다"를 낸다 | 개체 탐색기의 그 연결로는 접속되는지, 그리고 서버가 혼합 모드인지 (`SERVERPROPERTY('IsIntegratedSecurityOnly')` 가 `0`) |
| 연결이 "암호를 사용할 수 없습니다"를 낸다 | 개체 탐색기가 그 연결의 암호를 들고 있지 않다. 개체 탐색기에서 해당 서버에 다시 접속한 뒤 연결을 누른다 |
| 연결이 "개체 탐색기에서 ... 선택한 뒤"를 낸다 | 선택이 없거나, 여러 개이거나, 서버 노드다. 데이터베이스 노드 하나를 고른다 |
| 창 위쪽에 "변경 추적기가 구버전입니다"가 뜬다 | 0.2.6 이전에 초기화한 데이터베이스다. **추적기 업데이트** 를 누른 뒤 **전체 다시 추출** 을 한 번 실행한다. 배포·감사 용도에서는 0.7.3부터 뜨지 않는다 — 그런데도 뜨면 그 매핑이 개발 용도로 잡혀 있는 것이다 |
| 새로고침해도 목록이 비어 있다 | DDL 트리거 설치 확인. 트리거 설치 **이후에** 변경한 객체만 잡힌다 |
| Pull이 영문 메시지를 낸다 | 안내가 붙지 않은 경우다. 원격 URL이 SSH도 HTTPS도 아닌 형태인지 확인 |
| Pull이 `known_hosts` 를 말한다 | Git 클라이언트에서 `ssh -T git@<호스트>` 를 한 번 실행해 `yes` 입력 |
| 커밋했는데 원격에 없다 | 커밋과 Push는 별개다. **Push** 버튼을 누른다 |
| 되돌렸는데 새로고침하면 다시 나타난다 | 정상이다. 되돌리기는 파일만 되돌리고 데이터베이스의 변경은 남긴다. 목록에서 치우려면 되돌린 뒤 그 항목을 커밋한다 |
| Push가 거부된다 | 원격에 먼저 올라간 커밋이 있다. **Pull** 로 받아 병합한 뒤 다시 Push. 그래도 거부되면 브랜치 보호·권한을 확인 |
| `type %APPDATA%\...` 가 "경로를 찾을 수 없습니다"를 낸다 | PowerShell에서는 `%VAR%` 가 확장되지 않는다. `Get-Content $env:APPDATA\...` 를 쓴다 |
| `--add ... ^` 붙여넣기가 깨진다 | `^` 는 명령 프롬프트 전용 줄바꿈이다. PowerShell에서는 백틱(`` ` ``)을 쓰거나 한 줄로 붙여 쓴다 |
| `ssh-add` 가 "에이전트에 연결할 수 없습니다"를 낸다 | Windows 11에서 `ssh-agent` 서비스가 사용 안 함이다. 2단계의 `Set-Service ssh-agent -StartupType Automatic` + `Start-Service` (관리자 권한) |
| 설정 앱에서 "선택적 기능"을 못 찾는다 | Windows 11은 **설정 > 시스템** 아래다 (Windows 10은 앱 아래였다). `start ms-settings:optionalfeatures` 로 바로 연다 |
| `.vsix` 를 열면 "이 파일을 열 수 없습니다"가 뜬다 | 다른 기계에서 복사해 온 파일의 차단 표시다. `Unblock-File .\DBVC.Vsix.vsix` 후 다시 시도 |
