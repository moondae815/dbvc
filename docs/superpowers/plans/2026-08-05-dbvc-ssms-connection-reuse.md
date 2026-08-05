# SSMS 연결 재사용 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SSMS 개체 탐색기에서 선택한 데이터베이스의 연결 정보를 DBVC 도구 창이 자동으로 가져오되, 가져온 암호는 디스크가 아닌 프로세스 메모리에만 둔다.

**Architecture:** SSMS 셸 타입을 리플렉션으로 읽는 얇은 어댑터(`ObjectExplorerConnectionSource`)가 `ISsmsConnectionSource` 뒤에 숨는다. ViewModel은 그 인터페이스만 알고, 가져온 암호는 `SqlCredentialStore`가 합성한 `SessionPasswordCache`(메모리 전용)에 실려 기존 `SqlConnectionFactory` 경로를 그대로 탄다. 어느 단계가 실패하든 결과는 "자동 채움이 안 됨"이며 기존 수동 입력 동작이 그대로 남는다.

**Tech Stack:** C# / .NET Framework 4.8 (DBVC.Vsix), netstandard2.0+net48 (DBVC.Core), WPF, NUnit 4 + Moq, System.Reflection

**설계 문서:** `docs/superpowers/specs/2026-08-05-dbvc-ssms-connection-reuse-design.md`

## Global Constraints

> **[사후 수정 안내 — 2026-08-05]** Task 4, 5, 6은 최종 브랜치 리뷰 결과에 따라 실행 중에
> 내용이 변경되었다. 실제로 배포된 설계는
> `docs/superpowers/specs/2026-08-05-dbvc-ssms-connection-reuse-design.md`이며,
> 인증 방식 판정 순서는 §4.2, 암호 출처 규칙은 §4.5를 따른다. 아래 Task들의 코드 블록은
> 이 문서가 최초 계획을 그대로 담은 **역사적 기록**이라 고치지 않았지만, 실제 코드와 어긋나는
> 부분은 위 설계 문서가 우선한다. 특히 아래 두 결함은 계획 단계의 코드에 있었고 이후 수정되었다 —
> 이 문서의 코드 블록을 그대로 다시 구현하면 재발한다.
> 1. **Entra ID 연결이 Windows 인증으로 오판됨**: `UseIntegratedSecurity`를 먼저 묻고
>    `AccessToken`/`Authentication`을 확인하지 않아, 토큰 기반(Entra ID) 연결이 통합 보안으로
>    잘못 분류되었다(수정본은 §4.2의 순서 — `AccessToken` → `Authentication` → `UseIntegratedSecurity`
>    — 를 따른다).
> 2. **SSMS에서 가져온 암호가 대상이 바뀌어도 남아 있을 수 있음**: 계획의 ViewModel 코드에는
>    `ForgetSsmsPassword()` 호출이 전혀 없어, 서버·DB·인증 방식·계정 중 하나가 바뀌어도 이전
>    SSMS 암호가 새 대상으로 전송될 수 있었다(수정본은 §4.5.1의 네 setter 모두에서 이를 호출한다).

- 대상 셸은 **SSMS 21** (어셈블리 버전 21.200.0.0). SSMS 어셈블리는 **컴파일 타임에 참조하지 않는다** — 전부 리플렉션이다.
- `DBVC.Core`와 `DBVC.Vsix`는 비Windows에서도 컴파일·단위 테스트가 통과해야 한다. SSMS가 없는 환경에서 어댑터는 `null`을 반환할 뿐 예외를 던지지 않는다.
- **SSMS에서 가져온 암호는 어떤 경로로도 디스크에 기록하지 않는다.** 이것이 이번 작업의 핵심 계약이며 파일 내용을 직접 읽는 테스트로 고정한다.
- 사용자에게 보이는 문자열과 코드 주석은 한국어. 기존 파일의 주석 밀도·어투를 따른다.
- 테스트는 NUnit(`[TestFixture]`, `Assert.That`). DBVC.Vsix.Tests만 Moq를 쓴다.
- 빌드·테스트 명령: `dotnet build DBVC.slnx`, `dotnet test tests/DBVC.Core.Tests`, `dotnet test tests/DBVC.Vsix.Tests`

## File Structure

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/SessionPasswordCache.cs` (신규) | 프로세스 수명의 (서버, DB) → 암호. 디스크를 모른다 |
| `src/DBVC.Core/Abstractions.cs` (수정) | `ISqlCredentialStore.SetSessionPassword` 추가 |
| `src/DBVC.Core/SqlCredentialStore.cs` (수정) | 캐시 합성, 조회 우선순위, `Save`의 캐시 무효화 |
| `src/DBVC.Core/SqlConnectionFactory.cs` (수정) | 예외 안내 문구 보강 |
| `src/DBVC.Vsix/Services/SsmsUrn.cs` (신규) | SMO URN에서 데이터베이스 이름 추출 (순수 함수) |
| `src/DBVC.Vsix/Services/SsmsConnectionInfo.cs` (신규) | 어댑터가 돌려주는 DTO + `ISsmsConnectionSource` |
| `src/DBVC.Vsix/Services/ObjectExplorerConnectionSource.cs` (신규) | SSMS 셸 리플렉션. 판단 로직을 두지 않는다 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (수정) | 자동 채움, 암호 출처 추적, 저장 분기 |
| `src/DBVC.Vsix/DbvcServices.cs` (수정) | 어댑터 배선 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml(.cs)` (수정) | 가시성 트리거, 갱신 버튼, 안내 줄 |

---

### Task 1: `SessionPasswordCache` (DBVC.Core)

**Files:**
- Create: `src/DBVC.Core/SessionPasswordCache.cs`
- Test: `tests/DBVC.Core.Tests/SessionPasswordCacheTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public class SessionPasswordCache`
  - `void Set(string serverName, string databaseName, string? plainPassword)`
  - `string? TryGet(string serverName, string databaseName)`
  - `bool Remove(string serverName, string databaseName)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SessionPasswordCacheTests.cs`:

```csharp
using DBVC.Core;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SessionPasswordCacheTests
    {
        [Test]
        public void TryGet_ReturnsNull_WhenNothingWasSet()
        {
            Assert.That(new SessionPasswordCache().TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Set_ThenTryGet_RoundTripsThePassword()
        {
            var cache = new SessionPasswordCache();

            cache.Set("srv", "db", "p@ss");

            Assert.That(cache.TryGet("srv", "db"), Is.EqualTo("p@ss"));
        }

        [Test]
        public void TryGet_IgnoresCase_LikeTheCredentialStore()
        {
            var cache = new SessionPasswordCache();
            cache.Set("SRV", "DB", "p@ss");

            Assert.That(cache.TryGet("srv", "db"), Is.EqualTo("p@ss"),
                "저장소와 키 규약이 다르면 같은 항목을 서로 다른 것으로 봅니다");
        }

        [Test]
        public void TryGet_KeepsDatabasesApart()
        {
            var cache = new SessionPasswordCache();
            cache.Set("srv", "db1", "one");
            cache.Set("srv", "db2", "two");

            Assert.That(cache.TryGet("srv", "db1"), Is.EqualTo("one"));
            Assert.That(cache.TryGet("srv", "db2"), Is.EqualTo("two"));
        }

        [Test]
        public void Set_RemovesTheEntry_WhenThePasswordIsNullOrEmpty()
        {
            var cache = new SessionPasswordCache();
            cache.Set("srv", "db", "p@ss");

            cache.Set("srv", "db", null);
            Assert.That(cache.TryGet("srv", "db"), Is.Null);

            cache.Set("srv", "db", "p@ss");
            cache.Set("srv", "db", "");
            Assert.That(cache.TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Remove_ReportsWhetherSomethingWasThere()
        {
            var cache = new SessionPasswordCache();
            cache.Set("srv", "db", "p@ss");

            Assert.That(cache.Remove("srv", "db"), Is.True);
            Assert.That(cache.Remove("srv", "db"), Is.False);
            Assert.That(cache.TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void EmptyServerOrDatabase_IsIgnoredInsteadOfThrowing()
        {
            var cache = new SessionPasswordCache();

            Assert.DoesNotThrow(() => cache.Set("", "db", "p@ss"));
            Assert.That(cache.TryGet("", "db"), Is.Null);
            Assert.That(cache.Remove("srv", ""), Is.False);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter SessionPasswordCacheTests`
Expected: 컴파일 실패 — `SessionPasswordCache`를 찾을 수 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/SessionPasswordCache.cs`:

```csharp
using System;
using System.Collections.Concurrent;

namespace DBVC.Core
{
    /// <summary>
    /// 이 프로세스에서만 유효한 (서버, 데이터베이스)별 평문 암호.
    ///
    /// SSMS 개체 탐색기에서 가져온 암호가 여기에 들어간다. 사용자가 직접 입력한 암호와 달리
    /// 디스크에 남기지 않기로 한 값이므로, 파일을 다루는 <see cref="SqlCredentialStore"/>와
    /// 수명이 다르다. 두 수명을 한 클래스에 섞으면 직렬화 코드가 "이 항목은 쓰면 안 된다"는
    /// 분기를 들고 다니게 되므로 분리한다.
    ///
    /// 키 규약은 <see cref="SqlCredentialStore"/>와 같아야 한다 — 같은 (서버, DB)를 서로 다른
    /// 항목으로 보면 세션 암호가 조회되지 않는다.
    /// </summary>
    public class SessionPasswordCache
    {
        private readonly ConcurrentDictionary<string, string> _passwords =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 세션 암호를 기록한다. <c>null</c>이거나 빈 문자열이면 기존 항목을 제거한다 —
        /// "암호 없음"을 빈 문자열로 들고 있으면 조회 쪽에서 다시 분기해야 한다.
        /// </summary>
        public void Set(string serverName, string databaseName, string? plainPassword)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return;
            }

            if (string.IsNullOrEmpty(plainPassword))
            {
                Remove(serverName, databaseName);
                return;
            }

            _passwords[GetKey(serverName, databaseName)] = plainPassword!;
        }

        public string? TryGet(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }

            return _passwords.TryGetValue(GetKey(serverName, databaseName), out var password)
                ? password
                : null;
        }

        public bool Remove(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return false;
            }

            return _passwords.TryRemove(GetKey(serverName, databaseName), out _);
        }

        private static string GetKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter SessionPasswordCacheTests`
Expected: PASS (7개)

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Core/SessionPasswordCache.cs tests/DBVC.Core.Tests/SessionPasswordCacheTests.cs
git commit -m "feat(core): 프로세스 수명의 세션 암호 캐시를 추가"
```

---

### Task 2: 저장소·연결 팩토리에 세션 암호를 연결

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs` (`ISqlCredentialStore`)
- Modify: `src/DBVC.Core/SqlCredentialStore.cs`
- Modify: `src/DBVC.Core/SqlConnectionFactory.cs:43-47` (예외 문구)
- Test: `tests/DBVC.Core.Tests/SqlCredentialStoreTests.cs` (추가), `tests/DBVC.Core.Tests/SqlConnectionFactoryTests.cs` (추가)

**Interfaces:**
- Consumes: Task 1의 `SessionPasswordCache`
- Produces:
  - `ISqlCredentialStore.SetSessionPassword(string serverName, string databaseName, string? plainPassword)` — Task 4의 ViewModel이 호출한다
  - `SqlCredentialStore(string filePath, IPasswordProtector? protector = null, SessionPasswordCache? sessionPasswords = null)`
  - `ResolvePassword`가 세션 암호를 디스크 암호보다 먼저 본다

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SqlCredentialStoreTests.cs`의 `Remove_DeletesTheEntry` 위(제거 섹션 앞)에 새 섹션으로 추가한다:

```csharp
        // ---------- 세션 전용 암호 ----------

        [Test]
        public void ResolvePassword_PrefersTheSessionPassword_OverTheStoredOne()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "onDisk");

            store.SetSessionPassword("srv", "db", "fromSsms");

            Assert.That(store.ResolvePassword(store.TryGet("srv", "db")), Is.EqualTo("fromSsms"),
                "SSMS에서 방금 가져온 연결이 예전에 저장해 둔 암호보다 최신입니다");
        }

        [Test]
        public void SetSessionPassword_NeverTouchesTheFile()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", null);

            store.SetSessionPassword("srv", "db", "OnlyInMemory123");

            Assert.That(File.Exists(_path), Is.True);
            Assert.That(File.ReadAllText(_path), Does.Not.Contain("OnlyInMemory123"),
                "SSMS에서 가져온 암호는 어떤 형태로도 디스크에 남지 않아야 합니다");
        }

        [Test]
        public void SessionPassword_IsGoneInANewProcess()
        {
            NewStore().SetSessionPassword("srv", "db", "fromSsms");
            NewStore().Save("srv", "db", SqlAuthMode.Sql, "sa", null);

            // 새 인스턴스 = 새 캐시. 프로세스를 다시 띄운 것과 같다.
            var reloaded = new SqlCredentialStore(_path, new ReversibleProtector());

            Assert.That(reloaded.ResolvePassword(reloaded.TryGet("srv", "db")), Is.Null);
        }

        [Test]
        public void Save_ClearsTheSessionPassword_WhenAPlainPasswordIsGiven()
        {
            var store = NewStore();
            store.SetSessionPassword("srv", "db", "fromSsms");

            // 사용자가 암호를 직접 입력하고 Connect를 눌렀다.
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "typed");

            Assert.That(store.ResolvePassword(store.TryGet("srv", "db")), Is.EqualTo("typed"),
                "직접 입력한 값이 SSMS에서 가져온 값을 이겨야 합니다");
        }

        [Test]
        public void Save_KeepsTheSessionPassword_WhenPasswordIsNull()
        {
            var store = NewStore();
            store.SetSessionPassword("srv", "db", "fromSsms");

            // SSMS 경로: 인증 방식·계정명만 남기고 암호는 건드리지 않는다.
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", null);

            Assert.That(store.ResolvePassword(store.TryGet("srv", "db")), Is.EqualTo("fromSsms"));
        }

        [Test]
        public void Save_ClearsTheSessionPassword_WhenSwitchingBackToWindowsAuth()
        {
            var store = NewStore();
            store.SetSessionPassword("srv", "db", "fromSsms");

            store.Save("srv", "db", SqlAuthMode.Windows, null, null);

            Assert.That(store.ResolvePassword(store.TryGet("srv", "db")), Is.Null,
                "Windows 인증으로 되돌렸으면 세션 암호도 들고 있을 이유가 없습니다");
        }

        [Test]
        public void SetSessionPassword_ThrowsArgumentException_WhenServerOrDatabaseIsMissing()
        {
            var store = NewStore();

            Assert.Throws<ArgumentException>(() => store.SetSessionPassword("", "db", "p@ss"));
            Assert.Throws<ArgumentException>(() => store.SetSessionPassword("srv", "", "p@ss"));
        }
```

`tests/DBVC.Core.Tests/SqlConnectionFactoryTests.cs`의 `BuildSql_DoesNotPersistSecurityInfo` 앞에 추가한다:

```csharp
        [Test]
        public void Build_UsesTheSessionPassword_WhenNothingWasPersisted()
        {
            var store = NewStore();
            // SSMS 경로가 만드는 상태: 디스크에는 인증 방식과 계정명만, 암호는 메모리에만.
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", null);
            store.SetSessionPassword("srv", "db", "fromSsms");

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("User ID=sa"));
            Assert.That(connectionString, Does.Contain("fromSsms"));
            Assert.That(connectionString, Does.Not.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_PointsAtObjectExplorer_WhenSqlAuthHasNoUsablePassword()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "");

            var ex = Assert.Throws<SqlCredentialException>(() => new SqlConnectionFactory(store).Build("srv", "db"));

            Assert.That(ex!.Message, Does.Contain("개체 탐색기"),
                "이제 직접 입력 말고도 SSMS 연결을 가져오는 길이 있으므로 안내에 담겨야 합니다");
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter "SqlCredentialStoreTests|SqlConnectionFactoryTests"`
Expected: 컴파일 실패 — `SetSessionPassword`가 없음

- [ ] **Step 3: 인터페이스에 멤버를 더한다**

`src/DBVC.Core/Abstractions.cs`의 `ISqlCredentialStore` 안, `ResolvePassword` 아래에 추가:

```csharp
        /// <summary>
        /// 이 프로세스에서만 유효한 암호를 기록한다. 디스크에 쓰지 않는다.
        /// SSMS 개체 탐색기에서 가져온 암호가 이 경로로 들어온다.
        /// <c>null</c>이거나 빈 문자열이면 기존 세션 암호를 제거한다.
        /// </summary>
        void SetSessionPassword(string serverName, string databaseName, string? plainPassword);
```

- [ ] **Step 4: `SqlCredentialStore`를 고친다**

필드와 생성자 (`_protector` 선언 아래, 그리고 기존 생성자 교체):

```csharp
        private readonly SessionPasswordCache _sessionPasswords;
```

```csharp
        public SqlCredentialStore() : this(DefaultFilePath, null)
        {
        }

        public SqlCredentialStore(
            string filePath,
            IPasswordProtector? protector = null,
            SessionPasswordCache? sessionPasswords = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Credential file path cannot be null or whitespace.", nameof(filePath));
            }
            _filePath = filePath;
            _protector = protector ?? new DpapiPasswordProtector();
            _sessionPasswords = sessionPasswords ?? new SessionPasswordCache();
            Load();
        }
```

`Save`의 암호 처리 블록 바로 뒤(`_credentials[key] = credential;` 앞)에 무효화를 넣는다:

```csharp
            // 세션 암호(SSMS에서 가져온 값)를 언제 버리는지가 우선순위 규칙이다.
            //   - Windows 인증으로 되돌렸다 → 암호 자체가 필요 없다
            //   - 평문이 들어왔다(빈 문자열 포함) → 사용자가 직접 입력했으므로 그 값이 이긴다
            // plainPassword == null은 "저장된 것을 그대로 둔다"는 뜻이고 SSMS 경로가 쓰는 형태이므로
            // 여기서 지우면 안 된다.
            if (authMode != SqlAuthMode.Sql || plainPassword != null)
            {
                _sessionPasswords.Remove(serverName, databaseName);
            }
```

`ResolvePassword`를 교체한다:

```csharp
        /// <summary>
        /// 보호된 암호를 평문으로 되돌린다. 저장된 암호가 없거나 이 계정에서 풀 수 없으면 <c>null</c>.
        ///
        /// 세션 암호를 먼저 본다. SSMS에서 방금 가져온 연결이 예전에 저장해 둔 암호보다 최신이고,
        /// 애초에 디스크에 없는 값이므로 이 경로가 아니면 쓰일 곳이 없다.
        /// </summary>
        public string? ResolvePassword(SqlCredential? credential)
        {
            if (credential == null)
            {
                return null;
            }

            var sessionPassword = _sessionPasswords.TryGet(credential.ServerName, credential.DatabaseName);
            if (sessionPassword != null)
            {
                return sessionPassword;
            }

            if (string.IsNullOrEmpty(credential.ProtectedPassword))
            {
                return null;
            }

            return _protector.Unprotect(
                credential.ProtectedPassword,
                GetKey(credential.ServerName, credential.DatabaseName));
        }
```

`Remove` 아래에 새 메서드를 넣는다:

```csharp
        public void SetSessionPassword(string serverName, string databaseName, string? plainPassword)
        {
            ValidateServerAndDatabase(serverName, databaseName);
            _sessionPasswords.Set(serverName, databaseName, plainPassword);
        }
```

- [ ] **Step 5: `SqlConnectionFactory`의 안내 문구를 보강한다**

`src/DBVC.Core/SqlConnectionFactory.cs`의 `throw new SqlCredentialException(...)` 문자열을 교체:

```csharp
                throw new SqlCredentialException(
                    $"'{serverName}.{databaseName}'은(는) SQL 인증으로 설정되어 있으나 저장된 암호를 사용할 수 없습니다. " +
                    "Connect에서 사용자명과 암호를 다시 입력하세요. " +
                    "(암호는 저장한 Windows 계정에서만 복호화됩니다 — 다른 계정으로 로그온했다면 다시 입력해야 합니다.) " +
                    "SSMS 개체 탐색기에서 이 데이터베이스에 접속한 뒤 DBVC 창의 'SSMS 연결' 버튼을 누르면 " +
                    "그 연결의 인증 정보를 그대로 가져옵니다 — 이 방식으로 가져온 암호는 디스크에 저장되지 않습니다.");
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: 전부 PASS. 기존 테스트 중 깨지는 것이 없어야 한다 — 세션 암호가 없을 때의 동작은 이전과 동일하다.

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/SqlCredentialStore.cs src/DBVC.Core/SqlConnectionFactory.cs tests/DBVC.Core.Tests/SqlCredentialStoreTests.cs tests/DBVC.Core.Tests/SqlConnectionFactoryTests.cs
git commit -m "feat(core): 세션 암호를 디스크 암호보다 먼저 사용하도록 저장소를 확장"
```

---

### Task 3: `SsmsUrn` — URN에서 데이터베이스 이름 추출

**Files:**
- Create: `src/DBVC.Vsix/Services/SsmsUrn.cs`
- Test: `tests/DBVC.Vsix.Tests/Services/SsmsUrnTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public static class SsmsUrn` — `static string? TryGetDatabaseName(string? urn)`. Task 5의 어댑터가 호출한다.

**배경:** `INodeContext.Context`는 SMO URN이다. 예:
`Server[@Name='LOCALHOST\SQL2022']/Database[@Name='AdventureWorks']/Table[@Name='Person'and@Schema='Person']`
SMO는 값 안의 작은따옴표를 두 번 반복(`''`)으로 이스케이프한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/Services/SsmsUrnTests.cs`:

```csharp
using DBVC.Vsix.Services;
using NUnit.Framework;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class SsmsUrnTests
    {
        [Test]
        public void TryGetDatabaseName_ReadsTheDatabaseNode()
        {
            const string urn = @"Server[@Name='LOCALHOST\SQL2022']/Database[@Name='AdventureWorks']";

            Assert.That(SsmsUrn.TryGetDatabaseName(urn), Is.EqualTo("AdventureWorks"));
        }

        [Test]
        public void TryGetDatabaseName_ReadsItFromDeeperNodes()
        {
            const string urn =
                @"Server[@Name='LOCALHOST\SQL2022']/Database[@Name='SalesDB']/Table[@Name='Person'and@Schema='dbo']";

            Assert.That(SsmsUrn.TryGetDatabaseName(urn), Is.EqualTo("SalesDB"));
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_ForAServerNode()
        {
            // 사용자가 데이터베이스를 지목하지 않았다는 뜻이다. 초기 카탈로그로 넘겨짚지 않는다.
            Assert.That(SsmsUrn.TryGetDatabaseName(@"Server[@Name='LOCALHOST\SQL2022']"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_UnescapesDoubledQuotes()
        {
            // SMO URN은 값 안의 '를 ''로 이스케이프한다.
            const string urn = @"Server[@Name='S']/Database[@Name='Bob''s DB']/Table[@Name='T']";

            Assert.That(SsmsUrn.TryGetDatabaseName(urn), Is.EqualTo("Bob's DB"));
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_ForNullEmptyOrGarbage()
        {
            Assert.That(SsmsUrn.TryGetDatabaseName(null), Is.Null);
            Assert.That(SsmsUrn.TryGetDatabaseName(""), Is.Null);
            Assert.That(SsmsUrn.TryGetDatabaseName("not a urn at all"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_WhenTheQuoteIsNeverClosed()
        {
            Assert.That(SsmsUrn.TryGetDatabaseName("Server[@Name='S']/Database[@Name='unterminated"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_WhenTheNameIsEmpty()
        {
            Assert.That(SsmsUrn.TryGetDatabaseName("Server[@Name='S']/Database[@Name='']"), Is.Null);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter SsmsUrnTests`
Expected: 컴파일 실패 — `SsmsUrn`을 찾을 수 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Vsix/Services/SsmsUrn.cs`:

```csharp
using System;
using System.Text;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SMO URN에서 필요한 조각만 꺼낸다.
    ///
    /// 개체 탐색기 노드는 <c>INodeContext.Context</c>로 URN을 준다. 예:
    /// <c>Server[@Name='HOST\INST']/Database[@Name='SalesDB']/Table[@Name='Person'and@Schema='dbo']</c>
    ///
    /// 리플렉션 어댑터에서 이 로직만 떼어낸 이유는 단위 테스트다 —
    /// SSMS 프로세스 밖에서 검증할 수 있는 유일한 부분이므로 여기에 모아 둔다.
    /// </summary>
    public static class SsmsUrn
    {
        private const string DatabaseMarker = "/Database[@Name='";

        /// <summary>
        /// URN이 데이터베이스를 지목하고 있으면 그 이름을, 아니면 <c>null</c>.
        /// 서버 노드처럼 <c>Database</c> 마디가 없는 경우도 <c>null</c>이다 —
        /// 사용자가 데이터베이스를 고르지 않았다는 뜻이므로 넘겨짚지 않는다.
        /// </summary>
        public static string? TryGetDatabaseName(string? urn)
        {
            if (string.IsNullOrEmpty(urn))
            {
                return null;
            }

            int start = urn!.IndexOf(DatabaseMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
            start += DatabaseMarker.Length;

            var name = new StringBuilder();
            for (int i = start; i < urn.Length; i++)
            {
                if (urn[i] != '\'')
                {
                    name.Append(urn[i]);
                    continue;
                }

                // SMO는 값 안의 '를 ''로 이스케이프한다. 뒤따르는 문자가 '이면 닫는 따옴표가 아니다.
                if (i + 1 < urn.Length && urn[i + 1] == '\'')
                {
                    name.Append('\'');
                    i++;
                    continue;
                }

                return name.Length > 0 ? name.ToString() : null;
            }

            // 닫는 따옴표가 없다 — URN이 잘렸거나 형식이 다르다. 추측하지 않는다.
            return null;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter SsmsUrnTests`
Expected: PASS (7개)

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Vsix/Services/SsmsUrn.cs tests/DBVC.Vsix.Tests/Services/SsmsUrnTests.cs
git commit -m "feat(vsix): SMO URN에서 데이터베이스 이름을 뽑는 파서를 추가"
```

---

### Task 4: `ISsmsConnectionSource`와 ViewModel의 자동 채움

**Files:**
- Create: `src/DBVC.Vsix/Services/SsmsConnectionInfo.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` (추가)

**Interfaces:**
- Consumes: Task 2의 `ISqlCredentialStore.SetSessionPassword`
- Produces:
  - `public sealed class SsmsConnectionInfo` — 생성자
    `SsmsConnectionInfo(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password, string? unsupportedReason)`,
    같은 이름의 읽기 전용 속성 6개
  - `public interface ISsmsConnectionSource { SsmsConnectionInfo? TryGetCurrent(); }` — Task 5가 구현한다
  - `ViewChangesViewModel` 생성자의 마지막 선택 매개변수 `ISsmsConnectionSource? ssmsConnectionSource = null` — Task 5의 `DbvcServices`가 넘긴다
  - `bool ViewChangesViewModel.TryFillFromSsms()`, `ICommand RefreshFromSsmsCommand`,
    `string? ConnectionSourceMessage`, `bool HasConnectionSourceMessage` — Task 6의 UI가 쓴다

- [ ] **Step 1: DTO와 인터페이스를 만든다**

이 파일에는 로직이 없어 단독 테스트가 성립하지 않는다. Step 2의 테스트가 컴파일되려면 먼저 있어야 한다.

`src/DBVC.Vsix/Services/SsmsConnectionInfo.cs`:

```csharp
using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SSMS가 들고 있는 연결에서 DBVC가 쓸 수 있는 형태로 옮겨 담은 값.
    ///
    /// <see cref="ServerName"/>과 <see cref="DatabaseName"/>이 non-null인 것은 계약이다.
    /// 둘 중 하나라도 확정할 수 없으면 <see cref="ISsmsConnectionSource.TryGetCurrent"/>가
    /// <c>null</c>을 반환한다 — 절반짜리 값으로 입력란을 채우면 사용자가 직접 입력해 둔
    /// 데이터베이스 이름을 지우게 된다.
    /// </summary>
    public sealed class SsmsConnectionInfo
    {
        public SsmsConnectionInfo(
            string serverName,
            string databaseName,
            SqlAuthMode authMode,
            string? userName,
            string? password,
            string? unsupportedReason)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            AuthMode = authMode;
            UserName = userName;
            Password = password;
            UnsupportedReason = unsupportedReason;
        }

        public string ServerName { get; }
        public string DatabaseName { get; }
        public SqlAuthMode AuthMode { get; }
        public string? UserName { get; }

        /// <summary>SSMS가 암호를 들고 있지 않으면 <c>null</c>. 그 경우 저장된 암호로 폴백한다.</summary>
        public string? Password { get; }

        /// <summary>
        /// 이 연결을 그대로 재사용할 수 없는 사유(Entra ID 등). <c>null</c>이면 재사용 가능하다.
        /// 사유가 있어도 서버·데이터베이스는 쓸 수 있으므로 값 자체는 채워서 보낸다.
        /// </summary>
        public string? UnsupportedReason { get; }
    }

    /// <summary>
    /// SSMS 셸에서 현재 연결을 읽는 경로. ViewModel은 이 인터페이스만 안다 —
    /// 구현이 리플렉션이라 SSMS 프로세스 밖에서는 테스트할 수 없기 때문이다.
    /// </summary>
    public interface ISsmsConnectionSource
    {
        /// <summary>현재 선택에서 연결을 읽는다. 읽을 수 없으면 <c>null</c>(예외를 던지지 않는다).</summary>
        SsmsConnectionInfo? TryGetCurrent();
    }
}
```

- [ ] **Step 2: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`를 세 군데 고친다.

(a) 필드 선언부(`_credentials` 아래)에 추가:

```csharp
        private Mock<ISsmsConnectionSource> _ssms = null!;
```

(b) `SetUp`의 끝에 추가:

```csharp
            // 기본값: SSMS 연결 없음 = 자동 채움이 아무 일도 하지 않는다.
            _ssms = new Mock<ISsmsConnectionSource>();
            _ssms.Setup(s => s.TryGetCurrent()).Returns((SsmsConnectionInfo?)null);
```

(c) `NewViewModel()`을 교체:

```csharp
        private ViewChangesViewModel NewViewModel()
        {
            return new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _folderDialog, _credentials.Object, _ssms.Object);
        }
```

(d) 파일 끝(마지막 `}` 두 개 앞)에 새 섹션을 추가:

```csharp
        // ---------- SSMS 연결 자동 채움 ----------

        private static SsmsConnectionInfo SsmsSqlConnection(string? password = "fromSsms")
            => new SsmsConnectionInfo(Server, Database, SqlAuthMode.Sql, "sa", password, null);

        [Test]
        public void TryFillFromSsms_FillsTheTargetAndCredentialFields()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(SsmsSqlConnection());
            var vm = NewViewModel();

            Assert.That(vm.TryFillFromSsms(), Is.True);

            Assert.That(vm.ServerName, Is.EqualTo(Server));
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
            Assert.That(vm.AuthMode, Is.EqualTo(SqlAuthMode.Sql));
            Assert.That(vm.UserName, Is.EqualTo("sa"));
            Assert.That(vm.ConnectionSourceMessage, Does.Contain("암호 포함"));
        }

        [Test]
        public void TryFillFromSsms_KeepsTheSsmsCredential_WhenTheStoreHasAnOlderOne()
        {
            // Server/Database setter가 LoadSavedCredential()을 호출해 저장소 값을 덮어쓴다.
            // SSMS 값은 그 뒤에 얹혀야 한다 — 순서가 뒤집히면 이 테스트가 잡는다.
            _credentials.Setup(c => c.TryGet(Server, Database)).Returns(new SqlCredential
            {
                ServerName = Server,
                DatabaseName = Database,
                AuthMode = SqlAuthMode.Windows,
                UserName = null
            });
            _ssms.Setup(s => s.TryGetCurrent()).Returns(SsmsSqlConnection());
            var vm = NewViewModel();

            vm.TryFillFromSsms();

            Assert.That(vm.AuthMode, Is.EqualTo(SqlAuthMode.Sql));
            Assert.That(vm.UserName, Is.EqualTo("sa"));
        }

        [Test]
        public void TryFillFromSsms_ChangesNothing_WhenThereIsNoConnection()
        {
            var vm = NewViewModel();
            vm.ServerName = "TypedServer";
            vm.DatabaseName = "TypedDb";

            Assert.That(vm.TryFillFromSsms(), Is.False);

            Assert.That(vm.ServerName, Is.EqualTo("TypedServer"));
            Assert.That(vm.DatabaseName, Is.EqualTo("TypedDb"));
            Assert.That(vm.ConnectionSourceMessage, Is.Null);
        }

        [Test]
        public void TryFillFromSsms_WarnsAndLeavesAuthAlone_WhenTheConnectionIsUnsupported()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(new SsmsConnectionInfo(
                Server, Database, SqlAuthMode.Windows, null, null, "Entra ID 연결은 재사용할 수 없습니다."));
            var vm = NewViewModel();
            vm.AuthMode = SqlAuthMode.Sql;
            vm.UserName = "typedUser";

            vm.TryFillFromSsms();

            Assert.That(vm.ServerName, Is.EqualTo(Server));
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
            Assert.That(vm.WarningMessage, Does.Contain("Entra"));
            Assert.That(vm.UserName, Is.EqualTo("typedUser"),
                "재사용할 수 없는 연결이 사용자가 입력한 계정을 지워서는 안 됩니다");
        }

        [Test]
        public void TryFillFromSsms_IsSkipped_WhileTheUserIsTypingAPassword()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(SsmsSqlConnection());
            var vm = NewViewModel();
            vm.ServerName = "TypedServer";
            vm.Password = "typing";

            Assert.That(vm.TryFillFromSsms(), Is.False);
            Assert.That(vm.ServerName, Is.EqualTo("TypedServer"));
        }

        [Test]
        public void Connect_KeepsTheSsmsPasswordInMemoryOnly()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(SsmsSqlConnection());
            var vm = NewViewModel();
            vm.TryFillFromSsms();

            vm.ConnectCommand.Execute(null);

            _credentials.Verify(c => c.Save(Server, Database, SqlAuthMode.Sql, "sa", null), Times.Once,
                "SSMS에서 가져온 암호는 디스크에 저장하지 않습니다");
            _credentials.Verify(c => c.SetSessionPassword(Server, Database, "fromSsms"), Times.Once);
        }

        [Test]
        public void Connect_StillPersistsAPasswordTypedByTheUser()
        {
            var vm = NewViewModel();
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.AuthMode = SqlAuthMode.Sql;
            vm.UserName = "sa";
            vm.Password = "typed";

            vm.ConnectCommand.Execute(null);

            _credentials.Verify(c => c.Save(Server, Database, SqlAuthMode.Sql, "sa", "typed"), Times.Once);
            _credentials.Verify(c => c.SetSessionPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void Connect_FallsBackToTheStoredPassword_WhenSsmsHasNone()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(SsmsSqlConnection(password: null));
            var vm = NewViewModel();
            vm.TryFillFromSsms();

            vm.ConnectCommand.Execute(null);

            // plainPassword: null = "저장된 암호를 그대로 쓴다". 세션 암호는 기록할 것이 없다.
            _credentials.Verify(c => c.Save(Server, Database, SqlAuthMode.Sql, "sa", null), Times.Once);
            _credentials.Verify(c => c.SetSessionPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
            Assert.That(vm.ConnectionSourceMessage, Does.Not.Contain("암호 포함"));
        }
```

- [ ] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter ViewChangesViewModelTests`
Expected: 컴파일 실패 — `TryFillFromSsms`·`ConnectionSourceMessage`가 없음

- [ ] **Step 4: ViewModel을 고친다**

(a) `using DBVC.Vsix.Services;`는 이미 있다. 필드 선언(`_credentialStore` 아래)에 추가:

```csharp
        private readonly ISsmsConnectionSource? _ssmsConnectionSource;
```

(b) 생성자 시그니처의 마지막에 매개변수를 더하고 본문에서 대입한다:

```csharp
            ISqlCredentialStore? credentialStore = null,
            ISsmsConnectionSource? ssmsConnectionSource = null)
```

```csharp
            // null이면 자동 채움이 꺼진 것과 같다. 단위 테스트와 비SSMS 환경이 이 경로다.
            _ssmsConnectionSource = ssmsConnectionSource;
```

(c) 명령 목록(`ConnectRepositoryCommand` 아래)에 등록:

```csharp
            RefreshFromSsmsCommand = new RelayCommand(() => TryFillFromSsms(), () => _ssmsConnectionSource != null);
```

(d) `Password` 자동 속성을 백킹 필드 속성으로 바꾼다:

```csharp
        private string? _password;

        /// <summary>
        /// 입력 중인 평문 암호. Connect가 끝나면 즉시 비운다 — 보관은 저장소가 하고,
        /// ViewModel이 세션 내내 평문을 들고 있을 이유가 없다.
        ///
        /// <c>null</c>이거나 비어 있으면 "저장된 암호를 그대로 쓴다"는 뜻이다.
        /// PasswordBox는 바인딩을 지원하지 않으므로 코드 비하인드가 이 속성에 밀어 넣는다.
        ///
        /// setter를 탄다는 것은 곧 사용자가 직접 입력했다는 뜻이므로 출처 표시를 내린다.
        /// SSMS에서 가져온 암호는 이 setter를 거치지 않고 <see cref="TryFillFromSsms"/>가
        /// 백킹 필드에 직접 넣는다 — 그래야 저장 경로가 갈린다.
        /// </summary>
        public string? Password
        {
            get => _password;
            set
            {
                _password = value;
                _passwordFromSsms = false;
            }
        }

        /// <summary>현재 들고 있는 암호가 SSMS에서 온 것인지. 참이면 디스크에 저장하지 않는다.</summary>
        private bool _passwordFromSsms;
```

(e) `CanPersistPasswords` 아래에 안내 속성을 추가:

```csharp
        private string? _connectionSourceMessage;

        /// <summary>
        /// 자동 채움이 무슨 일을 했는지 알리는 한 줄. 채운 적이 없으면 <c>null</c>이고 UI에서 숨는다.
        ///
        /// 필요한 이유: SSMS에서 가져온 암호는 PasswordBox에 넣지 않으므로(넣으면 Password setter를
        /// 타서 디스크 저장 경로로 새어 나간다) 암호 칸은 비어 있는데 암호는 실려 있는 상태가 된다.
        /// 그 사실을 알리지 않으면 사용자가 다시 입력해야 하는 줄 안다.
        /// </summary>
        public string? ConnectionSourceMessage
        {
            get => _connectionSourceMessage;
            private set
            {
                if (_connectionSourceMessage == value) return;
                _connectionSourceMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConnectionSourceMessage));
            }
        }

        public bool HasConnectionSourceMessage => !string.IsNullOrEmpty(ConnectionSourceMessage);
```

(f) `LoadSavedCredential` 아래에 자동 채움을 추가:

```csharp
        /// <summary>
        /// SSMS 개체 탐색기의 현재 연결을 입력란에 채운다. 접속하지는 않는다 — 확정은 Connect가 한다.
        /// </summary>
        /// <returns>채웠으면 true. 가져올 연결이 없거나 채우지 않기로 했으면 false.</returns>
        public bool TryFillFromSsms()
        {
            var info = _ssmsConnectionSource?.TryGetCurrent();
            if (info == null) return false;

            // 사용자가 입력 중인 암호를 지우지 않는다. 도구 창이 다시 보일 때마다 이 메서드가
            // 불리므로(가시성 트리거), 가드가 없으면 타이핑 중이던 값이 사라진다.
            if (!_passwordFromSsms && !string.IsNullOrEmpty(_password)) return false;

            // 순서가 계약이다. Server/Database setter가 LoadSavedCredential()을 호출해
            // AuthMode·UserName을 저장소 값으로 되돌리므로, SSMS 값은 반드시 그 뒤에 얹는다.
            ServerName = info.ServerName;
            DatabaseName = info.DatabaseName;

            if (info.UnsupportedReason != null)
            {
                // 서버·DB는 쓸 수 있지만 인증은 사용자가 직접 지정해야 한다.
                // 이미 입력해 둔 인증 정보를 지우지 않는다.
                ConnectionSourceMessage = null;
                WarningMessage = info.UnsupportedReason;
                return true;
            }

            AuthMode = info.AuthMode;
            UserName = info.UserName;
            _password = info.Password;
            _passwordFromSsms = info.Password != null;

            ConnectionSourceMessage = _passwordFromSsms
                ? "SSMS 개체 탐색기 연결에서 가져왔습니다 (암호 포함). Connect를 누르세요."
                : "SSMS 개체 탐색기 연결에서 가져왔습니다. Connect를 누르세요.";
            return true;
        }
```

(g) `PersistCredential`을 교체:

```csharp
        /// <summary>
        /// 입력된 인증 정보를 저장소에 반영한다. 저장할 수 없으면 배너에 사유를 남기고 false.
        /// </summary>
        private bool PersistCredential()
        {
            try
            {
                if (_passwordFromSsms)
                {
                    // SSMS에서 가져온 암호는 디스크에 쓰지 않기로 했다.
                    // plainPassword: null은 "저장된 암호를 건드리지 않는다"이므로 인증 방식과
                    // 계정명만 파일에 남고, 암호는 세션 캐시가 이 프로세스 동안만 들고 있는다.
                    _credentialStore.Save(ServerName!, DatabaseName!, AuthMode, UserName, null);
                    _credentialStore.SetSessionPassword(ServerName!, DatabaseName!, _password);
                    return true;
                }

                bool fullySaved = _credentialStore.Save(
                    ServerName!, DatabaseName!, AuthMode, UserName, _password);

                if (AuthMode == SqlAuthMode.Sql && !fullySaved)
                {
                    WarningMessage =
                        "암호를 이 기계에 안전하게 저장하지 못했습니다(DPAPI를 사용할 수 없습니다). " +
                        "인증 정보가 저장되지 않았으므로 접속할 수 없습니다.";
                    return false;
                }
                return true;
            }
            finally
            {
                // 평문을 ViewModel에 남기지 않는다. 저장소가 보호된 형태로, 또는 세션 캐시가 들고 있다.
                _password = null;
                _passwordFromSsms = false;
            }
        }
```

(h) `ConnectCommand` 선언 위의 낡은 주석을 고치고 새 명령을 그 아래에 선언한다:

```csharp
        /// <summary>
        /// 입력된 서버/데이터베이스를 활성 컨텍스트로 적용한다.
        /// 입력란은 사용자가 직접 채우거나 <see cref="RefreshFromSsmsCommand"/>가 채운다.
        /// </summary>
        public ICommand ConnectCommand { get; }

        /// <summary>
        /// SSMS 개체 탐색기의 현재 연결을 입력란으로 가져온다.
        /// 도구 창을 개체 탐색기와 나란히 띄워 두면 가시성 이벤트가 뜨지 않으므로,
        /// 결정적인 수동 갱신 수단이 하나 필요하다.
        /// </summary>
        public ICommand RefreshFromSsmsCommand { get; }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests`
Expected: 전부 PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/Services/SsmsConnectionInfo.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): SSMS 연결을 입력란에 채우고 그 암호는 메모리에만 두도록 ViewModel을 확장"
```

---

### Task 5: `ObjectExplorerConnectionSource` (리플렉션 어댑터)와 배선

**Files:**
- Create: `src/DBVC.Vsix/Services/ObjectExplorerConnectionSource.cs`
- Modify: `src/DBVC.Vsix/DbvcServices.cs:77-82`
- Test: `tests/DBVC.Vsix.Tests/Services/ObjectExplorerConnectionSourceTests.cs`

**Interfaces:**
- Consumes: Task 3의 `SsmsUrn.TryGetDatabaseName`, Task 4의 `ISsmsConnectionSource`·`SsmsConnectionInfo`
- Produces: `public sealed class ObjectExplorerConnectionSource : ISsmsConnectionSource` (매개변수 없는 생성자)

**측정된 SSMS 21 타입 (설계 문서 2절):**

| 이름 | 어셈블리 |
| --- | --- |
| `Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache` (정적 속성 `ServiceProvider`, `IServiceProvider` 구현) | `Microsoft.SqlServer.SqlTools.VSIntegration` |
| `...ObjectExplorer.IObjectExplorerService` (`void GetSelectedNodes(out int, out INodeInformation[])`) | `SqlWorkbench.Interfaces` |
| `...ObjectExplorer.INodeContext` (`Connection`, `Context`) | `SqlWorkbench.Interfaces` |

연결 객체(`SqlOlapConnectionInfoBase`)는 `ServerName`·`UserName`·`Password`·`SecurePassword`·
`UseIntegratedSecurity`를 노출하고, 파생 타입에만 있는 `Authentication`은 `ActiveDirectory`로 시작하는
값이면 Entra ID다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

SSMS 프로세스 밖에서 검증할 수 있는 것은 "셸이 없을 때 조용히 실패한다" 하나뿐이다. 그것이 이
클래스에서 가장 중요한 계약이므로 테스트로 고정한다.

`tests/DBVC.Vsix.Tests/Services/ObjectExplorerConnectionSourceTests.cs`:

```csharp
using DBVC.Vsix.Services;
using NUnit.Framework;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class ObjectExplorerConnectionSourceTests
    {
        // 이 테스트는 SSMS 셸 밖에서 돈다. 리플렉션 대상 어셈블리가 아예 로드되어 있지 않은,
        // 어댑터가 반드시 견뎌야 하는 환경이다. 실제 개체 탐색기 읽기는 계획서의 수동 검증이 담당한다.

        [Test]
        public void Constructor_DoesNotTouchTheShell()
        {
            Assert.DoesNotThrow(() => new ObjectExplorerConnectionSource());
        }

        [Test]
        public void TryGetCurrent_ReturnsNull_WhenTheShellIsNotThere()
        {
            Assert.That(new ObjectExplorerConnectionSource().TryGetCurrent(), Is.Null,
                "자동 채움이 안 되는 것과 도구 창이 죽는 것은 비교할 문제가 아닙니다");
        }

        [Test]
        public void TryGetCurrent_CanBeCalledRepeatedly()
        {
            var source = new ObjectExplorerConnectionSource();

            Assert.DoesNotThrow(() =>
            {
                source.TryGetCurrent();
                source.TryGetCurrent();
            });
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter ObjectExplorerConnectionSourceTests`
Expected: 컴파일 실패 — `ObjectExplorerConnectionSource`를 찾을 수 없음

- [ ] **Step 3: 어댑터를 구현한다**

`src/DBVC.Vsix/Services/ObjectExplorerConnectionSource.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SSMS 개체 탐색기에서 선택된 노드의 연결을 읽는다.
    ///
    /// SSMS 어셈블리를 컴파일 타임에 참조하지 않는다. 그 어셈블리들은 SSMS 설치 폴더에만 있고
    /// GAC에 없으므로, 참조하면 (a) 빌드가 특정 SSMS 설치에 묶이고 — 이 저장소는 비Windows에서도
    /// 컴파일과 단위 테스트가 돌아간다 — (b) 어셈블리 버전이 고정되어 다음 SSMS에서 로드가 깨진다.
    /// 리플렉션은 두 문제를 모두 피하고 실패를 "자동 채움이 안 됨"으로 국한한다.
    ///
    /// 판단 로직을 여기에 두지 않는다. SSMS 밖에서 테스트할 수 없기 때문이다 —
    /// URN 파싱은 <see cref="SsmsUrn"/>로, 나머지는 속성 읽기와 얇은 분기로 유지한다.
    ///
    /// <b>UI 스레드에서만 호출한다.</b> <c>GetSelectedNodes</c>가 개체 탐색기 트리를 건드린다.
    /// 호출 지점(도구 창 가시성 이벤트, 갱신 명령)은 모두 이미 UI 스레드다.
    /// </summary>
    public sealed class ObjectExplorerConnectionSource : ISsmsConnectionSource
    {
        private const string VsIntegrationAssembly = "Microsoft.SqlServer.SqlTools.VSIntegration";
        private const string InterfacesAssembly = "SqlWorkbench.Interfaces";

        private const string ServiceCacheTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache";
        private const string ObjectExplorerServiceTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService";
        private const string NodeContextTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeContext";

        private const string EntraReason =
            "SSMS가 Microsoft Entra ID로 접속해 있습니다. DBVC는 토큰 기반 연결을 재사용할 수 없으니 " +
            "인증 방식과 계정을 직접 지정하세요.";

        public SsmsConnectionInfo? TryGetCurrent()
        {
            try
            {
                return Read();
            }
            catch (Exception ex)
            {
                // 어느 단계가 깨지든 결과는 "자동 채움 없음"이다. 도구 창은 계속 동작해야 한다.
                Debug.WriteLine($"ObjectExplorerConnectionSource.TryGetCurrent failed: {ex.Message}");
                return null;
            }
        }

        private static SsmsConnectionInfo? Read()
        {
            var serviceCacheType = FindType(VsIntegrationAssembly, ServiceCacheTypeName);
            var explorerServiceType = FindType(InterfacesAssembly, ObjectExplorerServiceTypeName);
            var nodeContextType = FindType(InterfacesAssembly, NodeContextTypeName);
            if (serviceCacheType == null || explorerServiceType == null || nodeContextType == null)
            {
                return null;   // SSMS 셸 밖이다.
            }

            var provider = serviceCacheType
                .GetProperty("ServiceProvider", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IServiceProvider;
            var explorer = provider?.GetService(explorerServiceType);
            if (explorer == null) return null;

            var getSelectedNodes = explorer.GetType().GetMethod("GetSelectedNodes");
            if (getSelectedNodes == null) return null;

            // void GetSelectedNodes(out int count, out INodeInformation[] nodes)
            var args = new object?[] { 0, null };
            getSelectedNodes.Invoke(explorer, args);

            int count = args[0] is int selected ? selected : 0;
            // 다중 선택은 어느 것을 뜻하는지 정할 근거가 없다. 아무것도 하지 않는다.
            if (count != 1 || !(args[1] is Array nodes) || nodes.Length < 1) return null;

            var node = nodes.GetValue(0);
            if (node == null || !nodeContextType.IsInstanceOfType(node)) return null;

            var urn = nodeContextType.GetProperty("Context")?.GetValue(node) as string;
            var databaseName = SsmsUrn.TryGetDatabaseName(urn);
            if (string.IsNullOrEmpty(databaseName)) return null;

            var connection = nodeContextType.GetProperty("Connection")?.GetValue(node);
            if (connection == null) return null;

            var serverName = ReadString(connection, "ServerName");
            if (string.IsNullOrEmpty(serverName)) return null;

            if (ReadBool(connection, "UseIntegratedSecurity"))
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, null);
            }

            // Authentication은 파생 타입(SqlConnectionInfo)에만 있다. 없으면 SQL 인증으로 본다.
            var authentication = connection.GetType().GetProperty("Authentication")
                ?.GetValue(connection)?.ToString();
            if (authentication != null && authentication.StartsWith("ActiveDirectory", StringComparison.Ordinal))
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, EntraReason);
            }

            return new SsmsConnectionInfo(
                serverName!,
                databaseName!,
                SqlAuthMode.Sql,
                ReadString(connection, "UserName"),
                ReadPassword(connection),
                null);
        }

        /// <summary>
        /// 로드된 어셈블리에서 타입을 찾는다. SSMS 프로세스 안에서는 이미 로드되어 있으므로
        /// 파일을 직접 로드하지 않는다 — 설치 경로를 추측하지 않아도 되고, 셸 밖에서는
        /// 자연스럽게 <c>null</c>이 된다.
        /// </summary>
        private static Type? FindType(string assemblySimpleName, string typeName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase));
            return assembly?.GetType(typeName, throwOnError: false);
        }

        private static string? ReadString(object instance, string propertyName)
            => instance.GetType().GetProperty(propertyName)?.GetValue(instance) as string;

        private static bool ReadBool(object instance, string propertyName)
            => instance.GetType().GetProperty(propertyName)?.GetValue(instance) is bool value && value;

        /// <summary>
        /// 평문 <c>Password</c>가 비어 있으면 <c>SecurePassword</c>에서 되돌린다.
        /// SSMS가 암호를 들고 있지 않을 수도 있으므로 <c>null</c>은 정상 결과다.
        /// </summary>
        private static string? ReadPassword(object connection)
        {
            var password = ReadString(connection, "Password");
            if (!string.IsNullOrEmpty(password)) return password;

            if (!(connection.GetType().GetProperty("SecurePassword")?.GetValue(connection) is SecureString secure)
                || secure.Length == 0)
            {
                return null;
            }

            IntPtr pointer = IntPtr.Zero;
            try
            {
                pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(pointer);
                }
            }
        }
    }
}
```

- [ ] **Step 4: `DbvcServices`에 배선한다**

`src/DBVC.Vsix/DbvcServices.cs`의 `CreateViewChangesViewModel`을 교체:

```csharp
        /// <summary>
        /// 도구 창이 쓸 ViewModel을 만든다. SSMS 개체 탐색기 연동은 기본으로 켠다 —
        /// 셸 밖에서는 어댑터가 <c>null</c>을 돌려줄 뿐이므로 안전하다.
        /// </summary>
        public ViewChangesViewModel CreateViewChangesViewModel(
            IUserNotifier? notifier = null,
            ISsmsConnectionSource? ssmsConnectionSource = null)
        {
            return new ViewChangesViewModel(
                ConfigManager, StateTracker, GitManager, SmoManager, notifier,
                credentialStore: CredentialStore,
                ssmsConnectionSource: ssmsConnectionSource ?? new ObjectExplorerConnectionSource());
        }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Vsix.Tests`
Expected: 전부 PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/Services/ObjectExplorerConnectionSource.cs src/DBVC.Vsix/DbvcServices.cs tests/DBVC.Vsix.Tests/Services/ObjectExplorerConnectionSourceTests.cs
git commit -m "feat(vsix): 개체 탐색기 연결을 리플렉션으로 읽는 어댑터를 추가"
```

---

### Task 6: UI 배선과 문서

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml:22-52`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs:36-46`
- Modify: `README.md:62` 아래

**Interfaces:**
- Consumes: Task 4의 `RefreshFromSsmsCommand`, `ConnectionSourceMessage`, `HasConnectionSourceMessage`, `TryFillFromSsms()`
- Produces: 없음 (마지막 작업)

- [ ] **Step 1: XAML에 버튼과 안내 줄을 넣는다**

`Grid.Row="0"`의 `WrapPanel`을 `StackPanel`로 감싼다. 행을 추가하면 아래 두 행의 `Grid.Row`를
전부 밀어야 하므로 그렇게 하지 않는다. 22번째 줄의 `<WrapPanel Grid.Row="0" ...>`를 다음으로 바꾼다:

```xml
        <StackPanel Grid.Row="0">
            <WrapPanel Orientation="Horizontal" Margin="5,5,5,0">
```

그리고 `Connect` 버튼(51번째 줄) 뒤에 갱신 버튼을 더한다:

```xml
                <Button Content="Connect" Command="{Binding ConnectCommand}" Width="80" Margin="0,0,6,4"/>
                <Button Content="SSMS 연결" Command="{Binding RefreshFromSsmsCommand}" Width="90" Margin="0,0,0,4"
                        ToolTip="SSMS 개체 탐색기에서 선택한 데이터베이스의 연결 정보를 가져옵니다. 가져온 암호는 디스크에 저장되지 않습니다."/>
```

`</WrapPanel>`(52번째 줄) 뒤에 안내 줄과 닫는 태그를 넣는다:

```xml
            </WrapPanel>

            <!--
                SSMS에서 가져온 암호는 PasswordBox에 넣지 않는다(넣으면 Password setter를 타서
                디스크 저장 경로로 새어 나간다). 그래서 암호 칸이 비어 있는데 암호는 실려 있는
                상태가 되므로, 그 사실을 여기서 알린다.
            -->
            <TextBlock Text="{Binding ConnectionSourceMessage}" Foreground="#3A6B35"
                       TextWrapping="Wrap" Margin="5,0,5,4"
                       Visibility="{Binding HasConnectionSourceMessage, Converter={StaticResource BoolToVis}}"/>
        </StackPanel>
```

- [ ] **Step 2: 코드 비하인드에 가시성 트리거를 건다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` 생성자의 `_viewModel.SelectionChanged += OnSelectionChanged;` 아래에 추가:

```csharp
            IsVisibleChanged += OnIsVisibleChanged;
```

같은 생성자의 `Unloaded` 람다 안에 해제를 추가:

```csharp
                IsVisibleChanged -= OnIsVisibleChanged;
```

`OnSqlPasswordChanged` 위에 핸들러를 추가:

```csharp
        /// <summary>
        /// 도구 창이 보여질 때 SSMS 개체 탐색기의 현재 연결을 입력란으로 가져온다.
        /// 처음 열 때와 다른 탭에서 돌아올 때를 함께 덮는다. 개체 탐색기와 나란히 도킹해 두어
        /// 이 이벤트가 뜨지 않는 배치는 'SSMS 연결' 버튼이 담당한다.
        ///
        /// 반환값을 무시하는 것은 의도다 — 가져올 연결이 없으면 입력란이 그대로인 것이 정상이다.
        /// </summary>
        private void OnIsVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible)
            {
                _viewModel.TryFillFromSsms();
            }
        }
```

- [ ] **Step 3: 빌드와 전체 테스트를 확인한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests && dotnet test tests/DBVC.Vsix.Tests`
Expected: 빌드 성공, 전부 PASS

- [ ] **Step 4: README를 갱신한다**

`README.md`의 62번째 줄(SQL 인증 설명 문단) **바로 뒤**에 새 문단을 넣는다:

```markdown
**SSMS 개체 탐색기의 연결을 그대로 가져올 수 있습니다.** 개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 선택한 상태로 DBVC 창을 열거나 **SSMS 연결** 버튼을 누르면 서버·데이터베이스·인증 방식·계정이 채워지고, SSMS가 들고 있는 SQL 인증 암호까지 함께 실립니다. 이렇게 가져온 암호는 **디스크에 저장되지 않고 SSMS를 닫으면 사라집니다** — `credentials.json`에는 인증 방식과 계정명만 남습니다. Microsoft Entra ID로 접속한 연결은 토큰 기반이라 재사용할 수 없으며, 이 경우 서버·데이터베이스만 채우고 안내를 표시합니다.
```

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs README.md
git commit -m "feat(vsix): 도구 창이 보일 때 SSMS 연결을 자동으로 채우고 안내를 표시"
```

- [ ] **Step 6: SSMS 21에서 수동 검증한다**

단위 테스트가 덮지 못하는 부분(리플렉션 경로)은 여기서만 확인된다. `dotnet build DBVC.slnx -c Release`로
만든 `.vsix`를 설치하고 순서대로 확인한다.

1. 개체 탐색기에서 **SQL 인증**으로 서버에 접속하고 데이터베이스 노드를 선택 → DBVC 창을 연다
   → 서버·DB·인증 방식·계정이 채워지고 "(암호 포함)" 안내가 보인다
2. **Connect** → 접속 성공, 변경 목록이 뜬다
3. `%APPDATA%\DBVC\credentials.json`을 열어 **해당 항목에 암호 필드가 없는지** 확인 —
   이번 작업의 핵심 계약이다
4. SSMS를 재시작하고 개체 탐색기에 접속하지 않은 채 Connect → 예외 안내에 "개체 탐색기" 문구가 보인다
5. **Windows 인증** 연결에서도 서버·DB가 채워지고 접속되는지
6. 도구 창을 개체 탐색기와 나란히 띄운 채 다른 DB를 선택 → **SSMS 연결** 버튼으로 값이 바뀌는지
7. 서버 노드(데이터베이스 아님)를 선택한 상태로 버튼을 눌러 → 입력란이 그대로인지
8. 암호를 입력하던 중 다른 탭에 갔다 돌아와도 입력값이 지워지지 않는지
9. (가능하면) Entra ID로 접속한 서버를 선택 → 경고가 뜨고 도구 창이 정상 동작하는지

- [ ] **Step 7: 검증에서 발견한 문제가 있으면 고치고 커밋한다**

수동 검증은 리플렉션 경로의 유일한 검증 수단이다. 실패하면 그 지점을 `Debug.WriteLine` 출력
(디버그 출력 창)으로 좁힌 뒤 고친다. 문제가 없었다면 이 단계는 건너뛴다.

---

## 자기 검토 결과

**스펙 커버리지**

| 스펙 절 | 담당 |
| --- | --- |
| 4.1 전체 흐름 | Task 1–6 |
| 4.2 어댑터 / 인증 방식 판정 표 | Task 5 |
| 4.3.1 `SessionPasswordCache` | Task 1 |
| 4.3.2 인터페이스 확장·조회 순서·무효화 표 | Task 2 |
| 4.3.3 DPAPI 부재 시 동작 | Task 4 Step 4(g) — SSMS 분기가 `fullySaved` 가드보다 앞에 있다 |
| 4.4 URN 파서 | Task 3 |
| 4.5 ViewModel (출처 추적·채움 순서·저장 분기·갱신 명령) | Task 4 |
| 4.6 가시성 트리거 | Task 6 Step 2 |
| 4.7 예외 문구 | Task 2 Step 5 |
| 4.8 `DbvcServices` 배선 | Task 5 Step 4 |
| 5 오류 처리 표 | Task 3·4·5의 테스트, Task 6의 수동 검증 6–9 |
| 6 테스트 전략 | 각 Task의 Step 1 |

**스펙에 없는 추가 항목:** Task 6 Step 4의 README 문단. 사용자에게 보이는 동작이 바뀌는데 스펙
3절에는 문서 갱신이 없어 명시해 둔다. 불필요하면 이 단계만 빼면 된다.

**타입 일관성:** `TryFillFromSsms`·`ConnectionSourceMessage`·`HasConnectionSourceMessage`·
`RefreshFromSsmsCommand`·`SetSessionPassword`·`TryGetCurrent`·`TryGetDatabaseName`의 이름과
시그니처가 Task 2·3·4·5·6에서 동일하다. `SsmsConnectionInfo` 생성자 인자 순서
(server, database, authMode, userName, password, unsupportedReason)는 Task 4의 정의와
Task 5의 세 호출 지점, Task 4 테스트의 두 호출 지점에서 일치한다.
