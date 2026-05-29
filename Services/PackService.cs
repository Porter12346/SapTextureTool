using SapTextureTool.Models;
using System.IO;
using System.Text.Json;

namespace SapTextureTool.Services;

public static class PackService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void SavePack(string name, string version, IEnumerable<TextureEntry> entries, string outputDir)
    {
        var spritesDir = Path.Combine(outputDir, "sprites");
        Directory.CreateDirectory(spritesDir);

        var manifest = new PackManifest { Name = name, Version = version, SchemaVersion = 2 };
        var v2 = new Dictionary<string, PackAssetEntry>();
        var usedV1Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries.Where(e => e.IncludeInPack && e.HasReplacement))
        {
            // PNG filename: first occurrence uses bare Name; duplicates get __{PathId} suffix
            // so different bundles' same-Name entries don't overwrite each other on disk.
            var v1Key = entry.Name;
            string destFile;
            if (usedV1Keys.Add(v1Key))
                destFile = $"sprites/{entry.Name}.png";
            else
                destFile = $"sprites/{entry.Name}__{entry.PathId}.png";

            File.Copy(entry.ReplacementPath!, Path.Combine(outputDir, destFile), overwrite: true);

            // v1: first occurrence wins by name (legacy tool versions can still load it)
            if (manifest.Assets.TryAdd(entry.Name, destFile))
            {
                // ok — first
            }
            // v2: always keyed by Name#PathId, full metadata
            v2[$"{entry.Name}#{entry.PathId}"] = new PackAssetEntry
            {
                Png = destFile,
                SourceTag = entry.SourceTag,
                Border = entry.Border,
            };
        }

        manifest.AssetsV2 = v2;
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        File.WriteAllText(Path.Combine(outputDir, "pack.json"), json);
        Logger.Info($"Pack saved: {outputDir} ({manifest.Assets.Count} v1 / {v2.Count} v2)");
    }

    // Returns (manifest, packDir) or null if the file can't be read.
    public static (PackManifest manifest, string packDir)? LoadPack(string packJsonPath)
    {
        try
        {
            var json = File.ReadAllText(packJsonPath);
            var manifest = JsonSerializer.Deserialize<PackManifest>(json);
            if (manifest == null) return null;
            return (manifest, Path.GetDirectoryName(packJsonPath)!);
        }
        catch (Exception ex) { Logger.Error($"LoadPack failed: {packJsonPath}", ex); return null; }
    }

    // Applies a loaded pack's replacement paths to the texture list.
    // Returns the number of textures matched. Prefers v2 (SourceTag-based) when present so
    // duplicate Name entries from different bundles map to the right TextureEntry.
    public static int ApplyPackToEntries(PackManifest manifest, string packDir, IList<TextureEntry> allEntries)
    {
        var bySourceTag = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEntries) bySourceTag.TryAdd(e.SourceTag, e);

        var byName = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEntries) byName.TryAdd(e.Name, e);

        var count = 0;
        if (manifest.AssetsV2 is { Count: > 0 } v2)
        {
            foreach (var (key, pae) in v2)
            {
                var absPath = Path.Combine(packDir, pae.Png);
                if (!File.Exists(absPath)) continue;

                TextureEntry? entry = null;
                if (pae.SourceTag != null && bySourceTag.TryGetValue(pae.SourceTag, out var bySt))
                    entry = bySt;
                else
                {
                    // Fallback: parse "Name#PathId" key, match by name
                    var hash = key.IndexOf('#');
                    var name = hash >= 0 ? key[..hash] : key;
                    if (byName.TryGetValue(name, out var byN)) entry = byN;
                }
                if (entry == null)
                {
                    Logger.Info($"Pack entry not matched: {key}");
                    continue;
                }
                entry.ReplacementPath = absPath;
                entry.Border = pae.Border;
                entry.IncludeInPack = true;
                count++;
            }
            Logger.Info($"Pack loaded (v2): {count}/{v2.Count} entries matched");
            return count;
        }

        // v1 fallback
        foreach (var (spriteName, relPath) in manifest.Assets)
        {
            var absPath = Path.Combine(packDir, relPath);
            if (!File.Exists(absPath)) continue;
            if (!byName.TryGetValue(spriteName, out var entry)) continue;
            entry.ReplacementPath = absPath;
            entry.IncludeInPack = true;
            count++;
        }
        Logger.Info($"Pack loaded (v1): {count}/{manifest.Assets.Count} entries matched");
        return count;
    }

    // ── Draft helpers (in-memory pack state persistence) ─────────────────────

    // Snapshots the current entry state into the active pack's draft. Creates the draft
    // if it doesn't exist. No-op if there's no active pack.
    public static void SnapshotToDraft(
        Dictionary<string, PackDraft> drafts,
        string? activePackDir, string? activePackName, string? activePackVersion,
        IEnumerable<TextureEntry> allEntries)
    {
        if (string.IsNullOrEmpty(activePackDir)) return;
        if (!drafts.TryGetValue(activePackDir, out var draft))
        {
            draft = new PackDraft
            {
                PackDir = activePackDir,
                Name = activePackName ?? "Untitled",
                Version = activePackVersion ?? "1.0",
            };
            drafts[activePackDir] = draft;
        }
        if (!string.IsNullOrEmpty(activePackName)) draft.Name = activePackName;
        if (!string.IsNullOrEmpty(activePackVersion)) draft.Version = activePackVersion;
        draft.Assets.Clear();
        foreach (var entry in allEntries)
        {
            if (!entry.IncludeInPack || !entry.HasReplacement) continue;
            var key = $"{entry.Name}#{entry.PathId}";
            draft.Assets[key] = new PackAssetEntry
            {
                Png = entry.ReplacementPath!,
                SourceTag = entry.SourceTag,
                Border = entry.Border,
            };
        }
    }

    // Applies a draft's asset map onto the entry list — same matching logic as
    // ApplyPackToEntries (SourceTag preferred, name fallback). Returns the count matched.
    public static int ApplyDraftToEntries(PackDraft draft, IList<TextureEntry> allEntries)
    {
        var bySourceTag = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEntries) bySourceTag.TryAdd(e.SourceTag, e);
        var byName = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEntries) byName.TryAdd(e.Name, e);

        var count = 0;
        foreach (var (key, pae) in draft.Assets)
        {
            if (string.IsNullOrEmpty(pae.Png) || !File.Exists(pae.Png)) continue;

            TextureEntry? entry = null;
            if (pae.SourceTag != null && bySourceTag.TryGetValue(pae.SourceTag, out var bySt))
                entry = bySt;
            else
            {
                var hash = key.IndexOf('#');
                var name = hash >= 0 ? key[..hash] : key;
                if (byName.TryGetValue(name, out var byN)) entry = byN;
            }
            if (entry == null) continue;
            entry.ReplacementPath = pae.Png;
            entry.Border = pae.Border;
            entry.IncludeInPack = true;
            count++;
        }
        return count;
    }

    // Writes a single entry's current state into a target draft's asset map. Used by the
    // "Add to Pack" picker when the user picks a non-active draft — the entry stays unchanged
    // (its IncludeInPack/ReplacementPath belong to the active pack), but the target pack
    // remembers it.
    public static void AddEntryToDraft(PackDraft draft, TextureEntry entry)
    {
        if (!entry.HasReplacement) return;
        var key = $"{entry.Name}#{entry.PathId}";
        draft.Assets[key] = new PackAssetEntry
        {
            Png = entry.ReplacementPath!,
            SourceTag = entry.SourceTag,
            Border = entry.Border,
        };
    }

    // ── Pack library (persistent list of saved packs) ─────────────────────────

    public static string GetPackLibraryPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SapTextureTool", "packs.json");
    }

    public static List<SavedPackRef> LoadPackLibrary()
    {
        try
        {
            var json = File.ReadAllText(GetPackLibraryPath());
            return JsonSerializer.Deserialize<List<SavedPackRef>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void SavePackLibrary(List<SavedPackRef> library)
    {
        var path = GetPackLibraryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(library, JsonOpts));
    }

    public static void AddOrUpdateInLibrary(SavedPackRef packRef)
    {
        var library = LoadPackLibrary();
        var existing = library.FirstOrDefault(p => p.PackDir == packRef.PackDir);
        if (existing != null) { existing.Name = packRef.Name; existing.Version = packRef.Version; }
        else library.Add(packRef);
        SavePackLibrary(library);
    }

    // ── Config ────────────────────────────────────────────────────────────────

    public static string GetConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SapTextureTool", "config.json");
    }

    public static string? LoadSavedGameDir()
    {
        try
        {
            var json = File.ReadAllText(GetConfigPath());
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("gameDir").GetString();
        }
        catch { return null; }
    }

    public static void SaveGameDir(string gameDir)
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { gameDir }));
    }
}
