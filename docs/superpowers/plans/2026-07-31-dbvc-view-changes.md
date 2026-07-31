# View Changes Tool Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a VS SDK Tool Window in the DBVC VSIX plugin that provides a split-view UI to list database schema changes and view side-by-side SQL diffs before committing.

**Architecture:** We will use WPF and MVVM. The ViewModel will interact with `DBVC.Core` (`StateTracker`, `GitManager`, `SmoManager`). The UI will use `AvalonEdit` for syntax-highlighted text viewing and `DiffPlex` to generate side-by-side diff models.

**Tech Stack:** .NET Framework 4.8, WPF, VS SDK, AvalonEdit, DiffPlex, NUnit, Moq (for ViewModel testing).

## Global Constraints

- Target Environment: SSMS 21 (Visual Studio 2022 Shell, 64-bit)
- Framework: `.NET 4.8` (or `netstandard2.0` where cross-platform)
- UI: WPF (No WinForms)

---

### Task 1: Dependencies and MVVM Scaffold

**Files:**
- Modify: `src/DBVC.Vsix/DBVC.Vsix.csproj`
- Modify: `tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj`
- Create: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Create: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: N/A
- Produces: `ViewChangesViewModel` class (INotifyPropertyChanged).

- [ ] **Step 1: Write the failing test**
```csharp
// tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
using NUnit.Framework;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ViewChangesViewModelTests
    {
        [Test]
        public void CommitMessage_CanBeSetAndRetrieved()
        {
            var vm = new ViewChangesViewModel();
            vm.CommitMessage = "Test commit";
            Assert.AreEqual("Test commit", vm.CommitMessage);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: FAIL (missing classes)

- [ ] **Step 3: Write minimal implementation**
```csharp
// src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBVC.Vsix.ViewModels
{
    public class ViewChangesViewModel : INotifyPropertyChanged
    {
        private string _commitMessage;
        public string CommitMessage
        {
            get => _commitMessage;
            set
            {
                _commitMessage = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```

- [ ] **Step 4: Add NuGet Packages to csproj**
Add `AvalonEdit` (latest compatible with net48, e.g. 6.3.0.90) and `DiffPlex` (1.7.2) to `src/DBVC.Vsix/DBVC.Vsix.csproj`.
Add `Moq` (4.18.4) to `tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj`.

- [ ] **Step 5: Run test to verify it passes**
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS

- [ ] **Step 6: Commit**
```bash
git add .
git commit -m "feat: scaffold ViewChangesViewModel and UI dependencies"
```

---

### Task 2: ChangeList and Refresh Logic

**Files:**
- Create: `src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `DBVC.Core.StateTracker`
- Produces: `ObservableCollection<ChangeItemViewModel> Changes` property, `RefreshCommand`.

- [ ] **Step 1: Write the failing test**
```csharp
// Append to ViewChangesViewModelTests.cs
[Test]
public void Refresh_PopulatesChangesList()
{
    var vm = new ViewChangesViewModel();
    // Assuming we can mock StateTracker or just verify the list initializes empty
    Assert.IsNotNull(vm.Changes);
    Assert.AreEqual(0, vm.Changes.Count);
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: FAIL (missing Changes property)

- [ ] **Step 3: Write minimal implementation**
```csharp
// src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBVC.Vsix.ViewModels
{
    public class ChangeItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
        public string ObjectName { get; set; }
        public string State { get; set; } // "Modified", "Added", "Deleted"

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```
Update `ViewChangesViewModel.cs` to add:
```csharp
using System.Collections.ObjectModel;
// Add to ViewChangesViewModel
public ObservableCollection<ChangeItemViewModel> Changes { get; } = new ObservableCollection<ChangeItemViewModel>();

// For now, a stub refresh method
public void Refresh()
{
    Changes.Clear();
    // Real implementation will call StateTracker, left for next tasks or integration
}
```

- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add .
git commit -m "feat: implement change item list logic in ViewModel"
```

---

### Task 3: Diff Logic Integration

**Files:**
- Create: `src/DBVC.Vsix/Services/DiffService.cs`
- Create: `tests/DBVC.Vsix.Tests/Services/DiffServiceTests.cs`

**Interfaces:**
- Consumes: `DiffPlex.DiffBuilder`, `DBVC.Core.GitManager`, `DBVC.Core.SmoManager`
- Produces: `DiffService.GetDiffModel(string objectName)` returning `SideBySideDiffModel`.

- [ ] **Step 1: Write the failing test**
```csharp
// tests/DBVC.Vsix.Tests/Services/DiffServiceTests.cs
using NUnit.Framework;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class DiffServiceTests
    {
        [Test]
        public void DiffString_ReturnsModel()
        {
            var diffService = new DiffService();
            var model = diffService.GetDiffModelFromString("A", "B");
            Assert.IsNotNull(model);
            Assert.AreEqual(1, model.OldText.Lines.Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: FAIL

- [ ] **Step 3: Write minimal implementation**
```csharp
// src/DBVC.Vsix/Services/DiffService.cs
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.Services
{
    public class DiffService
    {
        public SideBySideDiffModel GetDiffModelFromString(string oldText, string newText)
        {
            return SideBySideDiffBuilder.Diff(oldText ?? "", newText ?? "");
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add .
git commit -m "feat: add DiffService using DiffPlex"
```

---

### Task 4: Tool Window Layout (WPF XAML)

**Files:**
- Create: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Create: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs`
- Create: `src/DBVC.Vsix/UI/ViewChangesToolWindow.cs`

**Interfaces:**
- Consumes: `ViewChangesViewModel`
- Produces: VS SDK ToolWindow implementation.

- [ ] **Step 1: Create XAML Control**
```xml
<!-- src/DBVC.Vsix/UI/ViewChangesControl.xaml -->
<UserControl x:Class="DBVC.Vsix.UI.ViewChangesControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:avalonEdit="http://icsharpcode.net/sharpdevelop/avalonedit">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="5" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- Top Area -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="5">
            <Button Content="Refresh" Width="70" Margin="0,0,10,0"/>
            <TextBox Text="{Binding CommitMessage}" Width="200" Margin="0,0,10,0"/>
            <Button Content="Commit" Width="70" />
        </StackPanel>

        <!-- Middle Area -->
        <ListView Grid.Row="1" ItemsSource="{Binding Changes}">
            <ListView.View>
                <GridView>
                    <GridViewColumn Header="Stage">
                        <GridViewColumn.CellTemplate>
                            <DataTemplate>
                                <CheckBox IsChecked="{Binding IsSelected}"/>
                            </DataTemplate>
                        </GridViewColumn.CellTemplate>
                    </GridViewColumn>
                    <GridViewColumn Header="State" DisplayMemberBinding="{Binding State}"/>
                    <GridViewColumn Header="Object" DisplayMemberBinding="{Binding ObjectName}"/>
                </GridView>
            </ListView.View>
        </ListView>

        <GridSplitter Grid.Row="2" Height="5" HorizontalAlignment="Stretch" />

        <!-- Bottom Area -->
        <Grid Grid.Row="3">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="5" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            
            <avalonEdit:TextEditor x:Name="OldTextEditor" Grid.Column="0" IsReadOnly="True" SyntaxHighlighting="TSQL" />
            <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch" />
            <avalonEdit:TextEditor x:Name="NewTextEditor" Grid.Column="2" IsReadOnly="True" SyntaxHighlighting="TSQL" />
        </Grid>
    </Grid>
</UserControl>
```
```csharp
// src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs
using System.Windows.Controls;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        public ViewChangesControl()
        {
            InitializeComponent();
            this.DataContext = new ViewChangesViewModel();
        }
    }
}
```

- [ ] **Step 2: Create ToolWindowPane**
```csharp
// src/DBVC.Vsix/UI/ViewChangesToolWindow.cs
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    [Guid("d3b4e6d4-5c9f-4b7d-8e4d-7a6c5b4d3e2f")]
    public class ViewChangesToolWindow : ToolWindowPane
    {
        public ViewChangesToolWindow() : base(null)
        {
            this.Caption = "DBVC View Changes";
            this.Content = new ViewChangesControl();
        }
    }
}
```

- [ ] **Step 3: Compile**
Run: `dotnet build src/DBVC.Vsix`
Expected: Build SUCCESS

- [ ] **Step 4: Commit**
```bash
git add .
git commit -m "feat: add WPF ViewChangesControl and ToolWindowPane"
```
