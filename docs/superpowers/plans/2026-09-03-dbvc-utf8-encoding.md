# 저장소 인코딩 UTF-8 전환 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SMO가 저장소에 쓰는 `.sql`을 UTF-16LE에서 UTF-8 + BOM으로 바꿔, Git이 텍스트로 보고 GitLab이 diff를 그리며 3-way 병합이 성립하게 한다.

**Architecture:** 쓰는 자리는 둘뿐이다(`SmoManager.BuildScriptingOptions`, `DeploymentViewModel.SaveScript`). 읽는 자리 여섯 곳은 `File.ReadAllText`와 `Blob.GetContentText()`가 모두 BOM을 감지하므로 손대지 않고, 그 전제를 특성화 테스트로 고정한다. 이미 UTF-16으로 채워진 저장소는 새 `RepositoryEncoding` 판정기 + 배너/버튼으로 사용자가 전환한다.

**Tech Stack:** .NET Standard 2.0 / .NET Framework 4.8, SMO 171.30.0, LibGit2Sharp 0.32.0, NUnit 4, Moq, WPF

**Spec:** `docs/superpowers/specs/2026-09-03-dbvc-utf8-encoding-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지·알림·버튼·ToolTip 포함.
- **주석은 "왜"만 적는다.** 한국어 평서문. 함정과 근거를 남기는 기존 문체를 따른다.
- **테스트 이름은 영어** `Method_Result_WhenCondition` 형태.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): ...`
- **TDD**: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **패키지 버전을 절대 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0` 고정. 근거는 `DBVC.Core.csproj` 주석.
- **테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.** 전이 참조로만 받는다.
- 빌드/테스트 명령:
  - `dotnet build DBVC.slnx`
  - `dotnet test tests/DBVC.Core.Tests -f net10.0`
  - `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
  - `-f net48`은 Windows에서만 실행된다. 최종 확인에 둘 다 돌린다.
- **릴리스 버전은 `0.5.15`.** 스키마 버전(v5)은 **바뀌지 않는다** — 데이터베이스를 건드리지 않는 변경이다.
- `SmoManagerIntegrationTests`는 `localhost` SQL Server에 붙는다. 접속되지 않으면 실패가 아니라 Skip이다.

---

## File Structure

| 파일 | 책임 | 상태 |
| --- | --- | --- |
| `src/DBVC.Core/RepositoryEncoding.cs` | 저장소가 옛 인코딩인지 판정하고 `.gitattributes`를 만든다 | **신규** |
| `src/DBVC.Core/SmoManager.cs` | `BuildScriptingOptions()`에 `Encoding` 추가 | 수정 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 배너 상태 + 전환 명령 | 수정 |
| `src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs` | 생성 스크립트를 BOM과 함께 저장 | 수정 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 전환 배너 | 수정 |
| `tests/DBVC.Core.Tests/FileEncodingTests.cs` | 읽는 자리의 BOM 중립성 특성화 | **신규** |
| `tests/DBVC.Core.Tests/RepositoryEncodingTests.cs` | 판정기 + `.gitattributes` | **신규** |

`RepositoryEncoding`을 `ExtractionBaseline`에 합치지 않는다. 그쪽은 "추출물이 있는가"라는 다른 질문에 답하고 이미 `Refresh`의 분기를 정하는 데 쓰이고 있어, 인코딩 판정을 얹으면 한 함수가 두 결정을 하게 된다.

---

## Task 1: 읽는 자리의 BOM 중립성을 특성화 테스트로 고정한다

이 작업 전체가 "읽는 코드는 안 바꿔도 된다"는 전제 위에 서 있다. 그 전제를 먼저 못 박아야 나머지 작업이 안전하다. LibGit2Sharp나 .NET 업그레이드가 이 동작을 바꾸면 Diff의 Old쪽이 조용히 깨지는데, 이 테스트가 없으면 아무 데서도 잡히지 않는다.

**Files:**
- Test: `tests/DBVC.Core.Tests/FileEncodingTests.cs` (신규)

**Interfaces:**
- Consumes: 없음
- Produces: 없음 (특성화 테스트 전용)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/FileEncodingTests.cs`를 새로 만든다.

```csharp
using System;
using System.IO;
using System.Text;
using LibGit2Sharp;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// DBVC는 .sql을 여섯 자리에서 읽는데 어디에서도 인코딩을 지정하지 않는다. 저장소 인코딩을
    /// UTF-8로 바꾸면서 그 여섯 곳을 손대지 않기로 한 근거가 여기 고정되어 있다 —
    /// File.ReadAllText와 Blob.GetContentText가 둘 다 BOM을 감지하고, 없으면 UTF-8로 읽는다.
    ///
    /// 이 전제가 깨지면(라이브러리 업그레이드 등) Diff의 Old쪽이 조용히 깨진다. 화면에는
    /// 깨진 글자가 아니라 "전부 변경됨"으로 나타나 원인을 짐작하기 어렵다.
    /// </summary>
    [TestFixture]
    public class FileEncodingTests
    {
        private static readonly Signature TestSignature =
            new Signature("Test", "test@example.com", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        /// <summary>한국어를 넣는다. ASCII만으로는 인코딩이 어긋나도 통과해 버린다.</summary>
        private const string Sql = "CREATE PROCEDURE dbo.P AS SELECT 1 -- 한글 주석";

        private static Encoding EncodingFor(string kind) => kind switch
        {
            "utf16" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf8bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        private static string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_enc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [TestCase("utf16")]
        [TestCase("utf8bom")]
        [TestCase("utf8nobom")]
        public void FileReadAllText_RoundTripsTheText_ForEveryEncodingDbvcMayEncounter(string kind)
        {
            var dir = NewTempDir();
            var file = Path.Combine(dir, "p.sql");
            File.WriteAllText(file, Sql, EncodingFor(kind));

            Assert.That(File.ReadAllText(file), Is.EqualTo(Sql));
        }

        [TestCase("utf16")]
        [TestCase("utf8bom")]
        [TestCase("utf8nobom")]
        public void BlobGetContentText_RoundTripsTheText_ForEveryEncodingDbvcMayEncounter(string kind)
        {
            var dir = NewTempDir();
            File.WriteAllText(Path.Combine(dir, "p.sql"), Sql, EncodingFor(kind));

            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commands.Stage(repo, "*");
            var commit = repo.Commit("initial", TestSignature, TestSignature);

            var blob = (Blob)commit["p.sql"].Target;
            Assert.That(blob.GetContentText(), Is.EqualTo(Sql));
        }
    }
}
```

- [ ] **Step 2: 돌려서 통과하는지 본다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~FileEncodingTests"`
Expected: **PASS 6개.**

이 테스트는 예외적으로 처음부터 통과한다. 새 동작을 만드는 것이 아니라 이미 있는 동작을 고정하는 특성화 테스트이기 때문이다. 여기서 하나라도 실패하면 이 계획의 전제가 틀린 것이므로 **더 진행하지 말고 보고한다.**

- [ ] **Step 3: 커밋**

```bash
git add tests/DBVC.Core.Tests/FileEncodingTests.cs
git commit -m "test(core): 읽는 자리가 BOM을 감지한다는 전제를 고정한다"
```

---

## Task 2: `RepositoryEncoding` — 판정기와 `.gitattributes`

**Files:**
- Create: `src/DBVC.Core/RepositoryEncoding.cs`
- Test: `tests/DBVC.Core.Tests/RepositoryEncodingTests.cs` (신규)

**Interfaces:**
- Consumes: `ObjectPathConvention.TryParseRelativePath(string, out string, out string, out string)`, `ObjectPathConvention.UnknownFolder` (기존, `ExtractionBaseline.cs`가 쓰는 것과 같음)
- Produces:
  - `enum DBVC.Core.RepositoryEncodingKind { Unknown, Legacy, Current }`
  - `static RepositoryEncodingKind RepositoryEncoding.Detect(string repoPath)`
  - `static bool RepositoryEncoding.EnsureGitAttributes(string repoPath)` — 만들었으면 `true`, 이미 있어 두었으면 `false`
  - `const string RepositoryEncoding.GitAttributesContent`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/RepositoryEncodingTests.cs`를 새로 만든다.

```csharp
using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class RepositoryEncodingTests
    {
        private static string NewRepoDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_repoenc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>규약(`[Schema]/[Type]/[Name].sql`)에 맞는 자리에 파일을 놓는다.</summary>
        private static void WriteObject(string repoPath, string encoding)
        {
            var dir = Path.Combine(repoPath, "dbo", "Tables");
            Directory.CreateDirectory(dir);

            var enc = encoding == "utf16"
                ? (Encoding)new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

            File.WriteAllText(Path.Combine(dir, "Users.sql"), "CREATE TABLE dbo.Users (Id int);", enc);
        }

        [Test]
        public void Detect_ReturnsLegacy_WhenTheFilesAreUtf16()
        {
            var repo = NewRepoDir();
            WriteObject(repo, "utf16");

            Assert.That(RepositoryEncoding.Detect(repo), Is.EqualTo(RepositoryEncodingKind.Legacy));
        }

        [Test]
        public void Detect_ReturnsCurrent_WhenTheFilesAreUtf8()
        {
            var repo = NewRepoDir();
            WriteObject(repo, "utf8");

            Assert.That(RepositoryEncoding.Detect(repo), Is.EqualTo(RepositoryEncodingKind.Current));
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenNoExtractedObjectExists()
        {
            // 갓 연결한 저장소다. 판정할 근거가 없으므로 배너를 띄우면 안 된다 -
            // 전 파일 재작성을 권하는 안내가 빈 저장소에 뜨는 것은 명백히 틀렸다.
            Assert.That(RepositoryEncoding.Detect(NewRepoDir()), Is.EqualTo(RepositoryEncodingKind.Unknown));
        }

        [Test]
        public void Detect_IgnoresSqlFilesOutsideTheConvention()
        {
            // 사용자가 루트에 넣어 둔 .sql을 판정 근거로 삼으면, 그 파일 하나 때문에
            // 멀쩡한 저장소에 전환 배너가 뜬다. ExtractionBaseline과 같은 엄격함이다.
            var repo = NewRepoDir();
            File.WriteAllText(Path.Combine(repo, "adhoc.sql"), "SELECT 1;",
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            Assert.That(RepositoryEncoding.Detect(repo), Is.EqualTo(RepositoryEncodingKind.Unknown));
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenThePathDoesNotExist()
        {
            Assert.That(RepositoryEncoding.Detect(Path.Combine(Path.GetTempPath(), "dbvc_missing_" + Guid.NewGuid())),
                Is.EqualTo(RepositoryEncodingKind.Unknown));
        }

        [Test]
        public void GitAttributesContent_TurnsOffEolConversionForSqlFiles()
        {
            // text eol=crlf를 쓰면 블롭은 LF, 작업 트리는 CRLF가 된다. Diff의 Old는 블롭에서
            // New는 작업 트리에서 오므로 모든 줄이 변경으로 보인다. 실측으로 확인한 함정이다.
            Assert.That(RepositoryEncoding.GitAttributesContent, Does.Contain("*.sql -text"));
            Assert.That(RepositoryEncoding.GitAttributesContent, Does.Not.Contain("eol=crlf"));
        }

        [Test]
        public void EnsureGitAttributes_WritesTheFile_WhenItIsMissing()
        {
            var repo = NewRepoDir();

            Assert.That(RepositoryEncoding.EnsureGitAttributes(repo), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(repo, ".gitattributes")), Does.Contain("*.sql -text"));
        }

        [Test]
        public void EnsureGitAttributes_LeavesAnExistingFileAlone()
        {
            // 사용자가 손으로 넣은 규칙을 덮어쓰면 그 사람의 저장소 설정이 조용히 사라진다.
            var repo = NewRepoDir();
            var path = Path.Combine(repo, ".gitattributes");
            File.WriteAllText(path, "* text=auto\n");

            Assert.That(RepositoryEncoding.EnsureGitAttributes(repo), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("* text=auto\n"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RepositoryEncodingTests"`
Expected: 컴파일 실패 — `'RepositoryEncoding'이라는 이름이 현재 컨텍스트에 없습니다` (CS0103)

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/RepositoryEncoding.cs`를 새로 만든다.

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DBVC.Core
{
    /// <summary>저장소에 쌓인 추출물의 인코딩 세대.</summary>
    public enum RepositoryEncodingKind
    {
        /// <summary>판정할 근거가 없다. 갓 연결한 저장소이거나 읽을 수 없다.</summary>
        Unknown,

        /// <summary>0.5.15 이전이 쓴 UTF-16LE. Git이 바이너리로 취급해 diff도 병합도 되지 않는다.</summary>
        Legacy,

        /// <summary>UTF-8. Git이 텍스트로 본다.</summary>
        Current
    }

    /// <summary>
    /// 저장소의 추출물이 옛 인코딩(UTF-16LE)인지 판정하고, 줄바꿈 변환을 끄는 .gitattributes를 만든다.
    ///
    /// <see cref="ExtractionBaseline"/>과 합치지 않는다. 그쪽은 "추출물이 있는가"라는 다른 질문에
    /// 답하고 이미 새로고침의 분기를 정하는 데 쓰이고 있어, 인코딩 판정을 얹으면 한 함수가 두
    /// 결정을 하게 된다.
    /// </summary>
    public static class RepositoryEncoding
    {
        /// <summary>
        /// SMO가 쓰는 줄바꿈은 CRLF다. 변환을 끄면 작업 트리와 블롭의 바이트가 같아진다 —
        /// Diff의 Old는 블롭에서(GetFileContentAtHead), New는 작업 트리에서(ReadWorkingTreeFile)
        /// 오므로, 변환이 끼면 DiffPlex가 모든 줄을 변경으로 판정한다. 양쪽을 정규화하는 코드는 없다.
        /// 텍스트 diff와 3-way 병합은 -text와 무관하게 그대로 동작한다(실측 확인).
        /// </summary>
        public const string GitAttributesContent =
            "# DBVC가 추출하는 .sql은 SMO가 CRLF로 쓴다. 줄바꿈 변환을 끄면 작업 트리와 블롭의\r\n" +
            "# 바이트가 같아진다 — Diff의 Old는 블롭에서, New는 작업 트리에서 오므로 변환이 끼면\r\n" +
            "# 모든 줄이 변경으로 보인다. 텍스트 diff와 3-way 병합은 -text와 무관하게 동작한다.\r\n" +
            "*.sql -text\r\n";

        /// <summary>UTF-16LE BOM. 이 두 바이트로 시작하면 0.5.15 이전이 쓴 파일이다.</summary>
        private const byte Utf16LeBom0 = 0xFF;
        private const byte Utf16LeBom1 = 0xFE;

        /// <summary>
        /// 규약을 따르는 <c>.sql</c>을 <b>처음 하나만</b> 찾아 앞 2바이트를 본다.
        ///
        /// 전부를 훑지 않는 이유는 <see cref="ExtractionBaseline.Exists"/>와 같다. 저장소가 한
        /// 인코딩으로 통일되어 있다는 전제가 깨지는 경우는 전환이 중간에 멈춘 때뿐이고,
        /// 그때는 다시 눌러 이어가면 된다(전체 추출은 멱등이다).
        ///
        /// 판정하지 못하면 <see cref="RepositoryEncodingKind.Unknown"/>이다. 배너를 띄우지 않는
        /// 쪽이, 멀쩡한 저장소에 전 파일 재작성을 권하는 쪽보다 안전하다.
        /// </summary>
        public static RepositoryEncodingKind Detect(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return RepositoryEncodingKind.Unknown;

            try
            {
                if (!Directory.Exists(repoPath)) return RepositoryEncodingKind.Unknown;

                foreach (var schemaDir in Directory.EnumerateDirectories(repoPath))
                {
                    var schema = Path.GetFileName(schemaDir);

                    foreach (var typeDir in Directory.EnumerateDirectories(schemaDir))
                    {
                        var folder = Path.GetFileName(typeDir);

                        foreach (var file in Directory.EnumerateFiles(typeDir, "*.sql"))
                        {
                            var relativePath = $"{schema}/{folder}/{Path.GetFileName(file)}";

                            if (!ObjectPathConvention.TryParseRelativePath(relativePath, out _, out var objectType, out _)
                                || objectType == ObjectPathConvention.UnknownFolder)
                            {
                                continue;
                            }

                            return ReadKind(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RepositoryEncoding.Detect failed for '{repoPath}': {ex.Message}");
            }

            return RepositoryEncodingKind.Unknown;
        }

        private static RepositoryEncodingKind ReadKind(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var head = new byte[2];
            if (stream.Read(head, 0, 2) < 2) return RepositoryEncodingKind.Current;

            return head[0] == Utf16LeBom0 && head[1] == Utf16LeBom1
                ? RepositoryEncodingKind.Legacy
                : RepositoryEncodingKind.Current;
        }

        /// <summary>
        /// 줄바꿈 변환을 끄는 <c>.gitattributes</c>를 저장소 루트에 만든다.
        /// 이미 있으면 건드리지 않는다 - 사용자가 손으로 넣은 규칙을 덮어쓰지 않기 위해서다.
        /// </summary>
        /// <returns>새로 만들었으면 true. 이미 있었거나 쓰지 못했으면 false.</returns>
        public static bool EnsureGitAttributes(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return false;

            try
            {
                var path = Path.Combine(repoPath, ".gitattributes");
                if (File.Exists(path)) return false;

                // BOM 없이 쓴다. .gitattributes는 Git이 읽는 설정 파일이고 ASCII 밖의 내용은
                // 주석뿐이라, BOM을 붙이면 첫 줄만 Git이 규칙으로 알아보지 못할 위험이 생긴다.
                File.WriteAllText(path, GitAttributesContent, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RepositoryEncoding.EnsureGitAttributes failed for '{repoPath}': {ex.Message}");
                return false;
            }
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RepositoryEncodingTests"`
Expected: PASS 8개

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/RepositoryEncoding.cs tests/DBVC.Core.Tests/RepositoryEncodingTests.cs
git commit -m "feat(core): 저장소가 옛 인코딩인지 판정하고 .gitattributes를 만든다"
```

---

## Task 3: SMO가 UTF-8 + BOM으로 쓰게 한다

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs` — `BuildScriptingOptions()` (372-400행 근처)
- Test: `tests/DBVC.Core.Tests/SmoManagerTests.cs:200` (기존 테스트 수정 + 신규 1개)
- Test: `tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs` (신규 1개, SQL Server 필요)

**Interfaces:**
- Consumes: `RepositoryEncoding` 없음 (독립)
- Produces: 없음 (동작 변경만)

- [ ] **Step 1: 실패하는 테스트를 쓴다 (단위)**

`tests/DBVC.Core.Tests/SmoManagerTests.cs`의 `ScriptAll_PreservesBytesExactly_WhenContentDiffers`에서 기대 바이트를 바꾸고, 옵션 검증 테스트를 하나 더한다. 기존 테스트는 **지우지 않는다** — 바이트를 그대로 옮긴다는 의도는 그대로다.

기존 199-219행의 이 줄을

```csharp
                var expected = new byte[] { 0xFF, 0xFE, 0x43, 0x00, 0x52, 0x00 }; // UTF-16LE BOM + "CR"
```

이렇게 바꾼다.

```csharp
                var expected = new byte[] { 0xEF, 0xBB, 0xBF, 0x43, 0x52 }; // UTF-8 BOM + "CR"
```

그리고 같은 `[TestFixture]` 안에 새 테스트를 더한다.

```csharp
        [Test]
        public void BuildScriptingOptions_WritesUtf8WithBom()
        {
            // 설정하지 않으면 SMO 기본값(UTF-16LE)이 나가고, Git이 저장소의 모든 .sql을
            // 바이너리로 취급한다 - GitLab MR에서 diff가 보이지 않고, 겹치지 않는 변경끼리도
            // 3-way 병합이 성립하지 않는다.
            //
            // BOM을 붙이는 이유는 이 파일을 읽는 것이 DBVC만이 아니기 때문이다. SSMS와 sqlcmd는
            // BOM이 없는 .sql을 Windows ANSI 코드페이지로 읽어 한국어 주석과 MS_Description을
            // 깨뜨린다. DBVC 자신의 읽기는 어느 쪽이든 동작한다(FileEncodingTests).
            var options = SmoManager.BuildScriptingOptions();

            Assert.That(options.Encoding.GetPreamble(), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: 2개 FAIL — `BuildScriptingOptions_WritesUtf8WithBom`(preamble이 `FF FE`), `ScriptAll_PreservesBytesExactly_WhenContentDiffers`(바이트 불일치)

> `ScriptAll_PreservesBytesExactly`는 기대 바이트를 직접 써 주는 테스트라 구현과 무관하게 통과할 수도 있다. 그래도 `BuildScriptingOptions_WritesUtf8WithBom`이 반드시 실패해야 한다. 실패하지 않으면 편집이 반영되지 않은 것이다.

- [ ] **Step 3: 구현한다**

`src/DBVC.Core/SmoManager.cs` 맨 위 `using`에 `System.Text`가 없으면 더한다.

```csharp
using System.Text;
```

`BuildScriptingOptions()`의 `ScriptForCreateOrAlter = true` **다음 줄**에 더한다.

```csharp
                ScriptForCreateOrAlter = true,

                // 설정하지 않으면 SMO 기본값 UTF-16LE로 나가고, Git이 그 파일을 바이너리로 본다 —
                // diff도 3-way 병합도 성립하지 않아 GitLab에서 스키마 변경을 리뷰할 수 없다.
                // BOM을 붙이는 이유는 SSMS·sqlcmd가 BOM 없는 .sql을 ANSI 코드페이지로 읽어
                // 한국어 주석과 MS_Description을 깨뜨리기 때문이다.
                //
                // ScriptDrops가 값과 무관하게 ScriptForCreateOrAlter를 꺼버리는 부작용이 있었으므로
                // 이 세터도 순서를 의심할 대상이다. 실제 산출물의 앞 3바이트를
                // SmoManagerIntegrationTests가 확인한다.
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: PASS

- [ ] **Step 5: 통합 테스트를 더한다 (실제 SMO 산출물)**

`tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs`의 `ScriptObjectsDetailed_WritesTheActualCreateStatement`(85행) **바로 위**에 더한다. 이 픽스처의 `TempRepo`(479행)가 임시 저장소와 `SmoManager`를 함께 만들어 준다.

```csharp
        [Test]
        public void ScriptObjectsDetailed_WritesUtf8WithBom_ToTheRepository()
        {
            // 옵션을 설정하는 것과 SMO가 그것을 지키는 것은 다르다. ScriptDrops 세터가 값과 무관하게
            // ScriptForCreateOrAlter를 꺼버린 전례가 있으므로, 실제로 나온 파일의 앞 3바이트를 본다.
            //
            // 여기가 이 변경에서 SMO의 실제 동작을 확인하는 유일한 자리다. 단위 테스트는
            // ScriptingOptions 객체만 보므로 SMO가 그것을 무시해도 통과한다.
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result, Is.Not.Null, "추출이 시작조차 못 했습니다.");

            // 픽스처가 만든 객체 중 한국어 확장 속성이 붙은 테이블을 고른다.
            var path = Path.Combine(repo.Path, "dbo", "Tables", "Users.sql");
            Assert.That(File.Exists(path), Is.True);

            var bytes = File.ReadAllBytes(path);

            Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(3));
            Assert.That(new[] { bytes[0], bytes[1], bytes[2] },
                Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }),
                "SMO가 ScriptingOptions.Encoding을 지키지 않았습니다");

            // 한국어가 실제로 왕복하는지도 함께 본다. BOM만 맞고 본문이 깨지면 의미가 없다.
            // 픽스처의 MS_Description 값이 '사용자'다.
            Assert.That(File.ReadAllText(path), Does.Contain("사용자"));
        }
```

- [ ] **Step 6: 통합 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerIntegrationTests"`
Expected: PASS. **SQL Server에 붙지 못해 Skip되면 통과로 치지 않는다** — 이 테스트가 이 작업에서 유일하게 SMO의 실제 동작을 확인하는 자리다. Skip이면 그 사실을 보고한다.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Core/SmoManager.cs tests/DBVC.Core.Tests/SmoManagerTests.cs tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs
git commit -m "feat(core): 추출물을 UTF-8 + BOM으로 쓴다"
```

---

## Task 4: 생성된 배포 스크립트도 BOM과 함께 저장한다

지금도 살아 있는 결함이다. 배포 3단계 루프는 이 파일을 SSMS 쿼리 창에서 사람이 직접 실행하는 것을 전제하는데, BOM이 없으면 SSMS가 ANSI 코드페이지로 읽어 한국어가 깨진 채 실행된다.

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs:371`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelTests.cs`

**Interfaces:**
- Consumes: `RecordingSaveDialog.PathToReturn` (기존 테스트 더블, `TestDoubles.cs:12`), `NewViewModel(MappingMode, out string)` (기존 헬퍼, 51행)
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/DeploymentViewModelTests.cs`의 `SaveScriptCommand_ReportsNothingToWrite_WhenEveryObjectIsExcluded`(281행) **바로 위**에 더한다. 준비 방식은 그 위 테스트(263-277행)를 그대로 따랐다.

```csharp
        [Test]
        public void SaveScriptCommand_WritesUtf8WithBom_SoSsmsDoesNotReadItAsAnsi()
        {
            // 배포 스크립트는 SSMS 쿼리 창에서 사람이 직접 실행한다(배포 3단계 루프).
            // File.WriteAllText의 인자 두 개짜리 오버로드는 BOM 없는 UTF-8로 쓰는데, SSMS는
            // BOM이 없는 .sql을 Windows ANSI 코드페이지로 읽어 한국어 주석과 MS_Description을
            // 깨뜨린다. 깨진 채로 실행되면 데이터베이스에 그 상태로 들어간다.
            var vm = NewViewModel(MappingMode.Deploy, out var repoPath);

            var procPath = Path.Combine(repoPath, "dbo", "StoredProcedures");
            Directory.CreateDirectory(procPath);
            File.WriteAllText(Path.Combine(procPath, "GetUser.sql"),
                "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1 -- 사용자 조회");

            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(
                    new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            _saveDialog.PathToReturn = Path.Combine(NewTempDir(), "deploy.sql");
            vm.SaveScriptCommand.Execute(null);

            var bytes = File.ReadAllBytes(_saveDialog.PathToReturn);

            Assert.Multiple(() =>
            {
                Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(3));
                Assert.That(new[] { bytes[0], bytes[1], bytes[2] },
                    Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));

                // BOM만 맞고 본문이 깨지면 의미가 없다.
                Assert.That(File.ReadAllText(_saveDialog.PathToReturn), Does.Contain("사용자 조회"));
            });
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~SaveScript_WritesUtf8WithBom"`
Expected: FAIL — 첫 3바이트가 BOM이 아니라 스크립트 본문의 첫 글자다

- [ ] **Step 3: 구현한다**

`src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs` 맨 위 `using`에 `System.Text`가 없으면 더한다. 371행을 바꾼다.

```csharp
                // 인자 두 개짜리 오버로드는 BOM 없는 UTF-8로 쓴다. 이 파일은 SSMS 쿼리 창에서
                // 사람이 직접 실행하는데, SSMS는 BOM이 없는 .sql을 Windows ANSI 코드페이지로 읽어
                // 한국어 주석과 MS_Description을 깨뜨린다.
                File.WriteAllText(path, export.Script, new UTF8Encoding(true));
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~SaveScript_WritesUtf8WithBom"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs tests/DBVC.Vsix.Tests
git commit -m "fix(vsix): 배포 스크립트를 BOM과 함께 저장해 SSMS가 ANSI로 읽지 않게 한다"
```

---

## Task 5: 전환 배너와 버튼

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
  - `ConnectionProbe` 클래스 (479-486행 근처) — 필드 추가
  - `GatherConnectionProbe` (407-421행 근처) — 판정 채우기
  - `ApplyConnectionProbe` (445-455행 근처) — 배너 상태 세우기
  - `ApplyRefreshOutcome` (1423-1454행 근처) — 새로고침 뒤 다시 판정
  - 생성자 명령 등록 (102-115행 근처)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` — 추적기 배너(105-117행) **아래**에 새 배너
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `RepositoryEncoding.Detect(string)`, `RepositoryEncodingKind`, `RepositoryEncoding.EnsureGitAttributes(string)` (Task 2), `MappingPolicy.IsAllowed(MappingMode, DbvcOperation)` (기존)
- Produces:
  - `bool ViewChangesViewModel.IsRepositoryEncodingLegacy { get; }`
  - `ICommand ViewChangesViewModel.MigrateEncodingCommand { get; }`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`에 더한다.

**이 픽스처의 기본 매핑은 `GitPath = @"C:\repo"` 라는 없는 경로다**(`SetUp` 58행, `NewViewModelForMappedTarget` 117행). 인코딩 판정은 실제 파일을 읽어야 하므로, 아래 헬퍼가 임시 폴더로 매핑을 갈아 끼운다. `_tempDirs`/`TearDown`(31-44행)이 이미 있으므로 정리는 그쪽에 맡긴다.

먼저 헬퍼 둘을 `NewViewModelForMappedTarget`(114행) 아래에 더한다.

```csharp
        /// <summary>
        /// 실제 폴더로 매핑을 갈아 끼우고 규약대로 .sql 하나를 놓는다. 기본 매핑의 GitPath는
        /// 존재하지 않는 경로라 인코딩 판정이 늘 Unknown으로 떨어진다.
        /// </summary>
        private string NewMappedRepoWithObject(bool legacy, MappingMode mode = MappingMode.Write)
        {
            var repoPath = Path.Combine(Path.GetTempPath(), "dbvc_vmenc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);
            _tempDirs.Add(repoPath);

            var dir = Path.Combine(repoPath, "dbo", "Tables");
            Directory.CreateDirectory(dir);
            var enc = legacy
                ? (Encoding)new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(Path.Combine(dir, "Users.sql"), "CREATE TABLE dbo.Users (Id int);", enc);

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
```

`using System.Text;`가 이 파일에 없으면 맨 위에 더한다.

그리고 테스트를 더한다.

```csharp
        // ---------- 저장소 인코딩 전환 ----------

        [Test]
        public void Connect_RaisesTheEncodingBanner_WhenTheRepositoryIsStillUtf16()
        {
            NewMappedRepoWithObject(legacy: true);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsRepositoryEncodingLegacy, Is.True);
        }

        [Test]
        public void Connect_LeavesTheEncodingBannerDown_WhenTheRepositoryIsAlreadyUtf8()
        {
            NewMappedRepoWithObject(legacy: false);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsRepositoryEncodingLegacy, Is.False);
        }

        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void Connect_LeavesTheEncodingBannerDown_ForReadOnlyModes(MappingMode mode)
        {
            // 배포·감사 클론은 추출이 금지되어 있어 버튼을 눌러도 아무 일도 못 한다.
            // 전환된 커밋을 Pull하면 저절로 해결되므로, 누를 수 없는 버튼을 보여 줄 이유가 없다.
            NewMappedRepoWithObject(legacy: true, mode: mode);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsRepositoryEncodingLegacy, Is.False);
        }

        [Test]
        public void MigrateEncoding_WritesGitAttributesAndReExtractsEverything()
        {
            var repoPath = NewMappedRepoWithObject(legacy: true);
            _notifier.ConfirmResult = true;

            var vm = NewConnectedViewModel();
            vm.MigrateEncodingCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(repoPath, ".gitattributes")), Is.True,
                    ".gitattributes가 없으면 기계마다 다른 autocrlf 설정이 가짜 diff를 만든다");

                // 전체 추출은 필터가 null이다. 변경분만 뽑는 새로고침은 이름 목록을 넘긴다.
                _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null,
                    It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
            });
        }

        [Test]
        public void MigrateEncoding_DoesNothing_WhenTheUserCancelsTheConfirmation()
        {
            // 전 파일을 다시 쓰는 일이다. 실수로 눌렀을 때 되돌리려면 외부 Git 클라이언트가 필요하다.
            var repoPath = NewMappedRepoWithObject(legacy: true);
            _notifier.ConfirmResult = false;

            var vm = NewConnectedViewModel();
            vm.MigrateEncodingCommand.Execute(null);

            Assert.That(File.Exists(Path.Combine(repoPath, ".gitattributes")), Is.False);
        }

        [Test]
        public void MigrateEncoding_WarnsThatOnlyOnePersonShouldDoIt()
        {
            // 여러 사람이 각자 누르면 저장소 전체를 다시 쓴 커밋이 사람 수만큼 생겨 서로 충돌한다.
            // 도구가 막을 수는 없고 말할 수는 있다.
            NewMappedRepoWithObject(legacy: true);
            _notifier.ConfirmResult = false;

            var vm = NewConnectedViewModel();
            vm.MigrateEncodingCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Any(c => c.Message.Contains("한 사람만")), Is.True);
        }

        [Test]
        public void Refresh_LowersTheEncodingBanner_AfterTheFilesBecameUtf8()
        {
            // 전환이 끝나면 배너가 스스로 내려가야 한다. 그 사라짐이 전환이 실제로 일어났다는
            // 유일한 화면 신호이고, 다시 읽지 않으면 성공한 뒤에도 남아 사용자가 또 누른다.
            var repoPath = NewMappedRepoWithObject(legacy: true);
            _notifier.ConfirmResult = true;

            var vm = NewConnectedViewModel();
            Assume.That(vm.IsRepositoryEncodingLegacy, Is.True);

            // 추출이 목이라 파일이 저절로 바뀌지는 않는다. 전환된 결과를 손으로 만든다.
            File.WriteAllText(Path.Combine(repoPath, "dbo", "Tables", "Users.sql"),
                "CREATE TABLE dbo.Users (Id int);", new UTF8Encoding(true));

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.IsRepositoryEncodingLegacy, Is.False);
        }
```

> `ScriptObjectsDetailed`의 인자 순서는 `SetUp`(64행)의 기존 `_smo.Setup(...)`과 같다. `ISmoManager`의 정확한 시그니처는 `src/DBVC.Core/Abstractions.cs`에서 확인한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~EncodingBanner|FullyQualifiedName~MigrateEncoding"`
Expected: 컴파일 실패 — `IsRepositoryEncodingLegacy`, `MigrateEncodingCommand`가 없다 (CS1061)

- [ ] **Step 3: ViewModel을 구현한다**

`ConnectionProbe`(479-486행)에 필드를 더한다.

```csharp
            /// <summary>매핑이 없으면 Unknown이다. 판정은 Core가 하고 여기서는 나르기만 한다.</summary>
            public RepositoryEncodingKind Encoding { get; set; } = RepositoryEncodingKind.Unknown;
```

`GatherConnectionProbe`의 `probe.RepositoryState = ...` 바로 아래에 더한다.

```csharp
                // 파일을 여는 일이라 UI 스레드에서 부르지 않는다. 저장소 상태를 읽는 것과 같은 이유다.
                var mapping = _configManager.TryGetMapping(server, database);
                if (mapping != null)
                {
                    probe.Encoding = RepositoryEncoding.Detect(mapping.GitPath);
                }
```

`ApplyConnectionProbe`의 `IsTrackerOutdated = ...` 아래에 더한다.

```csharp
            // 배포·감사 클론은 추출이 금지되어 있어 이 버튼을 눌러도 아무 일도 못 한다.
            // Pull로 전환된 커밋을 받으면 저절로 해결되므로 배너 자체를 띄우지 않는다.
            IsRepositoryEncodingLegacy = probe.Encoding == RepositoryEncodingKind.Legacy
                && MappingPolicy.IsAllowed(probe.Mode, DbvcOperation.Extract);
```

속성을 `IsTrackerOutdated`(602행 근처) 옆에 같은 형태로 더한다.

```csharp
        private bool _isRepositoryEncodingLegacy;

        /// <summary>
        /// 저장소가 아직 UTF-16이라 Git이 .sql을 바이너리로 보고 있는 상태.
        /// 전환이 끝나면 <see cref="ApplyRefreshOutcome"/>가 다시 판정해 스스로 내려간다 —
        /// 그 사라짐이 전환이 실제로 일어났다는 유일한 화면 신호다.
        /// </summary>
        public bool IsRepositoryEncodingLegacy
        {
            get => _isRepositoryEncodingLegacy;
            private set
            {
                if (_isRepositoryEncodingLegacy == value) return;
                _isRepositoryEncodingLegacy = value;
                OnPropertyChanged();
                RaiseActionCanExecuteChanged();
            }
        }
```

> `OnPropertyChanged()`의 정확한 이름과 `RaiseActionCanExecuteChanged()`의 존재는 `IsTrackerOutdated`의 setter를 그대로 본떠 맞춘다.

생성자(102-115행 근처)에 명령을 등록한다.

```csharp
            MigrateEncodingCommand = new RelayCommand(MigrateEncoding,
                () => IsRepositoryEncodingLegacy && !IsBusy && MappingPolicy.IsAllowed(Mode, DbvcOperation.Extract));
```

공개 속성을 `UpdateTrackerCommand`(778행 근처) 옆에 더한다.

```csharp
        public ICommand MigrateEncodingCommand { get; }
```

`UpdateTracker`(1201행) 아래에 메서드를 더한다.

```csharp
        /// <summary>
        /// 저장소를 UTF-8로 전환한다. .gitattributes를 만든 뒤 전체를 다시 추출하면 모든 .sql이
        /// 수정으로 뜨고, 커밋은 사용자가 한다 - 저장소 전체를 다시 쓰는 유일한 커밋이므로
        /// 메시지와 시점을 사람이 정해야 한다.
        /// </summary>
        private void MigrateEncoding()
        {
            if (!HasContext || !IsRepositoryEncodingLegacy) return;

            var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
            if (mapping == null) return;

            // 여러 사람이 각자 누르면 전 파일 재작성 커밋이 사람 수만큼 생겨 서로 충돌한다.
            // 막을 수는 없고 말할 수는 있다.
            var proceed = _notifier.Confirm(
                "DBVC 저장소 인코딩 전환",
                "저장소의 모든 .sql을 UTF-8로 다시 씁니다. 전체 다시 추출이 한 번 돌고, 모든 객체가 "
                + "수정으로 표시됩니다." + Environment.NewLine + Environment.NewLine
                + "팀에서 한 사람만 하고, 나머지는 그 커밋을 Pull하세요. 여러 사람이 각자 하면 "
                + "저장소 전체를 다시 쓴 커밋이 사람 수만큼 생겨 서로 충돌합니다."
                + Environment.NewLine + Environment.NewLine
                + "계속할까요?");

            if (!proceed) return;

            // 지금 만들어야 전환 커밋에 함께 담긴다. 나중에 넣으면 그 사이의 clone이
            // 줄바꿈 변환이 켜진 채로 파일을 받는다.
            RepositoryEncoding.EnsureGitAttributes(mapping.GitPath);

            RefreshAll();
        }
```

`ApplyRefreshOutcome`(1449행 `WarningMessage = ...` 아래)에 재판정을 더한다.

```csharp
            // 전환이 끝나면 파일이 UTF-8이 되어 배너가 스스로 내려간다. 다시 읽지 않으면
            // 성공한 뒤에도 배너가 남아 사용자가 또 누른다.
            if (IsMapped)
            {
                var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
                IsRepositoryEncodingLegacy = mapping != null
                    && RepositoryEncoding.Detect(mapping.GitPath) == RepositoryEncodingKind.Legacy
                    && MappingPolicy.IsAllowed(Mode, DbvcOperation.Extract);
            }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS 전체

- [ ] **Step 5: 배너 XAML을 더한다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml`의 추적기 배너 `</Border>`(117행) **바로 아래**, 같은 `StackPanel` 안에 더한다.

```xml
            <!--
                인코딩 전환 안내. 추적기 배너와 별도로 둔다 - 원인도 조치도 다르고,
                둘이 동시에 뜰 수 있어야 한다(구버전 추적기 + 옛 인코딩).
            -->
            <Border Background="#FFF4CE" BorderBrush="#E0C77A" BorderThickness="1"
                    Padding="8,5" Margin="5,0,5,4"
                    Visibility="{Binding IsRepositoryEncodingLegacy, Converter={StaticResource BoolToVis}}">
                <DockPanel LastChildFill="True">
                    <Button DockPanel.Dock="Right" Content="전환하기" Width="110" Margin="8,0,0,0"
                            Command="{Binding MigrateEncodingCommand}"
                            ToolTip="저장소의 모든 .sql을 UTF-8로 다시 씁니다. 전체 다시 추출이 한 번 돌고 모든 객체가 수정으로 표시되며, 커밋은 직접 하셔야 합니다.&#10;팀에서 한 사람만 하고 나머지는 그 커밋을 Pull하세요."/>
                    <TextBlock Text="저장소가 옛 인코딩(UTF-16)입니다. Git이 .sql을 바이너리로 보아 GitLab에서 diff가 보이지 않습니다."
                               Foreground="#6B5A00" TextWrapping="Wrap" FontWeight="SemiBold"
                               VerticalAlignment="Center"/>
                </DockPanel>
            </Border>
```

- [ ] **Step 6: 전체 빌드와 테스트**

Run: `dotnet build DBVC.slnx` 그리고 `dotnet test DBVC.slnx -f net10.0`
Expected: 빌드 성공, 테스트 전체 PASS

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix tests/DBVC.Vsix.Tests
git commit -m "feat(vsix): 옛 인코딩 저장소를 알리고 UTF-8로 전환하는 버튼을 더한다"
```

---

## Task 6: 문서와 릴리스

**Files:**
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest`

**Interfaces:**
- Consumes: Task 1-5의 동작 전부
- Produces: 없음

- [ ] **Step 1: `README.md`에 절을 더한다**

"동작 방식" 목록의 `- **변경 추적기 업데이트(0.5.14):**` 항목 **바로 위**에 더한다.

```markdown
- **저장소 인코딩 전환(0.5.15):** 0.5.14까지 추출물이 UTF-16LE로 저장되어 **Git이 `.sql`을
  바이너리로 취급했습니다.** GitLab MR에서 스키마 변경의 diff가 보이지 않고, 겹치지 않는
  변경끼리도 3-way 병합이 되지 않았습니다. 0.5.15부터 UTF-8 + BOM으로 씁니다.
  옛 저장소에 연결하면 창 위쪽에 **"저장소가 옛 인코딩(UTF-16)입니다"** 안내와 **전환하기**
  버튼이 뜹니다. 누르면 `.gitattributes`가 만들어지고 전체 다시 추출이 돌아 모든 객체가
  `수정`으로 표시되며, **커밋은 직접 하셔야 합니다.**
  **팀에서 한 사람만 누르고 나머지는 그 커밋을 Pull하세요** — 여러 사람이 각자 하면 저장소
  전체를 다시 쓴 커밋이 사람 수만큼 생겨 서로 충돌합니다. 배포·감사 클론은 Pull만 하면
  되며 애초에 배너가 뜨지 않습니다.
  **전환 이전 커밋의 diff는 계속 바이너리로 보입니다** — 고치려면 이력을 다시 써야 하고
  그러면 모두가 클론을 다시 받아야 하므로, 앞으로의 리뷰가 되는 것까지를 목표로 했습니다.
```

- [ ] **Step 2: `docs/setup-checklist.md`에 수동 검증 절을 더한다**

`### 0.5.14 — 변경 로그 정리 실패 안내` **바로 위**에 더한다.

```markdown
### 0.5.15 — 저장소 인코딩 전환

**이 작업의 목적 자체가 CI 밖에 있다.** 단위 테스트는 바이트만 보고, 정말 확인해야 할 것은
GitLab이 diff를 그리는지다.

- [ ] **배너.** 0.5.14 이하로 추출한 저장소에 연결한다 → **"저장소가 옛 인코딩(UTF-16)입니다"**
      안내와 **전환하기** 버튼이 뜬다.
- [ ] **경고 문구.** 버튼을 누른다 → 확인 상자에 **"팀에서 한 사람만"** 이 들어 있다.
- [ ] **취소.** 일단 취소한다 → 저장소 루트에 `.gitattributes`가 생기지 **않았다.**
- [ ] **전환.** 다시 눌러 진행한다 → 전체 다시 추출이 돌고, 끝나면
  - [ ] 저장소 루트에 `.gitattributes`가 생겼고 `*.sql -text` 가 들어 있다
  - [ ] 모든 객체가 `수정` 으로 뜬다
  - [ ] **배너가 스스로 사라졌다** (커밋하기 전에 사라진다. 이것이 전환 성공의 화면 신호다)
- [ ] **바이트 확인.** 아무 `.sql` 하나의 앞 3바이트가 `EF BB BF` 다.
  ```powershell
  Get-Content <저장소>\dbo\Tables\<아무거나>.sql -AsByteStream -TotalCount 3
  # 239 187 191 이 나오면 UTF-8 BOM 이다
  ```
- [ ] **한글이 살아 있다.** 그 `.sql` 을 SSMS로 열어 한국어 주석과 확장 속성이 깨지지 않았는지 본다.
- [ ] **커밋하고 Push한다.** 저장소 전체가 담긴 커밋 하나가 만들어진다.
- [ ] **여기가 이 작업의 목적이다 — GitLab에서 그 커밋을 연다.**
  - [ ] `.sql` 변경이 **"Binary file"이 아니라 실제 diff로** 보인다
  - [ ] 한국어가 깨지지 않고 보인다
- [ ] **다른 사람 쪽.** 다른 PC에서 0.5.15로 올리고 **Pull만** 한다 →
      배너가 뜨지 않고 변경 목록이 평소대로 동작한다.
- [ ] **배포·감사 클론.** 그 클론으로 연결한다 → **배너가 뜨지 않는다** (추출이 금지된 모드다).
      Pull로 전환된 커밋을 받으면 차이 검사가 평소대로 동작한다.
- [ ] **배포 스크립트의 BOM.** 배포·감사 대상에서 **배포 스크립트 저장...** 으로 파일을 만든 뒤
      SSMS로 연다 → 한국어 주석이 깨지지 않는다 (0.5.15에서 함께 고친 것이다).
```

- [ ] **Step 3: "알려진 제약"에 이력 관련 항목을 더한다**

`docs/setup-checklist.md`의 `## 알려진 제약` 목록에 더한다.

```markdown
- **전환 이전 커밋의 diff는 바이너리로 남는다.** 0.5.15가 인코딩을 UTF-8로 바꿨지만 과거
  커밋의 블롭은 그대로다. 이력을 다시 쓰면(`git filter-repo`) 고칠 수 있으나 저장소를 가진
  모든 사람이 클론을 다시 받아야 하므로 하지 않는다. 앞으로의 변경은 정상적으로 보인다.
```

- [ ] **Step 4: 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest` 4행의 `Version="0.5.14"` 를 `Version="0.5.15"` 로 바꾼다.

```bash
grep -n 'Version="0.5.15"' src/DBVC.Vsix/source.extension.vsixmanifest
```

- [ ] **Step 5: 최종 검증**

```bash
dotnet build DBVC.slnx
dotnet test DBVC.slnx -f net10.0
dotnet test DBVC.slnx -f net48
```

Expected: 셋 다 성공, 실패 0. `SmoManagerIntegrationTests`가 Skip되었는지 확인하고, 되었다면 보고한다.

- [ ] **Step 6: 커밋**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: 저장소 인코딩 전환 절차와 검증 항목을 적는다"
```

---

## 완료 조건

- [ ] `dotnet test DBVC.slnx -f net10.0` 실패 0
- [ ] `dotnet test DBVC.slnx -f net48` 실패 0
- [ ] `SmoManagerIntegrationTests`가 Skip되지 않고 실제로 통과했다 (SMO가 옵션을 지키는지 확인하는 유일한 자리)
- [ ] `docs/setup-checklist.md`의 0.5.15 절을 SSMS 21에서 눌러 확인했다 — **특히 GitLab에서 diff가 보이는지.** 이것을 하기 전에는 "동작한다"고 말하지 않는다
