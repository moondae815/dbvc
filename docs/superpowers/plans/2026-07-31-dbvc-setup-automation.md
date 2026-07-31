# DBVC Setup Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automate DBVC_ChangeLog and DDL trigger initialization from the SSMS VSIX UI using an embedded SQL script.

**Architecture:** Embed `InstallTrigger.sql` in `DBVC.Core`. `StateTracker` adds `IsInitialized()` and `InitializeDatabase()` using ADO.NET. The WPF UI overlays a "Setup DBVC" screen bound to a new `SetupCommand` when initialization is missing.

**Tech Stack:** .NET 4.8 / .NET Standard 2.0, ADO.NET (Microsoft.Data.SqlClient), WPF (MVVM)

## Global Constraints

- Target Environment: SSMS 21 (Visual Studio 2022 Shell, 64-bit)
- Framework: `.NET 4.8` (or `netstandard2.0` where cross-platform)

---

### Task 1: Core Initialization Logic

**Files:**
- Modify: `src/DBVC.Core/DBVC.Core.csproj`
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `tests/DBVC.Core.Tests/StateTrackerTests.cs`

**Interfaces:**
- Consumes: `src/DBVC.Database/InstallTrigger.sql` (embedded resource)
- Produces: `bool StateTracker.IsInitialized(string connectionString)`, `void StateTracker.InitializeDatabase(string connectionString)`

- [ ] **Step 1: Write the failing tests**

```csharp
// Append to tests/DBVC.Core.Tests/StateTrackerTests.cs
using NUnit.Framework;

[TestFixture]
public partial class StateTrackerTests
{
    [Test]
    public void IsInitialized_ReturnsFalse_WhenNoTable()
    {
        var tracker = new DBVC.Core.StateTracker();
        Assert.IsFalse(tracker.IsInitialized("fake_connection_string"));
    }

    [Test]
    public void InitializeDatabase_DoesNotThrow()
    {
        var tracker = new DBVC.Core.StateTracker();
        Assert.Throws<System.ArgumentException>(() => tracker.InitializeDatabase(""));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: FAIL (missing methods)

- [ ] **Step 3: Embed the SQL script**

```xml
<!-- In src/DBVC.Core/DBVC.Core.csproj, inside the existing <ItemGroup> or a new one -->
  <ItemGroup>
    <EmbeddedResource Include="..\DBVC.Database\InstallTrigger.sql" LogicalName="InstallTrigger.sql" />
  </ItemGroup>
```

- [ ] **Step 4: Write minimal implementation**

```csharp
// In src/DBVC.Core/StateTracker.cs
using System.IO;
using System.Reflection;

// Add methods to StateTracker class:
public bool IsInitialized(string connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return false;
    try
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type in (N'U')";
        return ((int)cmd.ExecuteScalar()) > 0;
    }
    catch
    {
        return false;
    }
}

public void InitializeDatabase(string connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString)) throw new System.ArgumentException("Invalid connection string", nameof(connectionString));
    
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InstallTrigger.sql");
    if (stream == null) throw new FileNotFoundException("InstallTrigger.sql not found in embedded resources.");
    using var reader = new StreamReader(stream);
    var script = reader.ReadToEnd();
    
    var batches = script.Split(new[] { "\r\nGO", "\nGO", "GO\r\n", "GO\n" }, System.StringSplitOptions.RemoveEmptyEntries);
    
    using var conn = new SqlConnection(connectionString);
    conn.Open();
    foreach (var batch in batches)
    {
        if (string.IsNullOrWhiteSpace(batch)) continue;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = batch;
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/DBVC.Core/DBVC.Core.csproj src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/StateTrackerTests.cs
git commit -m "feat: add IsInitialized and InitializeDatabase to StateTracker"
```

---

### Task 2: ViewModel Updates

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `StateTracker.InitializeDatabase`
- Produces: `bool ViewChangesViewModel.IsInitialized`, `ICommand ViewChangesViewModel.SetupCommand`

- [ ] **Step 1: Write the failing tests**

```csharp
// Append to tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
[Test]
public void IsInitialized_DefaultsToFalse()
{
    var vm = new ViewChangesViewModel();
    Assert.IsFalse(vm.IsInitialized);
}

[Test]
public void SetupCommand_SetsIsInitializedToTrue()
{
    var vm = new ViewChangesViewModel();
    vm.SetupCommand.Execute(null);
    Assert.IsTrue(vm.IsInitialized);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: FAIL (missing properties)

- [ ] **Step 3: Write minimal implementation**

```csharp
// In src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs

// Add properties:
private bool _isInitialized;
public bool IsInitialized
{
    get => _isInitialized;
    set
    {
        _isInitialized = value;
        OnPropertyChanged();
    }
}

public System.Windows.Input.ICommand SetupCommand { get; }

// Modify constructor:
public ViewChangesViewModel()
{
    RefreshCommand = new Commands.RelayCommand(Refresh);
    SetupCommand = new Commands.RelayCommand(Setup);
}

// Add method:
private void Setup()
{
    // Real implementation will call StateTracker.InitializeDatabase(connString)
    IsInitialized = true;
    Refresh();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat: add IsInitialized and SetupCommand to ViewChangesViewModel"
```

---

### Task 3: UI Updates (Overlay)

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Create: `src/DBVC.Vsix/UI/InverseBooleanToVisibilityConverter.cs`

**Interfaces:**
- Consumes: `ViewChangesViewModel.IsInitialized`, `ViewChangesViewModel.SetupCommand`
- Produces: Overlay grid in XAML.

- [ ] **Step 1: Create Inverse Converter**

```csharp
// src/DBVC.Vsix/UI/InverseBooleanToVisibilityConverter.cs
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DBVC.Vsix.UI
{
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b) return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
```

- [ ] **Step 2: Update XAML**

```xml
<!-- In src/DBVC.Vsix/UI/ViewChangesControl.xaml -->
<!-- Add to UserControl tag: xmlns:local="clr-namespace:DBVC.Vsix.UI" -->
<!-- Add Resources right inside <UserControl> -->
<UserControl.Resources>
    <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    <local:InverseBooleanToVisibilityConverter x:Key="InverseBoolToVis"/>
</UserControl.Resources>

<!-- Wrap the existing Grid in a parent Grid -->
<Grid>
    <Grid Visibility="{Binding IsInitialized, Converter={StaticResource BoolToVis}}">
        <!-- ALL existing grid rows (Top Area, Middle Area, Bottom Area) go here -->
    </Grid>

    <!-- The Setup Overlay -->
    <Grid Visibility="{Binding IsInitialized, Converter={StaticResource InverseBoolToVis}}" Background="#F0F0F0">
        <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
            <TextBlock Text="This database is not initialized for DBVC." FontSize="16" Margin="0,0,0,20" Foreground="#333333"/>
            <Button Content="Setup DBVC" Command="{Binding SetupCommand}" Width="150" Height="40" FontSize="14" Cursor="Hand"/>
        </StackPanel>
    </Grid>
</Grid>
```

- [ ] **Step 3: Compile to verify**

Run: `dotnet build src/DBVC.Vsix`
Expected: Build SUCCESS

- [ ] **Step 4: Commit**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/UI/InverseBooleanToVisibilityConverter.cs
git commit -m "feat: add DBVC setup overlay UI"
```
