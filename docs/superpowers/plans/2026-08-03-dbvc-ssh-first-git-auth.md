# DBVC SSH 우선 Git 인증 및 원격 진단 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Pull 인증을 SSH에 위임하고, 실패 원인을 원격 URL과 `ssh` 실행 파일 유무로 판정해 사용자가 행동할 수 있는 한국어 안내로 옮긴다.

**Architecture:** libgit2는 SSH를 자체 구현하지 않고 시스템 `ssh` 실행 파일에 위임한다(`ssh_exec` 전송). 따라서 SSH 인증은 `CredentialsProvider`를 거치지 않으며, DBVC는 비밀을 저장할 필요가 없다. 뒤집으면 콜백이 호출됐다는 사실 자체가 "이 원격은 HTTPS이고 인증할 수 없다"는 확정 신호다. 판정 로직은 `(URL, bool) → string?` 순수 함수로 분리해 네트워크 없이 전량 단위 테스트한다.

**Tech Stack:** .NET Framework 4.8 / .NET Standard 2.0, LibGit2Sharp 0.32 (NativeBinaries 2.0.324), NUnit 4, Moq

## Global Constraints

* `DBVC.Core`는 `net48;netstandard2.0` 멀티타깃이다. WPF·VS SDK에 의존하는 코드를 넣지 않는다.
* macOS/Linux에서는 `net10.0` 타깃만 실행된다. 모든 테스트 명령에 `-f net10.0`을 붙인다.
* 사용자에게 보이는 모든 문구는 한국어이며 기존 어투("…합니다")를 따른다.
* **네트워크에 의존하는 테스트를 새로 만들지 않는다.** 이 저장소는 이미 그 이유로 CI가 멈춰 선 적이 있다(`PullChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth`가 Windows net48에서 무한 대기해 `[Explicit]`으로 밀려났다). 새 판정 로직은 전부 순수 함수 단위 테스트로 검증한다.
* **안내는 결정적인 근거에서만 나온다.** `Classify`가 `Other`·`Unknown`을 주면 `Explain`은 반드시 `null`을 반환한다. 이것이 "무관한 실패에 힌트를 덧붙이지 않는다"는 계약이며, 이 저장소가 한 번 제거한 안티패턴이다.
* `Vsix`는 새 예외 타입으로 분기하지 않는다. catch-all이 `ex.Message`를 그대로 보여주며, 분기를 더하면 출력이 catch-all과 동일해져 공허한 테스트를 부른다.
* 커밋 메시지는 한국어 제목 + Conventional Commits 접두사(`feat:`, `fix:`, `test:`, `docs:`).

---

## File Structure

**생성**

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/RemoteDiagnostics.cs` | 원격 URL 분류와 실패 안내 생성 (순수) |
| `src/DBVC.Core/SshExecutableLocator.cs` | `ssh` 실행 파일 유무 판정 |
| `src/DBVC.Core/GitRemoteException.cs` | 안내를 담아 던지는 도메인 예외 |
| `tests/DBVC.Core.Tests/RemoteDiagnosticsTests.cs` | Task 1·2 |
| `tests/DBVC.Core.Tests/SshExecutableLocatorTests.cs` | Task 3 |

**수정**

| 파일 | 변경 |
| --- | --- |
| `src/DBVC.Core/GitManager.cs` | `PullChanges` 연결, `ResolveCredentials` XML 주석 정정 |
| `src/DBVC.Core/GitAuthenticationException.cs` | XML 주석 정정 |
| `tests/DBVC.Core.Tests/GitManagerTests.cs` | Task 4 |
| `README.md` | Task 5 |

## 확정 문구

여러 태스크가 같은 문자열을 참조한다. **여기 적힌 그대로** 쓴다.

`HttpsGuidance`:
```
HTTPS 원격은 DBVC가 인증할 수 없습니다. SSH 원격으로 바꾸세요.
예: https://github.com/org/repo.git -> git@github.com:org/repo.git
Git 클라이언트에서 'git remote set-url origin <SSH URL>'을 실행하면 됩니다.
```

`SshMissingGuidance`:
```
SSH 원격이지만 ssh 실행 파일을 찾을 수 없습니다.
Windows 설정 > 시스템 > 선택적 기능에서 'OpenSSH 클라이언트'를 설치한 뒤 다시 시도하세요.
```

`SshFailureGuidance`:
```
SSH 연결에 실패했습니다. 다음을 확인하세요.
- 공개키가 원격 계정에 등록되어 있는지
- 해당 호스트가 known_hosts에 등록되어 있는지 (Git 클라이언트에서 한 번 접속해 두세요)
- 원격 호스트로 나가는 22번 포트가 열려 있는지
```

`CredentialFallbackMessage` (GitManager 안의 상수):
```
이 원격의 인증 방식을 DBVC가 처리할 수 없습니다. SSH 원격을 사용하세요.
```

여러 줄 문구는 `Environment.NewLine`으로 잇는다.

---

## Task 1: `RemoteDiagnostics.Classify`

**Files:**
- Create: `src/DBVC.Core/RemoteDiagnostics.cs`
- Test: `tests/DBVC.Core.Tests/RemoteDiagnosticsTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `internal enum RemoteUrlKind { Ssh, Https, Other, Unknown }`
  - `internal static RemoteUrlKind RemoteDiagnostics.Classify(string? remoteUrl)`

**배경.** `Classify`는 안내의 유일한 근거다. scp 형식(`git@host:path`)과 Windows 로컬 경로(`C:\repos\x`)를
구분하는 것이 이 태스크의 핵심 난점이다. 둘 다 콜론을 포함한다.

`[assembly: InternalsVisibleTo("DBVC.Core.Tests")]`가 `src/DBVC.Core/StateTracker.cs:11`에 이미 있으므로
`internal`로 두어도 테스트에서 직접 호출할 수 있다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/RemoteDiagnosticsTests.cs`를 새로 만든다.

```csharp
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class RemoteDiagnosticsTests
    {
        // ---------- Classify ----------

        [TestCase("ssh://git@github.com/org/repo.git")]
        [TestCase("SSH://git@github.com/org/repo.git")]
        [TestCase("git+ssh://git@gitlab.corp.local/team/repo.git")]
        public void Classify_RecognizesSshScheme(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Ssh));
        }

        [TestCase("git@github.com:org/repo.git")]
        [TestCase("git@gitlab.corp.local:team/repo.git")]
        [TestCase("gitlab.corp.local:team/repo.git")]
        public void Classify_RecognizesScpForm(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Ssh),
                "scp 형식은 SSH입니다. 사내 GitLab에서 흔히 쓰는 형태입니다");
        }

        [TestCase("https://github.com/org/repo.git")]
        [TestCase("HTTPS://github.com/org/repo.git")]
        [TestCase("http://gitlab.corp.local/team/repo.git")]
        public void Classify_RecognizesHttpSchemes(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Https));
        }

        [TestCase(@"C:\repos\dbvc")]
        [TestCase(@"c:\repos\dbvc")]
        public void Classify_DoesNotMistakeAWindowsDriveLetterForScpForm(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Other),
                "드라이브 문자 뒤의 콜론을 scp 구분자로 읽으면 로컬 경로 원격에 SSH 안내가 붙습니다");
        }

        [TestCase("/home/user/repos/dbvc")]
        [TestCase(@"\\fileserver\share\repo")]
        [TestCase("file:///home/user/repo")]
        public void Classify_TreatsLocalAndUncPathsAsOther(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Other));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Classify_ReturnsUnknown_ForMissingUrl(string? url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Unknown));
        }

        [Test]
        public void Classify_ReturnsUnknown_ForAnUnrecognizedForm()
        {
            Assert.That(RemoteDiagnostics.Classify("git://github.com/org/repo.git"),
                Is.EqualTo(RemoteUrlKind.Unknown),
                "인식하지 못하는 형태는 Unknown이어야 안내가 붙지 않습니다");
        }
    }
}
```

- [x] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "RemoteDiagnosticsTests"`

Expected: 컴파일 실패. `RemoteDiagnostics`와 `RemoteUrlKind`가 없다.

- [x] **Step 3: `Classify`를 구현한다**

`src/DBVC.Core/RemoteDiagnostics.cs`를 새로 만든다.

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>원격 URL의 종류. 안내를 붙일지 말지의 유일한 근거다.</summary>
    internal enum RemoteUrlKind
    {
        /// <summary>ssh:// 스킴 또는 scp 형식(git@host:path).</summary>
        Ssh,
        Https,
        /// <summary>로컬 경로, UNC, file:// 등 인증이 필요 없는 원격.</summary>
        Other,
        /// <summary>비었거나 인식하지 못하는 형태.</summary>
        Unknown
    }

    /// <summary>
    /// Pull 실패를 사용자가 행동할 수 있는 한국어 안내로 옮긴다.
    /// 순수 함수만 두어 네트워크 없이 전량 단위 테스트한다.
    /// </summary>
    internal static class RemoteDiagnostics
    {
        internal static RemoteUrlKind Classify(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return RemoteUrlKind.Unknown;

            var url = remoteUrl!.Trim();

            if (StartsWith(url, "ssh://") || StartsWith(url, "git+ssh://")) return RemoteUrlKind.Ssh;
            if (StartsWith(url, "https://") || StartsWith(url, "http://")) return RemoteUrlKind.Https;
            if (StartsWith(url, "file://")) return RemoteUrlKind.Other;

            // UNC와 유닉스 절대 경로.
            if (url.StartsWith(@"\\", StringComparison.Ordinal) || url[0] == '/') return RemoteUrlKind.Other;

            var colon = url.IndexOf(':');
            if (colon <= 0) return RemoteUrlKind.Unknown;

            var host = url.Substring(0, colon);

            // 'C:\repos\x' 같은 드라이브 문자. scp 형식의 호스트는 한 글자일 수 없다.
            if (host.Length == 1) return RemoteUrlKind.Other;

            // scp 형식의 호스트 부분에는 경로 구분자가 없다.
            if (host.IndexOf('/') >= 0 || host.IndexOf('\\') >= 0) return RemoteUrlKind.Unknown;

            return RemoteUrlKind.Ssh;
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

- [x] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`

Expected: 전부 PASS.

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/RemoteDiagnostics.cs tests/DBVC.Core.Tests/RemoteDiagnosticsTests.cs
git commit -m "feat(core): 원격 URL 종류를 판정하는 RemoteDiagnostics.Classify 추가"
```

---

## Task 2: `RemoteDiagnostics.Explain`

**Files:**
- Modify: `src/DBVC.Core/RemoteDiagnostics.cs`
- Test: `tests/DBVC.Core.Tests/RemoteDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `RemoteDiagnostics.Classify(string?)`, `RemoteUrlKind` (Task 1)
- Produces: `internal static string? RemoteDiagnostics.Explain(string? remoteUrl, bool sshExecutableAvailable)`

**배경.** 이 함수가 "안내는 결정적인 근거에서만"이라는 계약을 강제한다. `Other`·`Unknown`에서
`null`을 돌려주는 것이 그 계약이며, 호출자는 `null`이면 원문을 그대로 둔다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

`RemoteDiagnosticsTests.cs`의 `// ---------- Classify ----------` 구역 아래에 추가한다.

```csharp
        // ---------- Explain ----------

        [Test]
        public void Explain_TellsHttpsUsersToSwitchToSsh()
        {
            var guidance = RemoteDiagnostics.Explain("https://github.com/org/repo.git", sshExecutableAvailable: true);

            Assert.That(guidance, Is.Not.Null);
            Assert.That(guidance, Does.Contain("SSH 원격으로 바꾸세요"));
            Assert.That(guidance, Does.Contain("git remote set-url"),
                "사용자가 그대로 실행할 수 있는 명령을 줘야 합니다");
        }

        [Test]
        public void Explain_TellsTheUserToInstallOpenSsh_WhenTheSshExecutableIsMissing()
        {
            var guidance = RemoteDiagnostics.Explain("git@github.com:org/repo.git", sshExecutableAvailable: false);

            Assert.That(guidance, Does.Contain("OpenSSH 클라이언트"));
            Assert.That(guidance, Does.Not.Contain("known_hosts"),
                "실행 파일이 없는 단계에서 호스트 키를 확인하라는 안내는 순서가 틀립니다");
        }

        [Test]
        public void Explain_ListsTheThreeSshCauses_WhenTheExecutableIsPresent()
        {
            var guidance = RemoteDiagnostics.Explain("ssh://git@gitlab.corp.local/team/repo.git", sshExecutableAvailable: true);

            Assert.That(guidance, Does.Contain("공개키"));
            Assert.That(guidance, Does.Contain("known_hosts"));
            Assert.That(guidance, Does.Contain("22번 포트"));
        }

        [TestCase(@"C:\repos\dbvc")]
        [TestCase("/home/user/repos/dbvc")]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("git://github.com/org/repo.git")]
        public void Explain_ReturnsNull_WhenThereIsNoDeterministicCause(string? url)
        {
            Assert.That(RemoteDiagnostics.Explain(url, sshExecutableAvailable: true), Is.Null,
                "이것이 '무관한 실패에 힌트를 덧붙이지 않는다'는 계약입니다. 이 테스트가 깨지면 계약이 깨진 것입니다");
            Assert.That(RemoteDiagnostics.Explain(url, sshExecutableAvailable: false), Is.Null);
        }
```

- [x] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "Explain"`

Expected: 컴파일 실패. `Explain`이 없다.

- [x] **Step 3: `Explain`을 구현한다**

`RemoteDiagnostics` 클래스 안, `Classify` 위에 문구 상수와 함께 넣는다.

```csharp
        private static readonly string HttpsGuidance = string.Join(Environment.NewLine, new[]
        {
            "HTTPS 원격은 DBVC가 인증할 수 없습니다. SSH 원격으로 바꾸세요.",
            "예: https://github.com/org/repo.git -> git@github.com:org/repo.git",
            "Git 클라이언트에서 'git remote set-url origin <SSH URL>'을 실행하면 됩니다."
        });

        private static readonly string SshMissingGuidance = string.Join(Environment.NewLine, new[]
        {
            "SSH 원격이지만 ssh 실행 파일을 찾을 수 없습니다.",
            // 경로는 Windows 11 기준이다 (Windows 10에서는 '앱 > 선택적 기능'이었다).
            "Windows 설정 > 시스템 > 선택적 기능에서 'OpenSSH 클라이언트'를 설치한 뒤 다시 시도하세요."
        });

        private static readonly string SshFailureGuidance = string.Join(Environment.NewLine, new[]
        {
            "SSH 연결에 실패했습니다. 다음을 확인하세요.",
            "- 공개키가 원격 계정에 등록되어 있는지",
            "- 해당 호스트가 known_hosts에 등록되어 있는지 (Git 클라이언트에서 한 번 접속해 두세요)",
            "- 원격 호스트로 나가는 22번 포트가 열려 있는지"
        });

        /// <summary>
        /// 안내할 것이 있으면 한국어 문구를, 없으면 <c>null</c>을 반환한다.
        /// <c>null</c>일 때 호출자는 원본 오류 메시지를 그대로 둔다 - 근거 없는 추측을 덧붙이지 않는다.
        /// </summary>
        internal static string? Explain(string? remoteUrl, bool sshExecutableAvailable)
        {
            switch (Classify(remoteUrl))
            {
                case RemoteUrlKind.Https:
                    return HttpsGuidance;
                case RemoteUrlKind.Ssh:
                    return sshExecutableAvailable ? SshFailureGuidance : SshMissingGuidance;
                default:
                    return null;
            }
        }
```

- [x] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`

Expected: 전부 PASS.

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/RemoteDiagnostics.cs tests/DBVC.Core.Tests/RemoteDiagnosticsTests.cs
git commit -m "feat(core): 원격 종류별 한국어 실패 안내를 만드는 Explain 추가"
```

---

## Task 3: `SshExecutableLocator`

**Files:**
- Create: `src/DBVC.Core/SshExecutableLocator.cs`
- Test: `tests/DBVC.Core.Tests/SshExecutableLocatorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `internal static bool SshExecutableLocator.IsAvailable()` — 실제 환경을 읽는 얇은 진입점
  - `internal static bool SshExecutableLocator.IsAvailable(string? gitSshCommand, string? gitSsh, string? pathVariable, Func<string, bool> fileExists)` — 순수 판정

**배경.** 2절에서 측정한 대로 libgit2는 `GIT_SSH_COMMAND`와 `GIT_SSH`를 참조한다. 사용자가 PuTTY `plink` 등을
지정해 둔 경우를 놓치지 않으려면 그 둘을 `PATH` 탐색보다 먼저 본다. 환경 변수 내용은 검증하지 않는다 —
사용자가 설정했다면 그 판단을 존중한다.

인자를 받는 오버로드가 실제 판정이고 무인자 오버로드는 환경을 읽어 넘기기만 한다. 프로세스 환경 변수를
바꾸지 않고 테스트하기 위한 분리다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SshExecutableLocatorTests.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SshExecutableLocatorTests
    {
        private static Func<string, bool> NothingExists => _ => false;

        private static Func<string, bool> Exists(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        [Test]
        public void IsAvailable_TrustsGitSshCommand_WithoutSearchingPath()
        {
            Assert.That(
                SshExecutableLocator.IsAvailable("ssh -o StrictHostKeyChecking=yes", null, null, NothingExists),
                Is.True,
                "사용자가 GIT_SSH_COMMAND를 설정했다면 libgit2가 그것을 씁니다. 내용을 검증하지 않습니다");
        }

        [Test]
        public void IsAvailable_TrustsGitSsh_WithoutSearchingPath()
        {
            Assert.That(
                SshExecutableLocator.IsAvailable(null, @"C:\Program Files\PuTTY\plink.exe", null, NothingExists),
                Is.True,
                "PuTTY plink를 GIT_SSH로 지정한 환경을 놓치면 안 됩니다");
        }

        [TestCase("")]
        [TestCase("   ")]
        public void IsAvailable_IgnoresBlankEnvironmentVariables(string blank)
        {
            Assert.That(SshExecutableLocator.IsAvailable(blank, blank, null, NothingExists), Is.False);
        }

        [Test]
        public void IsAvailable_FindsTheExecutableOnPath()
        {
            var dir = Path.Combine("usr", "bin");
            var pathVariable = string.Join(Path.PathSeparator.ToString(), new[] { Path.Combine("nope"), dir });

            var found = SshExecutableLocator.IsAvailable(
                null, null, pathVariable,
                Exists(Path.Combine(dir, "ssh"), Path.Combine(dir, "ssh.exe")));

            Assert.That(found, Is.True);
        }

        [Test]
        public void IsAvailable_ReturnsFalse_WhenPathHasNoSshExecutable()
        {
            var pathVariable = string.Join(Path.PathSeparator.ToString(), new[] { "a", "b" });

            Assert.That(SshExecutableLocator.IsAvailable(null, null, pathVariable, NothingExists), Is.False);
        }

        [Test]
        public void IsAvailable_ReturnsFalse_WhenNothingIsConfigured()
        {
            Assert.That(SshExecutableLocator.IsAvailable(null, null, null, NothingExists), Is.False);
        }

        [Test]
        public void IsAvailable_ToleratesEmptyPathEntries()
        {
            var pathVariable = Path.PathSeparator + "" + Path.PathSeparator;

            Assert.DoesNotThrow(() => SshExecutableLocator.IsAvailable(null, null, pathVariable, NothingExists));
        }
    }
}
```

- [x] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "SshExecutableLocatorTests"`

Expected: 컴파일 실패. `SshExecutableLocator`가 없다.

- [x] **Step 3: 구현한다**

`src/DBVC.Core/SshExecutableLocator.cs`를 새로 만든다.

```csharp
using System;
using System.IO;

namespace DBVC.Core
{
    /// <summary>
    /// libgit2는 SSH를 자체 구현하지 않고 시스템 <c>ssh</c> 실행 파일에 위임한다(ssh_exec 전송).
    /// 실행 파일이 없으면 SSH 원격 Pull은 원인을 알 수 없는 오류로 실패하므로, 그 경우를 먼저 가려낸다.
    /// </summary>
    internal static class SshExecutableLocator
    {
        internal static bool IsAvailable()
        {
            return IsAvailable(
                Environment.GetEnvironmentVariable("GIT_SSH_COMMAND"),
                Environment.GetEnvironmentVariable("GIT_SSH"),
                Environment.GetEnvironmentVariable("PATH"),
                File.Exists);
        }

        /// <summary>
        /// 실제 판정. <paramref name="gitSshCommand"/>와 <paramref name="gitSsh"/>는 libgit2가 참조하는
        /// 환경 변수이므로 PATH 탐색보다 먼저 본다. 값의 내용은 검증하지 않는다 - 사용자가 설정했다면
        /// 그 판단을 따른다.
        /// </summary>
        internal static bool IsAvailable(
            string? gitSshCommand,
            string? gitSsh,
            string? pathVariable,
            Func<string, bool> fileExists)
        {
            if (!string.IsNullOrWhiteSpace(gitSshCommand)) return true;
            if (!string.IsNullOrWhiteSpace(gitSsh)) return true;
            if (string.IsNullOrWhiteSpace(pathVariable)) return false;

            foreach (var directory in pathVariable!.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                // Windows에서는 ssh.exe, 그 외에서는 ssh다. 둘 다 확인하면 플랫폼 분기가 필요 없다.
                if (fileExists(Path.Combine(directory, "ssh.exe"))) return true;
                if (fileExists(Path.Combine(directory, "ssh"))) return true;
            }

            return false;
        }
    }
}
```

- [x] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`

Expected: 전부 PASS.

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/SshExecutableLocator.cs tests/DBVC.Core.Tests/SshExecutableLocatorTests.cs
git commit -m "feat(core): ssh 실행 파일 유무를 판정하는 SshExecutableLocator 추가"
```

---

## Task 4: `GitRemoteException`과 `PullChanges` 연결

**Files:**
- Create: `src/DBVC.Core/GitRemoteException.cs`
- Modify: `src/DBVC.Core/GitManager.cs` (`PullChanges`, `ResolveCredentials`의 XML 주석)
- Modify: `src/DBVC.Core/GitAuthenticationException.cs` (XML 주석)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `RemoteDiagnostics.Explain(string?, bool)` (Task 2), `SshExecutableLocator.IsAvailable()` (Task 3)
- Produces: `public class GitRemoteException : Exception` — 생성자 `(string message)`, `(string message, Exception innerException)`

**배경.** `Explain`은 예외에 의존하지 않고 원격 URL과 `ssh` 실행 파일 유무만 보므로 `try` **이전에**
한 번 계산해 지역 변수에 담는다. 두 catch가 같은 문자열을 공유하고 중복 평가가 사라진다.

**`Vsix`는 `GitRemoteException`으로 분기하지 않는다. 의도적이다.** catch-all이 이미 제목
`DBVC Pull 실패`와 `ex.Message`를 보여주고, 이 예외의 메시지에 안내가 담겨 있다. 분기를 더하면
출력이 catch-all과 글자 그대로 같아져 공허한 테스트를 부른다 — 이 저장소가
`GitAuthenticationException`에서 실제로 겪은 결함이다. 이 태스크는 `DBVC.Vsix`를 건드리지 않는다.

- [x] **Step 1: 예외 타입을 만든다**

`src/DBVC.Core/GitRemoteException.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격과 통신하지 못해 Pull에 실패했고, 원인을 특정할 안내가 있는 경우.
    /// 메시지에 원본 오류와 한국어 안내가 함께 담긴다.
    /// <para>
    /// Vsix는 이 타입으로 분기하지 않는다. 의도적이다 - catch-all이 이미 제목 'DBVC Pull 실패'와
    /// <c>ex.Message</c>를 보여주므로, 전용 catch를 더하면 출력이 catch-all과 동일해져
    /// 아무것도 고정하지 못하는 테스트를 부른다.
    /// </para>
    /// </summary>
    public class GitRemoteException : Exception
    {
        public GitRemoteException(string message) : base(message)
        {
        }

        public GitRemoteException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
```

- [x] **Step 2: 실패하는 테스트를 쓴다**

> **주의 — 이 저장소는 네트워크 테스트로 CI가 멈춰 선 적이 있다.**
> `PullChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth`가
> Windows net48에서 무한 대기해 러너가 1시간 넘게 그 단계에 머물렀고, 결국 `[Explicit]`으로 밀려났다.
> 그 테스트는 `HttpListener`로 **서버를 띄워** HTTP 인증 왕복을 했고, Windows에서는 HTTP.sys를 거쳤다.
>
> 아래 첫 번째 테스트는 다르다. **서버를 띄우지 않는다.** 아무것도 듣고 있지 않은 루프백 포트로
> 연결을 시도하므로 모든 플랫폼에서 즉시 connection refused가 되어 돌아온다.
> 그래도 실행 시간을 눈으로 확인한다 — 1초를 넘기면 멈추고 보고한다.
> 두 번째 테스트는 로컬 경로 원격만 쓰므로 네트워크에 나가지 않는다.

`tests/DBVC.Core.Tests/GitManagerTests.cs`의 `PullChanges_ExplainsInKorean_WhenTheRepositoryHasNoCommitsYet` 다음에 추가한다.

```csharp
        [Test]
        public void PullChanges_TellsTheUserToSwitchToSsh_WhenTheRemoteIsHttps()
        {
            // 도달 불가능한 HTTPS 원격. 네트워크에 나가지 않고도 자격 증명 요구 이전 단계에서 실패한다.
            var localPath = NewRepoWithCommit();
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", "https://127.0.0.1:1/nope.git");
                var branchName = local.Head.FriendlyName;
                local.Config.Set($"branch.{branchName}.remote", "origin");
                local.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitRemoteException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("SSH 원격으로 바꾸세요"));
            Assert.That(ex.InnerException, Is.Not.Null, "원인을 보존해야 진단할 수 있습니다");
        }

        [Test]
        public void PullChanges_AddsNoGuidance_WhenTheRemoteIsALocalPath()
        {
            // 로컬 경로 원격이 사라진 상황. 안내를 붙일 결정적 근거가 없으므로 원문이 그대로 나와야 한다.
            var originPath = NewRepoWithCommit();
            var localPath = NewTempDir();
            Repository.Clone(originPath, localPath);
            TryDeleteDirectory(originPath);

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<LibGit2SharpException>(() => git.PullChanges("localhost", "testdb"),
                "안내가 없으면 원본 예외가 그대로 전파되어야 합니다 - 무관한 오류를 엉뚱한 메시지로 삼키면 안 됩니다");

            Assert.That(ex!.Message, Does.Not.Contain("SSH"));
            Assert.That(ex.Message, Does.Not.Contain("공개키"));
        }
```

- [x] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "PullChanges_TellsTheUserToSwitchToSsh|PullChanges_AddsNoGuidance"`

Expected: 첫 번째는 `GitRemoteException` 대신 `LibGit2SharpException`이 나와 FAIL.
두 번째는 현재도 통과할 수 있다 — 그렇다면 이 태스크가 그 계약을 **깨뜨리지 않는지** 지키는 회귀 테스트다.

출력에 찍힌 각 테스트의 소요 시간을 확인한다. 첫 번째가 1초를 넘으면 구현을 진행하지 말고
소요 시간과 함께 보고한다 — 로컬에서 느리면 Windows CI에서는 멈춰 설 수 있다.

- [x] **Step 4: `PullChanges`를 고친다**

`var headBefore = repo.Head.Tip;` 줄 바로 앞에 안내 계산을 넣는다.

```csharp
            // Explain은 예외가 아니라 원격 URL과 ssh 실행 파일 유무만 보므로 try 이전에 한 번 계산한다.
            // 추적 브랜치 가드를 이미 통과했으므로 RemoteName은 여기서 항상 존재한다.
            var remoteUrl = repo.Network.Remotes[repo.Head.RemoteName].Url;
            var guidance = RemoteDiagnostics.Explain(remoteUrl, SshExecutableLocator.IsAvailable());

            var headBefore = repo.Head.Tip;
```

기존 `GitAuthenticationException` catch를 아래로 교체하고, 그 뒤에 새 catch를 더한다.

```csharp
            catch (LibGit2SharpException ex) when (requiresUserCredentials)
            {
                // 콜백이 호출됐다는 것 자체가 "이 원격은 HTTPS이고 자격 증명을 요구한다"는 신호다.
                // SSH는 시스템 ssh 실행 파일이 처리하므로 이 콜백을 거치지 않는다.
                throw new GitAuthenticationException(
                    $"'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다." +
                    Environment.NewLine + Environment.NewLine +
                    (guidance ?? CredentialFallbackMessage), ex);
            }
            // 안내할 것이 있을 때만 가로챈다. 없으면 원본 예외가 그대로 전파되어
            // 무관한 libgit2 오류를 엉뚱한 메시지로 삼키지 않는다.
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }
```

`GitManager` 클래스 상단의 상수 옆에 폴백 문구를 더한다.

```csharp
        /// <summary>
        /// <see cref="RemoteDiagnostics.Explain"/>이 판정하지 못한 원격에서 자격 증명이 요구된 경우.
        /// 정상 경로에서는 도달하지 않지만, 메시지 없는 예외를 던지지 않도록 둔다.
        /// </summary>
        private const string CredentialFallbackMessage =
            "이 원격의 인증 방식을 DBVC가 처리할 수 없습니다. SSH 원격을 사용하세요.";
```

- [x] **Step 5: 낡은 주석 두 곳을 정정한다**

`ResolveCredentials`의 XML 주석에서 "DBVC는 Windows 통합 인증(NTLM/Kerberos)만 지원하므로" 문장을
아래로 교체한다.

```csharp
        /// libgit2는 SSH를 시스템 ssh 실행 파일에 위임하므로 SSH 원격은 이 콜백을 거치지 않는다.
        /// 뒤집으면 이 콜백이 호출됐다는 것은 원격이 HTTPS이고 자격 증명을 요구한다는 뜻이다.
        /// DefaultCredentials를 계속 반환하는 이유는 비용이 없고, 원격에 Kerberos가 붙어 있으면
        /// 그대로 통하기 때문이다.
```

`src/DBVC.Core/GitAuthenticationException.cs`의 클래스 주석을 교체한다.

```csharp
    /// <summary>
    /// HTTPS 원격이 사용자 자격 증명을 요구했으나 DBVC가 제공할 수 없다.
    /// DBVC는 인증을 SSH에 위임하며 비밀을 보관하지 않는다.
    /// </summary>
```

- [x] **Step 6: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net10.0`

Expected: 전부 PASS. 기존 `PullChanges_FastForwards_WhenRemoteHasNewCommits`와
`PullChanges_ThrowsMergeConflictException_AndRestoresHead_OnConflict`는 로컬 경로 원격을 쓰므로
`guidance`가 `null`이 되어 새 catch에 걸리지 않는다.

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Core/GitRemoteException.cs src/DBVC.Core/GitManager.cs src/DBVC.Core/GitAuthenticationException.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): Pull 실패에 원격 종류별 한국어 안내를 담는다"
```

---

## Task 5: README에 SSH 전환 안내

**Files:**
- Modify: `README.md` (사용법 7번 Pull 항목)

**Interfaces:**
- Consumes: Task 2의 안내 문구가 가리키는 절차
- Produces: 없음

**배경.** 두 대상 기계 모두 현재 HTTPS를 쓰므로 DBVC를 쓰려면 SSH 전환이 선행 작업이다.
`known_hosts`는 특히 중요하다 — VSIX 안에서는 호스트 신뢰 여부를 묻는 프롬프트에 답할 방법이 없어
등록되지 않은 호스트는 그냥 실패한다.

이 태스크에는 테스트가 없다. 문서만 바뀐다.

- [x] **Step 1: README를 고친다**

`README.md`의 7번 항목(`**원격 변경 가져오기:**`) 마지막 줄 다음에 아래를 들여쓰기 3칸으로 추가한다.

```markdown
   **인증은 SSH만 지원합니다.** DBVC는 자격 증명을 묻지도 저장하지도 않고, Git이 쓰는 시스템 `ssh`에 그대로 위임합니다. 처음 쓰기 전에 다음을 준비하세요.
   1. Windows에 OpenSSH 클라이언트가 설치되어 있어야 합니다 (Windows 11은 기본 포함. 없다면 설정 > 시스템 > 선택적 기능).
   2. `ssh-keygen`으로 키를 만들고 공개키를 GitHub·GitLab 계정에 등록합니다.
   3. 원격 URL을 SSH 형식으로 바꿉니다: `git remote set-url origin git@github.com:org/repo.git`
   4. **Git 클라이언트에서 한 번 접속해 호스트 키를 `known_hosts`에 등록해 두세요.** DBVC는 도구 창 안에서 "이 호스트를 신뢰하시겠습니까?"에 답할 수 없어, 등록되지 않은 호스트로는 Pull이 실패합니다.
   5. 사내 폐쇄망에서는 GitLab 호스트로 나가는 22번 포트가 열려 있어야 합니다.

   HTTPS 원격을 매핑하면 Pull이 실패하면서 SSH로 바꾸는 방법을 안내합니다.
```

- [x] **Step 2: 문서가 코드와 맞는지 확인한다**

각 문장이 사실인지 확인한다. 어긋나면 문서가 아니라 확인 내용을 고친다.

```bash
# 안내 문구가 실제로 이 절차를 가리키는지
grep -n "OpenSSH 클라이언트\|known_hosts\|22번 포트\|git remote set-url" src/DBVC.Core/RemoteDiagnostics.cs

# 자격 증명을 저장하는 코드가 정말 없는지 (없어야 한다)
grep -rn "Password\|Token\|Credential" src/DBVC.Vsix --include=*.cs | grep -v obj/

# Pull 외에 네트워크를 쓰는 지점이 없는지
grep -n "Commands.Pull\|Commands.Fetch\|repo.Network" src/DBVC.Core/GitManager.cs
```

- [x] **Step 3: 전체 테스트로 아무것도 깨지지 않았는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net10.0`

Expected: 전부 PASS.

- [x] **Step 4: 커밋**

```bash
git add README.md
git commit -m "docs: SSH 전환 절차와 known_hosts 선행 등록 안내 추가"
```

---

## 수동 검증 체크리스트 (Windows)

CI가 검증하지 못하는 항목이다.

- [x] **개발 노트북, SSH 원격으로 Pull 성공.** 원격 URL을 `git@github.com:org/repo.git`으로 바꾼 뒤 Pull이 동작하는지. 이번 작업의 목적이다.
- [x] **HTTPS 원격을 매핑했을 때 안내.** `https://` 원격으로 Pull하면 "SSH 원격으로 바꾸세요"와 `git remote set-url` 예시가 보이는지. libgit2 영문 원문만 보이면 실패다.
- [x] **`known_hosts` 미등록 상태.** 새 호스트를 등록하지 않은 채 Pull하면 공개키·`known_hosts`·22번 포트 확인 목록이 보이는지.
- [x] **폐쇄망 PC, 방화벽 개방 전.** 22번이 막힌 상태에서 위와 같은 SSH 확인 목록이 보이는지.
- [x] **폐쇄망 PC, 방화벽 개방 후.** GitLab에서 Pull이 성공하는지. 이 항목이 실패하면 HTTPS + PAT 설계가 필요해지며, 스펙의 Out of Scope에 조건과 함께 적혀 있다.
- [x] **OpenSSH 클라이언트가 없는 기계.** 선택적 기능을 끈 상태에서 Pull하면 "OpenSSH 클라이언트를 설치하세요" 안내가 보이는지. `PATH`에 `ssh.exe`가 없어야 재현된다.
