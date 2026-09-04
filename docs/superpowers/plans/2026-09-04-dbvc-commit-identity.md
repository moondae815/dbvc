# 커밋 작성자 신원 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 신원(`user.name`/`user.email`)이 없는 저장소에서 커밋·Pull을 차단하고, 도구 안에서 신원을 설정할 길을 준다. 폴백 `DBVC User <dbvc@example.com>`을 없앤다.

**Architecture:** Core에 `GitIdentity`(판정·검증·쓰기)를 두고 `GitManager`가 그것으로 차단한다. VSIX는 배너로 미리 알리고, 차단된 자리에서 입력 다이얼로그를 띄운 뒤 같은 경로를 다시 탄다(CoAuthor 확인이 이미 쓰는 재진입 패턴).

**Tech Stack:** C# / .NET Framework 4.8 + netstandard2.0 (Core), WPF MVVM (VSIX), LibGit2Sharp 0.32.0, NUnit + Moq, Win32 `secur32.GetUserNameEx`.

**Spec:** `docs/superpowers/specs/2026-09-04-dbvc-commit-identity-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 배너, 버튼, ToolTip, 다이얼로그 라벨 포함.
- 주석은 **"왜"만** 적는다. 한국어 평서문.
- 커밋 메시지는 한국어 명령형 현재시제 + 스코프: `feat(core): ...`, `fix(vsix): ...`, `test(core): ...`, `docs: ...`.
- TDD: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋. 테스트 이름은 영어 `Method_Result_WhenCondition`.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`, `LibGit2Sharp 0.32.0` 고정.
- **테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.**
- 저장소 경로 구분자는 항상 `/`. 경로 규약은 `ObjectPathConvention` 한 곳에서만 정한다.
- 인증 정보는 디스크에 쓰지 않는다. 이 작업이 쓰는 것은 이름·메일뿐이며 암호가 아니다.
- 문서 파일(`.md`)은 **BOM 없이** 저장한다. UTF-8 + BOM 규약은 `.sql`에만 걸린다.
- 빌드·테스트 명령:
  - `dotnet build DBVC.slnx`
  - `dotnet test tests/DBVC.Core.Tests -f net10.0`
  - `dotnet test tests/DBVC.Vsix.Tests -f net48`
  - 단일 테스트: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~Detect_ReturnsUnknown"`

## 파일 구조

| 파일 | 책임 | 작업 |
| --- | --- | --- |
| `src/DBVC.Core/GitIdentity.cs` | 신원 판정·검증·쓰기. 규칙의 유일한 자리 | 신규 (Task 1) |
| `src/DBVC.Core/GitIdentityMissingException.cs` | 신원이 없어 거부했다는 사실 | 신규 (Task 3) |
| `src/DBVC.Core/GitManager.cs` | 폴백 제거, 커밋·Pull 진입부 차단 | 수정 (Task 3) |
| `tests/DBVC.Core.Tests/GitIdentityTests.cs` | `Detect`/`Validate`/`Write` | 신규 (Task 1) |
| `tests/DBVC.Core.Tests/GitManagerTests.cs` | 팩토리에 신원 심기, 차단 테스트 | 수정 (Task 2, 3) |
| `src/DBVC.Vsix/Services/ICommitIdentityDialog.cs` | 다이얼로그 이음매 + `CommitIdentityInput` | 신규 (Task 5) |
| `src/DBVC.Vsix/Services/CommitIdentityDialogAdapter.cs` | 실제 WPF 창을 띄우는 구현 | 신규 (Task 5) |
| `src/DBVC.Vsix/Services/WindowsAccountIdentity.cs` | Windows 계정에서 초깃값 추정 | 신규 (Task 5) |
| `src/DBVC.Vsix/UI/CommitIdentityDialog.xaml(.cs)` | 이름·메일 입력 창 | 신규 (Task 5) |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 배너 | 수정 (Task 4) |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 판정·배너 속성·명령·재진입 | 수정 (Task 4, 5, 6) |
| `src/DBVC.Vsix/DbvcServices.cs` | 다이얼로그 배선 | 수정 (Task 5) |
| `tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs` | 다이얼로그 대역 | 수정 (Task 5) |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 배너·다이얼로그·재진입 | 수정 (Task 4, 5, 6) |
| `README.md`, `docs/setup-checklist.md`, `docs/team-rollout-backlog.md`, `source.extension.vsixmanifest` | 문서·버전 | 수정 (Task 7) |

---

### Task 1: `GitIdentity` — 판정·검증·쓰기

**Files:**
- Create: `src/DBVC.Core/GitIdentity.cs`
- Test: `tests/DBVC.Core.Tests/GitIdentityTests.cs`

**Interfaces:**
- Consumes: `LibGit2Sharp.Repository`, `LibGit2Sharp.ConfigurationLevel`
- Produces:
  - `enum DBVC.Core.GitIdentityState { Unknown, Missing, Configured }`
  - `static GitIdentityState GitIdentity.Detect(string repoPath)`
  - `static bool GitIdentity.IsConfigured(Repository repo)`
  - `static void GitIdentity.Write(string repoPath, string name, string email)`
  - `static string? GitIdentity.Validate(string? name, string? email)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitIdentityTests.cs` 전체:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using DBVC.Core;
using LibGit2Sharp;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class GitIdentityTests
    {
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    // .git 내부에는 읽기 전용 파일이 있을 수 있다.
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_ident_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            _tempDirs.Add(path);
            return path;
        }

        /// <summary>
        /// 신원이 비어 있는 저장소를 만든다. 전역 config가 있는 기계에서도 판정이 흔들리지
        /// 않도록 로컬에 빈 값을 심는다 - BuildSignature의 탐색은 로컬 → 전역 → 시스템이라,
        /// 로컬을 비워 두기만 하면 실행 기계의 전역 설정이 결과를 바꾼다.
        /// </summary>
        private string NewRepoWithoutIdentity()
        {
            var path = NewTempDir();
            Repository.Init(path);
            using (var repo = new Repository(path))
            {
                repo.Config.Set("user.name", string.Empty, ConfigurationLevel.Local);
                repo.Config.Set("user.email", string.Empty, ConfigurationLevel.Local);
            }
            return path;
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenThePathIsNotARepository()
        {
            // 판정할 수 없는 것을 "없음"으로 뭉개면 배너가 상시로 뜬다.
            Assert.That(GitIdentity.Detect(NewTempDir()), Is.EqualTo(GitIdentityState.Unknown));
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenThePathIsEmpty()
        {
            Assert.That(GitIdentity.Detect("  "), Is.EqualTo(GitIdentityState.Unknown));
        }

        [Test]
        public void Detect_ReturnsMissing_WhenTheRepositoryHasNoIdentity()
        {
            Assert.That(GitIdentity.Detect(NewRepoWithoutIdentity()), Is.EqualTo(GitIdentityState.Missing));
        }

        [Test]
        public void Detect_ReturnsConfigured_AfterWrite()
        {
            var path = NewRepoWithoutIdentity();

            GitIdentity.Write(path, "홍길동", "gildong@example.com");

            Assert.That(GitIdentity.Detect(path), Is.EqualTo(GitIdentityState.Configured));
        }

        [Test]
        public void Write_TouchesOnlyTheLocalConfig()
        {
            // SSMS 확장이 개발자의 전역 git 설정을 조용히 바꾸면 다른 프로젝트의 커밋
            // 작성자까지 바뀐다. 되돌리는 길은 도구 안에 없다.
            var path = NewRepoWithoutIdentity();

            GitIdentity.Write(path, "홍길동", "gildong@example.com");

            using var repo = new Repository(path);
            Assert.Multiple(() =>
            {
                Assert.That(repo.Config.Get<string>("user.name", ConfigurationLevel.Local)?.Value,
                    Is.EqualTo("홍길동"));
                Assert.That(repo.Config.Get<string>("user.email", ConfigurationLevel.Global)?.Value,
                    Is.Not.EqualTo("gildong@example.com"),
                    "전역 config에 쓰면 안 된다");
            });
        }

        [Test]
        public void Write_TrimsSurroundingWhitespace()
        {
            var path = NewRepoWithoutIdentity();

            GitIdentity.Write(path, "  홍길동  ", "  gildong@example.com  ");

            using var repo = new Repository(path);
            Assert.Multiple(() =>
            {
                Assert.That(repo.Config.Get<string>("user.name")?.Value, Is.EqualTo("홍길동"));
                Assert.That(repo.Config.Get<string>("user.email")?.Value, Is.EqualTo("gildong@example.com"));
            });
        }

        [TestCase(null, "gildong@example.com", TestName = "Validate_Rejects_WhenNameIsNull")]
        [TestCase("   ", "gildong@example.com", TestName = "Validate_Rejects_WhenNameIsBlank")]
        [TestCase("홍길동", null, TestName = "Validate_Rejects_WhenEmailIsNull")]
        [TestCase("홍길동", "   ", TestName = "Validate_Rejects_WhenEmailIsBlank")]
        [TestCase("홍길동", "gildong", TestName = "Validate_Rejects_WhenEmailHasNoAtSign")]
        [TestCase("홍길동", "gildong@example", TestName = "Validate_Rejects_WhenEmailHasNoDot")]
        [TestCase("홍길동", "gil dong@example.com", TestName = "Validate_Rejects_WhenEmailHasWhitespace")]
        [TestCase("홍길동", "@example.com", TestName = "Validate_Rejects_WhenEmailHasNoLocalPart")]
        public void Validate_ReturnsAKoreanReason_WhenInputIsUnusable(string? name, string? email)
        {
            var reason = GitIdentity.Validate(name, email);

            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("홍길동", "gildong@example.com")]
        [TestCase("Gil-Dong Hong", "gil.dong@corp.example.co.kr")]
        public void Validate_ReturnsNull_WhenInputIsUsable(string name, string email)
        {
            Assert.That(GitIdentity.Validate(name, email), Is.Null);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitIdentityTests"`
Expected: FAIL — `GitIdentity` / `GitIdentityState` 이름을 찾지 못해 컴파일되지 않는다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/GitIdentity.cs` 전체:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using LibGit2Sharp;

namespace DBVC.Core
{
    /// <summary>저장소의 커밋 작성자 신원 상태.</summary>
    public enum GitIdentityState
    {
        /// <summary>판정할 근거가 없다. 경로가 없거나 Git 저장소가 아니다.</summary>
        Unknown,

        /// <summary>user.name 또는 user.email이 없다. 커밋하면 누가 바꿨는지 남지 않는다.</summary>
        Missing,

        /// <summary>로컬·전역·시스템 어딘가에 신원이 있다.</summary>
        Configured
    }

    /// <summary>
    /// 커밋 작성자 신원을 판정하고 저장소 로컬 config에 쓴다.
    ///
    /// 판정 규칙이 화면과 Core 두 곳에 생기면 갈라지고, 갈라진 날 배너 없이 차단되는 사람이
    /// 나온다. 그래서 규칙은 <see cref="IsConfigured"/> 하나뿐이고 나머지는 그것을 부른다.
    /// </summary>
    public static class GitIdentity
    {
        /// <summary>
        /// 저장소를 열어 신원 유무를 본다.
        ///
        /// 판정하지 못하면 <see cref="GitIdentityState.Unknown"/>이다. 판정할 수 없는 경로를
        /// "없음"으로 뭉개면 배너가 상시로 뜬다 - <see cref="RepositoryEncoding.Detect"/>가
        /// Unknown을 두는 이유와 같다.
        /// </summary>
        public static GitIdentityState Detect(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return GitIdentityState.Unknown;

            try
            {
                if (!Directory.Exists(repoPath) || !Repository.IsValid(repoPath))
                {
                    return GitIdentityState.Unknown;
                }

                using var repo = new Repository(repoPath);
                return IsConfigured(repo) ? GitIdentityState.Configured : GitIdentityState.Missing;
            }
            catch (Exception ex)
            {
                // 판정 실패로 접속 전체가 실패하면 배너 하나 때문에 화면을 잃는다.
                Debug.WriteLine($"GitIdentity.Detect failed for '{repoPath}': {ex.Message}");
                return GitIdentityState.Unknown;
            }
        }

        /// <summary>
        /// 이미 열린 저장소에 신원이 있는지. 규칙의 유일한 자리다.
        ///
        /// BuildSignature는 user.name·user.email 중 하나라도 없으면 null을 낸다. 찾는 순서는
        /// git과 같은 로컬 → 전역 → 시스템이므로, 전역에만 설정해 둔 사람도 여기서 참이 된다.
        /// </summary>
        public static bool IsConfigured(Repository repo)
        {
            if (repo == null) return false;
            return repo.Config.BuildSignature(DateTimeOffset.Now) != null;
        }

        /// <summary>
        /// 신원을 저장소 로컬 config(.git/config)에 쓴다.
        ///
        /// 전역에 쓰지 않는다. SSMS 확장이 개발자의 다른 프로젝트 커밋 작성자까지 바꾸는 것은
        /// 되돌리는 길이 도구 안에 없는 부작용이다.
        /// </summary>
        public static void Write(string repoPath, string name, string email)
        {
            using var repo = new Repository(repoPath);
            repo.Config.Set("user.name", (name ?? string.Empty).Trim(), ConfigurationLevel.Local);
            repo.Config.Set("user.email", (email ?? string.Empty).Trim(), ConfigurationLevel.Local);
        }

        /// <summary>
        /// 입력이 쓸 수 있는 것인지 본다. 통과면 null, 아니면 사용자에게 보일 한국어 사유.
        ///
        /// 형식까지만 본다. 메일이 GitLab 계정과 실제로 일치하는지는 확인할 수 없다 -
        /// 폐쇄망 GitLab API를 부르는 것은 이 기능의 범위 밖이다.
        /// </summary>
        public static string? Validate(string? name, string? email)
        {
            if (string.IsNullOrWhiteSpace(name)) return "이름을 입력하세요.";

            var trimmed = (email ?? string.Empty).Trim();
            if (trimmed.Length == 0) return "메일 주소를 입력하세요.";

            var at = trimmed.IndexOf('@');
            if (at <= 0 || at == trimmed.Length - 1)
            {
                return "메일 주소 형식이 올바르지 않습니다. 예: hong@corp.co.kr";
            }

            var domain = trimmed.Substring(at + 1);
            if (!domain.Contains(".") || domain.StartsWith(".") || domain.EndsWith("."))
            {
                return "메일 주소 형식이 올바르지 않습니다. 예: hong@corp.co.kr";
            }

            // 공백이 든 주소는 git이 받아 주더라도 이력에서 사람을 찾을 수 없게 만든다.
            if (trimmed.IndexOf(' ') >= 0 || trimmed.IndexOf('\t') >= 0)
            {
                return "메일 주소에 공백이 들어갈 수 없습니다.";
            }

            return null;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitIdentityTests"`
Expected: PASS (16개)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/GitIdentity.cs tests/DBVC.Core.Tests/GitIdentityTests.cs
git commit -m "feat(core): 커밋 작성자 신원을 판정하고 쓰는 GitIdentity를 더한다"
```

---

### Task 2: 테스트 저장소 팩토리가 신원을 심게 한다

폴백을 없애기 **전에** 한다. 지금 테스트는 실행 기계의 전역 `git config`에 기대고 있고(스펙 1.3), 이 순서로 해야 다음 태스크의 실패가 "폴백 제거가 깨뜨린 것"이 아니라 "차단이 실제로 동작하는 것"으로 읽힌다.

**Files:**
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs` (`NewRepoWithCommit`, `NewClonedRepoWithBareOrigin`, `CloneRepository` 테스트들)

**Interfaces:**
- Consumes: `GitIdentity.Write` (Task 1)
- Produces: `private static void SeedIdentity(string repoPath)` — 이후 태스크의 테스트가 부른다

- [ ] **Step 1: 헬퍼를 더한다**

`GitManagerTests.cs`의 `TestSignature` 선언 아래에 넣는다:

```csharp
/// <summary>
/// 커밋할 수 있는 저장소로 만든다. GitManager는 신원이 없으면 커밋을 거부하므로
/// 실제 커밋을 부르는 테스트는 전부 이것을 거쳐야 한다.
///
/// 실행 기계의 전역 config에 기대지 않는 것이 요점이다 - 기대면 CI와 개발 PC에서
/// 다른 결과가 나오고, 어느 쪽이 옳은지 테스트만 봐서는 알 수 없다.
/// </summary>
private static void SeedIdentity(string repoPath)
{
    GitIdentity.Write(repoPath, "Test", "test@example.com");
}
```

- [ ] **Step 2: 저장소를 만드는 세 자리에서 부른다**

1. `NewRepoWithCommit` — `Repository.Init(path)` 다음 줄에 `SeedIdentity(path);`
2. `NewClonedRepoWithBareOrigin` — `Repository.Clone(originPath, localPath);` 다음 줄에 `SeedIdentity(localPath);`
   (bare 원격에는 심지 않는다. 원격에 커밋하는 테스트는 없다)
3. `Repository.Clone(...)`으로 로컬 클론을 직접 만드는 테스트들 — `Repository.Clone` 호출 바로 뒤에 클론 경로로 `SeedIdentity(...)`를 부른다. `grep -n "Repository.Clone(" tests/DBVC.Core.Tests/GitManagerTests.cs`로 전부 찾는다. bare 원격(`IsBare = true`)을 만드는 호출은 제외한다.

`GitManager.CloneRepository`가 만든 클론(`CloneRepository_*` 테스트)은 커밋하지 않으므로 심지 않는다. 커밋까지 가는 테스트가 있으면 그 테스트 안에서 `SeedIdentity(targetPath)`를 부른다.

- [ ] **Step 3: 여전히 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS — 동작을 바꾸지 않은 준비 작업이다. 실패한다면 `SeedIdentity`를 저장소가 만들어지기 전에 불렀다는 뜻이다.

- [ ] **Step 4: 커밋**

```bash
git add tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "test(core): 테스트 저장소가 실행 기계의 git 설정에 기대지 않게 한다"
```

---

### Task 3: 폴백을 없애고 커밋·Pull을 차단한다

**Files:**
- Create: `src/DBVC.Core/GitIdentityMissingException.cs`
- Modify: `src/DBVC.Core/GitManager.cs` (`:20-21` 상수, `:299~` `CommitChanges` 진입부, `:911~` `BuildSignature`)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `GitIdentity.IsConfigured` (Task 1), `SeedIdentity` (Task 2)
- Produces: `class DBVC.Core.GitIdentityMissingException : InvalidOperationException`, `const string GitIdentityMissingException.UserMessage`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`GitManagerTests.cs`에 넣는다. 신원 없는 저장소를 만드는 헬퍼가 함께 필요하다:

```csharp
// ---------- 커밋 작성자 신원 ----------

/// <summary>
/// 신원이 비어 있는 저장소. 로컬에 빈 값을 심는 이유는 GitIdentityTests와 같다 -
/// 로컬을 비워 두기만 하면 실행 기계의 전역 설정이 결과를 바꾼다.
/// </summary>
private string NewRepoWithCommitButNoIdentity()
{
    var path = NewRepoWithCommit();
    using (var repo = new Repository(path))
    {
        repo.Config.Set("user.name", string.Empty, ConfigurationLevel.Local);
        repo.Config.Set("user.email", string.Empty, ConfigurationLevel.Local);
    }
    return path;
}

[Test]
public void CommitChanges_Throws_WhenTheIdentityIsMissing()
{
    var repoPath = NewRepoWithCommitButNoIdentity();
    WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id BIGINT);");
    var git = NewGitManager("srv", "db", repoPath);

    Assert.Throws<GitIdentityMissingException>(() => git.CommitChanges("srv", "db", "메시지"));
}

[Test]
public void CommitChanges_LeavesTheIndexUntouched_WhenTheIdentityIsMissing()
{
    // 차단은 아무것도 바꾸지 않아야 한다. Commands.Stage가 서명보다 먼저 돌기 때문에,
    // 검사를 BuildSignature 자리에 두면 스테이징만 된 채 실패한다.
    var repoPath = NewRepoWithCommitButNoIdentity();
    WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id BIGINT);");
    var git = NewGitManager("srv", "db", repoPath);

    Assert.Throws<GitIdentityMissingException>(() => git.CommitChanges("srv", "db", "메시지"));

    using var repo = new Repository(repoPath);
    var staged = repo.Diff.Compare<TreeChanges>(repo.Head.Tip.Tree, DiffTargets.Index);
    Assert.That(staged.Count, Is.EqualTo(0), "차단된 커밋이 인덱스를 건드리면 안 된다");
}

[Test]
public void PullChanges_Throws_WhenTheIdentityIsMissing()
{
    // Pull은 병합 커밋을 만든다. 커밋만 막고 여기를 열어 두면 폴백이 그리로 새어 나간다.
    var (localPath, _) = NewClonedRepoWithBareOrigin();
    using (var repo = new Repository(localPath))
    {
        repo.Config.Set("user.name", string.Empty, ConfigurationLevel.Local);
        repo.Config.Set("user.email", string.Empty, ConfigurationLevel.Local);
    }
    var git = NewGitManager("srv", "db", localPath);

    Assert.Throws<GitIdentityMissingException>(() => git.PullChanges("srv", "db"));
}

[Test]
public void CommitChanges_UsesTheConfiguredIdentity_WhenItIsPresent()
{
    var repoPath = NewRepoWithCommit();
    GitIdentity.Write(repoPath, "홍길동", "gildong@corp.co.kr");
    WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id BIGINT);");
    var git = NewGitManager("srv", "db", repoPath);

    git.CommitChanges("srv", "db", "메시지");

    using var repo = new Repository(repoPath);
    Assert.Multiple(() =>
    {
        Assert.That(repo.Head.Tip.Author.Name, Is.EqualTo("홍길동"));
        Assert.That(repo.Head.Tip.Author.Email, Is.EqualTo("gildong@corp.co.kr"));
    });
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "Identity"`
Expected: FAIL — `GitIdentityMissingException` 이름을 찾지 못한다.

- [ ] **Step 3: 예외 타입을 만든다**

`src/DBVC.Core/GitIdentityMissingException.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 커밋 작성자 신원(user.name·user.email)이 없어 커밋 또는 병합을 거부했다.
    /// 저장소는 손대지 않았다 - 스테이징도 하지 않는다.
    ///
    /// <see cref="InvalidOperationException"/>을 물려받는 이유는
    /// <see cref="GitRemoteNotConfiguredException"/>과 같다. 화면을 거치지 않는 호출자도
    /// 지금까지 이 타입으로 안내를 받아 왔고, 그 동작이 옳다.
    ///
    /// 예전에는 이 자리에서 "DBVC User &lt;dbvc@example.com&gt;"으로 커밋했다. 20명이 한
    /// 저장소를 쓰면 blame과 MR 작성자가 전부 같은 이름이 되고, 되돌리려면 이력을 다시 써야
    /// 한다. 폴백을 되살리지 말 것.
    /// </summary>
    public class GitIdentityMissingException : InvalidOperationException
    {
        public const string UserMessage =
            "커밋 작성자가 설정되어 있지 않습니다. 이름과 메일 주소를 먼저 지정하세요.";

        public GitIdentityMissingException() : base(UserMessage)
        {
        }
    }
}
```

- [ ] **Step 4: 폴백을 지우고 차단을 넣는다**

`GitManager.cs`에서 세 곳을 고친다.

1. `:20-21`의 상수 두 줄을 지운다.

```csharp
// 지운다
private const string DefaultAuthorName = "DBVC User";
private const string DefaultAuthorEmail = "dbvc@example.com";
```

2. `BuildSignature`(`:911`)를 바꾼다.

```csharp
private static Signature BuildSignature(Repository repo)
{
    // 폴백을 두지 않는다. 신원 없이 만든 커밋은 공용 저장소에서 되돌릴 수 없다 -
    // 이력을 다시 쓰면 저장소를 가진 모두가 클론을 다시 받아야 한다.
    return repo.Config.BuildSignature(DateTimeOffset.Now)
        ?? throw new GitIdentityMissingException();
}
```

3. `CommitChanges`의 `using var repo = new Repository(repoPath);` **바로 다음**, `var paths = ...` 앞에 검사를 넣는다.

```csharp
// 스테이징보다 먼저 본다. Commands.Stage가 서명보다 앞서 돌기 때문에, 검사를
// BuildSignature 자리에 맡기면 스테이징만 된 채 실패해 작업 트리 상태가 바뀐다.
// 차단은 아무것도 바꾸지 않아야 한다.
if (!GitIdentity.IsConfigured(repo)) throw new GitIdentityMissingException();
```

`PullChanges`는 고치지 않는다. `:356`의 `BuildSignature`가 이미 부작용 이전이라 2번 수정만으로 막힌다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS 전부. 신원을 심지 않은 테스트가 남아 있으면 여기서 `GitIdentityMissingException`으로 실패한다 — Task 2의 방식대로 그 테스트가 쓰는 저장소에 `SeedIdentity`를 부른다.

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Core/GitIdentityMissingException.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 신원 없는 커밋·Pull을 거부하고 DBVC User 폴백을 없앤다"
```

---

### Task 4: 배너

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`ProbeContext`, `ApplyContextProbe`, `ContextProbe`, 바인딩 속성)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (인코딩 배너 아래)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `GitIdentity.Detect`, `GitIdentityState` (Task 1)
- Produces: `bool ViewChangesViewModel.IsCommitIdentityMissing`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`ViewChangesViewModelTests.cs`의 "저장소 인코딩 전환" 절 아래에 넣는다. 실제 Git 저장소가 필요하므로 헬퍼를 하나 더 만든다:

```csharp
// ---------- 커밋 작성자 신원 ----------

/// <summary>
/// 실제 Git 저장소로 매핑을 갈아 끼운다. 기본 매핑의 GitPath(C:\repo)는 저장소가 아니라
/// 신원 판정이 늘 Unknown으로 떨어진다.
/// </summary>
private string NewMappedGitRepo(bool withIdentity, MappingMode mode = MappingMode.Write)
{
    var repoPath = Path.Combine(Path.GetTempPath(), "dbvc_vmident_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(repoPath);
    _tempDirs.Add(repoPath);

    LibGit2Sharp.Repository.Init(repoPath);
    if (withIdentity)
    {
        GitIdentity.Write(repoPath, "홍길동", "gildong@corp.co.kr");
    }
    else
    {
        // 로컬을 비워 두기만 하면 실행 기계의 전역 설정이 결과를 바꾼다.
        using var repo = new LibGit2Sharp.Repository(repoPath);
        repo.Config.Set("user.name", string.Empty, LibGit2Sharp.ConfigurationLevel.Local);
        repo.Config.Set("user.email", string.Empty, LibGit2Sharp.ConfigurationLevel.Local);
    }

    _config.Setup(c => c.TryGetMapping(Server, Database))
        .Returns(new MappingConfig
        {
            ServerName = Server,
            DatabaseName = Database,
            GitPath = repoPath,
            Mode = mode,
            Branch = mode == MappingMode.Write ? null : "develop"
        });

    return repoPath;
}

[Test]
public void Connect_RaisesTheIdentityBanner_WhenTheRepositoryHasNoAuthor()
{
    NewMappedGitRepo(withIdentity: false);

    var vm = NewConnectedViewModel();

    Assert.That(vm.IsCommitIdentityMissing, Is.True);
}

[Test]
public void Connect_LeavesTheIdentityBannerDown_WhenTheAuthorIsConfigured()
{
    NewMappedGitRepo(withIdentity: true);

    var vm = NewConnectedViewModel();

    Assert.That(vm.IsCommitIdentityMissing, Is.False);
}

[Test]
public void Connect_LeavesTheIdentityBannerDown_WhenThePathIsNotARepository()
{
    // SetUp의 기본 매핑이 이 경우다 - GitPath가 C:\repo라 매핑은 있지만 저장소가 아니다.
    // 판정할 수 없는 것을 "없음"으로 뭉개면 이 파일의 거의 모든 테스트에서 배너가 뜬다.
    var vm = NewConnectedViewModel();

    Assert.That(vm.IsCommitIdentityMissing, Is.False);
}

[TestCase(MappingMode.Deploy)]
[TestCase(MappingMode.Audit)]
public void Connect_RaisesTheIdentityBanner_EvenForReadOnlyModes(MappingMode mode)
{
    // 인코딩 배너와 다르다. 배포·감사 클론도 Pull은 하고, 비-fast-forward Pull은 병합
    // 커밋을 만들어 같은 신원을 요구한다. 모드로 걸러 버리면 그 사람만 배너 없이
    // 차단당해 빠져나올 길이 없다.
    NewMappedGitRepo(withIdentity: false, mode: mode);

    var vm = NewConnectedViewModel();

    Assert.That(vm.IsCommitIdentityMissing, Is.True);
}
```

파일 상단 `using`에 `DBVC.Core`가 이미 있는지 확인하고, 없으면 더한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "IdentityBanner"`
Expected: FAIL — `IsCommitIdentityMissing`이 없어 컴파일되지 않는다.

- [ ] **Step 3: ViewModel에 판정과 속성을 넣는다**

1. `ContextProbe`에 필드를 더한다(`Encoding` 아래):

```csharp
/// <summary>매핑이 없거나 유효한 저장소가 아니면 Unknown이다. 판정은 Core가 한다.</summary>
public GitIdentityState Identity { get; set; } = GitIdentityState.Unknown;
```

2. `ProbeContext`의 `if (probe.IsMapped)` 블록 안, `probe.Encoding = ...` 다음 줄에 더한다:

```csharp
// config 파일을 여는 일이라 UI 스레드에서 부르지 않는다. 인코딩 판정과 같은 이유다.
probe.Identity = GitIdentity.Detect(probedMapping.GitPath);
```

3. `ApplyContextProbe`의 `IsRepositoryEncodingLegacy = ...` 다음 줄에 더한다:

```csharp
// 인코딩 배너와 달리 mode로 거르지 않는다. 배포·감사 클론도 Pull의 병합 커밋에 신원이
// 필요하고, 걸러 버리면 그 사람만 배너 없이 차단당해 빠져나올 길이 없다.
IsCommitIdentityMissing = probe.Identity == GitIdentityState.Missing;
```

4. `IsRepositoryEncodingLegacy` 속성 옆에 같은 모양으로 더한다:

```csharp
private bool _isCommitIdentityMissing;

/// <summary>
/// 커밋 작성자 신원이 없다. 배너가 이 값을 본다.
/// 판정할 수 없는 경우(저장소가 아닌 경로)는 거짓이다 - 매핑되지 않은 대상에서
/// 배너가 상시로 뜨는 것을 막는다.
/// </summary>
public bool IsCommitIdentityMissing
{
    get => _isCommitIdentityMissing;
    private set
    {
        if (_isCommitIdentityMissing == value) return;
        _isCommitIdentityMissing = value;
        OnPropertyChanged();
    }
}
```

`ApplyContextProbe`의 접속 실패 조기 반환 갈래(`if (probe.ConnectionError != null)`)에서는 `IsTrackerOutdated = false`와 나란히 `IsCommitIdentityMissing = false;`를 넣는다. 접속하지 못한 대상의 배너를 남겨 두면 대상을 바꿔도 이전 대상의 배너가 남는다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "IdentityBanner"`
Expected: PASS (5개)

- [ ] **Step 5: XAML에 배너를 더한다**

`ViewChangesControl.xaml`의 인코딩 배너 `</Border>` 다음에 넣는다. 버튼 명령은 Task 5에서 만든다 — 지금은 자리만 잡고, Task 5의 Step에서 `Command`를 채운다.

```xml
<!--
    커밋 작성자 안내. 인코딩·추적기 배너와 별도로 둔다 - 원인도 조치도 다르고,
    셋이 동시에 뜰 수 있어야 한다.
-->
<Border Background="#FFF4CE" BorderBrush="#E0C77A" BorderThickness="1"
        Padding="8,5" Margin="5,0,5,4"
        Visibility="{Binding IsCommitIdentityMissing, Converter={StaticResource BoolToVis}}">
    <DockPanel LastChildFill="True">
        <Button DockPanel.Dock="Right" Content="작성자 설정..." Width="110" Margin="8,0,0,0"
                Command="{Binding SetCommitIdentityCommand}"
                ToolTip="커밋에 남을 이름과 메일 주소를 지정합니다. 이 저장소(.git/config)에만 저장되며 다른 저장소에는 영향이 없습니다."/>
        <TextBlock Text="커밋 작성자가 설정되어 있지 않습니다. 지금은 커밋과 Pull이 막힙니다."
                   Foreground="#6B5A00" TextWrapping="Wrap" FontWeight="SemiBold"
                   VerticalAlignment="Center"/>
    </DockPanel>
</Border>
```

- [ ] **Step 6: 빌드를 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. `SetCommitIdentityCommand`는 아직 없지만 WPF 바인딩은 컴파일 타임에 확인되지 않으므로 빌드는 통과한다. Task 5에서 만든다.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 커밋 작성자가 없는 저장소를 배너로 알린다"
```

---

### Task 5: 입력 다이얼로그와 배너 버튼

**Files:**
- Create: `src/DBVC.Vsix/Services/ICommitIdentityDialog.cs`
- Create: `src/DBVC.Vsix/Services/CommitIdentityDialogAdapter.cs`
- Create: `src/DBVC.Vsix/Services/WindowsAccountIdentity.cs`
- Create: `src/DBVC.Vsix/UI/CommitIdentityDialog.xaml`, `src/DBVC.Vsix/UI/CommitIdentityDialog.xaml.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (생성자 매개변수, 명령)
- Modify: `src/DBVC.Vsix/DbvcServices.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `GitIdentity.Write`, `GitIdentity.Validate`, `GitIdentity.Detect` (Task 1), `IsCommitIdentityMissing` (Task 4)
- Produces:
  - `sealed class CommitIdentityInput { string Name; string Email; }`
  - `interface ICommitIdentityDialog { CommitIdentityInput? Prompt(string suggestedName, string suggestedEmail); }`
  - `static (string Name, string Email) WindowsAccountIdentity.Suggest()`
  - `ICommand ViewChangesViewModel.SetCommitIdentityCommand`
  - `ViewChangesViewModel` 생성자에 `ICommitIdentityDialog? identityDialog = null` 추가

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`TestDoubles.cs`에 대역을 더한다:

```csharp
/// <summary>신원 입력 대화상자의 대역. 무엇을 돌려줄지와 몇 번 불렸는지를 기록한다.</summary>
internal sealed class RecordingIdentityDialog : ICommitIdentityDialog
{
    public CommitIdentityInput? Result { get; set; }
    public int PromptCount { get; private set; }
    public string? SuggestedName { get; private set; }
    public string? SuggestedEmail { get; private set; }

    public CommitIdentityInput? Prompt(string suggestedName, string suggestedEmail)
    {
        PromptCount++;
        SuggestedName = suggestedName;
        SuggestedEmail = suggestedEmail;
        return Result;
    }
}
```

`ViewChangesViewModelTests.cs`에 필드를 하나 더한다 — `_connectDialog`를 두는 방식과 같다:

```csharp
private RecordingIdentityDialog _identityDialog = null!;
```

`SetUp()`에서 `_connectDialog = new RecordingConnectDialog();` 옆에 `_identityDialog = new RecordingIdentityDialog();`를 더하고, `NewViewModel()`의 호출을 고친다. 기존 인자는 순서로 넘어가고 있으므로 새 인자는 **이름을 붙여** 넘긴다(중간의 `scheduler`를 건너뛰기 위해서다):

```csharp
private ViewChangesViewModel NewViewModel()
{
    return new ViewChangesViewModel(
        _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
        _cleaner.Object, _connectDialog, _credentials.Object, _ssms.Object,
        identityDialog: _identityDialog);
}
```

```csharp
[Test]
public void SetCommitIdentityCommand_WritesTheIdentityAndDropsTheBanner_WhenTheUserConfirms()
{
    var repoPath = NewMappedGitRepo(withIdentity: false);
    var vm = NewConnectedViewModel();
    Assert.That(vm.IsCommitIdentityMissing, Is.True, "사전 조건: 배너가 떠 있어야 검증이 의미 있다");
    _identityDialog.Result = new CommitIdentityInput { Name = "홍길동", Email = "gildong@corp.co.kr" };

    vm.SetCommitIdentityCommand.Execute(null);

    Assert.Multiple(() =>
    {
        Assert.That(GitIdentity.Detect(repoPath), Is.EqualTo(GitIdentityState.Configured));
        Assert.That(vm.IsCommitIdentityMissing, Is.False,
            "설정이 끝났는데 배너가 남으면 사용자가 또 누른다");
    });
}

[Test]
public void SetCommitIdentityCommand_WritesNothing_WhenTheUserCancels()
{
    var repoPath = NewMappedGitRepo(withIdentity: false);
    var vm = NewConnectedViewModel();
    _identityDialog.Result = null;

    vm.SetCommitIdentityCommand.Execute(null);

    Assert.Multiple(() =>
    {
        Assert.That(GitIdentity.Detect(repoPath), Is.EqualTo(GitIdentityState.Missing));
        Assert.That(vm.IsCommitIdentityMissing, Is.True);
    });
}

[Test]
public void SetCommitIdentityCommand_ReportsTheReasonAndKeepsTheBanner_WhenTheInputIsInvalid()
{
    NewMappedGitRepo(withIdentity: false);
    var vm = NewConnectedViewModel();
    _identityDialog.Result = new CommitIdentityInput { Name = "홍길동", Email = "gildong" };

    vm.SetCommitIdentityCommand.Execute(null);

    Assert.Multiple(() =>
    {
        Assert.That(_notifier.Errors, Is.Not.Empty);
        Assert.That(vm.IsCommitIdentityMissing, Is.True);
    });
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "SetCommitIdentityCommand"`
Expected: FAIL — `ICommitIdentityDialog`가 없어 컴파일되지 않는다.

- [ ] **Step 3: 이음매와 추정기를 만든다**

`src/DBVC.Vsix/Services/ICommitIdentityDialog.cs`:

```csharp
namespace DBVC.Vsix.Services
{
    /// <summary>사용자가 입력한 커밋 작성자 신원.</summary>
    public sealed class CommitIdentityInput
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>
    /// 커밋 작성자를 사용자에게 묻는다. ViewModel이 WPF 창에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface ICommitIdentityDialog
    {
        /// <summary>사용자가 취소하면 <c>null</c>.</summary>
        CommitIdentityInput? Prompt(string suggestedName, string suggestedEmail);
    }
}
```

`src/DBVC.Vsix/Services/WindowsAccountIdentity.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 로그온 계정에서 커밋 작성자 초깃값을 추정한다.
    ///
    /// 얇은 어댑터다. 판단(형식이 쓸 만한가)은 하지 않는다 - 그것은 Core의
    /// <c>GitIdentity.Validate</c>가 한다. SSMS 어댑터에서 판단 로직을 SsmsUrn으로 뺀 것과
    /// 같은 규칙이다.
    ///
    /// 추정값을 자동 확정하지 않는 이유는 메일이 틀렸을 때 드러나는 시점이 첫 MR이기
    /// 때문이다. 사람이 한 번 보게 한다.
    /// </summary>
    public static class WindowsAccountIdentity
    {
        // EXTENDED_NAME_FORMAT. 3 = NameDisplay(표시 이름), 8 = NameUserPrincipal(UPN).
        private const int NameDisplay = 3;
        private const int NameUserPrincipal = 8;

        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserNameEx(int nameFormat, StringBuilder nameBuffer, ref uint size);

        /// <summary>
        /// 추정한 (이름, 메일). 도메인에 가입되지 않은 PC에서는 두 형식 모두 실패하므로
        /// 이름은 Windows 계정명으로 대체하고 메일은 빈 문자열로 둔다.
        /// </summary>
        public static (string Name, string Email) Suggest()
        {
            var name = Query(NameDisplay);
            var email = Query(NameUserPrincipal);

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Environment.UserName ?? string.Empty;
            }

            return (name ?? string.Empty, email ?? string.Empty);
        }

        private static string? Query(int format)
        {
            try
            {
                uint size = 256;
                var buffer = new StringBuilder((int)size);

                if (GetUserNameEx(format, buffer, ref size)) return buffer.ToString();

                // 버퍼가 작으면 필요한 크기를 size에 돌려준다. 한 번만 다시 시도한다.
                buffer = new StringBuilder((int)size);
                return GetUserNameEx(format, buffer, ref size) ? buffer.ToString() : null;
            }
            catch (Exception ex)
            {
                // 추정 실패는 결함이 아니다. 사용자가 두 칸을 직접 치면 된다.
                Debug.WriteLine($"WindowsAccountIdentity.Query({format}) failed: {ex.Message}");
                return null;
            }
        }
    }
}
```

- [ ] **Step 4: WPF 창과 어댑터를 만든다**

`src/DBVC.Vsix/UI/CommitIdentityDialog.xaml`:

```xml
<Window x:Class="DBVC.Vsix.UI.CommitIdentityDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="커밋 작성자 설정" Width="460" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False">
    <StackPanel Margin="14">
        <TextBlock TextWrapping="Wrap" Margin="0,0,0,10"
                   Text="커밋에 남을 이름과 메일 주소를 입력하세요. 이 값으로 git blame과 GitLab이 작성자를 찾습니다."/>

        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Grid.Column="0" Text="이름" VerticalAlignment="Center" Margin="0,0,8,6"/>
            <TextBox x:Name="NameBox" Grid.Row="0" Grid.Column="1" Margin="0,0,0,6"/>

            <TextBlock Grid.Row="1" Grid.Column="0" Text="메일 주소" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBox x:Name="EmailBox" Grid.Row="1" Grid.Column="1"/>
        </Grid>

        <!-- 저장 위치를 밝힌다. 전역 설정이 바뀐다고 오해하면 도구를 못 믿게 된다. -->
        <TextBlock Margin="0,10,0,0" TextWrapping="Wrap" Opacity="0.8"
                   Text="이 저장소에만 저장됩니다(.git/config). 이 PC의 다른 저장소에는 영향이 없습니다. GitLab 계정과 같은 메일 주소를 쓰면 커밋이 계정에 연결됩니다."/>

        <TextBlock x:Name="ErrorLabel" Margin="0,10,0,0" Foreground="#B00020"
                   TextWrapping="Wrap" Visibility="Collapsed"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,14,0,0">
            <Button Content="확인" Width="90" Margin="0,0,8,0" IsDefault="True" Click="Ok_Click"/>
            <Button Content="취소" Width="90" IsCancel="True"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`src/DBVC.Vsix/UI/CommitIdentityDialog.xaml.cs`:

```csharp
using System.Windows;
using DBVC.Core;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 커밋 작성자를 입력받는다. 형식 판정은 Core의 GitIdentity.Validate가 한다 -
    /// 여기서도 판정하면 같은 규칙이 두 곳에 생기고 언젠가 갈라진다.
    /// </summary>
    public partial class CommitIdentityDialog : Window
    {
        public CommitIdentityDialog(string suggestedName, string suggestedEmail)
        {
            InitializeComponent();
            NameBox.Text = suggestedName ?? string.Empty;
            EmailBox.Text = suggestedEmail ?? string.Empty;
        }

        public CommitIdentityInput? Result { get; private set; }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var reason = GitIdentity.Validate(NameBox.Text, EmailBox.Text);
            if (reason != null)
            {
                ErrorLabel.Text = reason;
                ErrorLabel.Visibility = Visibility.Visible;
                return;
            }

            Result = new CommitIdentityInput { Name = NameBox.Text.Trim(), Email = EmailBox.Text.Trim() };
            DialogResult = true;
        }
    }
}
```

`src/DBVC.Vsix/Services/CommitIdentityDialogAdapter.cs`:

```csharp
using DBVC.Vsix.UI;

namespace DBVC.Vsix.Services
{
    /// <summary>실제 WPF 창을 띄우는 구현.</summary>
    public sealed class CommitIdentityDialogAdapter : ICommitIdentityDialog
    {
        public CommitIdentityInput? Prompt(string suggestedName, string suggestedEmail)
        {
            var dialog = new CommitIdentityDialog(suggestedName, suggestedEmail)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
    }
}
```

`.xaml`을 새로 넣었으므로 `DBVC.Vsix.csproj`가 `Page`로 잡는지 확인한다. 기존 `RepositoryConnectDialog.xaml`이 명시적으로 나열되어 있으면 같은 방식으로 더한다. 와일드카드로 잡히면 아무것도 하지 않는다.

- [ ] **Step 5: ViewModel에 명령을 넣는다**

1. 필드와 생성자 매개변수를 더한다(`_connectDialog` 옆):

```csharp
private readonly ICommitIdentityDialog _identityDialog;
```

생성자 시그니처에 `ICommitIdentityDialog? identityDialog = null`을 **`scheduler` 앞**에 넣지 말고 마지막에 더한다 — 기존 호출자가 이름 없는 인자로 넘기는 순서를 깨지 않기 위해서다. 본문:

```csharp
_identityDialog = identityDialog ?? new CommitIdentityDialogAdapter();
```

2. 명령을 만든다(`MigrateEncodingCommand` 옆):

```csharp
SetCommitIdentityCommand = new RelayCommand(SetCommitIdentity, () => !IsBusy);
```

```csharp
/// <summary>배너와 커밋 차단이 함께 쓴다. 신원을 물어 저장소 로컬 config에 쓴다.</summary>
public ICommand SetCommitIdentityCommand { get; }

/// <returns>신원이 설정되었으면 true. 취소했거나 입력이 쓸 수 없으면 false.</returns>
private bool PromptForCommitIdentity()
{
    if (!HasContext) return false;

    var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
    if (mapping == null) return false;

    var (suggestedName, suggestedEmail) = WindowsAccountIdentity.Suggest();
    var input = _identityDialog.Prompt(suggestedName, suggestedEmail);

    // 취소는 오류가 아니다.
    if (input == null) return false;

    var reason = GitIdentity.Validate(input.Name, input.Email);
    if (reason != null)
    {
        // 대화상자가 이미 걸렀어야 하는 값이다. 여기까지 왔다면 대역이거나 버그이므로
        // 조용히 넘기지 않는다 - 넘기면 신원 없이 커밋이 이어진다.
        _notifier.ShowError("DBVC 작성자 설정", reason);
        return false;
    }

    try
    {
        GitIdentity.Write(mapping.GitPath, input.Name, input.Email);
    }
    catch (Exception ex)
    {
        _notifier.ShowError("DBVC 작성자 설정 실패", ex.Message);
        return false;
    }

    // 설정이 끝났는데 배너가 남으면 사용자가 또 누른다. 인코딩 전환이 끝난 뒤 배너를
    // 내리는 것과 같은 이유다.
    IsCommitIdentityMissing = GitIdentity.Detect(mapping.GitPath) == GitIdentityState.Missing;
    return !IsCommitIdentityMissing;
}

private void SetCommitIdentity() => PromptForCommitIdentity();
```

3. `RaiseCanExecuteChanged`를 모아 부르는 자리(`MigrateEncodingCommand`가 있는 줄 근처)에 `(SetCommitIdentityCommand as RelayCommand)?.RaiseCanExecuteChanged();`를 더한다.

`using DBVC.Vsix.Services;`는 이미 있다. `IsCommitIdentityMissing`의 setter가 `private set`이므로 같은 클래스 안에서 쓰는 이 코드는 그대로 동작한다.

- [ ] **Step 6: `DbvcServices` 배선**

`CreateViewChangesViewModel`의 `new ViewChangesViewModel(...)` 인자에 더한다:

```csharp
identityDialog: new CommitIdentityDialogAdapter(),
```

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "SetCommitIdentityCommand"`
Expected: PASS (3개)

Run: `dotnet build DBVC.slnx`
Expected: 성공

- [ ] **Step 8: 커밋**

```bash
git add src/DBVC.Vsix tests/DBVC.Vsix.Tests
git commit -m "feat(vsix): 커밋 작성자를 입력받아 저장소에 저장한다"
```

---

### Task 6: 커밋·Pull 재진입

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`Commit`, `CommitOutcome`, `Pull`)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `PromptForCommitIdentity()` (Task 5), `GitIdentity.Detect` (Task 1)
- Produces: 없음 (내부 동작)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
[Test]
public void Commit_PromptsForIdentityAndCommits_WhenTheUserFillsItIn()
{
    var repoPath = NewMappedGitRepo(withIdentity: false);
    var vm = NewViewModelWithChanges(Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"));
    vm.CommitMessage = "메시지";
    vm.Changes[0].IsSelected = true;
    _identityDialog.Result = new CommitIdentityInput { Name = "홍길동", Email = "gildong@corp.co.kr" };

    vm.CommitCommand.Execute(null);

    Assert.Multiple(() =>
    {
        Assert.That(_identityDialog.PromptCount, Is.EqualTo(1));
        _git.Verify(g => g.CommitChanges(Server, Database, "메시지", It.IsAny<IEnumerable<string>>()), Times.Once);
    });
}

[Test]
public void Commit_DoesNotCommit_WhenTheUserCancelsTheIdentityPrompt()
{
    NewMappedGitRepo(withIdentity: false);
    var vm = NewViewModelWithChanges(Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"));
    vm.CommitMessage = "메시지";
    vm.Changes[0].IsSelected = true;
    _identityDialog.Result = null;

    vm.CommitCommand.Execute(null);

    _git.Verify(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()), Times.Never);
}

[Test]
public void Commit_PromptsOnlyOnce_WhenWritingTheIdentityDoesNotTake()
{
    // 다이얼로그가 값을 돌려줬는데도 신원이 남지 않는 경우(권한 등)를 흉내 낸다.
    // 가드가 없으면 대화상자가 무한히 뜬다.
    NewMappedGitRepo(withIdentity: false);
    var vm = NewViewModelWithChanges(Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"));
    vm.CommitMessage = "메시지";
    vm.Changes[0].IsSelected = true;
    _identityDialog.Result = new CommitIdentityInput { Name = "홍길동", Email = "gildong" }; // 검증에 걸린다

    vm.CommitCommand.Execute(null);

    Assert.That(_identityDialog.PromptCount, Is.EqualTo(1));
}

[Test]
public void Pull_PromptsForIdentityBeforeStarting_WhenTheRepositoryHasNoAuthor()
{
    // Pull은 병합 커밋을 만든다. 신원 없이 시작하면 Core가 던지고, 사용자는 무엇을
    // 해야 하는지 알 수 없다.
    NewMappedGitRepo(withIdentity: false);
    var vm = NewConnectedViewModel();
    _identityDialog.Result = null;

    vm.PullCommand.Execute(null);

    Assert.Multiple(() =>
    {
        Assert.That(_identityDialog.PromptCount, Is.EqualTo(1));
        _git.Verify(g => g.PullChanges(Server, Database), Times.Never);
    });
}
```

`NewMappedGitRepo`는 반드시 `NewViewModelWithChanges`보다 **먼저** 부른다. 그쪽이 `NewConnectedViewModel`을 거치며 접속 판정을 끝내므로, 뒤에 부르면 매핑이 바뀌기 전의 판정이 그대로 남는다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "PromptsForIdentity or DoesNotCommit_WhenTheUserCancels or PromptsOnlyOnce"`
Expected: FAIL — 신원을 보지 않고 그대로 커밋한다.

- [ ] **Step 3: 커밋 경로에 재진입을 넣는다**

1. `CommitOutcome`에 필드를 더한다:

```csharp
/// <summary>참이면 커밋하지 않았고 작성자 신원을 받아야 한다는 뜻이다.</summary>
public bool NeedsIdentity { get; set; }
```

2. `Commit`의 시그니처를 바꾼다:

```csharp
private void Commit() => Commit(coAuthorConfirmed: false, identityPrompted: false);

/// <param name="identityPrompted">
/// 이미 신원을 물었는지. 입력을 받았는데도 신원이 남지 않으면(권한 등) 판정이 다시
/// "없음"으로 돌아와 대화상자가 무한히 뜬다. 두 번째는 사유를 알리고 멈춘다.
/// </param>
private void Commit(bool coAuthorConfirmed, bool identityPrompted)
```

기존 `Commit(coAuthorConfirmed: true)` 재귀 호출은 `Commit(coAuthorConfirmed: true, identityPrompted: identityPrompted)`로 고친다.

3. 백그라운드 델리게이트의 **맨 앞**(CoAuthor 검사보다 먼저) 에 넣는다:

```csharp
// CoAuthor 확인보다 먼저 본다. 순서가 뒤집히면 확인 대화상자와 신원 대화상자가
// 연달아 뜬다. 파일을 여는 일이라 UI 스레드가 아닌 여기서 한다.
var mappingForIdentity = _configManager.TryGetMapping(server, database);
if (mappingForIdentity != null
    && GitIdentity.Detect(mappingForIdentity.GitPath) == GitIdentityState.Missing)
{
    return new CommitOutcome { NeedsIdentity = true };
}
```

4. UI 콜백의 `if (outcome.CoAuthors != null)` **앞**에 넣는다:

```csharp
if (outcome.NeedsIdentity)
{
    // 차단이 막다른 길이 되지 않게 한다. 여기서 받고 같은 경로를 다시 탄다.
    if (!identityPrompted && PromptForCommitIdentity())
    {
        Commit(coAuthorConfirmed, identityPrompted: true);
        return;
    }

    if (identityPrompted)
    {
        _notifier.ShowError("DBVC 커밋 실패", GitIdentityMissingException.UserMessage);
    }

    return;
}
```

`using DBVC.Core;`가 이미 있으므로 `GitIdentityMissingException`은 그대로 쓸 수 있다.

- [ ] **Step 4: Pull에도 사전 검사를 넣는다**

`Pull()`의 `var pending = _gitManager.GetChangedFiles(mapping.GitPath);` **앞**에 넣는다:

```csharp
// Pull은 병합 커밋을 만들 수 있다. 시작한 뒤 Core가 던지면 사용자는 네트워크가 도는
// 동안 기다린 끝에 사유만 본다. 시작 전에 받는다.
if (GitIdentity.Detect(mapping.GitPath) == GitIdentityState.Missing
    && !PromptForCommitIdentity())
{
    return;
}
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS 전부

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS 전부

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 신원이 없으면 커밋 자리에서 받고 이어서 진행한다"
```

---

### Task 7: 문서와 버전

**Files:**
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest` (`Version="0.5.16"` → `0.5.17`)
- Modify: `README.md`, `docs/setup-checklist.md`, `docs/team-rollout-backlog.md`

- [ ] **Step 1: 버전을 올린다**

`source.extension.vsixmanifest`의 `<Identity ... Version="0.5.16" ...>`을 `0.5.17`로 바꾼다. 다른 곳에 숫자를 적지 않는다 — `DbvcVersion`이 매니페스트를 읽고, `DbvcVersionTests.Current_MatchesVsixManifest`가 그 배선을 지킨다.

- [ ] **Step 2: `README.md`**

Git 사용을 설명하는 절에 더한다:

```markdown
### 커밋 작성자

첫 커밋 전에 작성자를 지정해야 합니다. 지정되지 않은 저장소에서는 커밋과 Pull이 막히고
DBVC 창에 배너가 뜹니다. **작성자 설정...**을 눌러 이름과 메일 주소를 입력하세요.

- 값은 그 저장소의 `.git/config`에만 저장됩니다. 이 PC의 다른 저장소에는 영향이 없습니다
- 이미 `git config --global user.name`을 설정해 둔 경우에는 배너가 뜨지 않습니다
- GitLab 계정과 같은 메일 주소를 쓰면 커밋이 계정에 연결됩니다
```

- [ ] **Step 3: `docs/setup-checklist.md`**

0.5.17 절을 만들고 스펙 4.1의 수동 확인 5개를 체크박스로 옮긴다:

```markdown
## 0.5.17 — 커밋 작성자 신원

- [ ] 전역 `git config`가 없는 계정에서 저장소를 연결하면 작성자 배너가 뜬다
- [ ] **작성자 설정...**을 눌러 입력하면 배너가 사라진다
- [ ] 배너를 무시하고 커밋을 누르면 막히고 입력 창이 뜬다. 입력하면 커밋이 이어진다
- [ ] 그 커밋의 작성자가 `git log`와 GitLab에서 본인으로 보인다
- [ ] 전역 `git config`가 이미 있는 계정에서는 배너가 뜨지 않는다
- [ ] 설정 후 저장소의 `.git/config`에만 값이 들어갔고 `git config --global`은 그대로다
```

- [ ] **Step 4: `docs/team-rollout-backlog.md`**

"배포 전에 막을 것"의 1번을 지우고, 문서 머리말의 완료 목록에 더한다. 2번(`.vsix` 배포 채널)이 그 절의 유일한 항목으로 남으므로 번호를 다시 매긴다. 이후 항목의 번호도 함께 당긴다 — 6번이 "5번과 한 몸"이라고 참조하고 있으므로 본문의 상호 참조를 함께 고친다.

머리말은 이렇게 바꾼다:

```markdown
DBVC를 팀에 공유하기 위해 남은 일. 2026-09-04 기준이며, P0 둘(UTF-8 전환, MarkProcessed 실패
알림 + GRANT)은 0.5.16으로, 커밋 작성자 신원은 0.5.17로 끝났다.
```

- [ ] **Step 5: 확인**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "DbvcVersion"`
Expected: PASS — `Current_MatchesVsixManifest`가 새 번호로 통과한다.

Run: `dotnet build DBVC.slnx`
Expected: 성공

- [ ] **Step 6: 커밋**

```bash
git add README.md docs src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: 커밋 작성자 설정 절차를 적고 0.5.17로 올린다"
```

---

## 마지막 확인 — 사람이 SSMS에서 한다

CI는 WPF 렌더링, VS 패키지 로딩, `secur32` 추정값, 실제 GitLab 연동을 검증하지 못한다.
`docs/setup-checklist.md`의 0.5.17 절을 SSMS 21에서 직접 눌러 보기 전에는 완료가 아니다.

`.vsix` 산출물은 빌드 성공만으로 확인하지 않는다:

```powershell
dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release
dir src\DBVC.Vsix\bin\Release\net48\*.vsix
```
