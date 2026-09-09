# `master` 기준선 하네스 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 운영 DB를 읽기만 해서 `master` 브랜치의 기준선을 만드는 콘솔 도구를 만들고, 그것으로 팀 배포 백로그 10번을 닫는다.

**Architecture:** `DBVC.Core`의 `ISmoManager`·`IConfigManager`를 그대로 쓴다. 임시 경로의 `ConfigManager`에 `Mode = Write` 매핑을 써서 전체 추출을 돌리고, 같은 매핑을 `Mode = Audit`으로 덮어써 `CompareWithRepository`로 스스로를 검증한다. 판정 로직은 전부 순수 함수이거나 두 인터페이스만 아는 클래스라 SQL Server 없이 테스트된다.

**Tech Stack:** .NET Framework 4.8, NUnit 4.3.2, Moq 4.18.4, `DBVC.Core`(프로젝트 참조)

**Spec:** `docs/superpowers/specs/2026-09-09-dbvc-master-baseline-harness-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 콘솔 출력 포함.
- 주석은 **"왜"만** 적는다. 한국어 평서문.
- 커밋 메시지는 한국어 명령형 현재시제 + 스코프: `feat(baseline): …`
- 커밋 메시지 끝에 다음 두 줄을 붙인다:
  ```
  Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
  ```
- 테스트 이름은 영어 `Method_Result_WhenCondition`.
- **TDD.** 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **`Microsoft.Data.SqlClient`·`Microsoft.SqlServer.SqlManagementObjects`를 직접 `PackageReference` 하지 않는다.** `DBVC.Core` 프로젝트 참조로 전이해서만 받는다.
- **타깃은 `net48` 단독이다.** 두 프로젝트 모두. 런타임 동일성이 이 도구의 요구사항이다.
- **운영 DB에 쓰는 코드를 만들지 않는다.** `IStateTracker`를 참조하지 않는다 — `InitializeDatabase`가 닿는 경로가 하나도 없어야 한다.
- `README.md`는 고치지 않는다.

## 파일 구조

| 파일 | 책임 |
| --- | --- |
| `tools/DBVC.Baseline/DBVC.Baseline.csproj` | net48 콘솔. `DBVC.Core`만 참조 |
| `tools/DBVC.Baseline/BaselineOptions.cs` | 인자 파싱·검증. 순수 |
| `tools/DBVC.Baseline/PreflightCheck.cs` | 대상 폴더 판정. 순수 |
| `tools/DBVC.Baseline/BaselineReport.cs` | 결과 모델·렌더링·종료 코드 |
| `tools/DBVC.Baseline/BaselineRunner.cs` | 순서 지휘. 두 인터페이스만 안다 |
| `tools/DBVC.Baseline/TempConfig.cs` | 임시 설정 폴더 생성·삭제 |
| `tools/DBVC.Baseline/Program.cs` | 인자·프롬프트·수명·출력 |
| `tests/DBVC.Baseline.Tests/…` | 위 각각의 테스트 |

---

### Task 1: 프로젝트 둘을 세우고 인자 파싱을 만든다

프로젝트 뼈대만으로는 검증할 것이 없으므로 첫 순수 함수와 함께 낸다.

**Files:**
- Create: `tools/DBVC.Baseline/DBVC.Baseline.csproj`
- Create: `tools/DBVC.Baseline/BaselineOptions.cs`
- Create: `tests/DBVC.Baseline.Tests/DBVC.Baseline.Tests.csproj`
- Create: `tests/DBVC.Baseline.Tests/BaselineOptionsTests.cs`
- Modify: `DBVC.slnx`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: `DBVC.Baseline.BaselineOptions { string Server; string Database; string RepositoryPath; string? SqlUser }`, `DBVC.Baseline.BaselineOptionsResult { BaselineOptions? Options; string? Error; bool IsValid }`, `static BaselineOptionsResult BaselineOptions.Parse(IReadOnlyList<string> args)` — Task 4·5가 쓴다.

- [ ] **Step 1: 두 프로젝트 파일을 만든다**

`tools/DBVC.Baseline/DBVC.Baseline.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <!--
      net48 단독이다. develop 쪽 추출은 SSMS 안 net48에서 MDS 5.1.5 + SMO 171.30.0으로 돈다.
      이 도구의 존재 이유가 바이트 동일성이므로 같은 코드를 다른 런타임에서 돌릴 수 없다.
    -->
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>dbvc-baseline</AssemblyName>
    <RootNamespace>DBVC.Baseline</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <!--
    MDS·SMO를 직접 적지 않는다. DBVC.Core가 SSMS 21에 맞춰 고정한 버전을 전이 참조로 받는다.
    직접 적으면 그쪽이 이겨 Core와 다른 SMO가 올라오고, SmoManager가 그 TypeLoadException을
    삼켜 조용히 null을 돌려준다.
  -->
  <ItemGroup>
    <ProjectReference Include="..\..\src\DBVC.Core\DBVC.Core.csproj" />
  </ItemGroup>

</Project>
```

`tests/DBVC.Baseline.Tests/DBVC.Baseline.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!--
      저장소의 다른 테스트 프로젝트는 net48;net10.0 멀티타깃인데 여기만 net48 단독이다.
      대상인 DBVC.Baseline이 net48 단독이라 net10.0에서 참조할 수 없다. Windows에서만 돈다.
    -->
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />
    <PackageReference Include="Moq" Version="4.18.4" />
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="NUnit.Analyzers" Version="4.7.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="5.0.0" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="NUnit.Framework" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\tools\DBVC.Baseline\DBVC.Baseline.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 솔루션과 CI에 등록한다**

`DBVC.slnx` — `/tests/` 폴더 안에 프로젝트 한 줄을 더하고, `/tools/` 폴더를 새로 만든다:

```xml
  <Folder Name="/tests/">
    <Project Path="tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj" />
    <Project Path="tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj" />
    <Project Path="tests/DBVC.Baseline.Tests/DBVC.Baseline.Tests.csproj" />
  </Folder>
  <Folder Name="/tools/">
    <Project Path="tools/DBVC.Baseline/DBVC.Baseline.csproj" />
  </Folder>
```

`.github/workflows/ci.yml` — `테스트 - DBVC.Vsix (net10.0)` 단계 **다음**에 더한다:

```yaml
      # net48 단독이다 - 대상 도구가 net48이라 net10.0 짝이 없다.
      - name: 테스트 - DBVC.Baseline (net48)
        run: dotnet test tests/DBVC.Baseline.Tests -f net48 -c Release --logger "trx;LogFileName=baseline-net48.trx" --results-directory TestResults
```

- [ ] **Step 3: 실패하는 테스트를 쓴다**

`tests/DBVC.Baseline.Tests/BaselineOptionsTests.cs`:

```csharp
using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class BaselineOptionsTests
    {
        private static string[] Valid() => new[]
        {
            "--server", "PRODSRV", "--database", "SalesDB", "--repo", @"D:\dbvc\prod"
        };

        [Test]
        public void Parse_ReturnsOptions_WhenRequiredArgsGiven()
        {
            var result = BaselineOptions.Parse(Valid());

            Assert.That(result.IsValid, Is.True, result.Error);
            Assert.That(result.Options!.Server, Is.EqualTo("PRODSRV"));
            Assert.That(result.Options.Database, Is.EqualTo("SalesDB"));
            Assert.That(result.Options.RepositoryPath, Is.EqualTo(@"D:\dbvc\prod"));
        }

        [Test]
        public void Parse_LeavesSqlUserNull_WhenFlagOmitted()
        {
            var result = BaselineOptions.Parse(Valid());

            Assert.That(result.Options!.SqlUser, Is.Null);
        }

        [Test]
        public void Parse_ReadsSqlUser_WhenFlagGiven()
        {
            var args = new List<string>(Valid()) { "--sql-user", "dbvc_reader" };

            var result = BaselineOptions.Parse(args);

            Assert.That(result.Options!.SqlUser, Is.EqualTo("dbvc_reader"));
        }

        [Test]
        public void Parse_ReturnsError_WhenServerMissing()
        {
            var result = BaselineOptions.Parse(new[] { "--database", "SalesDB", "--repo", "C:\\x" });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("--server"));
        }

        [Test]
        public void Parse_ReturnsError_WhenPasswordFlagGiven()
        {
            var args = new List<string>(Valid()) { "--password", "hunter2" };

            var result = BaselineOptions.Parse(args);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("암호"));
        }

        [Test]
        public void Parse_ReturnsError_WhenUnknownFlagGiven()
        {
            // 값을 함께 준다. 값이 없으면 "값이 없습니다" 갈래로 빠져 엉뚱한 이유로 통과한다.
            var args = new List<string>(Valid()) { "--force", "true" };

            var result = BaselineOptions.Parse(args);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("모르는 옵션"));
        }

        [Test]
        public void Parse_ReturnsError_WhenFlagHasNoValue()
        {
            var result = BaselineOptions.Parse(new[] { "--server" });

            Assert.That(result.IsValid, Is.False);
        }
    }
}
```

- [ ] **Step 4: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48`
Expected: 컴파일 실패 — `BaselineOptions`가 없다.

- [ ] **Step 5: 최소 구현을 쓴다**

`tools/DBVC.Baseline/BaselineOptions.cs`:

```csharp
namespace DBVC.Baseline
{
    /// <summary>파싱 결과. 예외 대신 값으로 돌려주므로 판정이 순수 함수로 테스트된다.</summary>
    public sealed class BaselineOptionsResult
    {
        private BaselineOptionsResult(BaselineOptions? options, string? error)
        {
            Options = options;
            Error = error;
        }

        public BaselineOptions? Options { get; }
        public string? Error { get; }
        public bool IsValid => Options != null;

        internal static BaselineOptionsResult Ok(BaselineOptions options) => new(options, null);
        internal static BaselineOptionsResult Fail(string error) => new(null, error);
    }

    /// <summary>
    /// 명령줄 인자.
    ///
    /// <b>암호 속성이 없다.</b> 있으면 언젠가 렌더링되거나 로그에 실린다 — 암호는 파싱 결과에
    /// 담기지 않고 <see cref="Program"/>에서 자격증명 저장소로 곧장 들어간다.
    /// </summary>
    public sealed class BaselineOptions
    {
        private BaselineOptions(string server, string database, string repositoryPath, string? sqlUser)
        {
            Server = server;
            Database = database;
            RepositoryPath = repositoryPath;
            SqlUser = sqlUser;
        }

        public string Server { get; }
        public string Database { get; }
        public string RepositoryPath { get; }

        /// <summary><c>null</c>이면 Windows 통합 인증이다.</summary>
        public string? SqlUser { get; }

        public static string Usage =>
            "사용법: dbvc-baseline --server <서버> --database <DB> --repo <클론 경로> [--sql-user <계정>]";

        public static BaselineOptionsResult Parse(IReadOnlyList<string> args)
        {
            string? server = null, database = null, repo = null, sqlUser = null;

            for (int i = 0; i < args.Count; i++)
            {
                var flag = args[i];

                // 암호를 인자로 받지 않는다. 셸 이력과 프로세스 목록에 남기 때문이다.
                // 편의를 위해 열어 두면 반드시 쓰이므로 조용히 무시하지 않고 거부한다.
                if (flag == "--password" || flag == "-p")
                {
                    return BaselineOptionsResult.Fail(
                        "암호는 인자로 받지 않습니다. 셸 이력과 프로세스 목록에 남기 때문입니다. " +
                        "--sql-user만 주면 실행 중에 묻습니다.");
                }

                if (i + 1 >= args.Count)
                {
                    return BaselineOptionsResult.Fail($"'{flag}'에 값이 없습니다.\n{Usage}");
                }

                var value = args[++i];
                switch (flag)
                {
                    case "--server": server = value; break;
                    case "--database": database = value; break;
                    case "--repo": repo = value; break;
                    case "--sql-user": sqlUser = value; break;
                    default:
                        return BaselineOptionsResult.Fail($"모르는 옵션입니다: '{flag}'\n{Usage}");
                }
            }

            if (string.IsNullOrWhiteSpace(server))
            {
                return BaselineOptionsResult.Fail($"--server가 필요합니다.\n{Usage}");
            }
            if (string.IsNullOrWhiteSpace(database))
            {
                return BaselineOptionsResult.Fail($"--database가 필요합니다.\n{Usage}");
            }
            if (string.IsNullOrWhiteSpace(repo))
            {
                return BaselineOptionsResult.Fail($"--repo가 필요합니다.\n{Usage}");
            }

            return BaselineOptionsResult.Ok(new BaselineOptions(server!, database!, repo!, sqlUser));
        }
    }
}
```

`Program.cs`가 아직 없으면 컴파일되지 않으므로 임시 진입점을 함께 만든다 —
Task 5에서 본체로 바뀐다:

```csharp
namespace DBVC.Baseline
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine(BaselineOptions.Usage);
            return 3;
        }
    }
}
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48`
Expected: PASS 7/7

Run: `dotnet build DBVC.slnx -c Release`
Expected: 오류 0개 — 솔루션 등록이 맞는지 함께 본다.

- [ ] **Step 7: 커밋**

```bash
git add tools/DBVC.Baseline tests/DBVC.Baseline.Tests DBVC.slnx .github/workflows/ci.yml
git commit -F - <<'EOF'
feat(baseline): 기준선 하네스 프로젝트와 인자 파싱을 만든다

암호는 인자로 받지 않는다 - 셸 이력과 프로세스 목록에 남는다. --password를
조용히 무시하지 않고 거부하는 것은, 열어 두면 반드시 쓰이기 때문이다.
BaselineOptions에 암호 속성 자체를 두지 않아 렌더링에 샐 경로를 없앤다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 2: 대상 폴더 사전 점검

**Files:**
- Create: `tools/DBVC.Baseline/PreflightCheck.cs`
- Create: `tests/DBVC.Baseline.Tests/PreflightCheckTests.cs`

**Interfaces:**
- Produces: `DBVC.Baseline.PreflightInput { bool DirectoryExists; bool HasGitDirectory; bool HasSqlFiles }`, `static string? PreflightCheck.Validate(PreflightInput)` — `null`이면 통과, 아니면 한국어 사유. Task 5가 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Baseline.Tests/PreflightCheckTests.cs`:

```csharp
using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class PreflightCheckTests
    {
        private static PreflightInput Clean() => new()
        {
            DirectoryExists = true,
            HasGitDirectory = true,
            HasSqlFiles = false
        };

        [Test]
        public void Validate_ReturnsNull_WhenEmptyGitClone()
        {
            Assert.That(PreflightCheck.Validate(Clean()), Is.Null);
        }

        [Test]
        public void Validate_ReturnsReason_WhenDirectoryMissing()
        {
            var input = Clean();
            input.DirectoryExists = false;

            Assert.That(PreflightCheck.Validate(input), Does.Contain("폴더"));
        }

        [Test]
        public void Validate_ReturnsReason_WhenNotGitRepository()
        {
            var input = Clean();
            input.HasGitDirectory = false;

            Assert.That(PreflightCheck.Validate(input), Does.Contain("git"));
        }

        [Test]
        public void Validate_ReturnsReason_WhenSqlFilesPresent()
        {
            var input = Clean();
            input.HasSqlFiles = true;

            Assert.That(PreflightCheck.Validate(input), Does.Contain(".sql"));
        }

        [Test]
        public void Validate_ReportsMissingDirectory_WhenNothingIsTrue()
        {
            // 폴더가 없으면 나머지 관측값은 의미가 없다. 사용자가 먼저 고칠 것을 말해야 한다.
            var input = new PreflightInput
            {
                DirectoryExists = false,
                HasGitDirectory = false,
                HasSqlFiles = true
            };

            Assert.That(PreflightCheck.Validate(input), Does.Contain("폴더"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~PreflightCheckTests"`
Expected: 컴파일 실패 — `PreflightCheck`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`tools/DBVC.Baseline/PreflightCheck.cs`:

```csharp
namespace DBVC.Baseline
{
    /// <summary>파일시스템을 보지 않는다. 관측은 호출자가 하고 판정만 여기서 한다.</summary>
    public sealed class PreflightInput
    {
        public bool DirectoryExists { get; set; }
        public bool HasGitDirectory { get; set; }
        public bool HasSqlFiles { get; set; }
    }

    /// <summary>
    /// 기준선을 받아도 되는 폴더인지 판정한다.
    ///
    /// 막으려는 사고는 하나다 — 실수로 개발 클론을 가리켜 운영 사진으로 덮어쓰는 것.
    /// 작업 트리가 깨끗한지는 보지 않는다: GitManager 의존이 생기는 데 비해 얻는 것이 없고,
    /// master에 갓 체크아웃한 클론은 정의상 .sql이 없다.
    /// </summary>
    public static class PreflightCheck
    {
        public static string? Validate(PreflightInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // 순서가 곧 우선순위다. 폴더가 없으면 나머지 관측값은 뜻이 없다.
            if (!input.DirectoryExists)
            {
                return "--repo로 지정한 폴더가 없습니다.";
            }

            if (!input.HasGitDirectory)
            {
                return "--repo로 지정한 폴더가 git 저장소가 아닙니다. master를 체크아웃한 클론을 지정하세요.";
            }

            if (input.HasSqlFiles)
            {
                return "--repo로 지정한 폴더에 이미 .sql 파일이 있습니다. " +
                       "개발 클론을 지정하지 않았는지 확인하세요 — 기준선은 비어 있는 master 클론에만 만듭니다.";
            }

            return null;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~PreflightCheckTests"`
Expected: PASS 5/5

- [ ] **Step 5: 커밋**

```bash
git add tools/DBVC.Baseline/PreflightCheck.cs tests/DBVC.Baseline.Tests/PreflightCheckTests.cs
git commit -F - <<'EOF'
feat(baseline): 대상 폴더 사전 점검을 더한다

막으려는 사고는 하나다 - 실수로 개발 클론을 가리켜 운영 사진으로 덮어쓰는 것.
.sql이 이미 있으면 거부한다. 파일시스템을 보지 않고 관측값만 받으므로 판정이
디스크 없이 테스트된다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 3: 결과 모델과 종료 코드

**Files:**
- Create: `tools/DBVC.Baseline/BaselineReport.cs`
- Create: `tests/DBVC.Baseline.Tests/BaselineReportTests.cs`

**Interfaces:**
- Produces: `DBVC.Baseline.BaselineVerdict { Clean, ExtractionFailed, VerificationFailed, PreflightFailed }`, `DBVC.Baseline.BaselineReport { BaselineVerdict Verdict; int ExtractedCount; List<string> ExtractionFailures; int ComparedCount; List<string> VerificationFindings; bool RepositoryScanCompleted; int ExitCode; string Render() }` — Task 4·5가 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Baseline.Tests/BaselineReportTests.cs`:

```csharp
using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class BaselineReportTests
    {
        [Test]
        public void ExitCode_ReturnsZero_WhenClean()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.Clean };

            Assert.That(report.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void ExitCode_ReturnsOne_WhenExtractionFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.ExtractionFailed };

            Assert.That(report.ExitCode, Is.EqualTo(1));
        }

        [Test]
        public void ExitCode_ReturnsTwo_WhenVerificationFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.VerificationFailed };

            Assert.That(report.ExitCode, Is.EqualTo(2));
        }

        [Test]
        public void ExitCode_ReturnsThree_WhenPreflightFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.PreflightFailed };

            Assert.That(report.ExitCode, Is.EqualTo(3));
        }

        [Test]
        public void Render_ListsEveryExtractionFailure()
        {
            // 조용히 빠지는 것이 이 도구의 유일한 치명적 실패 방식이다. 전부 출력해야 한다.
            var report = new BaselineReport { Verdict = BaselineVerdict.ExtractionFailed };
            report.ExtractionFailures.Add("dbo.usp_A");
            report.ExtractionFailures.Add("dbo.usp_B");

            var text = report.Render();

            Assert.That(text, Does.Contain("dbo.usp_A"));
            Assert.That(text, Does.Contain("dbo.usp_B"));
        }

        [Test]
        public void Render_SaysDoNotCommit_WhenVerificationFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.VerificationFailed };
            report.VerificationFindings.Add("dbo.P — 수정됨");

            var text = report.Render();

            Assert.That(text, Does.Contain("커밋하지 마세요"));
            Assert.That(text, Does.Contain("dbo.P"));
        }

        [Test]
        public void Render_SaysSafeToCommit_WhenClean()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.Clean, ExtractedCount = 1234 };

            var text = report.Render();

            Assert.That(text, Does.Contain("1234"));
            Assert.That(text, Does.Contain("커밋해도 됩니다"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~BaselineReportTests"`
Expected: 컴파일 실패 — `BaselineReport`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`tools/DBVC.Baseline/BaselineReport.cs`:

```csharp
using System.Text;

namespace DBVC.Baseline
{
    public enum BaselineVerdict
    {
        /// <summary>추출·검증 모두 깨끗하다.</summary>
        Clean,

        /// <summary>스크립팅에 실패한 객체가 있다. 기준선이 불완전하다.</summary>
        ExtractionFailed,

        /// <summary>검증에서 차이가 나왔다. 이 기준선은 믿을 수 없다.</summary>
        VerificationFailed,

        /// <summary>사전 점검이나 인자에서 멈췄다. 아무것도 쓰지 않았다.</summary>
        PreflightFailed
    }

    public sealed class BaselineReport
    {
        public BaselineVerdict Verdict { get; set; }
        public int ExtractedCount { get; set; }
        public List<string> ExtractionFailures { get; } = new();
        public int ComparedCount { get; set; }

        /// <summary>검증에서 나온 차이·미판정 객체를 사람이 읽을 한 줄씩.</summary>
        public List<string> VerificationFindings { get; } = new();

        public bool RepositoryScanCompleted { get; set; } = true;

        /// <summary>사전 점검 실패 등, 판정 이전에 멈춘 사유.</summary>
        public string? StopReason { get; set; }

        public int ExitCode => Verdict switch
        {
            BaselineVerdict.Clean => 0,
            BaselineVerdict.ExtractionFailed => 1,
            BaselineVerdict.VerificationFailed => 2,
            _ => 3
        };

        public string Render()
        {
            var sb = new StringBuilder();

            if (StopReason != null)
            {
                sb.AppendLine(StopReason);
                return sb.ToString();
            }

            sb.AppendLine($"추출한 객체: {ExtractedCount}개");

            if (ExtractionFailures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"스크립팅에 실패한 객체 {ExtractionFailures.Count}개 —");
                sb.AppendLine("대개 VIEW DEFINITION 권한이 없거나 암호화된 모듈입니다.");
                foreach (var name in ExtractionFailures)
                {
                    sb.AppendLine($"  - {name}");
                }
                sb.AppendLine();
                sb.AppendLine("기준선이 불완전합니다. 커밋하지 마세요 — 빠진 객체는 나중에 " +
                              "\"브랜치에만 있음\"으로 떠서 배포 스크립트에 CREATE가 들어갑니다.");
                return sb.ToString();
            }

            if (Verdict == BaselineVerdict.VerificationFailed)
            {
                sb.AppendLine($"검증한 객체: {ComparedCount}개");
                sb.AppendLine();
                sb.AppendLine("검증에서 차이가 나왔습니다 —");
                foreach (var finding in VerificationFindings)
                {
                    sb.AppendLine($"  - {finding}");
                }
                if (!RepositoryScanCompleted)
                {
                    sb.AppendLine("  - 저장소 스캔이 끝까지 돌지 못했습니다.");
                }
                sb.AppendLine();
                sb.AppendLine("이 기준선은 믿을 수 없습니다. 커밋하지 마세요. " +
                              "실행 중에 운영이 바뀌었을 수 있으니 다시 돌려 보세요.");
                return sb.ToString();
            }

            sb.AppendLine($"검증한 객체: {ComparedCount}개, 차이 없음");
            sb.AppendLine();
            sb.AppendLine("기준선이 만들어졌습니다. git status로 확인한 뒤 커밋해도 됩니다.");
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~BaselineReportTests"`
Expected: PASS 7/7

- [ ] **Step 5: 커밋**

```bash
git add tools/DBVC.Baseline/BaselineReport.cs tests/DBVC.Baseline.Tests/BaselineReportTests.cs
git commit -F - <<'EOF'
feat(baseline): 결과 모델과 종료 코드를 더한다

실패해도 성공한 것은 남기되 실패 목록을 반드시 출력한다 - 조용히 빠지는 것이
이 도구의 유일한 치명적 실패 방식이다. 종료 코드로 0/1/2/3을 갈라 자동화가
"커밋해도 되는가"를 판단할 수 있게 한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 4: 순서를 지휘하는 러너

이 계획의 본체다. `IConfigManager`·`ISmoManager`가 이미 인터페이스라 SQL Server 없이 전부 검사된다.

**Files:**
- Create: `tools/DBVC.Baseline/BaselineRunner.cs`
- Create: `tests/DBVC.Baseline.Tests/BaselineRunnerTests.cs`

**Interfaces:**
- Consumes: `BaselineOptions`(Task 1), `BaselineReport`·`BaselineVerdict`(Task 3)
- Produces: `DBVC.Baseline.BaselineRunner(IConfigManager, ISmoManager)`, `BaselineReport Run(BaselineOptions, IProgress<ExtractionProgress>?, CancellationToken)` — Task 5·6이 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Baseline.Tests/BaselineRunnerTests.cs`:

```csharp
using DBVC.Baseline;
using DBVC.Core;
using DBVC.Core.Models;
using Moq;

namespace DBVC.Baseline.Tests
{
    public class BaselineRunnerTests
    {
        private const string Server = "PRODSRV";
        private const string Database = "SalesDB";
        private const string Repo = @"D:\dbvc\prod";

        private static BaselineOptions Options() =>
            BaselineOptions.Parse(new[] { "--server", Server, "--database", Database, "--repo", Repo }).Options!;

        private static ScriptResult CleanScript(int count)
        {
            return new ScriptResult { SucceededCount = count };
        }

        private static ComparisonResult CleanComparison(int compared)
        {
            return new ComparisonResult { ComparedCount = compared, RepositoryScanCompleted = true };
        }

        [Test]
        public void Run_WritesWriteModeMapping_BeforeExtraction()
        {
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();

            // 마지막으로 쓰인 mode를 계속 덮어 담는다. 추출 시점에는 Write여야 하고,
            // Run이 끝난 뒤에는 검증 직전에 덮어쓴 Audit이 남아 있어야 한다.
            MappingMode? lastWrittenMode = null;

            config.Setup(c => c.AddMapping(It.IsAny<MappingConfig>()))
                  .Callback<MappingConfig>(m => lastWrittenMode = m.Mode);
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(() =>
               {
                   Assert.That(lastWrittenMode, Is.EqualTo(MappingMode.Write), "추출 시점에는 Write여야 한다");
                   return CleanScript(10);
               });
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanComparison(10));

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            Assert.That(lastWrittenMode, Is.EqualTo(MappingMode.Audit), "검증 직전에 Audit으로 덮어써야 한다");
        }

        [Test]
        public void Run_PassesNullObjectNames_ToExtraction()
        {
            // 목록을 넘기면 조용히 부분 기준선이 만들어진다.
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanScript(3));
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanComparison(3));

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Run_ReturnsClean_WhenBothStepsClean()
        {
            var report = RunWith(CleanScript(42), CleanComparison(42));

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.Clean));
            Assert.That(report.ExtractedCount, Is.EqualTo(42));
            Assert.That(report.ComparedCount, Is.EqualTo(42));
        }

        [Test]
        public void Run_ReturnsExtractionFailed_WhenScriptResultHasFailures()
        {
            var script = CleanScript(5);
            script.FailedObjects.Add("dbo.usp_Secret");

            var report = RunWith(script, CleanComparison(5));

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.ExtractionFailed));
            Assert.That(report.ExtractionFailures, Does.Contain("dbo.usp_Secret"));
        }

        [Test]
        public void Run_SkipsComparison_WhenExtractionFailed()
        {
            // 이미 불완전한 기준선을 검증해도 새로 알 것이 없다. 운영을 두 번 읽지 않는다.
            var script = CleanScript(5);
            script.FailedObjects.Add("dbo.usp_Secret");

            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(script);

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void Run_ReturnsExtractionFailed_WhenScriptResultIsNull()
        {
            // 매핑이 없거나 접속에 실패하면 null이다.
            var report = RunWith(null, CleanComparison(0));

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.ExtractionFailed));
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenComparisonHasDifferences()
        {
            var comparison = CleanComparison(10);
            comparison.Differences.Add(new SchemaDifference("dbo.P", "dbo/StoredProcedures/P.sql", "StoredProcedure", ObjectDiffState.Modified));

            var report = RunWith(CleanScript(10), comparison);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
            Assert.That(report.VerificationFindings.Count, Is.EqualTo(1));
            Assert.That(report.VerificationFindings[0], Does.Contain("dbo.P"));
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenComparisonHasUnverifiedObjects()
        {
            // IsInSync는 "판정하지 못한 객체"를 담지 않는다. 차이가 0이어도 일치가 아니다.
            var comparison = CleanComparison(10);
            comparison.FailedObjects.Add("dbo.Q");

            var report = RunWith(CleanScript(10), comparison);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenRepositoryScanIncomplete()
        {
            var comparison = CleanComparison(10);
            comparison.RepositoryScanCompleted = false;

            var report = RunWith(CleanScript(10), comparison);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
            Assert.That(report.RepositoryScanCompleted, Is.False);
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenComparisonIsNull()
        {
            var report = RunWith(CleanScript(10), null);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
        }

        private static BaselineReport RunWith(ScriptResult? script, ComparisonResult? comparison)
        {
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(script);
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(comparison);

            return new BaselineRunner(config.Object, smo.Object).Run(Options());
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~BaselineRunnerTests"`
Expected: 컴파일 실패 — `BaselineRunner`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`tools/DBVC.Baseline/BaselineRunner.cs`:

```csharp
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Baseline
{
    /// <summary>
    /// 기준선을 만드는 순서를 지휘한다.
    ///
    /// 두 인터페이스만 알기 때문에 SQL Server 없이 전부 테스트된다. 운영 DB에 쓰는 경로가
    /// 하나도 없다 — IStateTracker를 참조하지 않으므로 InitializeDatabase에 닿을 수 없다.
    /// </summary>
    public sealed class BaselineRunner
    {
        private readonly IConfigManager _configManager;
        private readonly ISmoManager _smoManager;

        public BaselineRunner(IConfigManager configManager, ISmoManager smoManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _smoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
        }

        public BaselineReport Run(
            BaselineOptions options,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var report = new BaselineReport();

            // 추출은 Write에서만 허용된다(MappingPolicy).
            SetMode(options, MappingMode.Write);

            var script = _smoManager.ScriptObjectsDetailed(
                options.Server, options.Database, null, progress, cancellationToken);

            if (script == null)
            {
                report.Verdict = BaselineVerdict.ExtractionFailed;
                report.StopReason = "추출이 시작되지 못했습니다. 접속과 --repo 경로를 확인하세요.";
                return report;
            }

            report.ExtractedCount = script.SucceededCount;
            report.ExtractionFailures.AddRange(script.FailedObjects);

            if (script.HasFailures)
            {
                // 이미 불완전한 기준선을 검증해도 새로 알 것이 없다. 운영을 두 번 읽지 않는다.
                report.Verdict = BaselineVerdict.ExtractionFailed;
                return report;
            }

            // 차이 검사는 Write에서 금지다. 같은 대상을 Audit으로 덮어쓴다.
            SetMode(options, MappingMode.Audit);

            var comparison = _smoManager.CompareWithRepository(
                options.Server, options.Database, progress, cancellationToken);

            if (comparison == null)
            {
                report.Verdict = BaselineVerdict.VerificationFailed;
                report.StopReason = "검증이 시작되지 못했습니다. 이 기준선은 믿을 수 없습니다.";
                return report;
            }

            report.ComparedCount = comparison.ComparedCount;
            report.RepositoryScanCompleted = comparison.RepositoryScanCompleted;

            foreach (var difference in comparison.Differences)
            {
                report.VerificationFindings.Add($"{difference.QualifiedName} — {Describe(difference.State)}");
            }
            foreach (var failed in comparison.FailedObjects)
            {
                report.VerificationFindings.Add($"{failed} — 판정하지 못함");
            }

            // IsInSync만 보면 안 된다. 판정하지 못한 객체와 다 읽지 못한 저장소는
            // Differences에 들어오지 않는다(ComparisonResult의 주석).
            bool verified = comparison.IsInSync
                && comparison.FailedObjects.Count == 0
                && comparison.RepositoryScanCompleted;

            report.Verdict = verified ? BaselineVerdict.Clean : BaselineVerdict.VerificationFailed;
            return report;
        }

        private void SetMode(BaselineOptions options, MappingMode mode)
        {
            _configManager.AddMapping(new MappingConfig
            {
                ServerName = options.Server,
                DatabaseName = options.Database,
                GitPath = options.RepositoryPath,
                Mode = mode
            });
        }

        private static string Describe(ObjectDiffState state) => state switch
        {
            ObjectDiffState.Modified => "수정됨",
            ObjectDiffState.MissingInBranch => "DB에만 있음",
            _ => "브랜치에만 있음"
        };
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~BaselineRunnerTests"`
Expected: PASS 10/10

- [ ] **Step 5: 커밋**

```bash
git add tools/DBVC.Baseline/BaselineRunner.cs tests/DBVC.Baseline.Tests/BaselineRunnerTests.cs
git commit -F - <<'EOF'
feat(baseline): 추출과 자체 검증의 순서를 지휘하는 러너를 더한다

추출은 Write에서만, 차이 검사는 Write가 아닐 때만 허용되므로 같은 매핑의 mode를
뒤집어 두 단계를 잇는다. 검증 판정에 IsInSync만 쓰지 않는다 - 판정하지 못한
객체와 다 읽지 못한 저장소는 Differences에 들어오지 않아, 차이 0이 일치를
뜻하지 않는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 5: 진입점 — 임시 설정, 암호 프롬프트, 종료 코드

**Files:**
- Create: `tools/DBVC.Baseline/TempConfig.cs`
- Modify: `tools/DBVC.Baseline/Program.cs` (Task 1의 임시 진입점을 본체로 바꾼다)
- Create: `tests/DBVC.Baseline.Tests/TempConfigTests.cs`

**Interfaces:**
- Consumes: `BaselineOptions`, `PreflightCheck`, `PreflightInput`, `BaselineRunner`, `BaselineReport`
- Produces: `DBVC.Baseline.TempConfig { static string CreateDirectory(); static string MappingFilePath(string directory) }`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Baseline.Tests/TempConfigTests.cs`:

```csharp
using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class TempConfigTests
    {
        [Test]
        public void CreateDirectory_ReturnsPathUnderTempPath()
        {
            var path = TempConfig.CreateDirectory();
            try
            {
                Assert.That(path, Does.StartWith(Path.GetTempPath()));
                Assert.That(Directory.Exists(path), Is.True);
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }

        [Test]
        public void CreateDirectory_ReturnsPathOutsideAppData()
        {
            // 사용자의 진짜 mappings.json을 건드리면 운영이 "개발"로 매핑된 상태가 남는다.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = TempConfig.CreateDirectory();
            try
            {
                Assert.That(path, Does.Not.StartWith(appData));
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }

        [Test]
        public void CreateDirectory_ReturnsDistinctPath_WhenCalledTwice()
        {
            var first = TempConfig.CreateDirectory();
            var second = TempConfig.CreateDirectory();
            try
            {
                Assert.That(first, Is.Not.EqualTo(second));
            }
            finally
            {
                Directory.Delete(first, recursive: true);
                Directory.Delete(second, recursive: true);
            }
        }

        [Test]
        public void MappingFilePath_ReturnsMappingsJsonInsideDirectory()
        {
            var path = TempConfig.MappingFilePath(@"C:\temp\x");

            Assert.That(path, Is.EqualTo(Path.Combine(@"C:\temp\x", "mappings.json")));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~TempConfigTests"`
Expected: 컴파일 실패 — `TempConfig`가 없다.

- [ ] **Step 3: `TempConfig`를 만든다**

`tools/DBVC.Baseline/TempConfig.cs`:

```csharp
namespace DBVC.Baseline
{
    /// <summary>
    /// 임시 매핑 파일의 자리.
    ///
    /// %APPDATA%\DBVC\mappings.json을 절대 쓰지 않는다. 거기에 운영 DB가 Mode = Write로 남으면
    /// 누군가 DBVC를 열고 초기화를 누르는 순간 운영에 DDL 트리거가 설치된다. 임시 파일을 쓰면
    /// 그런 상태가 한 번도 존재하지 않는다.
    /// </summary>
    public static class TempConfig
    {
        public static string CreateDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_baseline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static string MappingFilePath(string directory) => Path.Combine(directory, "mappings.json");
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~TempConfigTests"`
Expected: PASS 4/4

- [ ] **Step 5: `Program`을 본체로 바꾼다**

`tools/DBVC.Baseline/Program.cs` 전체를 아래로 교체한다:

```csharp
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Baseline
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var parsed = BaselineOptions.Parse(args);
            if (!parsed.IsValid)
            {
                Console.Error.WriteLine(parsed.Error);
                return 3;
            }

            var options = parsed.Options!;

            var preflightError = PreflightCheck.Validate(Observe(options.RepositoryPath));
            if (preflightError != null)
            {
                Console.Error.WriteLine(preflightError);
                return 3;
            }

            var credentialStore = new SessionCredentialStore();
            if (options.SqlUser != null)
            {
                var password = ReadPasswordMasked($"{options.SqlUser}의 암호: ");
                credentialStore.Set(options.Server, options.Database, SqlAuthMode.Sql, options.SqlUser, password);
            }

            var tempDirectory = TempConfig.CreateDirectory();
            try
            {
                var configManager = new ConfigManager(TempConfig.MappingFilePath(tempDirectory));
                var smoManager = new SmoManager(configManager, credentialStore);

                var progress = new Progress<ExtractionProgress>(p =>
                    Console.Write($"\r{p.Completed}/{p.Total}  {p.CurrentObject}".PadRight(78)));

                var report = new BaselineRunner(configManager, smoManager).Run(options, progress);

                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine(report.Render());
                return report.ExitCode;
            }
            finally
            {
                // 임시 매핑이 남으면 격리가 무너진다. 실패해도 반드시 지운다.
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch (IOException) { /* 지우지 못해도 %TEMP%다. 실행 자체를 실패시키지 않는다. */ }
            }
        }

        private static PreflightInput Observe(string repositoryPath)
        {
            bool exists = Directory.Exists(repositoryPath);
            return new PreflightInput
            {
                DirectoryExists = exists,
                HasGitDirectory = exists && Directory.Exists(Path.Combine(repositoryPath, ".git")),
                HasSqlFiles = exists && Directory.EnumerateFiles(repositoryPath, "*.sql", SearchOption.AllDirectories).Any()
            };
        }

        /// <summary>
        /// 화면에 남기지 않고 암호를 읽는다. 인자로 받지 않는 이유와 같다 — 어깨너머와
        /// 터미널 스크롤백에 남기지 않는다.
        /// </summary>
        private static string ReadPasswordMasked(string prompt)
        {
            Console.Write(prompt);
            var buffer = new System.Text.StringBuilder();

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter) break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0) buffer.Length--;
                    continue;
                }

                if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
            }

            Console.WriteLine();
            return buffer.ToString();
        }
    }
}
```

- [ ] **Step 6: 전체 테스트와 빌드를 확인한다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48`
Expected: PASS **33/33** — Options 7, Preflight 5, Report 7, Runner 10, TempConfig 4.

> 개수가 다르면 앞 태스크의 테스트가 빠진 것이다. 멈추고 확인한다.

Run: `dotnet build DBVC.slnx -c Release`
Expected: 오류 0개

- [ ] **Step 7: 도움말이 실제로 나오는지 눈으로 본다**

Run: `dotnet run --project tools/DBVC.Baseline -f net48 -- --server X`
Expected: `--database가 필요합니다.` 와 사용법이 나오고 종료 코드 3.

- [ ] **Step 8: 커밋**

```bash
git add tools/DBVC.Baseline tests/DBVC.Baseline.Tests
git commit -F - <<'EOF'
feat(baseline): 진입점과 임시 설정 격리를 더한다

임시 매핑을 %TEMP% 아래 새 폴더에 쓰고 finally에서 지운다. %APPDATA%의 진짜
mappings.json에 운영 DB가 Mode = Write로 남으면 누군가 초기화를 누르는 순간
운영에 트리거가 설치되는데, 임시 파일을 쓰면 그런 상태가 한 번도 존재하지 않는다.
암호는 가려진 프롬프트로만 받아 메모리 전용 저장소에 넣는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 6: 실제 SQL Server를 상대로 한 종단 시험

**스펙과의 차이 하나를 여기서 정정한다.** 스펙 8장은 "하네스 경로로 뽑은 바이트와 일반 추출
경로로 뽑은 바이트가 같은지" 비교하라고 했는데, **비교할 두 경로가 없다** — 하네스는
`SmoManager`의 같은 메서드를 부르므로 바이트 동일성은 구성상 자명하다. 대신 **러너의 종단
동작**을 시험한다: 진짜 DB에 추출하고 이어서 검증했을 때 `Clean`이 나오는가. 이것이
mode 뒤집기 순서 같은 실제 버그를 잡는다.

**Files:**
- Create: `tests/DBVC.Baseline.Tests/BaselineRunnerIntegrationTests.cs`
- Modify: `tests/DBVC.Baseline.Tests/DBVC.Baseline.Tests.csproj` (테스트 DB 헬퍼를 링크로 공유)

**Interfaces:**
- Consumes: `BaselineRunner`(Task 4), `TempConfig`(Task 5), `DBVC.Core.Tests.SqlServerTestDatabase`(링크)

- [ ] **Step 1: 헬퍼를 링크로 가져온다**

`tests/DBVC.Baseline.Tests/DBVC.Baseline.Tests.csproj`의 마지막 `</Project>` 앞에 더한다:

```xml
  <!--
    복사하지 않고 링크한다. 임시 DB를 만들고 지우는 규약이 두 벌이 되면 한쪽만 고쳐지고,
    그 차이가 "왜 여기서만 Skip되는가"로 나타난다.
  -->
  <ItemGroup>
    <Compile Include="..\DBVC.Core.Tests\SqlServerTestDatabase.cs" Link="SqlServerTestDatabase.cs" />
  </ItemGroup>
```

- [ ] **Step 2: 헬퍼의 실제 API (확인된 값)**

`SqlServerTestDatabase`의 실제 멤버다. 추측이 아니라 파일에서 읽은 것이다:

- `const string ServerName = "localhost"`
- `string Name { get; }` — 데이터베이스 이름 (`DatabaseName`이 아니다)
- `static SqlServerTestDatabase? TryCreate(out string? skipReason)` — 접속 불가 시 `null` + 사유
- `void ExecuteInOneSession(params string[] statements)`
- `void Execute(string sql)`

접속 불가를 다루는 관례는 `SmoManagerIntegrationTests`와 같다 — `TryCreate`가 `null`이면
`skipReason`으로 `Assert.Ignore`한다.

- [ ] **Step 3: 종단 시험을 쓴다**

`tests/DBVC.Baseline.Tests/BaselineRunnerIntegrationTests.cs`:

```csharp
using DBVC.Baseline;
using DBVC.Core;
using DBVC.Core.Tests;

namespace DBVC.Baseline.Tests
{
    /// <summary>
    /// localhost의 SQL Server에 Windows 인증으로 붙어 임시 DB를 만든다.
    /// 접속되지 않으면 실패가 아니라 Skip이다 — CI에는 SQL Server가 없다.
    /// </summary>
    [TestFixture]
    public class BaselineRunnerIntegrationTests
    {
        [Test]
        public void Run_ReturnsClean_WhenExtractingRealDatabaseIntoEmptyFolder()
        {
            using var database = SqlServerTestDatabase.TryCreate(out var skipReason);
            if (database == null)
            {
                Assert.Ignore(skipReason);
                return;
            }

            database.ExecuteInOneSession(
                "CREATE TABLE dbo.Widget (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)",
                "CREATE OR ALTER PROCEDURE dbo.usp_Widget AS SELECT 1");

            var repo = TempConfig.CreateDirectory();
            var configDirectory = TempConfig.CreateDirectory();
            try
            {
                var configManager = new ConfigManager(TempConfig.MappingFilePath(configDirectory));
                var smoManager = new SmoManager(configManager, credentialStore: null);

                var options = BaselineOptions.Parse(new[]
                {
                    "--server", SqlServerTestDatabase.ServerName,
                    "--database", database.Name,
                    "--repo", repo
                }).Options!;

                var report = new BaselineRunner(configManager, smoManager).Run(options);

                Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.Clean), report.Render());
                Assert.That(report.ExtractedCount, Is.GreaterThan(0));
                Assert.That(File.Exists(Path.Combine(repo, "dbo", "Tables", "Widget.sql")), Is.True);
            }
            finally
            {
                Directory.Delete(repo, recursive: true);
                Directory.Delete(configDirectory, recursive: true);
            }
        }
    }
}
```

> `dbo/Tables/Widget.sql`은 `ObjectPathConvention`이 만드는 경로를 가정한 것이다. 첫 실행에서
> 어긋나면 실제 산출 경로에 맞춘다 — 그 규약은 `ObjectPathConventionTests`가 이미 고정하고 있다.

- [ ] **Step 4: 돌려 본다**

Run: `dotnet test tests/DBVC.Baseline.Tests -f net48 --filter "FullyQualifiedName~BaselineRunnerIntegrationTests"`

Expected: 로컬에 SQL Server가 있으면 PASS, 없으면 **Skip 1개**. 실패는 아니다.

- [ ] **Step 5: 커밋**

```bash
git add tests/DBVC.Baseline.Tests
git commit -F - <<'EOF'
test(baseline): 진짜 DB를 상대로 한 종단 시험을 더한다

스펙이 요구한 "두 경로의 바이트 비교"는 성립하지 않는다 - 하네스가 SmoManager의
같은 메서드를 부르므로 비교할 두 경로가 없다. 대신 추출에 이어 검증까지 돌려
Clean이 나오는지 본다. mode 뒤집기 순서 같은 실제 버그는 이쪽이 잡는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

---

### Task 7: 문서를 맞추고 아티팩트를 다시 게시한다

**10번을 닫는 것은 도구가 아니라 이 절차다.**

**Files:**
- Modify: `docs/setup-checklist.md`
- Modify: `docs/team-rollout-backlog.md`
- Modify: `docs/why-db-version-control.html`
- Modify: `CLAUDE.md`

**아티팩트 URL:** `https://claude.ai/code/artifact/c0a8e030-cea9-4d79-93cc-0e27bbdcd1cf`

- [ ] **Step 1: `setup-checklist.md`에 런북을 더한다**

10번이 가리키던 "길은 둘이다. 정해서 여기에 적는다" 절을 아래로 교체한다. 이 절이 10번을 닫는다.

````markdown
### `master` 기준선을 만든다

**운영 DB에는 아무것도 설치되지 않는다.** 이 절차가 운영에 하는 일은 읽기 하나뿐이고, 그것은
앞으로 감사 클론이 매주 하게 될 바로 그 읽기다. 트리거도 테이블도 만들지 않는다.

감사 클론은 추출·커밋이 잠겨 있어 스스로 `master`를 채우지 못한다. 그래서 별도 도구
(`tools/DBVC.Baseline`)로 한 번 만든다.

- [ ] **`develop`과 `master` 브랜치를 만든다.** 3단계에서 만든 원격 저장소에 둘 다 있어야 한다.
      기본 브랜치 이름이 `main`이면 그것과 별개로 둘을 만든다.
- [ ] **`master`를 체크아웃한 빈 클론**을 하나 만든다. 개발·테스트·운영 클론과 다른 폴더여야
      한다 — 도구는 `.sql`이 이미 있는 폴더를 거부한다.
- [ ] **읽기 계정을 확인한다.** 그 계정에 대상 객체의 `VIEW DEFINITION`이 있어야 한다.
      없는 객체는 조용히 빠지고, 나중에 "브랜치에만 있음"으로 떠서 배포 스크립트에 `CREATE`가
      들어간다.
- [ ] **도구를 실행한다.** 암호는 인자로 받지 않고 실행 중에 묻는다.

      ```
      dotnet run --project tools/DBVC.Baseline -f net48 -- ^
        --server <운영서버> --database <DB> --repo <그 클론 경로> --sql-user <읽기 계정>
      ```

- [ ] **종료 코드로 판단한다.**

      | 코드 | 뜻 | 할 일 |
      | --- | --- | --- |
      | 0 | 추출·검증 모두 깨끗 | `git status`로 확인하고 커밋·push |
      | 1 | 스크립팅에 실패한 객체가 있다 | 출력된 목록의 객체에 `VIEW DEFINITION`을 부여하고 다시 실행 |
      | 2 | 검증에서 차이가 나왔다 | **커밋하지 않는다.** 실행 중 운영이 바뀌었을 수 있으니 다시 실행 |
      | 3 | 인자·사전 점검 오류 | 출력된 사유대로 고친다 |

- [ ] **클론을 정리한다.** 그대로 감사 클론으로 써도 되고, 폐기하고 8단계에서 새로 만들어도 된다.

도구는 커밋하지 않는다. 운영 사진이 사람 눈을 거치지 않고 `master`에 올라가는 것은 자동화할
자리가 아니기 때문이다.
````

교체할 원문의 정확한 위치는 `grep -n "길은 둘이다" docs/setup-checklist.md` 로 찾는다.
3단계의 브랜치 생성 항목이 정말 없는지도 함께 확인한다 —
`grep -n "브랜치" docs/setup-checklist.md | sed -n '1,10p'`.

- [ ] **Step 2: `team-rollout-backlog.md`의 10번을 닫는다**

우선순위 표의 `**P1** | 10번 master 기준선 절차` 행을 취소선 처리하고, 사유를 적는다:
A(백업 복원)는 백업이 몇 테라이고 비운영 인스턴스가 없어 배제, D(하네스) 채택.
10번 절 본문의 "길은 둘이다. 정해서 여기에 적는다"를 **정해진 결과**로 바꾼다.

C(감사 모드에 기준선 만들기를 여는 것)를 **P3 항목으로 새로 세운다** — 환경을 새로 온보딩할
때마다 하네스를 꺼내야 하는 불편이 쌓이면 그때 착수한다.

- [ ] **Step 3: 설득 자료를 고친다 (세 자리)**

먼저 기준값을 잰다. Run: `grep -c "<li>" docs/why-db-version-control.html`
**2026-09-09 기준 실측값은 `36`이다.** 다르면 멈추고 알린다. 이 단계가 끝나면 `35`여야 한다.

**(1) 도입 절 함정 2를 다시 쓴다** (`:1234` 부근). 찾기:

```html
        <li>
          <strong><code>master</code>를 무엇으로 채울지가 아직 정해지지 않았다.</strong>
          감사 클론은 추출·커밋이 잠겨 있어 스스로 채우지 못한다 — 운영 DB에 트리거를
          설치하는 사고를 막는 것이 그 모드의 존재 이유라 옳은 설계지만, 그래서 채울 경로가
          도구 안에 없다. 길은 둘이다: <strong>운영 백업을 복원한 임시 DB에서 뽑아
          커밋</strong>하거나, <strong>첫 정규 배포 시점의 <code>develop</code> 커밋을
          <code>master</code>로 삼는다.</strong> 뒤쪽은 준비가 필요 없는 대신 그때까지 운영
          드리프트가 보이지 않는다.
        </li>
```

바꾸기:

```html
        <li>
          <strong><code>master</code>는 별도 도구로 한 번 채운다.</strong>
          감사 클론은 추출·커밋이 잠겨 있어 스스로 채우지 못한다 — 운영 DB에 트리거를
          설치하는 사고를 막는 것이 그 모드의 존재 이유라 옳은 설계다. 그래서 기준선은
          <strong>운영 DB를 읽기만 하는 별도 도구</strong>로 한 번 만든다. 운영에는 트리거도
          테이블도 <em>아무것도 설치되지 않는다</em> — 읽는 것은 앞으로 감사 클론이 매주 하게 될
          바로 그 읽기다. 절차는 <code>docs/setup-checklist.md</code>에 있다.
        </li>
```

**(2) 결정 절 머리말에서 예외 문장을 지운다** (`:1419` 부근). 찾기:

```html
          정해도 도입은 시작할 수 있다. <strong>다만 <code>master</code> 기준선은
```

이 문장이 시작되는 자리부터 그 문단의 마침표까지를 지우고, 앞 문장이 자연스럽게 끝나도록
`정해도 도입은 시작할 수 있다.` 로 마무리한다. **근거가 사라졌으므로 문장째 없앤다** —
백업 복원 리드타임이 더는 존재하지 않는다.

**(3) 결정 항목을 통째로 삭제한다** (`:1483` 부근). `<b><code>master</code> 기준선을 어떻게
만들지 <span class="who who-dba">DBA</span></b>` 로 시작하는 `<li>` 를 **여는 `<li>`부터 짝이
되는 `</li>`까지** 지운다. 안에 `<span class="record">` 블록이 들어 있으므로 그것까지 함께
빠져야 한다.

확인: `grep -c "<li>"` → `35`. 그리고 `grep -n "기준선을 어떻게 만들지\|정해지지 않았다" docs/why-db-version-control.html` → 결과 없음.

- [ ] **Step 4: `CLAUDE.md`를 맞춘다**

아키텍처 절의 "두 계층이다"를 세 프로젝트 구성으로 고치고, 빌드·테스트 절에
`dotnet test tests/DBVC.Baseline.Tests -f net48` 을 더한다.
하네스가 **운영 DB를 읽기만 한다**는 것과 `%APPDATA%`를 건드리지 않는다는 것을 한 줄로 남긴다.

- [ ] **Step 5: 일관성을 확인한다**

```bash
grep -rn "정해지지 않았다\|길은 둘이다" docs/setup-checklist.md docs/team-rollout-backlog.md docs/why-db-version-control.html
```
Expected: 아무것도 나오지 않는다.

```bash
grep -c "DBVC.Baseline" CLAUDE.md docs/setup-checklist.md
```
Expected: 둘 다 1 이상.

- [ ] **Step 6: 커밋**

```bash
git add docs CLAUDE.md
git commit -F - <<'EOF'
docs: master 기준선 절차를 정하고 10번을 닫는다

백업이 몇 테라이고 비운영 인스턴스가 없어 복원본에서 뽑는 길이 막혔다. 운영 DB를
읽기만 하는 하네스로 만들기로 하고 런북을 적는다. 회의 결정 항목이 하나 줄어든다.
감사 모드에 기준선 만들기를 여는 안은 P3로 새로 세운다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YajCB3Xxjmq6Ut3wcnSgiV
EOF
```

- [ ] **Step 7: 아티팩트를 같은 URL로 다시 게시한다**

Artifact 도구 `action: "read"` 로 `https://claude.ai/code/artifact/c0a8e030-cea9-4d79-93cc-0e27bbdcd1cf` 를
읽고(게시 전 읽기 게이트), 이어서 `file_path: docs/why-db-version-control.html`,
`url:` 같은 주소, `label: "master 기준선 결정 반영"` 으로 게시한다. `favicon`은 넘기지 않는다.

> 이 단계는 **메인 세션에서만** 된다. 서브에이전트로 실행 중이라면 Step 1~6까지만 하고
> 이 단계는 컨트롤러에게 넘긴다.

---

## 이 계획이 끝나도 남는 것

- **하네스가 운영 DB에 붙어 실제로 도는 것은 확인되지 않는다.** CI에는 SQL Server가 없고
  운영은 더더욱 없다. **첫 실행이 인수 시험이다** — 그 전에는 "동작한다"고 말하지 않는다.
- 소요 시간과 그 계정의 `VIEW DEFINITION` 실제 범위는 첫 실행이 알려 준다.
