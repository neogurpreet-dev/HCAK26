# Master Prompt: Godot 4 C# Gamified 2.5D Windows File Explorer
## Role & Objective
Act as a **Senior Godot 4.x (.NET 8 / C#) Architect, Windows Desktop Application Developer,
Systems Designer, and Technical Artist**.
I am building a gamified 2.5D isometric Windows file explorer inspired by village-building games
such as Clash of Clans. The application will transform a real Windows directory into a living,
explorable world where files and folders are represented by customizable buildings, structures,
and other visual elements.
This is a **functional Windows desktop application, not merely a game or a visual prototype**. It
must interact with the real Windows filesystem while delivering an imaginative, polished, high-
performance visual experience.
I will provide custom PNG sprite assets representing different file types and folders. You are
responsible for architecting the project, designing its node hierarchy, implementing the spatial
algorithms, writing the complete C# scripts and shaders, and integrating the visual world with
native Windows operations.
The result should combine:
- The familiarity of a conventional file explorer.
- The visual appeal of an isometric village-building game.
- An intelligent, customizable spatial visualization of the filesystem.
- Contextual relationships between files and folders.
- A coherent artistic world with room for creative experimentation.
**Development strategy:** Prioritize a reliable, working Windows application first, then layer in
sophisticated visual features without compromising functionality or performance.
---
# 1. Technical Constraints
- Use **Godot 4.3+ with the .NET / C# edition targeting .NET 8**.
- Use modern Godot C# syntax exclusively: `public partial class`, `[Export]`, `[Signal]`,
`Tween`, `Callable.From`, and the appropriate Godot 4 APIs.
- Do not use Godot 3.x APIs or obsolete methods.
- Use `System.IO` (`DirectoryInfo`, `FileInfo`, `FileSystemWatcher`) and `System.IO.Compression`
(`ZipFile`) for native filesystem paths and archive operations.
- Use `System.Threading.Tasks`, `Task.Run()`, cancellation tokens where appropriate, and bounded
concurrency for expensive background operations.
- Never modify Godot scene-tree objects directly from background threads. Marshal changes to the
main thread using `CallDeferred()` or an appropriate deferred callable.
- Treat Windows paths as native OS paths rather than project-relative Godot resource paths.
- Use absolute paths internally where appropriate. Handle inaccessible directories, symbolic
links, disappearing files, duplicate events, permission errors, locked files and excessively deep
directories gracefully.
- Properly unsubscribe from signals, stop watchers, cancel outstanding tasks and dispose
resources when scenes or services are removed.
- Keep filesystem operations, visual rendering, application state, UI and Windows shell
integration in separate, maintainable components.
- Do not block the rendering thread for directory enumeration, recursive folder-size calculation,
archive compression, extraction or expensive relationship analysis.
- Do not introduce unnecessary third-party dependencies. Explicitly identify any NuGet packages,
project settings, permissions or Windows-specific prerequisites required by the solution.
All code must compile against the stated Godot and .NET targets. Avoid placeholders, pseudocode,
omitted methods and `// TODO` comments in the delivered implementation.
---
# 2. Artistic Vision and Creative Freedom
## 2.1 The world should tell a story
The artistic direction is an **ancient or medieval civilization slowly crumbling to reveal an
underlying technological, futuristic and cyberpunk world**.
The environment should feel imaginative and cohesive rather than like a collection of unrelated
file icons.
Use creative judgment to develop this visual identity. Appropriate ideas include:
- Floating islands, ruined citadels, medieval towers, stone pathways and fragmented terrain.
- Libraries, archives, workshops, fortresses, temples and other structures that suggest different
classes of digital information.
- Small technological details emerging through the ancient architecture: circuitry, glowing
runes, holographic interfaces, embedded machinery, neon accents and energy lines.
- Subtle transitions between natural, medieval, industrial and futuristic elements.
- Environmental storytelling through cracks, ruins, overgrown structures, exposed mechanisms and
evolving visual states.
- Atmospheric lighting, shadows, restrained particles, subtle animation and coherent visual
effects.
The cyberpunk elements should enhance the medieval aesthetic rather than overwhelm it. Avoid
making everything neon, overly saturated or visually noisy.
## 2.2 Artistic freedom is encouraged
You may propose and implement additional visual systems, environmental details, animations,
effects, decorative structures, lighting treatments and interactions when they improve the
experience.
Do not restrict the design to literal buildings for every file type. Explore imaginative
representations as long as they remain recognizable, intuitive, clickable and consistent with the
world.
For example, code files might occupy workshops or arcane laboratories, video files might occupy
projection towers, archives might resemble vaults, and folders might resemble larger settlements
or strongholds. These are suggestions, not rigid rules.
Use the supplied PNG assets wherever possible. Provide sensible default assets and fallback
behavior when a file type has no dedicated sprite.
**Preserve creative freedom without sacrificing usability.** Visual decoration must never
interfere with selecting files, reading labels, accessing menus, distinguishing structures or
navigating directories.
The fixed isometric view should resemble the readability of a village-builder game. Use a 2.5D
composition in which terrain and decorative objects may be 3D assets or rendered backgrounds
while file buildings use 2D sprites where appropriate. Do not introduce unnecessary 3D complexity
if a 2D sprite achieves the intended result.
---
# 3. Core Filesystem and Spatial Architecture
## 3.1 Real filesystem representation
The application must represent actual files and directories on the Windows filesystem.
Every file or folder building must retain its original absolute path and filesystem identity. A
visual object must always map to the real file or folder it represents.
The application must support:
- Loading a selected directory into the world.
- Navigating into folders by double-clicking them.
- Returning to parent directories.
- Refreshing the active directory.
- Tracking selected and recently visited locations.
- Updating the world when filesystem changes occur externally.
- Opening and managing the real files through native Windows operations.
Do not treat visual buildings as substitutes for filesystem objects.
## 3.2 Required scene hierarchy
Provide an exact node hierarchy for `MainWorld.tscn`, `FileBuilding.tscn` and any additional
scenes introduced by the architecture.
The primary world should use `Node2D` and appropriate Y-sorting for file buildings, with a fixed
isometric camera.
A suggested starting hierarchy is:
MainWorld
- Environment
- Terrain
- DecorativeElements
- WorldContent
- FileBuildings
- OverflowPlusNode
- GhostLinks
- WorldEffects
- Camera2D
- WorldInteraction
- CursorProximityLabelManager
- UI
- TopBar
- BreadcrumbNavigation
- FilePathAndFeatureBarUI
- OverflowPlusButtonUI
- HoverInfoPanel
- ContextMenuUI
- PropertyMappingPanel
- OperationProgressUI
- NotificationLayer
FileBuilding
- Sprite2D
- Area2D
- CollisionShape2D
- OptionalVisualEffects
- OptionalBuildingLabelAnchor
This hierarchy is a starting point. Improve or reorganize it when there is a clear architectural
benefit, and explain the final design.
Use a dedicated scene or reusable component for each responsibility rather than placing all
functionality in a monolithic world script.
---
# 4. Customizable Property Mapping System
This is a core feature, not an optional enhancement.
Users must be able to decide **which filesystem properties control which visual characteristics
of the world**.
For example, a user might choose to map file size to building scale, modification date to color,
usage frequency to distance from the center and file type to architectural style. Another user
might prefer a completely different mapping.
## 4.1 Supported source properties
Provide a configurable mapping system that supports at least:
- File or folder size.
- Recursive folder size.
- File extension and type.
- Last modified time.
- Last accessed time, where available.
- Creation time.
- Recorded application usage frequency.
- Recency of interaction with a file.
- Folder or file hierarchy.
- File and folder names.
- Archive status and other useful derived attributes.
Filesystem timestamps can be unavailable, stale or unreliable on some Windows configurations. Use
the persistent application usage tracker as a complementary source rather than assuming that
`LastAccessTime` always reflects actual usage.
## 4.2 Supported visual properties
Users should be able to map source properties to:
- Building scale and apparent visual weight.
- Building height or vertical visual offset.
- Color, tint and gradients.
- Glow intensity or other subtle visual effects.
- Ring distance from the center.
- Spatial position or distribution.
- Grouping and clustering.
- Optional animation or environmental effects.
Since the core world is 2D, simulate height using appropriate sprite positioning, scaling, layers
and effects unless a genuine 3D component is beneficial.
## 4.3 Mapping configuration
Create a reusable, Inspector-configurable architecture using resources such as:
- `BuildingAssetConfig.cs`
- `PropertyMappingConfig.cs`
- Additional resource classes when needed.
Each mapping should support suitable controls such as:
- Source property selection.
- Target visual property selection.
- Minimum and maximum values.
- Linear or logarithmic transforms.
- Clamping and normalization.
- Optional inversion.
- Type-specific overrides.
- Reasonable default values and fallbacks.
Implement at least one sensible default preset. The default should preserve the intended usage-
based spatial organization and logarithmic size-based building scale.
When users change a mapping, the visualization must update without requiring the application to
restart.
Persist mapping preferences locally and restore them on the next launch. Include an appropriate
UI for inspecting and editing mappings.
If a custom mapping conflicts with minimum spacing or collision constraints, preserve the non-
overlap and usability guarantees rather than blindly applying the requested coordinates.
---
# 5. Concentric Ring Layout and Usage-Based Spatial Organization
## 5.1 Default layout behavior
When opening a directory, arrange its contents in concentric circles around a world-space origin
`(0, 0)`.
By default:
- The innermost rings contain frequently used and recently interacted-with files and folders.
- The middle rings contain moderately relevant items.
- The outer rings contain less frequently used items.
- Folder buildings and file buildings remain distinguishable.
- File size influences building scale by default.
- Spatial spacing is adjusted according to the visual footprint of each building.
Usage priority should combine the application's own interaction history with available filesystem
timestamps.
Use a documented scoring function that balances usage frequency, recency, and reliable filesystem
signals. Make relevant weights configurable where practical.
## 5.2 Non-overlapping polar placement
Implement a robust placement algorithm using polar coordinates.
Do not place buildings at fixed angular intervals regardless of their sizes. Account for the
scaled footprint of adjacent buildings to prevent overlaps.
The algorithm must:
- Distribute buildings around each ring.
- Estimate the visual footprint of each building.
- Account for the largest building on a ring.
- Maintain configurable minimum spacing.
- Increase the ring radius or create another ring when necessary.
- Avoid placing buildings outside reasonable world bounds.
- Handle directories containing many files without excessive computation.
- Recompute the layout after relevant filesystem changes or property-mapping edits.
The default result should be visually balanced, readable and suitable for a fixed isometric
perspective.
Do not require every directory to use precisely the same arrangement if a different layout
improves readability. However, retain concentric usage-based rings as the default layout mode.
## 5.3 Persistent usage tracker
Implement a lightweight, local JSON usage tracker.
It should record useful interaction events such as:
- Opening files.
- Opening folders.
- Revisiting directories.
- Frequency and recency of interactions.
- Relationships between files opened or used together.
Use safe writes, error handling, sensible retention limits and backward-compatible handling of
missing or outdated cache files.
All usage-history data should remain local by default. Do not upload filesystem paths, file names
or usage patterns to external services.
## 5.4 Excessive File Count Handling (100+ Files Overflow "+" Button)
To counter the problem of excessive files in a folder and prevent visual clutter or performance degradation:
- When a directory contains a large number of items (specifically **100+ files/folders**, configurable via an exported threshold such as `MaxVisibleBuildings = 100`), only render the top 100 highest-priority/most relevant items (based on the usage and relevance scoring model) as physical buildings in the 2.5D world.
- Automatically spawn or reveal a prominent **Plus ("+") Button** (both as a stylized interactive structure/beacon on the outer ring of the world and/or a clear floating HUD indicator) showing the remaining hidden file count (e.g., `+ 245 more items...`).
- Clicking this **Plus ("+") Button** must immediately open the current active directory in the **normal native Windows File Explorer** (`explorer.exe "path"`) so the user can seamlessly browse, search, and manage the excessive files using the standard OS interface.
---
# 6. Ghost Files and Ghost Links
This is another core feature.
The application should reveal relationships between files that are not necessarily stored beside
one another.
## 6.1 Ghost Files
Implement translucent contextual representations of related files or folders that are relevant to
the currently viewed directory.
For example, if a user frequently opens a source file together with a configuration file in
another directory, a contextual representation of that configuration file may appear near the
source file or along the edge of the current world.
A ghost must clearly differ from a real file building:
- Use translucency, restrained glow or another distinctive visual treatment.
- Make it clear that the object represents a real item located elsewhere.
- Display its original path or location in the information panel.
- Allow the user to navigate to or open the real item.
- Never treat a ghost representation as a separate file or create a duplicate on disk.
## 6.2 Relationship detection
Use the persistent usage tracker to identify likely relationships between files and folders.
Consider:
- Files opened together within a configurable time window.
- Repeated co-access across multiple sessions.
- Repeated navigation between related directories.
- The strength and recency of observed relationships.
Use a transparent scoring model, minimum observation thresholds and a configurable limit on
displayed ghosts.
Avoid displaying a relationship solely because two files were opened once by chance.
## 6.3 Ghost Links
Visualize meaningful relationships using subtle connecting lines, paths, energy trails or other
artistic treatments.
The visualization should:
- Connect real buildings to contextual ghost representations when useful.
- Distinguish a ghost link from ordinary filesystem hierarchy.
- Reveal more context on hover or selection.
- Avoid clutter when many relationships exist.
- Support toggling the feature on or off.
- Adapt its opacity and visibility to zoom level or relevance.
Ghost representations and their connecting lines must never intercept normal mouse interaction
with actual file buildings or interfere with directory navigation.
Use a dedicated component such as `GhostLinkManager.cs`, with any required supporting classes.
---
# 7. Building Asset Mapping and Size-Based Scaling
## 7.1 Asset mapping
Create `BuildingAssetConfig.cs` as a `[GlobalClass]` resource that allows assigning custom
`Texture2D` assets to file types through the Inspector.
Support mappings for extensions and categories such as:
- `.py`
- `.cs`
- `.cpp`
- `.js`
- `.html`
- `.json`
- `.pdf`
- `.mp4`
- `.png`
- `.jpg`
- `.zip`
- `Folder`
- `Default`
The mapping system must support extension normalization, unknown extensions, missing textures and
fallback assets.
Use a maintainable data structure. A dictionary, resource entries or another Inspector-friendly
configuration approach is acceptable if it works correctly with the target Godot version.
## 7.2 Logarithmic visual weighting
Calculate file sizes and scale the visual representations using a configurable transform.
The default size-to-scale mapping should use a logarithmic function, with a sensible minimum such
as `0.6f` and maximum such as `2.2f`.
The function must:
- Handle zero-byte files.
- Handle missing or inaccessible size information.
- Prevent `NaN`, infinity and invalid scale values.
- Keep tiny files visually selectable.
- Prevent enormous folders from overwhelming the world.
Folder-size calculation must be asynchronous and must handle access errors, junctions, symbolic
links and cycles safely.
For very large directory trees, avoid recalculating the entire filesystem unnecessarily. Use
cached results, bounded concurrency and invalidation when relevant files change.
If the user remaps building scale to a different property, honor that mapping instead of forcing
file size to control scale.
---
# 8. Isometric Camera and World Navigation
Implement `IsometricCamera.cs` with:
- Middle-mouse drag panning.
- Right-mouse drag panning.
- Scroll-wheel zooming.
- Configurable zoom limits.
- Smooth camera movement.
- Zooming toward the cursor position.
- Smooth focus on a selected building.
- A small configurable zoom-in when selecting a file or folder.
- Double-click navigation into directories.
- Restoration of a useful camera position when returning to a parent directory.
Use a Godot 4 `Camera2D` and valid modern C# APIs.
Smooth selection focus should use a tween with a cubic transition and an ease-out curve, for
example the equivalent of:
`CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out)`
Handle interrupted tweens safely so that rapid selections do not create competing camera
animations.
Selection, camera motion, drag interaction and building activation must not conflict.
---
# 9. Cursor-Proximity File and Folder Labels
Implement an intelligent proximity-label system.
**Wherever the mouse pointer moves, show the names of nearby file and folder buildings, but only
for objects close to the cursor. Do not display labels for the entire directory simultaneously.**
This should make a dense visual filesystem easy to inspect without cluttering the world.
## Required behavior
- Track the pointer and identify nearby visible file and folder buildings.
- Display each eligible item's name above or near its building.
- Use a configurable screen-space proximity radius.
- Fade labels in as the cursor approaches and fade them out as it moves away.
- Prioritize the directly hovered or selected building.
- Support configurable label scale, opacity, offset, maximum width and truncation.
- Keep labels readable at different camera zoom levels.
- Ensure that labels do not block mouse selection or clicks on the underlying building.
- Provide sensible behavior when several buildings are close together.
- Prevent excessive flicker as the pointer moves between neighboring buildings.
- Offer settings to enable or disable proximity labels and adjust their radius.
Use a dedicated `CursorProximityLabelManager.cs` and a reusable label scene or equivalent pooled
UI elements.
For performance, avoid creating and destroying label nodes on every mouse movement. Reuse labels
where practical and update visibility only when needed.
For large directories, use efficient spatial filtering, screen-space distance checks or another
suitable technique rather than repeatedly performing expensive operations for every building on
every frame.
This feature is distinct from the hover information panel: proximity labels identify nearby
objects, while the information panel provides detailed metadata for the currently hovered
building.
---
# 10. Real-Time Filesystem Synchronization
Integrate `FileSystemWatcher` into the world-generation architecture.
The active directory must update automatically when files or folders are created, deleted,
renamed or changed externally in Windows Explorer or another application.
Requirements:
- Handle `Created`, `Deleted`, `Renamed` and relevant `Changed` events.
- Account for duplicate, rapid and out-of-order events.
- Debounce filesystem changes and coalesce bursts of events.
- Refresh the correct building without unnecessarily rebuilding the entire world.
- Recalculate affected metadata and folder sizes.
- Recompute placement when necessary.
- Update the usage or relationship cache only when appropriate.
- Handle watcher errors and buffer overflow by performing a controlled directory rescan.
- Correctly dispose of watchers when navigating away from directories.
Watcher callbacks run on background threads. They must never directly manipulate nodes, textures,
tweens or other scene-tree objects.
Use deferred main-thread updates and robust synchronization.
---
# 11. Windows Desktop and Native Filesystem Integration
The application must ultimately support native Windows file operations, with full implementation
delivered in Part 2.
Implement a well-defined service architecture centered on `WindowsShellManager.cs`, with separate
supporting components whenever useful.
Required functionality:
- Open files in their default Windows application.
- Open directories in Windows Explorer.
- Rename, copy, cut and paste files or folders.
- Create new folders and files.
- Delete files and directories using the Windows Recycle Bin.
- Display native Windows file properties.
- Reveal a selected file in Windows Explorer.
- Open Terminal at the active directory.
- Open VS Code at a selected file or directory when installed.
- Compress selected files and folders into ZIP archives.
- Extract ZIP archives into appropriate destinations.
- Display progress and report errors for long-running operations.
Use `Process.Start()` with `UseShellExecute = true` where appropriate.
Use `Microsoft.VisualBasic.FileIO.FileSystem` with `RecycleOption.SendToRecycleBin` or a
correctly implemented Win32 alternative for recycle-bin operations. Do not substitute permanent
deletion for the requested recycle-bin behavior without explicitly warning the user.
For the Windows Properties dialog, implement the shell integration carefully, using the
appropriate native shell API and structures. For Explorer navigation, handle quoted paths and
spaces correctly.
Use asynchronous execution for archive and expensive filesystem operations. Report progress
through thread-safe mechanisms or deferred updates.
Validate all source and destination paths. Never silently overwrite files or extract untrusted
archive contents without checking for path traversal, invalid paths and other unsafe archive
entries.
Use a custom in-application context menu that visually matches the world, while retaining the
requested native Windows integrations.
---
# 12. Glassmorphic Hover Information Panel & Navigation / Feature Access HUD
Each building must expose hover information through an `Area2D` and `CollisionShape2D`, or an
equally robust interaction mechanism.
Create a floating or screen-anchored `PanelContainer` that follows a coherent UI layout.
The panel should display:
- File or folder name.
- Full path when appropriate.
- Formatted file or recursive folder size.
- File extension and type.
- Last modified date.
- Last accessed date when available.
- Usage tier or relevance score.
- Useful details for ghost representations.
- Any other metadata that helps users understand the selected object.
Use a custom `frosted_glass.gdshader` with `shader_type canvas_item;`, a screen-texture-based
blur approach compatible with Godot 4, and subtle translucency, borders and highlights.
Ensure that the shader uses valid Godot 4 syntax and the required screen-texture setup. Explain
any viewport or rendering configuration needed for it to work.
Do not assume that declaring a screen-texture sampler automatically creates a blurred image.
Implement the required blur approximation or multi-sample technique with reasonable performance.
The panel must be readable, lightweight and visually consistent with the ancient-to-cyberpunk
visual identity.

## 12.1 File Path Bar & Feature Access Bar (Master HUD)
Implement a dedicated, frosted-glass HUD bar (`FilePathAndFeatureBarUI.cs`) anchored cleanly at the top or bottom of the screen that gives the user full visibility of their current file path and direct one-click access to all features built into the application:
- **Interactive File Path Bar:**
  - Displays the full current native Windows directory path with clickable breadcrumb segments (clicking any folder segment navigates directly to that directory).
  - Clicking the path bar background switches it into an editable `LineEdit` text box where users can type or paste an exact Windows path (e.g., `C:\Projects\MyGame`), validate it, and jump straight to it on pressing Enter.
  - Includes a quick "Copy Path" button and drive/root selector dropdown.
- **Unified Feature Access Bar (Action Dock):**
  - **Navigation Controls:** Back, Forward, Parent Directory (Up), Home/Root Directory, and Refresh World.
  - **File & Archive Operations:** New Folder, New File, Copy, Cut, Paste, Rename, Delete to Recycle Bin, Compress to `.zip`, and Extract `.zip`.
  - **Simulation & World Controls:** Toggle Property Mapping Configuration Panel (`PropertyMappingPanel`), Toggle Ghost Files & Ghost Links (`GhostLinkManager`), Toggle Cursor-Proximity Labels & Radius (`CursorProximityLabelManager`), and Reset/Center Isometric Camera.
  - **External Windows Bridges:** Open Current Folder in Native Windows File Explorer, Open in Windows Terminal, Open in VS Code, and Native File Properties.
  - **100+ Files Overflow Plus ("+") Button Integration:** When the active directory contains **100+ files/folders**, display a prominent, glowing **Plus ("+") Button** badge in the HUD (alongside the in-world overflow node) displaying the excess file count (e.g., `+142 files`). Clicking this `+` button immediately launches the normal Windows File Explorer at the active directory path to counter excessive file clutter.
---
# 13. Performance, Reliability and Privacy
The application must remain responsive when exploring directories containing thousands of files
and folders.
Implement or explain:
- Asynchronous recursive size calculations.
- Cancellation and cache invalidation.
- Bounded background task concurrency.
- Incremental world updates.
- Efficient building and label pooling.
- Spatial filtering for cursor proximity.
- Debounced watcher events.
- Graceful handling of inaccessible files and directories.
- Safe resource disposal.
- Limits on ghost representations and visual effects.
- Sensible refresh behavior for large directories (including the 100+ file cap and Overflow "+" Button fallback to native Windows Explorer).
Never access the UI or scene tree directly from worker threads.
Do not assume every Windows filesystem timestamp is reliable or that every file can be opened.
Make unavailable metadata explicit and handle errors without crashing.
Keep usage analytics, mapping preferences, navigation history and relationship data in local
application data by default. Do not transmit private filesystem information externally.
Use logging and useful user-facing notifications for recoverable failures.
---
# 14. Code Architecture and File Organization
Use a clean, maintainable project structure. A suitable starting point is:
`res://`
- `Scenes/`
- `MainWorld.tscn`
- `FileBuilding.tscn`
- `UI/`
- `Environment/`
- `Scripts/`
- `World/WorldGenerator.cs`
- `World/SpatialRingLayout.cs`
- `World/FileBuilding.cs`
- `World/GhostLinkManager.cs`
- `World/CursorProximityLabelManager.cs`
- `World/BuildingAssetConfig.cs`
- `World/PropertyMappingConfig.cs`
- `World/IsometricCamera.cs`
- `Services/UsageTracker.cs`
- `Services/WindowsShellManager.cs`
- `Services/ZipEngine.cs`
- `UI/HoverInfoPanel.cs`
- `UI/FilePathAndFeatureBarUI.cs`
- `UI/ContextMenuUI.cs`
- `UI/PropertyMappingPanel.cs`
- `UI/OperationProgressUI.cs`
- `Shaders/`
- `frosted_glass.gdshader`
- `Assets/`
- `Buildings/`
- `Environment/`
- `UI/`
You may adjust this structure when appropriate. Keep services, visual nodes, configuration
resources and UI components separate.
Clearly state which script is attached to which node, which exported properties need Inspector
configuration, which signals connect different components, and which autoloads or project
settings are required.
Use strongly typed data structures for filesystem entries and configuration where practical.
Avoid excessively coupling the camera, building scenes, world generator, usage tracker and
Windows shell service.
---
# 15. Delivery in Two Parts
To prevent code truncation, deliver the implementation in the following two phases.
**IMPORTANT: In your first response, deliver Part 1 only. Then stop and wait for my explicit
confirmation before generating Part 2. Do not generate Part 2 in the first response.**
## Part 1 — Architecture, Asset Configuration, Property Mapping, Spatial Layout and Camera
Deliver the complete implementation for:
1. **Exact scene tree setup**
- `MainWorld.tscn`
- `FileBuilding.tscn`
- Any additional scenes required for the Part 1 implementation.
- Node types, exported properties, signal connections and setup instructions.
2. **`BuildingAssetConfig.cs`**
- Custom PNG-to-file-type mappings.
- Inspector configuration.
- Extension normalization and fallback handling.
3. **`PropertyMappingConfig.cs`**
- Configurable mappings between filesystem attributes and visual properties.
- Transform functions, clamping, normalization and default mappings.
- A design that supports runtime edits and later integration with the mapping settings UI.
4. **`UsageTracker.cs`**
- Persistent, local JSON-based usage tracking.
- Usage-frequency and recency scoring.
- Interaction-history data for later Ghost Files and Ghost Links functionality.
- Robust serialization and error handling.
5. **`SpatialRingLayout.cs`**
- Non-overlapping concentric polar placement.
- Footprint-aware ring capacity and spacing.
- Efficient handling of variable building sizes.
- Support for usage-based ordering and configurable spatial mappings.
6. **`WorldGenerator.cs`**
- Directory enumeration using `System.IO`.
- Sorting by usage relevance.
- **100+ Files Overflow Handling:** Limiting rendered buildings to the top 100 items and exposing the overflow state/button trigger to launch native Windows Explorer for excessive files.
- Asynchronous folder-size calculation.
- Logarithmic default scale mapping.
- Asset assignment and dynamic building instantiation.
- Integration with property mappings and usage tracking.
- `FileSystemWatcher` integration with robust event coalescing and deferred scene updates.
- Efficient refresh and incremental-update architecture.
- Hooks and data structures required by ghost representations and proximity labels in Part 2.
7. **`IsometricCamera.cs`**
- Drag panning.
- Cursor-centered zoom.
- Zoom limits.
- Smooth selection focus.
- Tween cleanup and interruption handling.
8. **Integration instructions**
- How to create and configure the project in Godot.
- How to assign the provided PNG assets.
- How to connect the scripts to scene nodes.
- How to set the initial directory.
- How to compile and run the project on Windows.
You may provide supporting classes when they are necessary for a clean and complete Part 1
architecture.
Do not stub important features merely because the UI implementation belongs to Part 2. Establish
the interfaces and data contracts that Part 2 will consume.
## Part 2 — Windows Shell Manager, File Interaction, Ghost Links, Proximity Labels, Glass UI, File Path & Feature Bar, and Context Menus
After I confirm, provide the complete implementation for:
1. **`WindowsShellManager.cs`**
- Open, rename, copy, cut, paste and create operations.
- Recycle Bin integration.
- Native Windows Properties dialog.
- Explorer reveal and directory navigation (including opening the active directory when the 100+ files Overflow "+" Button is clicked).
- Terminal and VS Code integration.
- Error handling and correct native Windows API usage.
2. **`ZipEngine.cs` or an appropriately integrated archive service**
- Asynchronous ZIP compression.
- Asynchronous extraction.
- Progress reporting.
- Cancellation where practical.
- Safe archive-path validation and destination handling.
3. **`FileBuilding.cs`**
- Hover and selection signals.
- Correct single-click and double-click behavior.
- Interaction with camera focus.
- Dynamic scaling animation.
- Context-menu invocation and metadata access.
4. **`GhostLinkManager.cs`**
- Cross-directory contextual ghost representations.
- Relationship scoring and filtering.
- Visual connections and navigation to original files.
- Distinct appearance for real objects and ghosts.
- Rendering and interaction safeguards.
5. **`CursorProximityLabelManager.cs`**
- Nearby-only labels around the mouse cursor.
- Adjustable screen-space search radius.
- Distance-based opacity and visibility.
- Efficient label pooling and filtering.
- Correct label behavior at different zoom levels.
6. **Customizable mapping interface**
- `PropertyMappingPanel.cs` or equivalent.
- Runtime editing of property-to-visual mappings.
- Saving and loading settings.
- Updating the world when mappings change.
7. **`frosted_glass.gdshader`**
- Valid Godot 4 shader syntax.
- A functional frosted-glass effect.
- Translucent borders and highlights.
- Required rendering and viewport setup.
8. **`HoverInfoPanel.cs`**
- Metadata display.
- Correct formatting and unavailable-value handling.
- Visual distinction between normal files, folders and ghost representations.
9. **`FilePathAndFeatureBarUI.cs` and `ContextMenuUI.cs` (with supporting UI)**
- **Interactive File Path Bar:** Clickable breadcrumbs and editable text path input (`LineEdit`) for direct Windows path navigation.
- **Feature Access Bar (Master Action Dock):** One-click buttons to access all built features (Back/Forward/Up/Home/Refresh, New File/Folder, Copy/Cut/Paste/Rename/Delete, Zip/Unzip, Property Mapping Panel toggle, Ghost Links toggle, Proximity Labels toggle, Terminal/VS Code launchers).
- **100+ Files Overflow Plus ("+") Button:** Appears automatically when a folder has 100+ items (both in the world and on the HUD bar) showing the excess file count, opening the normal Windows File Explorer when clicked.
- File and folder context menu operations.
- Selection-aware menus.
- Progress feedback for long-running operations.
- User-facing error and success notifications.
- A cohesive interface matching the rest of the application.
Include the full scene and Inspector setup for all new components, all necessary signal wiring,
and end-to-end integration instructions.
---
# 16. Implementation Standards
Follow these rules throughout both parts:
- Give complete, directly usable source code with appropriate namespaces and `using` statements.
- Use Godot 4.3+ / .NET 8-compatible APIs only.
- Do not invent nonexistent methods or assume that Godot automatically provides Windows shell
functionality.
- Explain Windows-specific interop accurately.
- Do not silently omit required class members or signal handlers.
- Avoid placeholders, pseudocode and TODO comments.
- Include exception handling at meaningful OS and asynchronous boundaries.
- Make scripts compatible with the exact node hierarchy that you specify.
- Maintain consistency between the classes, method signatures, signals, exported properties and
scene references across both parts.
- Do not duplicate responsibilities across services unnecessarily.
- Explain any required NuGet packages, project settings, resource assignments or native Windows
assumptions.
- Clearly distinguish code that belongs in Part 1 from code that will be delivered in Part 2.
- Prefer the simplest reliable implementation that meets the requirements rather than adding
unnecessary architecture.
- If a particular Windows feature has implementation limitations, explain the limitation and
provide the most reliable supported alternative.
**Begin now with Part 1 only.** First outline the architecture and exact node hierarchy, then
provide the complete Part 1 code and setup instructions. Do not produce Part 2 until I explicitly
confirm.