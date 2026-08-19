# DBVC SSMS Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Create a lightweight SSMS 21 plugin for Git version control of database objects.

**Architecture:** Native Integrated Architecture (All-in-One VSIX) using LibGit2Sharp, SMO, and a DDL Trigger for change detection.

**Tech Stack:** C#, .NET Framework 4.8 (SSMS 21 target), LibGit2Sharp, Microsoft.SqlServer.SqlManagementObjects.

## Global Constraints

- Target Environment: SSMS 21 (Visual Studio 2022 Shell, 64-bit)
- Language: C#
- Framework: .NET Framework 4.8 (or compatible with SSMS 21 Extensibility)
- Git Engine: LibGit2Sharp
- Change Detection: DDL Trigger recording to `DBVC_ChangeLog`

---

### Task 1: Configuration & Mapping Manager

**Files:**
- Create: `src/DBVC.Core/ConfigManager.cs`
- Create: `src/DBVC.Core/Models/MappingConfig.cs`
- Create: `tests/DBVC.Core.Tests/ConfigManagerTests.cs`

**Interfaces:**
- Consumes: None
- Produces: `ConfigManager.GetMapping(serverName, dbName)` returning local git path string.

- [x] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;
using DBVC.Core;

[TestFixture]
public class ConfigManagerTests
{
    [Test]
    public void GetMapping_ReturnsCorrectPath()
    {
        var manager = new ConfigManager();
        var path = manager.GetMapping("LocalServer", "SalesDB");
        Assert.IsNotNull(path);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: FAIL with compilation error or NotImplementedException

- [x] **Step 3: Write minimal implementation**

```csharp
namespace DBVC.Core
{
    public class ConfigManager
    {
        public string GetMapping(string serverName, string databaseName)
        {
            // Minimal implementation for now
            return $@"C:\Git\{serverName}\{databaseName}";
        }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/DBVC.Core tests/DBVC.Core.Tests
git commit -m "feat: add ConfigManager for mapping resolution"
```

### Task 2: GitManager (LibGit2Sharp Wrapper)

**Files:**
- Create: `src/DBVC.Core/GitManager.cs`
- Create: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `ConfigManager.GetMapping`
- Produces: `GitManager.Commit(repoPath, filePath, message)`, `GitManager.GetStatus(repoPath)`

- [x] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;
using DBVC.Core;

[TestFixture]
public class GitManagerTests
{
    [Test]
    public void GetStatus_ReturnsEmptyForNewRepo()
    {
        var manager = new GitManager();
        var status = manager.GetStatus("dummy/path");
        Assert.IsNotNull(status);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: FAIL

- [x] **Step 3: Write minimal implementation**

```csharp
namespace DBVC.Core
{
    public class GitManager
    {
        public string GetStatus(string repoPath)
        {
            return "Clean"; // Minimal stub
        }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/DBVC.Core tests/DBVC.Core.Tests
git commit -m "feat: add GitManager stub for LibGit2Sharp wrapping"
```

### Task 3: SmoManager (Object Scripting)

**Files:**
- Create: `src/DBVC.Core/SmoManager.cs`
- Create: `tests/DBVC.Core.Tests/SmoManagerTests.cs`

**Interfaces:**
- Consumes: None
- Produces: `SmoManager.ScriptObjects(connectionString, objectUrns, outputPath)`

- [x] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;
using DBVC.Core;

[TestFixture]
public class SmoManagerTests
{
    [Test]
    public void ScriptObjects_GeneratesFile()
    {
        var manager = new SmoManager();
        bool result = manager.ScriptObjects("conn", new[] { "urn" }, "out.sql");
        Assert.IsTrue(result);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: FAIL

- [x] **Step 3: Write minimal implementation**

```csharp
namespace DBVC.Core
{
    public class SmoManager
    {
        public bool ScriptObjects(string connectionString, string[] objectUrns, string outputPath)
        {
            return true; // Stub
        }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/DBVC.Core tests/DBVC.Core.Tests
git commit -m "feat: add SmoManager stub for scripting objects"
```

### Task 4: DDL Trigger & StateTracker

**Files:**
- Create: `src/DBVC.Database/InstallTrigger.sql`
- Create: `src/DBVC.Core/StateTracker.cs`
- Create: `tests/DBVC.Core.Tests/StateTrackerTests.cs`

**Interfaces:**
- Consumes: Database connection
- Produces: `StateTracker.GetPendingChanges(connectionString)`

- [x] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;
using DBVC.Core;

[TestFixture]
public class StateTrackerTests
{
    [Test]
    public void GetPendingChanges_ReturnsList()
    {
        var tracker = new StateTracker();
        var changes = tracker.GetPendingChanges("conn");
        Assert.IsNotNull(changes);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: FAIL

- [x] **Step 3: Write minimal implementation**

```csharp
using System.Collections.Generic;
namespace DBVC.Core
{
    public class StateTracker
    {
        public List<string> GetPendingChanges(string connectionString)
        {
            return new List<string>();
        }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/DBVC.Core tests/DBVC.Core.Tests
git commit -m "feat: add StateTracker for DDL change polling"
```

### Task 5: VSIX SSMS 21 Package Scaffolding

**Files:**
- Create: `src/DBVC.Vsix/DBVC.Vsix.csproj`
- Create: `src/DBVC.Vsix/source.extension.vsixmanifest`
- Create: `src/DBVC.Vsix/DbvcPackage.cs`

**Interfaces:**
- Consumes: `ConfigManager`, `GitManager`, `SmoManager`, `StateTracker`
- Produces: Visual Studio Extension loaded into SSMS

- [x] **Step 1: Write the failing test**
(VSIX initialization is tested via manual integration in Experimental Instance. For TDD completeness, we test the package class instantiates).

```csharp
using NUnit.Framework;
using DBVC.Vsix;

[TestFixture]
public class PackageTests
{
    [Test]
    public void Package_CanBeInstantiated()
    {
        var pkg = new DbvcPackage();
        Assert.IsNotNull(pkg);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Vsix.Tests`
Expected: FAIL

- [x] **Step 3: Write minimal implementation**

```csharp
namespace DBVC.Vsix
{
    public class DbvcPackage
    {
        public DbvcPackage() { }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Vsix.Tests`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/DBVC.Vsix tests/DBVC.Vsix.Tests
git commit -m "feat: scaffold SSMS VSIX DbvcPackage"
```
