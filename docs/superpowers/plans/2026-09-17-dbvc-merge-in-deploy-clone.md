# 배포·감사 클론 병합 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 테스트·운영 배포 때 GitLab에 가지 않고, 배포·감사 클론에서 미병합 브랜치를 골라 병합·Push하고 곧바로 차이 검사로 이어간다.

**Architecture:** `IGitManager`에 `GetUnmergedBranches`·`PreviewMerge`·`MergeAndPush`를 더한다. 미리보기는 `ObjectDatabase.MergeCommits`로 작업 트리 없이 계산하고, 운영(`master`) 목적지에서는 순수 함수 `PromotionLeakDetector`가 줄 단위 겹침으로 경고 A를 만든다. `MergeAndPush`는 병합 커밋 하나만 올리고, Push가 실패하면 로컬을 시작 전으로 되돌린다. 화면은 `DeploymentViewModel`에 병합 영역을 붙인다.

**Tech Stack:** C# / .NET Standard 2.0 + .NET Framework 4.8, LibGit2Sharp 0.32.0, WPF(MVVM), NUnit, Moq

**Spec:** [`docs/superpowers/specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md`](../specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md)

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip, 컬럼명 포함. libgit2/서버의 영문 원문은 인용할 때만 그대로 싣는다.
- **주석은 "왜"만 적는다.** 한국어 평서문, 함정과 근거를 남기는 기존 문체.
- **테스트 이름은 영어 `Method_Result_WhenCondition`.**
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프.** 예: `feat(core): 미병합 브랜치 목록을 더한다`. 끝에 `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **TDD:** 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `SqlManagementObjects 171.30.0` 고정. 이 계획은 새 패키지를 더하지 않는다.
- **테스트 프로젝트에 MDS/SMO를 직접 PackageReference 하지 않는다.**
- **`MappingPolicy.IsAllowed`와 `GetOperationName`의 `default`는 예외를 던진다.** 새 `DbvcOperation`을 더하면 두 표를 반드시 고친다.
- **환경 브랜치 이름은 `develop`, `master` 두 개이며 대소문자를 구분한다**(스펙 2.3).
- **`Push` 정책은 바꾸지 않는다** — 배포·감사 클론에서 계속 금지다(스펙 2.4).
- **병합은 항상 병합 커밋을 만든다**(`FastForwardStrategy.NoFastForward`, 스펙 2.6).
- **병합 커밋 메시지:** `{원본} 브랜치를 {목적지}에 병합`.
- 빌드·테스트 명령:
  - `dotnet build DBVC.slnx`
  - `dotnet test tests/DBVC.Core.Tests -f net10.0`
  - `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
  - 단일 테스트: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~<이름>"`
  - 마지막 태스크에서 `-f net48`도 한 번 돌린다(Windows 전용).

## 파일 구조

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/EnvironmentBranches.cs` (신규) | `develop`/`master` 판정 — 한 곳에서만 |
| `src/DBVC.Core/PromotionLeakDetector.cs` (신규) | 경고 A 판정. 순수 함수 |
| `src/DBVC.Core/Models/UnmergedBranch.cs` (신규) | 미병합 브랜치 한 줄 |
| `src/DBVC.Core/Models/MergePreview.cs` (신규) | 미리보기 결과 + `PromotionLeak` |
| `src/DBVC.Core/Models/MergeOutcome.cs` (신규) | 병합 결과 + `MergeOutcomeKind` |
| `src/DBVC.Core/MappingPolicy.cs` | `DbvcOperation.Merge` + 표 |
| `src/DBVC.Core/Abstractions.cs` | `IGitManager` 표면 셋 |
| `src/DBVC.Core/GitManager.cs` | 세 메서드, Fetch·Push 공용 도우미 추출 |
| `src/DBVC.Vsix/ViewModels/UnmergedBranchItemViewModel.cs` (신규) | 목록 한 줄의 한국어 표시 |
| `src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs` | 병합 영역 상태·명령 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 병합 영역 |
| `tests/DBVC.Core.Tests/EnvironmentBranchesTests.cs` (신규) | |
| `tests/DBVC.Core.Tests/PromotionLeakDetectorTests.cs` (신규) | |
| `tests/DBVC.Core.Tests/MappingPolicyTests.cs` | |
| `tests/DBVC.Core.Tests/FakeGitManagerBase.cs` | 새 인터페이스 메서드 |
| `tests/DBVC.Core.Tests/GitManagerMergeTests.cs` (신규) | 병합 경로 전용 픽스처 — `GitManagerTests.cs`는 이미 2800줄이다 |
| `tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelMergeTests.cs` (신규) | |
| `README.md`, `docs/*`, `source.extension.vsixmanifest` | 스펙 6절 |

---

### Task 1: 정책·환경 브랜치·결과 모델

순수한 조각을 먼저 세운다. 나머지가 전부 이 이름에 기댄다.

**Files:**
- Create: `src/DBVC.Core/EnvironmentBranches.cs`
- Create: `src/DBVC.Core/Models/UnmergedBranch.cs`
- Create: `src/DBVC.Core/Models/MergePreview.cs`
- Create: `src/DBVC.Core/Models/MergeOutcome.cs`
- Modify: `src/DBVC.Core/MappingPolicy.cs`
- Test: `tests/DBVC.Core.Tests/MappingPolicyTests.cs`
- Test: `tests/DBVC.Core.Tests/EnvironmentBranchesTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `DbvcOperation.Merge`; `MappingPolicy.IsAllowed(mode, Merge) == (mode != MappingMode.Write)`; 표시 이름 `"병합"`
  - `static class EnvironmentBranches { const string Develop = "develop"; const string Master = "master"; static bool IsEnvironmentBranch(string? name); }`
  - `class UnmergedBranch { string Name; string LastCommitAuthor; DateTimeOffset LastCommitTime; int CommitCount; bool? IsInDevelop; }` (속성)
  - `class PromotionLeak { string Path; string BranchName; IReadOnlyList<string> Lines; }`
  - `class MergePreview { IReadOnlyList<string> ChangedPaths; IReadOnlyList<string> ConflictPaths; IReadOnlyList<PromotionLeak> Leaks; bool AlreadyMerged; }`
  - `enum MergeOutcomeKind { Merged, AlreadyMerged, Conflicts, PushRejected, LocalAhead, Refused }`
  - `class MergeOutcome { MergeOutcomeKind Kind; string? Message; IReadOnlyList<string> Paths; }`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/MappingPolicyTests.cs`의 `IsAllowed_Throws_WhenOperationIsUnknown` 위에 더한다.

```csharp
        [TestCase(MappingMode.Write, false)]
        [TestCase(MappingMode.Deploy, true)]
        [TestCase(MappingMode.Audit, true)]
        public void IsAllowed_AllowsMerge_OnlyOnPinnedClones(MappingMode mode, bool expected)
        {
            // 목적지가 고정 브랜치여야 병합에 뜻이 있다. 개발 클론은 브랜치가 자유라 목적지가 흔들린다.
            Assert.That(MappingPolicy.IsAllowed(mode, DbvcOperation.Merge), Is.EqualTo(expected));
        }

        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void IsAllowed_StillDeniesPush_WhenMergeIsAllowed(MappingMode mode)
        {
            // 병합은 자기가 만든 커밋 하나만 올린다. 일반 Push를 열면 그 전의 로컬 커밋이 섞여 나간다.
            Assert.That(MappingPolicy.IsAllowed(mode, DbvcOperation.Push), Is.False);
        }

        [Test]
        public void BuildDeniedMessage_NamesMergeInKorean()
        {
            var message = MappingPolicy.BuildDeniedMessage(MappingMode.Write, DbvcOperation.Merge);

            Assert.That(message, Does.Contain("병합"));
        }
```

`tests/DBVC.Core.Tests/EnvironmentBranchesTests.cs`를 만든다.

```csharp
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// master 목록에 develop이 뜨면 develop을 통째로 운영에 병합하는 것이 버튼 한 번이 된다.
    /// </summary>
    [TestFixture]
    public class EnvironmentBranchesTests
    {
        [TestCase("develop", true)]
        [TestCase("master", true)]
        [TestCase("Develop", false)]
        [TestCase("PROJ-123", false)]
        [TestCase("feature/develop", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsEnvironmentBranch_MatchesExactNames_WhenNameIsGiven(string? name, bool expected)
        {
            Assert.That(EnvironmentBranches.IsEnvironmentBranch(name), Is.EqualTo(expected));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicyTests|FullyQualifiedName~EnvironmentBranchesTests"`
Expected: 빌드 실패 — `DbvcOperation.Merge`, `EnvironmentBranches` 없음

- [ ] **Step 3: 구현한다**

`src/DBVC.Core/MappingPolicy.cs` — 열거형에서 `SwitchBranch` 아래에 더한다.

```csharp
        /// <summary>고정 브랜치에 원격 브랜치를 병합하고 그 병합 커밋만 올린다.</summary>
        Merge,
```

`IsAllowed`의 `case DbvcOperation.Compare:` 위에 더한다.

```csharp
                case DbvcOperation.Merge:
                    // 목적지가 고정 브랜치여야 뜻이 있다 - 개발 클론은 브랜치가 자유라 목적지가 흔들리고,
                    // 병합 직후 이어야 할 Compare가 write에서 금지다. DB에는 쓰지 않으므로 audit도 허용한다.
                    // Push는 여기서 열지 않는다. 병합이 자기가 만든 커밋 하나만 올리도록 GitManager가 지킨다.
                    return mode != MappingMode.Write;
```

`GetOperationName`에 더한다.

```csharp
                case DbvcOperation.Merge: return "병합";
```

`src/DBVC.Core/EnvironmentBranches.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 환경 브랜치 이름. 병합 원본이 될 수 없다.
    ///
    /// 티켓 이름 규칙은 조직이 바꿀 수 있어 코드에 넣지 않았지만(브랜치 조작 설계 3.5), 이 둘은
    /// 워크플로 설계 1.1이 전제로 삼은 환경 브랜치라 성격이 다르다. 대소문자는 git과 같이 구분한다.
    /// </summary>
    public static class EnvironmentBranches
    {
        public const string Develop = "develop";
        public const string Master = "master";

        public static bool IsEnvironmentBranch(string? name)
        {
            return string.Equals(name, Develop, StringComparison.Ordinal)
                || string.Equals(name, Master, StringComparison.Ordinal);
        }
    }
}
```

`src/DBVC.Core/Models/UnmergedBranch.cs`:

```csharp
using System;

namespace DBVC.Core.Models
{
    /// <summary>고정 브랜치에 아직 병합되지 않은 원격 브랜치 한 줄.</summary>
    public class UnmergedBranch
    {
        /// <summary>remote 접두사를 뗀 이름(origin/PROJ-1 → PROJ-1).</summary>
        public string Name { get; set; } = string.Empty;

        public string LastCommitAuthor { get; set; } = string.Empty;

        public DateTimeOffset LastCommitTime { get; set; }

        /// <summary>목적지에 없는 커밋 수.</summary>
        public int CommitCount { get; set; }

        /// <summary>
        /// 끝 커밋이 원격 develop에 들어 있는가. 목적지가 master일 때만 값이 있다.
        /// 막는 데 쓰지 않는다 - hotfix는 테스트를 건너뛰는 것이 정상이다.
        /// </summary>
        public bool? IsInDevelop { get; set; }
    }
}
```

`src/DBVC.Core/Models/MergePreview.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>작업 트리를 건드리지 않고 계산한 병합 결과.</summary>
    public class MergePreview
    {
        /// <summary>목적지 끝 → 병합 결과 사이에 바뀌는 파일. '/' 구분.</summary>
        public IReadOnlyList<string> ChangedPaths { get; set; } = Array.Empty<string>();

        /// <summary>비어 있지 않으면 병합할 수 없다.</summary>
        public IReadOnlyList<string> ConflictPaths { get; set; } = Array.Empty<string>();

        /// <summary>목적지가 master일 때만 채워진다.</summary>
        public IReadOnlyList<PromotionLeak> Leaks { get; set; } = Array.Empty<PromotionLeak>();

        public bool AlreadyMerged { get; set; }
    }

    /// <summary>
    /// 운영으로 가는 줄이 아직 운영에 병합되지 않은 다른 브랜치에도 있다. 방향은 모른다 -
    /// 그쪽에서 딸려 왔을 수도, 이쪽 변경이 그쪽에 딸려 갔을 수도 있다.
    /// </summary>
    public class PromotionLeak
    {
        public string Path { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;

        /// <summary>겹친 줄(정규화된 형태), 정렬됨.</summary>
        public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();
    }
}
```

`src/DBVC.Core/Models/MergeOutcome.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    public enum MergeOutcomeKind
    {
        /// <summary>병합 커밋을 만들고 원격에 올렸다.</summary>
        Merged,

        /// <summary>원본 끝이 이미 목적지에 들어 있다.</summary>
        AlreadyMerged,

        /// <summary>충돌로 병합하지 않았다. 로컬은 시작 전 상태다.</summary>
        Conflicts,

        /// <summary>원격이 거부했다. 로컬은 병합 전으로 되돌렸다.</summary>
        PushRejected,

        /// <summary>로컬에 원격에 없는 커밋이 있어 시작하지 않았다.</summary>
        LocalAhead,

        /// <summary>입구 검사에 걸렸다. Message에 사유가 있다.</summary>
        Refused
    }

    /// <summary>
    /// 예상할 수 있는 결과는 값으로 돌려준다. 통신·인증 실패만 예외다 - PushChanges의
    /// NoUpstream과 같은 기준이다.
    /// </summary>
    public class MergeOutcome
    {
        public MergeOutcomeKind Kind { get; set; }

        /// <summary>한국어. 화면이 그대로 띄운다. Merged이면 null.</summary>
        public string? Message { get; set; }

        /// <summary>Conflicts면 충돌 파일, Merged면 바뀐 파일. 그 외에는 빈 목록.</summary>
        public IReadOnlyList<string> Paths { get; set; } = Array.Empty<string>();

        public static MergeOutcome Of(MergeOutcomeKind kind, string? message = null, IReadOnlyList<string>? paths = null) =>
            new MergeOutcome { Kind = kind, Message = message, Paths = paths ?? Array.Empty<string>() };
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicyTests|FullyQualifiedName~EnvironmentBranchesTests"`
Expected: PASS

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/MappingPolicy.cs src/DBVC.Core/EnvironmentBranches.cs src/DBVC.Core/Models/UnmergedBranch.cs src/DBVC.Core/Models/MergePreview.cs src/DBVC.Core/Models/MergeOutcome.cs tests/DBVC.Core.Tests/MappingPolicyTests.cs tests/DBVC.Core.Tests/EnvironmentBranchesTests.cs
git commit -m "feat(core): 병합 동작을 정책 표와 결과 모델에 올린다"
```

---

### Task 2: `PromotionLeakDetector`

경고 A의 판정. Git을 모르는 순수 함수다.

**Files:**
- Create: `src/DBVC.Core/PromotionLeakDetector.cs`
- Test: `tests/DBVC.Core.Tests/PromotionLeakDetectorTests.cs`

**Interfaces:**
- Consumes: `PromotionLeak` (Task 1)
- Produces:
  - `static IReadOnlyList<PromotionLeak> PromotionLeakDetector.Detect(IReadOnlyDictionary<string, IReadOnlyCollection<string>> source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> others)` — `source`: 경로 → 추가된 줄, `others`: 브랜치 → 경로 → 추가된 줄. 결과는 경로, 브랜치 순(Ordinal) 정렬.
  - `static string? PromotionLeakDetector.Normalize(string line)` — 비교에서 뺄 줄이면 `null`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 공용 개발 DB에서 뜬 파일은 통짜 스냅샷이라 남의 변경이 섞인다. 운영 병합에서 그것을
    /// 알리는 유일한 자리다(스펙 3.4).
    /// </summary>
    [TestFixture]
    public class PromotionLeakDetectorTests
    {
        private const string Path = "dbo/StoredProcedures/usp_Order.sql";

        private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Lines(string path, params string[] lines) =>
            new Dictionary<string, IReadOnlyCollection<string>> { [path] = lines };

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> Branch(
            string branch, IReadOnlyDictionary<string, IReadOnlyCollection<string>> lines) =>
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> { [branch] = lines };

        [Test]
        public void Detect_ReportsLeak_WhenAddedLinesOverlapAnotherBranch()
        {
            var source = Lines(Path, "    DiscountRate DECIMAL(5,2)", "SELECT 1");
            var others = Branch("PROJ-120", Lines(Path, "DiscountRate DECIMAL(5,2)"));

            var leaks = PromotionLeakDetector.Detect(source, others);

            Assert.That(leaks, Has.Count.EqualTo(1));
            Assert.That(leaks[0].Path, Is.EqualTo(Path));
            Assert.That(leaks[0].BranchName, Is.EqualTo("PROJ-120"));
            Assert.That(leaks[0].Lines, Is.EqualTo(new[] { "DiscountRate DECIMAL(5,2)" }));
        }

        [Test]
        public void Detect_IgnoresTrivialLines_WhenOnlyKeywordsOverlap()
        {
            // 빼지 않으면 모든 프로시저가 모든 브랜치와 겹친다.
            var source = Lines(Path, "BEGIN", "end", "GO", "AS", "(", ")", ",", ";", "   ");
            var others = Branch("PROJ-120", Lines(Path, "BEGIN", "END", "go", "as", "(", ")", ",", ";", ""));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }

        [Test]
        public void Detect_ReturnsEmpty_WhenNoOverlap()
        {
            var source = Lines(Path, "DiscountRate DECIMAL(5,2)");
            var others = Branch("PROJ-120", Lines(Path, "Memo NVARCHAR(50)"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }

        [Test]
        public void Detect_NormalizesWhitespace_WhenComparingLines()
        {
            var source = Lines(Path, "\tWHERE  o.Id =\t@id\r\n");
            var others = Branch("PROJ-120", Lines(Path, "WHERE o.Id = @id"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Has.Count.EqualTo(1));
        }

        [Test]
        public void Detect_IsCaseSensitive_WhenLinesDifferOnlyInCase()
        {
            // 식별자·문자열 리터럴의 대소문자는 서로 다른 변경일 수 있다. 키워드만 무시 목록에서 가린다.
            var source = Lines(Path, "SELECT Name FROM dbo.Users");
            var others = Branch("PROJ-120", Lines(Path, "select name from dbo.users"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }

        [Test]
        public void Detect_IgnoresOtherPaths_WhenSameLineIsInADifferentFile()
        {
            var source = Lines(Path, "DiscountRate DECIMAL(5,2)");
            var others = Branch("PROJ-120", Lines("dbo/Tables/Orders.sql", "DiscountRate DECIMAL(5,2)"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PromotionLeakDetectorTests"`
Expected: 빌드 실패 — `PromotionLeakDetector` 없음

- [ ] **Step 3: 구현한다**

`src/DBVC.Core/PromotionLeakDetector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 운영 병합에 딸려 가는 남의 변경을 줄 단위 겹침으로 찾는다(경고 A, 스펙 3.4).
    ///
    /// 원래 설계의 P@develop != P@master는 쓰지 않는다 - 테스트를 거친 브랜치는 develop에 자기
    /// 변경이 들어 있어 운영 병합마다 모든 객체에서 뜬다. 겹침은 방향을 말하지 못하므로 결과는
    /// 판정이 아니라 "사람이 확인할 것"이다.
    /// </summary>
    public static class PromotionLeakDetector
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        // 어느 객체에나 나오는 줄이다. 빼지 않으면 모든 프로시저가 모든 브랜치와 겹친다.
        private static readonly HashSet<string> Trivial = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GO", "BEGIN", "END", "AS", "(", ")", ",", ";"
        };

        public static IReadOnlyList<PromotionLeak> Detect(
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> source,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> others)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (others == null) throw new ArgumentNullException(nameof(others));

            var leaks = new List<PromotionLeak>();

            foreach (var path in source.Keys.OrderBy(p => p, StringComparer.Ordinal))
            {
                var sourceLines = NormalizeAll(source[path]);
                if (sourceLines.Count == 0) continue;

                foreach (var branch in others.Keys.OrderBy(b => b, StringComparer.Ordinal))
                {
                    if (!others[branch].TryGetValue(path, out var branchLines)) continue;

                    var overlap = NormalizeAll(branchLines);
                    overlap.IntersectWith(sourceLines);
                    if (overlap.Count == 0) continue;

                    leaks.Add(new PromotionLeak
                    {
                        Path = path,
                        BranchName = branch,
                        Lines = overlap.OrderBy(l => l, StringComparer.Ordinal).ToList()
                    });
                }
            }

            return leaks;
        }

        /// <summary>비교용 형태. 비교에서 뺄 줄이면 null이다.</summary>
        public static string? Normalize(string line)
        {
            if (line == null) return null;
            var collapsed = Whitespace.Replace(line.Trim(), " ");
            if (collapsed.Length == 0 || Trivial.Contains(collapsed)) return null;
            return collapsed;
        }

        private static HashSet<string> NormalizeAll(IEnumerable<string> lines)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var normalized = Normalize(line);
                if (normalized != null) set.Add(normalized);
            }
            return set;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PromotionLeakDetectorTests"`
Expected: PASS (6건)

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/PromotionLeakDetector.cs tests/DBVC.Core.Tests/PromotionLeakDetectorTests.cs
git commit -m "feat(core): 운영 병합에 딸려 가는 줄을 찾는 판정을 더한다"
```

---

### Task 3: `GetUnmergedBranches`와 Fetch 공용 도우미

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs` (`IGitManager`, `FetchRemoteStatus` 선언 아래)
- Modify: `src/DBVC.Core/GitManager.cs` (`FetchRemoteStatus` 부근, `GetBranches` 아래)
- Modify: `tests/DBVC.Core.Tests/FakeGitManagerBase.cs`
- Create: `tests/DBVC.Core.Tests/GitManagerMergeTests.cs`

**Interfaces:**
- Consumes: `UnmergedBranch`, `EnvironmentBranches` (Task 1)
- Produces:
  - `IReadOnlyList<UnmergedBranch> IGitManager.GetUnmergedBranches(string serverName, string databaseName)`
  - `GitManager` private 도우미 (Task 4·5가 쓴다):
    - `void FetchCurrentRemote(Repository repo, string repoPath, string operationName)` — `ValidateRemoteAndBuildGuidance` + `Commands.Fetch` + 예외 변환. `FetchRemoteStatus`도 이것을 쓰도록 바꾼다.
    - `static Commit? RemoteTip(Repository repo, string branchName)` — `repo.Branches[$"{repo.Head.RemoteName}/{branchName}"]?.Tip`
    - `(MappingConfig Mapping, string Target)? ResolvePinnedTarget(string serverName, string databaseName)` — 매핑과 고정 브랜치. 매핑이 없거나 `Branch`가 비면 null.
  - 테스트 픽스처 `GitManagerMergeTests`의 도우미 (Task 4·5가 쓴다):
    - `(string LocalPath, string OriginPath) NewPinnedClone(string target, MappingMode mode = MappingMode.Deploy)` — bare origin에 `master`·`develop`을 두고 `target`을 체크아웃한 clone
    - `string PushAuthorBranch(string originPath, string branch, string basedOn, string relativePath, string content)` — 작성자 clone에서 `basedOn`으로부터 브랜치를 따서 파일 하나를 커밋하고 origin에 올린다. 커밋 SHA 반환.
    - `GitManager NewPinnedGitManager(string localPath, string target, MappingMode mode)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerMergeTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 배포·감사 클론의 병합 경로. bare origin과 그것을 받은 고정 클론, 원격에 브랜치를 올리는
    /// 작성자 클론 셋으로 팀의 실제 배치를 흉내 낸다.
    /// </summary>
    [TestFixture]
    public class GitManagerMergeTests
    {
        private const string Server = "localhost";
        private const string Database = "testdb";
        private const string SqlPath = "dbo/StoredProcedures/usp_Order.sql";

        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void CleanUp()
        {
            foreach (var dir in _tempDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
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
            var path = Path.Combine(Path.GetTempPath(), "dbvc_merge_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(path);
            return path;
        }

        private static Signature Sig(string name = "Author") =>
            new Signature(name, name.ToLowerInvariant() + "@example.com", DateTimeOffset.Now);

        private static void WriteFile(string repoPath, string relativePath, string content)
        {
            var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        /// <summary>
        /// bare origin에 master·develop을 같은 첫 커밋에 두고 target을 받은 클론을 만든다.
        /// init.defaultBranch 전역 설정에 기대지 않도록 두 이름을 origin에 직접 만든다.
        /// </summary>
        private (string LocalPath, string OriginPath) NewPinnedClone(string target, MappingMode mode = MappingMode.Deploy)
        {
            var seed = NewTempDir();
            Repository.Init(seed);
            WriteFile(seed, SqlPath, "CREATE OR ALTER PROCEDURE dbo.usp_Order AS\nSELECT 1\n");
            using (var repo = new Repository(seed))
            {
                Commands.Stage(repo, "*");
                repo.Commit("initial", Sig(), Sig());
            }

            var origin = NewTempDir();
            Repository.Clone(seed, origin, new CloneOptions { IsBare = true });
            using (var repo = new Repository(origin))
            {
                var tip = repo.Head.Tip;
                if (repo.Branches[EnvironmentBranches.Master] == null) repo.CreateBranch(EnvironmentBranches.Master, tip);
                if (repo.Branches[EnvironmentBranches.Develop] == null) repo.CreateBranch(EnvironmentBranches.Develop, tip);
            }

            var local = NewTempDir();
            Repository.Clone(origin, local, new CloneOptions { BranchName = target });
            GitIdentity.Write(local, "Deployer", "deployer@example.com");
            return (local, origin);
        }

        private string PushAuthorBranch(string originPath, string branch, string basedOn, string relativePath, string content)
        {
            var author = NewTempDir();
            Repository.Clone(originPath, author, new CloneOptions { BranchName = basedOn });
            using var repo = new Repository(author);
            var created = repo.Branches[branch] ?? repo.CreateBranch(branch);
            Commands.Checkout(repo, created);
            WriteFile(author, relativePath, content);
            Commands.Stage(repo, "*");
            var commit = repo.Commit(branch + " 변경", Sig(), Sig());
            repo.Network.Push(repo.Network.Remotes["origin"], $"refs/heads/{branch}:refs/heads/{branch}");
            return commit.Sha;
        }

        private GitManager NewPinnedGitManager(string localPath, string target, MappingMode mode)
        {
            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(new MappingConfig
            {
                ServerName = Server, DatabaseName = Database, GitPath = localPath, Branch = target, Mode = mode
            });
            return new GitManager(config);
        }

        // ---------- GetUnmergedBranches ----------

        [Test]
        public void GetUnmergedBranches_ExcludesMergedAndEnvironmentBranches()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, "CREATE OR ALTER PROCEDURE dbo.usp_Order AS\nSELECT 2\n");
            // 끝 커밋이 master 첫 커밋 그대로인 브랜치 - develop에 이미 들어 있다.
            using (var repo = new Repository(origin)) repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Master].Tip);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var names = git.GetUnmergedBranches(Server, Database).Select(b => b.Name).ToList();

            Assert.That(names, Is.EqualTo(new[] { "PROJ-1" }));
        }

        [Test]
        public void GetUnmergedBranches_FetchesFirst_WhenBranchWasPushedAfterClone()
        {
            // 낡은 목록을 최신인 척 보여 주지 않는다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            PushAuthorBranch(origin, "PROJ-2", EnvironmentBranches.Master, SqlPath, "x\n");

            var branch = git.GetUnmergedBranches(Server, Database).Single();

            Assert.That(branch.Name, Is.EqualTo("PROJ-2"));
            Assert.That(branch.CommitCount, Is.EqualTo(1));
            Assert.That(branch.LastCommitAuthor, Is.EqualTo("Author"));
            Assert.That(branch.IsInDevelop, Is.Null, "목적지가 develop이면 테스트 반영 열이 없습니다");
        }

        [Test]
        public void GetUnmergedBranches_MarksIsInDevelop_WhenTargetIsMaster()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            var tested = PushAuthorBranch(origin, "PROJ-3", EnvironmentBranches.Master, SqlPath, "tested\n");
            PushAuthorBranch(origin, "PROJ-4", EnvironmentBranches.Master, "dbo/Views/v_A.sql", "untested\n");
            using (var repo = new Repository(origin))
            {
                // develop이 PROJ-3을 담은 것처럼 ref를 옮긴다. 병합 커밋이 없어도 조상 판정은 같다.
                repo.Refs.UpdateTarget("refs/heads/develop", tested);
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Master, MappingMode.Audit);

            var branches = git.GetUnmergedBranches(Server, Database).ToDictionary(b => b.Name);

            Assert.That(branches["PROJ-3"].IsInDevelop, Is.True);
            Assert.That(branches["PROJ-4"].IsInDevelop, Is.False);
            Assert.That(branches.ContainsKey(EnvironmentBranches.Develop), Is.False);
        }

        [Test]
        public void GetUnmergedBranches_ReturnsEmpty_WhenMappingIsNotPinned()
        {
            var (local, _) = NewPinnedClone(EnvironmentBranches.Develop);
            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(Server, Database, local);

            Assert.That(new GitManager(config).GetUnmergedBranches(Server, Database), Is.Empty);
        }
    }
}
```

`tests/DBVC.Core.Tests/FakeGitManagerBase.cs`의 `FetchRemoteStatus` 아래에 더한다.

```csharp
        public virtual IReadOnlyList<UnmergedBranch> GetUnmergedBranches(string serverName, string databaseName) => throw new NotSupportedException();
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerMergeTests"`
Expected: 빌드 실패 — `IGitManager.GetUnmergedBranches` 없음

- [ ] **Step 3: 인터페이스를 더한다**

`src/DBVC.Core/Abstractions.cs`의 `RemoteStatus FetchRemoteStatus(...)` 아래:

```csharp
        /// <summary>
        /// 원격을 받은 뒤, 매핑의 고정 브랜치에 아직 병합되지 않은 원격 브랜치를 마지막 커밋 시각
        /// 내림차순으로 낸다. develop/master는 빠진다. 매핑이 없거나 고정 브랜치가 없으면 빈 목록이다.
        /// 통신 실패는 FetchRemoteStatus와 같은 예외로 전파된다.
        /// </summary>
        IReadOnlyList<UnmergedBranch> GetUnmergedBranches(string serverName, string databaseName);
```

- [ ] **Step 4: Fetch 도우미를 뽑아 `FetchRemoteStatus`가 쓰게 한다**

`src/DBVC.Core/GitManager.cs`의 `FetchRemoteStatus` 몸체에서 `var guidance = ...`부터 `catch` 블록 끝까지를 아래 도우미 호출로 바꾼다.

```csharp
            using var repo = new Repository(repoPath);

            FetchCurrentRemote(repo, repoPath, "원격 확인");

            var details = repo.Head.TrackingDetails;
            return new RemoteStatus(details.AheadBy ?? 0, details.BehindBy ?? 0);
```

도우미는 `FetchRemoteStatus` 바로 아래에 둔다. 몸체는 옮겨 온 코드 그대로다.

```csharp
        /// <summary>
        /// 현재 브랜치의 원격을 받는다. 원격 확인·미병합 목록·병합이 글자 그대로 같은 검사와 예외
        /// 변환을 쓰므로 한 곳에 둔다 - 복제하면 한쪽 문구만 고쳐진다.
        /// </summary>
        private static void FetchCurrentRemote(Repository repo, string repoPath, string operationName)
        {
            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, operationName);
            var remoteName = repo.Head.RemoteName;

            try
            {
                var fetchOptions = new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) => ResolveCredentials(types, out _)
                };

                // 빈 refspec은 "원격에 설정된 기본 refspec을 쓰라"는 뜻이다.
                Commands.Fetch(repo, remoteName, Array.Empty<string>(), fetchOptions, null);
            }
            // 안내할 것이 있을 때만 가로챈다. 모든 예외를 감싸면 코딩 실수까지 "원격과 통신하지
            // 못했다"로 둔갑해서 원인을 찾을 수 없게 된다.
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }
        }
```

`ResolveCredentials`와 `BuildPushOptions`는 `internal static`이라 static 도우미에서 그대로 부를 수 있다.

- [ ] **Step 5: 기존 원격 확인 테스트가 그대로인지 본다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~FetchRemoteStatus"`
Expected: PASS (동작 변화 없음)

- [ ] **Step 6: `GetUnmergedBranches`를 구현한다**

`GetBranches` 아래에 둔다.

```csharp
        /// <summary>
        /// 고정 브랜치에 아직 병합되지 않은 원격 브랜치. 병합 요청의 표지를 따로 두지 않는다 -
        /// 준비됐는지는 지라 티켓 상태로 사람이 판단한다(스펙 2.2).
        /// </summary>
        public IReadOnlyList<UnmergedBranch> GetUnmergedBranches(string serverName, string databaseName)
        {
            var pinned = ResolvePinnedTarget(serverName, databaseName);
            if (pinned == null) return Array.Empty<UnmergedBranch>();

            var repoPath = pinned.Value.Mapping.GitPath;
            var target = pinned.Value.Target;

            using var repo = new Repository(repoPath);
            FetchCurrentRemote(repo, repoPath, "병합할 브랜치 확인");

            var targetTip = RemoteTip(repo, target) ?? repo.Head.Tip;
            if (targetTip == null) return Array.Empty<UnmergedBranch>();

            // develop 반영 여부는 운영 목적지에서만 뜻이 있다. 테스트 목적지에서 채우면 열이
            // 늘 "해당 없음"인 채로 떠 읽는 사람을 헷갈리게 한다.
            var developTip = target == EnvironmentBranches.Master ? RemoteTip(repo, EnvironmentBranches.Develop) : null;
            var remotePrefix = repo.Head.RemoteName + "/";

            var result = new List<UnmergedBranch>();
            foreach (var branch in repo.Branches.Where(b => b.IsRemote && b.FriendlyName.StartsWith(remotePrefix, StringComparison.Ordinal)))
            {
                var name = branch.FriendlyName.Substring(remotePrefix.Length);
                if (name == "HEAD" || EnvironmentBranches.IsEnvironmentBranch(name)) continue;

                var tip = branch.Tip;
                if (tip == null || IsAncestor(repo, tip, targetTip)) continue;

                var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(tip, targetTip);
                result.Add(new UnmergedBranch
                {
                    Name = name,
                    LastCommitAuthor = tip.Author.Name,
                    LastCommitTime = tip.Author.When,
                    CommitCount = divergence.AheadBy ?? 0,
                    IsInDevelop = target == EnvironmentBranches.Master
                        ? developTip != null && IsAncestor(repo, tip, developTip)
                        : (bool?)null
                });
            }

            return result.OrderByDescending(b => b.LastCommitTime).ToList();
        }

        private static bool IsAncestor(Repository repo, Commit candidate, Commit descendant)
        {
            if (candidate.Sha == descendant.Sha) return true;
            return repo.ObjectDatabase.FindMergeBase(candidate, descendant)?.Sha == candidate.Sha;
        }

        /// <summary>원격 추적 ref의 끝. 병합 판정은 로컬이 아니라 방금 받은 원격을 기준으로 한다.</summary>
        private static Commit? RemoteTip(Repository repo, string branchName)
        {
            return repo.Branches[repo.Head.RemoteName + "/" + branchName]?.Tip;
        }

        /// <summary>매핑과 그 고정 브랜치. 고정 브랜치가 없으면 병합 목적지가 없다.</summary>
        private (MappingConfig Mapping, string Target)? ResolvePinnedTarget(string serverName, string databaseName)
        {
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping == null || string.IsNullOrWhiteSpace(mapping.Branch)) return null;
            return (mapping, mapping.Branch!);
        }
```

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerMergeTests|FullyQualifiedName~GitManagerTests"`
Expected: PASS

`GetUnmergedBranches_ReturnsEmpty_WhenMappingIsNotPinned`가 실패하면 `ConfigManager.AddMapping(server, db, path)`가 `Branch`를 비우는지 확인한다(비우는 것이 정상).

- [ ] **Step 8: 커밋한다**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/FakeGitManagerBase.cs tests/DBVC.Core.Tests/GitManagerMergeTests.cs
git commit -m "feat(core): 고정 브랜치에 병합되지 않은 원격 브랜치 목록을 더한다"
```

---

### Task 4: `PreviewMerge`

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `tests/DBVC.Core.Tests/FakeGitManagerBase.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerMergeTests.cs`

**Interfaces:**
- Consumes: Task 1 모델, `PromotionLeakDetector.Detect` (Task 2), `RemoteTip`·`IsAncestor`·`ResolvePinnedTarget` (Task 3), 테스트 도우미 `NewPinnedClone`·`PushAuthorBranch`·`NewPinnedGitManager` (Task 3)
- Produces:
  - `MergePreview IGitManager.PreviewMerge(string serverName, string databaseName, string sourceBranch)` — 네트워크를 쓰지 않는다. 매핑이 고정되지 않았거나 원본 ref가 없으면 `InvalidOperationException`(한국어).
  - `GitManager` private: `static IReadOnlyCollection<string> AddedLines(Repository repo, Blob? before, Blob? after)` — Task 5는 쓰지 않지만 판정 재료의 유일한 자리다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`GitManagerMergeTests`에 더한다.

```csharp
        // ---------- PreviewMerge ----------

        private static string Proc(params string[] bodyLines) =>
            "CREATE OR ALTER PROCEDURE dbo.usp_Order AS\n" + string.Join("\n", bodyLines) + "\n";

        [Test]
        public void PreviewMerge_ListsChangedPaths_WithoutTouchingWorkingTree()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            string headBefore;
            using (var repo = new Repository(local)) headBefore = repo.Head.Tip.Sha;

            var preview = git.PreviewMerge(Server, Database, "PROJ-1");

            Assert.That(preview.ChangedPaths, Is.EqualTo(new[] { SqlPath }));
            Assert.That(preview.ConflictPaths, Is.Empty);
            Assert.That(preview.AlreadyMerged, Is.False);
            using var after = new Repository(local);
            Assert.That(after.Head.Tip.Sha, Is.EqualTo(headBefore));
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
        }

        [Test]
        public void PreviewMerge_ReportsConflicts_WithoutTouchingWorkingTree()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-A", EnvironmentBranches.Master, SqlPath, Proc("SELECT 'A'"));
            PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, SqlPath, Proc("SELECT 'B'"));
            using (var repo = new Repository(origin))
            {
                repo.Refs.UpdateTarget("refs/heads/develop", repo.Branches["PROJ-A"].Tip.Sha);
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);

            var preview = git.PreviewMerge(Server, Database, "PROJ-B");

            Assert.That(preview.ConflictPaths, Is.EqualTo(new[] { SqlPath }));
            using var after = new Repository(local);
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
        }

        [Test]
        public void PreviewMerge_ReportsAlreadyMerged_WhenSourceTipIsInTarget()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            using (var repo = new Repository(origin)) repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Develop].Tip);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);

            Assert.That(git.PreviewMerge(Server, Database, "PROJ-0").AlreadyMerged, Is.True);
        }

        [Test]
        public void PreviewMerge_ReportsLeaks_OnlyWhenTargetIsMaster()
        {
            // PROJ-B의 스냅샷에 PROJ-A의 줄이 딸려 온 상황. 두 클론 모두 같은 origin을 본다.
            var carried = "DECLARE @DiscountRate DECIMAL(5,2) = 0.1";
            var (masterLocal, origin) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            PushAuthorBranch(origin, "PROJ-A", EnvironmentBranches.Master, SqlPath, Proc(carried, "SELECT 1"));
            PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, SqlPath, Proc(carried, "SELECT 1", "SELECT 'B'"));

            var auditGit = NewPinnedGitManager(masterLocal, EnvironmentBranches.Master, MappingMode.Audit);
            auditGit.GetUnmergedBranches(Server, Database);
            var masterPreview = auditGit.PreviewMerge(Server, Database, "PROJ-B");

            var developLocal = NewTempDir();
            Repository.Clone(origin, developLocal, new CloneOptions { BranchName = EnvironmentBranches.Develop });
            var deployGit = NewPinnedGitManager(developLocal, EnvironmentBranches.Develop, MappingMode.Deploy);
            deployGit.GetUnmergedBranches(Server, Database);
            var developPreview = deployGit.PreviewMerge(Server, Database, "PROJ-B");

            Assert.That(masterPreview.Leaks, Has.Count.EqualTo(1));
            Assert.That(masterPreview.Leaks[0].BranchName, Is.EqualTo("PROJ-A"));
            Assert.That(masterPreview.Leaks[0].Lines, Does.Contain(carried));
            Assert.That(developPreview.Leaks, Is.Empty, "develop 병합에서는 딸려 온 것이 원래 있던 곳으로 돌아갈 뿐입니다");
        }

        [Test]
        public void PreviewMerge_ReportsLeaks_WhenSourceAddsANewFile()
        {
            // 원본에서 파일이 새로 생기면 이전 블롭이 없다. 전체 줄이 추가로 세어져야 한다.
            var newPath = "dbo/Views/v_Discount.sql";
            var shared = "SELECT o.Id, o.DiscountRate FROM dbo.Orders o";
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            PushAuthorBranch(origin, "PROJ-A", EnvironmentBranches.Master, newPath, "CREATE OR ALTER VIEW dbo.v_Discount AS\n" + shared + "\n");
            PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, newPath, "CREATE OR ALTER VIEW dbo.v_Discount AS\n" + shared + "\nWHERE 1 = 1\n");
            var git = NewPinnedGitManager(local, EnvironmentBranches.Master, MappingMode.Audit);
            git.GetUnmergedBranches(Server, Database);

            var preview = git.PreviewMerge(Server, Database, "PROJ-B");

            Assert.That(preview.Leaks.Single().Lines, Does.Contain(shared));
        }

        [Test]
        public void PreviewMerge_Throws_WhenSourceBranchDoesNotExist()
        {
            var (local, _) = NewPinnedClone(EnvironmentBranches.Develop);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PreviewMerge(Server, Database, "PROJ-404"));
            Assert.That(ex!.Message, Does.Contain("PROJ-404"));
        }
```

`FakeGitManagerBase`에 더한다.

```csharp
        public virtual MergePreview PreviewMerge(string serverName, string databaseName, string sourceBranch) => throw new NotSupportedException();
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerMergeTests.PreviewMerge"`
Expected: 빌드 실패 — `PreviewMerge` 없음

- [ ] **Step 3: 인터페이스를 더한다**

`Abstractions.cs`의 `GetUnmergedBranches` 아래:

```csharp
        /// <summary>
        /// 작업 트리를 건드리지 않고 원본을 고정 브랜치에 병합한 결과를 계산한다. 네트워크를 쓰지
        /// 않는다 - 마지막 Fetch(GetUnmergedBranches) 기준이다. 목적지가 master일 때만 경고 A를 채운다.
        /// </summary>
        MergePreview PreviewMerge(string serverName, string databaseName, string sourceBranch);
```

- [ ] **Step 4: 구현한다**

`GitManager.cs`의 `GetUnmergedBranches` 아래:

```csharp
        /// <summary>
        /// ObjectDatabase.MergeCommits는 메모리에서 병합 트리와 충돌을 낸다. 병합 버튼을 누르기 전에
        /// 충돌을 알아 버튼을 잠글 수 있고, 작업 트리를 되돌릴 일이 없다.
        /// </summary>
        public MergePreview PreviewMerge(string serverName, string databaseName, string sourceBranch)
        {
            var pinned = ResolvePinnedTarget(serverName, databaseName)
                ?? throw new InvalidOperationException("이 대상에는 고정 브랜치가 없어 병합할 수 없습니다.");
            var target = pinned.Target;

            using var repo = new Repository(pinned.Mapping.GitPath);

            var sourceTip = RemoteTip(repo, sourceBranch)
                ?? throw new InvalidOperationException(
                    $"원격에서 '{sourceBranch}' 브랜치를 찾을 수 없습니다. [병합할 브랜치 확인]을 다시 누르세요.");
            var targetTip = RemoteTip(repo, target) ?? repo.Head.Tip;

            if (IsAncestor(repo, sourceTip, targetTip))
            {
                return new MergePreview { AlreadyMerged = true };
            }

            var merged = repo.ObjectDatabase.MergeCommits(targetTip, sourceTip, new MergeTreeOptions());

            if (merged.Status == MergeTreeStatus.Conflicts)
            {
                return new MergePreview
                {
                    ConflictPaths = merged.Conflicts
                        .Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor).Path.Replace('\\', '/'))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList()
                };
            }

            var changedPaths = repo.Diff.Compare<TreeChanges>(targetTip.Tree, merged.Tree)
                .Select(c => c.Path.Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            return new MergePreview
            {
                ChangedPaths = changedPaths,
                // develop 병합에서 딸려 온 것은 원래 있던 곳으로 돌아갈 뿐이다(브랜치 정책 정정 3절).
                Leaks = target == EnvironmentBranches.Master
                    ? DetectLeaks(repo, sourceBranch, targetTip, merged.Tree, changedPaths)
                    : Array.Empty<PromotionLeak>()
            };
        }

        private static IReadOnlyList<PromotionLeak> DetectLeaks(
            Repository repo, string sourceBranch, Commit targetTip, Tree mergedTree, IReadOnlyList<string> changedPaths)
        {
            // 규약 밖 파일(사람이 둔 잡다한 .sql, .gitattributes)은 객체가 아니므로 판정하지 않는다.
            var objectPaths = changedPaths
                .Where(p => ObjectPathConvention.TryParseRelativePath(p, out _, out _, out _))
                .ToList();
            if (objectPaths.Count == 0) return Array.Empty<PromotionLeak>();

            var source = objectPaths.ToDictionary(
                p => p,
                p => AddedLines(repo, BlobAt(targetTip.Tree, p), BlobAt(mergedTree, p)),
                StringComparer.Ordinal);

            var remotePrefix = repo.Head.RemoteName + "/";
            var others = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal);

            foreach (var branch in repo.Branches.Where(b => b.IsRemote && b.FriendlyName.StartsWith(remotePrefix, StringComparison.Ordinal)))
            {
                var name = branch.FriendlyName.Substring(remotePrefix.Length);
                if (name == "HEAD" || name == sourceBranch || EnvironmentBranches.IsEnvironmentBranch(name)) continue;

                var tip = branch.Tip;
                if (tip == null || IsAncestor(repo, tip, targetTip)) continue;

                var mergeBase = repo.ObjectDatabase.FindMergeBase(targetTip, tip);
                var lines = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
                foreach (var path in objectPaths)
                {
                    var after = BlobAt(tip.Tree, path);
                    if (after == null) continue;
                    var before = mergeBase == null ? null : BlobAt(mergeBase.Tree, path);
                    if (before != null && before.Sha == after.Sha) continue;
                    lines[path] = AddedLines(repo, before, after);
                }

                if (lines.Count > 0) others[name] = lines;
            }

            return PromotionLeakDetector.Detect(source, others);
        }

        private static Blob? BlobAt(Tree tree, string path)
        {
            return tree[path]?.Target as Blob;
        }

        /// <summary>
        /// before → after에서 추가된 줄. before가 없으면(새 파일) after의 모든 줄이다 -
        /// Diff.Compare의 null 블롭 처리에 기대지 않고 직접 나눈다.
        /// </summary>
        private static IReadOnlyCollection<string> AddedLines(Repository repo, Blob? before, Blob? after)
        {
            if (after == null) return Array.Empty<string>();

            if (before == null)
            {
                return after.GetContentText().Split('\n');
            }

            return repo.Diff.Compare(before, after).AddedLines.Select(l => l.Content).ToList();
        }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerMergeTests"`
Expected: PASS

`PreviewMerge_ReportsConflicts_WithoutTouchingWorkingTree`에서 충돌이 나지 않으면 두 브랜치가 같은 줄을 다르게 고쳤는지(두 파일 모두 둘째 줄이 서로 다른지) 확인한다.

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/FakeGitManagerBase.cs tests/DBVC.Core.Tests/GitManagerMergeTests.cs
git commit -m "feat(core): 작업 트리 없이 병합 결과와 경고 A를 미리 계산한다"
```

---

### Task 5: `MergeAndPush`

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `tests/DBVC.Core.Tests/FakeGitManagerBase.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerMergeTests.cs`

**Interfaces:**
- Consumes: Task 1 모델·정책, Task 3 도우미(`FetchCurrentRemote`, `RemoteTip`, `IsAncestor`, `ResolvePinnedTarget`), 기존 `EnsureAllowed`, `BuildSignature`, `AbortMerge`, `UntrackedInclusiveOptions`, `BuildPushOptions`, `BuildPushRejectionMessage`, `GitPushRejectedException`
- Produces:
  - `MergeOutcome IGitManager.MergeAndPush(string serverName, string databaseName, string sourceBranch)`
  - `GitManager` private `void PushHeadOrThrow(Repository repo, string repoPath, string? guidance)` — `PushChanges`의 추적 갈래(`repo.Network.Push(repo.Head, options)`부터 `pushErrors` 검사까지)를 옮긴 것. `PushChanges`도 이것을 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // ---------- MergeAndPush ----------

        [Test]
        public void MergeAndPush_CreatesMergeCommit_WhenFastForwardIsPossible()
        {
            // develop이 원본의 조상이라 fast-forward가 가능해도 병합 커밋을 만든다(스펙 2.6).
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1");

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Merged), outcome.Message);
            Assert.That(outcome.Paths, Is.EqualTo(new[] { SqlPath }));
            using var remote = new Repository(origin);
            var tip = remote.Branches[EnvironmentBranches.Develop].Tip;
            Assert.That(tip.Parents.Count(), Is.EqualTo(2));
            Assert.That(tip.Parents.Select(p => p.Sha), Does.Contain(sourceSha));
            Assert.That(tip.MessageShort, Is.EqualTo("PROJ-1 브랜치를 develop에 병합"));
            Assert.That(tip.Author.Name, Is.EqualTo("Deployer"));
        }

        [Test]
        public void MergeAndPush_CatchesUp_WhenLocalIsBehindRemote()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            // 다른 사람이 먼저 develop에 병합해 둔 상황.
            PushAuthorBranch(origin, EnvironmentBranches.Develop, EnvironmentBranches.Develop, "dbo/Views/v_Other.sql", "other\n");
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1");

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Merged), outcome.Message);
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Tree["dbo/Views/v_Other.sql"], Is.Not.Null);
        }

        [Test]
        public void MergeAndPush_RestoresHead_WhenPushFails()
        {
            // 올라가지 않은 병합 커밋이 남으면 다음 병합이 LocalAhead에 걸리고, Push가 금지인 클론은
            // 도구 안에서 빠져나올 길이 없다(스펙 2.5).
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            string headBefore;
            using (var repo = new Repository(local)) headBefore = repo.Head.Tip.Sha;
            // 원격의 develop ref를 잠가 갱신을 실패시킨다. 파일 전송으로는 서버 거부를 재현할 수 없다.
            File.WriteAllText(Path.Combine(origin, "refs", "heads", "develop.lock"), string.Empty);

            // 실패는 예외(통신)로도 PushRejected(거부)로도 올 수 있다. 검증하는 것은 어느 쪽이든
            // 로컬이 되돌려졌다는 것이다.
            MergeOutcome? outcome = null;
            try { outcome = git.MergeAndPush(Server, Database, "PROJ-1"); }
            catch (LibGit2SharpException) { }
            catch (GitRemoteException) { }

            Assert.That(outcome == null || outcome.Kind == MergeOutcomeKind.PushRejected, Is.True,
                "Push가 실패했는데 Merged로 보고하면 안 됩니다: " + outcome?.Kind);
            using var after = new Repository(local);
            Assert.That(after.Head.Tip.Sha, Is.EqualTo(headBefore));
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
        }
```

```csharp
        [Test]
        public void MergeAndPush_ReturnsLocalAhead_WhenLocalHasUnpushedCommits()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            WriteFile(local, "dbo/Views/v_Stray.sql", "stray\n");
            using (var repo = new Repository(local))
            {
                Commands.Stage(repo, "*");
                repo.Commit("밖에서 만든 커밋", Sig(), Sig());
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1");

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.LocalAhead));
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Parents.Count(), Is.EqualTo(0),
                "원격 develop은 첫 커밋 그대로여야 합니다");
        }

        [Test]
        public void MergeAndPush_Refuses_WhenTreeIsDirty()
        {
            // 7단계의 hard reset이 안전한 근거가 이 검사다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            WriteFile(local, SqlPath, "손으로 고친 내용\n");
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1");

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Refused));
            Assert.That(outcome.Paths, Is.EqualTo(new[] { SqlPath }));
            Assert.That(File.ReadAllText(Path.Combine(local, "dbo", "StoredProcedures", "usp_Order.sql")), Is.EqualTo("손으로 고친 내용\n"));
        }

        [TestCase("develop")]
        [TestCase("master")]
        public void MergeAndPush_Refuses_WhenSourceIsEnvironmentBranch(string source)
        {
            var (local, _) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Master, MappingMode.Audit);

            var outcome = git.MergeAndPush(Server, Database, source);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Refused));
            Assert.That(outcome.Message, Does.Contain(source));
        }

        [Test]
        public void MergeAndPush_ReturnsAlreadyMerged_WhenSourceTipIsInTarget()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            using (var repo = new Repository(origin)) repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Develop].Tip);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            Assert.That(git.MergeAndPush(Server, Database, "PROJ-0").Kind, Is.EqualTo(MergeOutcomeKind.AlreadyMerged));
        }

        [Test]
        public void MergeAndPush_Throws_InWriteMode()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Write);

            Assert.Throws<OperationNotAllowedException>(() => git.MergeAndPush(Server, Database, "PROJ-1"));
        }
```

`FakeGitManagerBase`에 더한다.

```csharp
        public virtual MergeOutcome MergeAndPush(string serverName, string databaseName, string sourceBranch) => throw new NotSupportedException();
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerMergeTests.MergeAndPush"`
Expected: 빌드 실패 — `MergeAndPush` 없음

- [ ] **Step 3: Push 도우미를 뽑는다**

`GitManager.PushChanges`의 추적 갈래(두 번째 `var requiresUserCredentials = false;`부터 `if (pushErrors.Count > 0) { throw ... }`까지)를 아래 도우미로 옮기고, 그 자리를 `PushHeadOrThrow(repo, repoPath, guidance);`로 바꾼다. 옮긴 뒤에도 `return PushResult.Pushed;`는 `PushChanges`에 남는다.

```csharp
        /// <summary>
        /// 현재 브랜치를 추적 중인 원격에 올린다. Push와 병합이 같은 예외 변환을 쓴다 - catch 순서가
        /// 곧 정확성이라(NonFastForwardException이 먼저) 복제하면 한쪽만 틀어진다.
        /// </summary>
        private static void PushHeadOrThrow(Repository repo, string repoPath, string? guidance)
        {
            var requiresUserCredentials = false;
            var pushErrors = new List<PushStatusError>();
            var options = BuildPushOptions(
                () => requiresUserCredentials = true,
                error => pushErrors.Add(error));

            try
            {
                repo.Network.Push(repo.Head, options);
            }
            // (기존 PushChanges의 catch 세 개와 주석을 그대로 옮긴다.)

            // (기존 pushErrors 검사와 주석을 그대로 옮긴다.)
        }
```

위 두 괄호 주석은 옮길 자리 표시다. **실제 파일에는 `PushChanges`의 추적 갈래(현재 `GitManager.cs` 약 784–826행)에 있던 catch 세 개(`NonFastForwardException` → `requiresUserCredentials` → `guidance != null` 순서)와 `pushErrors.Count > 0` 검사를 주석까지 글자 그대로 붙여 넣는다.** 새로 쓰는 코드가 아니라 이동이다.

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PushChanges"`
Expected: PASS (동작 변화 없음)

- [ ] **Step 4: 인터페이스를 더한다**

`Abstractions.cs`의 `PreviewMerge` 아래:

```csharp
        /// <summary>
        /// 원본을 고정 브랜치에 병합 커밋으로 병합하고 그 커밋만 원격에 올린다. 로컬이 원격보다
        /// 앞서 있으면 시작하지 않는다. Push가 실패하면 로컬을 병합 전으로 되돌린 뒤 거부는
        /// PushRejected로, 통신·인증 실패는 예외로 알린다. mode가 병합을 허용하지 않으면
        /// OperationNotAllowedException을 던진다.
        /// </summary>
        MergeOutcome MergeAndPush(string serverName, string databaseName, string sourceBranch);
```

- [ ] **Step 5: 구현한다**

`GitManager.cs`의 `PreviewMerge` 아래:

```csharp
        public MergeOutcome MergeAndPush(string serverName, string databaseName, string sourceBranch)
        {
            EnsureAllowed(serverName, databaseName, DbvcOperation.Merge);

            var pinned = ResolvePinnedTarget(serverName, databaseName);
            if (pinned == null)
                return MergeOutcome.Of(MergeOutcomeKind.Refused, "이 대상에는 고정 브랜치가 없어 병합할 수 없습니다.");

            var repoPath = pinned.Value.Mapping.GitPath;
            var target = pinned.Value.Target;

            if (EnvironmentBranches.IsEnvironmentBranch(sourceBranch))
            {
                // master 목록에 develop이 뜨면 develop을 통째로 운영에 병합하는 것이 버튼 한 번이 된다.
                return MergeOutcome.Of(MergeOutcomeKind.Refused,
                    $"'{sourceBranch}'는 환경 브랜치라 병합 원본이 될 수 없습니다. 티켓 브랜치를 하나씩 병합하세요.");
            }

            using var repo = new Repository(repoPath);

            if (repo.Head.FriendlyName != target)
            {
                return MergeOutcome.Of(MergeOutcomeKind.Refused,
                    $"저장소가 '{target}'이 아니라 '{repo.Head.FriendlyName}'에 있어 병합하지 않았습니다.");
            }

            // 아래 Push 실패 시의 hard reset이 안전한 근거가 이 검사다. 옮기거나 느슨하게 하면
            // 되돌리기가 사용자 파일을 지운다.
            var dirty = repo.RetrieveStatus(UntrackedInclusiveOptions)
                .Where(e => e.State != FileStatus.Ignored && e.State != FileStatus.Unaltered)
                .Select(e => e.FilePath.Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (dirty.Count > 0)
            {
                return MergeOutcome.Of(MergeOutcomeKind.Refused,
                    "커밋되지 않은 변경이 있어 병합하지 않았습니다. 배포·감사 클론은 DBVC가 파일을 쓰지 않으므로 원인은 도구 밖에 있습니다.",
                    dirty);
            }

            FetchCurrentRemote(repo, repoPath, "병합");

            var remoteTarget = RemoteTip(repo, target);
            var localTip = repo.Head.Tip;
            if (remoteTarget != null && localTip != null && remoteTarget.Sha != localTip.Sha)
            {
                if (!IsAncestor(repo, localTip, remoteTarget))
                {
                    // 병합은 자기가 만든 커밋 하나만 올린다. 앞선 커밋이 섞여 나가면 Push 금지가 우회된다.
                    return MergeOutcome.Of(MergeOutcomeKind.LocalAhead,
                        "이 클론에 원격에 없는 커밋이 있어 병합하지 않았습니다. 배포·감사 클론은 DBVC가 커밋하지 않으므로 원인은 도구 밖에 있습니다.");
                }

                // 뒤처져만 있다. 트리가 깨끗하므로 잃을 것이 없다.
                repo.Reset(ResetMode.Hard, remoteTarget);
            }

            var sourceTip = RemoteTip(repo, sourceBranch);
            if (sourceTip == null)
            {
                return MergeOutcome.Of(MergeOutcomeKind.Refused, $"원격에서 '{sourceBranch}' 브랜치를 찾을 수 없습니다.");
            }

            var headBefore = repo.Head.Tip;
            if (headBefore != null && IsAncestor(repo, sourceTip, headBefore))
            {
                return MergeOutcome.Of(MergeOutcomeKind.AlreadyMerged, "이미 병합되어 있습니다.");
            }

            var signature = BuildSignature(repo);
            var result = repo.Merge(sourceTip, signature, new MergeOptions
            {
                // fast-forward하면 언제 무엇을 병합했는지가 이력에서 사라지고 되돌릴 단위도 없어진다.
                FastForwardStrategy = FastForwardStrategy.NoFastForward,
                CommitOnSuccess = false
            });

            if (result.Status == MergeStatus.Conflicts)
            {
                var conflicts = repo.Index.Conflicts
                    .Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor).Path.Replace('\\', '/'))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                AbortMerge(repo, headBefore);
                return MergeOutcome.Of(MergeOutcomeKind.Conflicts,
                    "그 사이 원격이 바뀌어 충돌이 생겼습니다. 도구 안에서는 충돌을 풀 수 없습니다.", conflicts);
            }

            var mergeCommit = repo.Commit($"{sourceBranch} 브랜치를 {target}에 병합", signature, signature);

            var changed = repo.Diff.Compare<TreeChanges>(headBefore?.Tree, mergeCommit.Tree)
                .Select(c => c.Path.Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            try
            {
                // FetchCurrentRemote가 같은 검사를 이미 통과했으므로 여기서는 던지지 않고 안내문만 돌려준다.
                PushHeadOrThrow(repo, repoPath, ValidateRemoteAndBuildGuidance(repo, repoPath, "병합"));
            }
            catch (GitPushRejectedException ex)
            {
                AbortMerge(repo, headBefore);
                return MergeOutcome.Of(MergeOutcomeKind.PushRejected,
                    "그 사이 다른 사람이 원격에 올렸거나 원격이 거부했습니다. 로컬은 병합 전으로 되돌렸습니다. 다시 시도하세요." +
                    Environment.NewLine + Environment.NewLine + ex.Message);
            }
            catch
            {
                // 올라가지 않은 병합 커밋을 남기면 다음 병합이 LocalAhead에 걸리고, Push가 금지인
                // 클론은 도구 안에서 빠져나올 길이 없다.
                AbortMerge(repo, headBefore);
                throw;
            }

            return MergeOutcome.Of(MergeOutcomeKind.Merged, null, changed);
        }
```

`CommitOnSuccess = false`(`MergeAndCheckoutOptionsBase`에 있다)로 두고 직접 커밋하는 이유는 메시지를 팀 문체로 정하기 위해서다. `repo.Commit`은 병합 진행 상태(`MERGE_HEAD`)를 읽어 부모 둘을 단다.

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerMergeTests"`
Expected: PASS

`MergeAndPush_RestoresHead_WhenPushFails`가 "Push가 성공해 버렸다"로 실패하면(잠금 파일을 libgit2 로컬 전송이 무시하는 경우) 잠금 대신 원격 `refs/heads/develop` 파일을 읽기 전용으로 만든다: `File.SetAttributes(path, FileAttributes.ReadOnly)`. 그래도 성공하면 `origin` 폴더를 지워 통신 실패를 만든다 — 목적은 "Push 단계 실패 뒤 로컬이 되돌려졌다"의 검증이지 실패 원인이 아니다.

- [ ] **Step 7: Core 전체를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS

- [ ] **Step 8: 커밋한다**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/FakeGitManagerBase.cs tests/DBVC.Core.Tests/GitManagerMergeTests.cs
git commit -m "feat(core): 병합 커밋 하나만 올리고 실패하면 되돌리는 병합을 더한다"
```

---

### Task 6: `DeploymentViewModel` 병합 영역

**Files:**
- Create: `src/DBVC.Vsix/ViewModels/UnmergedBranchItemViewModel.cs`
- Modify: `src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs`
- Create: `tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelMergeTests.cs`

**Interfaces:**
- Consumes: `IGitManager.GetUnmergedBranches`/`PreviewMerge`/`MergeAndPush`, 모델(Task 1), `MappingPolicy`, 기존 `IUserNotifier.Confirm`, `IBackgroundScheduler.Run(work, onSuccess, onError)`, `BusyState`
- Produces (Task 7 XAML이 바인딩한다):
  - `ObservableCollection<UnmergedBranchItemViewModel> UnmergedBranches`
  - `UnmergedBranchItemViewModel? SelectedBranch` (TwoWay) — 바뀌면 미리보기를 읽는다
  - `string? PreviewText` — 바뀔 객체·충돌·경고를 여러 줄로
  - `string? MergeTargetBranch` — 매핑의 고정 브랜치
  - `bool IsTargetMaster` — "테스트 반영" 열 표시
  - `bool CanMerge` (속성) / `ICommand LoadBranchesCommand` / `ICommand MergeCommand`
  - `UnmergedBranchItemViewModel(UnmergedBranch)`: `Name`, `AuthorText`, `TimeText`(`yyyy-MM-dd HH:mm`), `CommitCountText`(`"{n}개"`), `InDevelopText`(`"반영됨"`/`"미반영"`/`""`), `Branch`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelMergeTests.cs`. 픽스처 준비는 `DeploymentViewModelTests.NewViewModel`과 같은 모양이다(그 파일의 테스트 대역 `RecordingNotifier`, `RecordingSaveDialog`, `InlineBackgroundScheduler`를 그대로 쓴다).

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 병합과 배포 사이가 끊기면 "병합했으니 나갔겠지"가 생긴다. 병합 뒤 차이 검사를 제안하는 것이 요점이다.
    /// </summary>
    [TestFixture]
    public class DeploymentViewModelMergeTests
    {
        private const string Server = "TestServer";
        private const string Database = "TestDb";

        private Mock<IConfigManager> _config = null!;
        private Mock<IGitManager> _git = null!;
        private Mock<ISmoManager> _smo = null!;
        private RecordingNotifier _notifier = null!;

        private DeploymentViewModel NewViewModel(MappingMode mode, string branch)
        {
            var mapping = new MappingConfig
            {
                ServerName = Server, DatabaseName = Database, GitPath = Path.GetTempPath(), Mode = mode, Branch = branch
            };
            _config = new Mock<IConfigManager>();
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns(mapping);
            _git = new Mock<IGitManager>();
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            _smo = new Mock<ISmoManager>();
            _notifier = new RecordingNotifier();

            var vm = new DeploymentViewModel(
                _config.Object, _git.Object, _smo.Object,
                new ScriptExporter(_config.Object, _git.Object),
                _notifier, new RecordingSaveDialog(), new InlineBackgroundScheduler(), new BusyState());
            vm.SetTarget(Server, Database, mode);
            return vm;
        }

        private static UnmergedBranch Branch(string name, bool? inDevelop = null) => new UnmergedBranch
        {
            Name = name, LastCommitAuthor = "김개발", LastCommitTime = new DateTimeOffset(2026, 9, 17, 10, 30, 0, TimeSpan.FromHours(9)),
            CommitCount = 2, IsInDevelop = inDevelop
        };

        private DeploymentViewModel LoadedWith(MappingMode mode, string target, MergePreview preview, params UnmergedBranch[] branches)
        {
            var vm = NewViewModel(mode, target);
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(branches);
            _git.Setup(g => g.PreviewMerge(Server, Database, It.IsAny<string>())).Returns(preview);
            vm.LoadBranchesCommand.Execute(null);
            vm.SelectedBranch = vm.UnmergedBranches.First();
            return vm;
        }

        [Test]
        public void LoadBranchesCommand_FillsTheList_WithKoreanColumns()
        {
            var vm = NewViewModel(MappingMode.Audit, "master");
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(new[] { Branch("PROJ-1", inDevelop: true) });

            vm.LoadBranchesCommand.Execute(null);

            var item = vm.UnmergedBranches.Single();
            Assert.That(item.Name, Is.EqualTo("PROJ-1"));
            Assert.That(item.InDevelopText, Is.EqualTo("반영됨"));
            Assert.That(item.CommitCountText, Is.EqualTo("2개"));
            Assert.That(vm.IsTargetMaster, Is.True);
        }

        [Test]
        public void SelectedBranch_ShowsPreview_WhenBranchIsChosen()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop",
                new MergePreview { ChangedPaths = new[] { "dbo/StoredProcedures/usp_Order.sql" } }, Branch("PROJ-1"));

            Assert.That(vm.PreviewText, Does.Contain("dbo/StoredProcedures/usp_Order.sql"));
            Assert.That(vm.CanMerge, Is.True);
        }

        [Test]
        public void CanMerge_IsFalse_WhenPreviewHasConflicts()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop",
                new MergePreview { ConflictPaths = new[] { "dbo/Views/v_A.sql" } }, Branch("PROJ-1"));

            Assert.That(vm.CanMerge, Is.False);
            Assert.That(vm.MergeCommand.CanExecute(null), Is.False);
            Assert.That(vm.PreviewText, Does.Contain("충돌").And.Contain("dbo/Views/v_A.sql"));
            Assert.That(vm.PreviewText, Does.Contain("develop을 티켓 브랜치로 병합해서 풀면 안 됩니다"));
        }

        [Test]
        public void MergeCommand_IsDisabled_InWriteMode()
        {
            var vm = LoadedWith(MappingMode.Write, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));

            Assert.That(vm.MergeCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void MergeCommand_IncludesLeaksInConfirmation_WhenTargetIsMaster()
        {
            var preview = new MergePreview
            {
                ChangedPaths = new[] { "dbo/StoredProcedures/usp_Order.sql" },
                Leaks = new[] { new PromotionLeak { Path = "dbo/StoredProcedures/usp_Order.sql", BranchName = "PROJ-120", Lines = new[] { "DiscountRate DECIMAL(5,2)" } } }
            };
            var vm = LoadedWith(MappingMode.Audit, "master", preview, Branch("PROJ-1"));
            _notifier.ConfirmResult = false;

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Single().Message,
                Does.Contain("PROJ-1").And.Contain("master").And.Contain("PROJ-120").And.Contain("DiscountRate DECIMAL(5,2)"));
            _git.Verify(g => g.MergeAndPush(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void MergeCommand_OffersComparison_WhenMerged()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1"))
                .Returns(MergeOutcome.Of(MergeOutcomeKind.Merged, null, new[] { "a.sql" }));
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(new ComparisonResult { ComparedCount = 1 });

            vm.MergeCommand.Execute(null);   // 첫 Confirm: 병합, 둘째 Confirm: 차이 검사

            Assert.That(_notifier.ConfirmCalls, Has.Count.EqualTo(2));
            Assert.That(_notifier.ConfirmCalls[1].Message, Does.Contain("PROJ-1을 develop에 병합하고 올렸습니다"));
            _smo.Verify(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(vm.UnmergedBranches.Select(b => b.Name), Does.Not.Contain("PROJ-1"));
        }

        [TestCase(MergeOutcomeKind.PushRejected, "다시 시도하세요")]
        [TestCase(MergeOutcomeKind.LocalAhead, "원격에 없는 커밋")]
        [TestCase(MergeOutcomeKind.Refused, "사유")]
        public void MergeCommand_ShowsCoreMessage_WhenNotMerged(MergeOutcomeKind kind, string expected)
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1"))
                .Returns(MergeOutcome.Of(kind, "사유: " + expected));

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls.Single().Message, Does.Contain(expected));
            _smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void MergeCommand_ShowsError_WhenCoreThrows()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1")).Throws(new GitRemoteException("원격에 연결하지 못했습니다."));

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls.Single().Title, Is.EqualTo("DBVC 병합 실패"));
        }

        [Test]
        public void SetTarget_ClearsMergeState_WhenTargetChanges()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));

            vm.SetTarget("Other", "Db", MappingMode.Deploy);

            Assert.That(vm.UnmergedBranches, Is.Empty);
            Assert.That(vm.SelectedBranch, Is.Null);
            Assert.That(vm.PreviewText, Is.Null);
        }
    }
}
```

`GitRemoteException`의 생성자가 `(string)`을 받지 않으면 그 파일의 실제 생성자에 맞춘다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~DeploymentViewModelMergeTests"`
Expected: 빌드 실패 — `LoadBranchesCommand` 등 없음

- [ ] **Step 3: 목록 한 줄을 만든다**

`src/DBVC.Vsix/ViewModels/UnmergedBranchItemViewModel.cs`:

```csharp
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>미병합 브랜치 한 줄의 한국어 표시. Core는 값만 갖고 화면만 문구를 안다.</summary>
    public class UnmergedBranchItemViewModel
    {
        public UnmergedBranchItemViewModel(UnmergedBranch branch)
        {
            Branch = branch;
        }

        public UnmergedBranch Branch { get; }

        public string Name => Branch.Name;
        public string AuthorText => Branch.LastCommitAuthor;
        public string TimeText => Branch.LastCommitTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string CommitCountText => $"{Branch.CommitCount}개";

        /// <summary>목적지가 master가 아니면 빈 문자열이다 - 열 자체가 숨는다.</summary>
        public string InDevelopText =>
            Branch.IsInDevelop == null ? string.Empty : Branch.IsInDevelop.Value ? "반영됨" : "미반영";
    }
}
```

- [ ] **Step 4: `DeploymentViewModel`에 병합 영역을 더한다**

필드(기존 필드 아래):

```csharp
        private MergePreview? _preview;

        /// <summary>미리보기 읽기의 세대. 빠르게 고른 이전 선택의 응답이 화면을 덮지 않게 한다.</summary>
        private int _previewGeneration;
```

생성자의 명령 등록 아래:

```csharp
            LoadBranchesCommand = new RelayCommand(LoadBranches, () => HasTarget && !Busy.IsBusy && IsMergeAllowed);
            MergeCommand = new RelayCommand(Merge, () => CanMerge && !Busy.IsBusy);
```

속성(`SaveScriptCommand` 선언 아래):

```csharp
        public ICommand LoadBranchesCommand { get; }
        public ICommand MergeCommand { get; }

        public ObservableCollection<UnmergedBranchItemViewModel> UnmergedBranches { get; } =
            new ObservableCollection<UnmergedBranchItemViewModel>();

        private UnmergedBranchItemViewModel? _selectedBranch;
        public UnmergedBranchItemViewModel? SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (ReferenceEquals(_selectedBranch, value)) return;
                _selectedBranch = value;
                OnPropertyChanged();
                LoadPreview();
            }
        }

        private string? _previewText;
        public string? PreviewText
        {
            get => _previewText;
            private set
            {
                if (_previewText == value) return;
                _previewText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>매핑의 고정 브랜치. 병합 목적지는 이것 하나뿐이다.</summary>
        public string? MergeTargetBranch =>
            HasTarget ? _configManager.TryGetMapping(_serverName!, _databaseName!)?.Branch : null;

        public bool IsTargetMaster => MergeTargetBranch == EnvironmentBranches.Master;

        private bool IsMergeAllowed => MappingPolicy.IsAllowed(_mode, DbvcOperation.Merge);

        /// <summary>미리보기가 있고 충돌이 없으며 아직 병합되지 않았을 때만.</summary>
        public bool CanMerge =>
            HasTarget && IsMergeAllowed && SelectedBranch != null && _preview != null
            && !_preview.AlreadyMerged && _preview.ConflictPaths.Count == 0;
```

`SetTarget` 끝(`RaiseCanExecuteChanged();` 앞)에 더한다.

```csharp
            // 낡은 목록을 최신인 척 두지 않는다. 다른 대상의 브랜치를 병합하는 사고도 여기서 막는다.
            _previewGeneration++;
            _preview = null;
            _selectedBranch = null;
            OnPropertyChanged(nameof(SelectedBranch));
            UnmergedBranches.Clear();
            PreviewText = null;
            OnPropertyChanged(nameof(MergeTargetBranch));
            OnPropertyChanged(nameof(IsTargetMaster));
            OnPropertyChanged(nameof(CanMerge));
```

메서드(`SaveScript` 위):

```csharp
        private void LoadBranches()
        {
            if (!HasTarget) return;
            var server = _serverName!;
            var database = _databaseName!;

            Busy.IsBusy = true;
            Busy.IsCancellable = false;
            Busy.ProgressText = "원격에서 병합할 브랜치를 확인하는 중...";

            _scheduler.Run(
                () => _gitManager.GetUnmergedBranches(server, database),
                branches =>
                {
                    EndBusy();
                    _previewGeneration++;
                    _preview = null;
                    _selectedBranch = null;
                    OnPropertyChanged(nameof(SelectedBranch));
                    UnmergedBranches.Clear();
                    foreach (var branch in branches) UnmergedBranches.Add(new UnmergedBranchItemViewModel(branch));
                    PreviewText = UnmergedBranches.Count == 0
                        ? $"'{MergeTargetBranch}'에 병합되지 않은 브랜치가 없습니다."
                        : null;
                    OnPropertyChanged(nameof(IsTargetMaster));
                    OnPropertyChanged(nameof(CanMerge));
                    RaiseCanExecuteChanged();
                },
                ex =>
                {
                    EndBusy();
                    _notifier.ShowError("DBVC 병합할 브랜치 확인 실패", ex.Message);
                });
        }

        private void LoadPreview()
        {
            _preview = null;
            PreviewText = null;
            OnPropertyChanged(nameof(CanMerge));
            RaiseCanExecuteChanged();

            var selected = SelectedBranch;
            if (selected == null || !HasTarget) return;

            var server = _serverName!;
            var database = _databaseName!;
            var generation = ++_previewGeneration;

            _scheduler.Run(
                () => _gitManager.PreviewMerge(server, database, selected.Name),
                preview =>
                {
                    if (generation != _previewGeneration) return;
                    _preview = preview;
                    PreviewText = BuildPreviewText(preview, MergeTargetBranch);
                    OnPropertyChanged(nameof(CanMerge));
                    RaiseCanExecuteChanged();
                },
                ex =>
                {
                    if (generation != _previewGeneration) return;
                    PreviewText = "미리보기를 계산하지 못했습니다: " + ex.Message;
                });
        }

        private static string BuildPreviewText(MergePreview preview, string? target)
        {
            if (preview.AlreadyMerged) return "이미 병합되어 있습니다.";

            var sb = new StringBuilder();
            if (preview.ConflictPaths.Count > 0)
            {
                sb.AppendLine($"충돌 {preview.ConflictPaths.Count}개 — 병합할 수 없습니다.");
                foreach (var path in preview.ConflictPaths) sb.AppendLine("  " + path);
                sb.AppendLine();
                sb.AppendLine("도구 안에서는 충돌을 풀 수 없습니다. 브랜치 작성자가 GitLab이나 Git 클라이언트에서 풀어야 합니다.");
                sb.Append("develop을 티켓 브랜치로 병합해서 풀면 안 됩니다 — 운영 병합 때 develop 전체가 딸려 갑니다.");
                return sb.ToString();
            }

            sb.AppendLine($"'{target}'에서 바뀌는 파일 {preview.ChangedPaths.Count}개");
            foreach (var path in preview.ChangedPaths) sb.AppendLine("  " + path);

            if (preview.Leaks.Count > 0)
            {
                sb.AppendLine();
                sb.Append(BuildLeakText(preview.Leaks));
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>경고 A 문구. 방향을 모르므로 "딸려 왔다"고 단정하지 않는다(스펙 3.4 한계 1).</summary>
        private static string BuildLeakText(IReadOnlyList<PromotionLeak> leaks)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"확인 필요 {leaks.Count}건 — 아직 운영에 병합되지 않은 다른 브랜치와 같은 줄이 있습니다.");
            foreach (var leak in leaks)
            {
                sb.AppendLine($"  {leak.Path} — {leak.BranchName} 브랜치에도 같은 줄이 {leak.Lines.Count}개 있습니다. (예: {leak.Lines[0]})");
            }
            sb.Append("그 브랜치의 변경이 딸려 왔는지 확인하세요.");
            return sb.ToString();
        }

        private void Merge()
        {
            if (!CanMerge || SelectedBranch == null) return;

            var server = _serverName!;
            var database = _databaseName!;
            var source = SelectedBranch.Name;
            var target = MergeTargetBranch;
            var preview = _preview!;

            var question = $"'{source}' 브랜치를 '{target}'에 병합하고 원격에 올립니다.";
            if (preview.Leaks.Count > 0)
            {
                question += Environment.NewLine + Environment.NewLine + BuildLeakText(preview.Leaks);
            }
            if (!_notifier.Confirm("DBVC 병합", question)) return;

            Busy.IsBusy = true;
            Busy.IsCancellable = false;
            Busy.ProgressText = $"'{source}'을(를) '{target}'에 병합하는 중...";

            _scheduler.Run(
                () => _gitManager.MergeAndPush(server, database, source),
                outcome =>
                {
                    EndBusy();
                    ApplyMergeOutcome(outcome, source, target);
                },
                ex =>
                {
                    EndBusy();
                    _notifier.ShowError("DBVC 병합 실패", ex.Message);
                });
        }

        private void ApplyMergeOutcome(MergeOutcome outcome, string source, string? target)
        {
            switch (outcome.Kind)
            {
                case MergeOutcomeKind.Merged:
                case MergeOutcomeKind.AlreadyMerged:
                    var merged = UnmergedBranches.FirstOrDefault(b => b.Name == source);
                    if (merged != null) UnmergedBranches.Remove(merged);
                    _previewGeneration++;
                    _preview = null;
                    _selectedBranch = null;
                    OnPropertyChanged(nameof(SelectedBranch));
                    PreviewText = null;
                    OnPropertyChanged(nameof(CanMerge));
                    RaiseCanExecuteChanged();
                    break;
            }

            if (outcome.Kind == MergeOutcomeKind.Merged)
            {
                // 자동으로 돌리지 않는다 - 운영 DB 전체 추출은 오래 걸려 시작 시점은 사람이 정한다.
                // 병합과 배포 사이가 끊기면 "병합했으니 나갔겠지"가 생기므로 여기서 묻는다.
                var message = $"{source}을 {target}에 병합하고 올렸습니다." + Environment.NewLine + Environment.NewLine +
                              "DB는 병합만으로 바뀌지 않습니다. 지금 차이 검사를 시작해 반영할 것을 확인할까요?";
                if (_notifier.Confirm("DBVC 병합 완료", message) && CompareCommand.CanExecute(null))
                {
                    CompareCommand.Execute(null);
                }
                return;
            }

            if (outcome.Kind == MergeOutcomeKind.AlreadyMerged)
            {
                _notifier.ShowInfo("DBVC 병합", outcome.Message ?? "이미 병합되어 있습니다.");
                return;
            }

            var detail = outcome.Paths.Count == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, outcome.Paths);
            if (outcome.Kind == MergeOutcomeKind.Conflicts)
            {
                detail += Environment.NewLine + Environment.NewLine +
                          "develop을 티켓 브랜치로 병합해서 풀면 안 됩니다 — 운영 병합 때 develop 전체가 딸려 갑니다.";
            }
            _notifier.ShowError("DBVC 병합", (outcome.Message ?? string.Empty) + detail);
        }
```

`RaiseCanExecuteChanged`에 두 명령을 더한다.

```csharp
            (LoadBranchesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MergeCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

`using System.Collections.Generic;`이 없으면 더한다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~DeploymentViewModel"`
Expected: PASS — 기존 `DeploymentViewModelTests`도 그대로 통과

`MergeCommand_OffersComparison_WhenMerged`가 차이 검사를 부르지 않으면 `InlineBackgroundScheduler`가 `Busy`를 되돌린 뒤 `CompareCommand.CanExecute`가 참인지 본다 — `EndBusy()`가 `ApplyMergeOutcome` 앞에 있어야 한다.

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/UnmergedBranchItemViewModel.cs src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelMergeTests.cs
git commit -m "feat(vsix): 배포 패널에서 브랜치를 병합하고 차이 검사로 잇는다"
```

---

### Task 7: 병합 영역 XAML

CI가 렌더링을 검증하지 않는다. 빌드 통과까지만 여기서 확인하고, 실제 모습은 Task 8의 SSMS 확인으로 본다.

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (배포 패널, `<Grid DataContext="{Binding Deployment}">` 안)

**Interfaces:**
- Consumes: Task 6의 속성·명령
- Produces: 없음

- [ ] **Step 1: 행을 하나 더한다**

`<Grid DataContext="{Binding Deployment}">`의 `RowDefinitions` 맨 앞에 `<RowDefinition Height="Auto" />`를 더하고, 기존 요소의 `Grid.Row`를 모두 1씩 올린다(0→1, 1→2, 2→3, 3→4, 4→5).

- [ ] **Step 2: 병합 영역을 Row 0에 둔다**

```xml
                <!--
                    병합 영역. 목적지는 매핑의 고정 브랜치 하나라 고르는 칸이 없다(스펙 2.1).
                    차이 검사 위에 두는 것은 순서가 곧 절차이기 때문이다 - 병합하고, 검사하고, 배포한다.
                -->
                <Expander Grid.Row="0" Margin="5,5,5,0" IsExpanded="True">
                    <Expander.Header>
                        <TextBlock>
                            <Run Text="병합 — 목적지:"/>
                            <Run Text="{Binding MergeTargetBranch, Mode=OneWay}" FontWeight="SemiBold"/>
                        </TextBlock>
                    </Expander.Header>
                    <Grid Margin="0,4,0,0">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="140" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>

                        <WrapPanel Grid.Row="0" Margin="0,0,0,4">
                            <Button Content="병합할 브랜치 확인" Command="{Binding LoadBranchesCommand}" Margin="0,0,5,0" Padding="10,3"
                                    ToolTip="원격에서 가져온 뒤, 목적지에 아직 병합되지 않은 브랜치를 보여 줍니다. develop과 master는 목록에 나오지 않습니다."/>
                            <Button Content="병합" Command="{Binding MergeCommand}" Margin="0,0,5,0" Padding="10,3"
                                    ToolTip="고른 브랜치를 목적지에 병합 커밋으로 병합하고 원격에 올립니다. 충돌이 있으면 누를 수 없습니다."/>
                        </WrapPanel>

                        <Grid Grid.Row="1">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="5" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>

                            <!-- 목록은 작업 중에 잠근다. 명령이 없어 CanExecute가 막지 못한다(아래 차이 목록과 같은 이유). -->
                            <ListView Grid.Column="0" IsEnabled="{Binding Busy.IsNotBusy}"
                                      ItemsSource="{Binding UnmergedBranches}"
                                      SelectedItem="{Binding SelectedBranch, Mode=TwoWay}">
                                <ListView.View>
                                    <GridView>
                                        <GridViewColumn Header="브랜치" Width="140" DisplayMemberBinding="{Binding Name}"/>
                                        <GridViewColumn Header="작성자" Width="90" DisplayMemberBinding="{Binding AuthorText}"/>
                                        <GridViewColumn Header="마지막 커밋" Width="120" DisplayMemberBinding="{Binding TimeText}"/>
                                        <GridViewColumn Header="커밋" Width="50" DisplayMemberBinding="{Binding CommitCountText}"/>
                                        <GridViewColumn Header="테스트 반영" Width="80" DisplayMemberBinding="{Binding InDevelopText}"/>
                                    </GridView>
                                </ListView.View>
                            </ListView>

                            <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch"/>

                            <TextBox Grid.Column="2" IsReadOnly="True" TextWrapping="NoWrap"
                                     VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                                     FontFamily="Consolas"
                                     Background="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowBackgroundKey}}"
                                     Foreground="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowTextKey}}"
                                     Text="{Binding PreviewText, Mode=OneWay}"/>
                        </Grid>
                    </Grid>
                </Expander>
```

"테스트 반영" 열은 숨기지 않는다. 목적지가 `develop`이면 값이 빈 문자열이라 빈 열로 보인다 — `GridViewColumn`에는 `Visibility`가 없고, 열을 코드 비하인드로 빼는 것은 이 한 칸을 위해 과하다.

- [ ] **Step 3: 빌드한다**

Run: `dotnet build DBVC.slnx`
Expected: 경고 증가 없이 성공

- [ ] **Step 4: Vsix 테스트를 돌린다 (XAML 픽스처가 있다)**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS. `ViewChangesControlFixtures`가 행 번호를 검사해 실패하면 그 픽스처의 기대값을 새 행 배치에 맞춘다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml
git commit -m "feat(vsix): 배포 패널 위에 병합 영역을 그린다"
```

---

### Task 8: 문서·버전·전체 검증

**Files:**
- Modify: `README.md`
- Modify: `docs/user-guide.html`
- Modify: `docs/setup-checklist.md`
- Modify: `docs/rollout-announcement.md`
- Modify: `docs/team-rollout-backlog.md`
- Modify: `docs/superpowers/specs/2026-09-10-dbvc-branch-operations-design.md`
- Modify: `docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md`
- Modify: `docs/superpowers/specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md` (3.5의 기본 버튼 문장)
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest`

**Interfaces:**
- Consumes: 전 태스크의 동작
- Produces: 없음

- [ ] **Step 1: 전체 테스트를 돌린다**

Run:
```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Core.Tests -f net48
dotnet test tests/DBVC.Vsix.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests -f net48
```
Expected: 전부 PASS. 실패가 있으면 문서 작업 전에 고친다.

- [ ] **Step 2: 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest`의 `<Identity ... Version="0.7.2"`를 `Version="0.8.0"`으로 바꾼다. `DbvcVersionTests`가 버전 문자열을 고정해 두었으면 함께 고친다.

- [ ] **Step 3: 설계 문서 둘에 번복·정정 표시를 단다**

`docs/superpowers/specs/2026-09-10-dbvc-branch-operations-design.md`의 `### 2.4 병합·충돌·삭제는 그대로 밖이다` 바로 아래:

```markdown
> **2026-09-17 번복 (병합만).** 배포·감사 클론에서 고정 브랜치로 병합하는 기능을 넣었다 —
> [배포·감사 클론 병합 설계](2026-09-17-dbvc-merge-in-deploy-clone-design.md). 개발자가 `develop`에,
> DBA가 `master`에 직접 Push할 권한이 있다는 것이 확인되어 GitLab MR을 거칠 이유가 사라졌다.
> 충돌 해결과 브랜치 삭제는 여전히 도구 밖이다.
```

`docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md`의 `**경고 A — 미승격 변경을 품고 간다.**` 문단 바로 위:

```markdown
> **2026-09-17 정정.** 아래의 `P@develop != P@master` 판정은 쓰지 않는다 — 테스트를 거친 브랜치는
> `develop`에 자기 변경이 들어 있어 운영 병합마다 모든 객체에서 뜬다. 경고 A는 커밋 시점이 아니라
> **운영(`master`) 병합 미리보기**에서 줄 단위 겹침으로 판정한다
> ([배포·감사 클론 병합 설계](2026-09-17-dbvc-merge-in-deploy-clone-design.md) 3.4).
```

`docs/superpowers/specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md` 3.5의 4번 항목에서 `(기본 버튼 "차이 검사 시작")`을 아래로 바꾼다 — 구현이 기존 `IUserNotifier.Confirm`(기본 선택 취소)을 그대로 쓰기 때문이다.

```markdown
기존 확인 대화상자(`IUserNotifier.Confirm`)로 묻는다. 기본 선택이 취소인 것은 그대로 둔다 —
잘못 눌러도 검사를 시작하지 않을 뿐 잃는 것이 없다.
```

- [ ] **Step 4: 백로그를 고친다**

`docs/team-rollout-backlog.md`:
- 표의 `**P2** | 11번 미승격 변경 경고(경고 A)` 행을 `~~P2~~ | ~~11번 미승격 변경 경고(경고 A)~~ | 0.8.0 [배포·감사 클론 병합 설계](superpowers/specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md) 3.4로 닫았다 — 운영 병합 미리보기에서 줄 단위 겹침으로 판정한다`로 바꾼다.
- `### 6. 병합 충돌 해결` 절 첫 줄을 `도구 밖이다. 병합 자체는 0.8.0부터 배포·감사 클론에서 되지만(충돌이 없을 때만), 충돌은 GitLab이나 외부 도구로 푼다.`로 바꾼다.
- `### 11.` 절 끝에 `**0.8.0으로 닫았다.** 착수 전에 다시 보기로 한 "병합 목적지를 판정에 넣을지"는 넣는 쪽으로 정했다 — 판정 자리를 커밋에서 운영 병합 미리보기로 옮겼다.`를 더한다.
- 머리말 마지막 문단 뒤에 한 문단: `2026-09-17에 **배포·감사 클론 병합**을 0.8.0으로 넣었다. 팀이 GitLab 없이 DBVC 안에서 병합하기를 원했고, 개발자는 \`develop\`에·DBA는 \`master\`에 직접 Push할 권한이 있다는 것이 확인되었다. **보호 브랜치를 "MR로만 병합"으로 바꾸면 이 기능이 멈춘다.**`

- [ ] **Step 5: 운영 문서를 고친다**

`docs/setup-checklist.md` — 브랜치 운영 규칙 절(`**`hotfix/*`도 `feature/*`와 같은 정책이다.**`가 있는 절) 끝에 더한다.

```markdown
**병합은 배포·감사 클론에서 한다(0.8.0~).** 테스트 클론에서 `develop`으로, 운영 클론에서 `master`로.
목적지는 클론의 고정 브랜치 하나다.

- **`master`에 병합하기 전까지 티켓 브랜치를 지우지 않는다.** 운영 병합 미리보기의 "확인 필요"(경고 A)는
  아직 운영에 나가지 않은 다른 브랜치와 줄을 비교해 만든다. 지운 브랜치는 비교 대상에서 빠진다.
- **충돌을 `develop`을 티켓 브랜치로 병합해서 풀지 않는다.** 그 브랜치를 운영에 병합할 때 `develop`
  전체가 딸려 간다.
- **보호 브랜치를 "MR로만 병합"으로 바꾸면 병합이 원격 거부로 실패한다.** 개발자는 `develop`에,
  DBA는 `master`에 직접 Push할 권한이 있어야 한다.
```

같은 파일의 SSMS 수동 검증 절 끝에 더한다.

```markdown
#### 배포·감사 클론 병합 (0.8.0)

- [ ] 테스트 클론에서 [병합할 브랜치 확인] → 목록에 `develop`·`master`가 없는지
- [ ] 브랜치를 고르면 오른쪽에 바뀔 파일이 뜨는지
- [ ] [병합] → 확인 → GitLab에 병합 커밋이 보이고, DBVC 이력 탭 **종류**가 `병합`인지
- [ ] 병합 뒤 "차이 검사를 시작할까요?"에 확인 → 병합된 객체가 차이로 뜨는지
- [ ] 같은 줄을 다르게 고친 브랜치 → 미리보기에 충돌이 뜨고 [병합]이 잠기는지
- [ ] 운영 클론에서 같은 객체를 만진 브랜치 둘 → 미리보기에 "확인 필요"가 뜨고 확인 대화상자에도 실리는지
- [ ] 운영 클론 목록의 **테스트 반영** 열이 `develop`에 병합된 브랜치에만 `반영됨`인지
- [ ] 개발 클론에서는 병합 영역이 보이지 않는지
```

`docs/rollout-announcement.md` — 브랜치 규칙 절(`변경이 문제가 되는지는 **어느 병합 단계냐**로 갈린다.`가 있는 절) 끝에 위 `setup-checklist.md`의 규칙 목록 세 줄을 같은 문구로 더하고, 릴리스 노트 템플릿에 `### 병합` 소제목으로 "테스트·운영 배포 때 GitLab MR 대신 DBVC 배포 패널의 [병합할 브랜치 확인] → [병합]을 씁니다. 병합한 뒤에는 반드시 차이 검사로 DB에 반영하세요 — DB는 병합만으로 바뀌지 않습니다."를 더한다.

- [ ] **Step 6: 사용자 문서를 고친다**

`README.md` — 배포·감사 클론을 설명하는 절에 병합 영역 한 문단을 더한다: 목적지(고정 브랜치), 목록 규칙(`develop`/`master` 제외, Fetch 후 표시), 충돌 시 잠김, 운영 목적지의 "테스트 반영" 열과 "확인 필요", Push 실패 시 로컬 되돌림.

`docs/user-guide.html` — 테스트 배포·운영 배포 절차의 "GitLab에서 MR 병합" 단계를 "DBVC 배포 패널에서 [병합할 브랜치 확인] → 브랜치 선택 → [병합] → 차이 검사 시작"으로 바꾸고, 증상표에 두 줄을 더한다: "병합 버튼이 눌리지 않습니다 → 미리보기에 충돌이 있습니다. 브랜치 작성자가 풀어야 합니다", "병합이 원격 거부로 실패합니다 → 그 사이 누가 올렸으면 다시 시도하고, 반복되면 보호 브랜치 권한을 확인하세요".

바꾼 뒤 `grep -rn "GitLab.*MR\|MR.*병합" README.md docs/*.md docs/*.html`로 남은 "MR로 병합" 안내가 없는지 확인한다. 설계 문서(`docs/superpowers/`)의 과거 서술은 고치지 않는다.

- [ ] **Step 7: 커밋한다**

```bash
git add README.md docs/user-guide.html docs/setup-checklist.md docs/rollout-announcement.md docs/team-rollout-backlog.md docs/superpowers/specs/2026-09-10-dbvc-branch-operations-design.md docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md docs/superpowers/specs/2026-09-17-dbvc-merge-in-deploy-clone-design.md src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: 배포·감사 클론 병합을 가이드·체크리스트·백로그에 맞추고 0.8.0으로 올린다"
```

- [ ] **Step 8: 패키지를 만든다**

Run:
```powershell
dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release
dir src\DBVC.Vsix\bin\Release\net48\*.vsix
```
Expected: `.vsix`가 존재한다. **여기서 "동작한다"고 말하지 않는다** — Step 5의 SSMS 수동 검증 항목은 사용자가 SSMS 21에서 밟아야 한다.
