using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashExplorer.Services;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// Turns a real Windows directory into the building world and keeps it in sync.
///
/// Pipeline for every navigation or refresh:
///   worker thread : enumerate (System.IO) -> build EntryMetrics -> score relevance -> sort
///   main thread   : keep the top MaxVisibleBuildings -> reuse/create FileBuilding nodes
///                   -> mappings (scale/tint/glow/ring) -> SpatialRingLayout -> tween into place
///   background    : recursive folder sizes (cached, bounded concurrency) update scale when ready
///   watcher       : FileSystemWatcher events are only queued; after a debounce, one deferred call
///                   re-scans the directory and only the changed buildings are touched
///
/// Nothing here touches the scene tree from a worker thread; all hand-offs use
/// Callable.From(...).CallDeferred().
///
/// Setup: attach to a Node under MainWorld. Every export is optional - unset ones are created or
/// loaded automatically (see _Ready). Wire OverflowBeacon in the Inspector to show the "+" node.
/// </summary>
public partial class WorldGenerator : Node
{
    // ------------------------------------------------------------------ signals

    /// <summary>A directory load started (buildings of the old directory are still visible).</summary>
    [Signal] public delegate void DirectoryLoadingEventHandler(string path);

    /// <summary>A navigation finished. cameFromChild is the child folder just left when going up, else "".</summary>
    [Signal] public delegate void DirectoryLoadedEventHandler(string path, int totalCount, int visibleCount, Rect2 bounds, string cameFromChild);

    /// <summary>Layout changed without a navigation (watcher refresh, size update, mapping edit).</summary>
    [Signal] public delegate void LayoutUpdatedEventHandler(Rect2 bounds);

    [Signal] public delegate void BuildingHoveredEventHandler(FileBuilding building);
    [Signal] public delegate void BuildingUnhoveredEventHandler(FileBuilding building);
    [Signal] public delegate void BuildingClickedEventHandler(FileBuilding building);

    /// <summary>A file was double-clicked. Part 2's WindowsShellManager opens it.</summary>
    [Signal] public delegate void FileActivatedEventHandler(string path);

    /// <summary>The overflow state changed. HUD binds to this to show "+ N more items".</summary>
    [Signal] public delegate void OverflowChangedEventHandler(bool active, int hiddenCount, string directory);

    /// <summary>The overflow "+" was clicked. Part 2 opens the directory in Windows Explorer.</summary>
    [Signal] public delegate void OverflowActivatedEventHandler(string directory);

    /// <summary>User-facing message. severity: 0 info, 1 warning, 2 error.</summary>
    [Signal] public delegate void NoticeEventHandler(string message, int severity);

    // ------------------------------------------------------------------ exports

    [ExportGroup("References (all optional)")]
    [Export] public PackedScene? BuildingScene { get; set; }
    [Export] public Node2D? BuildingsRoot { get; set; }
    [Export] public OverflowBeacon? Overflow { get; set; }
    [Export] public BuildingAssetConfig? AssetConfig { get; set; }
    [Export] public PropertyMappingConfig? MappingConfig { get; set; }
    [Export] public SpatialRingLayout? Layout { get; set; }

    [ExportGroup("Behaviour")]
    /// <summary>Folder opened at startup. Empty = the user's profile folder.</summary>
    [Export] public string InitialDirectory { get; set; } = "";
    /// <summary>Only this many highest-priority items become buildings; the rest sit behind the "+" button.</summary>
    [Export(PropertyHint.Range, "10,1000,1")] public int MaxVisibleBuildings { get; set; } = 100;
    [Export] public bool ShowHiddenItems { get; set; } = false;
    [Export(PropertyHint.Range, "100,3000,10")] public int WatcherDebounceMs { get; set; } = 350;
    [Export] public bool AnimateLayoutChanges { get; set; } = true;
    [Export(PropertyHint.Range, "1,8,1")] public int FolderSizeConcurrency { get; set; } = 2;

    // ------------------------------------------------------------------ public state

    public string CurrentDirectory { get; private set; } = "";
    public UsageTracker? Usage { get; private set; }
    public int TotalItemCount { get; private set; }
    public int HiddenItemCount { get; private set; }
    public Rect2 LastBounds { get; private set; } = new Rect2(-400f, -300f, 800f, 600f);

    /// <summary>Most recent ring layout (ring radii, angles, footprints). The terrain draws its platforms from it.</summary>
    public LayoutResult? LastLayout { get; private set; }
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>All current buildings keyed by absolute path. Used by labels, ghosts and selection (Part 2).</summary>
    public IReadOnlyDictionary<string, FileBuilding> Buildings => _buildings;

    /// <summary>Items outside this directory that are statistically related to items inside it (ghost candidates).</summary>
    public List<RelatedPath> GetGhostCandidates(int max = 8) =>
        Usage != null && CurrentDirectory.Length > 0
            ? Usage.GetRelatedForDirectory(CurrentDirectory, max)
            : new List<RelatedPath>();

    // ------------------------------------------------------------------ private state

    private enum NavigationKind { User, Back, Forward, Refresh }

    private sealed class ScanResult
    {
        public string Directory { get; init; } = "";
        public List<EntryMetrics> Entries { get; init; } = new();
    }

    private readonly struct VisualSpec
    {
        public VisualSpec(Texture2D texture, float scale, Color tint, float glow, float footprint, float ring, int cluster)
        {
            Texture = texture; Scale = scale; Tint = tint; Glow = glow;
            Footprint = footprint; RingPreference = ring; Cluster = cluster;
        }
        public Texture2D Texture { get; }
        public float Scale { get; }
        public Color Tint { get; }
        public float Glow { get; }
        public float Footprint { get; }
        public float RingPreference { get; }
        public int Cluster { get; }
    }

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".cab", ".iso"
    };

    private readonly Dictionary<string, FileBuilding> _buildings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EntryMetrics> _visibleByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EntryMetrics> _visible = new();
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    private FolderSizeCalculator? _sizes;
    private CancellationTokenSource? _scanCts;
    private int _generation;
    private bool _refreshQueued;

    private FileSystemWatcher? _watcher;
    private string _watcherDirectory = "";
    private System.Threading.Timer? _debounce;
    private readonly object _pendingLock = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingRescan;
    private bool _shuttingDown;

    // ================================================================== lifecycle

    public override void _Ready()
    {
        BuildingScene ??= GD.Load<PackedScene>("res://Scenes/FileBuilding.tscn");
        AssetConfig ??= BuildingAssetConfig.CreateDefault();
        MappingConfig ??= PropertyMappingConfig.LoadOrDefault();
        Layout ??= new SpatialRingLayout();

        // Scene layout fallback: MainWorld/WorldContent/{FileBuildings, OverflowPlusNode, WorldGenerator}.
        BuildingsRoot ??= GetNodeOrNull<Node2D>("../FileBuildings");
        Overflow ??= GetNodeOrNull<OverflowBeacon>("../OverflowPlusNode");
        if (BuildingsRoot == null)
        {
            BuildingsRoot = new Node2D { Name = "FileBuildings" };
            AddChild(BuildingsRoot);
        }
        BuildingsRoot.YSortEnabled = true;

        Usage = new UsageTracker(log: msg => Report(msg, 1));
        _sizes = new FolderSizeCalculator(FolderSizeConcurrency);
        _debounce = new System.Threading.Timer(
            _ => Callable.From(() => FlushWatcherChanges()).CallDeferred(),
            null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        MappingConfig.Changed += OnMappingChanged;
        if (Overflow != null) Overflow.Activated += OnOverflowActivated;
        if (Overflow != null) Overflow.Visible = false;

        string start = string.IsNullOrWhiteSpace(InitialDirectory)
            ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
            : InitialDirectory;
        Callable.From(() => LoadDirectory(start)).CallDeferred();
    }

    public override void _ExitTree()
    {
        _shuttingDown = true;
        StopWatcher();
        _debounce?.Dispose();
        _debounce = null;
        _scanCts?.Cancel();
        _sizes?.Dispose();
        if (MappingConfig != null) MappingConfig.Changed -= OnMappingChanged;
        if (Overflow != null && IsInstanceValid(Overflow)) Overflow.Activated -= OnOverflowActivated;
        foreach (FileBuilding b in _buildings.Values)
        {
            if (IsInstanceValid(b)) DisconnectBuilding(b);
        }
        Usage?.Dispose();
        Usage = null;
    }

    // ================================================================== navigation API

    /// <summary>Loads a directory typed or picked by the user (adds a back-history entry).</summary>
    public void LoadDirectory(string path) => NavigateInternal(path, NavigationKind.User, recordVisit: true);

    /// <summary>Double-click on a folder building: records an explicit folder open, then navigates.</summary>
    public void OpenFolder(string path)
    {
        Usage?.RecordFolderOpened(path);
        NavigateInternal(path, NavigationKind.User, recordVisit: false);
    }

    public void Refresh()
    {
        if (CurrentDirectory.Length == 0) return;
        _sizes?.Clear();
        NavigateInternal(CurrentDirectory, NavigationKind.Refresh, recordVisit: false);
    }

    public void GoUp()
    {
        if (CurrentDirectory.Length == 0) return;
        string? parent = Path.GetDirectoryName(CurrentDirectory);
        if (string.IsNullOrEmpty(parent))
        {
            Report("Already at the top of this drive.", 0);
            return;
        }
        NavigateInternal(parent, NavigationKind.User, recordVisit: true);
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        string target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        NavigateInternal(target, NavigationKind.Back, recordVisit: true);
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        string target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        NavigateInternal(target, NavigationKind.Forward, recordVisit: true);
    }

    private void NavigateInternal(string path, NavigationKind kind, bool recordVisit)
    {
        string target = UsageTracker.NormalizePath(path);
        if (target.Length == 0 || !Directory.Exists(target))
        {
            Report($"Folder not found: {path}", 2);
            return;
        }

        string previous = CurrentDirectory;
        bool changed = !string.Equals(previous, target, StringComparison.OrdinalIgnoreCase);

        if (changed && previous.Length > 0)
        {
            switch (kind)
            {
                case NavigationKind.User:
                    _back.Add(previous);
                    if (_back.Count > 100) _back.RemoveAt(0);
                    _forward.Clear();
                    break;
                case NavigationKind.Back:
                    _forward.Add(previous);
                    break;
                case NavigationKind.Forward:
                    _back.Add(previous);
                    break;
            }
            Usage?.RecordNavigation(previous, target);
        }
        if (changed && recordVisit) Usage?.RecordDirectoryVisit(target);

        string cameFromChild = "";
        if (changed && previous.Length > 0)
        {
            string? previousParent = Path.GetDirectoryName(previous);
            if (previousParent != null && string.Equals(previousParent, target, StringComparison.OrdinalIgnoreCase))
            {
                cameFromChild = previous;
            }
        }

        CurrentDirectory = target;
        StopWatcher();
        _sizes?.CancelPending();
        int generation = ++_generation;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        EmitSignal(SignalName.DirectoryLoading, target);
        RunScan(target, generation, cameFromChild, _scanCts.Token, announce: true,
            animate: AnimateLayoutChanges, fallbackDirectory: changed ? previous : "");
    }

    // ================================================================== scanning (worker thread)

    private void RunScan(string directory, int generation, string cameFromChild, CancellationToken token,
        bool announce, bool animate, string fallbackDirectory)
    {
        Task.Run(() =>
        {
            try
            {
                ScanResult scan = Scan(directory, token);
                Callable.From(() =>
                {
                    if (_shuttingDown || generation != _generation) return;
                    ApplyScan(scan, animate, announce, cameFromChild);
                    StartWatcher(directory);
                }).CallDeferred();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer navigation.
            }
            catch (Exception ex)
            {
                Report($"Cannot read '{directory}': {ex.Message}", 2);
                Callable.From(() =>
                {
                    if (_shuttingDown || generation != _generation) return;
                    if (fallbackDirectory.Length > 0 && Directory.Exists(fallbackDirectory))
                    {
                        CurrentDirectory = fallbackDirectory;
                        StartWatcher(fallbackDirectory);
                    }
                }).CallDeferred();
            }
        }, token);
    }

    private ScanResult Scan(string directory, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = ShowHiddenItems ? (FileAttributes)0 : FileAttributes.Hidden | FileAttributes.System
        };

        var entries = new List<EntryMetrics>();
        foreach (FileSystemInfo info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            EntryMetrics? metrics = BuildMetrics(info);
            if (metrics != null) entries.Add(metrics);
        }

        // Relevance scoring: app-recorded usage first, filesystem timestamps as a complement.
        DateTime now = DateTime.UtcNow;
        PropertyMappingConfig config = MappingConfig!;
        Dictionary<string, UsageSnapshot> snapshots = Usage != null
            ? Usage.GetSnapshots(entries.Select(e => e.Path))
            : new Dictionary<string, UsageSnapshot>();

        foreach (EntryMetrics e in entries)
        {
            if (snapshots.TryGetValue(e.Path, out UsageSnapshot snap))
            {
                e.UsageCount = snap.Count;
                e.LastInteractionUtc = snap.LastUtc;
            }
            e.UsageScore = config.ComputeRelevance(e.UsageCount, e.LastInteractionUtc, e.ModifiedUtc, e.AccessedUtc, now);
        }

        entries.Sort((a, b) =>
        {
            int c = b.UsageScore.CompareTo(a.UsageScore);
            if (c != 0) return c;
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new ScanResult { Directory = directory, Entries = entries };
    }

    private static EntryMetrics? BuildMetrics(FileSystemInfo info)
    {
        try
        {
            bool isFolder = (info.Attributes & FileAttributes.Directory) != 0;
            string extension = isFolder ? "" : BuildingAssetConfig.NormalizeExtension(info.Extension);
            long size = -1;
            if (!isFolder && info is FileInfo file)
            {
                try
                {
                    size = file.Length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    size = -1;
                }
            }

            return new EntryMetrics
            {
                Path = info.FullName,
                Name = info.Name,
                Extension = extension,
                IsFolder = isFolder,
                IsArchive = !isFolder && ArchiveExtensions.Contains(extension),
                SizeBytes = size,
                ModifiedUtc = ReliableTime(info.LastWriteTimeUtc),
                AccessedUtc = ReliableTime(info.LastAccessTimeUtc),
                CreatedUtc = ReliableTime(info.CreationTimeUtc),
                Depth = 0
            };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null; // vanished while enumerating
        }
    }

    /// <summary>Windows reports 1601 for missing timestamps; treat anything before 1980 as unavailable.</summary>
    private static DateTime? ReliableTime(DateTime utc) => utc.Year < 1980 ? null : utc;

    // ================================================================== applying results (main thread)

    private void ApplyScan(ScanResult scan, bool animate, bool announce, string cameFromChild)
    {
        int max = Math.Max(1, MaxVisibleBuildings);
        TotalItemCount = scan.Entries.Count;
        HiddenItemCount = Math.Max(0, TotalItemCount - max);

        _visible.Clear();
        _visible.AddRange(scan.Entries.Take(max));

        var keep = new HashSet<string>(_visible.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        foreach (string key in _buildings.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            RemoveBuilding(key);
        }

        // Carry folder sizes over from the cache (or from the previous entry while recalculating).
        var needSize = new List<EntryMetrics>();
        _visibleByKey.Clear();
        foreach (EntryMetrics m in _visible)
        {
            _visibleByKey[m.Path] = m;
            if (!m.IsFolder) continue;

            if (_sizes != null && _sizes.TryGetCached(m.Path, m.ModifiedUtc ?? DateTime.MinValue, out long cached))
            {
                m.RecursiveSizeBytes = cached;
            }
            else
            {
                if (_buildings.TryGetValue(m.Path, out FileBuilding? old) && old.Metrics != null)
                {
                    m.RecursiveSizeBytes = old.Metrics.RecursiveSizeBytes;
                }
                needSize.Add(m);
            }
        }

        ApplyVisualsAndLayout(_visible, animate);

        if (announce)
        {
            EmitSignal(SignalName.DirectoryLoaded, scan.Directory, TotalItemCount, _visible.Count, LastBounds, cameFromChild);
        }
        else
        {
            EmitSignal(SignalName.LayoutUpdated, LastBounds);
        }

        int generation = _generation;
        foreach (EntryMetrics m in needSize) RequestFolderSize(m, generation);
    }

    /// <summary>
    /// Creates or updates one FileBuilding per visible entry, runs the mappings and the ring
    /// layout, and tweens everything to its position. Existing nodes are reused.
    /// </summary>
    private void ApplyVisualsAndLayout(List<EntryMetrics> visible, bool animate)
    {
        if (BuildingScene == null || BuildingsRoot == null || AssetConfig == null
            || MappingConfig == null || Layout == null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        var items = new List<LayoutItem>(visible.Count);
        var buildings = new List<FileBuilding>(visible.Count);
        var created = new HashSet<FileBuilding>();

        foreach (EntryMetrics m in visible)
        {
            VisualSpec v = ComputeVisual(m, now);

            if (!_buildings.TryGetValue(m.Path, out FileBuilding? building))
            {
                building = BuildingScene.Instantiate<FileBuilding>();
                BuildingsRoot.AddChild(building);
                building.Setup(m, v.Texture, v.Scale, v.Tint, v.Glow);
                ConnectBuilding(building);
                _buildings[m.Path] = building;
                created.Add(building);
            }
            else
            {
                building.UpdateMetrics(m);
                building.UpdateLook(v.Tint, v.Glow, v.Texture);
                building.ScaleTo(v.Scale, animate ? 0.4 : 0.0);
            }

            building.FootprintRadius = v.Footprint;
            buildings.Add(building);
            items.Add(new LayoutItem
            {
                Key = m.Path,
                Radius = v.Footprint,
                RingPreference = v.RingPreference,
                ClusterGroup = v.Cluster
            });
        }

        LayoutResult layout = Layout.Compute(items);

        foreach (PlacedItem placed in layout.Placed)
        {
            FileBuilding building = buildings[placed.Index];
            building.Visible = true;
            if (created.Contains(building))
            {
                building.Position = placed.Position;
                if (animate) building.PopIn();
            }
            else
            {
                building.MoveTo(placed.Position, animate ? 0.5 : 0.0);
            }
        }
        foreach (int index in layout.Unplaced) buildings[index].Visible = false;

        LastLayout = layout;
        LastBounds = layout.Bounds.Grow(140f); // room for platforms and their shadows
        UpdateOverflow(layout);
    }

    private VisualSpec ComputeVisual(EntryMetrics m, DateTime now)
    {
        PropertyMappingConfig mapping = MappingConfig!;
        BuildingAssetConfig assets = AssetConfig!;

        // Folders whose recursive size is still being calculated get a neutral scale for now.
        float scale = mapping.ResolveScale(m, now, m.IsFolder ? 1.1f : 1.0f);
        float folderWeight = mapping.TryResolveNormalized(VisualProperty.Scale, m, now, out float t, out _) ? t : 0.5f;

        Texture2D texture = assets.GetTexture(m.Extension, m.IsFolder, folderWeight);
        scale *= assets.GetBaseScale(m.Extension, m.IsFolder);

        Color tint = mapping.ResolveTint(m, now);
        float glow = Mathf.Clamp(mapping.ResolveValue(VisualProperty.GlowIntensity, m, now, 0f), 0f, 1f);
        float ring = mapping.ResolveRingPreference(m, now);
        int cluster = Mathf.RoundToInt(mapping.ResolveValue(VisualProperty.ClusterGroup, m, now, 0f));
        float footprint = Layout!.EstimateFootprintRadius(texture.GetSize(), scale);

        return new VisualSpec(texture, scale, tint, glow, footprint, ring, cluster);
    }

    private void UpdateOverflow(LayoutResult layout)
    {
        int hidden = HiddenItemCount + layout.Unplaced.Count;
        bool active = hidden > 0;

        if (Overflow != null)
        {
            Overflow.Visible = active;
            if (active)
            {
                Overflow.Position = layout.OverflowPosition;
                Overflow.SetCount(hidden);
            }
        }
        EmitSignal(SignalName.OverflowChanged, active, hidden, CurrentDirectory);
    }

    private void RemoveBuilding(string key)
    {
        if (_buildings.TryGetValue(key, out FileBuilding? building))
        {
            _buildings.Remove(key);
            if (IsInstanceValid(building))
            {
                DisconnectBuilding(building);
                building.QueueFree();
            }
        }
    }

    // ================================================================== building signals

    private void ConnectBuilding(FileBuilding b)
    {
        b.HoverStarted += OnBuildingHoverStarted;
        b.HoverEnded += OnBuildingHoverEnded;
        b.Clicked += OnBuildingClicked;
        b.DoubleClicked += OnBuildingDoubleClicked;
    }

    private void DisconnectBuilding(FileBuilding b)
    {
        b.HoverStarted -= OnBuildingHoverStarted;
        b.HoverEnded -= OnBuildingHoverEnded;
        b.Clicked -= OnBuildingClicked;
        b.DoubleClicked -= OnBuildingDoubleClicked;
    }

    private void OnBuildingHoverStarted(FileBuilding b) => EmitSignal(SignalName.BuildingHovered, b);

    private void OnBuildingHoverEnded(FileBuilding b) => EmitSignal(SignalName.BuildingUnhovered, b);

    private void OnBuildingClicked(FileBuilding b) => EmitSignal(SignalName.BuildingClicked, b);

    private void OnBuildingDoubleClicked(FileBuilding b)
    {
        if (b.IsFolder)
        {
            OpenFolder(b.Key);
            return;
        }
        Usage?.RecordFileOpened(b.Key);
        EmitSignal(SignalName.FileActivated, b.Key);
    }

    private void OnOverflowActivated() => EmitSignal(SignalName.OverflowActivated, CurrentDirectory);

    // ================================================================== folder sizes

    private async void RequestFolderSize(EntryMetrics m, int generation)
    {
        if (_sizes == null) return;
        try
        {
            long size = await _sizes.GetSizeAsync(m.Path, m.ModifiedUtc ?? DateTime.MinValue);
            Callable.From(() => OnFolderSize(m.Path, size, generation)).CallDeferred();
        }
        catch (OperationCanceledException)
        {
            // Navigation changed; the result is no longer needed.
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[WorldGenerator] Folder size failed for {m.Path}: {ex.Message}");
        }
    }

    private void OnFolderSize(string path, long size, int generation)
    {
        if (_shuttingDown || generation != _generation) return;
        if (!_visibleByKey.TryGetValue(path, out EntryMetrics? m)) return;
        m.RecursiveSizeBytes = size;
        ScheduleVisualRefresh();
    }

    /// <summary>Coalesces many size results (or mapping edits) into one layout pass.</summary>
    private void ScheduleVisualRefresh()
    {
        if (_refreshQueued || !IsInsideTree()) return;
        _refreshQueued = true;
        GetTree().CreateTimer(0.25).Timeout += () =>
        {
            _refreshQueued = false;
            if (_shuttingDown) return;
            ApplyVisualsAndLayout(_visible, AnimateLayoutChanges);
            EmitSignal(SignalName.LayoutUpdated, LastBounds);
        };
    }

    private void OnMappingChanged()
    {
        if (_visible.Count > 0) ScheduleVisualRefresh();
    }

    // ================================================================== file system watcher

    private void StartWatcher(string directory)
    {
        if (_watcher != null && string.Equals(_watcherDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
        StopWatcher();
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                               | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime
            };
            watcher.Created += OnFsChanged;
            watcher.Deleted += OnFsChanged;
            watcher.Changed += OnFsChanged;
            watcher.Renamed += OnFsRenamed;
            watcher.Error += OnFsError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watcherDirectory = directory;
        }
        catch (Exception ex)
        {
            Report($"Live updates are unavailable for this folder: {ex.Message}", 1);
        }
    }

    private void StopWatcher()
    {
        FileSystemWatcher? watcher = _watcher;
        _watcher = null;
        _watcherDirectory = "";
        if (watcher != null)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFsChanged;
                watcher.Deleted -= OnFsChanged;
                watcher.Changed -= OnFsChanged;
                watcher.Renamed -= OnFsRenamed;
                watcher.Error -= OnFsError;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[WorldGenerator] Watcher shutdown: {ex.Message}");
            }
        }
        lock (_pendingLock)
        {
            _pendingPaths.Clear();
            _pendingRescan = false;
        }
    }

    // The three handlers below run on thread-pool threads: they only queue data, never touch nodes.

    private void OnFsChanged(object sender, FileSystemEventArgs e) => QueueChange(e.FullPath);

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        Usage?.NotifyRenamed(e.OldFullPath, e.FullPath);
        QueueChange(e.OldFullPath);
        QueueChange(e.FullPath);
    }

    private void OnFsError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or lost watcher: fall back to a controlled full rescan.
        lock (_pendingLock)
        {
            _pendingRescan = true;
        }
        RestartDebounce();
    }

    private void QueueChange(string path)
    {
        lock (_pendingLock)
        {
            _pendingPaths.Add(path);
        }
        RestartDebounce();
    }

    private void RestartDebounce()
    {
        try
        {
            _debounce?.Change(Math.Max(50, WatcherDebounceMs), System.Threading.Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>Main thread, after the debounce: one rescan covers any number of queued events.</summary>
    private void FlushWatcherChanges()
    {
        if (_shuttingDown || _watcher == null || CurrentDirectory.Length == 0) return;

        List<string> changed;
        lock (_pendingLock)
        {
            changed = _pendingPaths.ToList();
            _pendingPaths.Clear();
            _pendingRescan = false;
        }

        foreach (string path in changed) _sizes?.Invalidate(path);

        if (!Directory.Exists(CurrentDirectory))
        {
            string? fallback = Path.GetDirectoryName(CurrentDirectory);
            while (!string.IsNullOrEmpty(fallback) && !Directory.Exists(fallback))
            {
                fallback = Path.GetDirectoryName(fallback);
            }
            Report("The current folder no longer exists.", 1);
            if (!string.IsNullOrEmpty(fallback)) NavigateInternal(fallback, NavigationKind.User, recordVisit: false);
            return;
        }

        int generation = ++_generation;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        RunScan(CurrentDirectory, generation, "", _scanCts.Token, announce: false,
            animate: AnimateLayoutChanges, fallbackDirectory: "");
    }

    // ================================================================== helpers

    private void Report(string message, int severity)
    {
        if (severity >= 1) GD.PushWarning("[WorldGenerator] " + message);
        Callable.From(() =>
        {
            if (!_shuttingDown && IsInstanceValid(this)) EmitSignal(SignalName.Notice, message, severity);
        }).CallDeferred();
    }
}
