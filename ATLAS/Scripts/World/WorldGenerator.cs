using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Atlas;

/// <summary>Describes one island for the HUD label cards.</summary>
public sealed class IslandInfo
{
    public string Name = "";
    public int Count;
    public Color Color;
    public Vector3 Anchor;
    public bool IsHub;
}

/// <summary>
/// Turns a real directory into the floating-island world.
///  * enumerates on a worker thread (never blocks rendering) and hands the result to the main thread deferred
///  * ranks by the usage score, renders the top <see cref="MaxVisibleBuildings"/> and exposes the overflow count
///  * hub island = folders + most relevant files in concentric rings; satellites = groups (file type by default)
///  * folder sizes are computed asynchronously with bounded concurrency; results trigger a coalesced rebuild
///  * a debounced FileSystemWatcher keeps the world in sync with external changes
/// Attach to WorldContent. MainWorld injects the roots / configs before calling LoadDirectory.
/// </summary>
public partial class WorldGenerator : Node3D
{
    [Export] public int MaxVisibleBuildings { get; set; } = 100;
    /// <summary>How many top-ranked files (besides all folders) live on the central "Recently Used" island.</summary>
    [Export] public int HubFileCount { get; set; } = 20;
    [Export] public float BaseHalfExtent { get; set; } = 0.8f;
    [Export] public float MinSpacing { get; set; } = 0.8f;
    [Export] public bool ShowHidden { get; set; } = false;
    /// <summary>How many of the MOST-USED folders of the current directory form the core of the main island (at its centre, surrounded by the file buildings).</summary>
    [ExportGroup("Island rings")]
    /// <summary>Radius of the first island ring from the hub centre. 0 = derived from the island sizes. See IslandRingSettings for all parameters.</summary>
    [Export] public float InnerRingRadius { get; set; } = 0f;
    [Export] public float RingRadiusIncrement { get; set; } = 0f;
    [Export] public float MinimumIslandSpacing { get; set; } = 3f;
    [Export] public float HubIslandGap { get; set; } = 7f;
    [Export] public int MinimumRingIslandCount { get; set; } = 3;
    [Export] public int SingleRingMaxIslands { get; set; } = 5;
    /// <summary>Upper limit for the number of rings (2 rings alternate inner/outer; more are only used when they make the layout clearly more compact).</summary>
    [Export] public int MaxIslandRings { get; set; } = 6;
    /// <summary>Each ring lies at least this fraction of an island radius beyond the ring inside it (makes the inner/outer alternation visible).</summary>
    [Export] public float RingSeparation { get; set; } = 0.5f;
    /// <summary>Width of the bridges, used to keep a radial bridge clear of the inner-ring islands it passes.</summary>
    [Export] public float BridgeClearanceWidth { get; set; } = 1.9f;
    /// <summary>Screen angle of island 0. All islands then follow at 360 / N degrees each, on every ring.</summary>
    [Export] public float FirstRingStartAngleDeg { get; set; } = 120f;
    /// <summary>Seconds an island takes to glide to its new position when a refresh changes the layout.</summary>
    [Export] public float IslandMoveDuration { get; set; } = 0.7f;
    /// <summary>Most satellite (category) islands around the hub (Others included); any beyond this are folded into Others. 24 fills four rings with the default capacity rule.</summary>
    [Export] public int MaxSatelliteIslands { get; set; } = 24;
    [ExportGroup("Island grouping")]
    /// <summary>A category with this many buildings or fewer does not get its own island: it joins the shared "Others" island (default 2, so 3+ buildings = own island).</summary>
    [Export] public int OthersThreshold { get; set; } = IslandGrouping.DefaultSmallCategoryThreshold;
    [ExportGroup("Folders")]
    [Export] public int CoreFolderCount { get; set; } = 5;
    /// <summary>Most folders shown on the secondary Folder Island (all folders that did not make the core). Any beyond this stay in the "+N more" beacon.</summary>
    [Export] public int MaxArchiveFolders { get; set; } = 200;
    /// <summary>
    /// Where the Folder Island sits, as a world offset (x, z) from the main island's centre. Vector2.Zero = automatic: next to the
    /// main island, clear of every satellite, joined by a bridge. A fixed offset is honoured up to <see cref="MaxFolderIslandOffset"/>
    /// (beyond that the camera could not reach it: far plane 1500, zoom limit 280, pan bounds follow the world radius), then pushed
    /// outward until it overlaps nothing. No bridge is built for islands more than 150 units away.
    /// </summary>
    [Export] public Vector2 FolderIslandOffset { get; set; } = Vector2.Zero;
    public const float MaxFolderIslandOffset = 400f;
    /// <summary>
    /// Optional view filter for FOLDERS: return false to keep a folder out of the main-island core. Such folders are never hidden;
    /// they go to the Folder Island like every other non-core folder. null = no filter.
    /// </summary>
    public Func<FsEntry, bool> FolderViewFilter { get; set; }
    public int ArchiveCount { get; private set; }
    [Export] public PackedScene FileBuildingScene { get; set; }
    [Export] public PackedScene PlatformScene { get; set; }
    [Export] public PackedScene BridgeScene { get; set; }

    // Injected by MainWorld
    public PropertyMappingConfig Mapping { get; set; }
    public BuildingAssetConfig Assets { get; set; }
    public Node3D TerrainRoot { get; set; }
    public Node3D BridgesRoot { get; set; }
    public Node3D DecorRoot { get; set; }
    public Node3D BuildingsRoot { get; set; }
    public Node3D OverflowRoot { get; set; }

    // Results / state
    public string CurrentPath { get; private set; }
    public int TotalCount { get; private set; }
    public int OverflowCount { get; private set; }
    public float WorldRadius { get; private set; } = 40f;
    public IReadOnlyList<FileBuilding> Buildings => _buildings;
    public IReadOnlyList<IslandInfo> Islands => _islands;
    public IReadOnlyList<FsEntry> AllEntries => _all;

    /// <summary>Raised on the main thread after a directory is loaded: (path, totalItems, overflow).</summary>
    public event Action<string, int, int> DirectoryLoaded;
    public event Action<string, string> LoadFailed;
    /// <summary>Raised after every rebuild so the HUD can reposition island label cards.</summary>
    public event Action<IReadOnlyList<IslandInfo>> IslandsRebuilt;
    /// <summary>Raised after every rebuild with the visible entries (usage-tier thresholds for the info panel).</summary>
    public event Action WorldRebuilt;

    readonly List<FsEntry> _all = new();
    readonly List<FileBuilding> _buildings = new();
    readonly Dictionary<string, FileBuilding> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IslandInfo> _islands = new();
    readonly DirectoryWatcherService _watcher = new();
    readonly FolderSizeService _sizes = new();

    CancellationTokenSource _cts = new();
    int _navGen;
    string _selectedPath;
    string _search = "";
    bool _rebuildQueued, _sizeDirty;
    double _maxVisibleScore = 1;
    MeshInstance3D _core;
    float _coreBaseY;
    double _time;

    public override void _Ready()
    {
        _watcher.Changed += OnWatcherChanged;
    }

    public override void _ExitTree()
    {
        _layoutTween?.Kill();
        _watcher.Changed -= OnWatcherChanged;
        _watcher.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        UsageTracker.Instance.Flush();
    }

    public override void _Process(double delta)
    {
        if (_core == null || !IsInstanceValid(_core)) return;
        _time += delta;
        _core.RotationDegrees = new Vector3(0, (float)(_time * 40.0), 0);
        _core.Position = new Vector3(_core.Position.X, _coreBaseY + Mathf.Sin((float)_time * 1.4f) * 0.18f, _core.Position.Z);
    }

    // ============================================================== loading

    public void LoadDirectory(string path)
    {
        if (Mapping == null) Mapping = PropertyMappingConfig.CreateDefault();
        if (Assets == null) Assets = BuildingAssetConfig.CreateDefault();

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { LoadFailed?.Invoke(path, "Invalid path: " + ex.Message); return; }
        if (!Directory.Exists(full)) { LoadFailed?.Invoke(full, "That folder does not exist or is not accessible."); return; }
        string denied = DriveGuard.Check(full);   // local / removable drives only, and never a link or cloud-only folder
        if (denied != null) { LoadFailed?.Invoke(full, denied); return; }

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int gen = ++_navGen;
        bool hidden = ShowHidden;

        Task.Run(() =>
        {
            try
            {
                var list = Enumerate(full, hidden, ct);
                Callable.From(() => ApplyListing(gen, full, list)).CallDeferred();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                string msg = ex is UnauthorizedAccessException ? "Access to this folder was denied." : ex.Message;
                Callable.From(() => { if (gen == _navGen) LoadFailed?.Invoke(full, msg); }).CallDeferred();
            }
        });
    }

    public void Refresh() { if (CurrentPath != null) LoadDirectory(CurrentPath); }

    /// <summary>Re-reads the folder and restarts the FileSystemWatcher (which can stop delivering events after an error).</summary>
    public void ForceRefresh()
    {
        if (CurrentPath == null) return;
        _watcher.Start(CurrentPath);
        LoadDirectory(CurrentPath);
    }

    static List<FsEntry> Enumerate(string path, bool showHidden, CancellationToken ct)
    {
        var list = new List<FsEntry>();
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            // links, junctions, cloud placeholders and offline files are skipped by the OS enumeration itself (DriveGuard)
            AttributesToSkip = (showHidden ? 0 : (FileAttributes.Hidden | FileAttributes.System)) | DriveGuard.SkipAttributes,
        };
        var di = new DirectoryInfo(path);
        foreach (var fi in di.EnumerateFileSystemInfos("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            try { list.Add(FsEntry.FromInfo(fi)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* one unreadable entry must never abort the scan */ }
            if (list.Count >= 50000) break; // hard safety cap for pathological folders
        }
        return list;
    }

    void ApplyListing(int gen, string path, List<FsEntry> list)
    {
        if (gen != _navGen || !IsInsideTree()) return;
        bool pathChanged = !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase);
        CurrentPath = path;
        if (pathChanged) { _selectedPath = null; _watcher.Start(path); }

        foreach (var e in list)
            if (e.IsDirectory && _sizes.TryGet(e.Path, out var sz)) e.RecursiveSize = sz;
        _all.Clear();
        _all.AddRange(list);

        Rebuild(animate: true);
        StartFolderSizeJobs(gen);
        DirectoryLoaded?.Invoke(path, TotalCount, OverflowCount);
    }

    // ============================================================== live sync

    void OnWatcherChanged(bool fullRescan)
    {
        // Thread-pool thread: only marshal, never touch nodes.
        Callable.From(RefreshFromWatcher).CallDeferred();
    }

    void RefreshFromWatcher()
    {
        if (CurrentPath == null || !IsInsideTree()) return;
        if (!Directory.Exists(CurrentPath)) { LoadFailed?.Invoke(CurrentPath, "The current folder was removed."); return; }
        string path = CurrentPath;
        int gen = _navGen;
        var ct = _cts.Token;
        bool hidden = ShowHidden;
        Task.Run(() =>
        {
            try
            {
                var list = Enumerate(path, hidden, ct);
                Callable.From(() => ApplyRefresh(gen, path, list)).CallDeferred();
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* folder vanished or became inaccessible: the next watcher event will report it */ }
        });
    }

    void ApplyRefresh(int gen, string path, List<FsEntry> list)
    {
        if (gen != _navGen || !IsInsideTree() || !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
        var old = new Dictionary<string, FsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _all) old[e.Path] = e;

        bool structural = list.Count != _all.Count;
        var changedDirs = new List<string>();
        foreach (var e in list)
        {
            if (old.TryGetValue(e.Path, out var o))
            {
                bool modified = o.Modified != e.Modified || o.Size != e.Size;
                if (modified) { structural = true; if (e.IsDirectory) changedDirs.Add(e.Path); }
                if (e.IsDirectory && o.RecursiveSize >= 0 && !modified) e.RecursiveSize = o.RecursiveSize;
            }
            else { structural = true; }
        }
        var newPaths = new HashSet<string>(list.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var kv in old) if (!newPaths.Contains(kv.Key)) _sizes.Invalidate(kv.Key);
        foreach (var d in changedDirs) _sizes.Invalidate(d);
        if (!structural) return;

        foreach (var e in list)
            if (e.IsDirectory && e.RecursiveSize < 0 && _sizes.TryGet(e.Path, out var sz)) e.RecursiveSize = sz;
        _all.Clear();
        _all.AddRange(list);
        Rebuild(animate: false);
        StartFolderSizeJobs(gen);
        DirectoryLoaded?.Invoke(CurrentPath, TotalCount, OverflowCount);
    }

    // ============================================================== folder sizes

    void StartFolderSizeJobs(int gen)
    {
        var ct = _cts.Token;
        foreach (var e in _all.Where(x => x.IsDirectory && x.RecursiveSize < 0 && !x.IsReparsePoint).Take(MaxVisibleBuildings))
        {
            string p = e.Path;
            _ = RunSizeJob(p, gen, ct);
        }
    }

    async Task RunSizeJob(string path, int gen, CancellationToken ct)
    {
        try
        {
            long size = await _sizes.ComputeAsync(path, ct).ConfigureAwait(false);
            Callable.From(() => OnFolderSize(gen, path, size)).CallDeferred();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* inaccessible folder: leave size unknown */ }
    }

    void OnFolderSize(int gen, string path, long size)
    {
        if (gen != _navGen || !IsInsideTree()) return;
        var e = _all.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (e == null) return;
        e.RecursiveSize = size;
        _sizeDirty = true;
        QueueRebuild(1.5, false);
    }

    // ============================================================== rebuild scheduling

    /// <summary>Re-applies the mapping without touching the filesystem (debounced).</summary>
    public void RebuildSoon() { _sizeDirty = true; QueueRebuild(0.12, false); }

    void QueueRebuild(double seconds, bool animate)
    {
        if (_rebuildQueued || !IsInsideTree()) return;
        _rebuildQueued = true;
        GetTree().CreateTimer(seconds).Timeout += () =>
        {
            _rebuildQueued = false;
            if (!IsInsideTree()) return;
            if (_sizeDirty) { _sizeDirty = false; Rebuild(animate); }
        };
    }

    // ============================================================== world construction

    void ClearWorld()
    {
        _layoutTween?.Kill();   // never two layout tweens at once, and never one that targets freed nodes
        _layoutTween = null;
        _moves.Clear();
        foreach (var root in new[] { TerrainRoot, BridgesRoot, DecorRoot, BuildingsRoot, OverflowRoot })
        {
            if (root == null) continue;
            foreach (var c in root.GetChildren())
            {
                root.RemoveChild(c);
                c.QueueFree();
            }
        }
        _buildings.Clear();
        _byPath.Clear();
        _islands.Clear();
        _core = null;
    }

    sealed class Group
    {
        public string Name = "";
        public Color Color;
        public List<FsEntry> Items = new();
        public RingLayoutResult Layout;
        public float Radius;
        public Vector3 Center;
        public float AngleDeg;   // screen angle of a satellite island (used to keep the Archive island clear of it)
        public int Ring;         // index of the concentric ring the island sits on (0 = innermost)
        public Vector3 Dir;      // unit direction hub -> island, computed from the SAME angle that placed the island (bridges use it)
    }

    IslandRingSettings RingSettings() => new IslandRingSettings
    {
        InnerRingRadius = InnerRingRadius, RingRadiusIncrement = RingRadiusIncrement, MinimumIslandSpacing = MinimumIslandSpacing,
        HubIslandGap = HubIslandGap, MinimumRingIslandCount = MinimumRingIslandCount, SingleRingMaxIslands = SingleRingMaxIslands,
        MaxIslandRings = MaxIslandRings, RingSeparation = RingSeparation, BridgeWidth = BridgeClearanceWidth, StartAngleDeg = FirstRingStartAngleDeg,
    };

    // island relocation on refresh: islands are matched by name between two rebuilds of the SAME folder
    Dictionary<string, Vector3> _prevCenters = new();
    string _prevPath;
    Tween _layoutTween;
    readonly List<(Node3D node, Vector3 from, Vector3 to)> _moves = new();

    void Rebuild(bool animate)
    {
        if (BuildingsRoot == null || TerrainRoot == null) return;
        ClearWorld();

        var tracker = UsageTracker.Instance;
        foreach (var e in _all) tracker.Annotate(e);
        TotalCount = _all.Count;

        // 1. relevance ranking -> cap -> overflow
        var ranked = _all.OrderByDescending(e => e.UsageScore).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Usage-based split. Folders are ranked by the same relevance score as everything else (UsageTracker: recency-weighted
        // usage, then name). The top CoreFolderCount folders form the core of the main island; EVERY other folder goes to the
        // secondary Folder Island, so no folder is ever hidden and the whole tree stays navigable.
        var folders = ranked.Where(x => x.IsDirectory).ToList();
        var coreCandidates = FolderViewFilter == null ? folders : folders.Where(FolderViewFilter).ToList();
        var coreFolders = coreCandidates.Take(Math.Max(0, CoreFolderCount)).ToList();
        var coreSet = new HashSet<FsEntry>(coreFolders);
        var archiveFolders = folders.Where(f => !coreSet.Contains(f)).Take(Math.Max(0, MaxArchiveFolders)).ToList();
        ArchiveCount = archiveFolders.Count;

        // Main island = core folders + files. The MaxVisibleBuildings cap applies to the files (core folders are never cut).
        int fileCap = Math.Max(0, Math.Max(1, MaxVisibleBuildings) - coreFolders.Count);
        var visible = coreFolders.Concat(ranked.Where(x => !x.IsDirectory).Take(fileCap)).ToList();
        OverflowCount = Math.Max(0, _all.Count - visible.Count - archiveFolders.Count);
        _maxVisibleScore = visible.Count > 0 ? Math.Max(0.0001, visible.Max(v => v.UsageScore)) : 1;

        var ctx = Mapping.BuildContext(visible);

        // 2. styles
        var styles = new Dictionary<FsEntry, BuildingStyle>();
        foreach (var e in visible) styles[e] = MakeStyle(e, ctx);
        foreach (var e in archiveFolders) styles[e] = MakeStyle(e, ctx);

        // 3. ordering key (default: relevance; optional RingDistance mapping)
        var rd = Mapping.FindRule(VisualProperty.RingDistance);
        bool useRd = rd != null && rd.Enabled && rd.Source != SourceProperty.None;
        Func<FsEntry, double> key;
        if (useRd) key = entry => PropertyMappingConfig.Normalise(rd, entry, ctx);
        else key = entry => -entry.UsageScore;
        List<FsEntry> Ordered(IEnumerable<FsEntry> src) => src.OrderBy(key).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // 4. hub = all folders + top files; satellites = grouped remainder
        var hubFiles = visible.Where(e => !e.IsDirectory).Take(Math.Max(0, HubFileCount)).ToHashSet();
        // Rule A: FOLDERS FIRST. The ring layout fills from the centre outward, so listing every folder before any file puts the
        // folders in the centre; LayoutGroup(folderCore: true) additionally starts the file rings beyond the folder core.
        var hubItems = Ordered(visible.Where(e => e.IsDirectory)).Concat(Ordered(visible.Where(e => hubFiles.Contains(e)))).ToList();
        var rest = visible.Where(e => !e.IsDirectory && !hubFiles.Contains(e)).ToList();
        var groups = BuildGroups(rest, Ordered);

        // 5. layouts
        var hub = new Group { Name = "Recently Used", Color = FileCategory.CategoryColor(FileCategory.Folder), Items = hubItems };
        LayoutGroup(hub, styles, 10f, 2.6f, folderCore: true);
        foreach (var g in groups) LayoutGroup(g, styles, 6.5f, 2.2f);
        Group archive = null;
        if (archiveFolders.Count > 0)
        {
            archive = new Group { Name = "Folder Island", Color = new Color("A7B1C2"), Items = Ordered(archiveFolders) };
            LayoutGroup(archive, styles, 6.5f, 2.2f);
        }

        // 6. place satellites around the hub in concentric rings (IslandRingLayout).
        // Islands are drawn larger than their layout radius (model armour rim), so space them by visual radius.
        float fp = FloatingPlatform.FootprintFactor;
        hub.Center = Vector3.Zero;   // the hub never moves
        var ringSettings = RingSettings();
        // One ring (<= SingleRingMaxIslands islands): keep the existing category order. More islands: ascending building count (stable, so
        // ties keep their category order) BEFORE the angular slots are handed out; islands then alternate inner / outer in angular order.
        var ordered = groups.Count > ringSettings.SingleRingMaxIslands ? groups.OrderBy(g => g.Items.Count).ToList() : groups;
        var visualRadii = ordered.Select(g => g.Radius * fp).ToList();
        var slots = IslandRingLayout.Layout(visualRadii, hub.Radius * fp, ringSettings, out float outerExtent);
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ring = slots[i].Ring;
            ordered[i].AngleDeg = slots[i].AngleDeg;
            ordered[i].Dir = ScreenDir(Mathf.DegToRad(slots[i].AngleDeg));
            ordered[i].Center = ordered[i].Dir * slots[i].Radius;   // position = hubCenter + direction(angle) * ringRadius; Y stays 0 (floating height untouched)
        }
        WorldRadius = groups.Count > 0 ? outerExtent + 2f : hub.Radius * fp + 4f;
        if (archive != null)
        {
            Vector2? fixedOffset = null;
            if (FolderIslandOffset != Vector2.Zero)
            {
                if (FolderIslandOffset.Length() <= MaxFolderIslandOffset) fixedOffset = FolderIslandOffset;
                else GD.PushWarning($"[ATLAS] FolderIslandOffset {FolderIslandOffset} is further than {MaxFolderIslandOffset} units: the camera could not reach it, so the island is placed automatically instead.");
            }
            archive.Center = PlaceArchive(hub, groups, archive, fp, out float archDist, fixedOffset);
            WorldRadius = Mathf.Max(WorldRadius, archDist + archive.Radius * fp + 2f);
        }

        // 7. build nodes
        BuildGroupNodes(hub, true, 0, styles, animate);
        for (int i = 0; i < groups.Count; i++) BuildGroupNodes(groups[i], false, i + 1, styles, animate);
        if (archive != null) BuildGroupNodes(archive, false, groups.Count + 1, styles, animate);

        void BuildAllBridges()
        {
            foreach (var g in groups)
            {
                // Same direction that placed the island: the bridge runs exactly along the hub -> island line, from the hub's deck edge
                // to the island's deck edge, so its length follows the ring radius and no extra rotation or offset is involved.
                var dir = g.Dir;
                float inset = fp > 1f ? 1.0f : 0.9f; // model islands: land bridges on the deck edge, over the armour rim
                BuildBridge(hub.Center + dir * (hub.Radius * inset), g.Center - dir * (g.Radius * inset), g.Color);
            }
            if (archive != null && archive.Center.Length() <= 150f)
            {
                var adir = archive.Center.Normalized();
                float ainset = fp > 1f ? 1.0f : 0.9f;
                BuildBridge(hub.Center + adir * (hub.Radius * ainset), archive.Center - adir * (archive.Radius * ainset), archive.Color);
            }
        }
        // Islands that changed position since the last rebuild of this folder glide there; their bridges appear when they arrive.
        if (_moves.Count > 0) StartRelocation(BuildAllBridges);
        else BuildAllBridges();

        BuildHubCore(hub, styles);
        BuildOverflowBeacon(hub);

        _prevCenters = new Dictionary<string, Vector3>();
        foreach (var g in groups) _prevCenters[g.Name] = g.Center;
        if (archive != null) _prevCenters[archive.Name] = archive.Center;
        _prevPath = CurrentPath;

        if (!string.IsNullOrEmpty(_selectedPath) && _byPath.TryGetValue(_selectedPath, out var sel)) sel.SetSelected(true);
        ApplySearch();
        IslandsRebuilt?.Invoke(_islands);
        WorldRebuilt?.Invoke();
    }

    /// <summary>One tween for all moving islands (X/Z only, so a selected building's lift on Y is not disturbed); a new rebuild kills it first.</summary>
    void StartRelocation(Action onArrived)
    {
        var moves = _moves.ToArray();
        _moves.Clear();
        _layoutTween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _layoutTween.TweenMethod(Callable.From<float>(t =>
        {
            foreach (var m in moves)
            {
                if (!IsInstanceValid(m.node)) continue;
                var pos = m.node.Position;
                pos.X = Mathf.Lerp(m.from.X, m.to.X, t);
                pos.Z = Mathf.Lerp(m.from.Z, m.to.Z, t);
                m.node.Position = pos;
            }
        }), 0f, 1f, Mathf.Max(0.05f, IslandMoveDuration));
        _layoutTween.TweenCallback(Callable.From(onArrived));
    }

    /// <summary>World-space direction for a screen angle (0 = screen right, 90 = screen up) with the default camera yaw of 45 degrees.</summary>
    static Vector3 ScreenDir(float psi)
    {
        const float s = 0.70710678f;
        float c = Mathf.Cos(psi), sn = Mathf.Sin(psi);
        return new Vector3(s * (c - sn), 0, -s * (c + sn));
    }

    // ---------------------------------------------------------------- groups

    static readonly Color[] Palette =
    {
        new Color("3B82F6"), new Color("A855F7"), new Color("22C55E"), new Color("FACC15"),
        new Color("FF6A3D"), new Color("EF4444"), new Color("14B8A6"), new Color("A7B1C2"),
    };

    List<Group> BuildGroups(List<FsEntry> rest, Func<IEnumerable<FsEntry>, List<FsEntry>> order)
    {
        var mode = Mapping.GroupBy;
        var buckets = new Dictionary<string, List<FsEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in rest)
        {
            string k = GroupKey(e, mode);
            if (!buckets.TryGetValue(k, out var l)) buckets[k] = l = new List<FsEntry>();
            l.Add(e);
        }

        IEnumerable<string> keys;
        if (mode == GroupMode.FileType) keys = FileCategory.Ordered.Where(buckets.ContainsKey);
        else keys = buckets.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key);

        var keyList = keys.ToList();

        // A category keeps its colour whether it has its own island or not, so colours do not shift when small categories fold into Others.
        var colours = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        int ci = 0;
        foreach (var k in keyList)
            colours[k] = mode == GroupMode.FileType ? FileCategory.CategoryColor(k) : Palette[ci++ % Palette.Length];

        // GroupMode.None is one "All items" island by design (there are no categories to compare), so the Others rule does not apply to it.
        if (mode == GroupMode.None)
            return keyList.Select(k => new Group { Name = k, Color = colours[k], Items = order(buckets[k]) }).ToList();

        // The Others rule: categories with more than OthersThreshold buildings get an island; the rest share ONE "Others" island
        // (also the overflow target when there would be more than MaxSatelliteIslands). Same FsEntry objects, only their island changes.
        var islands = IslandGrouping.Group(
            keyList.Select(k => new KeyValuePair<string, List<FsEntry>>(k, buckets[k])),
            OthersThreshold, Math.Max(2, MaxSatelliteIslands));
        return islands.Select(b => new Group
        {
            Name = b.Name,
            Color = b.IsOthers ? FileCategory.CategoryColor(FileCategory.Other) : colours[b.Name],
            Items = order(b.Items),
        }).ToList();
    }

    static string GroupKey(FsEntry e, GroupMode mode)
    {
        switch (mode)
        {
            case GroupMode.FileType: return e.Category;
            case GroupMode.Extension: return e.Extension.Length > 0 ? "." + e.Extension : "No extension";
            case GroupMode.LastModified:
                if (!e.HasModified) return "Unknown";
                double d = (DateTime.Now - e.Modified).TotalDays;
                return d < 1 ? "Today" : d < 7 ? "This week" : d < 31 ? "This month" : d < 365 ? "This year" : "Older";
            case GroupMode.SizeBucket:
                long s = Math.Max(0, e.Size);
                return s < 64L * 1024 ? "Tiny" : s < 1024L * 1024 ? "Small" : s < 32L * 1024 * 1024 ? "Medium" : s < 512L * 1024 * 1024 ? "Large" : "Huge";
            case GroupMode.Usage:
                // buckets of U(x): recency-weighted event count (see UsageTracker)
                return e.UsageU >= 3 ? "Frequent" : e.UsageU >= 1 ? "Regular" : e.UsageU > 0.05 ? "Occasional" : "Unused";
            default: return "All items";
        }
    }

    /// <summary>
    /// Fixed offset for the Archive island: straight down the screen (-90 degrees). If a satellite island already sits within
    /// 40 degrees of that direction, the free direction furthest from every satellite is used instead. The distance starts at the
    /// usual hub-to-satellite spacing and grows until the island clears every satellite.
    /// </summary>
    Vector3 PlaceArchive(Group hub, List<Group> groups, Group archive, float fp, out float dist, Vector2? fixedOffset = null)
    {
        float MinAngleToSatellites(float deg)
        {
            float m = float.PositiveInfinity;
            foreach (var g in groups) m = Mathf.Min(m, Mathf.Abs(Mathf.PosMod(deg - g.AngleDeg + 180f, 360f) - 180f));
            return m;
        }

        float chosen = -90f;
        if (groups.Count > 0 && MinAngleToSatellites(chosen) < 40f)
        {
            float best = -1f;
            for (int d = -90; d < 270; d += 15)
            {
                float m = MinAngleToSatellites(d);
                if (m > best + 0.01f) { best = m; chosen = d; }
            }
        }

        var dir = ScreenDir(Mathf.DegToRad(chosen));
        float d0 = hub.Radius * fp + archive.Radius * fp + 7f;
        if (fixedOffset.HasValue)
        {
            // fixed world offset (x, z): keep its direction and distance, but never closer than the non-overlap minimum
            var fo = new Vector3(fixedOffset.Value.X, 0f, fixedOffset.Value.Y);
            dir = fo.Normalized();
            d0 = Mathf.Max(d0, fo.Length());
        }
        for (int it = 0; it < 200; it++)
        {
            var c = dir * d0;
            bool clear = (c - hub.Center).Length() >= (archive.Radius + hub.Radius) * fp + 3f;
            foreach (var g in groups)
                if ((c - g.Center).Length() < (archive.Radius + g.Radius) * fp + 3f) { clear = false; break; }
            if (clear) break;
            d0 += 2f;
        }
        dist = d0;
        return dir * d0;
    }

    void LayoutGroup(Group g, Dictionary<FsEntry, BuildingStyle> styles, float minRadius, float margin, bool folderCore = false)
    {
        var fp = new List<float>(g.Items.Count);
        foreach (var e in g.Items) fp.Add(Footprint(styles[e]));

        // folderCore: Items are ordered folders-first. The folders are the core (first one dead centre); the files start on rings
        // beyond the core's outer edge (SpatialRingLayout.ComputeCoreAndRings), so a file never sits among the folders.
        int nf = 0;
        if (folderCore) while (nf < g.Items.Count && g.Items[nf].IsDirectory) nf++;
        if (folderCore && nf > 0 && nf < g.Items.Count)
            g.Layout = SpatialRingLayout.ComputeCoreAndRings(fp.GetRange(0, nf), fp.GetRange(nf, fp.Count - nf), MinSpacing);
        else g.Layout = SpatialRingLayout.Compute(fp, MinSpacing, centerFirst: true);
        g.Radius = Mathf.Max(minRadius, g.Layout.OuterRadius + margin);
    }

    static float Footprint(BuildingStyle s) => s.Shape == BuildingShape.Cylinder ? s.HalfExtent + 0.15f : s.HalfExtent * 1.42f + 0.15f;

    // ---------------------------------------------------------------- styles

    BuildingStyle MakeStyle(FsEntry e, MappingContext ctx)
    {
        float scale = Mathf.Clamp(Mapping.EvalScalar(VisualProperty.Scale, e, ctx, 1f), 0.3f, 3f);
        float he = BaseHalfExtent * scale * (e.IsDirectory ? 1.3f : 1f);
        float h = Mapping.EvalScalar(VisualProperty.Height, e, ctx, 3f * scale);
        h = Mathf.Clamp(h * (e.IsDirectory ? 1.15f : 1f), 0.9f, 14f);

        var cat = FileCategory.CategoryColor(e.Category);
        Color baseCol = Mapping.TryEvalColor(VisualProperty.BaseColor, e, ctx, out var bc) ? bc : cat.Lerp(new Color(0.82f, 0.88f, 0.98f), 0.62f);
        Color accent;
        if (Mapping.AccentOverrideEnabled) accent = Mapping.AccentOverride;
        else if (Mapping.TryEvalColor(VisualProperty.AccentColor, e, ctx, out var ac)) accent = ac;
        else accent = cat;

        float glow = Mathf.Clamp(Mapping.EvalScalar(VisualProperty.GlowIntensity, e, ctx, 2f), 0.2f, 6f);
        float opacity = Mathf.Clamp(Mapping.EvalScalar(VisualProperty.Opacity, e, ctx, 1f), 0.1f, 1f);

        var asset = Assets?.Resolve(e);
        var icon = asset?.Icon ?? IconLib.ForEntry(e, cat.Lerp(Colors.White, 0.7f), 96);

        return new BuildingStyle
        {
            HalfExtent = he,
            Height = h,
            Shape = e.IsDirectory ? BuildingShape.Tiered : (e.Category == FileCategory.Media ? BuildingShape.Cylinder : BuildingShape.Box),
            BaseColor = baseCol,
            AccentColor = accent,
            Glow = glow,
            Opacity = opacity,
            Icon = icon,
            AlbedoTexture = asset?.AlbedoTexture,
            MaterialOverride = asset?.MaterialOverride,
        };
    }

    // ---------------------------------------------------------------- nodes

    void BuildGroupNodes(Group g, bool isHub, int seed, Dictionary<FsEntry, BuildingStyle> styles, bool animate)
    {
        var platform = (PlatformScene?.Instantiate() as FloatingPlatform) ?? new FloatingPlatform();
        platform.Name = isHub ? "Island_Hub" : "Island_" + g.Name;

        // Same folder, island already existed at another spot: build it at the OLD spot and glide to the new one (X/Z only).
        Vector3 old = default;
        bool moved = !isHub && string.Equals(_prevPath, CurrentPath, StringComparison.OrdinalIgnoreCase)
                     && _prevCenters.TryGetValue(g.Name, out old)
                     && new Vector2(old.X - g.Center.X, old.Z - g.Center.Z).LengthSquared() > 0.0001f;
        Vector3 start = moved ? new Vector3(old.X, g.Center.Y, old.Z) : g.Center;
        platform.Position = start;
        if (moved) _moves.Add((platform, start, g.Center));
        TerrainRoot.AddChild(platform);
        platform.Setup(g.Radius, g.Color, seed * 7919 + 13, isHub);

        float tallest = 0f;
        for (int i = 0; i < g.Items.Count; i++)
        {
            var e = g.Items[i];
            var st = styles[e];
            var p = g.Layout.Positions[i];
            var b = (FileBuildingScene?.Instantiate() as FileBuilding) ?? new FileBuilding();
            BuildingsRoot.AddChild(b);
            b.Position = start + new Vector3(p.X, 0f, p.Y);
            if (moved) _moves.Add((b, b.Position, g.Center + new Vector3(p.X, 0f, p.Y)));
            b.Setup(e, st, animate);
            _buildings.Add(b);
            _byPath[e.Path] = b;
            tallest = Mathf.Max(tallest, st.Height);
        }
        _islands.Add(new IslandInfo
        {
            Name = g.Name,
            Count = g.Items.Count,
            Color = g.Color,
            Anchor = g.Center + new Vector3(0, tallest + 2.5f, 0),
            IsHub = isHub,
        });
    }

    void BuildBridge(Vector3 from, Vector3 to, Color accent)
    {
        var bridge = (BridgeScene?.Instantiate() as Bridge) ?? new Bridge();
        bridge.Name = "Bridge";
        BridgesRoot.AddChild(bridge);
        bridge.Setup(from, to, accent);
    }

    void BuildHubCore(Group hub, Dictionary<FsEntry, BuildingStyle> styles)
    {
        if (DecorRoot == null) return;
        float top = hub.Items.Count > 0 ? styles[hub.Items[0]].Height : 1f;
        var accent = hub.Color;
        _coreBaseY = top + 2.4f;
        _core = new MeshInstance3D
        {
            Name = "HubCore",
            Mesh = new BoxMesh { Size = new Vector3(1.3f, 1.3f, 1.3f) },
            MaterialOverride = MeshKit.Glow(accent.Lerp(Colors.White, 0.35f), 3.2f),
            Position = new Vector3(0, _coreBaseY, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        var light = new OmniLight3D { OmniRange = 16f, LightColor = accent, LightEnergy = 1.4f, ShadowEnabled = false };
        _core.AddChild(light);
        DecorRoot.AddChild(_core);
    }

    void BuildOverflowBeacon(Group hub)
    {
        if (OverflowRoot == null || OverflowCount <= 0) return;
        var pos = ScreenDir(Mathf.DegToRad(-90f)) * (hub.Radius * (FloatingPlatform.FootprintFactor > 1f ? 0.62f : 0.74f)); // keep clear of the model's corner power modules
        var root = new Node3D { Name = "OverflowBeacon", Position = pos, RotationDegrees = new Vector3(0, 45, 0) };
        var glow = MeshKit.Glow(new Color("22D3EE"), 3.4f);

        root.AddChild(new MeshInstance3D { Name = "Pedestal", Mesh = new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.75f, Height = 1.2f, RadialSegments = 12 }, MaterialOverride = MeshKit.Solid(new Color("2B3550")), Position = new Vector3(0, 0.6f, 0) });
        root.AddChild(new MeshInstance3D { Name = "PlusH", Mesh = MeshKit.BeveledBox(new Vector3(1.9f, 0.5f, 0.5f), 0.1f), MaterialOverride = glow, Position = new Vector3(0, 2.5f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        root.AddChild(new MeshInstance3D { Name = "PlusV", Mesh = MeshKit.BeveledBox(new Vector3(0.5f, 1.9f, 0.5f), 0.1f), MaterialOverride = glow, Position = new Vector3(0, 2.5f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        root.AddChild(new Label3D
        {
            Name = "OverflowLabel",
            Text = $"+ {OverflowCount} more items",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 56,
            PixelSize = 0.012f,
            OutlineSize = 10,
            Modulate = new Color("BFF6FF"),
            OutlineModulate = new Color(0.02f, 0.05f, 0.12f, 1f),
            Position = new Vector3(0, 4.3f, 0),
        });

        var area = new Area3D { Name = "OverflowArea", CollisionLayer = 4, CollisionMask = 0, Monitoring = false, Monitorable = true };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2.6f, 3.6f, 2.6f) }, Position = new Vector3(0, 2.2f, 0) });
        area.SetMeta("overflow", true);
        root.AddChild(area);
        OverflowRoot.AddChild(root);
    }

    // ============================================================== interaction helpers

    public FileBuilding FindBuilding(string path) => path != null && _byPath.TryGetValue(path, out var b) ? b : null;

    public void SelectBuilding(FileBuilding building)
    {
        if (_selectedPath != null && _byPath.TryGetValue(_selectedPath, out var prev) && IsInstanceValid(prev)) prev.SetSelected(false);
        _selectedPath = building?.Entry?.Path;
        building?.SetSelected(true);
    }

    public void SetSearchFilter(string text)
    {
        _search = (text ?? "").Trim().ToLowerInvariant();
        ApplySearch();
    }

    void ApplySearch()
    {
        foreach (var b in _buildings)
        {
            if (!IsInstanceValid(b) || b.Entry == null) continue;
            b.SetDimmed(_search.Length > 0 && !b.Entry.Name.ToLowerInvariant().Contains(_search));
        }
    }

    /// <summary>"High" / "Medium" / "Low" / "None" relative to the most relevant visible item.</summary>
    public string UsageTier(FsEntry e)
    {
        if (e.UsageCount == 0 && e.UsageScore <= 0.0001) return "None";
        double r = e.UsageScore / _maxVisibleScore;
        return r >= 0.66 ? "High" : r >= 0.33 ? "Medium" : "Low";
    }

    public void SetAccentOverride(bool enabled, Color color)
    {
        Mapping.AccentOverrideEnabled = enabled;
        Mapping.AccentOverride = color;
        Mapping.NotifyChanged();
    }
}
