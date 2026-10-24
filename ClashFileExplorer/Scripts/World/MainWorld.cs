using System;
using System.Diagnostics;
using Godot;

namespace ClashExplorer.World;

/// <summary>
/// Root of MainWorld.tscn. Connects the world generator to the camera and provides temporary
/// stand-ins for features that Part 2 replaces:
///   - a debug status label (Part 2: FilePathAndFeatureBarUI + notifications)
///   - keyboard shortcuts for up / back / forward / refresh / reset view (Part 2: action dock)
///   - opening files and the overflow folder via Process.Start (Part 2: WindowsShellManager)
/// </summary>
public partial class MainWorld : Node2D
{
	private WorldGenerator _generator = null!;
	private IsometricCamera _camera = null!;
	private WorldTerrain _terrain = null!;
	private Label? _status;

	private string _directoryLine = "";
	private string _noticeLine = "";

	public override void _Ready()
	{
		RenderingServer.SetDefaultClearColor(new Color(0.055f, 0.065f, 0.095f));

		_generator = GetNode<WorldGenerator>("WorldContent/WorldGenerator");
		_camera = GetNode<IsometricCamera>("Camera2D");
		_terrain = GetNode<WorldTerrain>("Environment/Terrain");
		_status = GetNodeOrNull<Label>("UI/DebugLabel");

		_generator.DirectoryLoaded += OnDirectoryLoaded;
		_generator.LayoutUpdated += OnLayoutUpdated;
		_generator.BuildingClicked += OnBuildingClicked;
		_generator.FileActivated += OnFileActivated;
		_generator.OverflowActivated += OnOverflowActivated;
		_generator.OverflowChanged += OnOverflowChanged;
		_generator.Notice += OnNotice;

		UpdateStatus();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

		switch (key.Keycode)
		{
			case Key.Backspace:
				_generator.GoUp();
				break;
			case Key.F5:
				_generator.Refresh();
				break;
			case Key.Left when key.AltPressed:
				_generator.GoBack();
				break;
			case Key.Right when key.AltPressed:
				_generator.GoForward();
				break;
			case Key.Home:
				_camera.FitBounds(_generator.LastBounds);
				break;
			default:
				return;
		}
		GetViewport().SetInputAsHandled();
	}

	// ------------------------------------------------------------------ generator events

	private void OnDirectoryLoaded(string path, int totalCount, int visibleCount, Rect2 bounds, string cameFromChild)
	{
		_terrain.Rebuild(_generator.LastLayout, fade: true);
		bool restored = _camera.EnterDirectory(path, bounds);
		if (!restored && cameFromChild.Length > 0
			&& _generator.Buildings.TryGetValue(cameFromChild, out FileBuilding? child))
		{
			_camera.FocusOnBuilding(child);
		}

		_directoryLine = totalCount > visibleCount
			? $"{path}\nShowing {visibleCount} of {totalCount:N0} items"
			: $"{path}\n{totalCount:N0} items";
		_noticeLine = "";
		UpdateStatus();
	}

	private void OnLayoutUpdated(Rect2 bounds)
	{
		_camera.SetWorldBounds(bounds);
		_terrain.Rebuild(_generator.LastLayout, fade: false);
	}

	private void OnBuildingClicked(FileBuilding building)
	{
		_camera.FocusOnBuilding(building);
		string kind = building.IsFolder ? "Folder" : "File";
		_noticeLine = $"{kind}: {building.Metrics?.Name}";
		UpdateStatus();
	}

	private void OnOverflowChanged(bool active, int hiddenCount, string directory)
	{
		if (!active) return;
		_directoryLine = $"{directory}\nShowing {_generator.TotalItemCount - hiddenCount} of {_generator.TotalItemCount:N0} items - click + for the rest";
		UpdateStatus();
	}

	private void OnNotice(string message, int severity)
	{
		_noticeLine = severity >= 2 ? "Error: " + message : message;
		UpdateStatus();
	}

	// ------------------------------------------------------------------ temporary shell stand-ins

	private void OnFileActivated(string path)
	{
		try
		{
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			_noticeLine = $"Could not open file: {ex.Message}";
			UpdateStatus();
		}
	}

	private void OnOverflowActivated(string directory)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			_noticeLine = $"Could not open Explorer: {ex.Message}";
			UpdateStatus();
		}
	}

	private void UpdateStatus()
	{
		if (_status == null) return;
		string text = _directoryLine;
		if (_noticeLine.Length > 0) text += "\n" + _noticeLine;
		text += "\n\nDouble-click: open | Click: focus | Right/Middle drag: pan | Wheel: zoom\n"
				+ "Backspace: up | Alt+Left/Right: back/forward | F5: refresh | Home: reset view";
		_status.Text = text;
	}
}
