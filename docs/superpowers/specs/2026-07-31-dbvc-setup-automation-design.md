# DBVC Setup Automation Design

## Overview
Automate the installation of the `DBVC_ChangeLog` table and DDL trigger directly from the SSMS VSIX extension UI. If a database is not yet configured for DBVC, the UI will display a prominent "Setup DBVC" button instead of the change list.

## Core Component (DBVC.Core)
1. **Resource Embedding**: `src/DBVC.Database/InstallTrigger.sql` will be bundled as an `<EmbeddedResource>` in `DBVC.Core.csproj`.
2. **`StateTracker` Enhancements**:
   - `bool IsInitialized(string connectionString)`: Queries `sys.objects` and `sys.triggers` to determine if both `DBVC_ChangeLog` table and `trg_DBVC_DDL_Tracker` trigger exist.
   - `void InitializeDatabase(string connectionString)`: Reads `InstallTrigger.sql` from the embedded resources and executes it against the target database using `Microsoft.Data.SqlClient`. The script contains `GO` separators, so it will need to be split on `GO` and executed in separate batches.

## UI / ViewModel Component (DBVC.Vsix)
1. **`ViewChangesViewModel`**:
   - `bool IsInitialized`: Raised via `INotifyPropertyChanged`. Default `false`.
   - `ICommand SetupCommand`: Invokes `StateTracker.InitializeDatabase()`, then sets `IsInitialized = true` and triggers a `Refresh()`.
2. **`ViewChangesControl.xaml`**:
   - Include a `BooleanToVisibilityConverter` in `UserControl.Resources` (along with an Inverse converter if necessary).
   - Add an overlay `Grid` covering the content area with `Visibility="{Binding IsInitialized, Converter={StaticResource InverseBoolToVis}}"`.
   - The overlay contains a `TextBlock` ("This database is not initialized for DBVC.") and a `Button` ("Setup DBVC") bound to `SetupCommand`.
   - The main content (`ListView` and Diff viewer) visibility is bound to `IsInitialized`.

## Error Handling
- Exceptions during script execution (e.g., lacking db_ddladmin/db_owner permissions) will be caught and bubbled up. If UI error handling is implemented later, it will display a message box. For now, it will throw an exception to be handled at the command level.

## Testing Strategy
- **Core**: Unit tests mock the SQL execution or run against a dummy setup to verify `IsInitialized` toggles after `InitializeDatabase()`.
- **ViewModel**: Unit tests verify `SetupCommand` execution updates `IsInitialized` and calls the setup dependency.
