using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Atlas;

/// <summary>
/// Optional source of relationships that sits IN FRONT of the production relationship query (ActivityStore.GetRelated / GetEntity).
/// It never touches stored history, scores, thresholds or settings, and it is not persisted.
/// </summary>
public interface IRelationOverlay
{
    /// <summary>True when the overlay answers for <paramref name="id"/> (possibly with an empty list = "no relationships").</summary>
    bool TryGetRelated(string id, out List<RelatedEntity> related);
    EntityInfo GetEntity(string id);
}

/// <summary>
/// Temporary developer test fixtures for Ghost Links. Active only while the "Ghost Links test mode" switch is on (session only).
///
///   Test A  Assignment.docx -> Google Chrome, ChatGPT, Microsoft Word, Notes.docx
///   Test B  main.cpp        -> renderer.cpp, renderer.h, Visual Studio Code
///   Test C  Unrelated.txt   -> nothing (the platform must not appear)
///
/// Files are looked up by name among the buildings currently in the world; when a named file is not there, a real file of a
/// matching type from the world is used as a stand-in (reported). Applications are only used when their real executable exists
/// in a standard install location - nothing is launched from a made-up path, and a missing application is simply left out.
/// Because the Ghost Links platform only shows entities that exist on disk, Open All in test mode can only start real, installed items
/// that belong to the selected fixture.
/// </summary>
public sealed class GhostTestFixtures : IRelationOverlay
{
    readonly Dictionary<string, List<RelatedEntity>> _related = new Dictionary<string, List<RelatedEntity>>(StringComparer.Ordinal);
    readonly Dictionary<string, EntityInfo> _entities = new Dictionary<string, EntityInfo>(StringComparer.Ordinal);
    public readonly List<string> Report = new List<string>();
    public readonly List<string> Missing = new List<string>();
    public int Sources => _related.Count;

    public static GhostTestFixtures Build(IEnumerable<FsEntry> entries, Func<string, bool> hasBuilding)
    {
        var f = new GhostTestFixtures();
        var files = new List<FsEntry>();
        foreach (var e in entries)
        {
            if (e == null || e.IsDirectory || string.IsNullOrEmpty(e.Path)) continue;
            try { if (!File.Exists(e.Path)) continue; } catch (Exception) { continue; }
            if (hasBuilding != null && !hasBuilding(e.Path)) continue; // fixtures must be clickable in the world
            files.Add(e);
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- Test A: assignment workflow
        var docExt = new[] { "docx", "doc", "rtf", "odt" };
        var a = f.PickFile(files, used, "Assignment.docx", docExt);
        var notes = f.PickFile(files, used, "Notes.docx", docExt);
        var chrome = f.ResolveApp("Google Chrome", new[]
        {
            Path.Combine(Pf(), @"Google\Chrome\Application\chrome.exe"), Path.Combine(PfX86(), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Local(), @"Google\Chrome\Application\chrome.exe"),
        });
        var chatgpt = f.ResolveApp("ChatGPT", new[]
        {
            Path.Combine(Local(), @"Programs\ChatGPT\ChatGPT.exe"), Path.Combine(Local(), @"ChatGPT\ChatGPT.exe"),
            Path.Combine(Pf(), @"ChatGPT\ChatGPT.exe"),
        });
        var word = f.ResolveApp("Microsoft Word", new[]
        {
            Path.Combine(Pf(), @"Microsoft Office\root\Office16\WINWORD.EXE"), Path.Combine(PfX86(), @"Microsoft Office\root\Office16\WINWORD.EXE"),
            Path.Combine(Pf(), @"Microsoft Office\Office16\WINWORD.EXE"), Path.Combine(PfX86(), @"Microsoft Office\Office16\WINWORD.EXE"),
        });
        if (a != null) f.AddSource(a, new object[] { chrome, chatgpt, word, notes });

        // ---- Test B: coding workflow
        var codeExt = new[] { "cpp", "cc", "cxx", "c", "cs", "py", "js", "ts", "java", "go", "rs", "h", "hpp" };
        var mainCpp = f.PickFile(files, used, "main.cpp", codeExt);
        var rcpp = f.PickFile(files, used, "renderer.cpp", codeExt);
        var rh = f.PickFile(files, used, "renderer.h", codeExt);
        var vscode = f.ResolveApp("Visual Studio Code", new[]
        {
            Path.Combine(Local(), @"Programs\Microsoft VS Code\Code.exe"), Path.Combine(Pf(), @"Microsoft VS Code\Code.exe"),
            Path.Combine(PfX86(), @"Microsoft VS Code\Code.exe"),
        });
        if (mainCpp != null) f.AddSource(mainCpp, new object[] { rcpp, rh, vscode });

        // ---- Test C: no relationships
        var c = f.PickFile(files, used, "Unrelated.txt", new[] { "txt", "md", "log" }, anyFallback: true);
        if (c != null) f.AddSource(c, new object[0]);

        return f;
    }

    public string Summary()
    {
        string s = Sources + " test source(s) ready";
        if (Missing.Count > 0) s += ". Not available: " + string.Join(", ", Missing.Take(4)) + (Missing.Count > 4 ? " +" + (Missing.Count - 4) : "");
        return s + ".";
    }

    // ------------------------------------------------------------------ IRelationOverlay

    public bool TryGetRelated(string id, out List<RelatedEntity> related)
    {
        if (id != null && _related.TryGetValue(id, out var l))
        {
            related = l.Select(r => new RelatedEntity { Id = r.Id, Type = r.Type, Score = r.Score, CoUsage = r.CoUsage, Sessions = r.Sessions }).ToList();
            return true;
        }
        related = null;
        return false;
    }

    public EntityInfo GetEntity(string id)
    {
        if (id != null && _entities.TryGetValue(id, out var e)) return new EntityInfo { Id = e.Id, Type = e.Type, Path = e.Path, Name = e.Name };
        return null;
    }

    // ------------------------------------------------------------------ building the fixtures

    static string Pf() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
    static string PfX86() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
    static string Local() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);

    FsEntry PickFile(List<FsEntry> files, HashSet<string> used, string name, string[] fallbackExt, bool anyFallback = false)
    {
        var hit = files.FirstOrDefault(e => !used.Contains(e.Path) && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        bool standIn = false;
        if (hit == null)
        {
            hit = files.FirstOrDefault(e => !used.Contains(e.Path) && fallbackExt.Any(x => string.Equals(e.Extension, x, StringComparison.OrdinalIgnoreCase)));
            if (hit == null && anyFallback) hit = files.FirstOrDefault(e => !used.Contains(e.Path));
            standIn = hit != null;
        }
        if (hit == null) { Missing.Add(name); Report.Add(name + ": no matching file in the current world"); return null; }
        used.Add(hit.Path);
        if (standIn) Report.Add(name + " -> using " + hit.Name + " as a stand-in");
        return hit;
    }

    EntityInfo ResolveApp(string display, string[] candidates)
    {
        foreach (var c in candidates)
        {
            try
            {
                if (!string.IsNullOrEmpty(c) && Path.IsPathRooted(c) && File.Exists(c))
                    return new EntityInfo { Id = ActivityIds.App(c), Type = EntityType.Application, Path = c, Name = display };
            }
            catch (Exception) { }
        }
        Missing.Add(display);
        Report.Add(display + ": not found in the standard install locations, left out");
        return null;
    }

    /// <summary>related items are EntityInfo (apps) or FsEntry (files); nulls (unresolved) are skipped.</summary>
    void AddSource(FsEntry source, object[] related)
    {
        string sid = ActivityIds.File(source.Path);
        _entities[sid] = new EntityInfo { Id = sid, Type = EntityType.File, Path = source.Path, Name = source.Name };
        var list = new List<RelatedEntity>();
        foreach (var o in related)
        {
            if (o == null) continue;
            EntityInfo info;
            if (o is EntityInfo ai) info = ai;
            else
            {
                var fe = (FsEntry)o;
                info = new EntityInfo { Id = ActivityIds.File(fe.Path), Type = EntityType.File, Path = fe.Path, Name = fe.Name };
            }
            _entities[info.Id] = info;
            list.Add(new RelatedEntity { Id = info.Id, Type = info.Type, Score = 1.0, CoUsage = 1.0, Sessions = 99 });
        }
        _related[sid] = list; // an empty list is a real answer: "no relationships" (Test C)
    }
}
