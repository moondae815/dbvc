# DBVC SSH 우선 Git 인증 및 원격 진단 설계

## 1. Overview

DBVC는 두 환경에서 동작해야 한다.

| 환경 | 원격 | 인증 |
| --- | --- | --- |
| 개발 노트북 (온라인) | github.com | 계정 |
| 운영 PC (폐쇄망) | 사내 GitLab 16.3 설치형 | LDAP(Windows AD 계정) |

현재 구현은 **두 환경 모두에서 동작하지 않는다.** `GitManager.ResolveCredentials`가 항상
`DefaultCredentials`(Windows 통합 인증)를 반환하는데, GitHub는 NTLM/Kerberos를 지원하지 않고,
GitLab의 LDAP 연동은 웹 UI 로그인과 계정 소스를 AD에 맡길 뿐 git over HTTPS는 HTTP Basic을 쓴다.
Kerberos를 붙이려면 `gitlab-kerberos`를 따로 구성해야 하며 LDAP만 켠 설치에는 없다.

이 문서는 인증을 **SSH에 위임**하고, 실패를 사용자가 행동할 수 있는 한국어로 옮기는 설계를 다룬다.

## 2. 측정된 전제

추측이 아니라 이 저장소에서 직접 측정한 사실이다.

* **libgit2는 SSH를 자체 구현하지 않는다.** `LibGit2Sharp.NativeBinaries 2.0.324`의 `win-x64`와
  `osx-arm64` 바이너리 모두 `libssh2` 문자열이 0개이고 `GIT_SSH`·`GIT_SSH_COMMAND`만 있다.
  즉 `ssh_exec` 전송을 쓰며 **시스템의 `ssh` 실행 파일에 위임**한다.
* 따라서 **SSH 인증은 `CredentialsProvider`를 거치지 않는다.** `SupportedCredentialTypes`에
  SSH 계열 멤버가 아예 없다는 점(`UsernamePassword = 1`, `Default = 2`)이 이를 뒷받침한다.
* 노출된 `Credentials` 구현체는 `DefaultCredentials`와 `SecureUsernamePasswordCredentials` 둘뿐이다.
  SSH 키 파일을 지정하는 타입은 없다.
* 자격 증명을 요구하는 실제 HTTPS 원격에서는 핸들러가 `types=UsernamePassword`로 호출되고,
  이어서 `Commands.Pull`이 `LibGit2SharpException("could not find appropriate mechanism for credentials")`를 던진다.
* **DBVC가 네트워크를 쓰는 지점은 Pull 하나뿐이다.** Push·Clone·Fetch API가 없다.
  (2026-08-18 갱신: Push가 추가되었다. [2026-08-18-dbvc-git-push-design.md](2026-08-18-dbvc-git-push-design.md) 참조.
  인증 경로는 이 문서가 정한 그대로이며, `RemoteDiagnostics`·`SshExecutableLocator`를 그대로 재사용한다.)

**환경 확인 결과:** 개발 노트북은 github.com으로 SSH가 닿는다. 폐쇄망 PC는 GitLab으로
`Connection timed out`이며, 방화벽 개방으로 해결 가능할 것으로 보이나 **확정되지 않았다.**

## 3. Scope

### In Scope

* 자격 증명 핸들러의 의미를 "HTTPS 원격 감지"로 바꾸고 안내 문구를 정정한다
* 원격 URL과 `ssh` 실행 파일 유무로 실패 원인을 판정하는 `RemoteDiagnostics`를 추가한다
* `PullChanges`가 그 판정을 예외 메시지에 담는다
* README에 HTTPS → SSH 전환 절차를 추가한다

### Out of Scope — 조건부 연기

아래는 **폐쇄망 방화벽이 열리지 않는 경우에만** 필요하다. 열리면 영영 필요 없으므로 지금 만들지 않는다.

> **2026-08-27 갱신: 조건이 해제됐다.** 폐쇄망 SSH(22) 승인이 나면서 이 세 항목은 연기가 아니라
> **소멸했다.** 형상 관리 2차의 범위에서 빠졌다
> ([2026-08-24-dbvc-git-workflow-design.md](2026-08-24-dbvc-git-workflow-design.md) §3.11, §7.2).

* **HTTPS + PAT 인증.** 토큰 입력 UI, DPAPI 기반 보관, 만료 시 재입력 흐름이 딸린 별도 서브시스템이다.
* **사설 CA 인증서 처리(`FetchOptions.CertificateCheck`).** 사내 GitLab이 자체 서명 인증서를 쓸 때 필요하다.
  SSH는 TLS를 타지 않으므로 SSH 경로에서는 발생하지 않는다.
* **프록시(`FetchOptions.ProxyOptions`).**

### Out of Scope — 그 밖

* **SSH 키 생성·등록을 DBVC가 대행하는 것.** 사용자가 Git 클라이언트와 공유하는 자산이며,
  DBVC가 만들거나 배포할 물건이 아니다. README로 안내만 한다.
* **`known_hosts` 자동 등록.** 호스트 키를 검증 없이 신뢰하는 것은 중간자 공격에 문을 여는 일이다.
  VSIX 안에서 사용자에게 지문을 확인시킬 방법도 없다.

## 4. Component Design

### 4.1. 자격 증명 핸들러의 의미 변경

코드 구조는 그대로 두고 **해석과 문구만** 바꾼다.

SSH에서 콜백이 호출되지 않는다는 사실을 뒤집으면, **콜백이 호출됐다는 것 자체가
"이 원격은 HTTPS이고 자격 증명을 요구한다"는 확정 신호**다. `requiresUserCredentials` 플래그의
의미가 "Windows 통합 인증으로 처리할 수 없음"에서 "HTTPS 원격이 자격 증명을 요구함"으로 바뀐다.

현재 메시지는 이렇다.

> DBVC는 Windows 통합 인증만 지원하므로, SSH 키를 사용하거나 원격 URL에 액세스 토큰을 포함해 다시 시도하세요.

LDAP 기반 GitLab에서 통합 인증은 애초에 성립하지 않으므로 이 안내는 **틀렸다.** 4.2의 판정 결과로 대체한다.

`ResolveCredentials`가 `DefaultCredentials`를 반환하는 동작은 유지한다. 비용이 없고,
훗날 GitLab에 `gitlab-kerberos`를 붙이면 그대로 통한다.

### 4.2. `RemoteDiagnostics` (신규, DBVC.Core)

실패를 사용자가 행동할 수 있는 한국어로 옮긴다. **판정은 결정적인 근거만 쓴다.**

```
internal enum RemoteUrlKind { Ssh, Https, Other, Unknown }

internal static class RemoteDiagnostics
{
    internal static RemoteUrlKind Classify(string? remoteUrl);

    // 안내할 것이 없으면 null. 호출자는 null이면 원문을 그대로 둔다.
    internal static string? Explain(string? remoteUrl, bool sshExecutableAvailable);
}
```

`Classify`가 인식하는 SSH 형태는 두 가지다 — `ssh://` 스킴, 그리고 scp 형식(`git@host:path`).
`https://`·`http://`는 `Https`, 로컬 경로와 `file://`은 `Other`, 비었거나 파싱 불가면 `Unknown`.

| 조건 | `Explain` 반환 |
| --- | --- |
| `Https` | HTTPS 원격은 인증을 지원하지 않는다는 안내 + SSH URL 변환 예시 |
| `Ssh` 이고 `sshExecutableAvailable == false` | OpenSSH 클라이언트가 없다는 안내 + Windows 기능 켜는 방법 |
| `Ssh` 이고 실행 파일은 있음 | SSH 한정 확인 목록: 공개키 등록, `known_hosts` 등록, 22번 포트 |
| `Other`·`Unknown` | `null` |

**세 번째 칸이 "모든 실패에 힌트 덧붙이기"와 다른 이유.** 이전에 제거한 그 패턴은 원격 미설정 같은
무관한 오류에도 미커밋 변경 힌트를 붙였다. 여기서는 **원격이 SSH임을 확인한 뒤에만** 나오고,
그 조건에서 원인 후보는 실제로 그 셋뿐이다. `Other`·`Unknown`에서 `null`을 반환하는 것이
이 절제를 강제한다.

**`Explain`이 예외를 인자로 받지 않는 이유.** 판정에 쓰이지 않는다. 예외 타입별 분기는
`PullChanges`의 기존 catch 구조가 이미 담당한다.

### 4.3. `SshExecutableLocator` (신규, DBVC.Core)

`ssh` 실행 파일 유무만 판정하는 얇은 컴포넌트다. `RemoteDiagnostics`를 순수 함수로 유지하기 위해 분리한다.

```
internal static class SshExecutableLocator
{
    internal static bool IsAvailable();
}
```

판정 순서: `GIT_SSH_COMMAND` → `GIT_SSH` → `PATH` 탐색.
앞의 두 환경 변수는 libgit2가 실제로 참조하는 것이므로(2절), 사용자가 PuTTY `plink` 등을
지정해 둔 경우를 놓치지 않는다. `PATH` 탐색 파일명은 Windows에서 `ssh.exe`, 그 외에서 `ssh`다.

### 4.4. `PullChanges`의 연결

**안내는 `try` 이전에 한 번 계산한다.** `Explain`은 예외에 의존하지 않고 원격 URL과 `ssh` 실행 파일
유무만 보므로, `Commands.Pull`을 부르기 전에 지역 변수에 담아 두면 된다. `when` 필터 안에서
다시 호출하는 중복 평가가 사라지고, 두 catch가 같은 문자열을 공유한다.

```
var remoteUrl = repo.Network.Remotes[repo.Head.RemoteName].Url;
var guidance = RemoteDiagnostics.Explain(remoteUrl, SshExecutableLocator.IsAvailable());

MergeResult result;
try { result = Commands.Pull(repo, signature, options); }
catch (CheckoutConflictException ex) { ... }                       // 기존, 변경 없음
catch (LibGit2SharpException ex) when (requiresUserCredentials)
    → GitAuthenticationException(guidance ?? CredentialFallbackMessage, ex)
catch (LibGit2SharpException ex) when (guidance != null)           // 신규
    → GitRemoteException($"{ex.Message}{개행}{개행}{guidance}", ex)
```

**`when (guidance != null)`로 "안내할 것이 있을 때만" 가로챈다.** 안내가 없으면 원본 예외가
그대로 전파되어 Vsix의 catch-all이 원문을 보여준다. 무관한 libgit2 오류를 엉뚱한 메시지로 삼키지 않는다.

`CredentialFallbackMessage`는 `Classify`가 `Other`·`Unknown`을 준 원격에서 자격 증명이 요구되는
경우에만 쓰이는 상수다. 정상 경로에서는 도달하지 않지만, `guidance`가 `null`일 때 메시지 없는
예외를 던지지 않도록 둔다. 내용은 "이 원격의 인증 방식을 DBVC가 처리할 수 없습니다. SSH 원격을 사용하세요."

원격 URL은 `repo.Network.Remotes[repo.Head.RemoteName].Url`로 얻는다. 추적 브랜치 가드가
이미 앞에 있으므로 `RemoteName`은 이 시점에 항상 존재한다.

#### 4.4.1. `GitRemoteException` (신규)

`MergeConflictException`과 같은 형태의 `Exception` 파생 타입.

**Vsix는 이 타입으로 분기하지 않는다. 의도적이다.** catch-all이 이미 제목 `DBVC Pull 실패`와
`ex.Message`를 보여주고, 이 예외의 메시지에 안내가 이미 담겨 있다. 분기를 더하면 출력이
catch-all과 글자 그대로 같아져 공허한 테스트를 부르는 중복 블록이 된다 —
이 저장소가 `GitAuthenticationException`에서 실제로 겪은 결함이다.
Core 계약에서 호출자가 원인을 구분할 수 있게 하는 값은 유지하되, 그 사실을 주석으로 남긴다.

### 4.5. README

두 기계 모두 현재 HTTPS를 쓰므로 SSH 전환이 선행 작업이다. 사용법 7번(Pull) 아래에 넣는다.

* 키 생성과 공개키를 GitHub·GitLab 계정에 등록
* 원격 URL을 SSH 형식으로 변경
* 첫 접속 전 `known_hosts` 등록 — VSIX 안에서는 호스트 신뢰 여부를 물을 수 없으므로
  Git 클라이언트에서 한 번 접속해 두어야 한다
* 폐쇄망은 GitLab으로 나가는 22번 포트가 열려 있어야 한다

## 5. Error Handling

| 상황 | 결과 |
| --- | --- |
| HTTPS 원격 | `GitAuthenticationException` + SSH 전환 안내 |
| SSH 원격, `ssh` 실행 파일 없음 | `GitRemoteException` + OpenSSH 설치 안내 |
| SSH 원격, 그 밖의 실패 | `GitRemoteException` + 원문 + SSH 확인 목록 |
| 로컬 경로 원격 등 | 기존 그대로. 안내를 덧붙이지 않는다 |
| 원격 미설정 / 추적 브랜치 없음 | 기존 `InvalidOperationException` 가드 그대로 |

## 6. Testing Strategy

**단위 테스트 (네트워크 없음)**

* `Classify` — `ssh://`, scp 형식, `https://`, `http://`, 로컬 경로, `null`, 빈 문자열
* `Explain` — 위 표의 네 칸 전부. 특히 `Other`·`Unknown`에서 `null`을 반환하는지
  (이것이 "무관한 실패에 힌트를 붙이지 않는다"는 계약이다)
* `SshExecutableLocator` — `GIT_SSH_COMMAND`·`GIT_SSH` 우선순위, `PATH` 탐색
* `PullChanges` — HTTPS 원격을 가진 저장소에서 안내가 예외 메시지에 담기는지.
  로컬 경로 원격에서는 안내가 붙지 않는지

**이 구조가 CI 문제를 함께 푼다.** 판정이 `(URL, bool) → string?` 순수 함수이므로 실제 의사결정
로직이 네트워크 없이 전량 검증된다. Windows net48에서 무한 대기해 `[Explicit]`으로 밀려난
`PullChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth`에 기대지 않는다.

**남는 공백(명시).** "빌드된 `PullOptions`가 실제로 `Commands.Pull`에 전달되는가"라는 배선은
여전히 그 `[Explicit]` 테스트만 지킨다. 이 설계는 그 공백을 좁히지 못한다.
SSH 경로에서는 `CredentialsProvider`가 호출되지 않으므로 배선의 실질 위험이 낮아진 것은 사실이나,
공백이 사라진 것은 아니다.

**수동 검증 (Windows)**

* 개발 노트북: SSH 원격으로 Pull 성공
* 폐쇄망 PC: 방화벽 개방 전에는 SSH 확인 목록 안내가 뜨는지, 개방 후 Pull이 성공하는지
* HTTPS 원격을 매핑했을 때 SSH 전환 안내가 뜨는지

## 7. 기존 코드에 미치는 영향

* `GitAuthenticationException`의 메시지 생성이 `RemoteDiagnostics`로 옮겨진다. 타입은 유지된다.
* `ViewChangesViewModel.Pull`은 **바뀌지 않는다.** 새 예외는 catch-all이 받아 `ex.Message`를 보여준다.
* `ResolveCredentials`와 `BuildPullOptions`의 동작은 바뀌지 않는다.
