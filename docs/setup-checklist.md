# DBVC 도입 체크리스트

제로 상태에서 DBVC를 실제로 쓰기까지의 순서다. 위에서부터 차례로 진행하고 완료한 항목에 체크한다.

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
- [ ] 사내 GitLab에서 **새 프로젝트를 만들 권한**이 있는지 확인한다. 없으면 관리자에게 요청한다.
- [ ] 개발 노트북에 **Visual Studio 2022**가 설치되어 있고 **Visual Studio 확장 개발** 워크로드가
      포함되어 있는지 확인한다. `.vsix`를 만들려면 이 워크로드가 필요하다.
- [ ] 두 기계에 **SSMS 21**이 설치되어 있는지 확인한다.
- [ ] 두 기계가 **Windows 11**인지 확인한다. Windows 10에서도 동작하지만 이 문서의 설정 앱 경로는
      Windows 11 기준이다.
  ```powershell
  Get-ComputerInfo -Property OsName,OsVersion | Format-List
  ```
  `OsName`에 `Windows 11`이 나오면 된다 (`OsVersion`은 Windows 11도 `10.0.x`로 시작한다 — 정상이다).
- [ ] 두 기계에서 **로컬 관리자 권한**이 있는지 확인한다. `.vsix` 설치가 전체 사용자 설치라
      UAC 승인이 필요하다 (4단계). 없으면 그 단계에서 막힌다.
      Windows 11 Home / Pro 어느 쪽이든 상관없다.
  ```powershell
  whoami /groups | Select-String 'S-1-5-32-544'
  ```
  `BUILTIN\Administrators` 줄이 보이면 통과다. 관리자 권한 없이 연 창에서는 그 줄에
  `Group used for deny only`(권한 거부용) 가 함께 붙는데, **정상이다** — UAC가 승인 전까지
  권한을 낮춰 둔 것뿐이고 4단계에서 "예"를 누르면 올라간다. 줄 자체가 안 나오면 관리자가 아니다.
- [ ] 각 기계에서 **어떤 인증으로 SQL Server에 붙을지** 정한다. DBVC는 **Windows 통합 인증과
      SQL Server 인증을 모두** 지원하며, (서버, 데이터베이스)마다 따로 기억한다.
      개발 노트북은 Windows 인증, 폐쇄망 운영 PC는 SQL 인증처럼 섞어 써도 된다.
  - SQL 인증을 쓸 서버는 **혼합 모드**여야 한다:
    `SELECT SERVERPROPERTY('IsIntegratedSecurityOnly');` 이 `0`이면 SQL 인증 가능(`1`이면 Windows 전용).

- [ ] 위에서 정한 계정으로 대상 데이터베이스에 다음이 가능한지 확인한다.
  - 테이블 생성 (`DBVC_ChangeLog` 생성용)
  - DDL 트리거 생성 (`CREATE TRIGGER ... ON DATABASE`)
  - 스키마 객체 조회 (스크립트 추출용)
  - `dbo` 가장 (트리거가 `WITH EXECUTE AS 'dbo'`로 실행되므로 `db_owner`여야 한다)

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

- [ ] 소스를 받는다.
  ```powershell
  git clone https://github.com/moondae815/dbvc.git
  cd dbvc
  ```
- [ ] 빌드한다. 일반 PowerShell 창에서 그대로 된다 — 개발자용 셸은 필요하지 않다.
  ```powershell
  dotnet build src\DBVC.Vsix\DBVC.Vsix.csproj -c Release
  ```
- [ ] 산출물이 실제로 생겼는지 확인한다. **경로에 `net48`이 들어간다.**
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

- [ ] 만들어진 `.vsix` 파일을 **따로 보관한다.** 6단계에서 폐쇄망 PC로 옮겨야 한다.

---

## 2단계 — SSH 준비 (개발 노트북)

DBVC는 자격 증명을 묻지도 저장하지도 않는다. libgit2가 시스템 `ssh`에 그대로 넘기므로,
평소 쓰는 Git과 똑같은 SSH 설정을 그대로 물려받는다.

- [ ] **OpenSSH 클라이언트가 있는지 확인한다.** Windows 11에는 기본으로 들어 있어
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

- [ ] **키를 만든다.** 이미 `~\.ssh\id_ed25519`가 있으면 건너뛴다.
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

- [ ] **공개키를 GitHub에 등록한다.** `~\.ssh\id_ed25519.pub` 내용을 통째로 복사해
      GitHub > Settings > SSH and GPG keys > New SSH key.
  ```powershell
  # 화면으로 확인
  Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub
  # 클립보드로 바로 복사
  Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | Set-Clipboard
  ```
  > `.pub` 이 붙은 **공개키** 파일이다. 확장자 없는 `id_ed25519`(개인키)는 절대 올리지 않는다.

- [ ] **접속을 확인한다.** 이 단계가 `known_hosts` 등록을 겸한다.
  ```powershell
  ssh -T git@github.com
  ```
  처음이면 `Are you sure you want to continue connecting (yes/no)?`가 뜬다 — **`yes`를 입력한다.**
  `Hi <사용자명>! You've successfully authenticated...`가 나오면 성공이다.

> **이 확인을 건너뛰지 않는다.** DBVC 도구 창 안에서는 호스트 신뢰 여부를 묻는 프롬프트에
> 답할 방법이 없어서, `known_hosts`에 없는 호스트로는 Pull이 그냥 실패한다.

---

## 3단계 — 스키마 저장소 만들기 (개발 노트북)

**원격을 먼저 만들고 clone하는 순서로 진행한다.** `git init`으로 시작하면 추적 브랜치가 없어
Pull과 Push 모두 거부되고(그 상태를 DBVC가 한국어로 안내는 하지만), 별도로 `git push -u`를 해줘야 한다.
clone은 그 문제를 애초에 만들지 않는다.

- [ ] GitHub에서 **새 저장소를 만든다.** 이름 예: `db-schema-<데이터베이스명>`.
      **"Add a README file"을 체크한다** — 빈 저장소는 clone해도 브랜치가 없다.
      사내 스키마이므로 **Private**로 만든다.

- [ ] **SSH URL로 clone한다.** HTTPS URL이 아니라 SSH URL이어야 한다.
  ```powershell
  New-Item -ItemType Directory -Force C:\dbvc-repos | Out-Null
  cd C:\dbvc-repos
  git clone git@github.com:<계정>/db-schema-<데이터베이스명>.git
  ```
  > SSH URL은 `git@github.com:...` 형태다. `https://github.com/...`을 쓰면 DBVC가 Pull에서
  > 거부하면서 SSH로 바꾸는 방법을 안내한다.

  > **폴더 위치.** `C:\` 바로 아래에 폴더를 만들려면 관리자 권한이 필요할 수 있다. 필요하면
  > `$env:USERPROFILE\dbvc-repos` 처럼 사용자 폴더 아래로 잡아도 된다 — DBVC는 경로를 가리지 않는다.
  > 다만 **OneDrive가 동기화하는 폴더(바탕 화면·문서)** 는 피한다. Windows 11에서는 이 폴더들이
  > 기본으로 OneDrive 백업 대상이라 `.git` 내부 파일이 동기화와 충돌할 수 있다.

- [ ] **추적 브랜치가 설정됐는지 확인한다.** clone했다면 자동으로 되어 있다.
  ```powershell
  git -C db-schema-<데이터베이스명> status -sb
  ```
  첫 줄이 `## main...origin/main` 처럼 `...` 뒤에 원격 브랜치가 보이면 통과.
  `## main` 만 보이면 추적이 없는 것이다:
  ```powershell
  git -C db-schema-<데이터베이스명> push -u origin main
  ```

- [ ] clone된 폴더의 **전체 경로를 적어둔다.** 4단계에서 DBVC에 입력한다.

---

## 4단계 — SSMS에 설치하고 첫 연결 (개발 노트북)

- [ ] **SSMS 21을 완전히 종료한다.**
- [ ] 1단계에서 만든 `.vsix`를 더블클릭해 설치한다. **UAC 창이 뜨면 "예"를 누른다.**
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
- [ ] SSMS 21을 실행하고 **View(보기) 메뉴 > DBVC**를 연다. 메뉴 아래쪽에 있다.
      메뉴에 항목이 없으면 설치가 안 된 것이다 — SSMS를 껐다 켜고 다시 확인한다.
  > "다른 창(Other Windows)" 안이 **아니다.** SSMS에서는 그 하위 메뉴 자체가 숨겨져 있어
  > 거기에 넣으면 보이지 않는다 (Visual Studio와 다른 점이다).

- [ ] **개체 탐색기**에서 0단계에서 정한 계정으로 대상 데이터베이스(또는 그 하위 개체)에 먼저
      접속해 둔다. Server/Database, 인증 방식, 계정은 모두 그 연결에서 그대로 온다 — DBVC 창에는
      입력란이 없다.

      인증 정보는 개체 탐색기의 연결에서 그대로 오며 디스크에 저장되지 않는다.
      SSMS를 다시 열면 개체 탐색기에 접속한 뒤 연결을 한 번 더 누른다.

- [ ] 개체 탐색기에서 대상 데이터베이스를 선택한 뒤 **연결** 을 누른다. 접속에 실패하면 배너에
      한국어 사유가 뜬다 (로그인 실패, 서버 도달 불가 등). 성공하면 아래 매핑 경고로 넘어간다.

- [ ] 경고 배너 `현재 데이터베이스에 연결된 Git 저장소가 없습니다.` 가 뜨는지 확인한다.
      **뜨는 것이 정상이다** — 아직 매핑하지 않았다.

- [ ] 배너의 **"저장소 연결..."** 버튼을 누르고 3단계에서 clone한 폴더를 선택한다.
      배너가 사라지면 성공이다.
  > Git 저장소가 아닌 폴더를 고르면 오류가 나고 매핑되지 않는다. `.git` 폴더가 있는
  > 최상위 폴더를 골라야 한다.

- [ ] 매핑이 저장됐는지 확인한다.
  ```powershell
  Get-Content $env:APPDATA\DBVC\mappings.json
  ```

- [ ] `%APPDATA%\DBVC` 에 `credentials.json` 이 **없는지** 확인한다. 이전 버전이 남긴 파일이
      있었다면 확장이 처음 로드될 때 지워진다.
  ```powershell
  # 아무것도 출력되지 않으면 통과
  Get-ChildItem $env:APPDATA\DBVC -Filter credentials.json
  ```

---

## 5단계 — 데이터베이스 초기화 (개발 노트북)

- [ ] **초기화하는 계정이 `db_owner`인지 확인한다.** 트리거를 `dbo` 권한으로 실행하도록 만들기 때문에
      `dbo`를 가장할 수 있어야 한다. 권한이 부족하면 초기화가 실패하고 사유가 그대로 표시된다.

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

- [ ] **SSH URL로 clone한다.**
  ```powershell
  git clone git@<gitlab-호스트>:<그룹>/db-schema-<데이터베이스명>.git
  ```
  > GitLab이 비표준 SSH 포트를 쓴다면 URL이 `ssh://git@<호스트>:2222/<그룹>/<프로젝트>.git`
  > 형태가 된다. GitLab 프로젝트 페이지의 Clone 버튼이 알려주는 값을 그대로 쓴다.

- [ ] `git -C <폴더> status -sb` 로 추적 브랜치 확인.

- [ ] **4단계를 운영 PC에서 반복한다** (VSIX 설치 → 연결 → 저장소 연결).

- [ ] **5단계를 운영 PC에서 반복한다** (DBVC 초기화 → 새로고침 → Commit → Push → Pull).

---

## 7단계 — 동작 검증 (각 기계에서)

여기부터는 기능이 의도대로 도는지 확인하는 항목이다. CI가 검증할 수 없는 것들이다.

### 기본 흐름

- [ ] 저장 프로시저를 하나 `ALTER` 한 뒤 **새로고침** → 목록에 `수정`으로 뜨는지
- [ ] SSMS **테이블 디자이너로 새 테이블을 만든** 뒤 **새로고침** → `수정`이 아니라 `추가`로 뜨는지
      (디자이너는 저장 한 번에 `CREATE_TABLE` 뒤로 `ALTER_TABLE`을 더 흘린다)
- [ ] 항목 선택 → **비교** 탭에서 좌(이전)/우(현재)가 나뉘고 **변경된 줄에 배경색**이 칠해지는지
- [ ] 비교 좌우 **세로 스크롤이 함께** 움직이는지
- [ ] 비교 **가로 스크롤**: 한쪽을 오른쪽 끝까지 밀었을 때 반대쪽이 자기 한계로 끌어당기지 않는지
      (넓은 쪽이 좁은 쪽 한계까지 끌려오면 결함이다)
- [ ] 탭을 **이력 → 비교** 로 오갔을 때 배경색이 다시 그려지는지
- [ ] 항목 선택 → **이력** 탭에 그 객체의 커밋(날짜·작성자·메시지·SHA)이 최신순으로 뜨는지
- [ ] 도구 창 **오른쪽 위에 버전**(`DBVC 0.3.0` 형태)이 뜨고, 방금 설치한 `.vsix`의 버전과 같은지
      (`알 수 없음` 이거나 `1.0.0` 이면 빌드 배선이 끊긴 것이다. 숫자가 이전 버전 그대로면
      설치 관리자가 같은 버전이라며 건너뛴 것이므로, 버전을 올려 다시 빌드하거나 먼저 제거한다)
- [ ] 도구 창을 **좁게 도킹**했을 때 상단 버튼들이 잘리지 않고 줄바꿈되는지
      (연결 버튼은 첫 줄 왼쪽에 그대로 남고 **대상 표시가 다음 줄로** 내려가는 것이 정상이다.
      이때 버전 표시는 오른쪽 **첫 줄**에 붙어 있어야 한다 — 두 줄 사이에 떠 있으면 결함이다)
- [ ] **Pull** 또는 **Push** 를 누른 직후 → 진행 표시가 뜨고 그동안 쿼리 편집기에 타이핑이 되는지
      (SSMS가 멈추면 결함이다). 이때 **취소** 버튼은 뜨지 않는 것이 정상이다
- [ ] SSMS **테마를 "어둡게"** 로 바꾼 뒤 → 연결 버튼 옆 대상 표시, 진행 문구, 안내 문구가
      배경에 묻히지 않고 읽히는지 (목록·비교 창은 밝은 배경을 유지하는 것이 정상이다)
- [ ] 기본값 제약과 인덱스를 가진 테이블을 만든 뒤 **새로고침** → 비교창의 스크립트에 `DEFAULT` 제약과 `CREATE ... INDEX` 가 함께 들어 있는지
- [ ] 객체를 `DROP` 한 뒤 **새로고침** → `삭제`로 뜨고, 체크해서 Commit하면 저장소에서도 파일이 사라지는지

### 0.3.0 — 스키마 v3 업그레이드

**기존 사용자 데이터가 걸린 항목이라 먼저 확인한다.**

- [ ] **v2로 초기화된 DB에 연결** → 창 위쪽에 **"변경 추적기가 구버전입니다"** 안내와
      **추적기 업데이트** 버튼이 뜨는지
- [ ] **업데이트를 누른 뒤** `SELECT COUNT(*) FROM dbo.DBVC_ChangeLog` → **기존 행이 그대로 남아
      있는지** (줄어들었다면 보정이 아니라 재생성이 일어난 것이므로 결함이다)
- [ ] 업데이트 뒤 프로시저를 하나 `ALTER` → `SELECT TOP 1 HostName, ClientNetAddress FROM
      dbo.DBVC_ChangeLog ORDER BY Id DESC` 에 **접속 PC 이름과 IP가 채워지는지**
      (`HostName`이 비면 작업자 필터가 통째로 무너진다)
- [ ] 업데이트 **이전에 쌓인 행**은 `HostName`이 `NULL`이라 **다른 사람 변경도 보기**를 켰을 때만
      목록에 뜨는지

### 0.3.0 — 브랜치 표시와 저장소 차단

- [ ] 연결 → 도구 창 오른쪽 위, **버전 왼쪽에 `브랜치: main`** 이 같은 줄에 뜨는지
- [ ] Git 클라이언트에서 브랜치를 바꾸고 다시 **연결** → 바뀐 이름으로 갱신되는지
- [ ] 매핑에 `Branch`가 없으면 **브랜치 표시가 아예 없는지** (`브랜치: ` 만 남으면 결함이다)
- [ ] `%APPDATA%\DBVC\mappings.json` 의 해당 항목에 `"Branch": "no-such-branch"` 를 손으로 넣고
      다시 연결 → **화면이 덮이고** 고정 브랜치와 현재 브랜치가 문구에 함께 나오는지
- [ ] 그 상태에서 **도구 줄까지 덮이는지** — 새로고침·Commit뿐 아니라 **Pull·Push·배포 스크립트도
      눌리지 않아야 한다** (내용 영역만 덮이면 어긋난 저장소를 상대로 원격 작업이 나간다)
- [ ] `git checkout <커밋 SHA>` 로 **detached HEAD** 를 만들고 연결 → 브랜치 불일치가 아니라
      **detached HEAD 문구**가 뜨는지
- [ ] 충돌하는 병합을 중단하지 않은 채 연결 → **끝나지 않은 작업 문구**가 뜨는지
      (브랜치도 함께 어긋나 있어도 병합 쪽이 먼저 뜬다 — 브랜치를 바꾸면 된다고 오해시키지 않기 위해서다)
- [ ] 정상 브랜치로 되돌리고 다시 연결 → **오버레이가 사라지고** 버튼이 다시 눌리는지
- [ ] SSMS **테마를 "어둡게"** 로 바꾼 뒤 차단 오버레이를 다시 띄워 → **문구가 읽히는지**
      (오버레이 배경만 고정색이라 테마에 따라 대비가 무너질 수 있다)

### 0.3.0 — 작업자 필터

공용 계정 환경을 흉내내려면 **다른 PC에서 SSMS로 접속**하거나, 접속 문자열의
`Workstation ID` 를 다르게 준 세션에서 DDL을 실행한다.

- [ ] 다른 PC에서 프로시저를 하나 만든 뒤 내 SSMS에서 **새로고침** → **내 목록에 뜨지 않는지**
- [ ] **다른 사람 변경도 보기** 를 켜면 → 그 객체가 뜨는지
- [ ] 목록의 **변경자** 컬럼에 그 PC 이름이 뜨는지 (로그인 이름이 뜨면 공용 계정에서 무의미하다)
- [ ] 새로고침이 도는 중에 **토글이 잠기는지** (겹쳐 돌면 서로의 결과를 덮어쓴다)
- [ ] 전체 보기에서 **남의 변경을 대신 커밋** → 다음 새로고침에 **그 항목이 사라지는지**
      (남으면 MarkProcessed가 그 행을 닫지 못한 것이다)
- [ ] 같은 객체를 나와 남이 각각 만진 상태에서 **내 것만 커밋** → 남의 행은 **닫히지 않고**
      전체 보기에 그대로 있는지

### 0.3.0 — 커밋 전 확인

- [ ] 같은 객체를 다른 PC에서도 만진 뒤 커밋 → **확인 대화상자가 뜨고 그 PC 이름이 문구에 있는지**
- [ ] **취소** → 커밋되지 않고 목록이 그대로인지
- [ ] 다시 커밋 → **확인** → 커밋되고 목록에서 사라지는지
- [ ] 남이 만지지 않은 객체만 커밋 → **확인이 뜨지 않는지** (매번 뜨면 사용자가 읽지 않게 된다)

### 0.3.0 — 추출 형식과 속도

- [ ] 새로 추출한 **프로시저·뷰·함수 `.sql` 이 `CREATE OR ALTER` 로 시작**하는지
- [ ] **테이블 `.sql` 은 `CREATE TABLE` 그대로**인지 (T-SQL에 `CREATE OR ALTER TABLE`이 없다)
- [ ] **객체가 많은 실제 개발 DB**에서 프로시저 하나만 고치고 **새로고침** → 체감으로 몇 초 안에
      끝나는지 (객체 수에 비례해 느려지면 열거 비용이 되살아난 것이다)
- [ ] **전체 다시 추출** 중에 쿼리 편집기와 개체 탐색기가 그대로 쓰이는지

> `mappings.json` 의 `Mode`(`Write`/`Deploy`/`Audit`)는 0.3.0에서 **저장만 하고 동작을 막지
> 않는다.** 지금 넣어 두는 이유는 나중에 파일을 마이그레이션하지 않기 위해서다 — 이 값으로
> 초기화 오버레이나 커밋을 막는지 확인할 것은 아직 없다.

### 인증

- [ ] **SQL 인증 서버에서 연결** → 대상 표시줄에 `서버.DB — SQL 인증 (계정)` 이 뜨고 접속되는지
- [ ] **SSMS를 재시작하고 개체 탐색기에 접속하지 않은 채 연결** → 선택 안내가 뜨고 접속을
      시도하지 않는지
- [ ] **개체 탐색기에서 서버 노드만 선택한 채 연결** → 같은 안내가 뜨는지
- [ ] **DBVC 창을 개체 탐색기와 나란히 띄운 채 다른 DB를 선택** → 패널에 마우스를 올리면
      "선택이 다릅니다" 안내가 뜨고, 연결을 누르면 그 대상으로 전환되는지
- [ ] `%APPDATA%\DBVC` 에 `credentials.json` 이 생기지 않는지

### 스크립트 생성

- [ ] 항목 몇 개 체크 → **배포 스크립트** → 저장 → **"N개 객체를 내보냈습니다."** 알림이 뜨는지
      (제외가 없어도 알림이 떠야 한다)
- [ ] 저장소에 `.sql` 파일이 없는 객체를 포함해 배포 스크립트를 만들면
      **"추출된 파일이 없어 제외했습니다"** 문구가 나오는지
- [ ] 커밋 이력이 하나뿐인 객체로 **롤백 스크립트** 를 만들면
      **"이전 리비전이 없어 제외했습니다"** 문구가 나오는지
- [ ] 생성된 `.sql` 을 텍스트 편집기로 열어 헤더에 `Excluded: N (...)` 줄이 있는지
- [ ] 스크립트를 만든 뒤 상단 **경고 배너가 오염되지 않았는지** (매핑 경고만 떠야 한다)

### Pull 성공 경로 (안내 문구 확인)

- [ ] **원격에 새 커밋이 있는 상태.** 다른 클론에서 커밋을 만들어 원격에 올린 뒤 Pull →
      안내에 **매핑된 저장소 폴더 경로**와 `[스키마]/[객체 유형]/[이름].sql` 위치 문구가
      함께 뜨는지. 그 폴더를 열어 안내가 가리킨 자리에 실제로 `.sql` 파일이 있는지 확인한다.
- [ ] **원격에 새 커밋이 없는 상태.** 곧바로 다시 Pull →
      `원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.` 가 뜨는지, 그리고 목록·이력
      화면이 다시 그려지지 않는지(값이 바뀐 것처럼 깜빡이면 결함이다).

### Pull 실패 경로 (안내 문구 확인)

각 상황을 일부러 만들어 **한국어 안내가 뜨는지** 본다. libgit2 영문 원문이 보이면 결함이다.

- [ ] **HTTPS 원격.** 임시로 `git remote set-url origin https://...` 로 바꾸고 Pull →
      `HTTPS 원격은 DBVC가 인증할 수 없습니다. SSH 원격으로 바꾸세요.` 와 변환 예시가 뜨는지.
      확인 후 SSH URL로 되돌린다.
- [ ] **추적 브랜치 없음.** `git checkout -b tmp` 로 새 브랜치를 만들고 Pull →
      `추적 중인 원격 브랜치가 없어 Pull할 수 없습니다` 와 `git push -u origin tmp` 안내가 뜨는지.
      확인 후 `git checkout main` 으로 되돌린다.
- [ ] **미커밋 변경이 있는 상태.** 새로고침만 하고 커밋하지 않은 채 Pull →
      확인 대화상자에 **"거부됩니다. 이 경우 저장소는 그대로입니다"** 와
      **"사라질 수 있습니다"** 가 **둘 다** 보이는지. 창이 SSMS 뒤로 숨지 않는지.
- [ ] **겹치는 미커밋 변경.** 원격이 바꾼 파일을 로컬에서 커밋하지 않고 수정한 뒤 Pull → 확인 →
      안내가 뜨고 **로컬 수정 내용이 그대로 남아 있는지**. (사라지면 심각한 결함이다)
- [ ] **SSH 포트 문구.** 안내 목록의 포트 항목이 `원격 호스트의 SSH 포트(기본 22)가 열려 있는지`
      로 표시되는지. 비표준 포트를 쓰는 GitLab에서 특히 확인한다.
- [ ] **OpenSSH가 없는 상태** (선택). 관리자 권한 PowerShell에서 클라이언트를 떼고 Pull →
      `OpenSSH 클라이언트를 설치한 뒤 다시 시도하세요` 안내가 뜨는지. 확인 후 다시 붙인다.
  ```powershell
  Remove-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0   # 끄기
  Add-WindowsCapability    -Online -Name OpenSSH.Client~~~~0.0.1.0   # 되돌리기
  ```
  > 재현이 안 되면 `PATH`에 다른 `ssh.exe`가 남아 있는 것이다 — Git for Windows도 `ssh.exe`를 함께
  > 깐다. `Get-Command ssh -All` 로 확인한다. `core.sshCommand` 가 설정돼 있어도 DBVC는 SSH가
  > 가능하다고 판단하므로 이 안내가 뜨지 않는다 (`git config --get core.sshCommand` 로 확인).
  >
  > 안내 문구가 가리키는 경로가 **설정 > 시스템 > 선택적 기능**(Windows 11 경로)인지도 함께 본다.

### Push 실패 경로 (안내 문구 확인)

Pull과 같은 이유로, 각 상황을 일부러 만들어 **한국어 안내가 뜨는지** 본다.

- [ ] **원격이 앞서 있는 상태.** 다른 클라이언트(또는 GitHub/GitLab 웹 편집)에서 같은 브랜치에
      커밋을 하나 올린다. 로컬에서 fetch/Pull하지 않은 채 스키마를 바꾸고 Commit한 뒤 Push →
      `원격이 '...' 갱신을 거부했습니다` 와 **"Pull을 먼저 하세요"** 문구가 뜨는지.
      확인 후 `git log`로 **로컬 커밋이 그대로 남아 있는지** (Push는 거부돼도 저장소를
      바꾸지 않는다). 이후 Pull로 받아 병합한 뒤 다시 Push해 정리한다.
- [ ] **올릴 것이 없는 상태.** 커밋 없이(또는 Push를 한 번 더 눌러) Push →
      오류 대화상자가 아니라 `올릴 커밋이 없습니다. 원격이 이미 최신입니다.` **정보 안내**가 뜨는지.
- [ ] **추적 브랜치 없음.** `git checkout -b tmp` 로 새 브랜치를 만들고 Push →
      `추적 중인 원격 브랜치가 없어 Push할 수 없습니다` 와 `git push -u origin tmp` 안내가 뜨는지.
      확인 후 `git checkout main` 으로 되돌린다.
- [ ] **SSH 원격으로의 실제 Push 성공.** 스키마를 하나 바꾸고 Commit한 뒤 Push →
      `커밋을 원격 저장소에 올렸습니다.` 알림이 뜨고, GitHub/GitLab에서 커밋이 실제로 보이는지.
      **이 확인이 이 섹션에서 가장 중요하다** — `OnPushStatusError` 배선을 지나는 유일한
      검증이다. 단위 테스트는 로컬/파일 전송 경로(`NonFastForwardException`)만 덮는다.

### 컨텍스트 메뉴

- [ ] SQL 에디터에서 객체 이름을 선택하고 우클릭 → **DBVC: 저장소 버전과 비교** 가 보이고
      동작하는지

---

## 조직이 정해야 할 운영 규칙

도구가 정할 수 없는 것들이다. **도입 전에 팀이 합의하고 여기에 답을 적어 둔다.** 정하지 않으면
사람마다 다르게 행동하고, 그 차이가 저장소에 조용히 쌓인다.

**DB 변경은 짧게 산다.** `feature/*`에서 DB를 고쳤으면 빨리 `develop`에 병합한다. 브랜치를 오래
들고 있을수록 공용 DB에서 같은 객체를 남이 만질 확률이 올라가고 커밋 전 확인이 매번 뜬다. 코드는
브랜치를 몇 주 들고 있어도 되지만 DB 변경은 그렇지 않다 — **DB는 이미 공유되어 있다.**

**같은 객체에 대한 동시 작업은 조율한다.** 공용 DB가 하나인 이상 프로시저 `P`를 둘이 만지면 나중
사람의 코드가 남는다. DBVC는 커밋 전에 알릴 뿐 막지 못한다.

**`hotfix/*`의 DB 변경을 어떻게 할지 정한다.** 개발 DB는 `master + 진행 중 feature` 상태라 거기서
뜬 스크립트에는 승격되지 않은 변경이 섞일 수 있다. 특히 `P` 한 파일 안에 develop 변경과 hotfix
변경이 **섞여 들어오면** 텍스트가 어느 쪽과도 달라 판정되지 않는다 — 파일 단위 비교의 한계다.
셋 중 하나를 택한다:

1. hotfix DB 변경은 **운영 백업을 복원한 별도 DB**에서 만든다 — 가장 정확하고 준비가 필요하다
2. 커밋 전 `master` 기준 diff를 **사람이 확인**한다
3. **hotfix에는 DB 변경을 넣지 않는다** — 운영 DB 변경은 정규 배포 경로로만

**`develop`을 리셋하거나 force-push하는가.** 그렇게 하는 팀이 있다. 그러면 비교의 기준이 흔들리고
테스트 DB 배포 이력이 끊긴다. 리셋한다면 그 주기와 절차를 정해 여기에 적는다.

**한 사람이 한 PC를 쓰는가.** 개발·테스트 DB가 공용 SQL 계정을 쓰므로 DBVC는 접속
PC(`HOST_NAME()`)로 사람을 가른다. 이 전제가 깨지는 경우가 둘이다.

- **여러 사람이 원격 데스크톱으로 같은 서버에서 SSMS를 쓴다** — `HostName`도 IP도 같아져 필터가
  무력해진다. 사람별 SQL 계정을 나누는 것이 유일한 해법이고, 그러면 `(LoginName, HostName)` 쌍이
  그대로 동작한다
- **한 사람이 여러 PC에서 작업한다** — 노트북에서 만든 변경을 데스크톱에서 커밋하려면 **다른 사람
  변경도 보기**를 켜야 한다. 무력해지지는 않지만 알고 있어야 한다

**공용 계정의 권한 범위를 확인한다.** 그 계정으로 접속한 모두가 서로의 객체를 고칠 수 있다.
DBVC는 누가 무엇을 만졌는지 기록하고 알릴 뿐, 막지 못한다.

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

- **Object Explorer 상태 아이콘 오버레이는 미구현.** SSMS에 공개 확장점이 없어 보류했다
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
| 창 위쪽에 "변경 추적기가 구버전입니다"가 뜬다 | 0.2.6 이전에 초기화한 데이터베이스다. **추적기 업데이트** 를 누른 뒤 **전체 다시 추출** 을 한 번 실행한다 |
| 새로고침해도 목록이 비어 있다 | DDL 트리거 설치 확인. 트리거 설치 **이후에** 변경한 객체만 잡힌다 |
| Pull이 영문 메시지를 낸다 | 안내가 붙지 않은 경우다. 원격 URL이 SSH도 HTTPS도 아닌 형태인지 확인 |
| Pull이 `known_hosts` 를 말한다 | Git 클라이언트에서 `ssh -T git@<호스트>` 를 한 번 실행해 `yes` 입력 |
| 커밋했는데 원격에 없다 | 커밋과 Push는 별개다. **Push** 버튼을 누른다 |
| Push가 거부된다 | 원격에 먼저 올라간 커밋이 있다. **Pull** 로 받아 병합한 뒤 다시 Push. 그래도 거부되면 브랜치 보호·권한을 확인 |
| `type %APPDATA%\...` 가 "경로를 찾을 수 없습니다"를 낸다 | PowerShell에서는 `%VAR%` 가 확장되지 않는다. `Get-Content $env:APPDATA\...` 를 쓴다 |
| `--add ... ^` 붙여넣기가 깨진다 | `^` 는 명령 프롬프트 전용 줄바꿈이다. PowerShell에서는 백틱(`` ` ``)을 쓰거나 한 줄로 붙여 쓴다 |
| `ssh-add` 가 "에이전트에 연결할 수 없습니다"를 낸다 | Windows 11에서 `ssh-agent` 서비스가 사용 안 함이다. 2단계의 `Set-Service ssh-agent -StartupType Automatic` + `Start-Service` (관리자 권한) |
| 설정 앱에서 "선택적 기능"을 못 찾는다 | Windows 11은 **설정 > 시스템** 아래다 (Windows 10은 앱 아래였다). `start ms-settings:optionalfeatures` 로 바로 연다 |
| `.vsix` 를 열면 "이 파일을 열 수 없습니다"가 뜬다 | 다른 기계에서 복사해 온 파일의 차단 표시다. `Unblock-File .\DBVC.Vsix.vsix` 후 다시 시도 |
