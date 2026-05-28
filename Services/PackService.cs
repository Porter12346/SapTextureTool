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

        var manifest = new PackManifest { Name = name, Version = version };

        foreach (var entry in entries.Where(e => e.IncludeInPack && e.HasReplacement))
        {
            var destFile = $"sprites/{entry.Name}.png";
            File.Copy(entry.ReplacementPath!, Path.Combine(outputDir, destFile), overwrite: true);
            manifest.Assets[entry.Name] = destFile;
        }

        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        File.WriteAllText(Path.Combine(outputDir, "pack.json"), json);
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
        catch { return null; }
    }

    // Applies a loaded pack's replacement paths to the texture list.
    // Returns the number of textures matched.
    public static int ApplyPackToEntries(PackManifest manifest, string packDir, IList<TextureEntry> allEntries)
    {
        // Use TryAdd so duplicate asset names (same name in different bundles) don't throw.
        var byName = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEntries)
            byName.TryAdd(e.Name, e);
        var count = 0;
        foreach (var (spriteName, relPath) in manifest.Assets)
        {
            var absPath = Path.Combine(packDir, relPath);
            if (!File.Exists(absPath)) continue;
            if (!byName.TryGetValue(spriteName, out var entry)) continue;
            entry.ReplacementPath = absPath;
            entry.IncludeInPack = true;
            count++;
        }
        return count;
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
