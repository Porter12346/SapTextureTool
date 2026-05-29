using SapTextureTool.Models;
using System.IO;
using System.Text.Json;

namespace SapTextureTool.Services;

// Loads and persists pack drafts to ~/.config/SapTextureTool/drafts.json (mac uses the
// same .NET default location as the existing pack library and game-dir config, so all
// app-data files stay co-located).
public static class DraftService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string GetDraftsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SapTextureTool", "drafts.json");
    }

    public static Dictionary<string, PackDraft> Load()
    {
        try
        {
            var json = File.ReadAllText(GetDraftsPath());
            var list = JsonSerializer.Deserialize<List<PackDraft>>(json) ?? new();
            var dict = new Dictionary<string, PackDraft>(StringComparer.OrdinalIgnoreCase);
            foreach (var draft in list)
            {
                if (!string.IsNullOrEmpty(draft.PackDir))
                    dict[draft.PackDir] = draft;
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, PackDraft>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(Dictionary<string, PackDraft> drafts)
    {
        try
        {
            var path = GetDraftsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(drafts.Values.ToList(), JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Error("DraftService.Save failed", ex);
        }
    }
}
