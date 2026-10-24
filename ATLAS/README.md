# ATLAS — 3D file explorer (Part 1)

Godot 4.3+ **.NET edition**, C# / .NET 8, Windows 10/11.

**STATUS: NOT COMPILED OR RUN.** The authoring sandbox had no .NET SDK or Godot, so this code is unverified.
Expect a few compile errors on first build; they should be small API-name fixes.

## Setup
1. Install Godot 4.3+ (.NET build) and the .NET 8 SDK.
2. In `ATLAS.csproj`, set `Godot.NET.Sdk/x.y.z` to your editor version (Help > About).
3. Open `project.godot`, press **Build** (hammer icon), fix any errors, then **F5**.
4. Start folder: first command-line user arg (`-- C:\path`), else `InitialDirectory` on the MainWorld node, else your user profile.

## Controls
Right-drag orbit · middle-drag / arrows pan · wheel zoom-to-cursor · Q/E rotate · Home reset ·
click select · double-click open · Backspace up · Alt+Left/Right history · F5 refresh.

## Validation checklist
- [ ] Project builds with no errors; window opens borderless with title bar
- [ ] Hub island + satellite islands + bridges render; HUD matches reference
- [ ] Orbit / pan / zoom / reset work; folders open on double-click; Back/Forward/Up work
- [ ] Hover shows info card; Open / Reveal / Properties work
- [ ] Property Mapping dropdowns and presets update the world live
- [ ] Folder with >100 items shows "+ N more" beacon and dock badge (opens Explorer)
- [ ] Creating/deleting a file in the current folder refreshes the view

## Customising
Assign PNG icons / PackedScene models via `BuildingAssetConfig` and the `ModelOverride` fields; otherwise procedural meshes and runtime SVG icons are used.

## Not in Part 1
ZipEngine, ghost links, proximity labels, context menus, breadcrumb bar and feature dock extras — Part 2, on your confirmation.

## Context menus and file operations (added after Part 1 - NOT COMPILED OR RUN)
Right-click a building / empty space / a Ctrl+click multi-selection for a glass context menu. Shift+F10 or the Menu key opens it
for the selected building. Shortcuts: F2 rename, Del recycle, Shift+Del delete permanently (confirmation), Ctrl+C / X / V, Ctrl+A.
Code: `Scripts/World/FileCommands.cs` (what each menu shows + commands), `Scripts/UI/ContextMenuView.cs` and `GlassDialog.cs`
(UI), `Scripts/Services/FileOps.cs` (safe rename / create / paste / delete). Right-click never changes the selection, so it
cannot trigger Ghost Links or previews. The ATLAS clipboard is internal (not shared with Windows Explorer).
Originals of every changed file are in `_backup_before_ctxmenu/`.
Not implemented: tabs / new window, drag-and-drop, undo, favorites, sort/group, Replace/Skip conflict prompts (clashes get "- Copy" names).
