# Project Context: ClashFileExplorer (Godot 4 .NET / C#)

## Environment & Build Specs
- Engine: Godot 4.7.2 .NET Edition (Compatibility Renderer)
- csproj SDK line: Godot.NET.Sdk/4.7.2 (do not change it)
- Target Framework: net8.0-windows
- csproj properties: UseWindowsForms, Nullable, AllowUnsafeBlocks, EnableDynamicLoading
- OS Target: Windows 10/11 Desktop
- Editor: VS Code (C# Dev Kit + C# Tools for Godot)

## Spec
- The full spec is docs/MASTER_PROMPT.md. Follow it exactly.
- Delivery is Part 1, then Part 2 only after the user confirms.

## Directory Map
- Scripts/World -> WorldGenerator, SpatialRingLayout, FileBuilding, IsometricCamera, BuildingAssetConfig, PropertyMappingConfig (namespace ClashExplorer.World)
- Scripts/Services -> UsageTracker, WindowsShellManager, ZipEngine (namespace ClashExplorer.Services)
- Scripts/UI -> HUD, panels, context menu (namespace ClashExplorer.UI)
- Scenes, Scenes/UI, Scenes/Environment
- Shaders -> frosted_glass.gdshader
- Assets/Buildings (sprites already imported), Assets/Environment, Assets/UI

## Strict Godot 4 C# Coding Laws
1. Every Godot-derived class is `public partial class`.
2. Class name must exactly match the file name.
3. NEVER write `using System.Windows.Forms;`. It clashes with Godot's Timer, Label, Button, Control and Panel. Use full names inline if needed.
4. Fetch child nodes with `GetNode<T>()` inside `_Ready()`, never in constructors.
5. Signals: `[Signal] public delegate void FooEventHandler();`, emit with `EmitSignal(SignalName.Foo)`.
6. Use `[Export]` for tunable values, not magic numbers.
7. Win32/Shell calls go in Scripts/Services via P/Invoke or Microsoft.VisualBasic.FileIO, wrapped in try/catch.
8. Never block the main thread with file I/O. Use Task.Run, then CallDeferred back to the main thread.
9. Never touch the scene tree from worker threads or FileSystemWatcher callbacks.
10. Free nodes with QueueFree(), never Dispose().
11. Nullable is on: handle nulls explicitly.

## Working Rules
- Build after each file and fix all errors before moving on.
- Never touch the csproj without asking.
