# DBVC 형상 관리 1차 구현 계획 — 안전한 커밋

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 공용 개발 DB에서 여러 사람이 같은 계정으로 작업해도 각자 자기 변경만 정확히 골라 커밋할 수 있게 하고, 저장소를 만들기 전에 추출 형식과 매핑 형식을 확정한다.

**Architecture:** 사람을 가르는 축을 SQL 로그인이 아니라 접속 PC(`HOST_NAME()`)로 잡는다. DDL 트리거가 `HostName`·`ClientNetAddress`를 기록하도록 스키마를 v3으로 올리고, `StateTracker`의 pending 조회와 처리 완료 표시를 그 축으로 좁힌다. 저장소가 만들어지기 전에 SMO 추출을 `CREATE OR ALTER`로 바꾸고 `mappings.json`에 `branch`·`mode`를 더해 나중에 형식 마이그레이션이 필요 없게 한다.

**Tech Stack:** .NET Standard 2.0 + .NET Framework 4.8 (Core), WPF/MVVM (Vsix), LibGit2Sharp 0.32.0, Microsoft.Data.SqlClient 5.1.5, SMO 171.30.0, System.Text.Json 10.0.3, NUnit 4 + Moq

**Spec:** `docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md` (§3.2, §3.4, §3.5, §3.9, §3.10 경고 B, §3.12 일부 — spec §7.1이 정한 1차 범위)

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip, 컬럼명 포함. Core는 상태를 영어 식별자로 다루고 화면 계층에서만 한국어로 옮긴다.
- **주석은 "왜"만 적는다.** 한국어 평서문. 함정과 근거를 남기는 기존 문체를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): 메모리 전용 자격증명 저장소를 더한다`
- **테스트 이름은 영어** `Method_Result_WhenCondition` 형태다.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`은 SSMS 21이 먼저 올리는 어셈블리에 맞춘 값이다. 올리면 어떤 DB에도 접속되지 않는다.
- **테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.** 전이 참조로만 받는다.
- `dotnet test tests/DBVC.Core.Tests -f net10.0` 이 기본 실행 명령이다. `net48`은 Windows에서만 돈다.
- SQL Server 통합 테스트(`SqlServerTestDatabase`)는 `localhost`에 붙지 못하면 **실패가 아니라 Skip**이다. Skip은 통과가 아니다 — 4.1 검증 항목은 실제 서버에서 한 번은 돌려야 한다.
- **`mode`는 1차에서 저장·직렬화만 하고 강제하지 않는다.** 동작 제한은 3차(spec §3.3)다. 지금 필드를 넣는 이유는 사용자가 1차에서 만든 `mappings.json`을 나중에 마이그레이션하지 않기 위해서다.

## 파일 구조

| 파일 | 책임 | 상태 |
|---|---|---|
| `src/DBVC.Core/Models/MappingConfig.cs` | 매핑 한 건의 값. `Branch`·`Mode` 추가 | 수정 |
| `src/DBVC.Core/Models/MappingMode.cs` | `Write`/`Deploy`/`Audit` 열거형 | 신규 |
| `src/DBVC.Core/Models/MappingModeConverter.cs` | 모르는 문자열을 가장 제한적인 값으로 읽는 JSON 변환기 | 신규 |
| `src/DBVC.Core/Models/MappingConfigSerializer.cs` | 변환기 등록 | 수정 |
| `src/DBVC.Core/RepositoryStateEvaluator.cs` | 차단 판정 순수 함수 + 한국어 사유 | 신규 |
| `src/DBVC.Core/Models/RepositoryState.cs` | 저장소 상태 값 객체 | 신규 |
| `src/DBVC.Core/GitManager.cs` | `GetRepositoryState` 추가 | 수정 |
| `src/DBVC.Core/Abstractions.cs` | `IGitManager`·`IStateTracker` 확장 | 수정 |
| `src/DBVC.Core/SmoManager.cs` | `ScriptForCreateOrAlter` | 수정 |
| `src/DBVC.Database/InstallTrigger.sql` | `HostName`·`ClientNetAddress` 기록, 버전 3 | 수정 |
| `src/DBVC.Core/StateTracker.cs` | 작업자 조회·필터·처리 완료 표시 | 수정 |
| `src/DBVC.Core/Models/ChangeRecord.cs` | `ChangeLogRow`·`ChangeRecord`에 작업자 필드 | 수정 |
| `src/DBVC.Core/CoAuthorDetector.cs` | 경고 B 판정 순수 함수 | 신규 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 브랜치 표시, 차단, 토글, 경고 B | 수정 |
| `src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs` | 변경자 표시 | 수정 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 브랜치 · 토글 · 변경자 컬럼 · 차단 오버레이 | 수정 |

작업 순서는 의존 방향을 따른다: 매핑(Task 1) → 저장소 상태(Task 2·3) → 추출 형식(Task 4) → 트리거(Task 5) → 필터(Task 6·7) → 경고 B(Task 8).

---

### Task 1: 매핑에 `Branch`·`Mode`를 더한다

**Files:**
- Create: `src/DBVC.Core/Models/MappingMode.cs`
- Create: `src/DBVC.Core/Models/MappingModeConverter.cs`
- Modify: `src/DBVC.Core/Models/MappingConfig.cs`
- Modify: `src/DBVC.Core/Models/MappingConfigSerializer.cs`
- Test: `tests/DBVC.Core.Tests/MappingConfigSerializerTests.cs` (신규)

**Interfaces:**
- Consumes: 없음 (첫 작업)
- Produces:
  - `enum DBVC.Core.Models.MappingMode { Write = 0, Deploy = 1, Audit = 2 }`
  - `MappingConfig.Branch` → `string?`
  - `MappingConfig.Mode` → `MappingMode`
  - `MappingConfigSerializer.Serialize(IReadOnlyList<MappingConfig>)` → `string` (기존 시그니처 유지)
  - `MappingConfigSerializer.Deserialize(string)` → `List<MappingConfig>?` (기존 시그니처 유지)

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/MappingConfigSerializerTests.cs` 를 만든다.

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// mappings.json은 사용자가 손으로 고칠 수 있는 파일이다. 값이 빠지거나 틀렸을 때
    /// 어느 쪽으로 실패하는지가 안전에 직결되므로 여기서 못박는다.
    /// </summary>
    [TestFixture]
    public class MappingConfigSerializerTests
    {
        [Test]
        public void Deserialize_DefaultsToWriteAndFreeBranch_WhenFieldsAreAbsent()
        {
            // 0.2.x가 만든 파일이다. 이 형식이 그대로 읽히지 않으면 기존 사용자의 매핑이 전부 사라진다.
            var json = @"[{""ServerName"":""localhost"",""DatabaseName"":""db1"",""GitPath"":""C:\\repo""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result![0].Mode, Is.EqualTo(MappingMode.Write));
            Assert.That(result[0].Branch, Is.Null);
        }

        [Test]
        public void Deserialize_ReadsBranchAndMode_WhenPresent()
        {
            var json = @"[{""ServerName"":""s"",""DatabaseName"":""d"",""GitPath"":""p"",""Branch"":""master"",""Mode"":""Audit""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result![0].Branch, Is.EqualTo("master"));
            Assert.That(result[0].Mode, Is.EqualTo(MappingMode.Audit));
        }

        [Test]
        public void Deserialize_FallsBackToAudit_WhenModeIsUnknown()
        {
            // 오타로 권한이 넓어지면 안 된다. 모르는 값은 가장 제한적인 쪽으로 읽는다.
            var json = @"[{""ServerName"":""s"",""DatabaseName"":""d"",""GitPath"":""p"",""Mode"":""audi""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result![0].Mode, Is.EqualTo(MappingMode.Audit));
        }

        [Test]
        public void Deserialize_IsCaseInsensitiveForMode()
        {
            var json = @"[{""ServerName"":""s"",""DatabaseName"":""d"",""GitPath"":""p"",""Mode"":""deploy""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result![0].Mode, Is.EqualTo(MappingMode.Deploy));
        }

        [Test]
        public void Serialize_WritesModeAsString()
        {
            var mappings = new List<MappingConfig>
            {
                new MappingConfig { ServerName = "s", DatabaseName = "d", GitPath = "p", Mode = MappingMode.Deploy }
            };

            var json = MappingConfigSerializer.Serialize(mappings);

            // 숫자로 나가면 사람이 파일을 읽고 고칠 수 없다.
            Assert.That(json, Does.Contain("\"Deploy\""));
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingConfigSerializerTests"`
Expected: 컴파일 실패 — `MappingMode` 형식이 없고 `MappingConfig.Branch`·`Mode` 속성이 없다.

- [x] **Step 3: 열거형을 만든다**

`src/DBVC.Core/Models/MappingMode.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// 매핑 대상에 허용되는 동작의 범위. 값의 순서는 제한이 강해지는 순서다 —
    /// 모르는 값을 만났을 때 가장 제한적인 쪽으로 떨어뜨리는 근거가 된다.
    ///
    /// 1차에서는 저장·직렬화만 하고 동작을 막지는 않는다. 지금 필드를 넣는 이유는
    /// 사용자가 만든 mappings.json을 나중에 마이그레이션하지 않기 위해서다.
    /// </summary>
    public enum MappingMode
    {
        /// <summary>개발 DB. 추출·커밋·Push·트리거 설치가 모두 허용된다.</summary>
        Write = 0,

        /// <summary>테스트 DB. 차이 검사와 배포 스크립트 생성만 한다.</summary>
        Deploy = 1,

        /// <summary>운영 DB. 차이 검사만 한다.</summary>
        Audit = 2
    }
}
```

- [x] **Step 4: 변환기를 만든다**

`src/DBVC.Core/Models/MappingModeConverter.cs`:

```csharp
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBVC.Core.Models
{
    /// <summary>
    /// <see cref="MappingMode"/>를 문자열로 읽고 쓴다.
    ///
    /// 기본 열거형 변환기를 쓰지 않는 이유는 실패 방향 때문이다. 기본 변환기는 모르는 문자열에
    /// 예외를 던지고, 그러면 mappings.json 한 줄의 오타가 전체 매핑을 날린다. 여기서는
    /// <see cref="MappingMode.Audit"/>으로 떨어뜨린다 — 오타 때문에 권한이 넓어지는 것보다
    /// 좁아지는 편이 안전하다.
    ///
    /// 속성 자체가 없는 경우(0.2.x가 만든 파일)는 이 변환기가 호출되지 않고 C# 기본값인
    /// <see cref="MappingMode.Write"/>가 남는다. 값이 빠진 것과 값이 틀린 것은 다르게 다뤄야 한다.
    /// </summary>
    internal sealed class MappingModeConverter : JsonConverter<MappingMode>
    {
        public override MappingMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

            return Enum.TryParse<MappingMode>(raw, ignoreCase: true, out var parsed)
                && Enum.IsDefined(typeof(MappingMode), parsed)
                    ? parsed
                    : MappingMode.Audit;
        }

        public override void Write(Utf8JsonWriter writer, MappingMode value, JsonSerializerOptions options)
        {
            // 숫자로 쓰면 사람이 파일을 읽고 고칠 수 없다.
            writer.WriteStringValue(value.ToString());
        }
    }
}
```

- [x] **Step 5: 모델과 직렬화기를 고친다**

`src/DBVC.Core/Models/MappingConfig.cs` 를 통째로 바꾼다:

```csharp
namespace DBVC.Core.Models
{
    public class MappingConfig
    {
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string GitPath { get; set; } = string.Empty;

        /// <summary>
        /// 이 저장소가 고정되어야 할 브랜치. 비면 전환이 자유롭다(개발 클론).
        ///
        /// 감사·배포용 클론에서 이 값이 어긋난 채로 비교하면 화면이 조용히 거짓말을 한다 —
        /// 운영 폴더가 develop을 가리키면 개발과 운영의 모든 차이가 "무단 변경"으로 보고된다.
        /// 그래서 판정 결과는 경고가 아니라 차단이다(RepositoryStateEvaluator).
        /// </summary>
        public string? Branch { get; set; }

        /// <summary>허용 동작의 범위. 값이 없는 구버전 파일은 <see cref="MappingMode.Write"/>로 읽힌다.</summary>
        public MappingMode Mode { get; set; } = MappingMode.Write;
    }
}
```

`src/DBVC.Core/Models/MappingConfigSerializer.cs` 의 `Options` 초기화만 바꾼다:

```csharp
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new MappingModeConverter() }
        };
```

- [x] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingConfigSerializerTests"`
Expected: 5개 PASS

- [x] **Step 7: 기존 매핑 테스트가 깨지지 않았는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~ConfigManagerTests"`
Expected: 전부 PASS. 실패하면 하위호환이 깨진 것이므로 되돌리고 원인을 찾는다.

- [x] **Step 8: 커밋**

```bash
git add src/DBVC.Core/Models/ tests/DBVC.Core.Tests/MappingConfigSerializerTests.cs
git commit -m "feat(core): 매핑에 고정 브랜치와 동작 범위를 더한다"
```

---

### Task 2: 저장소 차단 판정을 순수 함수로 만든다

**Files:**
- Create: `src/DBVC.Core/Models/RepositoryState.cs`
- Create: `src/DBVC.Core/RepositoryStateEvaluator.cs`
- Test: `tests/DBVC.Core.Tests/RepositoryStateEvaluatorTests.cs` (신규)

**Interfaces:**
- Consumes: Task 1의 `MappingConfig.Branch`
- Produces:
  - `enum DBVC.Core.Models.RepositoryBlockReason { None, OperationInProgress, DetachedHead, BranchMismatch }`
  - `class DBVC.Core.Models.RepositoryState { string? CurrentBranch; bool IsDetached; string? PendingOperation; RepositoryBlockReason BlockReason; string? BlockMessage; }`
  - `static RepositoryBlockReason RepositoryStateEvaluator.Evaluate(string? currentBranch, bool isDetached, string? pendingOperation, string? expectedBranch)`
  - `static string? RepositoryStateEvaluator.BuildMessage(RepositoryBlockReason reason, string? currentBranch, string? expectedBranch, string? pendingOperation)`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/RepositoryStateEvaluatorTests.cs`:

```csharp
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// DBVC는 저장소의 유일한 주인이 아니다. 외부 Git 클라이언트가 브랜치를 바꾸거나
    /// 병합을 중간에 남겨 둔 저장소를 열게 되고, 그 상태에서 비교하면 조용히 틀린 결과가 나온다.
    /// </summary>
    [TestFixture]
    public class RepositoryStateEvaluatorTests
    {
        [Test]
        public void Evaluate_ReturnsNone_WhenBranchMatches()
        {
            var reason = RepositoryStateEvaluator.Evaluate("master", false, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_ReturnsNone_WhenExpectedBranchIsEmpty()
        {
            // 개발 클론은 브랜치를 자유롭게 전환한다. 고정이 없으면 어느 브랜치든 정상이다.
            var reason = RepositoryStateEvaluator.Evaluate("feature/x", false, null, null);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_ReturnsBranchMismatch_WhenBranchDiffers()
        {
            var reason = RepositoryStateEvaluator.Evaluate("develop", false, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.BranchMismatch));
        }

        [Test]
        public void Evaluate_IgnoresCase_WhenComparingBranch()
        {
            var reason = RepositoryStateEvaluator.Evaluate("Master", false, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_ReturnsDetachedHead_EvenWhenNoBranchIsExpected()
        {
            // 고정이 없어도 detached는 막는다 - 커밋해도 어느 브랜치에도 남지 않는다.
            var reason = RepositoryStateEvaluator.Evaluate(null, true, null, null);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.DetachedHead));
        }

        [Test]
        public void Evaluate_PrefersOperationInProgress_OverBranchMismatch()
        {
            // 병합 중이면 브랜치 이름이 맞아도 작업 트리가 중간 상태다. 그쪽을 먼저 알려야
            // 사용자가 "브랜치를 바꾸면 되겠구나"로 오해하지 않는다.
            var reason = RepositoryStateEvaluator.Evaluate("develop", false, "Merge", "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.OperationInProgress));
        }

        [Test]
        public void BuildMessage_NamesBothBranches_WhenBranchMismatch()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.BranchMismatch, "develop", "master", null);

            Assert.That(message, Does.Contain("develop"));
            Assert.That(message, Does.Contain("master"));
        }

        [Test]
        public void BuildMessage_ReturnsNull_WhenNotBlocked()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.None, "master", "master", null);

            Assert.That(message, Is.Null);
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RepositoryStateEvaluatorTests"`
Expected: 컴파일 실패 — `RepositoryStateEvaluator`·`RepositoryBlockReason`이 없다.

- [x] **Step 3: 값 객체를 만든다**

`src/DBVC.Core/Models/RepositoryState.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// 차단 사유. 값의 순서가 곧 우선순위다 — 여럿이 겹치면 작은 값을 알린다.
    /// </summary>
    public enum RepositoryBlockReason
    {
        None = 0,

        /// <summary>병합·리베이스 등이 끝나지 않았다. 작업 트리가 중간 상태다.</summary>
        OperationInProgress = 1,

        /// <summary>어느 브랜치도 가리키지 않는다. 커밋해도 어디에도 남지 않는다.</summary>
        DetachedHead = 2,

        /// <summary>매핑이 고정한 브랜치와 다르다.</summary>
        BranchMismatch = 3
    }

    /// <summary>
    /// 저장소를 열었을 때의 상태 한 벌. UI는 <see cref="BlockReason"/>만 보고 화면을 덮는다.
    /// </summary>
    public class RepositoryState
    {
        /// <summary>현재 브랜치 이름. detached이면 null이다.</summary>
        public string? CurrentBranch { get; set; }

        public bool IsDetached { get; set; }

        /// <summary>진행 중인 작업 이름(<c>Merge</c> 등). 없으면 null이다.</summary>
        public string? PendingOperation { get; set; }

        public RepositoryBlockReason BlockReason { get; set; }

        /// <summary>차단되지 않았으면 null. 그 외에는 사용자에게 그대로 보일 한국어 사유다.</summary>
        public string? BlockMessage { get; set; }
    }
}
```

- [x] **Step 4: 판정 함수를 만든다**

`src/DBVC.Core/RepositoryStateEvaluator.cs`:

```csharp
using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 저장소를 그대로 써도 되는지 판정한다. LibGit2Sharp에 닿지 않는 순수 함수라
    /// 저장소 없이 테스트된다 — 판정 로직은 여기, 값 읽기는 GitManager가 맡는다.
    /// </summary>
    public static class RepositoryStateEvaluator
    {
        public static RepositoryBlockReason Evaluate(
            string? currentBranch, bool isDetached, string? pendingOperation, string? expectedBranch)
        {
            // 병합 중이면 브랜치 이름이 맞아도 작업 트리가 중간 상태다. 브랜치 불일치보다
            // 먼저 알려야 사용자가 "브랜치를 바꾸면 되겠구나"로 오해하지 않는다.
            if (!string.IsNullOrWhiteSpace(pendingOperation))
            {
                return RepositoryBlockReason.OperationInProgress;
            }

            // 고정이 없어도 막는다. detached에서 커밋하면 어느 브랜치에도 남지 않는다.
            if (isDetached)
            {
                return RepositoryBlockReason.DetachedHead;
            }

            // 고정이 없으면 어느 브랜치든 정상이다(개발 클론).
            if (string.IsNullOrWhiteSpace(expectedBranch))
            {
                return RepositoryBlockReason.None;
            }

            return string.Equals(currentBranch, expectedBranch, StringComparison.OrdinalIgnoreCase)
                ? RepositoryBlockReason.None
                : RepositoryBlockReason.BranchMismatch;
        }

        public static string? BuildMessage(
            RepositoryBlockReason reason, string? currentBranch, string? expectedBranch, string? pendingOperation)
        {
            switch (reason)
            {
                case RepositoryBlockReason.OperationInProgress:
                    return $"저장소에 끝나지 않은 작업({pendingOperation})이 남아 있어 DBVC를 사용할 수 없습니다. " +
                           "Git 클라이언트에서 그 작업을 끝내거나 되돌린 뒤 다시 시도하세요.";

                case RepositoryBlockReason.DetachedHead:
                    return "저장소가 어느 브랜치도 가리키지 않는 상태(detached HEAD)여서 DBVC를 사용할 수 없습니다. " +
                           "Git 클라이언트에서 브랜치를 체크아웃한 뒤 다시 시도하세요.";

                case RepositoryBlockReason.BranchMismatch:
                    return $"이 대상은 '{expectedBranch}' 브랜치에 고정되어 있는데 저장소는 '{currentBranch}'에 있습니다. " +
                           "그대로 두면 비교 결과가 사실과 달라지므로 중단했습니다. " +
                           $"Git 클라이언트에서 '{expectedBranch}'를 체크아웃한 뒤 다시 시도하세요.";

                default:
                    return null;
            }
        }
    }
}
```

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RepositoryStateEvaluatorTests"`
Expected: 8개 PASS

- [x] **Step 6: 커밋**

```bash
git add src/DBVC.Core/RepositoryStateEvaluator.cs src/DBVC.Core/Models/RepositoryState.cs tests/DBVC.Core.Tests/RepositoryStateEvaluatorTests.cs
git commit -m "feat(core): 저장소를 그대로 써도 되는지 판정하는 순수 함수를 더한다"
```

---

### Task 3: `GitManager`가 저장소 상태를 읽는다

**Files:**
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: Task 2의 `RepositoryStateEvaluator`, `RepositoryState`; Task 1의 `MappingConfig.Branch`
- Produces: `RepositoryState? IGitManager.GetRepositoryState(string serverName, string databaseName)` — 매핑이 없으면 `null`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs` 의 클래스 안에 더한다. 이 파일의 기존 테스트가 저장소를 어떻게 만드는지 먼저 읽고 같은 헬퍼를 쓴다(임시 폴더에 `Repository.Init` 후 커밋 하나를 만드는 형태다).

```csharp
        [Test]
        public void GetRepositoryState_ReportsCurrentBranch_WhenRepositoryIsClean()
        {
            var (config, repoPath) = NewRepositoryWithCommit();
            var git = new GitManager(config);

            var state = git.GetRepositoryState("srv", "db");

            Assert.That(state, Is.Not.Null);
            Assert.That(state!.IsDetached, Is.False);
            Assert.That(state.CurrentBranch, Is.Not.Null.And.Not.Empty);
            Assert.That(state.BlockReason, Is.EqualTo(RepositoryBlockReason.None));
            Assert.That(state.BlockMessage, Is.Null);
        }

        [Test]
        public void GetRepositoryState_BlocksWithMessage_WhenBranchDiffersFromMapping()
        {
            var (config, repoPath) = NewRepositoryWithCommit();

            // 실제 브랜치가 무엇이든 존재하지 않을 이름으로 고정해 불일치를 만든다.
            var mapping = config.TryGetMapping("srv", "db")!;
            mapping.Branch = "no-such-branch";
            config.AddMapping(mapping);

            var state = new GitManager(config).GetRepositoryState("srv", "db");

            Assert.That(state!.BlockReason, Is.EqualTo(RepositoryBlockReason.BranchMismatch));
            Assert.That(state.BlockMessage, Does.Contain("no-such-branch"));
        }

        [Test]
        public void GetRepositoryState_ReturnsNull_WhenMappingIsMissing()
        {
            var config = NewEmptyConfig();

            var state = new GitManager(config).GetRepositoryState("srv", "db");

            Assert.That(state, Is.Null);
        }
```

`NewRepositoryWithCommit()` 과 `NewEmptyConfig()` 는 이 파일에 이미 있는 헬퍼를 쓴다. 이름이 다르면 그쪽 이름으로 맞춘다. 없으면 다음을 더한다:

```csharp
        private static ConfigManager NewEmptyConfig()
            => new ConfigManager(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dbvc_cfg_" + System.Guid.NewGuid().ToString("N"), "mappings.json"));

        /// <summary>커밋이 하나 있는 저장소와 그것에 매핑된 설정을 만든다. HEAD가 없으면 브랜치 이름을 읽을 수 없다.</summary>
        private static (ConfigManager Config, string RepoPath) NewRepositoryWithCommit()
        {
            var repoPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dbvc_repo_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(repoPath);
            LibGit2Sharp.Repository.Init(repoPath);

            using (var repo = new LibGit2Sharp.Repository(repoPath))
            {
                var filePath = System.IO.Path.Combine(repoPath, "seed.txt");
                System.IO.File.WriteAllText(filePath, "seed");
                LibGit2Sharp.Commands.Stage(repo, "seed.txt");
                var who = new LibGit2Sharp.Signature("t", "t@t", System.DateTimeOffset.Now);
                repo.Commit("seed", who, who);
            }

            var config = NewEmptyConfig();
            config.AddMapping("srv", "db", repoPath);
            return (config, repoPath);
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetRepositoryState"`
Expected: 컴파일 실패 — `GetRepositoryState`가 없다.

- [x] **Step 3: `IGitManager`에 더한다**

`src/DBVC.Core/Abstractions.cs` 의 `IGitManager` 안, `IsRepository` 바로 아래에 넣는다:

```csharp
        /// <summary>
        /// 저장소를 그대로 써도 되는지 판정한 결과. 매핑이 없으면 null이다.
        ///
        /// DBVC는 저장소의 유일한 주인이 아니다 — 외부 Git 클라이언트가 남긴 상태를 만나는 것이
        /// 정상이고, 만나면 멈춰야 한다. 판정 자체는 RepositoryStateEvaluator에 있다.
        /// </summary>
        RepositoryState? GetRepositoryState(string serverName, string databaseName);
```

- [x] **Step 4: `GitManager`에 구현한다**

`src/DBVC.Core/GitManager.cs` 의 `IsRepository` 아래에 넣는다:

```csharp
        public RepositoryState? GetRepositoryState(string serverName, string databaseName)
        {
            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null || !IsValidRepository(mapping.GitPath)) return null;

            using var repo = new Repository(mapping.GitPath);

            // CurrentOperation은 병합·리베이스·체리픽이 끝나지 않았을 때만 None이 아니다.
            var operation = repo.Info.CurrentOperation == CurrentOperation.None
                ? null
                : repo.Info.CurrentOperation.ToString();

            var detached = repo.Info.IsHeadDetached;
            var branch = detached ? null : repo.Head.FriendlyName;

            var reason = RepositoryStateEvaluator.Evaluate(branch, detached, operation, mapping.Branch);

            return new RepositoryState
            {
                CurrentBranch = branch,
                IsDetached = detached,
                PendingOperation = operation,
                BlockReason = reason,
                BlockMessage = RepositoryStateEvaluator.BuildMessage(reason, branch, mapping.Branch, operation)
            };
        }
```

`using DBVC.Core.Models;` 가 이미 있는지 확인하고 없으면 더한다.

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests"`
Expected: 새 3개를 포함해 전부 PASS

- [x] **Step 6: Vsix 쪽 목(mock)이 깨지지 않았는지 확인한다**

`IGitManager`에 멤버가 늘었으므로 Moq는 자동으로 따라가지만, 직접 구현한 가짜가 있으면 컴파일이 깨진다.

Run: `dotnet build DBVC.slnx`
Expected: 성공. 실패하면 그 가짜 구현에 `GetRepositoryState`를 더하고 `null`을 반환하게 한다.

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Core/GitManager.cs src/DBVC.Core/Abstractions.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 저장소의 브랜치와 진행 중 작업을 읽어 차단 여부를 낸다"
```

---

### Task 4: 추출을 `CREATE OR ALTER`로 바꾼다

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs:136-155` (`BuildScriptingOptions`)
- Test: `tests/DBVC.Core.Tests/SmoManagerTests.cs`
- Test: `tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `SmoManager.BuildScriptingOptions()`의 `ScriptForCreateOrAlter`가 `true` (기존 시그니처 유지, `internal static`)

> **왜 지금인가:** 이 변경은 저장소의 모든 `.sql` 파일을 한 번에 바꾼다. 아직 저장소를 만들기 전이라 비용이 0이다. 나중에 켜면 수천 파일이 한 커밋에 바뀌는 이력이 영구히 남는다(spec §2.3).

- [x] **Step 1: 실패하는 단위 테스트를 쓴다**

`tests/DBVC.Core.Tests/SmoManagerTests.cs` 에 더한다:

```csharp
        [Test]
        public void BuildScriptingOptions_EnablesCreateOrAlter()
        {
            // 저장소 파일이 그대로 실행 가능해야 배포 스크립트를 파일에서 만들 수 있다.
            // 순수 CREATE로 두면 객체가 이미 있는 대상에서 첫 문장부터 실패한다.
            var options = SmoManager.BuildScriptingOptions();

            Assert.That(options.ScriptForCreateOrAlter, Is.True);
        }

        [Test]
        public void BuildScriptingOptions_StillDoesNotScriptDropsOrExistenceChecks()
        {
            // CREATE OR ALTER는 이 둘과 함께 켜면 SMO가 무엇을 뱉을지 예측하기 어렵다.
            // 기존 결정을 그대로 지킨다는 것을 여기서 못박는다.
            var options = SmoManager.BuildScriptingOptions();

            Assert.That(options.ScriptDrops, Is.False);
            Assert.That(options.IncludeIfNotExists, Is.False);
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~BuildScriptingOptions"`
Expected: `BuildScriptingOptions_EnablesCreateOrAlter` FAIL — `Expected: True, But was: False`

- [x] **Step 3: 옵션을 켠다**

`src/DBVC.Core/SmoManager.cs` 의 `BuildScriptingOptions()` 안, `ScriptDrops = false,` 바로 위에 넣는다:

```csharp
                // 저장소 파일 자체가 실행 가능해야 한다. 배포 스크립트의 재료는 브랜치의 파일이지
                // 대상 DB에서 다시 뜬 것이 아니므로(설계 2.3), 여기서 CREATE OR ALTER로 쓰지 않으면
                // 생성 시점에 텍스트를 치환해야 하고 그러면 주석·문자열 안의 CREATE까지 건드린다.
                // 테이블에는 적용되지 않는다 - T-SQL에 CREATE OR ALTER TABLE이 없다.
                ScriptForCreateOrAlter = true,
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~BuildScriptingOptions"`
Expected: 2개 PASS

- [x] **Step 5: 통합 테스트를 더한다**

`tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs` 에 더한다. 이 파일에는 매핑까지 갖춘 임시 저장소를 만드는 `TempRepo` 내부 클래스(`repo.Path`, `repo.Smo`, `repo.RelativePaths()`)와 `_database` 필드, `ServerName` 상수가 이미 있다. 그대로 쓴다.

```csharp
        [Test]
        public void ScriptObjects_EmitsCreateOrAlter_ForProcedures()
        {
            // SMO 옵션이 실제로 어떤 텍스트를 뱉는지는 서버에 붙어야만 확인된다.
            // 설계 3.6의 배포 스크립트 3분류가 전부 이 텍스트에 걸려 있다.
            ExecuteOnTestDatabase("CREATE PROCEDURE dbo.CreateOrAlterProbe AS SELECT 1");

            using var repo = new TempRepo(_database!);
            repo.Smo.ScriptObjects(ServerName, _database!, new List<string> { "dbo.CreateOrAlterProbe" });

            var relative = repo.RelativePaths().Single(p => p.EndsWith("CreateOrAlterProbe.sql", StringComparison.OrdinalIgnoreCase));
            var sql = File.ReadAllText(Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar)));

            Assert.That(sql, Does.Contain("CREATE OR ALTER").IgnoreCase);
        }

        [Test]
        public void ScriptObjects_KeepsPlainCreate_ForTables()
        {
            // T-SQL에 CREATE OR ALTER TABLE이 없다. 기존 테이블 변경이 자동화 불가인 근거다(설계 2.4).
            ExecuteOnTestDatabase("CREATE TABLE dbo.CreateOrAlterTableProbe (Id int NOT NULL)");

            using var repo = new TempRepo(_database!);
            repo.Smo.ScriptObjects(ServerName, _database!, new List<string> { "dbo.CreateOrAlterTableProbe" });

            var relative = repo.RelativePaths().Single(p => p.EndsWith("CreateOrAlterTableProbe.sql", StringComparison.OrdinalIgnoreCase));
            var sql = File.ReadAllText(Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar)));

            Assert.That(sql, Does.Contain("CREATE TABLE").IgnoreCase);
            Assert.That(sql, Does.Not.Contain("CREATE OR ALTER").IgnoreCase);
        }
```

`ExecuteOnTestDatabase` 는 이 파일이 임시 DB에 DDL을 거는 기존 방식의 이름으로 맞춘다 — 픽스처가 `SqlServerTestDatabase` 인스턴스를 들고 있으면 그 인스턴스의 `Execute(string)` 을 쓰면 된다. **폴더 이름을 문자열로 박지 않는 이유**는 `ObjectPathConvention`이 정하는 값이라 여기서 다시 적으면 두 벌이 되기 때문이다. 그래서 `RelativePaths()` 에서 파일 이름으로 찾는다.

- [x] **Step 6: 통합 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerIntegrationTests"`
Expected: 로컬 SQL Server가 있으면 PASS. 없으면 Skip.

**Skip은 통과가 아니다.** SQL Server 없이 이 작업을 끝내면 다음 줄을 커밋 메시지 본문에 남긴다: `검증 보류: ScriptForCreateOrAlter의 실제 출력은 서버 없이 확인하지 못했다.`

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Core/SmoManager.cs tests/DBVC.Core.Tests/SmoManagerTests.cs tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs
git commit -m "feat(core): 저장소 파일을 CREATE OR ALTER로 추출한다"
```

---

### Task 5: 트리거가 접속 PC와 IP를 기록한다 (스키마 v3)

**Files:**
- Modify: `src/DBVC.Database/InstallTrigger.sql`
- Modify: `src/DBVC.Core/StateTracker.cs:22` (`RequiredSchemaVersion`)
- Test: `tests/DBVC.Core.Tests/InstallScriptSyncTests.cs` (기존 테스트가 자동으로 잡는다)
- Test: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `DBVC_ChangeLog`에 `HostName NVARCHAR(128) NULL`, `ClientNetAddress NVARCHAR(48) NULL`
  - `StateTracker.RequiredSchemaVersion == 3`

> **핵심 제약:** 트리거는 `WITH EXECUTE AS 'dbo'`로 돈다(`InstallTrigger.sql:91`). 이 문맥은 DB 범위로 샌드박싱되어 **서버 범위 DMV(`sys.dm_exec_connections`)에 접근할 수 없다.** 세션 범위 내장 함수인 `HOST_NAME()`과 `CONNECTIONPROPERTY()`만 쓴다.

- [x] **Step 1: 실패하는 통합 테스트를 쓴다**

`tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs` 에 더한다:

```csharp
        [Test]
        public void Trigger_RecordsTheClientHostName_WhenDdlRuns()
        {
            // 개발·테스트 DB는 공용 SQL 계정을 쓴다. LoginName이 모든 행에서 같으므로
            // 사람을 가르는 축은 접속 PC뿐이다(설계 3.9). 여기가 비면 필터가 통째로 무너진다.
            _db!.ExecuteInOneSession("CREATE PROCEDURE dbo.HostNameProbe AS SELECT 1");

            var recorded = (string?)_db.QueryScalar(
                "SELECT TOP 1 HostName FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'HostNameProbe' ORDER BY Id DESC");

            Assert.That(recorded, Is.Not.Null.And.Not.Empty,
                "EXECUTE AS 'dbo' 문맥에서 HOST_NAME()이 값을 내지 못했습니다 - 필터의 축을 다시 정해야 합니다");
        }

        [Test]
        public void Trigger_RecordsTheClientNetAddress_WhenDdlRuns()
        {
            // IP는 필터에 쓰지 않는다. HostName은 클라이언트가 보내는 값이라 신뢰도가 낮아,
            // 이상한 경우를 사람이 판별할 근거로만 남긴다.
            _db!.ExecuteInOneSession("CREATE PROCEDURE dbo.ClientAddressProbe AS SELECT 1");

            var recorded = (string?)_db.QueryScalar(
                "SELECT TOP 1 ClientNetAddress FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'ClientAddressProbe' ORDER BY Id DESC");

            Assert.That(recorded, Is.Not.Null.And.Not.Empty,
                "EXECUTE AS 'dbo' 문맥에서 CONNECTIONPROPERTY가 값을 내지 못했습니다");
        }

        [Test]
        public void Trigger_RecordsTheSameHostNameTheClientSees()
        {
            // 클라이언트가 SELECT HOST_NAME()으로 얻은 값과 글자 단위로 같아야 한다.
            // 다르면 필터가 전부를 걸러내 목록이 항상 빈다.
            _db!.ExecuteInOneSession("CREATE PROCEDURE dbo.HostNameMatchProbe AS SELECT 1");

            var fromTrigger = (string?)_db.QueryScalar(
                "SELECT TOP 1 HostName FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'HostNameMatchProbe' ORDER BY Id DESC");
            var fromClient = (string?)_db.QueryScalar("SELECT HOST_NAME()");

            Assert.That(fromTrigger, Is.EqualTo(fromClient));
        }
```

`SqlServerTestDatabase.QueryScalar(string)` 는 이미 있고 `object?` 를 반환한다. 제네릭 오버로드를 새로 만들지 말고 위처럼 캐스팅한다.

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: 로컬 SQL Server가 있으면 새 3개가 FAIL — `HostName` 컬럼이 없다는 오류. 없으면 Skip.

- [x] **Step 3: 테이블에 컬럼을 더한다**

`src/DBVC.Database/InstallTrigger.sql` 의 `CREATE TABLE [dbo].[DBVC_ChangeLog]` 안, `[IsProcessed]` 바로 위에 넣는다:

```sql
        [HostName] NVARCHAR(128) NULL,
        [ClientNetAddress] NVARCHAR(48) NULL,
```

- [x] **Step 4: 구버전 테이블 보정을 더한다**

같은 파일의 보정 블록(`-- 구버전(SchemaName / IsProcessed 이전)에 설치된 테이블 보정` 아래, 기존 `IF NOT EXISTS ... ALTER TABLE` 들과 같은 자리)에 넣는다:

```sql
-- v3 이전에 설치된 테이블에는 작업자 컬럼이 없다. NULL로 더한다 -
-- 기존 행은 작업자를 알 수 없으므로 "내 변경만"에서 빠지고 "전체"에서만 보인다.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'HostName')
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [HostName] NVARCHAR(128) NULL;
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'ClientNetAddress')
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [ClientNetAddress] NVARCHAR(48) NULL;
GO
```

`GO` 배치 구분이 필요한 이유: 같은 배치 안에서 방금 더한 컬럼을 참조하면 컴파일 오류가 난다. 기존 보정 블록이 어떻게 구분되어 있는지 보고 같은 방식을 따른다.

- [x] **Step 5: 트리거의 INSERT에 두 값을 담는다**

같은 파일의 `INSERT INTO [dbo].[DBVC_ChangeLog] (` 컬럼 목록에서 `[IsProcessed]` 위에 넣는다:

```sql
        [HostName],
        [ClientNetAddress],
```

`VALUES (` 목록의 `0` (IsProcessed) 위에 넣는다:

```sql
        -- 세션 범위 내장 함수라 WITH EXECUTE AS 'dbo'의 샌드박싱에 걸리지 않는다.
        -- sys.dm_exec_connections는 서버 범위라 이 문맥에서 읽을 수 없다.
        HOST_NAME(),
        CONVERT(NVARCHAR(48), CONNECTIONPROPERTY('client_net_address')),
```

`CONNECTIONPROPERTY`는 `sql_variant`를 반환하므로 `CONVERT`가 반드시 필요하다.

- [x] **Step 6: 스키마 버전을 3으로 올린다**

같은 파일에서 `@value = N'2'` 두 곳(`sp_addextendedproperty`, `sp_updateextendedproperty`)을 `N'3'` 으로 바꾼다.

`src/DBVC.Core/StateTracker.cs:22`:

```csharp
        public const int RequiredSchemaVersion = 3;
```

- [x] **Step 7: 동기화 테스트를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~InstallScriptSyncTests"`
Expected: 전부 PASS. `InstallScript_StampsTheVersionCoreRequires` 가 두 값이 어긋나면 실패하므로, 한쪽만 고쳤다면 여기서 잡힌다.

- [x] **Step 8: 통합 테스트를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: 로컬 SQL Server가 있으면 전부 PASS. 없으면 Skip.

**여기서 Skip이 나면 Task 6을 시작하기 전에 반드시 실제 서버에서 한 번 돌린다.** `HOST_NAME()`이 `EXECUTE AS` 문맥에서 값을 내지 못하면 Task 6의 필터 축 자체를 다시 정해야 하고, 그 사실을 Task 6을 다 짠 뒤에 알면 전부 버리게 된다.

- [x] **Step 9: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/
git commit -m "feat(core): DDL 트리거가 접속 PC와 IP를 기록한다"
```

---

### Task 6: 작업자로 좁혀 읽고, 작업자로 좁혀 닫는다

**Files:**
- Modify: `src/DBVC.Core/Models/ChangeRecord.cs`
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`
- Test: `tests/DBVC.Core.Tests/StateTrackerTests.cs`
- Test: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 5의 `HostName`·`ClientNetAddress` 컬럼
- Produces:
  - `ChangeLogRow.LoginName` → `string?`, `ChangeLogRow.HostName` → `string?`
  - `ChangeRecord.Author` → `string?` (LoginName), `ChangeRecord.HostName` → `string?`
  - `IStateTracker.RefreshState(string serverName, string databaseName, bool includeAllAuthors)` — 기존 2인자 오버로드는 `includeAllAuthors: false`로 위임
  - `IStateTracker.GetPendingChanges(string serverName, string databaseName)` (기존 시그니처 유지 — 캐시를 읽으므로 필터는 `RefreshState`가 정한다)
  - `StateTracker.CurrentAuthorQuery` → `internal const string`
  - `StateTracker.PendingChangesQuery` / `PendingChangesByAuthorQuery` → `internal const string`

> **`MarkProcessed`는 현재 사용자가 아니라 레코드의 작업자로 좁힌다.** 전체 보기에서 남의 변경을 대신 커밋하는 경로가 있으므로, 현재 사용자로 좁히면 그 행이 영원히 닫히지 않는다.

- [x] **Step 1: 실패하는 단위 테스트를 쓴다**

`tests/DBVC.Core.Tests/StateTrackerTests.cs` 에 더한다:

```csharp
        [Test]
        public void PendingChangesByAuthorQuery_FiltersByBothLoginAndHost()
        {
            // 공용 계정 환경에서는 LoginName이 상수라 HostName이 일을 한다.
            // 계정을 사람별로 나눈 환경에서는 둘 다 의미가 있다. 규칙을 두 번 만들지 않는다.
            Assert.That(StateTracker.PendingChangesByAuthorQuery, Does.Contain("@login"));
            Assert.That(StateTracker.PendingChangesByAuthorQuery, Does.Contain("@host"));
        }

        [Test]
        public void PendingChangesQuery_SelectsAuthorColumns()
        {
            // 전체 보기에서도 변경자 컬럼을 띄워야 하므로 필터 없는 쪽도 값을 읽어야 한다.
            Assert.That(StateTracker.PendingChangesQuery, Does.Contain("LoginName"));
            Assert.That(StateTracker.PendingChangesQuery, Does.Contain("HostName"));
        }

        [Test]
        public void MarkProcessedCommand_NarrowsByAuthor()
        {
            // 같은 객체를 둘이 만졌을 때, A의 커밋이 B의 로그까지 닫으면
            // B 화면에서 조용히 사라진다(설계 1.3).
            Assert.That(StateTracker.MarkProcessedCommand, Does.Contain("@login"));
            Assert.That(StateTracker.MarkProcessedCommand, Does.Contain("@host"));
        }

        [Test]
        public void MarkProcessedCommand_TreatsNullHostAsEmpty()
        {
            // v3 이전 행은 HostName이 NULL이다. NULL = @host는 절대 참이 되지 않으므로
            // 전체 보기에서 그 행을 커밋해도 닫히지 않고 매번 다시 올라온다.
            Assert.That(StateTracker.MarkProcessedCommand, Does.Contain("ISNULL(HostName"));
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~StateTrackerTests"`
Expected: 새 4개 FAIL

- [x] **Step 3: 모델에 필드를 더한다**

`src/DBVC.Core/Models/ChangeRecord.cs` 의 `ChangeLogRow` 안, `TargetObjectType` 아래에 넣는다:

```csharp
        /// <summary>DDL을 실행한 SQL 로그인. 공용 계정 환경에서는 모든 행에서 같다.</summary>
        public string? LoginName { get; set; }

        /// <summary>
        /// DDL을 실행한 접속의 워크스테이션 이름(<c>HOST_NAME()</c>).
        /// 공용 계정을 쓰는 환경에서 사람을 가르는 유일한 축이다. v3 이전 행은 null이다.
        /// </summary>
        public string? HostName { get; set; }
```

같은 파일의 `ChangeRecord` 안, `LastLogId` 아래에 넣는다:

```csharp
        /// <summary>이 상태의 근거가 된 가장 최신 로그 행의 SQL 로그인.</summary>
        public string? Author { get; set; }

        /// <summary>
        /// 이 상태의 근거가 된 가장 최신 로그 행의 접속 PC.
        /// MarkProcessed가 현재 사용자가 아니라 이 값으로 좁힌다 - 전체 보기에서 남의 변경을
        /// 대신 커밋하는 경로가 있고, 현재 사용자로 좁히면 그 행이 영원히 닫히지 않는다.
        /// </summary>
        public string? HostName { get; set; }
```

- [x] **Step 4: 쿼리를 고친다**

`src/DBVC.Core/StateTracker.cs` 의 `PendingChangesQuery` 를 바꾸고 그 아래에 하나를 더한다:

```csharp
        /// <summary>
        /// 아직 처리(커밋)되지 않은 DDL 이벤트만 최신순으로 읽는다. 전체 보기용이다.
        /// </summary>
        internal const string PendingChangesQuery = @"
SELECT Id, SchemaName, ObjectName, ObjectType, EventType, TargetObjectName, TargetObjectType, LoginName, HostName
FROM dbo.DBVC_ChangeLog
WHERE IsProcessed = 0
ORDER BY PostTime DESC, Id DESC";

        /// <summary>
        /// 지금 이 접속의 작업자가 낸 이벤트만 읽는다. 기본 화면이 쓰는 쿼리다.
        ///
        /// 두 쿼리를 문자열 결합 대신 따로 두는 이유는 읽기와 테스트 때문이다 -
        /// WHERE를 조립하면 어느 조합이 실제로 나가는지 눈으로 확인할 수 없다.
        /// </summary>
        internal const string PendingChangesByAuthorQuery = @"
SELECT Id, SchemaName, ObjectName, ObjectType, EventType, TargetObjectName, TargetObjectType, LoginName, HostName
FROM dbo.DBVC_ChangeLog
WHERE IsProcessed = 0
  AND ISNULL(LoginName, N'') = ISNULL(@login, N'')
  AND ISNULL(HostName, N'') = ISNULL(@host, N'')
ORDER BY PostTime DESC, Id DESC";

        /// <summary>
        /// "나는 누구인가"를 서버에게 묻는다. 클라이언트에서 Environment.MachineName으로 유도하면
        /// 접속 문자열의 Workstation ID를 누가 바꿔 두었을 때 트리거가 기록한 값과 달라지고,
        /// 필터가 전부를 걸러내 목록이 항상 빈다.
        /// </summary>
        internal const string CurrentAuthorQuery = "SELECT SUSER_SNAME(), HOST_NAME()";
```

`MarkProcessedCommand` 의 마지막 줄 뒤에 두 조건을 더한다:

```csharp
        internal static readonly string MarkProcessedCommand = $@"
UPDATE dbo.DBVC_ChangeLog
SET IsProcessed = 1
WHERE IsProcessed = 0 AND Id <= @lastLogId
  AND (ObjectName = @objectName
       OR (ObjectType IN ({ParentPointingTypeList}) AND TargetObjectName = @objectName))
  AND (ISNULL(SchemaName, N'dbo') = @schemaName)
  AND ISNULL(LoginName, N'') = ISNULL(@login, N'')
  AND ISNULL(HostName, N'') = ISNULL(@host, N'')";
```

`ISNULL`로 감싸는 이유는 v3 이전 행 때문이다 — `HostName = @host`는 양쪽이 NULL이면 참이 되지 않아 그 행이 영원히 닫히지 않는다.

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~StateTrackerTests"`
Expected: 새 4개를 포함해 전부 PASS

- [x] **Step 6: 커밋 (쿼리 상수까지)**

```bash
git add src/DBVC.Core/StateTracker.cs src/DBVC.Core/Models/ChangeRecord.cs tests/DBVC.Core.Tests/StateTrackerTests.cs
git commit -m "feat(core): 변경 로그를 작업자로 좁혀 읽고 닫는 쿼리를 더한다"
```

- [x] **Step 7: 읽기 경로를 잇는다**

`src/DBVC.Core/StateTracker.cs` 의 `ReadPendingRows` 를 바꾼다. 시그니처에 필터 인자를 더하고, 새 두 컬럼을 읽는다:

```csharp
        /// <param name="author">null이면 전체를 읽는다(전체 보기).</param>
        private static List<ChangeLogRow> ReadPendingRows(string connectionString, (string? Login, string? Host)? author)
        {
            var rows = new List<ChangeLogRow>();

            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();

            if (author == null)
            {
                cmd.CommandText = PendingChangesQuery;
            }
            else
            {
                cmd.CommandText = PendingChangesByAuthorQuery;
                cmd.Parameters.AddWithValue("@login", (object?)author.Value.Login ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@host", (object?)author.Value.Host ?? DBNull.Value);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(NormalizeRow(new ChangeLogRow
                {
                    Id = reader.GetInt32(0),
                    SchemaName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ObjectName = reader.GetString(2),
                    ObjectType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    EventType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    TargetObjectName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TargetObjectType = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LoginName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    HostName = reader.IsDBNull(8) ? null : reader.GetString(8)
                }));
            }

            return rows;
        }

        /// <summary>
        /// 지금 이 접속이 서버에서 어떻게 보이는지 읽는다. 트리거가 기록하는 값과 같은 함수를
        /// 같은 접속에서 부르므로 정의상 일치한다.
        /// </summary>
        private static (string? Login, string? Host) ReadCurrentAuthor(string connectionString)
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = CurrentAuthorQuery;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (null, null);

            return (reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
        }
```

`NormalizeRow` 는 새 두 필드를 그대로 옮겨야 한다. `NormalizeRow` 안의 `return new ChangeLogRow { ... }` 에 다음을 더한다:

```csharp
                LoginName = row.LoginName,
                HostName = row.HostName,
```

`BuildChangeRecords`(`StateTracker.cs:386` 부근)가 `ChangeRecord`를 만드는 자리에 다음을 더한다. 행은 최신순이므로 그룹의 첫 행이 가장 최신이다:

```csharp
                Author = row.LoginName,
                HostName = row.HostName,
```

- [x] **Step 8: `RefreshState`에 필터 인자를 낸다**

`src/DBVC.Core/Abstractions.cs` 의 `IStateTracker` 에서 `RefreshState`를 바꾼다:

```csharp
        bool RefreshState(string serverName, string databaseName);

        /// <param name="includeAllAuthors">
        /// true면 다른 사람이 만든 변경까지 읽는다. 기본 화면은 false다 —
        /// 공용 계정 환경에서 필터가 없으면 목록에 남의 진행 중 작업이 전부 뜨고,
        /// 전체 선택 커밋 한 번이면 검증되지 않은 남의 작업이 브랜치에 담긴다.
        /// </param>
        bool RefreshState(string serverName, string databaseName, bool includeAllAuthors);
```

`StateTracker` 에서 기존 `RefreshState(server, database)` 는 `RefreshState(server, database, false)` 로 위임하고, 3인자 쪽이 `includeAllAuthors ? null : ReadCurrentAuthor(connectionString)` 를 `ReadPendingRows` 에 넘긴다.

- [x] **Step 9: `MarkProcessed`가 레코드의 작업자로 좁히게 한다**

`src/DBVC.Core/StateTracker.cs:512` 의 `MarkProcessed` 안, 기존 파라미터 세 줄 아래에 더한다:

```csharp
                    // 현재 사용자가 아니라 레코드의 작업자로 좁힌다. 전체 보기에서 남의 변경을
                    // 대신 커밋하는 경로가 있고, 현재 사용자로 좁히면 그 행이 영원히 닫히지 않는다.
                    cmd.Parameters.AddWithValue("@login", (object?)record.Author ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@host", (object?)record.HostName ?? DBNull.Value);
```

- [x] **Step 10: 빌드와 전체 테스트를 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. `IStateTracker`에 멤버가 늘었으므로 직접 구현한 가짜가 있으면 여기서 깨진다 — 그쪽에 3인자 오버로드를 더한다.

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: 전부 PASS 또는 Skip

- [x] **Step 11: 필터가 실제로 가르는지 통합 테스트로 확인한다**

`tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs` 에 더한다:

```csharp
        [Test]
        public void RefreshState_ExcludesOtherWorkstationsChanges_WhenNotIncludingAllAuthors()
        {
            // 같은 공용 계정으로 서로 다른 PC에서 작업하는 상황을 Workstation ID로 흉내낸다.
            // 이 테스트가 이 계획 전체의 핵심이다 - 여기가 통과하지 않으면 나머지는 의미가 없다.
            _db!.ExecuteWithWorkstationId("OTHER-PC", "CREATE PROCEDURE dbo.OtherPcProbe AS SELECT 1");

            var tracker = new StateTracker(NewConfig());
            tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: false);
            var mine = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

            Assert.That(mine.Select(c => c.ObjectName), Does.Not.Contain("OtherPcProbe"));

            tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
            var all = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

            Assert.That(all.Select(c => c.ObjectName), Does.Contain("OtherPcProbe"));
        }
```

`ExecuteWithWorkstationId` 를 `SqlServerTestDatabase` 에 더한다:

```csharp
        /// <summary>
        /// 지정한 워크스테이션 이름으로 접속해 실행한다. 트리거의 HOST_NAME()이 그 값을 본다 —
        /// 다른 PC에서 작업한 상황을 테스트에서 만드는 유일한 방법이다.
        /// </summary>
        public void ExecuteWithWorkstationId(string workstationId, params string[] statements)
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString()) { WorkstationID = workstationId };

            using var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            foreach (var sql in statements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
```

`RefreshState`가 여는 접속의 `HOST_NAME()`은 테스트 프로세스의 기본값이므로 `"OTHER-PC"`와 다르다. 그것이 이 테스트가 성립하는 근거다.

- [x] **Step 12: 통합 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: 로컬 SQL Server가 있으면 전부 PASS. 없으면 Skip.

- [x] **Step 13: 커밋**

```bash
git add src/DBVC.Core/ tests/DBVC.Core.Tests/
git commit -m "feat(core): 접속 PC로 변경 목록을 좁히고 그 축으로 처리 완료를 표시한다"
```

---

### Task 7: 화면에 브랜치·차단·작업자 토글을 붙인다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 3의 `IGitManager.GetRepositoryState`; Task 6의 `IStateTracker.RefreshState(server, db, bool)`, `ChangeRecord.Author`·`HostName`
- Produces:
  - `ViewChangesViewModel.CurrentBranch` → `string?`
  - `ViewChangesViewModel.BlockMessage` → `string?` (null이 아니면 차단 오버레이가 뜬다)
  - `ViewChangesViewModel.IsBlocked` → `bool`
  - `ViewChangesViewModel.ShowAllAuthors` → `bool` (기본 false, 바뀌면 `Refresh()`)
  - `ChangeItemViewModel.Author` → `string?` (표시용: `HostName` 우선, 없으면 `Author`)

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` 에 더한다.

**이 파일의 구조를 먼저 알아야 한다.** `NewViewModel()` 은 인자를 받지 않고 `_config`·`_stateTracker`·`_git`·`_smo` 같은 **필드 목**으로 조립한다. `NewConnectedViewModel()` 은 개체 탐색기가 대상을 내주는 상태로 만든 뒤 `ConnectCommand` 를 실행한다 — 실제 앱에 남은 유일한 접속 경로다. 목을 새로 만들지 말고 `[SetUp]` 이후에 필드 목의 `Setup` 을 덮어쓴다.

```csharp
        [Test]
        public void CurrentBranch_ShowsRepositoryBranch_WhenConnected()
        {
            // 비교 기준이 브랜치 내용이므로 어느 브랜치인지 모르면 diff를 오독한다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState { CurrentBranch = "feature/x", BlockReason = RepositoryBlockReason.None });

            var vm = NewConnectedViewModel();

            Assert.That(vm.CurrentBranch, Is.EqualTo("feature/x"));
        }

        [Test]
        public void IsBlocked_IsTrueWithMessage_WhenRepositoryStateIsBlocked()
        {
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    CurrentBranch = "develop",
                    BlockReason = RepositoryBlockReason.BranchMismatch,
                    BlockMessage = "이 대상은 'master' 브랜치에 고정되어 있는데 저장소는 'develop'에 있습니다."
                });

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsBlocked, Is.True);
            Assert.That(vm.BlockMessage, Does.Contain("master"));
        }

        [Test]
        public void CommitCommand_CannotExecute_WhenBlocked()
        {
            // 차단은 경고가 아니다. 조용히 틀린 결과를 내는 것보다 멈추는 편이 낫다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    BlockReason = RepositoryBlockReason.DetachedHead,
                    BlockMessage = "저장소가 어느 브랜치도 가리키지 않는 상태(detached HEAD)입니다."
                });

            var vm = NewConnectedViewModel();

            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void ShowAllAuthors_DefaultsToFalse()
        {
            // 기본이 전체면 목록에 남의 진행 중 작업이 전부 뜨고, 전체 선택 커밋 한 번이면
            // 검증되지 않은 남의 작업이 브랜치에 담긴다.
            Assert.That(NewViewModel().ShowAllAuthors, Is.False);
        }

        [Test]
        public void Refresh_PassesShowAllAuthorsToTracker()
        {
            _stateTracker.Setup(s => s.RefreshState(Server, Database, It.IsAny<bool>())).Returns(true);

            var vm = NewConnectedViewModel();
            vm.ShowAllAuthors = true;

            _stateTracker.Verify(s => s.RefreshState(Server, Database, true), Times.AtLeastOnce);
        }
```

`[SetUp]` 의 기존 줄 `_stateTracker.Setup(s => s.RefreshState(Server, Database)).Returns(true);` 를 3인자 오버로드로 바꿔야 다른 테스트가 깨지지 않는다:

```csharp
            _stateTracker.Setup(s => s.RefreshState(Server, Database, It.IsAny<bool>())).Returns(true);
```

`[SetUp]` 에 저장소 상태의 기본값도 더한다. 없으면 `GetRepositoryState` 가 `null` 을 돌려주고 모든 기존 테스트가 브랜치 없는 상태로 돌아 무해하지만, 명시하는 편이 읽기 쉽다:

```csharp
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None });
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ViewChangesViewModelTests"`
Expected: 컴파일 실패 — `CurrentBranch`·`IsBlocked`·`BlockMessage`·`ShowAllAuthors` 가 없다.

> `DBVC.Vsix.Tests` 는 Windows에서만 돈다. Windows가 아니면 이 Task는 시작할 수 없다.

- [x] **Step 3: 뷰모델에 속성을 더한다**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` 에 더한다. 기존 속성들이 쓰는 `SetProperty`/`OnPropertyChanged` 방식을 그대로 따른다.

```csharp
        private string? _currentBranch;

        /// <summary>
        /// 저장소의 현재 브랜치. 비교 기준이 브랜치 내용이므로 이것이 보이지 않으면
        /// 사용자가 diff를 오독한다.
        /// </summary>
        public string? CurrentBranch
        {
            get => _currentBranch;
            private set => SetProperty(ref _currentBranch, value);
        }

        private string? _blockMessage;

        /// <summary>
        /// null이 아니면 저장소를 그대로 쓸 수 없다는 뜻이고, 화면을 덮는다.
        /// 경고 배너로 두지 않는 이유는 조용히 틀린 결과가 더 나쁘기 때문이다(설계 3.4).
        /// </summary>
        public string? BlockMessage
        {
            get => _blockMessage;
            private set
            {
                if (SetProperty(ref _blockMessage, value))
                {
                    OnPropertyChanged(nameof(IsBlocked));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockMessage);

        private bool _showAllAuthors;

        /// <summary>
        /// 다른 작업자의 변경까지 볼지 여부. 기본은 false다.
        ///
        /// 토글이 필요한 이유가 넷이다 - 커밋하지 않고 떠난 사람의 고아 변경, 휴가 중인 동료의
        /// 대리 커밋, 노트북에서 만들고 데스크톱에서 커밋하는 경우, 그리고 v3 이전의 작업자 없는 행.
        /// </summary>
        public bool ShowAllAuthors
        {
            get => _showAllAuthors;
            set
            {
                if (SetProperty(ref _showAllAuthors, value))
                {
                    Refresh();
                }
            }
        }
```

`CommandManager` 를 쓰려면 `using System.Windows.Input;` 이 필요하다. 이 파일이 이미 다른 방식으로 `CanExecute` 재평가를 하고 있으면 그 방식을 따른다.

- [x] **Step 4: 컨텍스트 판정에 저장소 상태를 넣는다**

`ApplyContextProbe`(`ViewChangesViewModel.cs:353` 부근)에서 `IsInitialized` 를 정한 뒤, `Refresh()` 를 부르기 전에 넣는다:

```csharp
            // 저장소 상태는 매핑이 있을 때만 의미가 있다. 매핑이 없으면 안내가 이미 따로 뜬다.
            var repoState = IsMapped ? _gitManager.GetRepositoryState(server, database) : null;
            CurrentBranch = repoState?.CurrentBranch;
            BlockMessage = repoState?.BlockMessage;

            if (IsBlocked)
            {
                // 차단 상태에서 Refresh를 돌리면 틀린 기준으로 비교한 목록이 만들어진다.
                Changes.Clear();
                return;
            }
```

`server`·`database` 는 이 메서드가 이미 들고 있는 값 이름으로 맞춘다. 없으면 `probe` 에서 꺼낸다.

`InvalidateActiveContext`(`ViewChangesViewModel.cs:190`)에도 두 줄을 더한다 — 대상이 바뀌면 이전 대상의 값이 남아 있으면 안 된다:

```csharp
            CurrentBranch = null;
            BlockMessage = null;
```

- [x] **Step 5: `CanCommit`과 `Refresh`를 잇는다**

`CanCommit()` 의 맨 앞에 넣는다:

```csharp
            if (IsBlocked) return false;
```

`Refresh()` 안에서 `_stateTracker.RefreshState(server, database)` 를 부르는 자리를 `_stateTracker.RefreshState(server, database, ShowAllAuthors)` 로 바꾼다. `ShowAllAuthors` 는 UI 스레드에서 읽어 지역 변수에 담은 뒤 백그라운드로 넘긴다 — 이 파일의 다른 백그라운드 호출이 값만 넘기는 규약을 따른다.

- [x] **Step 6: 변경자 표시를 더한다**

`src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs` 에 더한다:

```csharp
        /// <summary>
        /// 목록에 띄울 변경자. 공용 계정 환경에서는 로그인 이름이 전부 같으므로
        /// 접속 PC를 우선한다 - 로그인 이름을 대면 아무 정보도 주지 못한다.
        /// </summary>
        public string? Author { get; set; }
```

`ChangeItemViewModel` 을 `ChangeRecord` 로부터 만드는 자리(`ViewChangesViewModel` 안)에서 채운다:

```csharp
                Author = string.IsNullOrWhiteSpace(record.HostName) ? record.Author : record.HostName,
```

- [x] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ViewChangesViewModelTests"`
Expected: 새 5개를 포함해 전부 PASS

- [x] **Step 8: XAML을 고친다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml`:

1. 버전 표시(`DBVC 0.2.8`)가 있는 상단 줄에 브랜치를 나란히 둔다:

```xml
<TextBlock Text="{Binding CurrentBranch, StringFormat=브랜치: {0}}"
           Margin="0,0,12,0"
           VerticalAlignment="Center"
           Visibility="{Binding CurrentBranch, Converter={StaticResource NullToVisibilityConverter}}" />
```

`NullToVisibilityConverter` 가 없으면 기존 `InverseBooleanToVisibilityConverter` 와 같은 자리에 만들어 등록한다.

2. 변경 목록 위 도구 줄에 토글을 둔다:

```xml
<CheckBox Content="다른 사람 변경도 보기"
          IsChecked="{Binding ShowAllAuthors, Mode=TwoWay}"
          IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBooleanConverter}}"
          ToolTip="공용 계정이라 사람은 접속 PC로 구분합니다. 평소에는 자기 변경만 보이는 편이 안전합니다."
          Margin="8,0,0,0" VerticalAlignment="Center" />
```

3. 변경 목록의 `DataGrid`(또는 `ListView`)에 컬럼을 더한다. 기존 `ObjectType` 컬럼 정의를 그대로 흉내낸다:

```xml
<DataGridTextColumn Header="변경자" Binding="{Binding Author}" Width="Auto" />
```

4. 루트 컨테이너의 마지막 자식으로 차단 오버레이를 둔다. 초기화 오버레이가 어떻게 구성되어 있는지 보고 같은 방식을 따른다:

```xml
<Border Background="#CC000000"
        Visibility="{Binding IsBlocked, Converter={StaticResource BooleanToVisibilityConverter}}">
    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="520">
        <TextBlock Text="저장소를 그대로 사용할 수 없습니다"
                   Foreground="White" FontSize="16" FontWeight="Bold"
                   HorizontalAlignment="Center" Margin="0,0,0,12" />
        <TextBlock Text="{Binding BlockMessage}"
                   Foreground="White" TextWrapping="Wrap" TextAlignment="Center" />
    </StackPanel>
</Border>
```

- [x] **Step 9: 빌드하고 `.vsix`가 나오는지 확인한다**

Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release`
Run: `dir src\DBVC.Vsix\bin\Release\net48\*.vsix`
Expected: `.vsix` 파일이 존재한다. **빌드 성공은 `.vsix` 생성을 뜻하지 않는다** — 산출물을 눈으로 확인한다.

- [x] **Step 10: 커밋**

```bash
git add src/DBVC.Vsix/ tests/DBVC.Vsix.Tests/
git commit -m "feat(vsix): 현재 브랜치와 작업자 토글을 띄우고 어긋난 저장소를 차단한다"
```

---

### Task 8: 남의 미커밋 작업이 딸려 오는 것을 경고한다 (경고 B)

**Files:**
- Create: `src/DBVC.Core/CoAuthorDetector.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Core.Tests/CoAuthorDetectorTests.cs` (신규)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 6의 `ChangeLogRow.LoginName`·`HostName`, `ChangeRecord.QualifiedName`
- Produces:
  - `class DBVC.Core.Models.CoAuthorWarning { string QualifiedName; string Author; }`
  - `static IReadOnlyList<CoAuthorWarning> CoAuthorDetector.Detect(IEnumerable<ChangeLogRow> allPendingRows, IEnumerable<string> committingQualifiedNames, string? currentLogin, string? currentHost)`
  - `IReadOnlyList<CoAuthorWarning> IStateTracker.GetCoAuthorWarnings(string serverName, string databaseName, IEnumerable<string> qualifiedNames)`

> **왜 경고이고 차단이 아닌가:** 대부분은 실제로 이어서 작업한 정상적인 경우다. 차단하면 도구를 쓰지 않게 된다(spec §3.10).

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/CoAuthorDetectorTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 공용 DB가 하나뿐인 이상 같은 객체를 둘이 만지는 것을 막을 수 없다.
    /// 막을 수는 없고 알릴 수는 있다 - 커밋하는 내용에 남의 미커밋 작업이 들어 있다는 사실을.
    /// </summary>
    [TestFixture]
    public class CoAuthorDetectorTests
    {
        private static ChangeLogRow Row(string schema, string name, string login, string host)
            => new ChangeLogRow { SchemaName = schema, ObjectName = name, LoginName = login, HostName = host };

        [Test]
        public void Detect_ReturnsWarning_WhenAnotherHostTouchedTheSameObject()
        {
            var rows = new[]
            {
                Row("dbo", "P", "app_dev", "MY-PC"),
                Row("dbo", "P", "app_dev", "KIM-PC")
            };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0].QualifiedName, Is.EqualTo("dbo.P"));
            Assert.That(warnings[0].Author, Is.EqualTo("KIM-PC"));
        }

        [Test]
        public void Detect_ReturnsNothing_WhenOnlyCurrentAuthorTouchedIt()
        {
            var rows = new[] { Row("dbo", "P", "app_dev", "MY-PC") };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void Detect_IgnoresObjectsNotBeingCommitted()
        {
            // 커밋하지 않는 객체를 남이 만졌다는 사실은 지금 알릴 일이 아니다.
            var rows = new[] { Row("dbo", "Q", "app_dev", "KIM-PC") };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void Detect_ReportsEachOtherAuthorOnce_WhenTheyTouchedItRepeatedly()
        {
            var rows = new[]
            {
                Row("dbo", "P", "app_dev", "KIM-PC"),
                Row("dbo", "P", "app_dev", "KIM-PC"),
                Row("dbo", "P", "app_dev", "LEE-PC")
            };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings.Select(w => w.Author), Is.EquivalentTo(new[] { "KIM-PC", "LEE-PC" }));
        }

        [Test]
        public void Detect_MatchesQualifiedNameIgnoringCase()
        {
            var rows = new[] { Row("dbo", "P", "app_dev", "KIM-PC") };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "DBO.p" }, "app_dev", "MY-PC");

            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void Detect_TreatsNullHostAsAnotherAuthor()
        {
            // v3 이전 행은 작업자를 알 수 없다. "내 것"으로 볼 근거가 없으므로 남의 것으로 다룬다.
            var rows = new[] { Row("dbo", "P", "app_dev", null!) };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Has.Count.EqualTo(1));
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CoAuthorDetectorTests"`
Expected: 컴파일 실패 — `CoAuthorDetector` 가 없다.

- [x] **Step 3: 값 객체와 판정 함수를 만든다**

`src/DBVC.Core/Models/RepositoryState.cs` 와 같은 폴더가 아니라 별도 파일 `src/DBVC.Core/CoAuthorDetector.cs` 에 둘 다 넣는다 — 값 객체가 이 판정에만 쓰인다.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 커밋하려는 객체를 다른 작업자도 만졌는지 알린다.
    ///
    /// 공용 개발 DB가 하나뿐인 이상, A가 프로시저 P를 고치고 뒤이어 B도 P를 고치면 DB의 P는
    /// B의 코드다. A가 추출해 커밋하면 B의 미완성 작업이 A의 브랜치에 담긴다. 막을 방법은
    /// 구조적으로 없고 알릴 수만 있다.
    ///
    /// DB에도 Git에도 닿지 않는 순수 함수다.
    /// </summary>
    public static class CoAuthorDetector
    {
        public static IReadOnlyList<CoAuthorWarning> Detect(
            IEnumerable<ChangeLogRow> allPendingRows,
            IEnumerable<string> committingQualifiedNames,
            string? currentLogin,
            string? currentHost)
        {
            var targets = new HashSet<string>(
                committingQualifiedNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (targets.Count == 0) return Array.Empty<CoAuthorWarning>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<CoAuthorWarning>();

            foreach (var row in allPendingRows ?? Enumerable.Empty<ChangeLogRow>())
            {
                if (row == null) continue;

                var qualified = $"{row.SchemaName ?? ObjectPathConvention.DefaultSchema}.{row.ObjectName}";
                if (!targets.Contains(qualified)) continue;

                if (IsCurrentAuthor(row, currentLogin, currentHost)) continue;

                // v3 이전 행은 HostName이 null이다. "내 것"으로 볼 근거가 없으므로 남의 것으로 다루고,
                // 표시는 로그인 이름으로 대신한다.
                var author = string.IsNullOrWhiteSpace(row.HostName)
                    ? (row.LoginName ?? "알 수 없음")
                    : row.HostName!;

                // 같은 사람이 같은 객체를 여러 번 만졌어도 한 번만 알린다.
                if (!seen.Add($"{qualified}|{author}")) continue;

                warnings.Add(new CoAuthorWarning { QualifiedName = qualified, Author = author });
            }

            return warnings;
        }

        private static bool IsCurrentAuthor(ChangeLogRow row, string? currentLogin, string? currentHost)
        {
            // HostName이 비어 있으면 현재 사용자와 같다고 볼 수 없다 - 비교 자체가 성립하지 않는다.
            if (string.IsNullOrWhiteSpace(row.HostName)) return false;

            return string.Equals(row.LoginName, currentLogin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.HostName, currentHost, StringComparison.OrdinalIgnoreCase);
        }
    }
}

namespace DBVC.Core.Models
{
    /// <summary>커밋 대상 객체 하나를 만진 다른 작업자 한 명.</summary>
    public class CoAuthorWarning
    {
        public string QualifiedName { get; set; } = string.Empty;

        /// <summary>접속 PC 이름. 알 수 없으면 로그인 이름이 대신 온다.</summary>
        public string Author { get; set; } = string.Empty;
    }
}
```

한 파일에 두 네임스페이스를 두는 것이 이 저장소의 기존 방식과 다르면, `CoAuthorWarning` 을 `src/DBVC.Core/Models/CoAuthorWarning.cs` 로 분리한다.

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CoAuthorDetectorTests"`
Expected: 6개 PASS

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/CoAuthorDetector.cs tests/DBVC.Core.Tests/CoAuthorDetectorTests.cs
git commit -m "feat(core): 커밋 대상을 다른 작업자도 만졌는지 판정한다"
```

- [x] **Step 6: `IStateTracker`에 조회를 낸다**

`src/DBVC.Core/Abstractions.cs` 의 `IStateTracker` 에 더한다:

```csharp
        /// <summary>
        /// 커밋하려는 객체들을 다른 작업자도 만졌는지 조회한다. 비어 있으면 경고할 것이 없다.
        /// 화면 필터와 무관하게 항상 전체 로그를 본다 - "내 변경만" 상태에서도 남이 만졌다는
        /// 사실은 알려야 한다.
        /// </summary>
        IReadOnlyList<CoAuthorWarning> GetCoAuthorWarnings(
            string serverName, string databaseName, IEnumerable<string> qualifiedNames);
```

`src/DBVC.Core/StateTracker.cs` 에 구현한다:

```csharp
        public IReadOnlyList<CoAuthorWarning> GetCoAuthorWarnings(
            string serverName, string databaseName, IEnumerable<string> qualifiedNames)
        {
            try
            {
                var connectionString = BuildConnectionString(serverName, databaseName);
                var current = ReadCurrentAuthor(connectionString);

                // 필터 없이 읽는다. "내 변경만" 상태에서도 남이 만졌다는 사실은 알려야 한다.
                var rows = ReadPendingRows(connectionString, author: null);

                return CoAuthorDetector.Detect(rows, qualifiedNames, current.Login, current.Host);
            }
            catch (Exception ex)
            {
                // 경고를 못 내는 것이 커밋을 막을 이유는 되지 않는다.
                Debug.WriteLine($"StateTracker.GetCoAuthorWarnings failed for '{serverName}.{databaseName}': {ex.Message}");
                return Array.Empty<CoAuthorWarning>();
            }
        }
```

`BuildConnectionString` 은 이 클래스가 이미 쓰는 이름이다.

- [x] **Step 7: 뷰모델에서 커밋 전에 확인한다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` 에 먼저 테스트를 더한다:

**`IUserNotifier.Confirm(string title, string message) → bool` 은 이미 있다.** 테스트 쪽 `RecordingNotifier` 도 `ConfirmResult`(응답 조작)와 `ConfirmCalls`(전달된 문구 검증)를 이미 갖고 있다. 새로 만들지 않는다.

```csharp
        [Test]
        public void Commit_AsksForConfirmation_WhenAnotherAuthorTouchedTheSameObject()
        {
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new[] { new CoAuthorWarning { QualifiedName = "dbo.P", Author = "KIM-PC" } });

            _notifier.ConfirmResult = false;

            var vm = NewViewModelWithOneSelectedChange("dbo.P");
            vm.CommitMessage = "테스트";
            vm.CommitCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Any(c => c.Message.Contains("KIM-PC")), Is.True,
                "다른 작업자의 PC 이름이 확인 문구에 없습니다");

            // 사용자가 취소했으므로 커밋이 일어나면 안 된다.
            _git.Verify(g => g.CommitChanges(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()),
                Times.Never);
        }

        [Test]
        public void Commit_DoesNotAsk_WhenNoOtherAuthorTouchedIt()
        {
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new CoAuthorWarning[0]);

            var before = _notifier.ConfirmCallCount;

            var vm = NewViewModelWithOneSelectedChange("dbo.P");
            vm.CommitMessage = "테스트";
            vm.CommitCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(before),
                "경고할 것이 없는데 확인을 물었습니다 - 매번 뜨면 사용자가 읽지 않게 된다");
        }
```

`NewViewModelWithOneSelectedChange` 는 이 파일의 기존 커밋 테스트가 변경 항목 하나를 선택 상태로 만드는 방식을 그대로 따라 만든다(`_stateTracker.GetPendingChanges` 가 `ChangeRecord` 하나를 돌려주게 하고 `NewConnectedViewModel()` 후 `Changes[0].IsSelected = true`). 같은 일을 하는 헬퍼가 이미 있으면 그것을 쓴다.

`[SetUp]` 에 기본값을 더한다 — 없으면 Moq가 `null` 을 돌려주고 `Commit` 에서 `NullReferenceException` 이 난다:

```csharp
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new CoAuthorWarning[0]);
```

`Commit()`(`ViewChangesViewModel.cs:1056`) 의 `committedRecords` 를 만든 직후, `IsBusy = true;` 앞에 넣는다:

```csharp
            // 커밋 직전에 묻는다. 목록을 만든 시점과 커밋 시점 사이에 남이 또 만졌을 수 있다.
            var coAuthors = _stateTracker.GetCoAuthorWarnings(ServerName!, DatabaseName!, committedNames);
            if (coAuthors.Count > 0)
            {
                var lines = string.Join(Environment.NewLine,
                    coAuthors.Select(w => $"  · {w.QualifiedName} — {w.Author}"));

                var confirmed = _notifier.Confirm(
                    "DBVC 커밋 확인",
                    "다음 객체는 다른 작업자도 변경했습니다. 지금 커밋하는 내용에 그 변경이 포함됩니다."
                    + Environment.NewLine + Environment.NewLine + lines
                    + Environment.NewLine + Environment.NewLine + "그대로 커밋할까요?");

                if (!confirmed) return;
            }
```

- [x] **Step 8: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 전부 PASS

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: 전부 PASS 또는 Skip

- [x] **Step 9: 빌드와 `.vsix`를 확인한다**

Run: `dotnet build DBVC.slnx`
Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release && dir src\DBVC.Vsix\bin\Release\net48\*.vsix`
Expected: 둘 다 성공하고 `.vsix` 가 존재한다

- [x] **Step 10: 커밋**

```bash
git add src/DBVC.Core/ src/DBVC.Vsix/ tests/
git commit -m "feat(vsix): 남의 변경이 딸려 오는 커밋 전에 확인을 묻는다"
```

---

### Task 9: 문서와 버전을 맞춘다

**Files:**
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest`

**Interfaces:**
- Consumes: Task 1~8 전부
- Produces: 없음 (문서)

> 사용자 눈에 보이는 동작이 바뀌면 `README.md`와 `docs/setup-checklist.md`를 함께 고치고 매니페스트 버전을 올린다.

- [x] **Step 1: 매니페스트 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest` 의 `Version` 을 `0.2.8` → `0.3.0` 으로 바꾼다. 기능이 늘고 스키마가 바뀌었으므로 minor를 올린다.

`tests/DBVC.Vsix.Tests/DbvcVersionTests.cs` 가 버전을 검증하고 있으면 그쪽도 맞춘다.

- [x] **Step 2: README에 세 가지를 더한다**

`## 주요 기능` 목록에 더한다:

```markdown
- **작업자별 변경 목록:** 공용 SQL 계정을 쓰는 개발 DB에서도 자기가 바꾼 것만 보입니다. 사람은
  접속 PC(`HOST_NAME()`)로 구분합니다. 다른 사람의 변경이 필요하면 **다른 사람 변경도 보기**를
  켜세요 — 커밋하지 않고 떠난 사람의 변경을 대신 올리거나, 노트북에서 만든 변경을 데스크톱에서
  커밋할 때 필요합니다.
- **커밋 전 확인:** 커밋하려는 객체를 다른 작업자도 변경했으면 그 사실을 알리고 물어봅니다.
  공용 DB가 하나뿐이라 같은 객체를 둘이 만지는 것을 막을 수는 없고, 알릴 수만 있습니다.
- **저장소 상태 차단:** 매핑에 브랜치가 고정된 대상은 저장소가 그 브랜치에 있지 않으면 화면을
  덮고 동작을 막습니다. detached HEAD나 끝나지 않은 병합도 마찬가지입니다. 어긋난 기준으로
  비교하면 결과가 조용히 거짓이 되기 때문입니다.
```

`### 동작 방식` 에 더한다:

```markdown
- **현재 브랜치:** 도구 창 위쪽에 저장소의 현재 브랜치가 표시됩니다. 비교의 기준이 그 브랜치의
  파일 내용이므로, 브랜치를 바꾸면 같은 데이터베이스라도 다른 차이가 보입니다.
- **변경 추적기 업데이트(0.3.0):** 작업자를 기록하기 위해 트리거가 바뀌었습니다(스키마 v3).
  그 이전에 초기화한 데이터베이스는 **변경 추적기 업데이트**를 눌러 갈아 끼우세요. 그 전에 쌓인
  로그에는 작업자 정보가 없어 "다른 사람 변경도 보기"에서만 보입니다.
```

`## 알려진 이슈` 나 그에 준하는 절에 더한다(없으면 `### 기능 커버리지` 아래에 만든다):

```markdown
### 이 방식이 성립하지 않는 환경
여러 사람이 원격 데스크톱으로 같은 서버에 붙어 SSMS를 쓰면 접속 PC 이름이 모두 같아져
작업자 구분이 되지 않습니다. 그 환경에서는 사람마다 SQL 로그인을 나눠야 합니다.
```

- [x] **Step 3: 도입 체크리스트에 운영 규칙을 옮긴다**

`docs/setup-checklist.md` 에 절을 더한다. 내용은 spec 6장(`docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md`)에서 옮긴다 — **DB 변경은 짧게 산다**, **같은 객체에 대한 동시 작업은 조율한다**, **`hotfix/*`의 DB 변경**(세 선택지), **`develop` 리셋 여부**, **한 사람이 한 PC를 쓰는가**. 각 항목의 "왜"를 함께 옮긴다.

- [x] **Step 4: 전체 빌드와 테스트를 확인한다**

Run: `dotnet build DBVC.slnx`
Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 전부 PASS 또는 Skip

- [x] **Step 5: 커밋**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest tests/DBVC.Vsix.Tests/DbvcVersionTests.cs
git commit -m "docs: 작업자 필터와 저장소 차단을 문서에 반영하고 0.3.0으로 올린다"
```

---

## 완료 조건

CI가 검증하지 않는 영역이 있다. **아래를 실제로 눌러 보기 전에는 "동작한다"고 말할 수 없다.**

- [x] **로컬 SQL Server에서 통합 테스트를 돌렸다.** Skip이 아니라 PASS를 봤다. 특히 `Trigger_RecordsTheClientHostName_WhenDdlRuns` 와 `RefreshState_ExcludesOtherWorkstationsChanges_WhenNotIncludingAllAuthors`
- [x] **SSMS 21에 `.vsix`를 설치하고 도구 창의 버전이 그 `.vsix`와 같다.** 덮어 설치 후 SSMS를 다시 시작해야 반영된다
- [x] **개발 DB에 연결해 현재 브랜치가 보인다**
- [x] **다른 PC(또는 다른 `Workstation ID`로 접속한 SSMS)에서 만든 변경이 내 목록에 안 뜨고, "다른 사람 변경도 보기"를 켜면 뜬다**
- [x] **같은 객체를 다른 PC에서도 만진 뒤 커밋하면 확인 대화상자가 뜨고, 그 PC 이름이 문구에 있다**
- [x] **매핑에 `"Branch": "no-such-branch"` 를 손으로 넣으면 화면이 덮이고 아무 버튼도 눌리지 않는다**
- [x] **v2로 초기화된 DB에 연결하면 "변경 추적기 업데이트"가 뜨고, 누르면 v3으로 올라가며 기존 로그가 남아 있다**
- [x] **새로 추출한 프로시저 `.sql` 파일이 `CREATE OR ALTER` 로 시작한다**

---

## 실기 확인 뒤

2026-08-26~27 SSMS 21에서 위 완료 조건을 모두 확인했다. 그 과정에서 CI도 테스트도 닿지
않는 결함 다섯 개가 드러났고, 0.3.1과 0.3.2에서 고쳤다 — 작업자 필터가 Git 폴백에 새던 것,
어두운 테마에서 토글 문구가 묻히던 것, `sp_rename`의 새 이름을 기록하지 않아 테이블
디자이너가 살아 있는 테이블을 삭제로 만들던 것, 접힌 옛 이름의 로그 행이 닫히지 않던 것,
저장소와 이미 같아진 항목을 커밋으로 지울 수 없던 것.

다섯 중 셋은 이 계획이 만든 코드가 아니라 **그 코드가 기존 동작과 만나는 자리**에서 나왔다.
계획서에 그 접점을 적어 두지 않은 것이 이번 작업의 교훈이다.
