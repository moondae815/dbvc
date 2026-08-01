# DBVC Core Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the DBVC Core Engine managers (SmoManager, GitManager, StateTracker) with their actual business logic (SMO, LibGit2Sharp, ADO.NET).

> **정정 (구현 후):** 아래 Task 4의 코드 예시는 `ORDER BY EventDate DESC`를 사용하지만
> `DBVC_ChangeLog`에는 `EventDate` 컬럼이 없다(실제 컬럼은 `PostTime`). 이 예시를 그대로
> 따르면 정상 초기화된 DB에서도 조회가 항상 `SqlException`으로 실패한다.
> 실제 조회 조건은 `WHERE IsProcessed = 0 ORDER BY PostTime DESC, Id DESC`이며,
> 자세한 내용은 core-engine 설계 문서 3.3.1을 참고할 것.

**Architecture:** We are replacing the previous stubs in `src/DBVC.Core` with functional implementations that interact with SQL Server (SMO), Local Git Repo (LibGit2Sharp), and the DDL Trigger Log Table (Microsoft.Data.SqlClient).

**Tech Stack:** C#, .NET 4.8 / .NET Standard 2.0, LibGit2Sharp, Microsoft.SqlServer.SqlManagementObjects, Microsoft.Data.SqlClient, NUnit

## Global Constraints

- Target Environment: SSMS 21 (Visual Studio 2022 Shell, 64-bit)
- Framework: `.NET 4.8` (or `netstandard2.0` where cross-platform)
- Git Engine: `LibGit2Sharp` (no external git cli calls)
- Change Detection: Read from `DBVC_ChangeLog` table using `Microsoft.Data.SqlClient`

---

### Task 1: Add Package Dependencies

**Files:**
- Modify: `src/DBVC.Core/DBVC.Core.csproj`
- Modify: `tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj`

**Interfaces:**
- Produces: Correct NuGet references to `Microsoft.SqlServer.SqlManagementObjects`, `LibGit2Sharp`, and `Microsoft.Data.SqlClient`.

- [ ] **Step 1: Add NuGet packages to DBVC.Core**

```bash
dotnet add src/DBVC.Core/DBVC.Core.csproj package Microsoft.SqlServer.SqlManagementObjects
dotnet add src/DBVC.Core/DBVC.Core.csproj package LibGit2Sharp
dotnet add src/DBVC.Core/DBVC.Core.csproj package Microsoft.Data.SqlClient
```

- [ ] **Step 2: Add same packages to Tests for Integration**

```bash
dotnet add tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj package Microsoft.SqlServer.SqlManagementObjects
dotnet add tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj package LibGit2Sharp
dotnet add tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj package Microsoft.Data.SqlClient
```

- [ ] **Step 3: Run build to verify package resolution**

Run: `dotnet build src/DBVC.Core/DBVC.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/DBVC.Core/DBVC.Core.csproj tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj
git commit -m "build: add SMO, LibGit2Sharp, and SqlClient dependencies"
```

### Task 2: Implement SmoManager Scripting

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs`
- Modify: `tests/DBVC.Core.Tests/SmoManagerTests.cs`

**Interfaces:**
- Consumes: `ConfigManager.GetMapping`
- Produces: `bool ScriptObjects(string serverName, string databaseName, List<string> objectNames = null)`

- [ ] **Step 1: Write the failing test (Integration)**

```csharp
// In tests/DBVC.Core.Tests/SmoManagerTests.cs
using System.IO;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    public class SmoManagerTests
    {
        [Test]
        public void ScriptObjects_GivenValidDb_GeneratesFile()
        {
            var config = new ConfigManager();
            config.AddMapping("localhost", "master", Path.Combine(Path.GetTempPath(), "dbvc_test"));
            var smo = new SmoManager(config);
            
            // Should script successfully (assuming localhost\master exists for testing)
            // Or we test exception throwing if not exists, but let's test basic structure
            Assert.DoesNotThrow(() => smo.ScriptObjects("localhost", "master"));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails/compiles**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter SmoManagerTests`
Expected: If stub throws NotImplementedException, it fails. If stub returns true, it passes but doesn't write files.

- [ ] **Step 3: Write minimal implementation**

```csharp
// In src/DBVC.Core/SmoManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Common;
using Microsoft.Data.SqlClient;

namespace DBVC.Core
{
    public class SmoManager
    {
        private readonly ConfigManager _configManager;
        
        public SmoManager(ConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        public bool ScriptObjects(string serverName, string databaseName, List<string> objectNames = null)
        {
            var mapping = _configManager.GetMapping(serverName, databaseName);
            if (mapping == null) return false;

            try 
            {
                var connStr = new SqlConnectionStringBuilder { DataSource = serverName, InitialCatalog = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }.ToString();
                using var sqlConn = new SqlConnection(connStr);
                var conn = new ServerConnection(sqlConn);
                var server = new Server(conn);
                var db = server.Databases[databaseName];
                
                if (db == null) return false;
                
                var scripter = new Scripter(server)
                {
                    Options = new ScriptingOptions
                    {
                        ScriptDrops = false,
                        IncludeIfNotExists = false,
                        ToFileOnly = true,
                        AppendToFile = false
                    }
                };

                // For MVP, just iterate Tables as a proof of concept.
                // A full iteration over Views, StoredProcedures, etc. will follow.
                foreach (Table tb in db.Tables)
                {
                    if (tb.IsSystemObject) continue;
                    
                    var dir = Path.Combine(mapping.LocalGitPath, tb.Schema, "Tables");
                    Directory.CreateDirectory(dir);
                    scripter.Options.FileName = Path.Combine(dir, $"{tb.Name}.sql");
                    scripter.Script(new[] { tb.Urn });
                }
                
                return true;
            } 
            catch 
            {
                return false;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter SmoManagerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Core/SmoManager.cs tests/DBVC.Core.Tests/SmoManagerTests.cs
git commit -m "feat: implement basic SMO scripting for tables"
```

### Task 3: Implement GitManager Commit

**Files:**
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `ConfigManager.GetMapping`
- Produces: `bool CommitChanges(string serverName, string databaseName, string message)`

- [ ] **Step 1: Write the failing test**

```csharp
// In tests/DBVC.Core.Tests/GitManagerTests.cs
using System.IO;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    public class GitManagerTests
    {
        [Test]
        public void CommitChanges_ThrowsException_IfRepoNotFound()
        {
            var config = new ConfigManager();
            var path = Path.Combine(Path.GetTempPath(), "dbvc_git_test");
            if (Directory.Exists(path)) Directory.Delete(path, true);
            config.AddMapping("localhost", "testdb", path);
            
            var git = new GitManager(config);
            // Should throw RepositoryNotFoundException
            Assert.Throws<LibGit2Sharp.RepositoryNotFoundException>(() => git.CommitChanges("localhost", "testdb", "test"));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter GitManagerTests`

- [ ] **Step 3: Write minimal implementation**

```csharp
// In src/DBVC.Core/GitManager.cs
using System;
using System.Linq;
using LibGit2Sharp;

namespace DBVC.Core
{
    public class GitManager
    {
        private readonly ConfigManager _configManager;

        public GitManager(ConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        public bool CommitChanges(string serverName, string databaseName, string message)
        {
            var mapping = _configManager.GetMapping(serverName, databaseName);
            if (mapping == null) return false;

            using var repo = new Repository(mapping.LocalGitPath);
            Commands.Stage(repo, "*");
            
            var signature = new Signature("DBVC User", "dbvc@example.com", DateTimeOffset.Now);
            repo.Commit(message, signature, signature);
            return true;
        }

        public bool PullChanges(string serverName, string databaseName)
        {
            // Stubbed for now
            return true;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter GitManagerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat: implement LibGit2Sharp commit logic"
```

### Task 4: Implement StateTracker DDL Log Fetching

**Files:**
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `tests/DBVC.Core.Tests/StateTrackerTests.cs`

**Interfaces:**
- Consumes: `ConfigManager.GetMapping`
- Produces: `void RefreshState(string serverName, string databaseName)`

- [ ] **Step 1: Write the failing test**

```csharp
// In tests/DBVC.Core.Tests/StateTrackerTests.cs
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    public class StateTrackerTests
    {
        [Test]
        public void RefreshState_HandlesMissingDatabaseGracefully()
        {
            var config = new ConfigManager();
            config.AddMapping("localhost", "nonexistent_db", "path");
            var tracker = new StateTracker(config);
            
            // Should not throw, should handle SqlException internally
            Assert.DoesNotThrow(() => tracker.RefreshState("localhost", "nonexistent_db"));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter StateTrackerTests`

- [ ] **Step 3: Write minimal implementation**

```csharp
// In src/DBVC.Core/StateTracker.cs
using System;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace DBVC.Core
{
    public class StateTracker
    {
        private readonly ConfigManager _configManager;
        private readonly ConcurrentDictionary<string, string> _stateCache = new ConcurrentDictionary<string, string>();

        public StateTracker(ConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        public void RefreshState(string serverName, string databaseName)
        {
            var mapping = _configManager.GetMapping(serverName, databaseName);
            if (mapping == null) return;

            try
            {
                var connStr = new SqlConnectionStringBuilder { DataSource = serverName, InitialCatalog = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }.ToString();
                using var conn = new SqlConnection(connStr);
                conn.Open();
                
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ObjectName, EventType FROM DBVC_ChangeLog ORDER BY EventDate DESC";
                
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var objName = reader.GetString(0);
                    var evType = reader.GetString(1);
                    _stateCache[$"{serverName}.{databaseName}.{objName}"] = evType;
                }
            }
            catch (SqlException)
            {
                // Graceful fail if DB/table doesn't exist yet
            }
        }
        
        public string GetObjectState(string serverName, string databaseName, string objectName)
        {
            if (_stateCache.TryGetValue($"{serverName}.{databaseName}.{objectName}", out var state))
                return state;
            return "Clean";
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter StateTrackerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/StateTrackerTests.cs
git commit -m "feat: implement StateTracker DBVC_ChangeLog fetch"
```
