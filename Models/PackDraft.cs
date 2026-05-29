namespace SapTextureTool.Models;

// In-memory representation of a pack's working state. Persists across pack swaps and app
// restarts via DraftService — so adding sprites without an explicit Save Pack no longer
// loses them when the user switches between packs.
public class PackDraft
{
    public string PackDir { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";
    // Keyed by "<Name>#<PathId>" — same shape as PackManifest.AssetsV2.
    public Dictionary<string, PackAssetEntry> Assets { get; set; } = new();
}
