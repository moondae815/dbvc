# DBVC View Changes Tool Window Design

## Overview
The "View Changes" tool window is the primary UI for users to stage and commit database schema changes in the DBVC SSMS 21 plugin. It provides a split-view interface where users can select modified objects and instantly preview the side-by-side SQL diff before committing.

## Target Environment
- **Platform**: SSMS 21 (Visual Studio 2022 Shell, 64-bit)
- **Framework**: .NET Framework 4.8
- **Project**: `DBVC.Vsix` (UI Layer)
- **Dependencies**: `DBVC.Core`, `AvalonEdit`, `DiffPlex`

## Architecture & Components

### 1. Tool Window Registration
- **Class**: `ViewChangesToolWindow` inheriting from `ToolWindowPane`.
- **Command**: A VS SDK Command will be added to the SSMS View menu or DBVC main menu to open this tool window.
- **Content**: Hosts a WPF `UserControl` (`ViewChangesControl.xaml`).

### 2. UI Layout (Split View)
The UI is a WPF UserControl divided into three main sections:
- **Top (Action Area)**: 
  - Refresh Button (`Button`).
  - Commit Message Input (`TextBox`).
  - Commit Button (`Button`).
- **Middle (List Area)**:
  - `ListView` displaying the pending changes fetched from `StateTracker`.
  - Each item contains a CheckBox (for staging), a State column showing `Modified` / `Added` / `Deleted` as text, and the Object Name (e.g., `dbo.Users`). Status icons are not implemented; the state is rendered as text.
- **Bottom (Diff Area)**:
  - Embedded `AvalonEdit` text editors combined with `DiffPlex` for side-by-side diff rendering.
  - When an item in the middle list is selected, this area fetches the old SQL state from Git and the new SQL state from the database, runs `DiffPlex`, and highlights the diffs.

### 3. Data Flow
1. **Load/Refresh**: 
   - `ViewChangesControl` calls `StateTracker.RefreshState(...)` to read the log.
   - The UI binds the returned changes to the `ListView`.
2. **Preview Diff**:
   - On ListView selection change, the selected object's name is passed to `GitManager` to fetch the `HEAD` version of the `.sql` file.
   - The current DB version is scripted via `SmoManager.ScriptObjects`.
   - Both strings are passed to `DiffPlex` to generate a diff model, which is then rendered in `AvalonEdit`.
3. **Commit**:
   - The user checks desired items and clicks Commit.
   - The control calls `GitManager.CommitChanges(...)` passing the checked objects and the commit message.
   - Upon success, the message box is cleared, and the UI triggers a Refresh.

## Error Handling
- Exceptions during Diff generation (e.g., file not found in Git for new objects) will be handled gracefully: new objects will simply show empty left side and full right side.
- Exceptions during Commit will display a WPF `MessageBox` with the error detail to the user.
- The target database is entered manually in the Server / Database inputs and applied with the **Connect** button. There is no automatic "active database" detection — that would require Object Explorer integration, which is deferred for the same reason as Feature 10 (see [2026-08-01-dbvc-object-explorer-overlay.md](../plans/2026-08-01-dbvc-object-explorer-overlay.md)).
- If `ConfigManager` cannot resolve a mapping for the connected database, a warning banner is shown above the content area ("Active Database is not mapped to a Git repository.") and commit actions are disabled. The banner also carries a **"저장소 연결..."** button that prompts for a folder, verifies it is a valid Git repository via `IGitManager.IsRepository`, and registers the mapping through `ConfigManager.AddMapping`. The banner sits outside the initialization overlay so that an uninitialized database can still be mapped.

## Out of Scope
- Branch management or switching branches (handled by standard Git clients for now).
- Object Explorer tree icon overlays (this will be a separate feature design).
- Conflict resolution.
