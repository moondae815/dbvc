# DBVC 연결 정보 입력란 제거 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** DBVC Connect 패널의 입력란 다섯 개(`Server`·`Database`·인증 방식·`User`·`Password`)를 없애고, 접속 대상과 인증 정보가 오직 SSMS 개체 탐색기에서만 오도록 바꾼다. 그와 함께 디스크 자격증명 저장(`credentials.json` + DPAPI)을 폐지한다.

**Architecture:** 자격증명 저장소를 파일 기반(`SqlCredentialStore`)에서 프로세스 메모리 전용(`SessionCredentialStore`)으로 교체하고, `ISqlCredentialStore`를 `TryGet` + `Set` 두 멤버로 축소한다. `ViewChangesViewModel`은 입력란을 반영하던 가변 속성 대신 "마지막으로 채택한 대상"만 읽기 전용으로 노출하고, `Connect` 명령 하나가 개체 탐색기를 읽어 저장소에 넣고 접속까지 수행한다. `StateTracker`·`SmoManager`·`GitManager`는 인터페이스 뒤에 있으므로 바뀌지 않는다.

**Tech Stack:** C# (net48 + netstandard2.0 멀티타깃), WPF, NUnit, Moq, Microsoft.Data.SqlClient

## Global Constraints

- **설계 문서:** `docs/superpowers/specs/2026-08-06-dbvc-ssms-only-connection-design.md`. 이 계획과 어긋나면 설계 문서가 이긴다.
- **주석·문서·테스트 이름은 한국어**로 쓴다. 기존 코드베이스의 규약이다. 테스트 메서드 이름만 영어다.
- **`Microsoft.Data.SqlClient` 5.1.5와 `Microsoft.SqlServer.SqlManagementObjects` 171.30.0 버전을 건드리지 않는다.** `DBVC.Core.csproj`의 주석에 이유가 적혀 있다 (SSMS 21이 프로세스에 먼저 올리는 어셈블리에 맞춘 고정 버전).
- **평문 암호를 로그에 남기지 않는다.** 진단에는 `암호 실림=True/False`처럼 존재 여부만 적는다.
- **`SqlCredential`에 `ToString()`을 재정의하지 않는다.** 평문 암호가 로그로 새는 경로가 된다.
- 빌드·테스트 명령:
  ```bash
  dotnet build DBVC.slnx
  dotnet test tests/DBVC.Core.Tests
  dotnet test tests/DBVC.Vsix.Tests
  ```
- 각 태스크는 위 세 명령이 모두 통과하는 상태로 끝난다. Task 3과 Task 4는 중간에 컴파일이 깨지므로 태스크 안에서 끝까지 간다.

---

## File Structure

**생성**

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/SessionCredentialStore.cs` | (서버, DB)별 인증 정보를 프로세스 메모리에만 보관. 파일을 모른다 |
| `src/DBVC.Core/LegacyCredentialFile.cs` | 옛 `credentials.json`의 일회성 삭제. 유일하게 파일을 아는 곳 |
| `tests/DBVC.Core.Tests/SessionCredentialStoreTests.cs` | 위 저장소의 계약 + "디스크에 아무것도 남기지 않는다" 검증 |
| `tests/DBVC.Core.Tests/LegacyCredentialFileTests.cs` | 삭제·부재·실패 삼킴·디렉터리 보존 |

**삭제**

| 파일 | 이유 |
| --- | --- |
| `src/DBVC.Core/SqlCredentialStore.cs` | 디스크 보관이 유일한 책임이었다 |
| `src/DBVC.Core/DpapiPasswordProtector.cs` | 보호할 대상이 디스크에 없다 |
| `src/DBVC.Core/IPasswordProtector.cs` | 구현이 하나뿐이었고 그것이 사라진다 |
| `src/DBVC.Core/SessionPasswordCache.cs` | `SessionCredentialStore`에 흡수 |
| `src/DBVC.Core/Models/SqlCredentialSerializer.cs` | 직렬화 대상이 없다 |
| `tests/DBVC.Core.Tests/SqlCredentialStoreTests.cs` | 대상 클래스가 사라진다 |
| `tests/DBVC.Core.Tests/SessionPasswordCacheTests.cs` | 새 저장소 테스트로 흡수 |

**수정**

| 파일 | 변경 |
| --- | --- |
| `src/DBVC.Core/Abstractions.cs` | `ISqlCredentialStore`를 두 멤버로 축소 |
| `src/DBVC.Core/Models/SqlCredential.cs` | `ProtectedPassword` → `Password`(평문, 메모리 한정) |
| `src/DBVC.Core/SqlConnectionFactory.cs` | `ResolvePassword` 호출 제거, 예외 문구 교체 |
| `src/DBVC.Core/DBVC.Core.csproj` | `System.Security.Cryptography.ProtectedData` 참조 제거 |
| `src/DBVC.Vsix/DbvcServices.cs` | `SessionCredentialStore`로 교체, 공유 제약 주석 갱신 |
| `src/DBVC.Vsix/DbvcPackage.cs` | `LegacyCredentialFile.DeleteIfPresent()` 호출 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 연결 영역 전면 재작성 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 상단 입력란 → `Connect` + 대상 표시줄 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` | `OnSqlPasswordChanged` 제거, 가시성 핸들러 전환 |
| `tests/DBVC.Core.Tests/SqlConnectionFactoryTests.cs` | 메모리 저장소 기준 재작성 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 연결 관련 테스트 재작성 |
| `tests/DBVC.Vsix.Tests/PackageTests.cs` | 저장소 공유 테스트를 새 경로로 |
| `README.md`, `docs/setup-checklist.md` | 새 동작 반영 |

---

## Task 1: `SessionCredentialStore` — 메모리 전용 자격증명 저장소

기존 코드를 전혀 건드리지 않고 새 클래스만 더한다. 아직 아무도 쓰지 않으므로 이 태스크가 끝나도 동작은 그대로다. `ISqlCredentialStore`를 **구현하지 않는다** — 인터페이스가 아직 옛 형태(`Save`·`ResolvePassword`·`CanPersistPasswords` 등)라 구현할 수 없다. 인터페이스 선언은 Task 3에서 붙인다.

**Files:**
- Create: `src/DBVC.Core/SessionCredentialStore.cs`
- Modify: `src/DBVC.Core/Models/SqlCredential.cs`
- Test: `tests/DBVC.Core.Tests/SessionCredentialStoreTests.cs`

**Interfaces:**
- Consumes: `SqlAuthMode`, `SqlCredential` (`DBVC.Core.Models`)
- Produces:
  - `SqlCredential.Password { get; set; }` — `string?`, 평문. 기존 `ProtectedPassword`는 Task 3까지 함께 남는다
  - `SessionCredentialStore.TryGet(string serverName, string databaseName)` → `SqlCredential?`
  - `SessionCredentialStore.Set(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password)` → `void`

---

- [x] **Step 1: `SqlCredential`에 평문 `Password`를 더한다**

`src/DBVC.Core/Models/SqlCredential.cs`의 `ProtectedPassword` 속성 **아래에** 다음을 추가한다. 이 단계에서 `ProtectedPassword`를 지우지 않는다 — `SqlCredentialStore`가 아직 쓰고 있다.

```csharp
        /// <summary>
        /// 평문 암호. 이 프로세스가 사는 동안만 존재하며 디스크에 닿지 않는다.
        ///
        /// 값의 출처는 SSMS 개체 탐색기뿐이고, SSMS가 닫히면 함께 사라진다.
        /// 이 타입을 로그에 통째로 싣지 말 것 — 진단에는 존재 여부만 남긴다.
        /// </summary>
        public string? Password { get; set; }
```

- [x] **Step 2: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SessionCredentialStoreTests.cs`를 새로 만든다.

```csharp
using System;
using System.IO;
using DBVC.Core;
using DBVC.Core.Models;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SessionCredentialStoreTests
    {
        [Test]
        public void TryGet_ReturnsNull_ForAnUnknownTarget()
        {
            Assert.That(new SessionCredentialStore().TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Set_ThenTryGet_RoundTripsAllFourValues()
        {
            var store = new SessionCredentialStore();

            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            var credential = store.TryGet("srv", "db");
            Assert.That(credential, Is.Not.Null);
            Assert.That(credential!.ServerName, Is.EqualTo("srv"));
            Assert.That(credential.DatabaseName, Is.EqualTo("db"));
            Assert.That(credential.AuthMode, Is.EqualTo(SqlAuthMode.Sql));
            Assert.That(credential.UserName, Is.EqualTo("sa"));
            Assert.That(credential.Password, Is.EqualTo("p@ss"));
        }

        [Test]
        public void TryGet_IgnoresCase_InTheServerAndDatabaseNames()
        {
            var store = new SessionCredentialStore();
            store.Set("SRV", "DB", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(store.TryGet("srv", "db"), Is.Not.Null);
        }

        [Test]
        public void Set_OverwritesEveryValue_LeavingNothingFromThePreviousCall()
        {
            // Save(plainPassword: null)이 "저장된 암호를 그대로 둔다"였던 옛 계약을 물려받으면 안 된다.
            // 대상이 같아도 개체 탐색기가 준 값이 통째로 이긴다.
            var store = new SessionCredentialStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "old");

            store.Set("srv", "db", SqlAuthMode.Sql, "sa", null);

            Assert.That(store.TryGet("srv", "db")!.Password, Is.Null,
                "이전 호출의 암호가 남으면 사라진 계정의 암호로 접속을 시도하게 됩니다");
        }

        [Test]
        public void Set_DropsTheUserAndPassword_ForWindowsAuth()
        {
            var store = new SessionCredentialStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            store.Set("srv", "db", SqlAuthMode.Windows, "sa", "p@ss");

            var credential = store.TryGet("srv", "db")!;
            Assert.That(credential.UserName, Is.Null);
            Assert.That(credential.Password, Is.Null,
                "Windows 인증에는 암호가 필요 없고, 들고 있으면 언젠가 잘못된 대상에 쓰입니다");
        }

        [Test]
        public void Set_Throws_WhenTheServerOrDatabaseIsBlank()
        {
            var store = new SessionCredentialStore();

            Assert.Throws<ArgumentException>(() => store.Set(" ", "db", SqlAuthMode.Windows, null, null));
            Assert.Throws<ArgumentException>(() => store.Set("srv", " ", SqlAuthMode.Windows, null, null));
        }

        [Test]
        public void Set_WritesNothingToDisk()
        {
            // 이번 결정의 핵심 계약이다. 예전 계약("파일 내용에 암호가 없다")보다 강하다 —
            // 파일 자체가 생기지 않아야 한다.
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DBVC");
            var credentialFile = Path.Combine(appData, "credentials.json");
            var existedBefore = File.Exists(credentialFile);

            new SessionCredentialStore().Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(File.Exists(credentialFile), Is.EqualTo(existedBefore),
                "메모리 전용 저장소가 %APPDATA%에 파일을 만들거나 지워서는 안 됩니다");
        }
    }
}
```

- [x] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter FullyQualifiedName~SessionCredentialStoreTests`
Expected: 컴파일 실패 — `SessionCredentialStore` 형식을 찾을 수 없음

- [x] **Step 4: `SessionCredentialStore`를 구현한다**

`src/DBVC.Core/SessionCredentialStore.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Concurrent;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// (서버, 데이터베이스)별 SQL 접속 인증 정보를 이 프로세스가 사는 동안만 보관한다.
    ///
    /// 값의 출처는 SSMS 개체 탐색기뿐이므로 디스크에 남길 이유가 없다 — SSMS를 다시 열면
    /// 개체 탐색기에 다시 접속하게 되고, 그 순간 최신 값이 다시 들어온다.
    ///
    /// <b>이 클래스는 파일을 모른다.</b> 옛 credentials.json 정리는
    /// <see cref="LegacyCredentialFile"/>가 따로 맡는다. 여기에 파일 접근을 들이면
    /// "디스크에 아무것도 쓰지 않는다"는 계약을 단위 테스트로 증명할 수 없게 된다.
    /// </summary>
    public class SessionCredentialStore
    {
        private readonly ConcurrentDictionary<string, SqlCredential> _credentials =
            new ConcurrentDictionary<string, SqlCredential>(StringComparer.OrdinalIgnoreCase);

        public SqlCredential? TryGet(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }

            return _credentials.TryGetValue(GetKey(serverName, databaseName), out var credential)
                ? credential
                : null;
        }

        /// <summary>
        /// 이 대상의 인증 정보를 통째로 덮어쓴다.
        ///
        /// 옛 <c>Save</c>의 "<c>plainPassword == null</c>이면 저장된 암호를 그대로 둔다"는 병합
        /// 규칙을 물려받지 않는다. 그 규칙은 디스크에 이전 값이 있다는 전제 위에서만 뜻이 있었고,
        /// 지금은 개체 탐색기가 준 네 값이 언제나 최신이다.
        /// </summary>
        public void Set(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                throw new ArgumentException("ServerName cannot be null or whitespace.", nameof(serverName));
            }
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("DatabaseName cannot be null or whitespace.", nameof(databaseName));
            }

            _credentials[GetKey(serverName, databaseName)] = new SqlCredential
            {
                ServerName = serverName,
                DatabaseName = databaseName,
                AuthMode = authMode,
                // Windows 인증에는 둘 다 의미가 없다. 들고 있으면 인증 방식만 바뀐 뒤에도
                // 남아서 언젠가 잘못된 대상으로 나간다.
                UserName = authMode == SqlAuthMode.Sql ? userName : null,
                Password = authMode == SqlAuthMode.Sql ? password : null
            };
        }

        /// <summary>키 규약은 옛 파일 저장소와 같다. 대소문자를 무시한다.</summary>
        private static string GetKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
```

- [x] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter FullyQualifiedName~SessionCredentialStoreTests`
Expected: PASS (7개)

- [x] **Step 6: 솔루션 전체가 여전히 통과하는지 확인한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests && dotnet test tests/DBVC.Vsix.Tests`
Expected: 전부 PASS. 이 태스크는 기존 경로를 건드리지 않았다

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Core/SessionCredentialStore.cs src/DBVC.Core/Models/SqlCredential.cs tests/DBVC.Core.Tests/SessionCredentialStoreTests.cs
git commit -m "feat(core): 메모리 전용 자격증명 저장소를 더한다"
```

---

## Task 2: `LegacyCredentialFile` — 옛 credentials.json 정리

기존 사용자의 `%APPDATA%\DBVC\credentials.json`에는 DPAPI로 암호화된 암호가 남는다. 아무도 읽지 않게 되므로 지운다. 이 태스크도 새 클래스만 더하며, 호출 지점 연결은 Task 3에서 한다.

**Files:**
- Create: `src/DBVC.Core/LegacyCredentialFile.cs`
- Test: `tests/DBVC.Core.Tests/LegacyCredentialFileTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `LegacyCredentialFile.DefaultPath` → `string` (`%APPDATA%\DBVC\credentials.json`)
  - `LegacyCredentialFile.DeleteIfPresent(string? path = null)` → `void`, 예외를 던지지 않는다

---

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/LegacyCredentialFileTests.cs`를 새로 만든다.

```csharp
using System;
using System.IO;
using DBVC.Core;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class LegacyCredentialFileTests
    {
        private string _dir = null!;
        private string _file = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dbvc_legacy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "credentials.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                try { Directory.Delete(_dir, true); } catch { }
            }
        }

        [Test]
        public void DeleteIfPresent_RemovesTheFile()
        {
            File.WriteAllText(_file, "[]");

            LegacyCredentialFile.DeleteIfPresent(_file);

            Assert.That(File.Exists(_file), Is.False);
        }

        [Test]
        public void DeleteIfPresent_KeepsTheDirectory()
        {
            // 같은 폴더에 mappings.json이 산다. 폴더를 지우면 매핑이 함께 사라진다.
            File.WriteAllText(_file, "[]");
            var mappings = Path.Combine(_dir, "mappings.json");
            File.WriteAllText(mappings, "[]");

            LegacyCredentialFile.DeleteIfPresent(_file);

            Assert.That(Directory.Exists(_dir), Is.True);
            Assert.That(File.Exists(mappings), Is.True);
        }

        [Test]
        public void DeleteIfPresent_IsQuiet_WhenTheFileIsNotThere()
        {
            Assert.DoesNotThrow(() => LegacyCredentialFile.DeleteIfPresent(_file));
        }

        [Test]
        public void DeleteIfPresent_SwallowsFailures()
        {
            // 디렉터리를 경로로 주면 File.Delete가 던진다. 삭제 실패로 플러그인이 뜨지
            // 않는 것과 옛 파일이 남는 것은 비교할 문제가 아니다.
            Assert.DoesNotThrow(() => LegacyCredentialFile.DeleteIfPresent(_dir));
            Assert.That(Directory.Exists(_dir), Is.True);
        }

        [Test]
        public void DefaultPath_PointsAtTheOldCredentialFile()
        {
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DBVC",
                "credentials.json");

            Assert.That(LegacyCredentialFile.DefaultPath, Is.EqualTo(expected));
        }
    }
}
```

- [x] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter FullyQualifiedName~LegacyCredentialFileTests`
Expected: 컴파일 실패 — `LegacyCredentialFile` 형식을 찾을 수 없음

- [x] **Step 3: `LegacyCredentialFile`을 구현한다**

`src/DBVC.Core/LegacyCredentialFile.cs`를 새로 만든다. `public`인 이유는 호출자(`DbvcPackage`)가 다른 어셈블리에 있기 때문이다.

```csharp
using System;
using System.Diagnostics;
using System.IO;

namespace DBVC.Core
{
    /// <summary>
    /// DBVC가 예전에 남긴 <c>%APPDATA%\DBVC\credentials.json</c>을 지운다.
    ///
    /// 그 파일에는 DPAPI로 보호한 SQL 인증 암호가 들어 있었다. 이제 자격증명은 프로세스
    /// 메모리에만 두므로 아무도 읽지 않는 파일이 되는데, 읽히지 않는다고 남겨 두면
    /// "디스크에 자격증명을 남기지 않는다"는 결정과 어긋난 채 방치된다.
    ///
    /// 한 번 지우면 다시 생기지 않는다. 멱등이므로 여러 번 불려도 무해하다.
    /// </summary>
    public static class LegacyCredentialFile
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "credentials.json");

        /// <summary>
        /// 파일이 있으면 지운다. <b>예외를 던지지 않는다</b> — 이 정리에 실패하는 것과
        /// 확장이 뜨지 않는 것은 비교할 문제가 아니다.
        ///
        /// 디렉터리는 건드리지 않는다. 같은 폴더에 <c>mappings.json</c>이 있다.
        /// </summary>
        /// <param name="path">테스트용 경로 재정의. <c>null</c>이면 <see cref="DefaultPath"/>.</param>
        public static void DeleteIfPresent(string? path = null)
        {
            var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;

            try
            {
                if (!File.Exists(target))
                {
                    return;
                }

                File.Delete(target);
                Debug.WriteLine($"LegacyCredentialFile: '{target}'을(를) 지웠습니다.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LegacyCredentialFile: '{target}'을(를) 지우지 못했습니다: {ex.Message}");
            }
        }
    }
}
```

- [x] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter FullyQualifiedName~LegacyCredentialFileTests`
Expected: PASS (5개)

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/LegacyCredentialFile.cs tests/DBVC.Core.Tests/LegacyCredentialFileTests.cs
git commit -m "feat(core): 옛 credentials.json을 지우는 일회성 정리를 더한다"
```

---

## Task 3: 디스크 저장소 폐지 — 인터페이스 축소와 소비자 전환

여기서 컴파일이 한동안 깨진다. 인터페이스를 축소하는 순간 `SqlCredentialStore`·`SqlConnectionFactory`·`DbvcServices`·`ViewChangesViewModel`이 동시에 깨지므로, 이 태스크 안에서 전부 처리한다.

**이 태스크의 종료 상태는 "UI는 그대로인데 디스크 저장만 없어진 DBVC"다.** 입력란은 아직 남아 있고 정상 동작한다 — 사용자가 친 암호가 디스크 대신 메모리 저장소로 갈 뿐이다. 입력란 제거는 Task 4가 한다.

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs:23-54`
- Modify: `src/DBVC.Core/Models/SqlCredential.cs`
- Modify: `src/DBVC.Core/SessionCredentialStore.cs` (인터페이스 선언 추가)
- Modify: `src/DBVC.Core/SqlConnectionFactory.cs:16-52`
- Modify: `src/DBVC.Core/DBVC.Core.csproj:31-35`
- Modify: `src/DBVC.Vsix/DbvcServices.cs:31-67`
- Modify: `src/DBVC.Vsix/DbvcPackage.cs:28-36`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`PersistCredential`·`TraceSave`·`CanPersistPasswords`)
- Delete: `src/DBVC.Core/SqlCredentialStore.cs`, `src/DBVC.Core/DpapiPasswordProtector.cs`, `src/DBVC.Core/IPasswordProtector.cs`, `src/DBVC.Core/SessionPasswordCache.cs`, `src/DBVC.Core/Models/SqlCredentialSerializer.cs`
- Delete: `tests/DBVC.Core.Tests/SqlCredentialStoreTests.cs`, `tests/DBVC.Core.Tests/SessionPasswordCacheTests.cs`
- Test: `tests/DBVC.Core.Tests/SqlConnectionFactoryTests.cs` (재작성)
- Test: `tests/DBVC.Vsix.Tests/PackageTests.cs:90-117` (수정)

**Interfaces:**
- Consumes: `SessionCredentialStore.TryGet` / `.Set` (Task 1), `LegacyCredentialFile.DeleteIfPresent` (Task 2)
- Produces:
  - `ISqlCredentialStore` — `TryGet(string, string)` → `SqlCredential?`, `Set(string, string, SqlAuthMode, string?, string?)` → `void`. 다른 멤버는 없다
  - `SessionCredentialStore : ISqlCredentialStore`
  - `SqlCredential`에 `ProtectedPassword`가 없다. `Password`(평문)만 있다

---

- [x] **Step 1: 실패하는 테스트를 쓴다 — `SqlConnectionFactoryTests` 재작성**

`tests/DBVC.Core.Tests/SqlConnectionFactoryTests.cs`의 **전체 내용**을 아래로 교체한다. 옛 파일은 `SqlCredentialStore`와 `ReversibleProtector`(삭제될 `SqlCredentialStoreTests.cs`에 정의되어 있다)에 의존하므로 부분 수정으로는 컴파일되지 않는다.

```csharp
using DBVC.Core;
using DBVC.Core.Models;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SqlConnectionFactoryTests
    {
        private static SessionCredentialStore NewStore() => new SessionCredentialStore();

        [Test]
        public void Build_UsesWindowsAuth_WhenNoCredentialIsStored()
        {
            // 정상 흐름에서는 Connect가 항상 Set을 부르므로 이 갈래에 닿지 않는다.
            // 남겨 두는 것은 방어다 — 통합 인증으로 한 번 시도하는 편이 예외로 죽는 것보다 낫다.
            var connectionString = new SqlConnectionFactory(NewStore()).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("Integrated Security=True"));
            Assert.That(connectionString, Does.Contain("srv"));
            Assert.That(connectionString, Does.Contain("db"));
        }

        [Test]
        public void Build_UsesWindowsAuth_WhenTheStoredModeIsWindows()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Windows, null, null);

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_UsesTheStoredUserAndPassword_ForSqlAuth()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("User ID=sa"));
            Assert.That(connectionString, Does.Contain("p@ss"));
            Assert.That(connectionString, Does.Not.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_Throws_WhenSqlAuthHasNoPassword()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", null);

            var factory = new SqlConnectionFactory(store);

            var ex = Assert.Throws<SqlCredentialException>(() => factory.Build("srv", "db"));
            Assert.That(ex!.Message, Does.Contain("SQL 인증"),
                "영문 원문 대신 한국어 안내가 나와야 합니다");
        }

        [Test]
        public void Build_Throws_WhenSqlAuthHasNoUserName()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, null, "p@ss");

            Assert.Throws<SqlCredentialException>(() => new SqlConnectionFactory(store).Build("srv", "db"));
        }

        [Test]
        public void Build_PointsAtObjectExplorer_WhenSqlAuthHasNoPassword()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", null);

            var ex = Assert.Throws<SqlCredentialException>(() => new SqlConnectionFactory(store).Build("srv", "db"));

            Assert.That(ex!.Message, Does.Contain("개체 탐색기"),
                "이제 인증 정보를 얻는 길이 개체 탐색기뿐이므로 안내가 그리로 보내야 합니다");
            Assert.That(ex.Message, Does.Not.Contain("Windows 계정"),
                "DPAPI가 사라졌으므로 '저장한 Windows 계정에서만 복호화된다'는 안내는 거짓입니다");
        }

        [Test]
        public void BuildSql_DoesNotPersistSecurityInfo()
        {
            // 연결 후 ConnectionString 속성에서 암호가 다시 읽히면 로그·예외 메시지로 샐 수 있다.
            var connectionString = SqlConnectionFactory.BuildSql("srv", "db", "sa", "p@ss");

            Assert.That(connectionString, Does.Not.Contain("Persist Security Info=True"));
        }
    }
}
```

- [x] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 컴파일 실패 — `SessionCredentialStore`를 `SqlConnectionFactory` 생성자에 넘길 수 없음 (`ISqlCredentialStore`를 구현하지 않는다)

- [x] **Step 3: `ISqlCredentialStore`를 축소한다**

`src/DBVC.Core/Abstractions.cs`의 `ISqlCredentialStore` 블록(19~54행) **전체**를 아래로 교체한다.

```csharp
    /// <summary>
    /// (서버, 데이터베이스)별 SQL 접속 인증 정보를 이 프로세스가 사는 동안만 보관한다.
    ///
    /// 디스크에 쓰지 않는다 — 값의 출처는 SSMS 개체 탐색기뿐이고, SSMS가 닫히면 함께 사라진다.
    /// 매핑(<see cref="IConfigManager"/>)과는 수명도 저장 매체도 다르므로 분리되어 있다.
    /// </summary>
    public interface ISqlCredentialStore
    {
        SqlCredential? TryGet(string serverName, string databaseName);

        /// <summary>
        /// 이 대상의 인증 정보를 통째로 덮어쓴다. 이전 값과 병합하지 않는다.
        /// </summary>
        void Set(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password);
    }
```

- [x] **Step 4: `SessionCredentialStore`가 인터페이스를 구현하게 한다**

`src/DBVC.Core/SessionCredentialStore.cs`의 클래스 선언 한 줄을 고친다.

```csharp
    public class SessionCredentialStore : ISqlCredentialStore
```

- [x] **Step 5: 옛 자격증명 계층을 삭제한다**

```bash
git rm src/DBVC.Core/SqlCredentialStore.cs \
       src/DBVC.Core/DpapiPasswordProtector.cs \
       src/DBVC.Core/IPasswordProtector.cs \
       src/DBVC.Core/SessionPasswordCache.cs \
       src/DBVC.Core/Models/SqlCredentialSerializer.cs \
       tests/DBVC.Core.Tests/SqlCredentialStoreTests.cs \
       tests/DBVC.Core.Tests/SessionPasswordCacheTests.cs
```

- [x] **Step 6: `SqlCredential`에서 `ProtectedPassword`를 지운다**

`src/DBVC.Core/Models/SqlCredential.cs`에서 `ProtectedPassword` 속성과 그 XML 주석을 삭제하고, 클래스 XML 주석(15~20행)을 아래로 교체한다.

```csharp
    /// <summary>
    /// 한 (서버, 데이터베이스)에 접속할 때 쓸 인증 정보.
    ///
    /// <see cref="Password"/>는 평문이며 이 프로세스 안에서만 산다 — 디스크에 닿는 경로가 없다.
    /// 이 타입을 로그나 예외 메시지에 통째로 싣지 말 것. <c>ToString()</c>을 재정의하지 않는 것도
    /// 같은 이유다.
    /// </summary>
```

- [x] **Step 7: `SqlConnectionFactory`를 전환한다**

`src/DBVC.Core/SqlConnectionFactory.cs`의 생성자와 `Build`를 아래로 교체한다 (`BuildWindows`·`BuildSql`은 그대로 둔다).

```csharp
        public SqlConnectionFactory(ISqlCredentialStore? credentialStore = null)
        {
            _credentialStore = credentialStore ?? new SessionCredentialStore();
        }

        /// <summary>
        /// 보관된 인증 정보로 연결 문자열을 만든다.
        /// 인증 정보가 없으면 Windows 통합 인증으로 간주한다 — 정상 흐름에서는 Connect가 항상
        /// 인증 정보를 넣으므로 닿지 않는 갈래이고, 남겨 두는 것은 방어다.
        /// </summary>
        /// <exception cref="SqlCredentialException">
        /// SQL 인증으로 설정되어 있으나 계정명이나 암호가 없는 경우.
        /// </exception>
        public string Build(string serverName, string databaseName)
        {
            var credential = _credentialStore.TryGet(serverName, databaseName);

            if (credential == null || credential.AuthMode != SqlAuthMode.Sql)
            {
                return BuildWindows(serverName, databaseName);
            }

            if (string.IsNullOrEmpty(credential.UserName) || string.IsNullOrEmpty(credential.Password))
            {
                throw new SqlCredentialException(
                    $"'{serverName}.{databaseName}'은(는) SQL 인증으로 설정되어 있으나 암호를 사용할 수 없습니다. " +
                    "SSMS 개체 탐색기에서 이 데이터베이스에 접속한 뒤 DBVC 창에서 Connect를 누르세요. " +
                    "(인증 정보는 SSMS를 닫으면 사라지므로 재시작 후에는 다시 눌러야 합니다.)");
            }

            return BuildSql(serverName, databaseName, credential.UserName!, credential.Password!);
        }
```

- [x] **Step 8: `DBVC.Core.csproj`에서 DPAPI 참조를 지운다**

31~35행의 주석 블록과 `System.Security.Cryptography.ProtectedData` `PackageReference` 한 줄을 함께 삭제한다. `System.Text.Json`은 **남긴다** — `MappingConfigSerializer`가 쓴다.

- [x] **Step 9: `DbvcServices`를 전환한다**

`src/DBVC.Vsix/DbvcServices.cs`에서 `new SqlCredentialStore()` 두 곳(42행, 66행)을 `new SessionCredentialStore()`로 바꾸고, 31~38행의 XML 주석을 아래로 교체한다.

```csharp
        /// <summary>
        /// 하나의 <see cref="ConfigManager"/>와 <see cref="SessionCredentialStore"/>를 모든 매니저가
        /// 공유하도록 구성한다.
        ///
        /// 인증 저장소를 공유하지 않으면 다른 인스턴스에는 인증 정보가 <b>아예 없다</b> —
        /// 디스크 파일이 있던 시절에는 각자 같은 파일을 읽어 최악의 경우 값이 오래된 정도였지만,
        /// 이제는 메모리뿐이다. ViewModel이 Connect에서 넣은 암호를 StateTracker가 보지 못하면
        /// SQL 인증 접속이 Windows 인증으로 흘러가 실패한다.
        /// </summary>
```

- [x] **Step 10: `DbvcPackage`가 옛 파일을 지우게 한다**

`src/DBVC.Vsix/DbvcPackage.cs`의 `InitializeAsync`에서 `base.InitializeAsync` 호출 **뒤에** 다음을 넣고, 파일 상단에 `using DBVC.Core;`를 더한다.

```csharp
            // 자격증명을 디스크에 두지 않기로 했으므로, 예전 버전이 남긴 파일을 지운다.
            // DbvcServices가 아니라 여기인 이유: 그 클래스는 셸 없이 단위 테스트에서 그대로
            // 생성되므로, 거기에 두면 테스트를 돌릴 때마다 개발자의 실제 파일이 사라진다.
            LegacyCredentialFile.DeleteIfPresent();
```

- [x] **Step 11: `ViewChangesViewModel`을 새 저장소 계약에 맞춘다**

이 단계는 **최소 변경**이다. 입력란과 그 상태(`Password`·`PasswordFromSsms`·`AuthMode` 등)는 그대로 두고, 저장소를 부르는 부분만 고친다.

1. `CanPersistPasswords` 속성(279행 부근)을 삭제한다.
2. `TraceSave` 메서드(510~528행) 전체를 삭제한다.
3. `PersistCredential()`(533~574행) 전체를 아래로 교체한다.

```csharp
        /// <summary>
        /// 입력·수집된 인증 정보를 저장소에 반영한다.
        ///
        /// 반환값이 없어졌다. 디스크 쓰기가 사라지면서 "저장에 실패해 접속할 수 없다"는 상태
        /// 자체가 없어졌기 때문이다 — 메모리 사전 대입은 실패하지 않는다.
        /// </summary>
        private void PersistCredential()
        {
            try
            {
                _credentialStore.Set(ServerName!, DatabaseName!, AuthMode, UserName, _password);
                SsmsDiagnostics.Trace(
                    $"인증 정보 반영: {ServerName}.{DatabaseName} {AuthMode} 인증, " +
                    $"계정={UserName ?? "(없음)"}, 암호 실림={!string.IsNullOrEmpty(_password)}");
            }
            finally
            {
                // 평문을 ViewModel이 세션 내내 들고 있을 이유가 없다. 저장소가 들고 있다.
                _password = null;
                PasswordFromSsms = false;
                ConnectionSourceMessage = null;
            }
        }
```

4. `SetContext`(476~479행)의 호출부를 고친다.

```csharp
            PersistCredential();
```

(`if (!PersistCredential()) { return; }` 세 줄을 위 한 줄로 바꾼다.)

- [x] **Step 12: `PackageTests`의 저장소 공유 테스트를 고친다**

`tests/DBVC.Vsix.Tests/PackageTests.cs`의 `Services_ShareTheSameCredentialStoreInstance`(90~111행)를 아래로 교체한다. Task 4에서 이 테스트를 한 번 더 고치게 된다 — 그때는 `vm.AuthMode`·`vm.Password`·`vm.SetContext`가 사라지기 때문이다. 지금 최종형으로 건너뛰지 않는 이유는 이 태스크가 그 자체로 컴파일되고 통과해야 하기 때문이다.

```csharp
        [Test]
        public void Services_ShareTheSameCredentialStoreInstance()
        {
            // ConfigManager와 같은 이유이고, 이제는 더 엄격하다. 각자 인스턴스를 만들면
            // 다른 쪽에는 인증 정보가 아예 없다 — 디스크 파일이라는 공통 근거가 사라졌기 때문이다.
            var credentials = new Mock<ISqlCredentialStore>();

            var services = new DbvcServices(NewIsolatedConfig(), credentials.Object);
            var vm = services.CreateViewChangesViewModel();

            vm.AuthMode = SqlAuthMode.Sql;
            vm.UserName = "sa";
            vm.Password = "p@ss";
            vm.SetContext("S", "DB");

            Assert.That(services.CredentialStore, Is.SameAs(credentials.Object));
            credentials.Verify(c => c.Set("S", "DB", SqlAuthMode.Sql, "sa", "p@ss"), Times.Once,
                "ViewModel이 컨테이너의 인증 저장소를 그대로 써야 합니다");
        }
```

- [x] **Step 13: `ViewChangesViewModelTests`의 저장소 목 설정을 고친다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 `SetUp`(74~78행)에서 아래 세 줄을 삭제한다. `Set`은 `void`라 Moq 설정이 필요 없다.

```csharp
            _credentials.Setup(c => c.CanPersistPasswords).Returns(true);
            _credentials.Setup(c => c.Save(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SqlAuthMode>(),
                It.IsAny<string>(), It.IsAny<string>())).Returns(true);
```

(`_credentials = new Mock<ISqlCredentialStore>();` 한 줄은 남긴다.)

이어서 사라진 멤버(`Save`·`ResolvePassword`·`SetSessionPassword`·`CanPersistPasswords`·`ProtectedPassword`)에 의존하는 테스트들을 삭제한다. 이들이 검증하던 동작(디스크 저장, DPAPI 실패 경고, 세션 암호와 디스크 암호의 우선순위)은 더 이상 존재하지 않는다.

먼저 대상을 기계적으로 찾는다.

Run: `grep -n "\.Save(\|ResolvePassword\|SetSessionPassword\|CanPersistPasswords\|ProtectedPassword" tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

확인된 대상은 아래와 같다. **grep 결과가 이 목록보다 많으면 그쪽이 옳다** — 나온 줄이 속한 `[Test]` 메서드를 통째로 지운다. 어느 것도 Task 4에서 다시 필요하지 않다.

- `SetContext_SavesTheEnteredCredential` (209행)
- `SetContext_ClearsThePlainTextPassword_AfterSaving` (222행)
- `SetContext_WarnsAndStops_WhenThePasswordCannotBePersisted` (251행)
- `SettingTheTarget_RestoresTheStoredSqlAuth_SoConnectDoesNotOverwriteIt` (269행 — `ProtectedPassword = "protected"` 포함)
- `Connect_KeepsTheSsmsPasswordInMemoryOnly` (1353행)
- `Connect_StillPersistsAPasswordTypedByTheUser` (1370행)
- `Connect_FallsBackToTheStoredPassword_WhenSsmsHasNone` (1387행)
- `Connect_DoesNotReuseTheSsmsPassword_AfterSwitchingToWindowsAuth` (1462행)
- `Connect_DoesNotReuseTheSsmsPassword_AfterTheUserChangesTheUserName` (1481행)

`LoadSavedCredential_RestoresTheStoredAuthModeAndUserName`(299행)은 사라진 멤버를 쓰지 않으므로 이 태스크에서는 **남긴다.** Task 4가 지운다.

- [x] **Step 14: 빌드하고 전체 테스트를 돌린다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests && dotnet test tests/DBVC.Vsix.Tests`
Expected: 전부 PASS. 컴파일 오류가 남아 있으면 그 파일이 아직 옛 멤버(`Save`·`ResolvePassword`·`SetSessionPassword`·`CanPersistPasswords`·`FilePath`·`LastSaveError`·`ProtectedPassword`)를 참조하는 것이다 — 위 단계들에 빠진 참조가 있는지 확인하고 같은 방식으로 고친다

- [x] **Step 15: 옛 이름이 정말 사라졌는지 확인한다**

Run: `grep -rn "ProtectedPassword\|IPasswordProtector\|SessionPasswordCache\|SqlCredentialStore\|CanPersistPasswords\|SetSessionPassword\|ResolvePassword" src/ tests/`
Expected: 결과 없음 (설계·계획 문서의 언급은 `docs/` 아래이므로 검색 범위에 들지 않는다)

- [x] **Step 16: 커밋**

```bash
git add -A
git commit -m "refactor(core): 자격증명을 디스크에서 걷어내고 메모리 전용으로 바꾼다"
```

---

## Task 4: 입력란 제거 — ViewModel과 화면 재작성

여기서 UI가 바뀐다. XAML 바인딩은 컴파일 오류를 내지 않고 조용히 실패하므로, ViewModel과 XAML을 **같은 태스크 안에서** 함께 고친다. (코드 비하인드의 `OnSqlPasswordChanged`는 `vm.Password`를 참조하므로 컴파일 오류로 잡힌다.)

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml:18-86`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs:91-128`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`
- Test: `tests/DBVC.Vsix.Tests/PackageTests.cs`

**Interfaces:**
- Consumes: `ISsmsConnectionSource.TryGetCurrent()` → `SsmsConnectionInfo?`; `SsmsConnectionInfo(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password, string? unsupportedReason)`; `ISqlCredentialStore.Set(...)` (Task 3)
- Produces (`ViewChangesViewModel`의 새 연결 표면):
  - `string? ServerName { get; }` / `string? DatabaseName { get; }` / `SqlAuthMode AuthMode { get; }` / `string? UserName { get; }`
  - `string TargetSummary { get; }`
  - `ICommand ConnectCommand { get; }`
  - `string? SsmsHintMessage { get; }` / `bool HasSsmsHintMessage { get; }`
  - `void CheckSsmsSelection()`
  - **없어지는 것:** `Password`, `HasSsmsPassword`, `IsSqlAuth`, `AuthModes`, `AuthModeOption`, `LoadSavedCredential`, `RefreshFromSsmsCommand`, `TryFillFromSsms`, `ConnectionSourceMessage`, `HasConnectionSourceMessage`, `SetContext`(public)

---

- [x] **Step 1: 실패하는 테스트를 쓴다 — 테스트 헬퍼부터 바꾼다**

`ViewChangesViewModelTests.cs`의 `NewConnectedViewModel`(92~97행)을 아래로 교체하고, 바로 아래에 `Info` 헬퍼를 더한다. 53곳의 `NewConnectedViewModel()` 호출부는 그대로 둔다 — 이제 실제 앱과 같은 경로(개체 탐색기 → Connect)로 접속한다.

```csharp
        /// <summary>
        /// 개체 탐색기가 Server/Database를 내주는 상태로 만들고 Connect를 누른다.
        /// 실제 앱에 남은 유일한 접속 경로다.
        /// </summary>
        private ViewChangesViewModel NewConnectedViewModel()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);
            return vm;
        }

        private static SsmsConnectionInfo Info(
            string server = Server,
            string database = Database,
            SqlAuthMode authMode = SqlAuthMode.Windows,
            string? userName = null,
            string? password = null,
            string? unsupportedReason = null)
            => new SsmsConnectionInfo(server, database, authMode, userName, password, unsupportedReason);
```

- [x] **Step 2: 실패하는 테스트를 쓴다 — 연결 영역 테스트를 교체한다**

`// ---------- 인증 ----------`(187행)부터 `// ---------- Setup ----------`(345행) **직전**까지를 아래로 교체한다.

```csharp
        // ---------- 연결 ----------

        [Test]
        public void TargetSummary_SaysNotConnected_BeforeAnyConnect()
        {
            Assert.That(NewViewModel().TargetSummary, Is.EqualTo("(접속되지 않음)"));
        }

        [Test]
        public void TargetSummary_ShowsTheWindowsAuthTarget()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.TargetSummary, Is.EqualTo($"{Server}.{Database} — Windows 인증"));
        }

        [Test]
        public void TargetSummary_ShowsTheSqlAuthAccount()
        {
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(authMode: SqlAuthMode.Sql, userName: "sa", password: "p@ss"));

            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.TargetSummary, Is.EqualTo($"{Server}.{Database} — SQL 인증 (sa)"));
        }

        [Test]
        public void ConnectCommand_AdoptsTheObjectExplorerTarget()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.ServerName, Is.EqualTo(Server));
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
            Assert.That(vm.IsMapped, Is.True);
            _stateTracker.Verify(s => s.IsInitialized(Server, Database), Times.Once);
        }

        [Test]
        public void ConnectCommand_StoresTheCredentialFromObjectExplorer()
        {
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(authMode: SqlAuthMode.Sql, userName: "sa", password: "p@ss"));

            NewViewModel().ConnectCommand.Execute(null);

            _credentials.Verify(c => c.Set(Server, Database, SqlAuthMode.Sql, "sa", "p@ss"), Times.Once);
        }

        [Test]
        public void ConnectCommand_CanExecute_OnlyWhenAConnectionSourceIsWired()
        {
            Assert.That(NewViewModel().ConnectCommand.CanExecute(null), Is.True);

            var withoutSource = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _folderDialog, _credentials.Object, null);

            Assert.That(withoutSource.ConnectCommand.CanExecute(null), Is.False,
                "개체 탐색기를 읽을 수 없으면 누를 수 있는 것이 아무것도 없습니다");
        }

        [Test]
        public void ConnectCommand_ExplainsWhatToSelect_WhenTheSelectionCannotBeRead()
        {
            // 기본값: _ssms가 null을 돌려준다
            var vm = NewViewModel();

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.WarningMessage, Does.Contain("개체 탐색기"));
            Assert.That(vm.ServerName, Is.Null);
            _stateTracker.Verify(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "대상을 모르는 채로 접속을 시도할 수는 없습니다");
        }

        [Test]
        public void ConnectCommand_KeepsTheCurrentTarget_WhenTheSelectionCannotBeRead()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns((SsmsConnectionInfo?)null);

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.ServerName, Is.EqualTo(Server),
                "읽지 못했다는 사실이 이미 잡아 둔 대상을 거짓으로 만들지는 않습니다");
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
        }

        [Test]
        public void ConnectCommand_ShowsTheReason_AndDoesNotConnect_WhenTheConnectionIsUnsupported()
        {
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(unsupportedReason: "Microsoft Entra ID 연결은 그대로 재사용할 수 없습니다."));

            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.ServerName, Is.EqualTo(Server), "서버·DB는 알 수 있으므로 표시한다");
            Assert.That(vm.WarningMessage, Does.Contain("Entra"));
            _stateTracker.Verify(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "실패가 확정된 접속을 시도해 낮은 수준 오류를 흘리면 안 됩니다");
            _credentials.Verify(
                c => c.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SqlAuthMode>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void ConnectCommand_ShowsTheConnectionError_AndDoesNotClaimInitialized()
        {
            _stateTracker.Setup(s => s.TestConnection(Server, Database))
                .Returns("'LocalServer'에 로그인하지 못했습니다. 사용자명과 암호를 확인하세요.");

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsInitialized, Is.False);
            Assert.That(vm.WarningMessage, Does.Contain("로그인하지 못했습니다"),
                "접속 실패를 '초기화되지 않음'으로 뭉개면 원인을 알 수 없습니다");
            _stateTracker.Verify(s => s.IsInitialized(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "접속도 안 되는 상태에서 초기화 여부를 물을 이유가 없습니다");
        }

        [Test]
        public void ConnectCommand_ClearsTheSelection_WhenTheTargetMoves()
        {
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.SelectedChange, Is.Null,
                "A의 변경 목록이 남아 있으면 커밋이 B로 나갑니다");
            Assert.That(vm.Changes, Is.Empty);
        }

        // ---------- 개체 탐색기 선택 대조 ----------

        [Test]
        public void CheckSsmsSelection_TellsWhatToSelect_WhenNothingIsConnectedAndNothingIsSelected()
        {
            var vm = NewViewModel();

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Does.Contain("선택"),
                "입력란이 없어졌으므로 이 한 줄이 유일한 길잡이입니다");
        }

        [Test]
        public void CheckSsmsSelection_PreviewsTheTarget_BeforeTheFirstConnect()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            var vm = NewViewModel();

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Does.Contain(Server));
            Assert.That(vm.SsmsHintMessage, Does.Contain("Connect"));
        }

        [Test]
        public void CheckSsmsSelection_PointsAtTheNewTarget_WhenTheSelectionMoved()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Does.Contain("OtherServer"));
            Assert.That(vm.HasSsmsHintMessage, Is.True);
        }

        [Test]
        public void CheckSsmsSelection_DoesNotTouchTheTarget()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));

            vm.CheckSsmsSelection();

            Assert.That(vm.ServerName, Is.EqualTo(Server),
                "지나가던 마우스가 대상을 바꾸면 버튼을 유지하기로 한 결정이 무의미해집니다");
        }

        [Test]
        public void CheckSsmsSelection_SaysNothing_WhenTheSelectionStillMatches()
        {
            var vm = NewConnectedViewModel();

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Is.Null);
        }

        [Test]
        public void CheckSsmsSelection_SaysNothing_WhenConnectedAndTheSelectionIsNotUsable()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns((SsmsConnectionInfo?)null);

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Is.Null,
                "개체 탐색기에서 잠깐 다른 노드를 클릭했다고 배너가 뜨면 진짜 경고까지 묻힙니다");
        }

        [Test]
        public void ConnectCommand_ClearsTheHint_WhenItAdoptsTheSelection()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));
            vm.CheckSsmsSelection();
            Assert.That(vm.HasSsmsHintMessage, Is.True);

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.SsmsHintMessage, Is.Null,
                "방금 누른 버튼이 배너를 남긴 것처럼 보이면 안 됩니다");
        }
```

- [x] **Step 3: 남은 옛 테스트를 지운다**

같은 파일에서 아래 테스트들을 삭제한다. 검증 대상(입력란, 암호 출처 추적, 자동 채움)이 사라졌다.

- `SetContext_ClearsTheSelection` (698행) — Step 2의 `ConnectCommand_ClearsTheSelection_WhenTheTargetMoves`가 대체한다
- `LoadSavedCredential_RestoresTheStoredAuthModeAndUserName`
- `TryFillFromSsms_*` 전부 — `FillsTheTargetAndCredentialFields`, `KeepsTheSsmsCredential_WhenTheStoreHasAnOlderOne`, `ChangesNothing_WhenThereIsNoConnection`, `WarnsAndLeavesAuthAlone_WhenTheConnectionIsUnsupported`, `IsSkipped_WhileTheUserIsTypingAPassword`, `DropsTheSsmsPassword_WhenRetargetedToAnUnsupportedConnection`, `DropsTheSsmsPassword_WhenTheUserRetargetsTheServer`, `DropsTheSsmsPassword_WhenTheUserRetargetsTheDatabase`, `ClearsTheHint_WhenItActuallyFills`, `ExplainsWhy_WhenATypedPasswordBlocksIt`
- `HasSsmsPassword_*` 전부 — `IsTrue_WhileTheSsmsPasswordIsHeld`, `IsFalse_WhenTheSsmsConnectionHadNoPassword`, `GoesFalseAndNotifies_WhenTheUserTypesAPassword`, `IsFalse_AfterConnect`
- `CheckSsmsSelection_SaysNothing_BeforeAnyFillHasSucceeded` — 그 전제(`_ssmsFillEverSucceeded`)가 사라졌고, Step 2의 `PreviewsTheTarget_BeforeTheFirstConnect`가 반대 동작을 요구한다
- 옛 `CheckSsmsSelection_*` 나머지 — Step 2가 같은 이름으로 다시 정의하므로 중복을 남기지 말 것

- [x] **Step 4: `PackageTests`의 저장소 공유 테스트를 새 경로로 옮긴다**

`Services_ShareTheSameCredentialStoreInstance`(Task 3 Step 12에서 고친 것)를 다시 아래로 교체한다. `vm.AuthMode`·`vm.Password`·`vm.SetContext`가 모두 사라졌으므로 가짜 연결 소스를 주입해 Connect를 누른다.

```csharp
        [Test]
        public void Services_ShareTheSameCredentialStoreInstance()
        {
            // ConfigManager와 같은 이유이고, 이제는 더 엄격하다. 각자 인스턴스를 만들면
            // 다른 쪽에는 인증 정보가 아예 없다 — 디스크 파일이라는 공통 근거가 사라졌기 때문이다.
            var credentials = new Mock<ISqlCredentialStore>();
            var ssms = new Mock<ISsmsConnectionSource>();
            ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S", "DB", SqlAuthMode.Sql, "sa", "p@ss", null));

            var services = new DbvcServices(NewIsolatedConfig(), credentials.Object);
            var vm = services.CreateViewChangesViewModel(null, ssms.Object);

            vm.ConnectCommand.Execute(null);

            Assert.That(services.CredentialStore, Is.SameAs(credentials.Object));
            credentials.Verify(c => c.Set("S", "DB", SqlAuthMode.Sql, "sa", "p@ss"), Times.Once,
                "ViewModel이 컨테이너의 인증 저장소를 그대로 써야 합니다");
        }
```

파일 상단에 `using DBVC.Vsix.Services;`를 더한다.

- [x] **Step 5: 테스트가 실패하는지 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 컴파일 실패 — `TargetSummary`가 없음, `ViewChangesViewModel` 생성자에 `null` 소스를 넘기는 오버로드 확인 필요 등

- [x] **Step 6: ViewModel의 연결 컨텍스트 영역을 재작성한다**

`ViewChangesViewModel.cs`의 `// ---------- 연결 컨텍스트 ----------`(88행)부터 `LoadSavedCredential()` 끝(387행)까지를 아래로 교체한다. `InvalidateActiveContext()`는 **그대로 유지한다** (124~148행의 내용과 주석을 그대로 옮긴다).

```csharp
        // ---------- 연결 컨텍스트 ----------

        /// <summary>Connect가 마지막으로 채택한 대상. 입력란이 없으므로 setter는 닫혀 있다.</summary>
        public string? ServerName { get; private set; }

        public string? DatabaseName { get; private set; }

        /// <summary>표시용. 값은 개체 탐색기가 정한다.</summary>
        public SqlAuthMode AuthMode { get; private set; } = SqlAuthMode.Windows;

        /// <summary>표시용. <see cref="SqlAuthMode.Sql"/>일 때만 의미가 있다.</summary>
        public string? UserName { get; private set; }

        private bool HasContext => !string.IsNullOrWhiteSpace(ServerName) && !string.IsNullOrWhiteSpace(DatabaseName);

        /// <summary>
        /// 화면 맨 위에 한 줄로 뜨는 대상 표시.
        ///
        /// "Connect가 마지막으로 채택한 대상"을 말할 뿐 접속 성공 여부는 말하지 않는다 —
        /// 실패는 경고 배너가, 접속되었다는 사실은 변경 목록이 이미 말하고 있어서,
        /// 여기서 같은 것을 반복하면 세 곳을 동시에 맞춰야 한다.
        /// </summary>
        public string TargetSummary
        {
            get
            {
                if (!HasContext)
                {
                    return "(접속되지 않음)";
                }

                var auth = AuthMode == SqlAuthMode.Sql
                    ? $"SQL 인증 ({UserName ?? "계정 미상"})"
                    : "Windows 인증";
                return $"{ServerName}.{DatabaseName} — {auth}";
            }
        }

        /// <summary>
        /// 대상과 인증 정보를 통째로 갈아 끼운다. 네 값은 언제나 함께 온다 —
        /// 개체 탐색기가 하나의 연결에서 읽어 오기 때문이다.
        /// </summary>
        private void SetTarget(string serverName, string databaseName, SqlAuthMode authMode, string? userName)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            AuthMode = authMode;
            UserName = userName;

            // 대상이 바뀌면 화면이 설명하던 것이 통째로 무효가 된다. 같은 대상으로 다시
            // 누른 경우에도 무효화한다 — 그것은 "지금 상태를 다시 판정해 달라"는 뜻이다.
            InvalidateActiveContext();

            OnPropertyChanged(nameof(ServerName));
            OnPropertyChanged(nameof(DatabaseName));
            OnPropertyChanged(nameof(AuthMode));
            OnPropertyChanged(nameof(UserName));
            OnPropertyChanged(nameof(TargetSummary));
            RaiseActionCanExecuteChanged();
        }

        /// <summary>
        /// 화면이 지금 무엇을 설명하는지를 지운다 — 대상이 바뀔 때 부른다.
        ///
        /// <see cref="Changes"/>·<see cref="IsMapped"/>·<see cref="IsInitialized"/>는 모두 특정
        /// (서버, 데이터베이스) 하나만을 설명하는 값이다. 대상이 바뀌는 순간 이 값들은 더 이상
        /// 화면에 보이는 대상을 가리키지 않는데, 그 사실을 즉시 반영하지 않으면
        /// <see cref="CanCommit"/>은 여전히 참을 반환한다 — A/db1의 변경 목록이 B/db2의 변경
        /// 로그에 처리 완료로 기록되어 버린다.
        /// </summary>
        private void InvalidateActiveContext()
        {
            Changes.Clear();
            SelectedChange = null;
            _lastChangeRecords = new List<ChangeRecord>();
            _failedCleanupPaths.Clear();
            IsMapped = false;
            IsInitialized = false;
            WarningMessage = null;
            // 대상이 바뀌면 "개체 탐색기 선택이 다릅니다"의 판정 근거가 사라진다.
            // 여전히 다르다면 다음 CheckSsmsSelection()에서 다시 뜬다.
            SsmsHintMessage = null;
        }

        // ---------- 개체 탐색기 안내 ----------

        private string? _ssmsHintMessage;

        /// <summary>
        /// 개체 탐색기와 관련해 사용자가 지금 알아야 할 한 줄. 없으면 <c>null</c>이고 UI에서 숨는다.
        ///
        /// 입력란이 사라진 뒤로 이 문장이 "무엇을 해야 하는가"를 말하는 유일한 곳이다.
        /// </summary>
        public string? SsmsHintMessage
        {
            get => _ssmsHintMessage;
            private set
            {
                if (_ssmsHintMessage == value) return;
                _ssmsHintMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSsmsHintMessage));
            }
        }

        public bool HasSsmsHintMessage => !string.IsNullOrEmpty(SsmsHintMessage);

        /// <summary>
        /// 개체 탐색기의 현재 선택이 지금 대상과 다른지 확인하고, 다르면 안내를 띄운다.
        /// <b>대상을 건드리지 않는다</b> — 전환은 사용자가 Connect를 눌러야 일어난다.
        ///
        /// 선택 변경 이벤트를 구독하는 대신, 사용자가 이 패널로 시선을 옮기는 순간
        /// (마우스 진입·포커스)에만 확인한다. 배경 비용이 없고 필요한 시점에만 뜬다.
        /// </summary>
        public void CheckSsmsSelection()
        {
            if (_ssmsConnectionSource == null)
            {
                return;
            }

            var info = _ssmsConnectionSource.TryGetCurrent();

            if (info == null)
            {
                // 대상을 이미 잡아 두었다면 침묵한다 — 개체 탐색기에서 잠깐 다른 노드를 클릭한
                // 것과 구분할 근거가 없고, 그때마다 배너가 뜨면 진짜 경고까지 함께 묻힌다.
                SsmsHintMessage = HasContext
                    ? null
                    : "개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 하나 선택한 뒤 Connect를 누르세요.";
                return;
            }

            bool sameTarget =
                string.Equals(info.ServerName, ServerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.DatabaseName, DatabaseName, StringComparison.OrdinalIgnoreCase);

            if (sameTarget)
            {
                SsmsHintMessage = null;
                return;
            }

            SsmsHintMessage = HasContext
                ? $"개체 탐색기 선택이 다릅니다 — {info.ServerName}.{info.DatabaseName}. " +
                  "Connect를 누르면 이 대상으로 전환됩니다."
                : $"개체 탐색기 선택: {info.ServerName}.{info.DatabaseName} — Connect를 누르세요.";
        }
```

- [x] **Step 7: `Connect()`와 `ApplyContext()`로 `SetContext`·`PersistCredential`을 대체한다**

`SetContext`(460~501행)와 `PersistCredential`(Task 3에서 고친 것)을 아래로 교체한다.

```csharp
        /// <summary>
        /// 개체 탐색기의 현재 선택을 대상으로 채택하고 접속한다. 유일한 연결 경로다.
        /// </summary>
        private void Connect()
        {
            var info = _ssmsConnectionSource?.TryGetCurrent();

            if (info == null)
            {
                // 대상을 모르는 채로 할 수 있는 일이 없다. 다만 지금 대상과 목록은 그대로 둔다 —
                // 읽지 못했다는 사실이 그것들을 거짓으로 만들지는 않는다.
                WarningMessage =
                    "개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 하나 선택한 뒤 다시 누르세요. " +
                    "서버 노드나 여러 개를 한꺼번에 선택한 상태에서는 대상을 정할 수 없습니다.";
                SsmsDiagnostics.Trace("Connect 중단: 개체 탐색기에서 연결을 읽지 못했습니다.");
                return;
            }

            SetTarget(info.ServerName, info.DatabaseName, info.AuthMode, info.UserName);

            if (info.UnsupportedReason != null)
            {
                // 인증 정보를 얻을 길이 없으므로 실패가 확정된 접속을 시도하지 않는다.
                // 시도하면 사유 대신 TestConnection의 낮은 수준 오류가 배너에 뜬다.
                WarningMessage = info.UnsupportedReason;
                SsmsDiagnostics.Trace(
                    $"Connect 중단: {info.ServerName}.{info.DatabaseName} — {info.UnsupportedReason}");
                return;
            }

            _credentialStore.Set(info.ServerName, info.DatabaseName, info.AuthMode, info.UserName, info.Password);
            SsmsDiagnostics.Trace(
                $"접속 시도: {info.ServerName}.{info.DatabaseName} {info.AuthMode} 인증, " +
                $"계정={info.UserName ?? "(없음)"}, 암호 실림={info.Password != null}");

            ApplyContext();
        }

        /// <summary>
        /// 지금 대상에 대해 접속·매핑·초기화 상태를 다시 판정하고 목록을 채운다.
        /// </summary>
        private void ApplyContext()
        {
            if (!HasContext)
            {
                return;
            }

            // 접속부터 확인한다. 실패를 "초기화되지 않음"으로 뭉개면
            // 사용자는 Setup DBVC 버튼만 보고 원인을 알 수 없다.
            var connectionError = _stateTracker.TestConnection(ServerName!, DatabaseName!);
            if (connectionError != null)
            {
                IsMapped = _configManager.TryGetMapping(ServerName!, DatabaseName!) != null;
                IsInitialized = false;
                WarningMessage = connectionError;
                return;
            }

            IsMapped = _configManager.TryGetMapping(ServerName!, DatabaseName!) != null;
            WarningMessage = IsMapped ? null : NotMappedWarning;

            IsInitialized = _stateTracker.IsInitialized(ServerName!, DatabaseName!);

            if (IsMapped && IsInitialized)
            {
                Refresh();
            }
        }
```

- [x] **Step 8: 남은 참조를 정리한다**

같은 파일에서:

1. 생성자의 두 줄을 고친다.

```csharp
            ConnectCommand = new RelayCommand(Connect, () => _ssmsConnectionSource != null);
```

그리고 `RefreshFromSsmsCommand = new RelayCommand(...)` 줄을 삭제한다.

2. `RefreshFromSsmsCommand` 속성 선언과 그 XML 주석을 삭제한다.

3. `ConnectCommand`의 XML 주석을 아래로 바꾼다.

```csharp
        /// <summary>
        /// 개체 탐색기의 현재 선택을 대상으로 채택하고 접속한다.
        /// 입력란이 없으므로 이것이 유일한 연결 경로다.
        /// </summary>
```

4. `RaiseConnectCanExecuteChanged()` 메서드를 삭제한다. `ConnectCommand`의 실행 가능 여부는 생성 시점에 정해져 바뀌지 않는다.

5. `ConnectRepository()` 끝의 `SetContext(ServerName, DatabaseName);`를 아래로 바꾼다.

```csharp
            // 매핑이 생겼으므로 상태를 다시 판정한다. 인증 정보는 이미 저장소에 있다.
            InvalidateActiveContext();
            ApplyContext();
```

6. `TryFillFromSsms()` 메서드(393~455행)를 통째로 삭제한다. Step 6이 교체한 88~387행 밖에 있어 남아 있다.

   (`AuthModes`·`AuthModeOption`·`IsSqlAuth`·`Password`·`PasswordFromSsms`·`HasSsmsPassword`·`ForgetSsmsPassword`·`ConnectionSourceMessage`·`HasConnectionSourceMessage`·`_ssmsFillEverSucceeded`·`LoadSavedCredential`은 Step 6의 교체 범위 안이라 이미 사라졌다. 다시 찾지 말 것.)

- [x] **Step 9: XAML 상단 영역을 교체한다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml`의 18~86행(`<!-- 대상 데이터베이스 지정 ... -->` 주석부터 `</StackPanel>`까지)을 아래로 교체한다.

```xml
        <!--
            대상 표시와 유일한 연결 버튼. 연결 정보는 SSMS 개체 탐색기에서만 오므로
            입력란이 없다.
        -->
        <StackPanel Grid.Row="0">
            <WrapPanel Orientation="Horizontal" Margin="5,5,5,0">
                <Button Content="Connect" Command="{Binding ConnectCommand}" Width="80" Margin="0,0,10,4"
                        ToolTip="SSMS 개체 탐색기에서 선택한 데이터베이스로 접속합니다. 인증 정보는 그 연결에서만 오며 디스크에 저장되지 않습니다."/>
                <TextBlock Text="{Binding TargetSummary}" VerticalAlignment="Center" Margin="0,0,0,4"/>
            </WrapPanel>

            <!--
                행동 안내. 개체 탐색기 선택이 지금 대상과 다르거나, 아직 아무것도 고르지
                않았을 때 뜬다. 입력란이 사라진 뒤로 "무엇을 눌러야 하는가"를 말하는 유일한 곳이다.
            -->
            <TextBlock Text="{Binding SsmsHintMessage}" Foreground="#8A6D1F"
                       TextWrapping="Wrap" Margin="5,0,5,4"
                       Visibility="{Binding HasSsmsHintMessage, Converter={StaticResource BoolToVis}}"/>
        </StackPanel>
```

`<UserControl.Resources>`의 두 컨버터는 그대로 둔다 — `InverseBooleanToVisibilityConverter`는 아래쪽 Setup 오버레이가, `BooleanToVisibilityConverter`는 위 안내와 경고 배너가 쓴다.

- [x] **Step 10: 코드 비하인드를 고친다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs`에서:

1. `OnSqlPasswordChanged` 메서드(117~128행)를 통째로 삭제한다.
2. 파일 상단의 `using System.Windows.Controls;`는 `UserControl` 때문에 필요하므로 남긴다.
3. `OnIsVisibleChanged`(91~104행)를 아래로 교체한다.

```csharp
        /// <summary>
        /// 도구 창이 보여질 때 개체 탐색기 선택을 현재 대상과 대조한다.
        /// 처음 열 때와 다른 탭에서 돌아올 때를 함께 덮는다.
        ///
        /// 채울 입력란이 없으므로 여기서 할 수 있는 일은 안내를 맞춰 두는 것뿐이다.
        /// 접속은 언제나 사용자가 Connect를 눌러야 일어난다.
        /// </summary>
        private void OnIsVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible)
            {
                _viewModel.CheckSsmsSelection();
            }
        }
```

4. `OnPointerOrFocusEntered`의 XML 주석에서 "'개체 탐색기에서 가져오기' 버튼이 한다"를 "Connect 버튼이 한다"로 고친다.

- [x] **Step 11: 빌드하고 전체 테스트를 돌린다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests && dotnet test tests/DBVC.Vsix.Tests`
Expected: 전부 PASS

- [x] **Step 12: 사라진 이름이 남아 있지 않은지 확인한다**

Run: `grep -rn "TryFillFromSsms\|RefreshFromSsmsCommand\|ConnectionSourceMessage\|HasSsmsPassword\|IsSqlAuth\|AuthModeOption\|PasswordBox\|SetContext" src/ tests/`
Expected: 결과 없음

- [x] **Step 13: 커밋**

```bash
git add -A
git commit -m "feat(vsix): 연결 입력란을 없애고 Connect 하나로 개체 탐색기 선택에 붙는다"
```

---

## Task 5: 문서 갱신

코드가 끝났으므로 README와 설치 체크리스트를 실제 동작에 맞춘다. 기존 설계 문서들은 당시의 결정 기록이므로 **수정하지 않는다.**

**Files:**
- Modify: `README.md:62-64`
- Modify: `docs/setup-checklist.md` (183~186, 201~204, 269, 291~298, 346~349, 372~373행)

**Interfaces:**
- Consumes: Task 1~4가 만든 최종 동작
- Produces: 없음 (문서)

---

- [x] **Step 1: README의 인증 서술 두 문단을 교체한다**

`README.md`의 62행과 64행(각각 한 문단)을 아래 두 문단으로 교체한다.

```markdown
**데이터베이스 연결 정보는 SSMS 개체 탐색기에서만 옵니다.** DBVC 창에는 입력란이 없습니다 — 개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 선택하고 **Connect** 를 누르면 서버·데이터베이스·인증 방식·계정·암호를 그 연결에서 그대로 가져와 접속합니다. Windows 통합 인증과 SQL Server 인증을 모두 지원하며, 어느 쪽인지는 개체 탐색기의 연결이 정합니다.

**인증 정보는 디스크에 저장되지 않습니다.** SSMS가 살아 있는 동안 프로세스 메모리에만 있고, SSMS를 닫으면 사라집니다. 다시 열었을 때는 개체 탐색기에 접속한 뒤 **Connect** 를 한 번 누르면 됩니다. DBVC 창을 열어 둔 채 개체 탐색기에서 다른 데이터베이스를 선택하면 대상이 저절로 따라가지는 않고, 선택이 다르다는 안내가 뜹니다 — **Connect** 를 눌러야 전환됩니다. Microsoft Entra ID로 접속한 연결은 토큰 기반이라 재사용할 수 없으며, 이 경우 사유를 표시하고 접속을 시도하지 않습니다.
```

- [x] **Step 2: 설치 체크리스트의 인증 절차를 고친다**

`docs/setup-checklist.md`에서:

1. **183~186행** — "암호는 DPAPI로 암호화되어 `%APPDATA%\DBVC\credentials.json`에 저장되며, 저장한 Windows 계정에서만 복호화된다. 다음부터는 암호 칸을 비워 두면 저장된 값을 쓴다"를 아래로 교체한다.

```markdown
      인증 정보는 개체 탐색기의 연결에서 그대로 오며 디스크에 저장되지 않는다.
      SSMS를 다시 열면 개체 탐색기에 접속한 뒤 Connect를 한 번 더 누른다.
```

2. **186행** — "**Connect** 를 누른다"를 "개체 탐색기에서 대상 데이터베이스를 선택한 뒤 **Connect** 를 누른다"로 고친다.

3. **201~204행** — `credentials.json`을 열어 `ProtectedPassword`를 확인하는 항목을 삭제하고, 아래 항목으로 교체한다.

```markdown
- [x] `%APPDATA%\DBVC` 에 `credentials.json` 이 **없는지** 확인한다. 이전 버전이 남긴 파일이
      있었다면 확장이 처음 로드될 때 지워진다.
```

4. **291~298행 (`### 인증` 절)** — 항목 전체를 아래로 교체한다.

```markdown
- [x] **SQL 인증 서버에서 Connect** → 대상 표시줄에 `서버.DB — SQL 인증 (계정)` 이 뜨고 접속되는지
- [x] **SSMS를 재시작하고 개체 탐색기에 접속하지 않은 채 Connect** → 선택 안내가 뜨고 접속을
      시도하지 않는지
- [x] **개체 탐색기에서 서버 노드만 선택한 채 Connect** → 같은 안내가 뜨는지
- [x] **DBVC 창을 개체 탐색기와 나란히 띄운 채 다른 DB를 선택** → 패널에 마우스를 올리면
      "선택이 다릅니다" 안내가 뜨고, Connect를 누르면 그 대상으로 전환되는지
- [x] `%APPDATA%\DBVC` 에 `credentials.json` 이 생기지 않는지
```

5. **346~349행** — "SQL 인증 암호는 저장한 Windows 계정에 묶인다" 항목 전체를 아래로 교체한다.

```markdown
- **인증 정보는 SSMS 프로세스와 함께 산다.** 디스크에 남지 않으므로 다른 기계로 옮길 것도 없고,
  SSMS를 닫으면 사라진다. 다시 열었을 때는 개체 탐색기에 접속한 뒤 Connect를 한 번 누른다.
```

6. **372~373행 (문제 해결 표)** — 두 행을 아래로 교체한다.

```markdown
| Connect가 "로그인하지 못했습니다"를 낸다 | 개체 탐색기의 그 연결로는 접속되는지, 그리고 서버가 혼합 모드인지 (`SERVERPROPERTY('IsIntegratedSecurityOnly')` 가 `0`) |
| Connect가 "암호를 사용할 수 없습니다"를 낸다 | 개체 탐색기가 그 연결의 암호를 들고 있지 않다. 개체 탐색기에서 해당 서버에 다시 접속한 뒤 Connect를 누른다 |
| Connect가 "개체 탐색기에서 ... 선택한 뒤"를 낸다 | 선택이 없거나, 여러 개이거나, 서버 노드다. 데이터베이스 노드 하나를 고른다 |
```

7. **35~37행**(혼합 모드 요구)은 **그대로 둔다.** 여전히 유효하다.

- [x] **Step 3: 문서에 남은 옛 서술이 없는지 확인한다**

Run: `grep -rn "credentials.json\|DPAPI\|암호 칸\|가져오기" README.md docs/setup-checklist.md`
Expected: `credentials.json`은 "없는지 확인한다"는 맥락으로만 남아야 한다. `DPAPI`·`암호 칸`·`가져오기` 버튼 언급은 없어야 한다

- [x] **Step 4: 커밋**

```bash
git add README.md docs/setup-checklist.md
git commit -m "docs: 연결 정보가 개체 탐색기에서만 온다는 사실을 문서에 반영한다"
```

---

## 최종 확인 (수동, SSMS 21 필요)

계획의 마지막 단계다. 단위 테스트로 덮이지 않는 리플렉션 경로를 사람이 확인한다. `.vsix`를 빌드해 SSMS 21에 설치한 뒤:

- [x] 개체 탐색기에서 **SQL 인증**으로 접속하고 데이터베이스 노드를 선택 → DBVC 창에서 **Connect** → 대상 표시줄에 `서버.DB — SQL 인증 (계정)`이 뜨고 변경 목록이 채워진다
- [x] `%APPDATA%\DBVC`에 `credentials.json`이 **없다** (이전 버전 파일이 있었다면 지워졌다)
- [x] **Windows 인증** 연결에서도 같은 흐름이 돈다
- [x] SSMS를 재시작하고 개체 탐색기에 접속하지 않은 채 **Connect** → 선택 안내가 뜨고 접속하지 않는다
- [x] 개체 탐색기에서 아무것도 선택하지 않거나 서버 노드를 선택한 채 **Connect** → 같은 안내
- [x] DBVC 창을 개체 탐색기와 나란히 띄운 채 다른 DB를 선택 → 패널에 마우스를 올리면 안내가 뜨고, **Connect**로 전환된다
- [x] Entra ID로 접속한 서버를 선택한 채 **Connect** → 사유가 뜨고 접속 시도가 없다 (가능한 경우)
- [x] `%APPDATA%\DBVC\ssms-diagnostics.log`에 `접속 시도:` 또는 `Connect 중단:` 줄이 남는다
