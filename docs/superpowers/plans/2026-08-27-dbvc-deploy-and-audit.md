# DBVC 배포와 감사 (3차) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 배포·감사용 저장소에서 대상 DB와 브랜치의 차이를 검사하고, 실행 가능한 배포 스크립트를 만들어 주며, 개발용이 아닌 대상에서는 커밋·트리거 설치를 막는다.

**Architecture:** 차이 검사는 새 엔진을 만들지 않는다. `SmoManager`의 추출 루프가 이미 객체마다 임시 스테이징 파일과 저장소 파일의 바이트를 비교하고 있으므로(`PublishIfChanged`/`HasSameBytes`), 그 루프에서 **반영 단계만 델리게이트로 갈아 끼워** 파일을 쓰지 않고 판정만 모은다. 저장소는 한 글자도 바뀌지 않으므로 되돌릴 것이 없다. mode별 허용 동작은 순수 함수 `MappingPolicy` 하나가 정하고 ViewModel과 Core가 같은 함수를 본다. 화면은 `ViewChangesViewModel`(1592줄)에 얹지 않고 `DeploymentViewModel`로 빼되, 진행 표시와 취소 버튼은 공유 `BusyState` 하나를 본다.

**Tech Stack:** C# / .NET (Core는 `netstandard2.0;net48` 멀티타깃, VSIX는 `net48` WPF+MVVM), NUnit 4 + Moq, LibGit2Sharp 0.32.0, SMO 171.30.0, Microsoft.Data.SqlClient 5.1.5

**Spec:** `docs/superpowers/specs/2026-08-27-dbvc-deploy-and-audit-design.md`
(상위 설계: `docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md`)

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip, 컬럼명 포함. Core는 상태를 영어 식별자로 다루고 화면 계층에서만 한국어로 옮긴다.
- **주석은 "왜"만 적는다.** 한국어 평서문, 기존 문체(함정과 근거를 남기는)를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): 메모리 전용 자격증명 저장소를 더한다`
- **TDD**: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋. 테스트 이름은 영어 `Method_Result_WhenCondition`.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `SqlManagementObjects 171.30.0` 고정. 테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다 — 전이 참조로만 받는다.
- **Core는 `netstandard2.0`도 타깃한다.** `File.Move`의 덮어쓰기 오버로드, `record`, 파일 범위 namespace 같은 최신 문법을 쓰지 않는다. 기존 파일의 블록 namespace 스타일을 따른다.
- **`DBVC.Vsix.csproj`의 `RegisterWithCodebase`·`VSSDK.targets` Import·`IncludeCoreDependenciesInVsix`·매니페스트의 `InstallationTarget`을 건드리지 않는다.**
- 빌드·테스트: `dotnet build DBVC.slnx`, `dotnet test tests/DBVC.Core.Tests`, `dotnet test tests/DBVC.Vsix.Tests`. 단일 테스트는 `--filter "FullyQualifiedName~Name"`.
- **CI가 검증하지 않는 것:** WPF 렌더링, VS 패키지 로딩, `.vsct` 메뉴 등록, SSMS 통합, 실제 DB 연결. Task 17이 그 목록이다.

---

## Task 1: `MappingPolicy` — mode별 허용 동작 판정

**Files:**
- Create: `src/DBVC.Core/MappingPolicy.cs`
- Create: `src/DBVC.Core/OperationNotAllowedException.cs`
- Test: `tests/DBVC.Core.Tests/MappingPolicyTests.cs`

**Interfaces:**
- Consumes: `DBVC.Core.Models.MappingMode` (`Write = 0`, `Deploy = 1`, `Audit = 2`) — 이미 있다.
- Produces:
  - `enum DbvcOperation { InstallTracker, Extract, Commit, Push, Compare, GenerateScript }`
  - `static bool MappingPolicy.IsAllowed(MappingMode mode, DbvcOperation operation)`
  - `static string MappingPolicy.BuildDeniedMessage(MappingMode mode, DbvcOperation operation)`
  - `class OperationNotAllowedException : Exception` — 생성자 `(MappingMode mode, DbvcOperation operation)`, 속성 `Mode`, `Operation`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/MappingPolicyTests.cs`:

```csharp
using System;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// mode는 실수를 막는 장치다. 판정이 두 곳에 생기면 화면과 Core가 갈라지고,
    /// 갈라진 쪽이 이기는 날 배포 클론에서 커밋이 나간다.
    /// </summary>
    [TestFixture]
    public class MappingPolicyTests
    {
        [TestCase(DbvcOperation.InstallTracker)]
        [TestCase(DbvcOperation.Extract)]
        [TestCase(DbvcOperation.Commit)]
        [TestCase(DbvcOperation.Push)]
        public void IsAllowed_ReturnsTrue_WhenModeIsWrite(DbvcOperation operation)
        {
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Write, operation), Is.True);
        }

        [TestCase(MappingMode.Deploy, DbvcOperation.InstallTracker)]
        [TestCase(MappingMode.Deploy, DbvcOperation.Extract)]
        [TestCase(MappingMode.Deploy, DbvcOperation.Commit)]
        [TestCase(MappingMode.Deploy, DbvcOperation.Push)]
        [TestCase(MappingMode.Audit, DbvcOperation.InstallTracker)]
        [TestCase(MappingMode.Audit, DbvcOperation.Extract)]
        [TestCase(MappingMode.Audit, DbvcOperation.Commit)]
        [TestCase(MappingMode.Audit, DbvcOperation.Push)]
        public void IsAllowed_ReturnsFalse_WhenModeIsNotWrite(MappingMode mode, DbvcOperation operation)
        {
            Assert.That(MappingPolicy.IsAllowed(mode, operation), Is.False);
        }

        [Test]
        public void IsAllowed_DeniesCompare_WhenModeIsWrite()
        {
            // 개발 DB는 정의상 master + 진행 중인 모든 feature 상태다.
            // 브랜치와의 차이 전체가 잡음이라 검사 자체가 의미를 갖지 않는다.
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Write, DbvcOperation.Compare), Is.False);
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Deploy, DbvcOperation.Compare), Is.True);
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Audit, DbvcOperation.Compare), Is.True);
        }

        [TestCase(MappingMode.Write)]
        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void IsAllowed_AllowsGenerateScript_InEveryMode(MappingMode mode)
        {
            // 결과물은 동작이 아니라 텍스트 파일이다. 막으면 안전이 늘지 않고 분기만 는다.
            Assert.That(MappingPolicy.IsAllowed(mode, DbvcOperation.GenerateScript), Is.True);
        }

        [Test]
        public void IsAllowed_Throws_WhenOperationIsUnknown()
        {
            // 새 동작이 생겼는데 표를 고치지 않으면 조용히 허용되는 것이 아니라 시끄럽게 죽어야 한다.
            Assert.Throws<InvalidOperationException>(
                () => MappingPolicy.IsAllowed(MappingMode.Write, (DbvcOperation)999));
        }

        [Test]
        public void BuildDeniedMessage_NamesBothModeAndOperationInKorean()
        {
            var message = MappingPolicy.BuildDeniedMessage(MappingMode.Audit, DbvcOperation.Commit);

            Assert.That(message, Does.Contain("감사"));
            Assert.That(message, Does.Contain("커밋"));
        }

        [Test]
        public void OperationNotAllowedException_CarriesTheDeniedMessage()
        {
            var ex = new OperationNotAllowedException(MappingMode.Deploy, DbvcOperation.Push);

            Assert.That(ex.Mode, Is.EqualTo(MappingMode.Deploy));
            Assert.That(ex.Operation, Is.EqualTo(DbvcOperation.Push));
            Assert.That(ex.Message, Is.EqualTo(MappingPolicy.BuildDeniedMessage(MappingMode.Deploy, DbvcOperation.Push)));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicyTests"`
Expected: 컴파일 실패 — `MappingPolicy`, `DbvcOperation`, `OperationNotAllowedException`이 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/MappingPolicy.cs`:

```csharp
using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>DBVC가 대상에 대해 할 수 있는 동작. mode 판정의 축이다.</summary>
    public enum DbvcOperation
    {
        /// <summary>DDL 트리거와 ChangeLog 설치·갱신.</summary>
        InstallTracker,

        /// <summary>저장소에 파일을 쓰는 추출.</summary>
        Extract,

        Commit,
        Push,

        /// <summary>대상 DB와 브랜치의 차이 검사. 저장소에 쓰지 않는다.</summary>
        Compare,

        GenerateScript
    }

    /// <summary>
    /// mode별 허용 동작을 정하는 유일한 자리. 순수 함수라 DB·Git 없이 테스트된다.
    ///
    /// 화면과 Core가 각자 판정하면 언젠가 갈라지고, 갈라진 쪽이 이기는 날 배포 클론에서
    /// 커밋이 나간다. 그래서 ViewModel의 CanExecute와 Core API 진입부가 모두 이 함수를 부른다.
    ///
    /// 이것은 실수를 막는 장치이지 보안 장치가 아니다 — mappings.json은 사용자가 편집할 수
    /// 있는 로컬 파일이다. 실제 권한은 SQL Server 계정 권한이 담당한다.
    /// </summary>
    public static class MappingPolicy
    {
        public static bool IsAllowed(MappingMode mode, DbvcOperation operation)
        {
            switch (operation)
            {
                case DbvcOperation.InstallTracker:
                case DbvcOperation.Extract:
                case DbvcOperation.Commit:
                case DbvcOperation.Push:
                    // 테스트 DB에서 나온 추출물은 새 변경이 아니라 배포 결과다. 커밋하면
                    // develop에 자기 자신을 되먹이고, 배포가 덜 된 상태였다면 그 상태를
                    // 정답으로 굳혀 버린다.
                    return mode == MappingMode.Write;

                case DbvcOperation.Compare:
                    // 개발 DB는 master + 진행 중인 모든 feature 상태라 차이 전체가 잡음이다.
                    return mode != MappingMode.Write;

                case DbvcOperation.GenerateScript:
                    return true;

                default:
                    // 새 동작이 생겼는데 이 표를 고치지 않으면 조용히 허용되는 대신 죽어야 한다.
                    throw new InvalidOperationException($"처리되지 않은 {nameof(DbvcOperation)}: {operation}");
            }
        }

        public static string BuildDeniedMessage(MappingMode mode, DbvcOperation operation)
        {
            return $"이 대상은 '{GetModeName(mode)}' 용도로 등록되어 있어 {GetOperationName(operation)}을(를) 할 수 없습니다. " +
                   "용도를 바꾸려면 저장소를 다시 연결하세요.";
        }

        private static string GetModeName(MappingMode mode)
        {
            switch (mode)
            {
                case MappingMode.Write: return "개발";
                case MappingMode.Deploy: return "배포";
                case MappingMode.Audit: return "감사";
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(MappingMode)}: {mode}");
            }
        }

        private static string GetOperationName(DbvcOperation operation)
        {
            switch (operation)
            {
                case DbvcOperation.InstallTracker: return "변경 추적 설치";
                case DbvcOperation.Extract: return "저장소 추출";
                case DbvcOperation.Commit: return "커밋";
                case DbvcOperation.Push: return "Push";
                case DbvcOperation.Compare: return "차이 검사";
                case DbvcOperation.GenerateScript: return "배포 스크립트 생성";
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(DbvcOperation)}: {operation}");
            }
        }
    }
}
```

`src/DBVC.Core/OperationNotAllowedException.cs`:

```csharp
using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// mode가 허용하지 않는 동작을 불렀다.
    ///
    /// 조용한 false로 돌려보내지 않는 이유는, 버튼을 죽이는 것만으로는 나중에 코드 경로가
    /// 하나 늘 때 조용히 다시 열리기 때문이다. 메시지는 그대로 사용자에게 보인다.
    /// </summary>
    public class OperationNotAllowedException : Exception
    {
        public OperationNotAllowedException(MappingMode mode, DbvcOperation operation)
            : base(MappingPolicy.BuildDeniedMessage(mode, operation))
        {
            Mode = mode;
            Operation = operation;
        }

        public MappingMode Mode { get; }
        public DbvcOperation Operation { get; }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicyTests"`
Expected: PASS (13개)

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/MappingPolicy.cs src/DBVC.Core/OperationNotAllowedException.cs tests/DBVC.Core.Tests/MappingPolicyTests.cs
git commit -m "feat(core): mode별 허용 동작을 한 곳에서 판정한다"
```

---

## Task 2: 작업 트리가 더러우면 배포·감사를 차단한다

**Files:**
- Modify: `src/DBVC.Core/Models/RepositoryState.cs` (`RepositoryBlockReason`에 값 추가)
- Modify: `src/DBVC.Core/RepositoryStateEvaluator.cs`
- Modify: `src/DBVC.Core/GitManager.cs:146-171` (`GetRepositoryState`)
- Test: `tests/DBVC.Core.Tests/RepositoryStateEvaluatorTests.cs` (추가), `tests/DBVC.Core.Tests/GitManagerTests.cs` (추가)

**Interfaces:**
- Consumes: `MappingMode` (Task 1과 무관하게 이미 있다), `RepositoryStateEvaluator.Evaluate/BuildMessage`
- Produces:
  - `RepositoryBlockReason.WorkingTreeDirty = 4`
  - `RepositoryStateEvaluator.Evaluate(string? currentBranch, bool isDetached, string? pendingOperation, string? expectedBranch, MappingMode mode = MappingMode.Write, bool hasUncommittedChanges = false)` — **새 인자는 뒤에 붙는 선택 인자다.** 기존 호출부와 기존 테스트가 그대로 컴파일된다.
  - `RepositoryStateEvaluator.BuildMessage`는 서명이 그대로다. `WorkingTreeDirty` case만 는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/RepositoryStateEvaluatorTests.cs` 끝(마지막 `}` 두 개 앞)에 추가:

```csharp
        // ---------- 작업 트리 더러움: 비교 기준이 브랜치가 아니게 된다 ----------

        [Test]
        public void Evaluate_ReturnsWorkingTreeDirty_WhenDeployCloneHasUncommittedChanges()
        {
            // 비교 기준은 "브랜치의 내용"인데 실제로 읽는 것은 작업 트리 파일이다.
            // 미커밋 편집이 있으면 그것이 브랜치인 척한다.
            var reason = RepositoryStateEvaluator.Evaluate(
                "develop", false, null, "develop", MappingMode.Deploy, hasUncommittedChanges: true);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.WorkingTreeDirty));
        }

        [Test]
        public void Evaluate_ReturnsWorkingTreeDirty_WhenAuditCloneHasUncommittedChanges()
        {
            var reason = RepositoryStateEvaluator.Evaluate(
                "master", false, null, "master", MappingMode.Audit, hasUncommittedChanges: true);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.WorkingTreeDirty));
        }

        [Test]
        public void Evaluate_IgnoresDirtyWorkingTree_WhenModeIsWrite()
        {
            // 개발 클론에서 더러운 트리는 추출 직후의 정상 상태다. 여기서 막으면
            // 새로고침한 사람이 전부 차단된다.
            var reason = RepositoryStateEvaluator.Evaluate(
                "feature/x", false, null, null, MappingMode.Write, hasUncommittedChanges: true);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_PrefersBranchMismatch_OverWorkingTreeDirty()
        {
            // enum의 순서가 곧 우선순위다. 브랜치가 틀린 채로 "커밋되지 않은 변경이 있습니다"를
            // 띄우면 사용자가 커밋하거나 되돌린 뒤에야 진짜 이유를 만난다.
            var reason = RepositoryStateEvaluator.Evaluate(
                "feature/x", false, null, "develop", MappingMode.Deploy, hasUncommittedChanges: true);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.BranchMismatch));
        }

        [Test]
        public void BuildMessage_ExplainsWhatToDo_WhenWorkingTreeIsDirty()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.WorkingTreeDirty, "develop", "develop", null);

            Assert.That(message, Is.Not.Null);
            Assert.That(message, Does.Contain("커밋되지 않은"));
        }
```

파일 맨 위 `using`에 `DBVC.Core.Models`가 이미 있는지 확인한다(있다).

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RepositoryStateEvaluatorTests"`
Expected: 컴파일 실패 — `RepositoryBlockReason.WorkingTreeDirty`가 없고 `Evaluate`가 6개 인자를 받지 않는다.

- [ ] **Step 3: 구현한다**

`src/DBVC.Core/Models/RepositoryState.cs`의 `RepositoryBlockReason`에 추가:

```csharp
        /// <summary>매핑이 고정한 브랜치와 다르다.</summary>
        BranchMismatch = 3,

        /// <summary>
        /// 배포·감사 클론에 커밋되지 않은 변경이 있다. 비교 기준이 브랜치가 아니게 된다.
        /// 개발 클론(write)에서는 정상 상태이므로 발동하지 않는다.
        /// </summary>
        WorkingTreeDirty = 4
```

`src/DBVC.Core/RepositoryStateEvaluator.cs`:

```csharp
        public static RepositoryBlockReason Evaluate(
            string? currentBranch,
            bool isDetached,
            string? pendingOperation,
            string? expectedBranch,
            MappingMode mode = MappingMode.Write,
            bool hasUncommittedChanges = false)
        {
```

기존 본문은 그대로 두고, 마지막 `return string.Equals(...)` 를 다음으로 바꾼다:

```csharp
            if (!string.Equals(currentBranch, expectedBranch, StringComparison.OrdinalIgnoreCase))
            {
                return RepositoryBlockReason.BranchMismatch;
            }

            // 마지막에 본다. enum의 순서가 곧 우선순위이고, 이것이 가장 약한 사유다 —
            // 브랜치가 틀린 채로 "커밋되지 않은 변경이 있습니다"를 띄우면 사용자가
            // 그것을 정리한 뒤에야 진짜 이유를 만난다.
            return DeniesDirtyWorkingTree(mode, hasUncommittedChanges)
                ? RepositoryBlockReason.WorkingTreeDirty
                : RepositoryBlockReason.None;
```

`expectedBranch`가 비어 있어 일찍 반환하던 자리(`if (string.IsNullOrWhiteSpace(expectedBranch))`)도 같은 검사를 타야 한다. 그 `return RepositoryBlockReason.None;`을 다음으로 바꾼다:

```csharp
            if (string.IsNullOrWhiteSpace(expectedBranch))
            {
                return DeniesDirtyWorkingTree(mode, hasUncommittedChanges)
                    ? RepositoryBlockReason.WorkingTreeDirty
                    : RepositoryBlockReason.None;
            }
```

클래스 안에 헬퍼를 더한다:

```csharp
        /// <summary>
        /// 개발 클론(write)에서 더러운 트리는 추출 직후의 정상 상태다. 배포·감사 클론은
        /// 커밋하지 않으므로 정상이면 항상 깨끗하고, 더럽다면 비교 기준을 믿을 수 없다.
        /// </summary>
        private static bool DeniesDirtyWorkingTree(MappingMode mode, bool hasUncommittedChanges)
        {
            return mode != MappingMode.Write && hasUncommittedChanges;
        }
```

`BuildMessage`의 `switch`에 case를 더한다(`default` 앞):

```csharp
                case RepositoryBlockReason.WorkingTreeDirty:
                    return "이 저장소에 커밋되지 않은 변경이 있어 비교 기준을 믿을 수 없습니다. " +
                           "배포·감사용 저장소는 브랜치의 내용 그대로여야 합니다. " +
                           "Git 클라이언트에서 변경을 되돌린 뒤 다시 시도하세요.";
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RepositoryStateEvaluatorTests"`
Expected: PASS (기존 + 새 5개)

- [ ] **Step 5: `GitManager`가 실제 값을 넘기게 한다**

`src/DBVC.Core/GitManager.cs`의 `GetRepositoryState`에서 `Evaluate` 호출 앞에 추가하고 호출을 고친다:

```csharp
            var detached = repo.Info.IsHeadDetached;
            var branch = detached ? null : repo.Head.FriendlyName;

            // write에서는 더러운 트리가 정상이므로 묻지 않는다. RetrieveStatus는 작업 트리
            // 전체를 훑어 객체 수에 비례하는 비용이 있고, 이 함수는 대상을 열 때마다 돈다.
            var dirty = mapping.Mode != MappingMode.Write
                && repo.RetrieveStatus(UntrackedInclusiveOptions).IsDirty;

            var reason = RepositoryStateEvaluator.Evaluate(
                branch, detached, operation, mapping.Branch, mapping.Mode, dirty);
```

- [ ] **Step 6: `GitManager` 테스트를 더한다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에 추가한다. 이 파일의 기존 픽스처가 만드는 임시 저장소·`ConfigManager` 헬퍼를 그대로 쓴다(파일 상단의 `SetUp`을 읽고 이름을 맞춘다). 매핑에 `Mode`/`Branch`를 넣으려면 `AddMapping(string, string, string)` 대신 `AddMapping(MappingConfig)` 오버로드를 쓴다:

```csharp
        [Test]
        public void GetRepositoryState_ReportsWorkingTreeDirty_WhenDeployCloneHasUncommittedFile()
        {
            // 배포 클론은 커밋하지 않으므로 정상이면 항상 깨끗하다. 더럽다는 것은
            // 누군가 외부에서 손을 댔다는 뜻이고, 그 상태로 비교하면 결과가 사실과 다르다.
            var repoPath = NewRepositoryWithCommit(out var config, out var git, MappingMode.Deploy);
            File.WriteAllText(Path.Combine(repoPath, "dirty.sql"), "-- 손댄 파일");

            var state = git.GetRepositoryState(Server, Database);

            Assert.That(state, Is.Not.Null);
            Assert.That(state!.BlockReason, Is.EqualTo(RepositoryBlockReason.WorkingTreeDirty));
            Assert.That(state.BlockMessage, Does.Contain("커밋되지 않은"));
        }

        [Test]
        public void GetRepositoryState_IgnoresDirtyWorkingTree_WhenModeIsWrite()
        {
            var repoPath = NewRepositoryWithCommit(out var config, out var git, MappingMode.Write);
            File.WriteAllText(Path.Combine(repoPath, "extracted.sql"), "-- 방금 추출한 파일");

            var state = git.GetRepositoryState(Server, Database);

            Assert.That(state, Is.Not.Null);
            Assert.That(state!.BlockReason, Is.EqualTo(RepositoryBlockReason.None));
        }
```

그리고 같은 파일에 헬퍼를 더한다. 저장소의 현재 브랜치 이름은 libgit2 기본값(`master`)이라 `Branch`를 그것에 맞춰 둔다 — 브랜치 불일치가 더러움보다 먼저 잡히므로, 맞춰 두지 않으면 이 테스트가 엉뚱한 사유를 확인하게 된다:

```csharp
        /// <summary>커밋 하나가 든 저장소와 그것을 가리키는 매핑을 만든다.</summary>
        private string NewRepositoryWithCommit(out ConfigManager config, out GitManager git, MappingMode mode)
        {
            var repoPath = NewTempDir();
            Repository.Init(repoPath);

            using (var repo = new Repository(repoPath))
            {
                File.WriteAllText(Path.Combine(repoPath, "seed.sql"), "-- seed");
                Commands.Stage(repo, "seed.sql");
                repo.Commit("seed", TestSignature, TestSignature);
            }

            string branch;
            using (var repo = new Repository(repoPath))
            {
                branch = repo.Head.FriendlyName;
            }

            config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(new MappingConfig
            {
                ServerName = Server,
                DatabaseName = Database,
                GitPath = repoPath,
                Mode = mode,
                Branch = branch
            });
            git = new GitManager(config);
            return repoPath;
        }
```

`Server`/`Database`/`TestSignature`/`NewTempDir`이 이 픽스처에 이미 있는지 확인하고, 없으면 `ScriptExporterTests.cs`의 것과 같은 모양으로 더한다.

- [ ] **Step 7: 전체 Core 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS. 기존 테스트가 깨지면 `Evaluate`의 새 인자가 선택 인자가 아닌 것이다.

- [ ] **Step 8: 커밋한다**

```bash
git add src/DBVC.Core/Models/RepositoryState.cs src/DBVC.Core/RepositoryStateEvaluator.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/RepositoryStateEvaluatorTests.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 배포·감사 저장소의 미커밋 변경을 차단 사유로 다룬다"
```

---

## Task 3: 비교 결과 모델과 "브랜치에만 있음" 순수 함수

**Files:**
- Create: `src/DBVC.Core/Models/SchemaDifference.cs`
- Create: `src/DBVC.Core/SchemaComparison.cs`
- Test: `tests/DBVC.Core.Tests/SchemaComparisonTests.cs`

**Interfaces:**
- Consumes: `ObjectPathConvention.TryParseRelativePath(string?, out string schema, out string objectType, out string objectName)`, `ObjectPathConvention.GetQualifiedName(string?, string)`
- Produces:
  - `enum ObjectDiffState { Modified, MissingInDatabase, MissingInBranch }`
  - `class SchemaDifference` — 생성자 `(string qualifiedName, string relativePath, string objectType, ObjectDiffState state)`, 읽기 전용 속성 넷
  - `class ComparisonResult` — `List<SchemaDifference> Differences`, `List<string> FailedObjects`, `int ComparedCount { get; set; }`, `bool IsInSync`
  - `static IReadOnlyList<SchemaDifference> SchemaComparison.FindMissingInDatabase(IEnumerable<string>? repositoryRelativePaths, ISet<string>? extractedRelativePaths)`
  - `static IReadOnlyList<string> SchemaComparison.EnumerateRepositoryScriptPaths(string repositoryPath)` — 파일 시스템에 닿는 얇은 어댑터

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SchemaComparisonTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 대상 DB를 훑으면 "브랜치에 있는데 DB에 없는 것"은 애초에 열거되지 않는다.
    /// 그것이 배포에서 가장 중요한 항목이므로 저장소 쪽에서 따로 찾아야 한다.
    /// </summary>
    [TestFixture]
    public class SchemaComparisonTests
    {
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dbvc_cmp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        [Test]
        public void FindMissingInDatabase_ReturnsPathsNotExtracted()
        {
            var repoPaths = new[] { "dbo/Tables/Users.sql", "dbo/StoredProcedures/GetUser.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };

            var missing = SchemaComparison.FindMissingInDatabase(repoPaths, extracted);

            Assert.That(missing.Count, Is.EqualTo(1));
            Assert.That(missing[0].RelativePath, Is.EqualTo("dbo/StoredProcedures/GetUser.sql"));
            Assert.That(missing[0].QualifiedName, Is.EqualTo("dbo.GetUser"));
            Assert.That(missing[0].ObjectType, Is.EqualTo("StoredProcedure"));
            Assert.That(missing[0].State, Is.EqualTo(ObjectDiffState.MissingInDatabase));
        }

        [Test]
        public void FindMissingInDatabase_ReturnsEmpty_WhenEverythingWasExtracted()
        {
            var repoPaths = new[] { "dbo/Tables/Users.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };

            Assert.That(SchemaComparison.FindMissingInDatabase(repoPaths, extracted), Is.Empty);
        }

        [Test]
        public void FindMissingInDatabase_IgnoresCase_WhenMatchingExtractedPaths()
        {
            // Windows 파일 시스템은 대소문자를 구분하지 않는다. 여기서 구분하면 같은 파일이
            // "DB에 없는 객체"로 보고된다.
            var repoPaths = new[] { "DBO/Tables/Users.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };

            Assert.That(SchemaComparison.FindMissingInDatabase(repoPaths, extracted), Is.Empty);
        }

        [Test]
        public void FindMissingInDatabase_SkipsPathsOutsideTheConvention()
        {
            // 저장소에 사람이 둔 잡다한 .sql이 "DB에 없는 객체"로 보고되면 안 된다.
            var repoPaths = new[] { "README.sql", "docs/notes.sql", "dbo/Tables/Users/extra.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Assert.That(SchemaComparison.FindMissingInDatabase(repoPaths, extracted), Is.Empty);
        }

        [Test]
        public void FindMissingInDatabase_NormalizesBackslashes()
        {
            var repoPaths = new[] { @"dbo\Views\ActiveUsers.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var missing = SchemaComparison.FindMissingInDatabase(repoPaths, extracted);

            Assert.That(missing.Count, Is.EqualTo(1));
            Assert.That(missing[0].RelativePath, Is.EqualTo("dbo/Views/ActiveUsers.sql"));
        }

        [Test]
        public void FindMissingInDatabase_ReturnsEmpty_WhenInputsAreNull()
        {
            Assert.That(SchemaComparison.FindMissingInDatabase(null, null), Is.Empty);
        }

        [Test]
        public void EnumerateRepositoryScriptPaths_ReturnsSlashSeparatedRelativePaths()
        {
            var root = NewTempDir();
            Directory.CreateDirectory(Path.Combine(root, "dbo", "Tables"));
            File.WriteAllText(Path.Combine(root, "dbo", "Tables", "Users.sql"), "-- t");

            var paths = SchemaComparison.EnumerateRepositoryScriptPaths(root);

            Assert.That(paths, Is.EquivalentTo(new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void EnumerateRepositoryScriptPaths_SkipsTheGitDirectory()
        {
            // .git 안에도 .sql이 들어갈 수 있다(훅 예제, 사용자가 둔 파일).
            // 그것이 "DB에 없는 객체"로 보고되면 목록이 통째로 신뢰를 잃는다.
            var root = NewTempDir();
            Directory.CreateDirectory(Path.Combine(root, ".git", "hooks"));
            File.WriteAllText(Path.Combine(root, ".git", "hooks", "sample.sql"), "-- x");

            Assert.That(SchemaComparison.EnumerateRepositoryScriptPaths(root), Is.Empty);
        }

        [Test]
        public void EnumerateRepositoryScriptPaths_ReturnsEmpty_WhenDirectoryDoesNotExist()
        {
            var missing = Path.Combine(Path.GetTempPath(), "dbvc_absent_" + Guid.NewGuid().ToString("N"));

            Assert.That(SchemaComparison.EnumerateRepositoryScriptPaths(missing), Is.Empty);
        }

        [Test]
        public void ComparisonResult_IsInSync_WhenThereAreNoDifferences()
        {
            var result = new ComparisonResult { ComparedCount = 12 };

            Assert.That(result.IsInSync, Is.True);

            result.Differences.Add(new SchemaDifference("dbo.Users", "dbo/Tables/Users.sql", "Table", ObjectDiffState.Modified));

            Assert.That(result.IsInSync, Is.False);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SchemaComparisonTests"`
Expected: 컴파일 실패 — `SchemaComparison`, `SchemaDifference`, `ComparisonResult`, `ObjectDiffState`가 없다.

- [ ] **Step 3: 모델을 만든다**

`src/DBVC.Core/Models/SchemaDifference.cs`:

```csharp
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>
    /// 대상 DB와 브랜치가 어긋난 방식. "같음"은 값이 없다 — 결과에 담지 않기 때문이다.
    /// 수천 개가 되고 화면에 쓸 데가 없으며, 개수는 <see cref="ComparisonResult.ComparedCount"/>가 말한다.
    /// </summary>
    public enum ObjectDiffState
    {
        /// <summary>양쪽에 있고 바이트가 다르다.</summary>
        Modified,

        /// <summary>브랜치에만 있다. 배포되지 않았다.</summary>
        MissingInDatabase,

        /// <summary>DB에만 있다. 커밋되지 않았거나 무단 추가다.</summary>
        MissingInBranch
    }

    /// <summary>객체 하나의 차이. 화면과 배포 스크립트 분류가 같은 것을 본다.</summary>
    public class SchemaDifference
    {
        public SchemaDifference(string qualifiedName, string relativePath, string objectType, ObjectDiffState state)
        {
            QualifiedName = qualifiedName;
            RelativePath = relativePath;
            ObjectType = objectType;
            State = state;
        }

        public string QualifiedName { get; }
        public string RelativePath { get; }

        /// <summary>SMO 타입명(<c>Table</c>, <c>StoredProcedure</c> 등). 분류의 축이다.</summary>
        public string ObjectType { get; }

        public ObjectDiffState State { get; }
    }

    /// <summary>
    /// 차이 검사 한 번의 결과. 저장소에는 아무것도 쓰지 않았으므로 되돌릴 것이 없다.
    /// </summary>
    public class ComparisonResult
    {
        public List<SchemaDifference> Differences { get; } = new List<SchemaDifference>();

        /// <summary>스크립팅에 실패해 판정하지 못한 객체. 차이가 아니라 "모른다"이다.</summary>
        public List<string> FailedObjects { get; } = new List<string>();

        /// <summary>대상 DB에서 훑은 객체 수. "n개 중 m개 차이"의 분모다.</summary>
        public int ComparedCount { get; set; }

        public bool IsInSync => Differences.Count == 0;
    }
}
```

- [ ] **Step 4: 순수 함수와 어댑터를 만든다**

`src/DBVC.Core/SchemaComparison.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 대상 DB를 훑는 것만으로는 "브랜치에 있는데 DB에 없는 것"을 찾을 수 없다 —
    /// 열거 자체가 DB에서 나오기 때문이다. 그것이 배포에서 가장 중요한 항목이므로
    /// 저장소 쪽에서 따로 찾는다.
    ///
    /// 판정은 파일 시스템에 닿지 않는 순수 함수로 두고 스캔만 어댑터가 한다.
    /// </summary>
    public static class SchemaComparison
    {
        public static IReadOnlyList<SchemaDifference> FindMissingInDatabase(
            IEnumerable<string>? repositoryRelativePaths,
            ISet<string>? extractedRelativePaths)
        {
            var missing = new List<SchemaDifference>();
            if (repositoryRelativePaths == null) return missing;

            foreach (var raw in repositoryRelativePaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var normalized = raw.Replace('\\', '/');

                // 규약 밖의 .sql은 객체가 아니다. 저장소에 사람이 둔 메모까지
                // "DB에 없는 객체"로 보고하면 목록이 통째로 신뢰를 잃는다.
                if (!ObjectPathConvention.TryParseRelativePath(normalized, out var schema, out var objectType, out var objectName))
                {
                    continue;
                }

                if (extractedRelativePaths != null && extractedRelativePaths.Contains(normalized)) continue;

                missing.Add(new SchemaDifference(
                    ObjectPathConvention.GetQualifiedName(schema, objectName),
                    normalized,
                    objectType,
                    ObjectDiffState.MissingInDatabase));
            }

            return missing;
        }

        /// <summary>
        /// 저장소의 `.sql`을 슬래시 구분 상대 경로로 모은다. 규약 판정은 하지 않는다 —
        /// 그것은 <see cref="FindMissingInDatabase"/>가 하고, 여기서도 하면 규칙이 두 곳에 생긴다.
        /// </summary>
        public static IReadOnlyList<string> EnumerateRepositoryScriptPaths(string repositoryPath)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath)) return paths;

            try
            {
                var root = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                foreach (var full in Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories))
                {
                    var relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var normalized = relative.Replace('\\', '/');

                    // .git 안에도 .sql이 들어갈 수 있다. Git의 내부 파일은 객체가 아니다.
                    if (normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)) continue;

                    paths.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                // 권한 문제로 일부를 못 읽는 것이 검사 전체를 죽이면 안 된다.
                // 다만 목록이 줄면 "브랜치에만 있음"이 빠지므로 흔적은 남긴다.
                Debug.WriteLine($"SchemaComparison.EnumerateRepositoryScriptPaths failed for '{repositoryPath}': {ex.Message}");
            }

            return paths;
        }
    }
}
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SchemaComparisonTests"`
Expected: PASS (10개)

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Core/Models/SchemaDifference.cs src/DBVC.Core/SchemaComparison.cs tests/DBVC.Core.Tests/SchemaComparisonTests.cs
git commit -m "feat(core): 브랜치에만 있는 객체를 저장소 쪽에서 찾는다"
```

---

## Task 4: 추출 루프에서 반영 단계를 델리게이트로 뽑는다

순수 리팩터링이다. **기존 `ScriptAll` 서명은 그대로 두고** 내부를 공유 루프로 옮긴다. `SmoManagerTests`의 기존 호출(9곳)이 한 줄도 바뀌지 않아야 한다 — 바뀐다면 서명을 건드린 것이다.

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs:221-278` (`ScriptAll`)
- Test: `tests/DBVC.Core.Tests/SmoManagerTests.cs` (추가)

**Interfaces:**
- Consumes: `ScriptTargetInfo.RelativePath`, `ExtractionProgress(int, int, string)`, `ScriptResult`
- Produces:
  - `internal static ScriptResult SmoManager.RunScriptingLoop(IEnumerable<ScriptTargetInfo> targets, string repositoryPath, Action<ScriptTargetInfo, string> scriptOne, Action<ScriptTargetInfo, string, string> onScripted, IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)`
    - `onScripted`의 인자는 `(target, stagingPath, outputPath)`. 스테이징 파일은 이 콜백이 끝나면 지워진다 — 콜백 안에서만 읽을 수 있다.
  - `internal static bool SmoManager.HasSameBytes(string stagingPath, string outputPath)` — `private`에서 `internal`로 올린다(Task 5가 부른다).
  - `ScriptAll`은 그대로 남고 `RunScriptingLoop`에 `PublishIfChanged`를 넘기는 얇은 래퍼가 된다.

- [ ] **Step 1: 공유 루프가 반영을 위임한다는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SmoManagerTests.cs`의 `ScriptAll` 영역 끝에 추가:

```csharp
        // ---------- RunScriptingLoop: 반영 단계를 갈아 끼울 수 있다 ----------

        [Test]
        public void RunScriptingLoop_DoesNotWriteToRepository_WhenPublishStepOnlyInspects()
        {
            // 차이 검사가 성립하는 유일한 근거다. 저장소에 한 글자라도 쓰면
            // 되돌리는 단계가 필요해지고, 그 단계가 실패하는 날 작업 트리가 망가진다.
            var root = NewTempDir();
            var targets = new[]
            {
                new ScriptTargetInfo { Schema = "dbo", Name = "Users", ObjectType = "Table" }
            };

            var seen = new List<string>();
            var result = SmoManager.RunScriptingLoop(
                targets,
                root,
                (t, stagingPath) => File.WriteAllText(stagingPath, $"-- {t.Name}"),
                (t, stagingPath, outputPath) => seen.Add(File.ReadAllText(stagingPath)));

            Assert.That(result.SucceededCount, Is.EqualTo(1));
            Assert.That(seen, Is.EquivalentTo(new[] { "-- Users" }));
            Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public void RunScriptingLoop_PassesTheConventionalOutputPath_EvenWhenNothingIsWritten()
        {
            // 비교는 "저장소의 이 경로에 파일이 있는가"를 물어야 하므로,
            // 파일을 쓰지 않더라도 규약 경로는 그대로 계산되어야 한다.
            var root = NewTempDir();
            var targets = new[]
            {
                new ScriptTargetInfo { Schema = "dbo", Name = "GetUser", ObjectType = "StoredProcedure" }
            };

            string? captured = null;
            SmoManager.RunScriptingLoop(
                targets,
                root,
                (t, stagingPath) => File.WriteAllText(stagingPath, "-- p"),
                (t, stagingPath, outputPath) => captured = outputPath);

            Assert.That(captured, Is.EqualTo(
                Path.Combine(root, "dbo", "StoredProcedures", "GetUser.sql")));
        }

        [Test]
        public void RunScriptingLoop_RecordsFailureAndContinues_WhenPublishStepThrows()
        {
            // 판정 하나가 터져도 나머지 객체의 판정은 나와야 한다. 기존 스크립팅 실패와
            // 같은 규칙이다 — 부분 결과가 없는 것보다 낫다.
            var root = NewTempDir();
            var targets = new[]
            {
                new ScriptTargetInfo { Schema = "dbo", Name = "A", ObjectType = "Table" },
                new ScriptTargetInfo { Schema = "dbo", Name = "B", ObjectType = "Table" }
            };

            var result = SmoManager.RunScriptingLoop(
                targets,
                root,
                (t, stagingPath) => File.WriteAllText(stagingPath, "-- x"),
                (t, stagingPath, outputPath) =>
                {
                    if (t.Name == "A") throw new InvalidOperationException("nope");
                });

            Assert.That(result.SucceededCount, Is.EqualTo(1));
            Assert.That(result.FailedObjects, Is.EquivalentTo(new[] { "dbo.A" }));
        }
```

`NewTempDir`이 이 픽스처에 없으면 `SchemaComparisonTests`의 것과 같은 모양으로 더한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: 컴파일 실패 — `RunScriptingLoop`가 없다.

- [ ] **Step 3: 루프를 옮긴다**

`src/DBVC.Core/SmoManager.cs`에서 기존 `ScriptAll`의 **본문 전체**를 `RunScriptingLoop`로 옮기고, `ScriptAll`은 래퍼로 남긴다:

```csharp
        /// <summary>
        /// 추출해서 저장소에 반영한다. <see cref="RunScriptingLoop"/>에 지금까지의 반영
        /// 방식(내용이 같으면 건드리지 않는 복사)을 넘기는 래퍼다.
        ///
        /// 서명을 그대로 두는 이유는 이 오버로드를 부르는 테스트가 여럿이고, 그것들이
        /// 검증하는 것은 "반영"의 규칙이지 루프의 구조가 아니기 때문이다.
        /// </summary>
        internal static ScriptResult ScriptAll(
            IEnumerable<ScriptTargetInfo> targets,
            string localGitPath,
            Action<ScriptTargetInfo, string> scriptOne,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return RunScriptingLoop(
                targets,
                localGitPath,
                scriptOne,
                (target, stagingPath, outputPath) => PublishIfChanged(stagingPath, outputPath),
                progress,
                cancellationToken);
        }

        /// <summary>
        /// 객체마다 스테이징에 뜬 뒤 <paramref name="onScripted"/>에게 넘긴다.
        /// 취소·진행률·객체별 실패 격리·스테이징 정리가 여기 한 벌만 있다.
        ///
        /// 추출과 차이 검사가 이 루프를 공유하는 것이 요점이다. 검사용으로 루프를 따로 쓰면
        /// 취소가 한쪽에만 붙거나 실패 격리가 갈라지는 일이 실제로 일어난다.
        /// </summary>
        /// <param name="onScripted">
        /// <c>(target, stagingPath, outputPath)</c>. <c>stagingPath</c>는 이 콜백이 돌아오면
        /// 지워지므로 콜백 안에서만 읽을 수 있다. <c>outputPath</c>는 규약이 정한 저장소 경로이며
        /// 파일이 실제로 있는지는 보장하지 않는다.
        /// </param>
        internal static ScriptResult RunScriptingLoop(
            IEnumerable<ScriptTargetInfo> targets,
            string repositoryPath,
            Action<ScriptTargetInfo, string> scriptOne,
            Action<ScriptTargetInfo, string, string> onScripted,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // ... 기존 ScriptAll 본문을 그대로 옮긴다. 딱 두 곳만 바꾼다:
            //   1) localGitPath  ->  repositoryPath
            //   2) PublishIfChanged(stagingPath, outputPath);  ->  onScripted(target, stagingPath, outputPath);
        }
```

`HasSameBytes`의 접근자를 `private static`에서 `internal static`으로 바꾼다. Task 5가 부른다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: PASS. **기존 `ScriptAll` 테스트 9개가 한 줄도 바뀌지 않은 채 통과해야 한다.** 하나라도 고쳐야 했다면 리팩터링이 아니라 동작을 바꾼 것이다 — 되돌리고 다시 한다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/SmoManager.cs tests/DBVC.Core.Tests/SmoManagerTests.cs
git commit -m "refactor(core): 추출 루프에서 반영 단계를 갈아 끼울 수 있게 한다"
```

---

## Task 5: `CompareWithRepository` — 저장소에 쓰지 않고 차이만 판정한다

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs`
- Modify: `src/DBVC.Core/Abstractions.cs` (`ISmoManager`)
- Test: `tests/DBVC.Core.Tests/SmoManagerTests.cs` (판정 조합만), `tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs` (Task 16에서 실제 DB로)

**Interfaces:**
- Consumes: Task 3의 `ComparisonResult`/`SchemaDifference`/`ObjectDiffState`/`SchemaComparison`, Task 4의 `RunScriptingLoop`·`HasSameBytes`, Task 1의 `MappingPolicy`·`OperationNotAllowedException`
- Produces:
  - `ISmoManager.CompareWithRepository(string serverName, string databaseName, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)` → `ComparisonResult?`
  - `internal static ComparisonResult SmoManager.BuildComparison(IReadOnlyList<SchemaDifference> scriptedDifferences, ISet<string> extractedPaths, IReadOnlyList<string> repositoryPaths, IReadOnlyList<string> failedObjects, int comparedCount)` — 순수 함수. DB 없이 테스트한다.

- [ ] **Step 1: 순수 조립 함수의 테스트를 쓴다**

`tests/DBVC.Core.Tests/SmoManagerTests.cs`에 추가:

```csharp
        // ---------- BuildComparison: 두 갈래의 차이를 한 목록으로 합친다 ----------

        [Test]
        public void BuildComparison_MergesScriptedDifferencesWithMissingInDatabase()
        {
            var scripted = new List<SchemaDifference>
            {
                new SchemaDifference("dbo.Users", "dbo/Tables/Users.sql", "Table", ObjectDiffState.Modified)
            };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };
            var repoPaths = new List<string> { "dbo/Tables/Users.sql", "dbo/Views/ActiveUsers.sql" };

            var result = SmoManager.BuildComparison(scripted, extracted, repoPaths, new List<string>(), comparedCount: 1);

            Assert.That(result.ComparedCount, Is.EqualTo(1));
            Assert.That(result.IsInSync, Is.False);
            Assert.That(result.Differences.Select(d => d.QualifiedName),
                Is.EquivalentTo(new[] { "dbo.Users", "dbo.ActiveUsers" }));
            Assert.That(result.Differences.Single(d => d.QualifiedName == "dbo.ActiveUsers").State,
                Is.EqualTo(ObjectDiffState.MissingInDatabase));
        }

        [Test]
        public void BuildComparison_ReportsInSync_WhenNothingDiffers()
        {
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };
            var repoPaths = new List<string> { "dbo/Tables/Users.sql" };

            var result = SmoManager.BuildComparison(
                new List<SchemaDifference>(), extracted, repoPaths, new List<string>(), comparedCount: 1);

            Assert.That(result.IsInSync, Is.True);
            Assert.That(result.ComparedCount, Is.EqualTo(1));
        }

        [Test]
        public void BuildComparison_CarriesFailedObjects_SeparatelyFromDifferences()
        {
            // 스크립팅에 실패한 객체는 "차이가 없다"가 아니라 "모른다"이다.
            // 차이 목록에 섞으면 사용자가 배포 대상으로 읽는다.
            var result = SmoManager.BuildComparison(
                new List<SchemaDifference>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new List<string>(),
                new List<string> { "dbo.Broken" },
                comparedCount: 1);

            Assert.That(result.Differences, Is.Empty);
            Assert.That(result.FailedObjects, Is.EquivalentTo(new[] { "dbo.Broken" }));

            // IsInSync는 차이만 본다. 실패가 있으면 화면이 따로 알린다.
            Assert.That(result.IsInSync, Is.True);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: 컴파일 실패 — `BuildComparison`이 없다.

- [ ] **Step 3: 조립 함수를 만든다**

`src/DBVC.Core/SmoManager.cs`에 추가:

```csharp
        /// <summary>
        /// 추출 루프가 모은 판정과 저장소 스캔이 찾은 "브랜치에만 있음"을 합친다.
        /// DB에 닿지 않으므로 조합 자체는 DB 없이 테스트된다.
        /// </summary>
        internal static ComparisonResult BuildComparison(
            IReadOnlyList<SchemaDifference> scriptedDifferences,
            ISet<string> extractedPaths,
            IReadOnlyList<string> repositoryPaths,
            IReadOnlyList<string> failedObjects,
            int comparedCount)
        {
            var result = new ComparisonResult { ComparedCount = comparedCount };
            result.Differences.AddRange(scriptedDifferences);
            result.Differences.AddRange(SchemaComparison.FindMissingInDatabase(repositoryPaths, extractedPaths));
            result.FailedObjects.AddRange(failedObjects);
            return result;
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: PASS

- [ ] **Step 5: `CompareWithRepository`를 만든다**

`ScriptObjectsDetailed`의 접속·열거 부분(`SmoManager.cs:79-104`)이 그대로 필요하다. **복사하지 말고**, 접속부터 `targets` 열거까지를 `private ScriptingSession? OpenScriptingSession(string serverName, string databaseName, List<string>? objectNames)`로 뽑아 셋(`ScriptObjectsDetailed`·`CompareWithRepository`·`ScriptObjectToText`)이 나눠 쓴다. 실패하면 `null`을 돌려준다 — 지금 `ScriptObjectsDetailed`가 `null`로 뭉개는 모든 자리와 같은 규칙이다.

세션이 노출하는 것은 셋뿐이다. **이 이름을 Task 6이 그대로 쓴다.**

```csharp
        /// <summary>접속·열거를 한 번만 하고 세 진입점이 나눠 쓴다. 복사하면 SetDefaultInitFields
        /// 튜닝이 한쪽에만 남아 다른 쪽이 객체당 수 초를 낸다.</summary>
        private sealed class ScriptingSession
        {
            public IEnumerable<ScriptTargetInfo> Targets { get; set; } = Enumerable.Empty<ScriptTargetInfo>();

            /// <summary>(target, stagingPath) — TextMode 해제와 Scripter 호출을 감싼다.</summary>
            public Action<ScriptTargetInfo, string> ScriptOne { get; set; } = (t, p) => { };

            /// <summary>매핑된 저장소 경로. 규약 경로 계산에만 쓰인다.</summary>
            public string RepositoryPath { get; set; } = string.Empty;
        }
```

`SmoManager.cs`에 추가:

```csharp
        public ComparisonResult? CompareWithRepository(
            string serverName,
            string databaseName,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null) return null;

            // 개발 클론에서 부르면 차이 전체가 잡음이다. 화면이 이미 막지만, 코드 경로가
            // 하나 늘 때 조용히 다시 열리지 않도록 여기서도 확인한다.
            if (!MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.Compare))
            {
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.Compare);
            }

            var session = OpenScriptingSession(serverName, databaseName, objectNames: null);
            if (session == null) return null;

            var differences = new List<SchemaDifference>();
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var scriptResult = RunScriptingLoop(
                session.Targets,
                mapping.GitPath,
                session.ScriptOne,
                (target, stagingPath, outputPath) =>
                {
                    extracted.Add(target.RelativePath);

                    // HasSameBytes는 대상이 없을 때도 false를 돌려주므로 존재 여부를 먼저 가른다.
                    // 두 경우는 사용자가 할 일이 완전히 다르다.
                    if (!File.Exists(outputPath))
                    {
                        differences.Add(new SchemaDifference(
                            target.QualifiedName, target.RelativePath, target.ObjectType, ObjectDiffState.MissingInBranch));
                    }
                    else if (!HasSameBytes(stagingPath, outputPath))
                    {
                        differences.Add(new SchemaDifference(
                            target.QualifiedName, target.RelativePath, target.ObjectType, ObjectDiffState.Modified));
                    }
                },
                progress,
                cancellationToken);

            return BuildComparison(
                differences,
                extracted,
                SchemaComparison.EnumerateRepositoryScriptPaths(mapping.GitPath),
                scriptResult.FailedObjects,
                scriptResult.SucceededCount + scriptResult.FailedObjects.Count);
        }
```

`OperationCanceledException`은 `RunScriptingLoop`에서 그대로 전파된다. `ScriptObjectsDetailed`와 같은 관례이므로 여기서 잡지 않는다.

- [ ] **Step 6: `ISmoManager`에 더한다**

`src/DBVC.Core/Abstractions.cs`의 `ISmoManager`에 추가:

```csharp
        /// <summary>
        /// 대상 DB와 저장소 작업 트리의 차이를 판정한다. <b>저장소에 아무것도 쓰지 않는다</b> —
        /// 그래서 실패하거나 취소해도 되돌릴 것이 없다.
        ///
        /// 매핑이 없거나 접속에 실패하면 <c>null</c>이다. mode가 허용하지 않으면
        /// <see cref="OperationNotAllowedException"/>을 던진다.
        /// </summary>
        ComparisonResult? CompareWithRepository(
            string serverName,
            string databaseName,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default);
```

- [ ] **Step 7: 빌드하고 전체 테스트를 돌린다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS. `ISmoManager`를 구현하는 테스트 더블이 `DBVC.Vsix.Tests`에 `Mock<ISmoManager>`로만 있으므로 Moq가 새 멤버를 자동으로 채운다 — 손댈 것이 없다.

- [ ] **Step 8: 커밋한다**

```bash
git add src/DBVC.Core/SmoManager.cs src/DBVC.Core/Abstractions.cs tests/DBVC.Core.Tests/SmoManagerTests.cs
git commit -m "feat(core): 저장소를 건드리지 않고 대상 DB와의 차이를 판정한다"
```

---

## Task 6: `ScriptObjectToText` — diff 본문용으로 객체 하나만 뜬다

비교 중에 뜬 텍스트는 그 자리에서 버려진다. 사용자가 목록에서 항목을 고르면 그 객체 하나를 다시 떠서 텍스트로 돌려준다.

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs`
- Modify: `src/DBVC.Core/Abstractions.cs` (`ISmoManager`)

**Interfaces:**
- Consumes: Task 5의 `OpenScriptingSession`, Task 4의 `RunScriptingLoop`
- Produces: `ISmoManager.ScriptObjectToText(string serverName, string databaseName, string qualifiedName)` → `string?`

- [ ] **Step 1: 구현한다**

이 메서드는 SMO 접속 없이는 아무것도 검증할 수 없으므로 단위 테스트를 쓰지 않는다 — 통합 테스트(Task 16)가 덮는다. 대신 **저장소에 쓰지 않는다**는 성질이 `RunScriptingLoop`를 쓰는 데서 구조적으로 나온다.

`src/DBVC.Core/SmoManager.cs`:

```csharp
        /// <summary>
        /// 객체 하나를 스크립팅해 텍스트로 돌려준다. 저장소에 쓰지 않는다 — diff 본문 전용이다.
        ///
        /// 비교(<see cref="CompareWithRepository"/>)가 뜬 텍스트를 들고 있지 않는 이유는
        /// 객체 수천 개분을 메모리에 쌓게 되기 때문이다. 사용자가 실제로 열어 보는 것은
        /// 한 번에 하나뿐이므로 그때 다시 뜬다.
        /// </summary>
        /// <returns>대상에 없거나 스크립팅에 실패하면 <c>null</c>.</returns>
        public string? ScriptObjectToText(string serverName, string databaseName, string qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName)) return null;

            var session = OpenScriptingSession(serverName, databaseName, new List<string> { qualifiedName });
            if (session == null) return null;

            string? text = null;

            // 저장소 경로는 쓰이지 않지만 규약 경로 계산에 필요하다. 임시 폴더를 넘겨도
            // 되지만 매핑 경로를 그대로 넘기는 편이 outputPath가 실제와 같아 헷갈리지 않는다.
            RunScriptingLoop(
                session.Targets,
                session.RepositoryPath,
                session.ScriptOne,
                (target, stagingPath, outputPath) => text = File.ReadAllText(stagingPath));

            return text;
        }
```

`ISmoManager`에 더한다:

```csharp
        /// <summary>
        /// 객체 하나의 현재 DDL을 텍스트로 읽는다. 저장소에 쓰지 않는다.
        /// 대상에 없거나 스크립팅에 실패하면 <c>null</c>이다.
        /// </summary>
        string? ScriptObjectToText(string serverName, string databaseName, string qualifiedName);
```

- [ ] **Step 2: 빌드한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공

- [ ] **Step 3: 커밋한다**

```bash
git add src/DBVC.Core/SmoManager.cs src/DBVC.Core/Abstractions.cs
git commit -m "feat(core): diff 본문을 위해 객체 하나를 텍스트로 뜬다"
```

---

## Task 7: Core 쓰기 API가 mode를 확인한다

**Files:**
- Modify: `src/DBVC.Core/StateTracker.cs` (`InitializeDatabase`)
- Modify: `src/DBVC.Core/SmoManager.cs` (`ScriptObjectsDetailed`)
- Modify: `src/DBVC.Core/GitManager.cs` (`CommitChanges`, `PushChanges`)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs` (추가)

**Interfaces:**
- Consumes: Task 1의 `MappingPolicy.IsAllowed`, `OperationNotAllowedException`
- Produces: 없음 — 기존 서명 그대로다. 던지는 예외만 는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에 추가(Task 2의 `NewRepositoryWithCommit` 헬퍼를 쓴다):

```csharp
        [Test]
        public void CommitChanges_Throws_WhenModeIsDeploy()
        {
            // 테스트 DB에서 나온 추출물은 새 변경이 아니라 배포 결과다. 커밋하면
            // develop에 자기 자신을 되먹이고, 배포가 덜 된 상태였다면 그것을 정답으로 굳힌다.
            var repoPath = NewRepositoryWithCommit(out _, out var git, MappingMode.Deploy);
            File.WriteAllText(Path.Combine(repoPath, "new.sql"), "-- x");

            var ex = Assert.Throws<OperationNotAllowedException>(
                () => git.CommitChanges(Server, Database, "메시지"));

            Assert.That(ex!.Operation, Is.EqualTo(DbvcOperation.Commit));
            Assert.That(ex.Message, Does.Contain("배포"));
        }

        [Test]
        public void PushChanges_Throws_WhenModeIsAudit()
        {
            NewRepositoryWithCommit(out _, out var git, MappingMode.Audit);

            var ex = Assert.Throws<OperationNotAllowedException>(
                () => git.PushChanges(Server, Database));

            Assert.That(ex!.Operation, Is.EqualTo(DbvcOperation.Push));
        }

        [Test]
        public void CommitChanges_Succeeds_WhenModeIsWrite()
        {
            var repoPath = NewRepositoryWithCommit(out _, out var git, MappingMode.Write);
            File.WriteAllText(Path.Combine(repoPath, "new.sql"), "-- x");

            var result = git.CommitChanges(Server, Database, "메시지");

            Assert.That(result.Committed, Is.True);
        }
```

`GitCommitResult`의 속성 이름은 `src/DBVC.Core/Models/GitCommitResult.cs`를 열어 확인하고 맞춘다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests"`
Expected: 두 테스트가 `OperationNotAllowedException`이 아니라 다른 결과로 실패한다.

- [ ] **Step 3: 네 곳에 같은 확인을 넣는다**

각 메서드가 매핑을 찾은 직후, 다른 일을 하기 전에 넣는다. `GitManager`는 `_configManager`가 nullable이므로 매핑이 없으면 지금처럼 기존 경로로 흘려보낸다 — mode를 모르면 막을 근거도 없다.

`src/DBVC.Core/GitManager.cs`의 `CommitChanges`:

```csharp
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.Commit))
            {
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.Commit);
            }
```

`PushChanges`도 같은 모양으로 `DbvcOperation.Push`를 쓴다.

`src/DBVC.Core/SmoManager.cs`의 `ScriptObjectsDetailed`에서 `localGitPath`를 얻은 직후:

```csharp
            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.Extract))
            {
                // 배포·감사 클론은 저장소에 쓸 일이 없다. 차이 검사는 파일을 만들지 않는다.
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.Extract);
            }
```

`src/DBVC.Core/StateTracker.cs`의 `InitializeDatabase` 진입부:

```csharp
            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.InstallTracker))
            {
                // 운영 DB에는 DDL 트리거를 설치할 수 없다. 화면이 오버레이를 띄우지 않지만
                // 그것만으로는 코드 경로가 하나 늘 때 조용히 다시 열린다.
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.InstallTracker);
            }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS 전부. 기존 테스트가 깨지면 그 테스트의 매핑이 `Mode`를 지정하지 않은 것이고, 기본값이 `Write`이므로 깨질 자리가 없어야 한다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/GitManager.cs src/DBVC.Core/SmoManager.cs src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 쓰기 API가 대상 용도를 확인하고 거절한다"
```

---

## Task 8: 제외 사유를 구분하고 스크립트 헤더를 한국어로 옮긴다

**Files:**
- Create: `src/DBVC.Core/Models/ScriptExclusion.cs`
- Modify: `src/DBVC.Core/ScriptGenerator.cs`
- Modify: `src/DBVC.Core/ScriptExporter.cs` (`ScriptExportResult.ExcludedObjects` 타입)
- Test: `tests/DBVC.Core.Tests/ScriptGeneratorTests.cs` (수정+추가), `tests/DBVC.Core.Tests/ScriptExporterTests.cs` (수정)

**Interfaces:**
- Produces:
  - `enum ScriptExclusionReason { NoContent, ManualChangeRequired, NotInBranch }`
  - `class ScriptExclusion` — 생성자 `(string qualifiedName, ScriptExclusionReason reason)`, 속성 `QualifiedName`, `Reason`
  - `ScriptGenerator.BuildScript(IEnumerable<ScriptSection>? sections, ScriptKind kind, DateTimeOffset generatedAt, IReadOnlyCollection<ScriptExclusion>? exclusions = null)` — **네 번째 인자의 타입이 바뀐다.**
  - `ScriptExportResult.ExcludedObjects`의 타입이 `List<string>` → `List<ScriptExclusion>`

- [ ] **Step 1: 모델을 만든다**

`src/DBVC.Core/Models/ScriptExclusion.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// 배포 스크립트에서 객체가 빠진 이유. 셋 다 사용자가 할 일이 다르므로 뭉뚱그리지 않는다.
    /// 열거 순서가 곧 헤더에 적히는 순서다.
    /// </summary>
    public enum ScriptExclusionReason
    {
        /// <summary>스크립트로 만들 내용이 없다. 파일이 없거나 비었다.</summary>
        NoContent,

        /// <summary>
        /// 대상에 이미 있는데 <c>CREATE OR ALTER</c>를 지원하지 않는 타입이다.
        /// 그대로 실행하면 "이미 있습니다"로 실패한다.
        /// </summary>
        ManualChangeRequired,

        /// <summary>DB에만 있고 브랜치에 없다. 스크립트에 담을 재료 자체가 없다.</summary>
        NotInBranch
    }

    public class ScriptExclusion
    {
        public ScriptExclusion(string qualifiedName, ScriptExclusionReason reason)
        {
            QualifiedName = qualifiedName;
            Reason = reason;
        }

        public string QualifiedName { get; }
        public ScriptExclusionReason Reason { get; }
    }
}
```

- [ ] **Step 2: `ScriptGenerator` 테스트를 고치고 더한다**

`tests/DBVC.Core.Tests/ScriptGeneratorTests.cs`를 연다. `BuildScript`에 `excludedObjects`를 넘기던 기존 테스트를 `new[] { new ScriptExclusion("dbo.X", ScriptExclusionReason.NoContent) }` 형태로 고치고, 영어 헤더 문자열(`"DBVC Deployment Script"`, `"Objects:"`, `"Excluded:"`)을 확인하던 단언을 한국어로 고친다. 그 뒤 다음을 더한다:

```csharp
        [Test]
        public void BuildScript_WritesTheHeaderInKorean()
        {
            // 스크립트는 사람이 열어 보는 산출물이다. 사유만 한국어로 적으면 한 헤더에
            // 두 언어가 섞인다.
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.GetUser", RelativePath = "dbo/StoredProcedures/GetUser.sql", Sql = "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1" }
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt);

            Assert.That(script, Does.Contain("DBVC 배포 스크립트"));
            Assert.That(script, Does.Contain("생성:"));
            Assert.That(script, Does.Contain("객체: 1"));
            Assert.That(script, Does.Not.Contain("Deployment Script"));
        }

        [Test]
        public void BuildScript_WritesRollbackTitleInKorean()
        {
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.A", RelativePath = "dbo/Views/A.sql", Sql = "CREATE VIEW dbo.A AS SELECT 1" }
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Rollback, GeneratedAt);

            Assert.That(script, Does.Contain("DBVC 롤백 스크립트"));
        }

        [Test]
        public void BuildScript_GroupsExclusionsByReason()
        {
            // 셋을 한 줄에 뭉치면 사용자가 무엇을 손으로 해야 하는지 알 수 없다.
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.A", RelativePath = "dbo/Views/A.sql", Sql = "CREATE VIEW dbo.A AS SELECT 1" }
            };
            var exclusions = new[]
            {
                new ScriptExclusion("dbo.Orders", ScriptExclusionReason.ManualChangeRequired),
                new ScriptExclusion("dbo.Customers", ScriptExclusionReason.ManualChangeRequired),
                new ScriptExclusion("dbo.Temp1", ScriptExclusionReason.NotInBranch)
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt, exclusions);

            Assert.That(script, Does.Contain("수동 변경이 필요합니다: 2 (dbo.Orders, dbo.Customers)"));
            Assert.That(script, Does.Contain("확인이 필요합니다: 1 (dbo.Temp1)"));
        }

        [Test]
        public void BuildScript_OmitsExclusionLines_WhenNothingWasExcluded()
        {
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.A", RelativePath = "dbo/Views/A.sql", Sql = "CREATE VIEW dbo.A AS SELECT 1" }
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt);

            Assert.That(script, Does.Not.Contain("제외"));
        }
```

`GeneratedAt` 상수가 이 픽스처에 없으면 `ScriptExporterTests.cs`의 것과 같은 값으로 더한다.

- [ ] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~ScriptGeneratorTests"`
Expected: 컴파일 실패(타입 불일치) 또는 한국어 헤더 단언 실패

- [ ] **Step 4: `ScriptGenerator`를 고친다**

`AppendHeader`를 통째로 바꾼다:

```csharp
        private static void AppendHeader(
            StringBuilder builder,
            ScriptKind kind,
            DateTimeOffset generatedAt,
            int objectCount,
            IReadOnlyCollection<ScriptExclusion>? exclusions)
        {
            var title = kind == ScriptKind.Rollback ? "DBVC 롤백 스크립트" : "DBVC 배포 스크립트";

            builder.AppendLine("/* ============================================================");
            builder.AppendLine($"   {title}");
            builder.AppendLine($"   생성: {generatedAt:yyyy-MM-ddTHH:mm:sszzz}");
            builder.AppendLine($"   객체: {objectCount}");

            AppendExclusions(builder, exclusions);

            builder.AppendLine("   ============================================================ */");
            builder.AppendLine();
        }

        /// <summary>
        /// 사유별로 묶어 적는다. 뭉뚱그리면 무엇을 손으로 해야 하는지 알 수 없다 —
        /// "수동 변경"은 사용자가 ALTER를 써야 한다는 뜻이고 "확인 필요"는 그렇지 않다.
        /// </summary>
        private static void AppendExclusions(StringBuilder builder, IReadOnlyCollection<ScriptExclusion>? exclusions)
        {
            if (exclusions == null || exclusions.Count == 0) return;

            foreach (ScriptExclusionReason reason in Enum.GetValues(typeof(ScriptExclusionReason)))
            {
                var names = exclusions
                    .Where(e => e != null && e.Reason == reason)
                    .Select(e => e.QualifiedName)
                    .ToList();

                if (names.Count == 0) continue;

                builder.AppendLine($"   제외 — {DescribeReason(reason)}: {names.Count} ({string.Join(", ", names)})");
            }
        }

        private static string DescribeReason(ScriptExclusionReason reason)
        {
            switch (reason)
            {
                case ScriptExclusionReason.NoContent:
                    return "스크립트로 만들 내용이 없습니다";
                case ScriptExclusionReason.ManualChangeRequired:
                    return "대상에 이미 있어 수동 변경이 필요합니다";
                case ScriptExclusionReason.NotInBranch:
                    return "브랜치에 없어 확인이 필요합니다";
                default:
                    throw new InvalidOperationException($"처리되지 않은 {nameof(ScriptExclusionReason)}: {reason}");
            }
        }
```

`BuildScript`의 네 번째 인자를 `IReadOnlyCollection<ScriptExclusion>? exclusions = null`로 바꾸고 `AppendHeader` 호출에 그대로 넘긴다.

- [ ] **Step 5: `ScriptExporter`를 맞춘다**

`ScriptExportResult.ExcludedObjects`의 타입을 바꾼다:

```csharp
        /// <summary>제외된 객체와 사유. 사용자가 할 일이 사유마다 다르다.</summary>
        public List<ScriptExclusion> ExcludedObjects { get; } = new List<ScriptExclusion>();
```

`Export`에서 제외를 담던 자리를 고친다:

```csharp
                    result.ExcludedObjects.Add(new ScriptExclusion(target.QualifiedName, ScriptExclusionReason.NoContent));
```

`ScriptExporterTests.cs`에서 `ExcludedObjects`를 문자열로 비교하던 단언을 `Select(e => e.QualifiedName)`로 고친다.

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS 전부

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Core/Models/ScriptExclusion.cs src/DBVC.Core/ScriptGenerator.cs src/DBVC.Core/ScriptExporter.cs tests/DBVC.Core.Tests/ScriptGeneratorTests.cs tests/DBVC.Core.Tests/ScriptExporterTests.cs
git commit -m "feat(core): 스크립트 제외 사유를 구분하고 헤더를 한국어로 적는다"
```

---

## Task 9: `CREATE OR ALTER` 지원 타입 표와 배포 분류

**Files:**
- Modify: `src/DBVC.Core/ObjectPathConvention.cs`
- Create: `src/DBVC.Core/DeploymentClassifier.cs`
- Test: `tests/DBVC.Core.Tests/ObjectPathConventionTests.cs` (추가), `tests/DBVC.Core.Tests/DeploymentClassifierTests.cs`

**Interfaces:**
- Consumes: Task 3의 `ObjectDiffState`
- Produces:
  - `static bool ObjectPathConvention.SupportsCreateOrAlter(string? objectType)`
  - `enum ScriptDisposition { Include, ExcludeManualChange, ExcludeNotInBranch }`
  - `static ScriptDisposition DeploymentClassifier.Classify(ObjectDiffState state, string? objectType)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/ObjectPathConventionTests.cs`에 추가:

```csharp
        // ---------- CREATE OR ALTER 지원 타입 ----------

        [TestCase("StoredProcedure")]
        [TestCase("View")]
        [TestCase("UserDefinedFunction")]
        [TestCase("Trigger")]
        public void SupportsCreateOrAlter_ReturnsTrue_ForTheFourTsqlTypes(string objectType)
        {
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter(objectType), Is.True);
        }

        [TestCase("Table")]
        [TestCase("Sequence")]
        [TestCase("Synonym")]
        [TestCase("UserDefinedType")]
        [TestCase("UserDefinedDataType")]
        [TestCase("UserDefinedTableType")]
        public void SupportsCreateOrAlter_ReturnsFalse_ForEveryOtherType(string objectType)
        {
            // 테이블만 빼면 Sequence·Synonym 같은 것들이 조용히 스크립트에 들어가
            // "이미 있습니다"로 실패한다. 축은 "테이블인가"가 아니다.
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter(objectType), Is.False);
        }

        [Test]
        public void SupportsCreateOrAlter_IgnoresCaseAndWhitespace()
        {
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter("  storedprocedure  "), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("Other")]
        public void SupportsCreateOrAlter_ReturnsFalse_WhenTypeIsUnknown(string? objectType)
        {
            // 모르는 타입은 안전한 쪽으로 떨어뜨린다. 실행 실패보다 "손으로 하세요"가 낫다.
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter(objectType), Is.False);
        }
```

`tests/DBVC.Core.Tests/DeploymentClassifierTests.cs`:

```csharp
using System;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 저장소 파일은 CREATE OR ALTER로 저장돼 있다. 그것을 그대로 실행해도 되는 경우와
    /// 안 되는 경우를 가르는 것이 배포 스크립트의 전부다.
    /// </summary>
    [TestFixture]
    public class DeploymentClassifierTests
    {
        [TestCase("Table")]
        [TestCase("StoredProcedure")]
        [TestCase("Sequence")]
        public void Classify_Includes_WhenObjectIsMissingInDatabase(string objectType)
        {
            // 신규는 CREATE 그대로라 타입을 가리지 않는다. 테이블도 안전하다.
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.MissingInDatabase, objectType),
                Is.EqualTo(ScriptDisposition.Include));
        }

        [TestCase("StoredProcedure")]
        [TestCase("View")]
        [TestCase("UserDefinedFunction")]
        [TestCase("Trigger")]
        public void Classify_Includes_WhenModifiedTypeSupportsCreateOrAlter(string objectType)
        {
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.Modified, objectType),
                Is.EqualTo(ScriptDisposition.Include));
        }

        [TestCase("Table")]
        [TestCase("Sequence")]
        [TestCase("Synonym")]
        [TestCase("UserDefinedType")]
        public void Classify_RequiresManualChange_WhenModifiedTypeDoesNotSupportCreateOrAlter(string objectType)
        {
            // 기존 테이블에 컬럼을 더하는 것은 기존 행을 무엇으로 채울지의 문제라
            // 스키마만 보고 결정할 수 없다. 틀린 ALTER를 자동 생성하느니 빼는 편이 낫다.
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.Modified, objectType),
                Is.EqualTo(ScriptDisposition.ExcludeManualChange));
        }

        [Test]
        public void Classify_ExcludesNotInBranch_WhenObjectExistsOnlyInDatabase()
        {
            // 브랜치에 파일이 없으므로 스크립트에 담을 재료 자체가 없다.
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.MissingInBranch, "StoredProcedure"),
                Is.EqualTo(ScriptDisposition.ExcludeNotInBranch));
        }

        [Test]
        public void Classify_Throws_WhenStateIsUnknown()
        {
            Assert.Throws<InvalidOperationException>(
                () => DeploymentClassifier.Classify((ObjectDiffState)999, "Table"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DeploymentClassifierTests|FullyQualifiedName~ObjectPathConventionTests"`
Expected: 컴파일 실패

- [ ] **Step 3: 구현한다**

`src/DBVC.Core/ObjectPathConvention.cs`에 추가:

```csharp
        /// <summary>
        /// T-SQL의 <c>CREATE OR ALTER</c>가 이 타입을 받는가. 받는 것은 넷뿐이다 —
        /// 프로시저·뷰·함수·트리거.
        ///
        /// 저장소 파일은 <c>ScriptForCreateOrAlter</c>로 저장되어 있으므로, 이 넷은
        /// 대상에 있든 없든 그대로 실행된다. 나머지는 대상에 이미 있으면 실패하므로
        /// 배포 스크립트에서 빼야 한다. <b>테이블만 빼면 안 된다</b> — Sequence·Synonym·
        /// UserDefinedType도 같은 자리에 있다.
        ///
        /// 모르는 타입은 false다. 실행 실패보다 "손으로 하세요"가 낫다.
        /// </summary>
        private static readonly HashSet<string> CreateOrAlterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StoredProcedure",
            "View",
            "UserDefinedFunction",
            "Trigger"
        };

        public static bool SupportsCreateOrAlter(string? objectType)
        {
            return !string.IsNullOrWhiteSpace(objectType) && CreateOrAlterTypes.Contains(objectType!.Trim());
        }
```

`src/DBVC.Core/DeploymentClassifier.cs`:

```csharp
using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>차이 하나를 배포 스크립트에서 어떻게 다룰지.</summary>
    public enum ScriptDisposition
    {
        /// <summary>브랜치의 파일 내용을 그대로 담는다.</summary>
        Include,

        /// <summary>대상에 이미 있고 CREATE OR ALTER가 안 되는 타입이다. 사람이 ALTER를 쓴다.</summary>
        ExcludeManualChange,

        /// <summary>DB에만 있다. 담을 재료가 없다.</summary>
        ExcludeNotInBranch
    }

    /// <summary>
    /// 차이 검사 결과를 배포 스크립트의 분류로 옮긴다. 순수 함수이므로 DB도 파일도 없이
    /// 테스트된다. 이 판정이 곧 "대상 DB에 각 객체가 있는지 조회"를 대신한다 —
    /// 차이 검사가 이미 답을 들고 있어 다시 물을 필요가 없다.
    /// </summary>
    public static class DeploymentClassifier
    {
        public static ScriptDisposition Classify(ObjectDiffState state, string? objectType)
        {
            switch (state)
            {
                case ObjectDiffState.MissingInDatabase:
                    // 신규는 CREATE 그대로라 타입을 가리지 않는다.
                    return ScriptDisposition.Include;

                case ObjectDiffState.Modified:
                    return ObjectPathConvention.SupportsCreateOrAlter(objectType)
                        ? ScriptDisposition.Include
                        : ScriptDisposition.ExcludeManualChange;

                case ObjectDiffState.MissingInBranch:
                    return ScriptDisposition.ExcludeNotInBranch;

                default:
                    throw new InvalidOperationException($"처리되지 않은 {nameof(ObjectDiffState)}: {state}");
            }
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DeploymentClassifierTests|FullyQualifiedName~ObjectPathConventionTests"`
Expected: PASS

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/ObjectPathConvention.cs src/DBVC.Core/DeploymentClassifier.cs tests/DBVC.Core.Tests/ObjectPathConventionTests.cs tests/DBVC.Core.Tests/DeploymentClassifierTests.cs
git commit -m "feat(core): CREATE OR ALTER 지원 여부로 배포 대상을 가른다"
```

---

## Task 10: `ExportFromComparison` — 차이 목록에서 배포 스크립트를 만든다

**Files:**
- Modify: `src/DBVC.Core/ScriptExporter.cs`
- Test: `tests/DBVC.Core.Tests/ScriptExporterTests.cs` (추가)

**Interfaces:**
- Consumes: Task 3의 `SchemaDifference`, Task 8의 `ScriptExclusion`, Task 9의 `DeploymentClassifier`
- Produces: `ScriptExportResult ScriptExporter.ExportFromComparison(string serverName, string databaseName, IEnumerable<SchemaDifference>? differences, DateTimeOffset generatedAt)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/ScriptExporterTests.cs`에 추가. 이 픽스처는 `_repoPath`에 실제 임시 저장소를 만들어 두므로 파일을 직접 쓴다:

```csharp
        // ---------- ExportFromComparison: 차이 목록이 곧 분류의 입력이다 ----------

        private void WriteRepositoryFile(string relativePath, string content)
        {
            var full = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        [Test]
        public void ExportFromComparison_IncludesNewObjectsAndModifiedProcedures()
        {
            WriteRepositoryFile("dbo/Tables/Orders.sql", "CREATE TABLE dbo.Orders (Id INT)");
            WriteRepositoryFile("dbo/StoredProcedures/GetUser.sql", "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1");

            var differences = new[]
            {
                new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.MissingInDatabase),
                new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(2));
            Assert.That(result.ExcludedObjects, Is.Empty);
            Assert.That(result.Script, Does.Contain("CREATE TABLE dbo.Orders"));
            Assert.That(result.Script, Does.Contain("CREATE OR ALTER PROCEDURE dbo.GetUser"));
        }

        [Test]
        public void ExportFromComparison_ExcludesModifiedTable_AsManualChange()
        {
            WriteRepositoryFile("dbo/Tables/Orders.sql", "CREATE TABLE dbo.Orders (Id INT)");

            var differences = new[]
            {
                new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.Modified)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(0));
            Assert.That(result.ExcludedObjects.Count, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects[0].QualifiedName, Is.EqualTo("dbo.Orders"));
            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.ManualChangeRequired));
            Assert.That(result.HasContent, Is.False);
        }

        [Test]
        public void ExportFromComparison_ExcludesDatabaseOnlyObject_AsNotInBranch()
        {
            var differences = new[]
            {
                new SchemaDifference("dbo.Temp1", "dbo/Tables/Temp1.sql", "Table", ObjectDiffState.MissingInBranch)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.NotInBranch));
        }

        [Test]
        public void ExportFromComparison_ExcludesAsNoContent_WhenBranchFileIsMissing()
        {
            // 차이 목록은 파일이 있다고 말했는데 실제로 없다. 검사와 생성 사이에 누군가
            // 지웠거나 권한이 막은 것이다. 조용히 빼면 배포가 덜 된 채로 성공한 척한다.
            var differences = new[]
            {
                new SchemaDifference("dbo.Gone", "dbo/Views/Gone.sql", "View", ObjectDiffState.MissingInDatabase)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.NoContent));
        }

        [Test]
        public void ExportFromComparison_ReturnsEmpty_WhenThereIsNoMapping()
        {
            var emptyConfig = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            var exporter = new ScriptExporter(emptyConfig, new GitManager(emptyConfig));

            var result = exporter.ExportFromComparison(Server, Database, new SchemaDifference[0], GeneratedAt);

            Assert.That(result.HasContent, Is.False);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~ScriptExporterTests"`
Expected: 컴파일 실패 — `ExportFromComparison`이 없다.

- [ ] **Step 3: 구현한다**

`src/DBVC.Core/ScriptExporter.cs`에 추가:

```csharp
        /// <summary>
        /// 차이 검사 결과에서 배포 스크립트를 만든다.
        ///
        /// 재료는 <b>브랜치의 파일</b>이지 대상 DB에서 다시 뜬 것이 아니다. "develop에 병합된
        /// 것만 테스트에 나간다"를 검사가 아니라 배치로 지킨다 — 배포 클론은 develop에
        /// 고정되어 있고 병합 안 된 변경은 애초에 파일로 존재하지 않는다.
        /// </summary>
        public ScriptExportResult ExportFromComparison(
            string serverName,
            string databaseName,
            IEnumerable<SchemaDifference>? differences,
            DateTimeOffset generatedAt)
        {
            var result = new ScriptExportResult();

            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null)
            {
                Debug.WriteLine($"'{serverName}.{databaseName}'에 매핑된 Git 저장소가 없어 스크립트를 생성할 수 없습니다.");
                return result;
            }

            var sections = new List<ScriptSection>();

            foreach (var difference in differences ?? Enumerable.Empty<SchemaDifference>())
            {
                if (difference == null || string.IsNullOrWhiteSpace(difference.RelativePath)) continue;

                var disposition = DeploymentClassifier.Classify(difference.State, difference.ObjectType);

                if (disposition == ScriptDisposition.ExcludeManualChange)
                {
                    result.ExcludedObjects.Add(new ScriptExclusion(difference.QualifiedName, ScriptExclusionReason.ManualChangeRequired));
                    continue;
                }

                if (disposition == ScriptDisposition.ExcludeNotInBranch)
                {
                    result.ExcludedObjects.Add(new ScriptExclusion(difference.QualifiedName, ScriptExclusionReason.NotInBranch));
                    continue;
                }

                var sql = ReadWorkingTreeFile(mapping.GitPath, difference.RelativePath);
                if (string.IsNullOrWhiteSpace(sql))
                {
                    // 검사할 때는 있었는데 지금 없다. 조용히 빼면 배포가 덜 된 채로 성공한 척한다.
                    result.ExcludedObjects.Add(new ScriptExclusion(difference.QualifiedName, ScriptExclusionReason.NoContent));
                    continue;
                }

                sections.Add(new ScriptSection
                {
                    QualifiedName = difference.QualifiedName,
                    RelativePath = difference.RelativePath,
                    Sql = sql
                });
            }

            result.IncludedCount = sections.Count;
            result.Script = sections.Count > 0
                ? ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, generatedAt, result.ExcludedObjects)
                : string.Empty;

            return result;
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS 전부

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/ScriptExporter.cs tests/DBVC.Core.Tests/ScriptExporterTests.cs
git commit -m "feat(core): 차이 목록에서 배포 스크립트를 만든다"
```

---

## Task 11: `BusyState` — 진행 표시와 취소를 두 ViewModel이 공유한다

**Files:**
- Create: `src/DBVC.Vsix/ViewModels/BusyState.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/BusyStateTests.cs`

**Interfaces:**
- Produces:
  - `class BusyState : INotifyPropertyChanged` — 설정 가능한 `bool IsBusy`, `string? ProgressText`, `bool IsCancellable`; 읽기 전용 `bool IsNotBusy`; `event EventHandler? Changed`
- `ViewChangesViewModel`이 `public BusyState Busy { get; }`를 노출한다. 기존 `IsBusy`·`IsNotBusy`·`ProgressText`는 **이름과 접근성이 그대로 남고** 내부만 `Busy`로 흘러간다 — XAML 바인딩과 2697줄짜리 기존 테스트가 한 줄도 바뀌지 않아야 한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/BusyStateTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 도구 줄의 진행 표시와 취소 버튼은 하나뿐이다. 배포 화면이 따로 들면
    /// 두 개가 각자 켜지고 꺼져 사용자가 무엇이 도는지 알 수 없게 된다.
    /// </summary>
    [TestFixture]
    public class BusyStateTests
    {
        [Test]
        public void IsNotBusy_MirrorsIsBusy()
        {
            var busy = new BusyState();

            Assert.That(busy.IsNotBusy, Is.True);

            busy.IsBusy = true;

            Assert.That(busy.IsNotBusy, Is.False);
        }

        [Test]
        public void Changed_Raises_WhenAnyValueChanges()
        {
            var busy = new BusyState();
            var count = 0;
            busy.Changed += (s, e) => count++;

            busy.IsBusy = true;
            busy.ProgressText = "추출하는 중...";
            busy.IsCancellable = true;

            Assert.That(count, Is.EqualTo(3));
        }

        [Test]
        public void Changed_DoesNotRaise_WhenValueIsUnchanged()
        {
            // 같은 값을 다시 넣을 때마다 CanExecute를 다시 계산하면 목록이 깜빡인다.
            var busy = new BusyState { IsBusy = true };
            var count = 0;
            busy.Changed += (s, e) => count++;

            busy.IsBusy = true;

            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void PropertyChanged_ReportsIsNotBusy_WhenIsBusyChanges()
        {
            // IsNotBusy는 계산 속성이라 스스로 알리지 못한다. 체크박스가 이것에 묶여 있다.
            var busy = new BusyState();
            var names = new List<string?>();
            busy.PropertyChanged += (s, e) => names.Add(e.PropertyName);

            busy.IsBusy = true;

            Assert.That(names, Does.Contain(nameof(BusyState.IsBusy)));
            Assert.That(names, Does.Contain(nameof(BusyState.IsNotBusy)));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~BusyStateTests"`
Expected: 컴파일 실패 — `BusyState`가 없다.

> `DBVC.Vsix.Tests`는 Windows에서 `net48`로 돈다. 이 프로젝트의 다른 테스트를 돌릴 때와 같은 프레임워크를 쓴다.

- [ ] **Step 3: 구현한다**

`src/DBVC.Vsix/ViewModels/BusyState.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 백그라운드 작업 하나의 상태. 변경 목록 화면과 배포·감사 화면이 같은 인스턴스를 본다.
    ///
    /// 나누면 도구 줄에 진행 표시가 둘, 취소 버튼이 둘 생기고 사용자가 무엇이 도는지 알 수
    /// 없게 된다. 자식 ViewModel이 부모를 역참조하는 것보다 이쪽이 낫다 — 순환 참조가 없고
    /// 둘 다 이것 하나만 테스트하면 된다.
    ///
    /// <b>UI 스레드에서만 바꾼다.</b> 보고는 백그라운드에서 오므로 호출부가
    /// <c>IBackgroundScheduler.Post</c>로 넘긴 뒤에 만져야 한다.
    /// </summary>
    public class BusyState : INotifyPropertyChanged
    {
        private bool _isBusy;
        private bool _isCancellable;
        private string? _progressText;

        /// <summary>진행 중에는 모든 동작 버튼이 잠긴다. 겹쳐 돌면 서로의 결과를 덮어쓴다.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotBusy));
                RaiseChanged();
            }
        }

        /// <summary>버튼은 CanExecute가 잠그지만 체크박스에는 명령이 없다. 화면이 막을 근거다.</summary>
        public bool IsNotBusy => !IsBusy;

        /// <summary>
        /// 지금 걸린 작업을 취소가 실제로 멈출 수 있는지.
        /// 없는 취소를 있는 척하는 버튼보다 없는 편이 정직하다.
        /// </summary>
        public bool IsCancellable
        {
            get => _isCancellable;
            set
            {
                if (_isCancellable == value) return;
                _isCancellable = value;
                OnPropertyChanged();
                RaiseChanged();
            }
        }

        /// <summary>진행 표시 옆에 붙는 한 줄. 작업이 없으면 null이다.</summary>
        public string? ProgressText
        {
            get => _progressText;
            set
            {
                if (_progressText == value) return;
                _progressText = value;
                OnPropertyChanged();
                RaiseChanged();
            }
        }

        /// <summary>어느 값이든 바뀌면 오른다. 두 ViewModel이 CanExecute를 다시 거는 자리다.</summary>
        public event EventHandler? Changed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~BusyStateTests"`
Expected: PASS (4개)

- [ ] **Step 5: `ViewChangesViewModel`을 갈아 끼운다**

세 곳만 바꾼다. **기존 대입문(`IsBusy = true;`, `ProgressText = "..."`)은 한 줄도 고치지 않는다.**

(a) 필드와 속성. `private bool _isBusy;`와 `private string? _progressText;`와 `private bool _cancellableWorkOutstanding;`를 지우고 다음으로 바꾼다:

```csharp
        /// <summary>
        /// 진행 표시와 취소 버튼의 유일한 상태. 배포·감사 화면(DeploymentViewModel)이
        /// 같은 인스턴스를 본다 — 나누면 도구 줄에 진행 표시가 둘 생긴다.
        /// </summary>
        public BusyState Busy { get; } = new BusyState();
```

기존 `IsBusy`/`IsNotBusy`/`ProgressText` 속성 본문을 다음으로 바꾼다. 이름·접근성·주석은 그대로 둔다:

```csharp
        public bool IsBusy
        {
            get => Busy.IsBusy;
            private set => Busy.IsBusy = value;
        }

        public bool IsNotBusy => !IsBusy;

        public string? ProgressText
        {
            get => Busy.ProgressText;
            private set => Busy.ProgressText = value;
        }
```

(b) `_cancellableWorkOutstanding`을 쓰던 자리(9곳)를 `Busy.IsCancellable`로 바꾼다. 대입 뒤에 붙어 있던 `RaiseActionCanExecuteChanged();`는 그대로 둬도 된다 — 중복 호출은 무해하다.

(c) 생성자 끝에 구독을 더한다:

```csharp
            // BusyState가 바뀌면 이 화면의 바인딩과 버튼 상태를 다시 계산한다.
            // 배포 화면이 일을 시작해도 여기 버튼이 함께 잠겨야 한다 — 같은 저장소와
            // 같은 접속을 쓰므로 겹쳐 돌면 서로의 결과를 덮어쓴다.
            Busy.Changed += (s, e) =>
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(ProgressText));
                RaiseActionCanExecuteChanged();
            };
```

`CancelCommand`의 조건을 바꾼다:

```csharp
            CancelCommand = new RelayCommand(Cancel, () => IsBusy && Busy.IsCancellable);
```

- [ ] **Step 6: 기존 테스트가 그대로 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS 전부. **`ViewChangesViewModelTests`를 한 줄이라도 고쳐야 했다면 리팩터링이 아니라 동작을 바꾼 것이다** — 되돌리고 다시 한다.

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/BusyState.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/BusyStateTests.cs
git commit -m "refactor(vsix): 진행 표시와 취소 상태를 한 곳으로 모은다"
```

---

## Task 12: `DeploymentViewModel` — 차이 검사와 스크립트 저장

**Files:**
- Create: `src/DBVC.Vsix/ViewModels/DifferenceItemViewModel.cs`
- Create: `src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/DifferenceItemViewModelTests.cs`, `tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelTests.cs`

**Interfaces:**
- Consumes: `ISmoManager.CompareWithRepository`/`ScriptObjectToText`, `IGitManager.PullChanges`, `IConfigManager.TryGetMapping`, `ScriptExporter.ExportFromComparison`, `IUserNotifier`, `IFileSaveDialog`, `IBackgroundScheduler`, Task 11의 `BusyState`
- Produces:
  - `static string DifferenceTextProvider.GetStateText(ObjectDiffState state, MappingMode mode)`
  - `class DifferenceItemViewModel` — 생성자 `(SchemaDifference difference, MappingMode mode)`, 속성 `QualifiedName`, `ObjectTypeText`, `RelativePath`, `StateText`, `Difference`
  - `class DeploymentViewModel : INotifyPropertyChanged` — 생성자 `(IConfigManager, IGitManager, ISmoManager, ScriptExporter, IUserNotifier, IFileSaveDialog, IBackgroundScheduler, BusyState)`; `ObservableCollection<DifferenceItemViewModel> Differences`, `DifferenceItemViewModel? SelectedDifference`, `string? SummaryText`, `bool HasResult`, `ICommand CompareCommand`, `ICommand SaveScriptCommand`, `event EventHandler? SelectionChanged`, `void SetTarget(string? serverName, string? databaseName, MappingMode mode)`, `(string BranchText, string DatabaseText) LoadSelectedTexts()`

- [ ] **Step 1: 문구 판정과 항목의 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/DifferenceItemViewModelTests.cs`:

```csharp
using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 운영에는 트리거를 설치할 수 없으므로 차이가 "미배포"인지 "무단 변경"인지 구분할 수 없다.
    /// 구분되는 척하면 DBA가 잘못된 판단을 한다.
    /// </summary>
    [TestFixture]
    public class DifferenceItemViewModelTests
    {
        [Test]
        public void GetStateText_DistinguishesEachState_WhenModeIsDeploy()
        {
            Assert.That(DifferenceTextProvider.GetStateText(ObjectDiffState.MissingInDatabase, MappingMode.Deploy),
                Is.EqualTo("배포 필요 (신규)"));
            Assert.That(DifferenceTextProvider.GetStateText(ObjectDiffState.Modified, MappingMode.Deploy),
                Is.EqualTo("배포 필요 (내용 다름)"));
            Assert.That(DifferenceTextProvider.GetStateText(ObjectDiffState.MissingInBranch, MappingMode.Deploy),
                Is.EqualTo("DB에만 있음"));
        }

        [TestCase(ObjectDiffState.MissingInDatabase)]
        [TestCase(ObjectDiffState.Modified)]
        [TestCase(ObjectDiffState.MissingInBranch)]
        public void GetStateText_ReportsNeedsReview_ForEveryState_WhenModeIsAudit(ObjectDiffState state)
        {
            Assert.That(DifferenceTextProvider.GetStateText(state, MappingMode.Audit), Is.EqualTo("확인 필요"));
        }

        [Test]
        public void Constructor_TranslatesObjectTypeIntoKorean()
        {
            var difference = new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified);

            var item = new DifferenceItemViewModel(difference, MappingMode.Deploy);

            Assert.That(item.QualifiedName, Is.EqualTo("dbo.GetUser"));
            Assert.That(item.ObjectTypeText, Is.EqualTo("저장 프로시저"));
            Assert.That(item.StateText, Is.EqualTo("배포 필요 (내용 다름)"));
        }

        [Test]
        public void Constructor_FallsBackToTheRawType_WhenItIsNotKnown()
        {
            var difference = new SchemaDifference("dbo.X", "dbo/Other/X.sql", "Other", ObjectDiffState.Modified);

            var item = new DifferenceItemViewModel(difference, MappingMode.Deploy);

            Assert.That(item.ObjectTypeText, Is.EqualTo("Other"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~DifferenceItemViewModelTests"`
Expected: 컴파일 실패

- [ ] **Step 3: 항목과 문구를 만든다**

`src/DBVC.Vsix/ViewModels/DifferenceItemViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 차이 하나를 사용자에게 뭐라고 부를지 정한다. mode에 따라 다르다 —
    /// 운영(audit)에서는 트리거가 없어 "미배포"인지 "무단 변경"인지 구분할 수 없으므로
    /// 구분되는 척하지 않는다.
    /// </summary>
    public static class DifferenceTextProvider
    {
        public static string GetStateText(ObjectDiffState state, MappingMode mode)
        {
            if (mode == MappingMode.Audit) return "확인 필요";

            switch (state)
            {
                case ObjectDiffState.MissingInDatabase: return "배포 필요 (신규)";
                case ObjectDiffState.Modified: return "배포 필요 (내용 다름)";
                case ObjectDiffState.MissingInBranch: return "DB에만 있음";
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(ObjectDiffState)}: {state}");
            }
        }

        private static readonly Dictionary<string, string> KoreanByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Table"] = "테이블",
            ["View"] = "뷰",
            ["StoredProcedure"] = "저장 프로시저",
            ["UserDefinedFunction"] = "함수",
            ["Trigger"] = "트리거",
            ["UserDefinedType"] = "형식",
            ["UserDefinedDataType"] = "형식",
            ["UserDefinedTableType"] = "테이블 형식",
            ["Sequence"] = "시퀀스",
            ["Synonym"] = "동의어"
        };

        /// <summary>모르는 타입은 원문 그대로 보여준다. 빈칸보다 낫다.</summary>
        public static string GetObjectTypeText(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return string.Empty;
            return KoreanByObjectType.TryGetValue(objectType!.Trim(), out var korean) ? korean : objectType!.Trim();
        }
    }

    public class DifferenceItemViewModel
    {
        public DifferenceItemViewModel(SchemaDifference difference, MappingMode mode)
        {
            Difference = difference ?? throw new ArgumentNullException(nameof(difference));
            StateText = DifferenceTextProvider.GetStateText(difference.State, mode);
            ObjectTypeText = DifferenceTextProvider.GetObjectTypeText(difference.ObjectType);
        }

        public SchemaDifference Difference { get; }
        public string QualifiedName => Difference.QualifiedName;
        public string RelativePath => Difference.RelativePath;
        public string ObjectTypeText { get; }
        public string StateText { get; }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~DifferenceItemViewModelTests"`
Expected: PASS

- [ ] **Step 5: `DeploymentViewModel`의 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelTests.cs`:

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
    /// 배포는 3단계 루프다 — 차이를 보고, 스크립트를 만들어 사람이 실행하고, 다시 검사한다.
    /// 3단계가 없으면 "됐다고 생각했는데 안 된" 배포가 성공으로 보인다.
    /// </summary>
    [TestFixture]
    public class DeploymentViewModelTests
    {
        private const string Server = "TestServer";
        private const string Database = "TestDb";

        private Mock<IConfigManager> _config = null!;
        private Mock<IGitManager> _git = null!;
        private Mock<ISmoManager> _smo = null!;
        private RecordingNotifier _notifier = null!;
        private RecordingSaveDialog _saveDialog = null!;
        private BusyState _busy = null!;
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dbvc_dep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private DeploymentViewModel NewViewModel(MappingMode mode, out string repoPath)
        {
            repoPath = NewTempDir();
            var mapping = new MappingConfig
            {
                ServerName = Server,
                DatabaseName = Database,
                GitPath = repoPath,
                Mode = mode,
                Branch = mode == MappingMode.Audit ? "master" : "develop"
            };

            _config = new Mock<IConfigManager>();
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns(mapping);
            _git = new Mock<IGitManager>();
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            _smo = new Mock<ISmoManager>();
            _notifier = new RecordingNotifier();
            _saveDialog = new RecordingSaveDialog();
            _busy = new BusyState();

            var vm = new DeploymentViewModel(
                _config.Object, _git.Object, _smo.Object,
                new ScriptExporter(_config.Object, _git.Object),
                _notifier, _saveDialog, new InlineBackgroundScheduler(), _busy);

            vm.SetTarget(Server, Database, mode);
            return vm;
        }

        private static ComparisonResult ResultWith(params SchemaDifference[] differences)
        {
            var result = new ComparisonResult { ComparedCount = 10 };
            result.Differences.AddRange(differences);
            return result;
        }

        [Test]
        public void CompareCommand_FillsTheList_WhenDifferencesAreFound()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified)));

            vm.CompareCommand.Execute(null);

            Assert.That(vm.Differences.Count, Is.EqualTo(1));
            Assert.That(vm.Differences[0].StateText, Is.EqualTo("배포 필요 (내용 다름)"));
            Assert.That(vm.SummaryText, Does.Contain("10").And.Contain("1"));
        }

        [Test]
        public void CompareCommand_PullsBeforeComparing()
        {
            // 로컬 develop이 낡았으면 방금 병합된 변경이 목록에서 통째로 빠지고,
            // 그것은 "배포 완료"로 보인다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith());

            vm.CompareCommand.Execute(null);

            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void CompareCommand_ReportsInSync_WhenNothingDiffers()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith());

            vm.CompareCommand.Execute(null);

            Assert.That(vm.Differences, Is.Empty);
            Assert.That(vm.SummaryText, Does.Contain("일치"));
        }

        [Test]
        public void CompareCommand_StopsAndReports_WhenPullFails()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _git.Setup(g => g.PullChanges(Server, Database)).Throws(new GitRemoteException("원격에 연결할 수 없습니다."));

            vm.CompareCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Not.Empty);
            _smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void SetTarget_ClearsPreviousResults()
        {
            // 낡은 결과를 최신인 척 보여주지 않는다. 원격 확인 표시와 같은 규칙이다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(new SchemaDifference("dbo.A", "dbo/Views/A.sql", "View", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            vm.SetTarget("OtherServer", "OtherDb", MappingMode.Audit);

            Assert.That(vm.Differences, Is.Empty);
            Assert.That(vm.SummaryText, Is.Null);
            Assert.That(vm.HasResult, Is.False);
        }

        [Test]
        public void SaveScriptCommand_IsDisabled_UntilComparisonHasRun()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);

            Assert.That(vm.SaveScriptCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void SaveScriptCommand_WritesTheScript_AndReportsExclusions()
        {
            var vm = NewViewModel(MappingMode.Deploy, out var repoPath);
            var procPath = Path.Combine(repoPath, "dbo", "StoredProcedures");
            Directory.CreateDirectory(procPath);
            File.WriteAllText(Path.Combine(procPath, "GetUser.sql"), "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1");

            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(
                    new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified),
                    new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            _saveDialog.PathToReturn = Path.Combine(NewTempDir(), "deploy.sql");
            vm.SaveScriptCommand.Execute(null);

            var written = File.ReadAllText(_saveDialog.PathToReturn);
            Assert.That(written, Does.Contain("CREATE OR ALTER PROCEDURE dbo.GetUser"));
            Assert.That(written, Does.Contain("수동 변경이 필요합니다: 1 (dbo.Orders)"));
            Assert.That(_notifier.Infos, Is.Not.Empty);
        }

        [Test]
        public void SaveScriptCommand_ReportsNothingToWrite_WhenEveryObjectIsExcluded()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            vm.SaveScriptCommand.Execute(null);

            Assert.That(_saveDialog.WasPrompted, Is.False);
            Assert.That(_notifier.Infos.Concat(_notifier.Errors).Any(m => m.Contains("생성할")), Is.True);
        }

        [Test]
        public void Commands_AreDisabled_WhileTheSharedBusyStateIsSet()
        {
            // 같은 저장소와 같은 접속을 쓰므로 변경 목록 화면이 일하는 동안 겹쳐 돌면 안 된다.
            var vm = NewViewModel(MappingMode.Deploy, out _);

            _busy.IsBusy = true;

            Assert.That(vm.CompareCommand.CanExecute(null), Is.False);
        }
    }
}
```

`RecordingNotifier`·`RecordingSaveDialog`·`InlineBackgroundScheduler`는 `ViewChangesViewModelTests.cs`에 이미 있다. 파일 안에 중첩 클래스로 있으면 공용 위치(`tests/DBVC.Vsix.Tests/TestSetup.cs` 옆의 새 파일 `tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs`)로 옮기고 두 픽스처가 함께 쓴다. **복사하지 않는다** — 둘이 갈라지면 한쪽 테스트만 고쳐진다. `RecordingSaveDialog`에 `PathToReturn`·`WasPrompted`가 없으면 더한다.

- [ ] **Step 6: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~DeploymentViewModelTests"`
Expected: 컴파일 실패 — `DeploymentViewModel`이 없다.

- [ ] **Step 7: `DeploymentViewModel`을 만든다**

`src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Commands;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 배포·감사 대상의 차이 검사와 배포 스크립트 생성.
    ///
    /// ViewChangesViewModel에 얹지 않는 이유는 그쪽이 이미 1592줄이고 대상 선택·접속·매핑·
    /// 차단·커밋·이력을 전부 들고 있기 때문이다. 진행 표시와 취소만 <see cref="BusyState"/>로
    /// 공유한다 — 도구 줄에 그것이 둘 생기면 사용자가 무엇이 도는지 알 수 없다.
    /// </summary>
    public class DeploymentViewModel : INotifyPropertyChanged
    {
        private readonly IConfigManager _configManager;
        private readonly IGitManager _gitManager;
        private readonly ISmoManager _smoManager;
        private readonly ScriptExporter _scriptExporter;
        private readonly IUserNotifier _notifier;
        private readonly IFileSaveDialog _saveDialog;
        private readonly IBackgroundScheduler _scheduler;

        private CancellationTokenSource? _comparison;
        private ComparisonResult? _lastResult;
        private string? _serverName;
        private string? _databaseName;
        private MappingMode _mode = MappingMode.Write;

        public DeploymentViewModel(
            IConfigManager configManager,
            IGitManager gitManager,
            ISmoManager smoManager,
            ScriptExporter scriptExporter,
            IUserNotifier notifier,
            IFileSaveDialog saveDialog,
            IBackgroundScheduler scheduler,
            BusyState busy)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            _smoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
            _scriptExporter = scriptExporter ?? throw new ArgumentNullException(nameof(scriptExporter));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Busy = busy ?? throw new ArgumentNullException(nameof(busy));

            CompareCommand = new RelayCommand(Compare, () => HasTarget && !Busy.IsBusy);
            SaveScriptCommand = new RelayCommand(SaveScript, () => HasResult && !Busy.IsBusy);

            Busy.Changed += (s, e) => RaiseCanExecuteChanged();
        }

        public BusyState Busy { get; }

        public ObservableCollection<DifferenceItemViewModel> Differences { get; } =
            new ObservableCollection<DifferenceItemViewModel>();

        private DifferenceItemViewModel? _selectedDifference;
        public DifferenceItemViewModel? SelectedDifference
        {
            get => _selectedDifference;
            set
            {
                if (ReferenceEquals(_selectedDifference, value)) return;
                _selectedDifference = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>선택이 바뀌면 뷰가 Diff를 다시 그리도록 알린다.</summary>
        public event EventHandler? SelectionChanged;

        private string? _summaryText;

        /// <summary>"12개 중 3개 차이" 또는 "일치합니다". 검사 전에는 null이다.</summary>
        public string? SummaryText
        {
            get => _summaryText;
            private set
            {
                if (_summaryText == value) return;
                _summaryText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>검사가 한 번이라도 끝났는지. 스크립트 생성의 전제다.</summary>
        public bool HasResult => _lastResult != null;

        private bool HasTarget => !string.IsNullOrWhiteSpace(_serverName) && !string.IsNullOrWhiteSpace(_databaseName);

        public ICommand CompareCommand { get; }
        public ICommand SaveScriptCommand { get; }

        /// <summary>
        /// 대상을 바꾼다. 이전 결과를 지운다 — 낡은 목록을 최신인 척 보여주지 않는다.
        /// </summary>
        public void SetTarget(string? serverName, string? databaseName, MappingMode mode)
        {
            _serverName = serverName;
            _databaseName = databaseName;
            _mode = mode;

            _lastResult = null;
            SelectedDifference = null;
            Differences.Clear();
            SummaryText = null;
            OnPropertyChanged(nameof(HasResult));
            RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 선택된 객체의 좌우 원문. 왼쪽은 브랜치의 파일, 오른쪽은 DB의 현재 모습이다.
        /// 뷰가 Diff를 그릴 때만 부르므로 여기서만 SMO를 한 번 더 탄다.
        /// </summary>
        public (string BranchText, string DatabaseText) LoadSelectedTexts()
        {
            var selected = SelectedDifference;
            if (selected == null || !HasTarget) return (string.Empty, string.Empty);

            var mapping = _configManager.TryGetMapping(_serverName!, _databaseName!);
            var branchText = string.Empty;

            if (mapping != null)
            {
                var full = Path.Combine(mapping.GitPath, selected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) branchText = File.ReadAllText(full);
            }

            // DB에만 있는 객체는 왼쪽이, 브랜치에만 있는 객체는 오른쪽이 빈다.
            var databaseText = selected.Difference.State == ObjectDiffState.MissingInDatabase
                ? string.Empty
                : _smoManager.ScriptObjectToText(_serverName!, _databaseName!, selected.QualifiedName) ?? string.Empty;

            return (branchText, databaseText);
        }

        private void Compare()
        {
            if (!HasTarget) return;

            var server = _serverName!;
            var database = _databaseName!;
            var mode = _mode;

            _comparison?.Dispose();
            _comparison = new CancellationTokenSource();
            var token = _comparison.Token;

            Busy.IsBusy = true;
            Busy.IsCancellable = true;
            Busy.ProgressText = "원격 저장소에서 가져오는 중...";

            var progress = new ExtractionProgressRelay(p =>
            {
                var text = p.Total > 0
                    ? $"비교하는 중... {p.Completed}/{p.Total} — {p.ObjectName}"
                    : "비교하는 중...";
                _scheduler.Post(() => Busy.ProgressText = text);
            });

            _scheduler.Run(
                () =>
                {
                    // 낡은 브랜치로 비교하면 방금 병합된 변경이 목록에서 통째로 빠지고,
                    // 그것은 "배포 완료"로 보인다. 원격이 없으면 Core가 NoRemote를 돌려준다.
                    _gitManager.PullChanges(server, database);
                    return _smoManager.CompareWithRepository(server, database, progress, token);
                },
                result => ApplyComparison(result, mode),
                ex =>
                {
                    EndBusy();
                    if (ex is OperationCanceledException) return;
                    _notifier.ShowError("DBVC 차이 검사 실패", ex.Message);
                });
        }

        private void ApplyComparison(ComparisonResult? result, MappingMode mode)
        {
            EndBusy();

            if (result == null)
            {
                _notifier.ShowError("DBVC 차이 검사 실패",
                    "대상 데이터베이스에 연결하지 못했거나 매핑된 저장소가 없어 비교하지 못했습니다.");
                return;
            }

            _lastResult = result;
            SelectedDifference = null;
            Differences.Clear();

            foreach (var difference in result.Differences.OrderBy(d => d.QualifiedName, StringComparer.OrdinalIgnoreCase))
            {
                Differences.Add(new DifferenceItemViewModel(difference, mode));
            }

            SummaryText = result.IsInSync
                ? $"대상 {result.ComparedCount}개를 검사했습니다. 브랜치와 일치합니다."
                : $"대상 {result.ComparedCount}개 중 {result.Differences.Count}개가 다릅니다.";

            if (result.FailedObjects.Count > 0)
            {
                // 실패는 "차이가 없다"가 아니라 "모른다"이다. 목록에 섞으면 배포 대상으로 읽힌다.
                _notifier.ShowError("DBVC 차이 검사",
                    $"{result.FailedObjects.Count}개 객체는 스크립팅에 실패해 판정하지 못했습니다:" + Environment.NewLine +
                    string.Join(", ", result.FailedObjects));
            }

            OnPropertyChanged(nameof(HasResult));
            RaiseCanExecuteChanged();
        }

        private void SaveScript()
        {
            if (_lastResult == null || !HasTarget) return;

            var export = _scriptExporter.ExportFromComparison(
                _serverName!, _databaseName!, _lastResult.Differences, DateTimeOffset.Now);

            if (!export.HasContent)
            {
                _notifier.ShowInfo("DBVC 배포 스크립트",
                    "스크립트에 담을 내용이 없어 생성할 것이 없습니다." + Environment.NewLine +
                    "차이가 전부 수동 처리 또는 확인 대상입니다.");
                return;
            }

            var path = _saveDialog.PromptForSavePath("배포 스크립트를 저장할 위치를 선택하세요.", "dbvc_deploy.sql");
            if (path == null) return;

            try
            {
                File.WriteAllText(path, export.Script);
            }
            catch (Exception ex)
            {
                _notifier.ShowError("DBVC 배포 스크립트 저장 실패", ex.Message);
                return;
            }

            var message = $"{export.IncludedCount}개 객체를 담았습니다." + Environment.NewLine + path;
            if (export.ExcludedObjects.Count > 0)
            {
                message += Environment.NewLine + Environment.NewLine +
                           $"{export.ExcludedObjects.Count}개는 제외했습니다. 사유는 파일 머리말에 있습니다.";
            }
            message += Environment.NewLine + Environment.NewLine +
                       "SSMS 쿼리 창에서 실행한 뒤 [차이 검사]를 다시 눌러 결과를 확인하세요.";

            _notifier.ShowInfo("DBVC 배포 스크립트", message);
        }

        private void EndBusy()
        {
            Busy.IsBusy = false;
            Busy.IsCancellable = false;
            Busy.ProgressText = null;
        }

        /// <summary>진행 중인 비교를 멈춘다. 저장소에 쓴 것이 없으므로 되돌릴 것이 없다.</summary>
        public void Cancel()
        {
            _comparison?.Cancel();
            Busy.ProgressText = "취소하는 중...";
        }

        private void RaiseCanExecuteChanged()
        {
            (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```

`ExtractionProgressRelay`가 없으면 `ViewChangesViewModel.cs`의 `CloneProgressRelay`와 같은 모양으로 만든다(이미 있으면 그것을 쓴다). `RelayCommand.RaiseCanExecuteChanged`의 실제 이름은 `src/DBVC.Vsix/Commands/RelayCommand.cs`를 열어 확인하고 맞춘다.

- [ ] **Step 8: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~DeploymentViewModelTests"`
Expected: PASS (9개)

- [ ] **Step 9: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/DifferenceItemViewModel.cs src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/
git commit -m "feat(vsix): 배포·감사 대상의 차이 검사와 스크립트 생성을 더한다"
```

---

## Task 13: 패널 전환과 `ViewChangesViewModel` 통합

**Files:**
- Create: `src/DBVC.Vsix/ViewModels/PanelSelector.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `src/DBVC.Vsix/DbvcServices.cs` (조립)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/PanelSelectorTests.cs`, `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` (추가)

**Interfaces:**
- Consumes: Task 1의 `MappingPolicy`, Task 12의 `DeploymentViewModel`
- Produces:
  - `enum DbvcPanelKind { ChangeList, SetupOverlay, DeploymentPanel }`
  - `static DbvcPanelKind PanelSelector.Select(MappingMode mode, bool isInitialized)`
  - `ViewChangesViewModel`에 `public DeploymentViewModel Deployment { get; }`, `public MappingMode Mode { get; }`, `public bool ShowChangeList`, `public bool ShowSetupOverlay`, `public bool ShowDeploymentPanel`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/PanelSelectorTests.cs`:

```csharp
using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// DBA가 운영 DB에 붙었다가 초기화 버튼을 한 번 누르면 금지된 DDL 트리거가 설치된다.
    /// 그 버튼이 있는 화면이 뜨지 않게 하는 것이 이 판정의 존재 이유다.
    /// </summary>
    [TestFixture]
    public class PanelSelectorTests
    {
        [Test]
        public void Select_ShowsChangeList_WhenWriteAndInitialized()
        {
            Assert.That(PanelSelector.Select(MappingMode.Write, isInitialized: true),
                Is.EqualTo(DbvcPanelKind.ChangeList));
        }

        [Test]
        public void Select_ShowsSetupOverlay_WhenWriteAndNotInitialized()
        {
            Assert.That(PanelSelector.Select(MappingMode.Write, isInitialized: false),
                Is.EqualTo(DbvcPanelKind.SetupOverlay));
        }

        [TestCase(MappingMode.Deploy, true)]
        [TestCase(MappingMode.Deploy, false)]
        [TestCase(MappingMode.Audit, true)]
        [TestCase(MappingMode.Audit, false)]
        public void Select_ShowsDeploymentPanel_RegardlessOfInitialization_WhenModeIsNotWrite(MappingMode mode, bool isInitialized)
        {
            // 초기화 여부를 보면 안 된다. 운영 DB는 미초기화 상태가 정상이고,
            // 그때 오버레이가 뜨면 눌리는 버튼이 바로 금지된 트리거 설치다.
            Assert.That(PanelSelector.Select(mode, isInitialized), Is.EqualTo(DbvcPanelKind.DeploymentPanel));
        }
    }
}
```

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`에 추가. 이 픽스처가 대상을 붙이는 헬퍼(`Connect`를 태우거나 매핑을 세우는 기존 방식)를 그대로 쓴다:

```csharp
        [Test]
        public void SetupCommand_IsDisabled_WhenModeIsAudit()
        {
            // 화면이 오버레이를 띄우지 않더라도 명령이 살아 있으면 코드 경로가 하나 늘 때
            // 다시 눌린다.
            var vm = NewViewModelForMappedTarget(MappingMode.Audit);

            Assert.That(vm.SetupCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void CommitCommand_IsDisabled_WhenModeIsDeploy()
        {
            var vm = NewViewModelForMappedTarget(MappingMode.Deploy);

            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void ShowDeploymentPanel_IsTrue_WhenModeIsNotWrite()
        {
            var vm = NewViewModelForMappedTarget(MappingMode.Deploy);

            Assert.That(vm.ShowDeploymentPanel, Is.True);
            Assert.That(vm.ShowSetupOverlay, Is.False);
            Assert.That(vm.ShowChangeList, Is.False);
        }

        [Test]
        public void ParentCommands_AreDisabled_WhileTheDeploymentPanelIsWorking()
        {
            // 두 화면이 같은 저장소와 같은 접속을 쓴다. 배포 쪽이 전체 비교를 도는 동안
            // 여기서 Pull이 눌리면 작업 트리를 동시에 건드린다.
            var vm = NewViewModelForMappedTarget(MappingMode.Write);
            var wasEnabled = vm.PullCommand.CanExecute(null);

            vm.Deployment.Busy.IsBusy = true;

            Assert.That(wasEnabled, Is.True, "전제가 깨졌다 - 눌리지 않던 버튼으로는 잠김을 확인할 수 없다");
            Assert.That(vm.PullCommand.CanExecute(null), Is.False);
            Assert.That(vm.IsBusy, Is.True, "BusyState가 공유되지 않으면 부모가 자식의 작업을 모른다");
        }
```

`NewViewModelForMappedTarget(MappingMode)`는 이 픽스처의 기존 조립 헬퍼를 참고해 만든다 — `_config.Setup(c => c.TryGetMapping(...))`이 그 mode를 담은 `MappingConfig`를 돌려주게 하고, 대상을 붙인 뒤(`Connect` 경로) VM을 돌려준다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~PanelSelectorTests"`
Expected: 컴파일 실패

- [ ] **Step 3: `PanelSelector`를 만든다**

`src/DBVC.Vsix/ViewModels/PanelSelector.cs`:

```csharp
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    public enum DbvcPanelKind
    {
        ChangeList,
        SetupOverlay,
        DeploymentPanel
    }

    /// <summary>
    /// 본문 자리에 무엇을 띄울지. 순수 함수라 WPF 없이 테스트된다.
    ///
    /// mode를 먼저 본다. 운영·테스트 대상은 미초기화가 정상 상태이고, 거기서 초기화
    /// 오버레이가 뜨면 사용자가 누르는 버튼이 곧 금지된 DDL 트리거 설치다.
    /// </summary>
    public static class PanelSelector
    {
        public static DbvcPanelKind Select(MappingMode mode, bool isInitialized)
        {
            if (mode != MappingMode.Write) return DbvcPanelKind.DeploymentPanel;
            return isInitialized ? DbvcPanelKind.ChangeList : DbvcPanelKind.SetupOverlay;
        }
    }
}
```

- [ ] **Step 4: `ViewChangesViewModel`에 통합한다**

(a) 필드와 속성을 더한다:

```csharp
        private MappingMode _mode = MappingMode.Write;

        /// <summary>현재 대상의 용도. 매핑이 없으면 기본값(개발)이다.</summary>
        public MappingMode Mode
        {
            get => _mode;
            private set
            {
                if (_mode == value) return;
                _mode = value;
                OnPropertyChanged();
                RaisePanelChanged();
                RaiseActionCanExecuteChanged();
            }
        }

        /// <summary>배포·감사 화면. 진행 표시와 취소를 이 화면과 공유한다.</summary>
        public DeploymentViewModel Deployment { get; }

        public bool ShowChangeList => PanelSelector.Select(Mode, IsInitialized) == DbvcPanelKind.ChangeList;
        public bool ShowSetupOverlay => PanelSelector.Select(Mode, IsInitialized) == DbvcPanelKind.SetupOverlay;
        public bool ShowDeploymentPanel => PanelSelector.Select(Mode, IsInitialized) == DbvcPanelKind.DeploymentPanel;

        private void RaisePanelChanged()
        {
            OnPropertyChanged(nameof(ShowChangeList));
            OnPropertyChanged(nameof(ShowSetupOverlay));
            OnPropertyChanged(nameof(ShowDeploymentPanel));
        }
```

`IsInitialized`의 setter 끝에 `RaisePanelChanged();`를 더한다.

(b) 생성자에서 `Deployment`를 만든다(`_scriptExporter` 다음 줄):

```csharp
            Deployment = new DeploymentViewModel(
                _configManager, _gitManager, _smoManager, _scriptExporter,
                _notifier, _saveDialog, _scheduler, Busy);
```

(c) `Cancel()`이 배포 쪽 작업도 멈추게 한다:

```csharp
        private void Cancel()
        {
            // Cancel을 눌러도 IsBusy는 작업이 실제로 멈출 때까지 유지된다.
            // 취소 버튼은 하나뿐이므로 어느 화면이 걸어 둔 작업인지 묻지 않고 둘 다 멈춘다.
            _cancellableOperation?.Cancel();
            Deployment.Cancel();
            ProgressText = "취소하는 중...";
        }
```

(d) `ContextProbe`가 mode를 날라 오게 한다. `ContextProbe`에 `public MappingMode Mode { get; set; } = MappingMode.Write;`를 더하고, `ProbeContext`에서 매핑을 읽는 자리(`probe.IsMapped`를 세우는 곳)에서 함께 채운다. `ApplyContextProbe`에서 `IsInitialized`를 세우기 **전에** 대입한다 — 순서가 뒤바뀌면 패널이 한 번 잘못 떴다가 바뀐다:

```csharp
            Mode = probe.Mode;
            WarningMessage = IsMapped ? null : NotMappedWarning;
            IsInitialized = probe.InstalledVersion > 0;
```

그리고 `ApplyContextProbe` 끝의 자동 `Refresh()` 조건과 대상 전달을 고친다:

```csharp
            Deployment.SetTarget(ServerName, DatabaseName, Mode);

            if (IsMapped && IsInitialized && Mode == MappingMode.Write)
            {
                Refresh();
            }
```

`IsBlocked`로 일찍 반환하는 자리보다 앞에 `Deployment.SetTarget(...)`을 두면 차단 상태에서 낡은 목록이 남지 않는다.

(e) 명령의 `CanExecute`에 mode를 건다. 각 조건 앞에 `MappingPolicy.IsAllowed`를 더한다:

```csharp
            RefreshCommand = new RelayCommand(Refresh, () => !IsBusy && MappingPolicy.IsAllowed(Mode, DbvcOperation.Extract));
            RefreshAllCommand = new RelayCommand(RefreshAll, () => !IsBusy && MappingPolicy.IsAllowed(Mode, DbvcOperation.Extract));
            SetupCommand = new RelayCommand(Setup, () => !IsBusy && MappingPolicy.IsAllowed(Mode, DbvcOperation.InstallTracker));
            UpdateTrackerCommand = new RelayCommand(UpdateTracker, () => IsTrackerOutdated && !IsBusy && MappingPolicy.IsAllowed(Mode, DbvcOperation.InstallTracker));
```

`CanCommit`과 `CanPush`에는 각각 다음을 `&&`로 더한다:

```csharp
            MappingPolicy.IsAllowed(Mode, DbvcOperation.Commit)
            MappingPolicy.IsAllowed(Mode, DbvcOperation.Push)
```

Pull·이력·원격 확인은 읽기라 건드리지 않는다 — 배포 클론은 오히려 Pull을 해야 한다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS 전부

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/PanelSelector.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/
git commit -m "feat(vsix): 대상 용도에 따라 화면과 명령을 가른다"
```

---

## Task 14: 배포·감사 패널 화면

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs`
- Test: `tests/DBVC.Vsix.Tests/UI/` (기존 `TopRowLayoutTests.cs`와 같은 방식의 XAML 구조 테스트)

**Interfaces:**
- Consumes: Task 13의 `ShowChangeList`/`ShowSetupOverlay`/`ShowDeploymentPanel`, Task 12의 `Deployment.*`
- Produces: 없음 — 화면만 바뀐다.

> **CI가 검증하지 않는 영역이다.** 이 태스크가 끝나도 Task 17의 SSMS 확인 전에는 "동작한다"고 말할 수 없다.

- [ ] **Step 1: 기존 두 패널의 조건을 바꾼다**

`ViewChangesControl.xaml:136`:

```xml
        <Grid Grid.Row="2" Visibility="{Binding ShowChangeList, Converter={StaticResource BoolToVis}}">
```

`ViewChangesControl.xaml:253` (초기화 오버레이):

```xml
        <Grid Grid.Row="2" Visibility="{Binding ShowSetupOverlay, Converter={StaticResource BoolToVis}}"
```

`InverseBoolToVis`는 다른 곳(`IsMapped`)에서 계속 쓰이므로 리소스는 남긴다.

- [ ] **Step 2: 배포·감사 패널을 더한다**

초기화 오버레이 `</Grid>` 바로 뒤, `BlockOverlay` 앞에 넣는다:

```xml
        <!--
            배포·감사 패널. IsInitialized를 보지 않는다 - 운영·테스트 대상은 미초기화가
            정상이고, 거기서 초기화 오버레이가 뜨면 사용자가 누르는 버튼이 곧 금지된
            DDL 트리거 설치다(상위 설계 1.4).
        -->
        <Grid Grid.Row="2" Visibility="{Binding ShowDeploymentPanel, Converter={StaticResource BoolToVis}}"
              Background="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowBackgroundKey}}"
              TextElement.Foreground="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowTextKey}}"
              DataContext="{Binding Deployment}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="5" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <!--
                버튼은 둘뿐이다. "다시 검사"는 새 화면이 아니라 [차이 검사]를 다시 누르는 것이다 -
                별도 단계로 만들면 오히려 안 눌린다.
            -->
            <WrapPanel Grid.Row="0" Margin="5">
                <Button Content="차이 검사" Command="{Binding CompareCommand}" Margin="0,0,5,0" Padding="10,3"
                        ToolTip="원격에서 가져온 뒤 대상 데이터베이스 전체를 브랜치와 비교합니다. 저장소에는 아무것도 쓰지 않습니다."/>
                <Button Content="배포 스크립트 저장..." Command="{Binding SaveScriptCommand}" Margin="0,0,5,0" Padding="10,3"
                        ToolTip="차이 목록으로 실행 가능한 스크립트를 만듭니다. 자동화할 수 없는 항목은 머리말에 사유와 함께 빠집니다."/>
            </WrapPanel>

            <TextBlock Grid.Row="1" Margin="5,0,5,5" TextWrapping="Wrap"
                       Text="{Binding SummaryText}"/>

            <ListView Grid.Row="2" Margin="5,0,5,0"
                      ItemsSource="{Binding Differences}"
                      SelectedItem="{Binding SelectedDifference, Mode=TwoWay}">
                <ListView.View>
                    <GridView>
                        <GridViewColumn Header="객체" Width="240" DisplayMemberBinding="{Binding QualifiedName}"/>
                        <GridViewColumn Header="유형" Width="110" DisplayMemberBinding="{Binding ObjectTypeText}"/>
                        <GridViewColumn Header="상태" Width="160" DisplayMemberBinding="{Binding StateText}"/>
                        <GridViewColumn Header="경로" Width="320" DisplayMemberBinding="{Binding RelativePath}"/>
                    </GridView>
                </ListView.View>
            </ListView>

            <GridSplitter Grid.Row="3" Height="5" HorizontalAlignment="Stretch"/>

            <Grid Grid.Row="4" Margin="5,0,5,5">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="5" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>

                <TextBlock Grid.Row="0" Grid.Column="0" Text="브랜치" FontWeight="SemiBold" Margin="0,0,0,3"/>
                <TextBlock Grid.Row="0" Grid.Column="2" Text="데이터베이스" FontWeight="SemiBold" Margin="0,0,0,3"/>

                <avalonEdit:TextEditor x:Name="DeployLeftEditor" Grid.Row="1" Grid.Column="0"
                                       IsReadOnly="True" ShowLineNumbers="True" SyntaxHighlighting="TSQL"
                                       HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto"/>
                <GridSplitter Grid.Row="1" Grid.Column="1" Width="5" HorizontalAlignment="Stretch"/>
                <avalonEdit:TextEditor x:Name="DeployRightEditor" Grid.Row="1" Grid.Column="2"
                                       IsReadOnly="True" ShowLineNumbers="True" SyntaxHighlighting="TSQL"
                                       HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto"/>
            </Grid>
        </Grid>
```

`avalonEdit:TextEditor`의 속성 이름과 배경/전경 처리는 `ViewChangesControl.xaml`의 기존 diff 에디터를 열어 **그대로 베낀다** — 테마 브러시 처리가 거기 이미 있고, 다르게 쓰면 어두운 테마에서 흰 바탕에 흰 글씨가 된다.

- [ ] **Step 3: 코드 비하인드에서 diff를 그린다**

`ViewChangesControl.xaml.cs`의 기존 `OnSelectionChanged`(121행)가 어떻게 `_diffService.GetDiffModel`로 두 에디터를 채우는지 읽고, 같은 방식으로 배포 쪽 핸들러를 더한다. `DiffService.GetDiffModelFromString(oldText, newText)`가 이미 있으므로 텍스트만 넘기면 된다:

```csharp
        private void OnDeploymentSelectionChanged(object? sender, EventArgs e)
        {
            // 선택된 객체 하나를 다시 뜬다. 비교는 텍스트를 들고 있지 않다 —
            // 객체 수천 개분을 메모리에 쌓지 않으려고 그 자리에서 버렸다.
            var (branchText, databaseText) = _viewModel.Deployment.LoadSelectedTexts();
            var model = _diffService.GetDiffModelFromString(branchText, databaseText);

            // 기존 OnSelectionChanged가 두 에디터에 DiffPane을 넣는 방식을 그대로 따른다.
            ApplyDiffPanes(model, DeployLeftEditor, DeployRightEditor);
        }
```

기존 `OnSelectionChanged`가 인라인으로 하고 있으면 그 본문을 `ApplyDiffPanes(SideBySideDiffModel, TextEditor left, TextEditor right)`로 뽑아 둘이 나눠 쓴다. **복사하지 않는다** — 줄 배경 렌더러(`DiffLineBackgroundRenderer`) 부착이 한쪽에만 남는 일이 실제로 일어난다.

구독·해제는 기존 `_viewModel.SelectionChanged`와 **같은 자리에서** 짝을 맞춘다(39·72·73·87행):

```csharp
            _viewModel.Deployment.SelectionChanged -= OnDeploymentSelectionChanged;
            _viewModel.Deployment.SelectionChanged += OnDeploymentSelectionChanged;
```

`LoadSelectedTexts`는 SMO를 타므로 UI 스레드를 잡는다. **객체 하나짜리라 전체 추출과 다르다** — 기존 diff 경로도 같은 성질이므로 여기서만 백그라운드로 빼지 않는다. Task 17의 SSMS 확인에서 체감상 걸리면 그때 `IBackgroundScheduler`로 옮긴다.

- [ ] **Step 4: XAML 구조 테스트를 더한다**

`tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs`를 열어 그 픽스처가 XAML을 어떻게 읽는지 확인하고, 같은 방식으로 `tests/DBVC.Vsix.Tests/UI/DeploymentPanelLayoutTests.cs`를 만든다:

```csharp
using NUnit.Framework;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 운영 대상에 초기화 오버레이가 뜨면 눌리는 버튼이 곧 금지된 DDL 트리거 설치다.
    /// 렌더링은 CI가 못 보지만 바인딩 이름이 바뀐 것은 볼 수 있다.
    /// </summary>
    [TestFixture]
    public class DeploymentPanelLayoutTests
    {
        [Test]
        public void SetupOverlay_BindsToShowSetupOverlay_NotIsInitialized()
        {
            var xaml = ReadControlXaml();

            Assert.That(xaml, Does.Contain("Binding ShowSetupOverlay"));
            Assert.That(xaml, Does.Not.Contain("Binding IsInitialized"));
        }

        [Test]
        public void DeploymentPanel_IsPresent_AndBindsToItsOwnViewModel()
        {
            var xaml = ReadControlXaml();

            Assert.That(xaml, Does.Contain("Binding ShowDeploymentPanel"));
            Assert.That(xaml, Does.Contain("DataContext=\"{Binding Deployment}\""));
            Assert.That(xaml, Does.Contain("Binding CompareCommand"));
            Assert.That(xaml, Does.Contain("Binding SaveScriptCommand"));
        }

        // ReadControlXaml()은 TopRowLayoutTests가 ViewChangesControl.xaml을 찾는 방식을
        // 그대로 쓴다. 경로 규칙이 두 곳에 생기면 파일이 옮겨질 때 한쪽만 고쳐진다 —
        // 그 픽스처에 헬퍼가 있으면 공용 위치로 뽑아 함께 쓴다.
    }
}
```

- [ ] **Step 5: 빌드하고 테스트한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/UI/ tests/DBVC.Vsix.Tests/UI/
git commit -m "feat(vsix): 배포·감사 대상에 차이 목록과 diff 화면을 띄운다"
```

---

## Task 15: 연결 대화상자가 용도와 고정 브랜치를 받는다

**Files:**
- Modify: `src/DBVC.Vsix/Services/IRepositoryConnectDialog.cs`
- Modify: `src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml`, `.xaml.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`AdoptRepository`/`CloneAndConnect`)
- Modify: `src/DBVC.Core/GitManager.cs`, `src/DBVC.Core/Abstractions.cs` (`CloneRepository`에 브랜치 인자)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` (수정+추가), `tests/DBVC.Core.Tests/GitManagerTests.cs` (추가)

**Interfaces:**
- Produces:
  - `RepositoryConnectRequest.ForExistingFolder(string path, MappingMode mode, string? branch)`
  - `RepositoryConnectRequest.ForClone(string remoteUrl, string targetPath, MappingMode mode, string? branch)`
  - `RepositoryConnectRequest.Mode`, `.Branch`
  - `IGitManager.CloneRepository(string remoteUrl, string targetPath, IProgress<CloneProgress>? progress, CancellationToken cancellationToken, string? branchName = null)` — **선택 인자를 맨 뒤에 붙인다.** 기존 호출부와 2차의 테스트가 그대로 컴파일된다.

> **spec에 없던 것 하나.** clone은 원격 HEAD(보통 `master`)를 체크아웃한다. 사용자가 `develop` 고정 배포 클론을 만들면 받자마자 브랜치 불일치로 차단된다 — 이 흐름의 주 사용례가 첫 사용에서 막힌다. LibGit2Sharp의 `CloneOptions.BranchName`이 이것을 정확히 해결하므로 함께 넣는다.

- [ ] **Step 1: Core의 clone 브랜치 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에 추가. 원격은 로컬 폴더 저장소로 만든다(2차 테스트가 쓰는 방식을 그대로 따른다 — 파일에서 확인하고 이름을 맞춘다):

```csharp
        [Test]
        public void CloneRepository_ChecksOutTheRequestedBranch()
        {
            // 배포 클론은 develop에 고정된다. 원격 HEAD(master)를 받아 두면 받자마자
            // 브랜치 불일치로 차단되고, 사용자는 외부 클라이언트를 다시 꺼내야 한다.
            var originPath = NewTempDir();
            Repository.Init(originPath, isBare: false);
            using (var origin = new Repository(originPath))
            {
                File.WriteAllText(Path.Combine(originPath, "seed.sql"), "-- seed");
                Commands.Stage(origin, "seed.sql");
                origin.Commit("seed", TestSignature, TestSignature);
                origin.CreateBranch("develop");
            }

            var targetPath = Path.Combine(NewTempDir(), "clone");
            var git = new GitManager(new ConfigManager(Path.Combine(NewTempDir(), "mappings.json")));

            git.CloneRepository(originPath, targetPath, null, CancellationToken.None, "develop");

            using var cloned = new Repository(targetPath);
            Assert.That(cloned.Head.FriendlyName, Is.EqualTo("develop"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository_ChecksOutTheRequestedBranch"`
Expected: 컴파일 실패 — `CloneRepository`가 5번째 인자를 받지 않는다.

- [ ] **Step 3: Core를 고친다**

`src/DBVC.Core/GitManager.cs`의 `CloneRepository` 서명에 `string? branchName = null`을 **맨 뒤에** 더하고, `CloneOptions`를 만드는 자리에 넣는다:

```csharp
            // 배포·감사 클론은 특정 브랜치에 고정된다. 원격 HEAD를 받아 두면 받자마자
            // 브랜치 불일치로 차단되어 사용자가 외부 클라이언트를 다시 꺼내야 한다.
            if (!string.IsNullOrWhiteSpace(branchName))
            {
                options.BranchName = branchName;
            }
```

`Abstractions.cs`의 `IGitManager.CloneRepository`에도 같은 선택 인자를 더하고, 주석에 위 이유를 한 줄 남긴다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS 전부

- [ ] **Step 5: 요청 객체를 넓힌다**

`src/DBVC.Vsix/Services/IRepositoryConnectDialog.cs`:

```csharp
        private RepositoryConnectRequest(
            RepositoryConnectKind kind, string? existingPath, string? remoteUrl, string? targetPath,
            MappingMode mode, string? branch)
        {
            Kind = kind;
            ExistingPath = existingPath;
            RemoteUrl = remoteUrl;
            TargetPath = targetPath;
            Mode = mode;
            Branch = branch;
        }

        public static RepositoryConnectRequest ForExistingFolder(string path, MappingMode mode, string? branch) =>
            new RepositoryConnectRequest(RepositoryConnectKind.ExistingFolder, path, null, null, mode, branch);

        public static RepositoryConnectRequest ForClone(string remoteUrl, string targetPath, MappingMode mode, string? branch) =>
            new RepositoryConnectRequest(RepositoryConnectKind.Clone, null, remoteUrl, targetPath, mode, branch);

        /// <summary>이 저장소의 용도. 허용 동작을 정한다.</summary>
        public MappingMode Mode { get; }

        /// <summary>
        /// 고정할 브랜치. 비면 전환이 자유롭다(개발 클론).
        /// 배포·감사에서는 대화상자가 비우지 못하게 막는다 - 고정 없는 배포 클론은
        /// 차단 판정이 막으려던 사고를 그대로 허용한다.
        /// </summary>
        public string? Branch { get; }
```

`using DBVC.Core.Models;`를 더한다.

- [ ] **Step 6: 대화상자에 입력을 더한다**

`RepositoryConnectDialog.xaml`의 기존 두 갈래 아래, 확인 버튼 위에 넣는다:

```xml
        <GroupBox Header="용도" Margin="0,10,0,0">
            <StackPanel Margin="8">
                <RadioButton x:Name="WriteMode" GroupName="Mode" IsChecked="True" Margin="0,0,0,4"
                             Content="개발 — 변경을 추적하고 커밋합니다"/>
                <RadioButton x:Name="DeployMode" GroupName="Mode" Margin="0,0,0,4"
                             Content="배포 — 차이를 검사하고 배포 스크립트를 만듭니다 (커밋하지 않습니다)"/>
                <RadioButton x:Name="AuditMode" GroupName="Mode" Margin="0,0,0,8"
                             Content="감사 — 차이만 확인합니다 (커밋하지 않고 트리거도 설치하지 않습니다)"/>

                <DockPanel>
                    <TextBlock Text="고정 브랜치:" VerticalAlignment="Center" Margin="0,0,6,0"/>
                    <TextBox x:Name="BranchBox"/>
                </DockPanel>
                <TextBlock Margin="0,4,0,0" TextWrapping="Wrap" Foreground="Gray"
                           Text="배포·감사는 브랜치를 반드시 고정해야 합니다(예: 배포는 develop, 감사는 master). 개발은 비워 두면 전환이 자유롭습니다."/>
            </StackPanel>
        </GroupBox>
```

`RepositoryConnectDialog.xaml.cs`의 `Ok_Click`에서 두 갈래 각각이 `Result`를 만들기 **전에** 공통 검증을 돌린다:

```csharp
        private bool TryReadUsage(out MappingMode mode, out string? branch)
        {
            mode = DeployMode.IsChecked == true ? MappingMode.Deploy
                 : AuditMode.IsChecked == true ? MappingMode.Audit
                 : MappingMode.Write;

            branch = string.IsNullOrWhiteSpace(BranchBox.Text) ? null : BranchBox.Text.Trim();

            // 고정 없는 배포 클론은 아무 브랜치나 가리킨 채로 "운영과 다릅니다"를 보고한다.
            if (mode != MappingMode.Write && branch == null)
            {
                ShowError("배포·감사 용도는 고정할 브랜치를 입력해야 합니다.");
                return false;
            }

            return true;
        }
```

두 `Result = ...` 자리를 `if (!TryReadUsage(out var mode, out var branch)) return;` 다음에 `ForExistingFolder(path, mode, branch)` / `ForClone(url, target, mode, branch)`로 고친다.

- [ ] **Step 7: ViewModel이 매핑에 담게 한다**

`ViewChangesViewModel`의 `ConnectRepository` 흐름에서 요청을 처리하는 자리를 고친다. `AdoptRepository(localPath)`가 매핑을 만드는 곳에서 `AddMapping(server, db, path)` 대신 `MappingConfig`를 쓴다:

```csharp
        private void AdoptRepository(string localPath, MappingMode mode, string? branch)
        {
            _configManager.AddMapping(new MappingConfig
            {
                ServerName = ServerName!,
                DatabaseName = DatabaseName!,
                GitPath = localPath,
                Mode = mode,
                Branch = branch
            });

            // 매핑이 생겼으므로 상태를 다시 판정한다. 인증 정보는 이미 저장소에 있다.
            InvalidateActiveContext();
            ApplyContext();
        }
```

`CloneAndConnect`에 `mode`·`branch`를 넘겨 `CloneRepository(remoteUrl, targetPath, progress, token, branch)`를 부르고, 성공 콜백에서 `AdoptRepository(localPath, mode, branch)`를 부른다.

- [ ] **Step 8: 테스트를 고치고 더한다**

`ViewChangesViewModelTests.cs`의 `RecordingConnectDialog`가 만드는 요청에 `MappingMode.Write, null`을 더한다. 그리고 추가한다:

```csharp
        [Test]
        public void ConnectRepository_StoresModeAndBranch_WhenUserPicksDeploy()
        {
            // 손편집으로 두면 오타 한 글자가 Audit으로 떨어지고, 사용자에게는
            // "왜 아무것도 안 되지"로 보인다.
            var repoPath = NewTempGitRepository();
            _connectDialog.RequestToReturn = RepositoryConnectRequest.ForExistingFolder(repoPath, MappingMode.Deploy, "develop");

            var vm = NewViewModelForConnectedTarget();
            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.Is<MappingConfig>(
                m => m.Mode == MappingMode.Deploy && m.Branch == "develop")), Times.Once);
        }
```

`_config`가 `Mock<IConfigManager>`이므로 `AddMapping(MappingConfig)`가 `IConfigManager`에 노출되어 있어야 한다. 없으면 `Abstractions.cs`의 `IConfigManager`에 `void AddMapping(MappingConfig mapping);`을 더한다(`ConfigManager`에는 이미 있다).

- [ ] **Step 9: 통과를 확인한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS 전부

- [ ] **Step 10: 커밋한다**

```bash
git add src/DBVC.Core/GitManager.cs src/DBVC.Core/Abstractions.cs src/DBVC.Vsix/ tests/
git commit -m "feat(vsix): 저장소를 연결할 때 용도와 고정 브랜치를 받는다"
```

---

## Task 16: 실제 SQL Server가 필요한 통합 테스트

**Files:**
- Modify: `tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 5의 `CompareWithRepository`, Task 6의 `ScriptObjectToText`, Task 10의 `ExportFromComparison`

> 로컬 SQL Server에 접속되지 않으면 실패가 아니라 **Skip**이다. 이 파일의 기존 픽스처(`SqlServerTestDatabase`)가 그 판정을 이미 한다.

- [ ] **Step 1: 거짓 양성 테스트를 먼저 쓴다**

**이것이 이 설계 전체가 서 있는 가정이다.** 방식 전체가 "같은 객체를 두 번 뜨면 바이트가 같다"에 기댄다. 흔들리면 전부 `Modified`로 나와 화면이 무의미해진다.

```csharp
        [Test]
        public void CompareWithRepository_ReportsInSync_RightAfterAFullExtraction()
        {
            // 이 설계 전체가 SMO 출력의 결정성에 기댄다. 흔들리면 전부 Modified로 나오고
            // 화면이 무의미해진다. 깨지면 대비책은 텍스트 정규화(BOM·개행·후행 공백) 비교로
            // 떨어뜨리는 것이다.
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            db.Execute("CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL)");

            var repoPath = NewTempDir();
            var config = NewConfig(db, repoPath, MappingMode.Write);
            new SmoManager(config).ScriptObjectsDetailed(db.ServerName, db.DatabaseName);

            // 비교는 mode가 write가 아니어야 돈다. 같은 저장소를 배포 용도로 다시 매핑한다.
            var deployConfig = NewConfig(db, repoPath, MappingMode.Deploy);
            var result = new SmoManager(deployConfig).CompareWithRepository(db.ServerName, db.DatabaseName);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Differences.Select(d => d.QualifiedName), Is.Empty);
            Assert.That(result.ComparedCount, Is.GreaterThan(0));
        }
```

`NewConfig(db, repoPath, mode)`는 이 픽스처에 헬퍼로 더한다 — `MappingConfig`에 `ServerName`/`DatabaseName`/`GitPath`/`Mode`를 채워 `ConfigManager.AddMapping`을 부르고, `Branch`는 비운다(저장소가 아직 커밋되지 않았을 수 있으므로 고정하지 않는다).

- [ ] **Step 2: 나머지 통합 테스트를 쓴다**

```csharp
        [Test]
        public void CompareWithRepository_WritesNothingIntoTheRepository()
        {
            // 저장소를 건드리지 않는다는 것이 이 방식을 고른 이유다. 한 글자라도 쓰면
            // 되돌리는 단계가 필요해지고, 그 단계가 실패하는 날 작업 트리가 망가진다.
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");

            var repoPath = NewTempDir();
            var deployConfig = NewConfig(db, repoPath, MappingMode.Deploy);

            new SmoManager(deployConfig).CompareWithRepository(db.ServerName, db.DatabaseName);

            Assert.That(Directory.GetFileSystemEntries(repoPath), Is.Empty);
        }

        [Test]
        public void CompareWithRepository_ReportsOnlyTheAlteredObject_AsModified()
        {
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            db.Execute("CREATE PROCEDURE dbo.GetTwo AS SELECT 2");

            var repoPath = NewTempDir();
            new SmoManager(NewConfig(db, repoPath, MappingMode.Write))
                .ScriptObjectsDetailed(db.ServerName, db.DatabaseName);

            db.Execute("ALTER PROCEDURE dbo.GetOne AS SELECT 99");

            var result = new SmoManager(NewConfig(db, repoPath, MappingMode.Deploy))
                .CompareWithRepository(db.ServerName, db.DatabaseName);

            Assert.That(result!.Differences.Count, Is.EqualTo(1));
            Assert.That(result.Differences[0].QualifiedName, Is.EqualTo("dbo.GetOne"));
            Assert.That(result.Differences[0].State, Is.EqualTo(ObjectDiffState.Modified));
        }

        [Test]
        public void CompareWithRepository_ReportsMissingInBranch_WhenTheFileWasDeleted()
        {
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");

            var repoPath = NewTempDir();
            new SmoManager(NewConfig(db, repoPath, MappingMode.Write))
                .ScriptObjectsDetailed(db.ServerName, db.DatabaseName);
            File.Delete(Path.Combine(repoPath, "dbo", "StoredProcedures", "GetOne.sql"));

            var result = new SmoManager(NewConfig(db, repoPath, MappingMode.Deploy))
                .CompareWithRepository(db.ServerName, db.DatabaseName);

            var one = result!.Differences.Single(d => d.QualifiedName == "dbo.GetOne");
            Assert.That(one.State, Is.EqualTo(ObjectDiffState.MissingInBranch));
        }

        [Test]
        public void CompareWithRepository_ReportsMissingInDatabase_WhenTheObjectWasDropped()
        {
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");

            var repoPath = NewTempDir();
            new SmoManager(NewConfig(db, repoPath, MappingMode.Write))
                .ScriptObjectsDetailed(db.ServerName, db.DatabaseName);
            db.Execute("DROP PROCEDURE dbo.GetOne");

            var result = new SmoManager(NewConfig(db, repoPath, MappingMode.Deploy))
                .CompareWithRepository(db.ServerName, db.DatabaseName);

            var one = result!.Differences.Single(d => d.QualifiedName == "dbo.GetOne");
            Assert.That(one.State, Is.EqualTo(ObjectDiffState.MissingInDatabase));
        }

        [Test]
        public void GeneratedDeploymentScript_RunsAgainstADatabaseThatAlreadyHasTheObjects()
        {
            // 저장소 파일이 CREATE OR ALTER로 저장되어 있다는 1차의 결정이 실제로
            // 실행 가능한 스크립트를 만드는지 확인하는 유일한 자리다.
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            db.Execute("CREATE VIEW dbo.OneView AS SELECT 1 AS N");

            var repoPath = NewTempDir();
            var config = NewConfig(db, repoPath, MappingMode.Write);
            new SmoManager(config).ScriptObjectsDetailed(db.ServerName, db.DatabaseName);

            db.Execute("ALTER PROCEDURE dbo.GetOne AS SELECT 42");

            var deployConfig = NewConfig(db, repoPath, MappingMode.Deploy);
            var result = new SmoManager(deployConfig).CompareWithRepository(db.ServerName, db.DatabaseName);
            var export = new ScriptExporter(deployConfig, new GitManager(deployConfig))
                .ExportFromComparison(db.ServerName, db.DatabaseName, result!.Differences, DateTimeOffset.Now);

            Assert.That(export.HasContent, Is.True);

            // 객체가 이미 있는 DB에 그대로 실행한다. "이미 있습니다"가 나오면 실패다.
            Assert.DoesNotThrow(() => db.ExecuteScript(export.Script));

            // 실행 뒤에는 저장소와 일치해야 한다. 3단계 루프가 실제로 닫히는지 본다.
            var after = new SmoManager(deployConfig).CompareWithRepository(db.ServerName, db.DatabaseName);
            Assert.That(after!.Differences.Select(d => d.QualifiedName), Does.Not.Contain("dbo.GetOne"));
        }

        [Test]
        public void ScriptObjectToText_ReturnsTheCurrentDefinition_WithoutTouchingTheRepository()
        {
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");

            var repoPath = NewTempDir();
            var config = NewConfig(db, repoPath, MappingMode.Deploy);

            var text = new SmoManager(config).ScriptObjectToText(db.ServerName, db.DatabaseName, "dbo.GetOne");

            Assert.That(text, Does.Contain("GetOne"));
            Assert.That(Directory.GetFileSystemEntries(repoPath), Is.Empty);
        }

        [Test]
        public void CompareWithRepository_Throws_WhenCancelledMidway()
        {
            using var db = SqlServerTestDatabase.CreateOrSkip();
            db.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            db.Execute("CREATE PROCEDURE dbo.GetTwo AS SELECT 2");

            var repoPath = NewTempDir();
            var config = NewConfig(db, repoPath, MappingMode.Deploy);

            using var cts = new CancellationTokenSource();
            var progress = new SimpleProgress<ExtractionProgress>(_ => cts.Cancel());

            Assert.Throws<OperationCanceledException>(
                () => new SmoManager(config).CompareWithRepository(db.ServerName, db.DatabaseName, progress, cts.Token));
        }
```

`db.ExecuteScript(...)`는 `GO`로 나눠 실행하는 헬퍼다. `SqlServerTestDatabase`에 없으면 더한다 — `Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)`로 나누고 빈 조각을 건너뛰며 `Execute`한다. `SimpleProgress<T>`가 없으면 `IProgress<T>`를 구현하는 세 줄짜리 중첩 클래스로 더한다.

- [ ] **Step 3: 로컬 SQL Server로 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net48 --filter "FullyQualifiedName~SmoManagerIntegrationTests"`
Expected: SQL Server가 있으면 PASS, 없으면 Skip.

**첫 테스트가 실패하면 멈추고 보고한다.** SMO 출력이 결정적이지 않다는 뜻이고, 그러면 `HasSameBytes` 대신 텍스트 정규화 비교로 떨어뜨려야 한다 — 설계의 대비책이 발동하는 지점이다.

- [ ] **Step 4: 커밋한다**

```bash
git add tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs tests/DBVC.Core.Tests/SqlServerTestDatabase.cs
git commit -m "test(core): 실제 서버에서 차이 검사와 배포 스크립트를 확인한다"
```

---

## Task 17: 문서, 버전, SSMS 확인

**Files:**
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest`
- Modify: `docs/superpowers/plans/2026-08-27-dbvc-deploy-and-audit.md` (이 파일 — 체크박스를 닫는다)

- [ ] **Step 1: `README.md`를 고친다**

다음을 담는다.

- 저장소 세 벌 배치: `...\dbvc\dev`(개발 DB, 브랜치 자유) / `...\dbvc\test`(테스트 DB, `develop` 고정) / `...\dbvc\prod`(운영 DB, `master` 고정). 한 PC에 셋이 공존해도 폴더가 나뉘어 있으면 간섭하지 않는다.
- 용도별로 되는 것과 안 되는 것: Task 1의 표를 사용자 말로 옮긴다.
- 배포 3단계 루프: `[차이 검사]` → `[배포 스크립트 저장...]` → SSMS 쿼리 창에서 실행 → 다시 `[차이 검사]`.
- **DBVC는 스크립트를 실행하지 않는다.** 실행은 사람이 한다.
- **기존 테이블 등 `CREATE OR ALTER`가 안 되는 타입은 스크립트에서 빠진다.** 사유는 파일 머리말에 있다.
- 감사 대상의 차이는 "미배포"인지 "무단 변경"인지 구분되지 않는다. 둘 다 "확인 필요"다.

- [ ] **Step 2: `docs/setup-checklist.md`를 고친다**

- 배포·감사 클론 만들기: 저장소 연결 대화상자에서 용도와 고정 브랜치를 고른다. **배포·감사는 브랜치가 필수다.**
- 상위 설계 6장의 운영 규칙을 싣는다: DB 변경은 짧게 산다 / 같은 객체 동시 작업은 조율한다 / `hotfix/*`의 DB 변경 세 선택지 / `develop` 리셋 정책 / 한 사람이 한 PC를 쓰는가 / 공용 계정의 권한 범위.
- **드리프트 검사 주기는 도구가 아니라 조직이 정한다.** 얼마나 자주 `[차이 검사]`를 돌릴지 적는다.
- 배포·감사 저장소에 커밋되지 않은 변경이 있으면 화면이 차단된다는 것과 그때 할 일(외부 클라이언트에서 되돌리기).

- [ ] **Step 3: 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest`의 `Version`을 **0.5.0**으로 바꾼다. `src/DBVC.Vsix/DbvcVersion.cs`에 같은 값이 있으면 함께 올리고, `DbvcVersionTests`가 둘을 대조하는지 확인한다.

- [ ] **Step 4: 전체를 빌드하고 테스트한다**

Run:
```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests -f net48
dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release
dir src\DBVC.Vsix\bin\Release\net48\*.vsix
```
Expected: 전부 PASS, `.vsix` 산출물 존재. **빌드 성공 ≠ `.vsix` 생성**이므로 파일을 눈으로 확인한다. 없으면 개발자 셸에서 `msbuild src/DBVC.Vsix/DBVC.Vsix.csproj -restore -p:Configuration=Release`로 한 번 더 본다.

- [ ] **Step 5: SSMS 21에서 직접 눌러 본다**

CI가 검증하지 않는 영역이다. **여기를 통과하기 전에는 "동작한다"고 말하지 않는다.**

- [ ] `mode = deploy`/`audit` 대상에서 **초기화 오버레이가 뜨지 않는가** (상위 설계 1.4)
- [ ] 배포·감사 패널에서 커밋·Push·새로고침·초기화 버튼이 **잠겨 있는가**
- [ ] 차단 오버레이(브랜치 불일치, 미커밋 변경)가 **배포 패널도 덮는가**
- [ ] `[차이 검사]` 중 SSMS가 잠기지 않는가 — 개체 탐색기를 클릭해 본다
- [ ] 검사 중 취소 버튼이 실제로 먹는가
- [ ] 목록에서 항목을 고르면 좌우 diff가 뜨는가 (브랜치 / 데이터베이스)
- [ ] `[배포 스크립트 저장...]`이 파일을 만들고, 머리말에 제외 사유가 사유별로 나뉘어 있는가
- [ ] 저장한 스크립트를 SSMS 쿼리 창에서 실행한 뒤 `[차이 검사]`를 다시 누르면 그 항목이 사라지는가
- [ ] 연결 대화상자에서 배포/감사를 고르고 브랜치를 비우면 거부되는가
- [ ] 원격에서 `develop`을 고정해 받으면 받은 직후 차단되지 않는가 (Task 15의 clone 브랜치)

- [ ] **Step 6: 커밋한다**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest src/DBVC.Vsix/DbvcVersion.cs
git commit -m "docs: 배포와 감사를 문서에 반영하고 0.5.0으로 올린다"
```

- [ ] **Step 7: 계획 문서를 닫는다**

이 파일의 체크박스가 모두 채워졌는지 확인하고, 남은 것이 있으면 사유를 적는다.

```bash
git add docs/superpowers/plans/2026-08-27-dbvc-deploy-and-audit.md
git commit -m "docs(plan): 배포와 감사 구현 계획을 완료로 닫는다"
```

---

## 상위 설계 문서에 반영할 것

3차가 끝나면 `docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md`의 두 자리를 고친다. 근거는 3차 spec의 2장에 있다.

- 3.3 표: `deploy`·`audit`의 추출 "O (전체만)" → **X** (비교가 저장소에 쓰지 않는다)
- 3.6: 제외 대상 "테이블" → **`CREATE OR ALTER` 미지원 타입 전부**

그리고 7.3의 남은 항목은 **경고 A(3.10)** 하나다.
